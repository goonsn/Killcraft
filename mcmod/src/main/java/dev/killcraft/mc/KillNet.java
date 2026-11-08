package dev.killcraft.mc;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.networking.v1.PayloadTypeRegistry;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.chat.Component;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;

/**
 * Killcraft's multiplayer messages between the players' ULTRAKILLs (where each player's V1 is, what
 * the level's enemies do, ...). Each ULTRAKILL talks to its own Minecraft client over this PC's
 * localhost (KillNetClient); the clients pass the messages to this server as packets, and the server
 * passes each on to the players it's for. It only reads the first bytes of each (the message's
 * type and where it goes, little-endian as ULTRAKILL writes them):
 *
 * <ul>
 * <li>route 0: everyone else in the sender's room (the same ULTRAKILL level, or its Nether);
 * <li>route 1: the room's authority (the player whose ULTRAKILL runs the level's enemies: the
 *     world's host when they're there, otherwise whoever came first);
 * <li>route 2: one player, by the id in bytes 2-5;
 * <li>route 4: everyone else in the world, wherever they are (voice chat, party notices);
 * <li>route 3: this server: type 1 is a player's presence (bytes 2-5 the level, byte 6 flags: 1
 *     ULTRAKILL mode, 2 the Nether, 4 in a level; then the level's scene name, a .NET string); the
 *     party host's: 4 kick a player (their id), 5 make another player the party host (their id), 6
 *     everyone to a level (its scene name, as type 102 to the others).
 * </ul>
 *
 * <p>The party host picks levels, kicks, and runs the enemies of any level they're in (the room's
 * authority): the world's host, until they hand it to someone else. Type 101, the party: everyone in
 * the world, their id, UUID, name, level and whether they're the world's host or the party's.
 *
 * A message on to a player starts with the sender's id (4 bytes), then the message as sent. The
 * server's own (from -1) is type 100, who's in the room: your id, the authority's, then for each
 * player in it (you too) their id and UUID.
 */
final class KillNet {
	record Frame(byte[] data) implements CustomPacketPayload {
		static final Type<Frame> TYPE = new Type<>(Killcraft.id("net"));
		static final StreamCodec<RegistryFriendlyByteBuf, Frame> CODEC = StreamCodec.of(
			(buf, p) -> buf.writeByteArray(p.data), buf -> new Frame(buf.readByteArray(MAX)));

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	static final int MAX = 30000;
	static final int ROUTE_ROOM = 0, ROUTE_AUTHORITY = 1, ROUTE_PLAYER = 2, ROUTE_SERVER = 3, ROUTE_ALL = 4;
	static final int MSG_PRESENCE = 1, MSG_KICK = 4, MSG_LEAD = 5, MSG_GO = 6, MSG_ROOM = 100, MSG_PARTY = 101, MSG_GOING = 102;

	private static final class Member {
		final int id;
		final UUID uuid;
		volatile ServerPlayer player;
		volatile long room = -1;
		volatile String scene = "";
		long since;

		Member(int id, UUID uuid) {
			this.id = id;
			this.uuid = uuid;
		}
	}

	private static final Map<UUID, Member> MEMBERS = new ConcurrentHashMap<>();
	private static int nextId = 1;
	private static volatile boolean roomsChanged;
	/** The party host, if the world's host handed it on (else the world's host). */
	private static volatile UUID leader;
	private static int ticks;

	private KillNet() {
	}

	/** Who's where, and the PvP setting, sent again now (it changed). */
	static void roomsChanged() {
		roomsChanged = true;
	}

	static void init() {
		PayloadTypeRegistry.serverboundPlay().register(Frame.TYPE, Frame.CODEC);
		PayloadTypeRegistry.clientboundPlay().register(Frame.TYPE, Frame.CODEC);
		// (Passed on at once, on the network thread: only presence waits for the server's tick.)
		ServerPlayNetworking.registerGlobalReceiver(Frame.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			byte[] data = payload.data();
			if (data.length >= 2 && (data[1] & 0xFF) == ROUTE_SERVER) {
				context.server().execute(() -> receive(context.server(), player, data));
			} else {
				receive(context.server(), player, data);
			}
		});
		ServerPlayConnectionEvents.DISCONNECT.register((handler, server) -> {
			if (MEMBERS.remove(handler.getPlayer().getUUID()) != null) {
				roomsChanged = true;
			}
		});
		ServerTickEvents.END_SERVER_TICK.register(server -> {
			if (++ticks % 40 == 0 || roomsChanged) {
				roomsChanged = false;
				sendRooms(server);
			}
		});
	}

	private static synchronized Member member(ServerPlayer player) {
		Member m = MEMBERS.computeIfAbsent(player.getUUID(), id -> new Member(nextId++, id));
		m.player = player;
		return m;
	}

