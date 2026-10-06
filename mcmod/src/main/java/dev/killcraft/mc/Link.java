package dev.killcraft.mc;

/** Killcraft's events go into SkyCraft's event ring, which Killcraft (in ULTRAKILL) reads. */
final class Link {
	private static boolean broken;

	private Link() {
	}

	static void push(int type, float a, float b, float c, float d, int flags) {
		if (broken) {
			return;
		}
		try {
			dev.skycraft.link.SkyLink.pushEvent(type, 0, a, b, c, d, flags);
		} catch (LinkageError e) {
			broken = true; // no SkyCraft: plain Minecraft, nobody to tell
		}
	}
}
