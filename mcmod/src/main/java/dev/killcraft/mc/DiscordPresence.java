package dev.killcraft.mc;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import net.minecraft.client.Minecraft;
import net.minecraft.world.level.Level;

/**
 * Discord Rich Presence as Killcraft: the player's Discord status shows Killcraft (Minecraft inside
 * ULTRAKILL) instead of SkyCraft. Killcraft turns SkyCraft's own off and passes the Killcraft
 * application's id (-Dskycraft.discordAppId=0, -Dkillcraft.discordAppId=...; see McSave).
 *
 * <p>Talks to the Discord app on this PC over its local IPC pipe (\\.\pipe\discord-ipc-N) from one
 * background thread, the way SkyCraft's DiscordPresence does (MIT, chasmlol). Without Discord running
 * it quietly retries now and then.
 */
final class DiscordPresence {
	private static final String APP_ID = System.getProperty("killcraft.discordAppId", "");
	private static final int OP_HANDSHAKE = 0, OP_FRAME = 1, OP_CLOSE = 2, OP_PING = 3, OP_PONG = 4;
	private static final long START = System.currentTimeMillis() / 1000L;

	private static volatile String wantedActivity;
	private static long nextDescribe;

	private DiscordPresence() {
	}

	static void start() {
		if (APP_ID.isBlank() || "0".equals(APP_ID)) {
			return;
		}
		Thread thread = new Thread(DiscordPresence::run, "Killcraft Discord");
		thread.setDaemon(true);
		thread.start();
		Killcraft.LOG.info("Killcraft: Discord Rich Presence on (application {})", APP_ID);
	}

	/** Every client tick: what the status should say. */
	static void tick(Minecraft minecraft) {
		if (APP_ID.isBlank() || "0".equals(APP_ID)) {
			return;
		}
		long now = System.currentTimeMillis();
		if (now < nextDescribe) {
			return;
		}
		nextDescribe = now + 1000;
		JsonObject activity = null;
		if (minecraft.player != null && minecraft.level != null) {
			activity = new JsonObject();
			activity.addProperty("details", "Playing ULTRAKILL as a Minecraft player");
			activity.addProperty("state", minecraft.level.dimension() == Level.NETHER ? "In the Nether" : "In ULTRAKILL's levels");
			JsonObject timestamps = new JsonObject();
			timestamps.addProperty("start", START);
			activity.add("timestamps", timestamps);
			JsonObject assets = new JsonObject();
			assets.addProperty("large_image", "killcraft");
			assets.addProperty("large_text", "Killcraft");
			activity.add("assets", assets);
		}
		JsonObject args = new JsonObject();
		args.addProperty("pid", ProcessHandle.current().pid());
		args.add("activity", activity);
		wantedActivity = args.toString();
	}

	// ---- the IPC thread --------------------------------------------------------------------------

	private static RandomAccessFile pipe;
	private static FileInputStream in;
	private static String sentActivity;
	private static long lastSent;
	private static boolean loggedNoDiscord;
	private static int nonce;

	private static void run() {
		while (true) {
			try {
				if (pipe == null && !connect()) {
					Thread.sleep(15_000);
					continue;
				}
				String wanted = wantedActivity;
				long now = System.currentTimeMillis();
				// Discord accepts 5 updates per 20 s; stay well under.
				if (wanted != null && !wanted.equals(sentActivity) && now - lastSent > 5_000) {
					command("SET_ACTIVITY", JsonParser.parseString(wanted).getAsJsonObject());
					sentActivity = wanted;
					lastSent = now;
				}
				readAvailable();
				Thread.sleep(250);
			} catch (IOException e) {
				Killcraft.LOG.info("Killcraft: Discord connection closed ({}); retrying later", e.getMessage());
				close();
			} catch (InterruptedException e) {
				return;
			} catch (RuntimeException e) {
				Killcraft.LOG.warn("Killcraft: Discord Rich Presence error", e);
				close();
			}
		}
	}

	private static boolean connect() throws IOException, InterruptedException {
		for (int i = 0; i < 10; i++) {
			try {
				pipe = new RandomAccessFile("\\\\.\\pipe\\discord-ipc-" + i, "rw");
			} catch (IOException e) {
				continue;
			}
			in = new FileInputStream(pipe.getFD());
			JsonObject hello = new JsonObject();
			hello.addProperty("v", 1);
			hello.addProperty("client_id", APP_ID);
			write(OP_HANDSHAKE, hello);
			long deadline = System.currentTimeMillis() + 5_000;
			while (System.currentTimeMillis() < deadline) {
				JsonObject msg = readFrame();
				if (msg != null && "READY".equals(str(msg, "evt"))) {
					Killcraft.LOG.info("Killcraft: connected to Discord (Rich Presence)");
					loggedNoDiscord = false;
					sentActivity = null;
					return true;
				}
				if (msg == null) {
					Thread.sleep(100);
				}
			}
			close();
			return false;
		}
		if (!loggedNoDiscord) {
			loggedNoDiscord = true;
			Killcraft.LOG.info("Killcraft: Discord isn't running; Rich Presence will connect when it is");
		}
		return false;
	}

	private static void command(String cmd, JsonObject args) throws IOException {
		JsonObject frame = new JsonObject();
		frame.addProperty("cmd", cmd);
		frame.addProperty("nonce", Integer.toString(++nonce));
		frame.add("args", args);
		write(OP_FRAME, frame);
	}

	private static void readAvailable() throws IOException {
		JsonObject msg;
		while ((msg = readFrame()) != null) {
			if ("ERROR".equals(str(msg, "evt"))) {
				Killcraft.LOG.warn("Killcraft: Discord says {}", msg.get("data"));
			}
		}
	}

	private static void write(int op, JsonObject payload) throws IOException {
		byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
		ByteBuffer frame = ByteBuffer.allocate(8 + body.length).order(ByteOrder.LITTLE_ENDIAN);
		frame.putInt(op).putInt(body.length).put(body);
		pipe.write(frame.array());
	}

	/** The next frame if one has fully arrived (never blocks for one that hasn't started). */
	private static JsonObject readFrame() throws IOException {
		while (in.available() >= 8) {
			byte[] header = new byte[8];
			pipe.readFully(header);
			ByteBuffer h = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
			int op = h.getInt();
			int length = h.getInt();
			byte[] body = new byte[length];
			pipe.readFully(body);
			String text = new String(body, StandardCharsets.UTF_8);
			switch (op) {
				case OP_PING -> {
					ByteBuffer pong = ByteBuffer.allocate(8 + length).order(ByteOrder.LITTLE_ENDIAN);
					pong.putInt(OP_PONG).putInt(length).put(body);
					pipe.write(pong.array());
				}
				case OP_CLOSE -> throw new IOException("Discord closed the connection: " + text);
				case OP_FRAME -> {
					return JsonParser.parseString(text).getAsJsonObject();
				}
				default -> {
				}
			}
		}
		return null;
	}

	private static String str(JsonObject o, String key) {
		return o.has(key) && o.get(key).isJsonPrimitive() ? o.get(key).getAsString() : null;
	}

	private static void close() {
		try {
			if (pipe != null) {
				pipe.close();
			}
		} catch (IOException ignored) {
		}
		pipe = null;
		in = null;
		sentActivity = null;
	}
}
