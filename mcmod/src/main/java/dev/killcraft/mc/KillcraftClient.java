package dev.killcraft.mc;

import dev.skycraft.client.SkyClient;
import dev.skycraft.link.SkyLink;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.minecraft.client.Minecraft;
import net.minecraft.world.level.Level;

import java.lang.reflect.Field;

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

	@Override
	public void onInitializeClient() {
		try {
			holdPos = SkyClient.class.getDeclaredField("holdPos");
			holdPos.setAccessible(true);
		} catch (ReflectiveOperationException | RuntimeException e) {
			Killcraft.LOG.warn("Killcraft: can't reach SkyCraft's player hold ({}); the Nether follows V1 slowly", e.toString());
			holdPos = null;
		}
		ClientTickEvents.START_CLIENT_TICK.register(KillcraftClient::tick);
		DiscordPresence.start();
		ClientTickEvents.END_CLIENT_TICK.register(DiscordPresence::tick);
	}

	private static void tick(Minecraft minecraft) {
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
