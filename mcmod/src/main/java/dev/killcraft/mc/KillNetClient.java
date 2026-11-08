package dev.killcraft.mc;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetAddress;
import java.net.Socket;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;

/**
 * This Minecraft's end of KillNet (Killcraft's messages between the players' ULTRAKILLs): a
 * localhost connection to this PC's ULTRAKILL (Killcraft listens on the port in -Dkillcraft.netPort),
 * whose messages go to the world's server as packets and the server's back to it. One kind is only
 * for here: which other players ULTRAKILL shows as V1 (route 255, type 200), whose Minecraft body
 * is hidden meanwhile (HidePlayerMixin).
 */
public final class KillNetClient {
	private static final int PORT = Integer.getInteger("killcraft.netPort", 0);
	private static final int ROUTE_LOCAL = 255, MSG_HIDE = 200, MSG_CHAT = 201, MSG_HAND_LIGHT = 202, MSG_WORLD = 203, MSG_ACTION = 204;
	private static final int ACTION_HOST = 1, ACTION_JOIN = 2, ACTION_LEAVE = 3;
	private static int worldTicks;
	/** Minecraft's packed light for the first-person hand, as ULTRAKILL lights V1's place (-1: Minecraft's own). */
	public static volatile int handLight = -1;
	private static final Set<UUID> HIDDEN = ConcurrentHashMap.newKeySet();
	private static volatile DataOutputStream toUltrakill;

	private KillNetClient() {
	}

	static void start() {
		if (PORT <= 0) {
			return;
		}
		ClientPlayNetworking.registerGlobalReceiver(KillNet.Frame.TYPE, (payload, context) -> write(payload.data()));
		Thread thread = new Thread(KillNetClient::run, "Killcraft KillNet");
		thread.setDaemon(true);
		thread.start();
	}

	/** ULTRAKILL's message to the world's server, at once (Minecraft's connection takes it from any thread). */
	private static void send(byte[] d) {
		Minecraft minecraft = Minecraft.getInstance();
		try {
			if (minecraft.player != null && minecraft.getConnection() != null && ClientPlayNetworking.canSend(KillNet.Frame.TYPE)) {
				ClientPlayNetworking.send(new KillNet.Frame(d));
			}
		} catch (RuntimeException e) {
			// (Between worlds: dropped, as when not connected.)
		}
	}

	static void tick(Minecraft minecraft) {
		if (minecraft.level == null) {
			HIDDEN.clear();
		}
		if (++worldTicks % 20 == 0) {
			sendWorld(minecraft);
		}
	}

	/**
	 * Which world this Minecraft is in, for ULTRAKILL's party screen and server list: flags (1 in a
	 * world, 2 this player's own, 4 open to friends), its e4mc link (this player's own world's once
	 * open, or the friend's world's address), and this player's name.
	 */
	private static void sendWorld(Minecraft minecraft) {
		if (toUltrakill == null) {
			return;
		}
		var server = minecraft.getSingleplayerServer();
		boolean inWorld = minecraft.player != null && minecraft.level != null && minecraft.getConnection() != null;
		boolean own = inWorld && server != null;
		boolean open = own && server.isPublished();
		String link = open ? DiscordPresence.hostLink() : null;
		if (inWorld && !own) {
			link = dev.skycraft.client.MirrorWorld.friendAddress(minecraft);
		}
		var out = new java.io.ByteArrayOutputStream(64);
		byte[] head = new byte[6];
		KillNet.putIntLE(head, 0, -2);
		head[4] = (byte) MSG_WORLD;
		head[5] = (byte) ROUTE_LOCAL;
		out.writeBytes(head);
		out.write((inWorld ? 1 : 0) | (own ? 2 : 0) | (open ? 4 : 0));
		KillNet.writeString(out, link != null ? link : "");
		KillNet.writeString(out, minecraft.getUser() != null ? minecraft.getUser().getName() : "");
		// (Then this player's UUID: a Steam party knows its players by Steam id; Minecraft hides the
		// others' bodies by this. Little-endian halves, most significant first.)
		java.util.UUID id = minecraft.getUser() != null ? minecraft.getUser().getProfileId() : null;
		byte[] uuid = new byte[16];
		if (id != null) {
			KillNet.putLongLE(uuid, 0, id.getMostSignificantBits());
			KillNet.putLongLE(uuid, 8, id.getLeastSignificantBits());
		}
		out.writeBytes(uuid);
		write(out.toByteArray());
	}