	private static void receive(MinecraftServer server, ServerPlayer from, byte[] d) {
		if (d.length < 2) {
			return;
		}
		Member m = member(from);
		int type = d[0] & 0xFF, route = d[1] & 0xFF;
		switch (route) {
			case ROUTE_SERVER -> {
				if (type == MSG_PRESENCE && d.length >= 7) {
					long level = intLE(d, 2) & 0xFFFFFFFFL;
					int flags = d[6] & 0xFF;
					long room = (flags & 4) == 0 ? -1 : (flags & 2) != 0 ? level | (1L << 32) : level;
					String scene = d.length > 7 ? readString(d, 7) : "";
					if (!scene.equals(m.scene)) {
						m.scene = scene;
						roomsChanged = true;
					}
					if (room != m.room) {
						m.room = room;
						m.since = System.nanoTime();
						roomsChanged = true;
					}
				} else if ((type == MSG_KICK || type == MSG_LEAD) && d.length >= 6) {
					partyOrder(server, m, type, intLE(d, 2));
				} else if (type == MSG_GO && d.length > 2 && isLeader(server, m)) {
					String scene = readString(d, 2);
					Killcraft.LOG.info("Killcraft: the party host took everyone to {}", scene);
					for (Member other : MEMBERS.values()) {
						if (other != m) {
							byte[] going = d.clone();
							going[0] = (byte) MSG_GOING;
							going[1] = (byte) ROUTE_ROOM;
							send(server, other, m.id, going);
						}
					}
				}
			}
			case ROUTE_ROOM -> {
				if (m.room < 0) {
					return;
				}
				for (Member other : MEMBERS.values()) {
					if (other != m && other.room == m.room) {
						send(server, other, m.id, d);
					}
				}
			}
			case ROUTE_ALL -> {
				for (Member other : MEMBERS.values()) {
					if (other != m) {
						send(server, other, m.id, d);
					}
				}
			}
			case ROUTE_AUTHORITY -> {
				Member authority = authority(server, m.room);
				if (authority != null && authority != m) {
					send(server, authority, m.id, d);
				}
			}
			case ROUTE_PLAYER -> {
				if (d.length >= 6) {
					int target = intLE(d, 2);
					for (Member other : MEMBERS.values()) {
						if (other.id == target && other != m) {
							send(server, other, m.id, d);
						}
					}
				}
			}
			default -> {
			}
		}
	}

	private static boolean isWorldHost(Member m) {
		ServerPlayer p = m.player;
		return p != null && PlayerLink.isHost(p);
	}

	/** The party host: the one the world's host handed it to, while they're here; else the world's host. */
	private static boolean isLeader(MinecraftServer server, Member m) {
		UUID l = leader;
		if (l != null) {
			Member lm = MEMBERS.get(l);
			if (lm != null && lm.player != null && !lm.player.hasDisconnected()) {
				return lm == m;
			}
		}
		return isWorldHost(m);
	}

	/** The party host (or the world's host) kicking a player out, or handing the party on. */
	private static void partyOrder(MinecraftServer server, Member from, int type, int targetId) {
		if (!isLeader(server, from) && !isWorldHost(from)) {
			return;
		}
		Member target = null;
		for (Member other : MEMBERS.values()) {
			if (other.id == targetId) {
				target = other;
			}
		}
		if (target == null || target.player == null) {
			return;
		}
		String by = from.player != null ? from.player.getName().getString() : "the host";
		if (type == MSG_LEAD) {
			leader = target.uuid;
			roomsChanged = true;
			Killcraft.LOG.info("Killcraft: {} made {} the party host", by, target.player.getName().getString());
			return;
		}
		// (Not the world's host: the world is theirs. Nor yourself.)
		if (target == from || isWorldHost(target)) {
			return;
		}
		Killcraft.LOG.info("Killcraft: {} kicked {} out of the party", by, target.player.getName().getString());
		target.player.connection.disconnect(Component.literal("Kicked out of the party by " + by));
	}

	/** The room's authority: the party host when they're in it, otherwise whoever came first. */
	private static Member authority(MinecraftServer server, long room) {
		if (room < 0) {
			return null;
		}
		Member best = null;
		boolean bestHost = false;
		for (Member m : MEMBERS.values()) {
			if (m.room != room) {
				continue;
			}
			boolean host = isLeader(server, m);
			if (best == null || (host && !bestHost) || (host == bestHost && (m.since < best.since || (m.since == best.since && m.id < best.id)))) {
				best = m;
				bestHost = host;
			}
		}
		return best;
	}

