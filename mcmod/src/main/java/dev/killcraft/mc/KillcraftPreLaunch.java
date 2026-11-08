package dev.killcraft.mc;

import java.io.IOException;
import java.io.Reader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;
import net.fabricmc.loader.api.FabricLoader;
import net.fabricmc.loader.api.entrypoint.PreLaunchEntrypoint;

/**
 * Killcraft's settings for this Minecraft (and SkyCraft's), before anything has read them: which
 * ULTRAKILL it links to (skycraft.link: player 2 on a PC has its own), hidden window, Discord, the
 * KillNet port, ... Killcraft writes them to config/killcraft-launch.properties each time it starts
 * this Minecraft. (They were Java options in Prism's instance settings, which Prism rewrites as it
 * likes: a copy made while it ran lost them.)
 */
public final class KillcraftPreLaunch implements PreLaunchEntrypoint {
	@Override
	public void onPreLaunch() {
		Path file = FabricLoader.getInstance().getGameDir().resolve("config").resolve("killcraft-launch.properties");
		if (!Files.exists(file)) {
			return;
		}
		Properties props = new Properties();
		try (Reader in = Files.newBufferedReader(file)) {
			props.load(in);
		} catch (IOException e) {
			System.err.println("Killcraft: couldn't read " + file + ": " + e);
			return;
		}
		for (String key : props.stringPropertyNames()) {
			System.setProperty(key, props.getProperty(key));
		}
	}
}
