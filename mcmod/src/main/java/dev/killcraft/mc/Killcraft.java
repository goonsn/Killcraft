package dev.killcraft.mc;

import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.core.Holder;
import net.minecraft.core.Registry;
import net.minecraft.core.component.DataComponents;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.effect.MobEffect;
import net.minecraft.world.effect.MobEffectCategory;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.CreativeModeTab;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.item.Rarity;
import net.minecraft.world.item.component.Consumables;
import net.minecraft.world.item.consume_effects.ApplyStatusEffectsConsumeEffect;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.entity.BlockEntityType;
import net.minecraft.world.level.block.state.BlockBehaviour;

import java.util.ArrayList;
import java.util.List;
import java.util.Set;

import dev.skycraft.link.SkyLink;
import net.fabricmc.fabric.api.entity.event.v1.ServerLivingEntityEvents;

/**
 * Minecraft's side of Killcraft (Minecraft inside ULTRAKILL): its own items and blocks, and what
 * Killcraft has to know about them, sent over SkyCraft's link (events past SkyCraft's own types).
 */
public final class Killcraft implements ModInitializer {
	static final org.slf4j.Logger LOG = org.slf4j.LoggerFactory.getLogger("killcraft");
	public static final String MOD_ID = "killcraft";

	/** Killcraft's events (Proto.EvKc* on the ULTRAKILL side). */
	public static final int EV_ULTRAKILL_MOVES = 100; // the player has the ULTRAKILL effect
	public static final int EV_ULTRAKILL_STOP = 101;  // ... but is in a Nether portal: stop at once
	public static final int EV_TERMINAL = 102;        // a SMILEOS terminal at a, b, c (block), facing d
	public static final int EV_HELD_SELECTED = 103;   // the skull in the player's hand (ULTRAKILL's ItemType), or 0
	public static final int EV_FOLLOWER_HURT = 104;   // ULTRAKILL has V1: what would have hurt Minecraft's player (a), for V1
	public static final int EV_DIMENSION = 105;      // which world the player is really in (a: 1 the Nether)

	/** How long a Potion of ULTRAKILL lasts, in ticks. */
	public static final int POTION_TICKS = 2 * 60 * 20;

	public static Holder<MobEffect> ULTRAKILL;
	public static Item ULTRAKILL_POTION;
	public static Block SMILEOS_TERMINAL;
	public static Item SMILEOS_TERMINAL_ITEM;
	public static BlockEntityType<TerminalBlockEntity> TERMINAL_ENTITY;

	public static Identifier id(String path) {
		return Identifier.fromNamespaceAndPath(MOD_ID, path);
	}