	/** ULTRAKILL's party screen: open this world to friends, join one (its link), or go back to your own. */
	private static void action(int action, String arg) {
		Minecraft minecraft = Minecraft.getInstance();
		minecraft.execute(() -> {
			try {
				switch (action) {
					case ACTION_HOST -> KillcraftClient.host(minecraft);
					case ACTION_JOIN -> {
						if (arg != null && !arg.isBlank()) {
							Killcraft.LOG.info("Killcraft: joining {} from ULTRAKILL's server list", arg);
							dev.skycraft.client.MirrorWorld.joinFriend(minecraft, arg.trim());
						}
					}
					case ACTION_LEAVE -> dev.skycraft.client.MirrorWorld.leaveFriend(minecraft);
					default -> {
					}
				}
			} catch (RuntimeException | LinkageError e) {
				Killcraft.LOG.warn("Killcraft: ULTRAKILL's party screen asked for {} and it failed ({})", action, e.toString());
			}
		});
	}

	/** A line of Minecraft's chat, for ULTRAKILL to show in ULTRAKILL mode (as from -2, Minecraft itself). */
	public static void chatLine(String text) {
		if (toUltrakill == null || text == null || text.isBlank()) {
			return;
		}
		byte[] utf8 = text.getBytes(java.nio.charset.StandardCharsets.UTF_8);
		int length = Math.min(utf8.length, 1000);
		var out = new java.io.ByteArrayOutputStream(length + 12);
		byte[] head = new byte[6];
		KillNet.putIntLE(head, 0, -2);
		head[4] = (byte) MSG_CHAT;
		head[5] = (byte) ROUTE_LOCAL;
		out.writeBytes(head);
		// (A .NET BinaryReader string: its length 7 bits a byte, then UTF-8.)
		for (int v = length; ; v >>>= 7) {
			if (v < 0x80) {
				out.write(v);
				break;
			}
			out.write((v & 0x7F) | 0x80);
		}
		out.write(utf8, 0, length);
		write(out.toByteArray());
	}

	public static boolean hidingAny() {
		return !HIDDEN.isEmpty();
	}

	/** Another player whose V1 ULTRAKILL shows instead (their Minecraft body isn't drawn). */
	public static boolean hidden(Entity entity) {
		return entity instanceof Player && !HIDDEN.isEmpty() && HIDDEN.contains(entity.getUUID()) && entity != Minecraft.getInstance().player;
	}

	private static void write(byte[] d) {
		DataOutputStream out = toUltrakill;
		if (out == null) {
			return;
		}
		try {
			synchronized (out) {
				out.writeShort(d.length);
				out.write(d);
				out.flush();
			}
		} catch (IOException e) {
			toUltrakill = null;
		}
	}

	private static void run() {
		boolean logged = false;
		while (true) {
			try (Socket socket = new Socket(InetAddress.getLoopbackAddress(), PORT)) {
				socket.setTcpNoDelay(true);
				DataInputStream in = new DataInputStream(socket.getInputStream());
				toUltrakill = new DataOutputStream(socket.getOutputStream());
				Killcraft.LOG.info("Killcraft: KillNet connected to ULTRAKILL (port {})", PORT);
				logged = false;
				while (true) {
					int length = in.readUnsignedShort();
					byte[] d = new byte[length];
					in.readFully(d);
					if (length >= 2 && (d[1] & 0xFF) == ROUTE_LOCAL) {
						local(d);
					} else if (length <= KillNet.MAX) {
						send(d);
					}
				}
			} catch (IOException e) {
				toUltrakill = null;
				HIDDEN.clear();
				handLight = -1;
				if (!logged) {
					logged = true;
					Killcraft.LOG.info("Killcraft: KillNet not connected to ULTRAKILL ({}); retrying", e.getMessage());
				}
			}
			try {
				Thread.sleep(2000);
			} catch (InterruptedException e) {
				return;
			}
		}
	}

	private static void local(byte[] d) {
		if ((d[0] & 0xFF) == MSG_ACTION && d.length >= 3) {
			action(d[2] & 0xFF, d.length > 3 ? KillNet.readString(d, 3) : "");
			return;
		}
		if ((d[0] & 0xFF) == MSG_HAND_LIGHT && d.length >= 6) {
			handLight = KillNet.intLE(d, 2);
			return;
		}
		if ((d[0] & 0xFF) == MSG_HIDE && d.length >= 6) {
			int n = KillNet.intLE(d, 2);
			Set<UUID> now = new java.util.HashSet<>();
			for (int i = 0; i < n && 6 + i * 16 + 16 <= d.length; i++) {
				int at = 6 + i * 16;
				long msb = (KillNet.intLE(d, at) & 0xFFFFFFFFL) | ((long) KillNet.intLE(d, at + 4) << 32);
				long lsb = (KillNet.intLE(d, at + 8) & 0xFFFFFFFFL) | ((long) KillNet.intLE(d, at + 12) << 32);
				now.add(new UUID(msb, lsb));
			}
			HIDDEN.retainAll(now);
			HIDDEN.addAll(now);
		}
	}
}
