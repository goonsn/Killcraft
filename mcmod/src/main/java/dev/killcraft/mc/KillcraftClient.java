package dev.killcraft.mc;

import dev.skycraft.client.SkyClient;
import dev.skycraft.link.SkyLink;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.command.v2.ClientCommandRegistrationCallback;
import net.fabricmc.fabric.api.client.command.v2.ClientCommands;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.fabric.api.client.screen.v1.Screens;
import net.minecraft.client.gui.components.AbstractWidget;
import net.minecraft.client.gui.screens.PauseScreen;
import net.minecraft.client.Minecraft;
import net.minecraft.client.server.IntegratedServer;
import net.minecraft.network.chat.Component;
import net.minecraft.util.HttpUtil;
import net.minecraft.server.MinecraftServer;
import net.minecraft.world.level.Level;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.List;

/**
 * Killcraft's client side. SkyCraft holds the player after each of Killcraft's teleports until
 * ULTRAKILL's ground has arrived under it, but there is no ULTRAKILL ground in the Nether: there
 * each teleport (Minecraft's player following V1, a Potion of ULTRAKILL) held it for 6 seconds, so
 * it fell behind V1 and was pinned in place afterwards. In the Nether the player stands on
 * Minecraft's own blocks: no hold (except while ULTRAKILL is loading or paused).
 */
public final class KillcraftClient implements ClientModInitializer {
	private static Field holdPos;
	private static boolean warned;
	// Trying multiplayer alone (Killcraft's HostForSecondPlayer setting): this world opens by itself to
	// the second Killcraft on this PC, on this port, without the sign-in check (it plays offline).
	private static final int TEST_HOST = Integer.getInteger("killcraft.testHost", 0);
	private static boolean testHosted;

	@Override
	public void onInitializeClient() {
		try {
			holdPos = SkyClient.class.getDeclaredField("holdPos");
			holdPos.setAccessible(true);
		} catch (ReflectiveOperationException | RuntimeException e) {
			Killcraft.LOG.warn("Killcraft: can't reach SkyCraft's player hold ({}); the Nether follows V1 slowly", e.toString());
			holdPos = null;
		}
		// SkyCraft's "Skyrim destruction" button on the pause menu: Skyrim's, nothing in Killcraft. Taken
		// off once the menu is built (by every mod: SkyCraft's own may add it after this one's runs).
		ScreenEvents.AFTER_INIT.register((minecraft, screen, width, height) -> {
			if (screen instanceof PauseScreen) {
				ScreenEvents.afterTick(screen).register(s ->
					Screens.getWidgets(s).removeIf(w -> w instanceof AbstractWidget a && a.getMessage().getString().startsWith("Skyrim destruction")));
			}
		});
		// Multiplayer: /host opens this world to friends (e4mc puts its link in chat), who /join it
		// (SkyCraft's). Minecraft's own Open to LAN is in its pause menu, which Killcraft never shows.
		ClientCommandRegistrationCallback.EVENT.register((dispatcher, context) ->
			dispatcher.register(ClientCommands.literal("host").executes(c -> {
				Minecraft.getInstance().execute(() -> host(Minecraft.getInstance()));
				return 1;
			})));
		// In a friend's world: Killcraft's events for this player come by packets (PlayerLink).
		ClientPlayNetworking.registerGlobalReceiver(PlayerLink.Event.TYPE, (payload, context) ->
			context.client().execute(() -> Link.push(payload.kind(), payload.a(), payload.b(), payload.c(), payload.d(), payload.flags())));
		// (And hits on this player's enemies' stand-ins there: GuestStandIns.)
		ClientPlayNetworking.registerGlobalReceiver(PlayerLink.Hit.TYPE, (payload, context) ->
			context.client().execute(() -> {
				try {
					SkyLink.pushEvent(dev.skycraft.link.Proto.EV_HIT_ACTOR, payload.formId(), payload.damage(), payload.pushX(), payload.pushZ(), payload.push(),
						payload.flags(), payload.weapon());
				} catch (LinkageError e) {
					// no SkyCraft link: nothing to tell
				}
			}));
		KillNetClient.start();
		ClientTickEvents.START_CLIENT_TICK.register(KillcraftClient::tick);
		DiscordPresence.start();
		ClientTickEvents.END_CLIENT_TICK.register(DiscordPresence::tick);
	}

	static void host(Minecraft minecraft) {
		IntegratedServer server = minecraft.getSingleplayerServer();
		if (server == null) {
			say(minecraft, "You're in a friend's world: only they can open it. (/leave goes back to yours.)");
			return;
		}
		if (server.isPublished()) {
			say(minecraft, "Your world is already open to friends (port " + server.getPort() + "). Send them e4mc's link from chat; they type /join <link>.");
			return;
		}
		boolean open = server.publishServer(MinecraftServer.MultiplayerScope.LAN, false, HttpUtil.getAvailablePort());
		Killcraft.LOG.info("Killcraft: /host: {} (port {})", open ? "world opened to friends" : "couldn't open the world", server.getPort());
		say(minecraft, open ? "Your world is open to friends. e4mc's link comes up in chat in a moment: click it to copy it, "
			+ "and your friends type /join <link>." : "Couldn't open your world to friends (see Minecraft's log).");
	}

	// Minecraft's reason for the last disconnect (DisconnectReasonMixin), said in chat once back in a world.
	private static volatile Component disconnectReason;