	@Override
	public void onInitialize() {
		ULTRAKILL = Registry.registerForHolder(BuiltInRegistries.MOB_EFFECT, id("ultrakill"), new UltrakillEffect());

		ResourceKey<Item> potionKey = ResourceKey.create(Registries.ITEM, id("ultrakill_potion"));
		ULTRAKILL_POTION = Registry.register(BuiltInRegistries.ITEM, potionKey, new Item(new Item.Properties()
			.setId(potionKey)
			.stacksTo(16)
			.rarity(Rarity.UNCOMMON)
			.usingConvertsTo(Items.GLASS_BOTTLE)
			.component(DataComponents.CONSUMABLE, Consumables.defaultDrink()
				.onConsume(new ApplyStatusEffectsConsumeEffect(new MobEffectInstance(ULTRAKILL, POTION_TICKS)))
				.build())));

		ResourceKey<Block> terminalKey = ResourceKey.create(Registries.BLOCK, id("smileos_terminal"));
		SMILEOS_TERMINAL = Registry.register(BuiltInRegistries.BLOCK, terminalKey, new TerminalBlock(BlockBehaviour.Properties.of()
			.setId(terminalKey)
			.strength(1.5F, 6.0F)
			.sound(SoundType.METAL)
			.noOcclusion()));
		ResourceKey<Item> terminalItemKey = ResourceKey.create(Registries.ITEM, id("smileos_terminal"));
		SMILEOS_TERMINAL_ITEM = Registry.register(BuiltInRegistries.ITEM, terminalItemKey,
			new BlockItem(SMILEOS_TERMINAL, new Item.Properties().setId(terminalItemKey).useBlockDescriptionPrefix()));
		TERMINAL_ENTITY = Registry.register(BuiltInRegistries.BLOCK_ENTITY_TYPE, id("smileos_terminal"),
			new BlockEntityType<>(TerminalBlockEntity::new, Set.of(SMILEOS_TERMINAL)));

		// ULTRAKILL's skulls (only ever the one V1 holds: HeldItems), not in the creative inventory.
		String[] skulls = { null, "blue_skull", "red_skull", "green_skull" };
		for (int t = 1; t < skulls.length; t++) {
			ResourceKey<Item> key = ResourceKey.create(Registries.ITEM, id(skulls[t]));
			HeldItems.SKULLS[t] = Registry.register(BuiltInRegistries.ITEM, key, new Item(new Item.Properties()
				.setId(key)
				.stacksTo(16)
				.rarity(Rarity.RARE)));
		}

		Registry.register(BuiltInRegistries.CREATIVE_MODE_TAB, id("killcraft"), CreativeModeTab.builder(CreativeModeTab.Row.TOP, 0)
			.title(Component.translatable("itemGroup.killcraft"))
			.icon(() -> new ItemStack(ULTRAKILL_POTION))
			.displayItems((parameters, output) -> {
				output.accept(ULTRAKILL_POTION);
				output.accept(SMILEOS_TERMINAL_ITEM);
			})
			.build());

		ServerTickEvents.END_SERVER_TICK.register(Killcraft::tick);
		// While ULTRAKILL has V1, Minecraft's player only follows it about: what would hurt it (mobs,
		// lava, fire, falling into the void) hurts V1 instead.
		ServerLivingEntityEvents.ALLOW_DAMAGE.register((entity, source, amount) -> {
			if (entity instanceof ServerPlayer && following) {
				Link.push(EV_FOLLOWER_HURT, amount, 0, 0, 0, 0);
				return false;
			}
			return true;
		});
	}

	private static int ticks;

	private static final List<SkyLink.Actor> ACTORS = new ArrayList<>();
	private static volatile boolean following;
	private static boolean frozeIt;

	private static void tick(MinecraftServer server) {
		ticks++;
		// What Killcraft publishes (as SkyCraft actors): V1's skulls, destruction to do, ...
		boolean linked;
		try {
			linked = SkyLink.readActors(ACTORS);
		} catch (LinkageError e) {
			linked = false;
		}
		if (!linked) {
			ACTORS.clear();
		}
		// ULTRAKILL paused: Minecraft's world waits too (Killcraft says so; saying nothing, it doesn't).
		boolean freeze = false;
		for (SkyLink.Actor a : ACTORS) {
			if (a.formId() == HeldItems.STATE_ID) {
				freeze = a.width() > 0.5F;
			}
		}
		if (freeze != frozeIt) {
			frozeIt = freeze;
			server.tickRateManager().setFrozen(freeze);
		}
		for (ServerPlayer player : server.getPlayerList().getPlayers()) {
			HeldItems.tick(player, ticks, ACTORS);
			Destruction.tick(player, ACTORS);
			following = HeldItems.following(ACTORS);
			if (ticks % 5 == 0) {
				Link.push(EV_DIMENSION, player.level().dimension() == net.minecraft.world.level.Level.NETHER ? 1 : 0, 0, 0, 0, 0);
			}
			// A Nether portal trip makes a new player: Killcraft must not be moving this one then
			// (with the ULTRAKILL effect, or following V1).
			if (player.level().getBlockState(player.blockPosition()).is(Blocks.NETHER_PORTAL)) {
				Link.push(EV_ULTRAKILL_STOP, 0, 0, 0, 0, 0);
			} else if (player.hasEffect(ULTRAKILL) && ticks % 10 == 0) {
				Link.push(EV_ULTRAKILL_MOVES, 0, 0, 0, 0, 0);
			}
		}
	}

	private static final class UltrakillEffect extends MobEffect {
		UltrakillEffect() {
			super(MobEffectCategory.BENEFICIAL, 0xD01818);
		}
	}
}
