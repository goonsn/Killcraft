package dev.killcraft.mc;

import dev.skycraft.link.SkyLink;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.phys.AABB;

import java.util.List;

/**
 * ULTRAKILL's skulls in Minecraft's inventory. What V1 carries (Killcraft publishes it as a dead
 * actor with Killcraft's id: x, y, z are how many blue, red and green skulls) is kept in the player's
 * inventory as these items, and no more of them: picking a skull up in ULTRAKILL gives one, placing
 * one in an altar takes it away. Which one is in the player's hand goes back to Killcraft, which then
 * puts that skull in V1's hand and shows it.
 */
final class HeldItems {
	static final int STATE_ID = 0x3FFFFFF0;

	/** By ULTRAKILL's ItemType: 1 SkullBlue, 2 SkullRed, 3 SkullGreen. */
	static final Item[] SKULLS = new Item[4];

	private HeldItems() {
	}

	static int typeOf(ItemStack stack) {
		for (int t = 1; t < SKULLS.length; t++) {
			if (SKULLS[t] != null && stack.is(SKULLS[t])) {
				return t;
			}
		}
		return 0;
	}

	/** How many of each skull V1 carries (by ItemType), or null while Killcraft says nothing. */
	private static int[] carried(List<SkyLink.Actor> actors) {
		for (SkyLink.Actor a : actors) {
			if (a.formId() == STATE_ID) {
				return new int[] { 0, Math.round(a.x()), Math.round(a.y()), Math.round(a.z()) };
			}
		}
		return null;
	}

	/** ULTRAKILL has V1 in the Nether and Minecraft's player only follows it (Killcraft says so). */
	static boolean following(List<SkyLink.Actor> actors) {
		for (SkyLink.Actor a : actors) {
			if (a.formId() == STATE_ID) {
				return a.yaw() > 0.5F;
			}
		}
		return false;
	}

	static void tick(ServerPlayer player, int ticks, List<SkyLink.Actor> actors) {
		int[] want = carried(actors);
		if (want == null) {
			return;
		}
		Inventory inventory = player.getInventory();
		int[] have = new int[SKULLS.length];
		ItemStack carriedByCursor = player.containerMenu.getCarried();
		have[typeOf(carriedByCursor)] += carriedByCursor.getCount();
		for (int i = 0; i < inventory.getContainerSize(); i++) {
			ItemStack stack = inventory.getItem(i);
			int t = typeOf(stack);
			if (t == 0) {
				continue;
			}
			int keep = Math.max(0, Math.min(stack.getCount(), want[t] - have[t]));
			if (keep == 0) {
				inventory.setItem(i, ItemStack.EMPTY);
			} else {
				stack.setCount(keep);
			}
			have[t] += keep;
		}
		for (int t = 1; t < SKULLS.length; t++) {
			if (want[t] > have[t]) {
				inventory.add(new ItemStack(SKULLS[t], want[t] - have[t]));
			}
		}
		// Dropped ones aren't in the world: the skulls are V1's (they come back to the inventory above).
		for (Entity e : player.level().getEntities(player, new AABB(player.blockPosition()).inflate(32.0), e -> e instanceof ItemEntity)) {
			if (typeOf(((ItemEntity) e).getItem()) != 0) {
				e.discard();
			}
		}
		if (ticks % 2 == 0) {
			Link.push(Killcraft.EV_HELD_SELECTED, typeOf(player.getMainHandItem()), 0, 0, 0, 0);
		}
	}
}