	public static void disconnected(Component title, Component reason) {
		Killcraft.LOG.info("Killcraft: disconnected: {}: {}", title.getString(), reason.getString());
		disconnectReason = reason;
	}

	private static void say(Minecraft minecraft, String text) {
		minecraft.gui.hud.getChat().addClientSystemMessage(Component.literal(text));
	}

	private static final List<SkyLink.Actor> ACTORS = new ArrayList<>();
	private static List<SkyLink.Actor> sent = List.of();
	private static List<SkyLink.Actor> sentEnemies = List.of();
	private static int sentAt;
	private static int ticks;

	/**
	 * In a friend's world, their server can't read this ULTRAKILL's Killcraft (it's on this PC's
	 * shared memory): what it publishes goes there by packets, when it changes and every half second.
	 */
	private static void forwardActors(Minecraft minecraft) {
		ticks++;
		if (minecraft.isLocalServer() || minecraft.player == null || minecraft.getConnection() == null || !ClientPlayNetworking.canSend(PlayerLink.Actors.TYPE)) {
			sent = List.of();
			return;
		}
		boolean read;
		try {
			read = SkyLink.readActors(ACTORS);
		} catch (LinkageError e) {
			read = false;
		}
		if (!read) {
			return;
		}
		List<SkyLink.Actor> ours = new ArrayList<>();
		List<SkyLink.Actor> enemies = new ArrayList<>();
		for (SkyLink.Actor a : ACTORS) {
			if (PlayerLink.ours(a)) {
				if (ours.size() < PlayerLink.MAX_ACTORS) {
					ours.add(a);
				}
			} else if ((a.flags() & dev.skycraft.link.Proto.ACTOR_HOSTILE) != 0 && !a.dead() && enemies.size() < PlayerLink.MAX_ACTORS) {
				enemies.add(a);
			}
		}
		// Its enemies (where they are), ten times a second while there are any.
		if (ticks % 2 == 0 && (!enemies.isEmpty() || !sentEnemies.isEmpty()) && ClientPlayNetworking.canSend(PlayerLink.Enemies.TYPE)) {
			sentEnemies = enemies;
			ClientPlayNetworking.send(new PlayerLink.Enemies(enemies));
		}
		if (ours.equals(sent) && ticks - sentAt < 10) {
			return;
		}
		sent = ours;
		sentAt = ticks;
		ClientPlayNetworking.send(new PlayerLink.Actors(ours));
	}

	private static net.minecraft.world.phys.Vec3 lastPos;
	private static net.minecraft.client.player.LocalPlayer lastPlayer;

	/**
	 * In a friend's world: the player moved far since the last tick (Killcraft put it at V1), so the
	 * friend's server is asked to move it there too (PlayerLink.Jump): it would send it back.
	 */
	private static void forwardJumps(Minecraft minecraft) {
		var player = minecraft.player;
		if (player == null || minecraft.isLocalServer() || minecraft.getConnection() == null) {
			lastPos = null;
			lastPlayer = null;
			return;
		}
		net.minecraft.world.phys.Vec3 now = player.position();
		boolean far = lastPos != null && player == lastPlayer && now.distanceToSqr(lastPos) > 8 * 8;
		lastPos = now;
		lastPlayer = player;
		if (far && ClientPlayNetworking.canSend(PlayerLink.Jump.TYPE)) {
			ClientPlayNetworking.send(new PlayerLink.Jump(now.x, now.y, now.z, player.getYRot(), player.getXRot()));
		}
	}

	private static void tick(Minecraft minecraft) {
		forwardJumps(minecraft);
		forwardActors(minecraft);
		KillNetClient.tick(minecraft);
		Component reason = disconnectReason;
		if (reason != null && minecraft.player != null) {
			disconnectReason = null;
			minecraft.gui.hud.getChat().addClientSystemMessage(Component.literal("Minecraft's reason: ").append(reason));
		}
		if (TEST_HOST > 0 && !testHosted && minecraft.player != null) {
			IntegratedServer server = minecraft.getSingleplayerServer();
			if (server != null && !server.isPublished()) {
				testHosted = true;
				server.setUsesAuthentication(false);
				boolean open = server.publishServer(MinecraftServer.MultiplayerScope.LAN, true, TEST_HOST);
				Killcraft.LOG.info("Killcraft: {} the world to a second player on this PC (port {}, no sign-in check)", open ? "opened" : "couldn't open", TEST_HOST);
			}
		}
		// ULTRAKILL paused (Minecraft's world frozen): the player's effects keep queueing their
		// particles meanwhile, which would all burst out on unpausing. They go instead.
		if (minecraft.level != null && minecraft.level.tickRateManager().isFrozen()) {
			minecraft.particleEngine.clearParticles();
		}
		if (holdPos == null || minecraft.player == null || minecraft.level == null || minecraft.level.dimension() != Level.NETHER) {
			return;
		}
		SkyLink.SkyState sky = SkyClient.sky();
		if (sky == null || !sky.inGame() || sky.loading() || sky.menuOpen()) {
			return;
		}
		try {
			holdPos.set(null, null);
		} catch (ReflectiveOperationException | RuntimeException e) {
			if (!warned) {
				warned = true;
				Killcraft.LOG.warn("Killcraft: can't release SkyCraft's player hold: {}", e.toString());
			}
		}
	}
}