	private static void sendRooms(MinecraftServer server) {
		for (Member m : MEMBERS.values()) {
			List<Member> in = new ArrayList<>();
			if (m.room >= 0) {
				for (Member other : MEMBERS.values()) {
					if (other.room == m.room) {
						in.add(other);
					}
				}
			}
			Member authority = authority(server, m.room);
			// (Then the host's PvP setting: on, and how hard; an older Killcraft stops before it.)
			byte[] d = new byte[2 + 4 + 4 + 4 + in.size() * 20 + 5];
			d[0] = (byte) MSG_ROOM;
			d[1] = (byte) ROUTE_ROOM;  // (not ROUTE_PLAYER: that would put a player's id next)
			putIntLE(d, 2, m.id);
			putIntLE(d, 6, authority != null ? authority.id : 0);
			putIntLE(d, 10, in.size());
			int at = 14;
			for (Member other : in) {
				putIntLE(d, at, other.id);
				putLongLE(d, at + 4, other.uuid.getMostSignificantBits());
				putLongLE(d, at + 12, other.uuid.getLeastSignificantBits());
				at += 20;
			}
			d[at] = (byte) (Killcraft.pvp ? 1 : 0);
			putIntLE(d, at + 1, Float.floatToRawIntBits(Killcraft.pvpDamage));
			send(server, m, -1, d);
		}
		sendParty(server);
	}

	/** Everyone in the world: id, UUID, name, level (scene name, "" when not in one), flags (1 the world's host, 2 the party host, 4 in the Nether). */
	private static void sendParty(MinecraftServer server) {
		var out = new java.io.ByteArrayOutputStream(64 + MEMBERS.size() * 64);
		out.write(MSG_PARTY);
		out.write(ROUTE_ROOM);
		List<Member> all = new ArrayList<>();
		for (Member m : MEMBERS.values()) {
			if (m.player != null && !m.player.hasDisconnected()) {
				all.add(m);
			}
		}
		all.sort((a, b) -> Integer.compare(a.id, b.id));
		byte[] four = new byte[8];
		putIntLE(four, 0, all.size());
		out.write(four, 0, 4);
		for (Member m : all) {
			putIntLE(four, 0, m.id);
			out.write(four, 0, 4);
			putLongLE(four, 0, m.uuid.getMostSignificantBits());
			out.write(four, 0, 8);
			putLongLE(four, 0, m.uuid.getLeastSignificantBits());
			out.write(four, 0, 8);
			writeString(out, m.player.getName().getString());
			writeString(out, m.room >= 0 ? m.scene : "");
			out.write((isWorldHost(m) ? 1 : 0) | (isLeader(server, m) ? 2 : 0) | (m.room >= 0 && (m.room >> 32) != 0 ? 4 : 0));
		}
		byte[] d = out.toByteArray();
		for (Member m : all) {
			send(server, m, -1, d);
		}
	}

	/** A .NET BinaryWriter string: its UTF-8 length 7 bits a byte, then the bytes. */
	static void writeString(java.io.ByteArrayOutputStream out, String s) {
		byte[] utf8 = (s == null ? "" : s).getBytes(java.nio.charset.StandardCharsets.UTF_8);
		int length = Math.min(utf8.length, 200);
		for (int v = length; ; v >>>= 7) {
			if (v < 0x80) {
				out.write(v);
				break;
			}
			out.write((v & 0x7F) | 0x80);
		}
		out.write(utf8, 0, length);
	}

	static String readString(byte[] d, int at) {
		int length = 0, shift = 0;
		while (at < d.length && shift < 35) {
			int b = d[at++] & 0xFF;
			length |= (b & 0x7F) << shift;
			shift += 7;
			if ((b & 0x80) == 0) {
				break;
			}
		}
		if (length <= 0 || at + length > d.length) {
			return "";
		}
		return new String(d, at, Math.min(length, 200), java.nio.charset.StandardCharsets.UTF_8);
	}

	private static void send(MinecraftServer server, Member to, int fromId, byte[] d) {
		ServerPlayer player = to.player;
		if (player == null || player.hasDisconnected() || !ServerPlayNetworking.canSend(player, Frame.TYPE)) {
			return;
		}
		byte[] out = new byte[4 + d.length];
		putIntLE(out, 0, fromId);
		System.arraycopy(d, 0, out, 4, d.length);
		ServerPlayNetworking.send(player, new Frame(out));
	}

	static int intLE(byte[] d, int at) {
		return (d[at] & 0xFF) | (d[at + 1] & 0xFF) << 8 | (d[at + 2] & 0xFF) << 16 | (d[at + 3] & 0xFF) << 24;
	}

	static void putIntLE(byte[] d, int at, int v) {
		d[at] = (byte) v;
		d[at + 1] = (byte) (v >> 8);
		d[at + 2] = (byte) (v >> 16);
		d[at + 3] = (byte) (v >> 24);
	}

	static void putLongLE(byte[] d, int at, long v) {
		putIntLE(d, at, (int) v);
		putIntLE(d, at + 4, (int) (v >> 32));
	}
}
