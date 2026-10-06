package dev.killcraft.mc;

import dev.skycraft.link.SkyLink;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.ExplosionDamageCalculator;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.BaseFireBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;

import java.util.LinkedHashSet;
import java.util.List;

/**
 * ULTRAKILL's explosions and charged beams tear up the Nether. Killcraft asks for them as dead
 * actors with ids from REQUEST_IDS (each a while, under a name of its own): level 1 is an explosion
 * at x, y, z of power width, level 2 a tunnel from x, y, z to yaw, width, height of radius
 * healthFrac; bit 0x100 sets fires. The explosions only break blocks: ULTRAKILL's own already hurt
 * whoever they hit. Level 3 is ULTRAKILL's weapons hitting a mob (entity id x, damage width,
 * pushed along yaw, height by healthFrac), 4 the chainsaw cutting blocks within healthFrac of x, y, z.
 */
final class Destruction {
	static final int REQUEST_IDS = 0x3FFFFE00, REQUEST_COUNT = 256;
	private static final int MAX_TUNNEL_BLOCKS = 800;
	private static final LinkedHashSet<String> DONE = new LinkedHashSet<>();

	private static final ExplosionDamageCalculator BLOCKS_ONLY = new ExplosionDamageCalculator() {
		@Override
		public boolean shouldDamageEntity(Explosion explosion, Entity entity) {
			return false;
		}

		@Override
		public float getKnockbackMultiplier(Entity entity) {
			return 0.0F;
		}
	};

	private Destruction() {
	}

	static void tick(ServerPlayer player, List<SkyLink.Actor> actors) {
		ServerLevel level = (ServerLevel) player.level();
		for (SkyLink.Actor a : actors) {
			if (a.formId() < REQUEST_IDS || a.formId() > REQUEST_IDS + REQUEST_COUNT || !DONE.add(a.name())) {
				continue;
			}
			while (DONE.size() > 1024) {
				DONE.remove(DONE.iterator().next());
			}
			int kind = a.level() & 0xFF;
			// ULTRAKILL's weapons on a mob (anywhere): as the player hit it.
			if (kind == 3) {
				hurt(level, player, Math.round(a.x()), a.width(), a.yaw(), a.height(), a.healthFrac());
				continue;
			}
			// Out of the Nether, back through the player's portal (Killcraft's data pack).
			if (kind == 5) {
				var server = level.getServer();
				server.getCommands().performPrefixedCommand(server.createCommandSourceStack().withSuppressedOutput(),
					"execute as " + player.getStringUUID() + " at @s run function killcraft:leave_nether");
				continue;
			}
			if (level.dimension() != Level.NETHER) {
				continue;
			}
			boolean fire = (a.level() & 0x100) != 0;
			switch (kind) {
				case 4 -> dig(level, player, new Vec3(a.x(), a.y(), a.z()), Math.min(a.healthFrac(), 3.0F));
				case 1 -> level.explode(null, null, BLOCKS_ONLY, a.x(), a.y(), a.z(), Math.max(0.5F, Math.min(a.width(), 16.0F)), fire,
					Level.ExplosionInteraction.TNT);
				case 2 -> tunnel(level, new Vec3(a.x(), a.y(), a.z()), new Vec3(a.yaw(), a.width(), a.height()), Math.min(a.healthFrac(), 4.0F), fire);
				default -> {
				}
			}
		}
	}

	private static void hurt(ServerLevel level, ServerPlayer player, int id, float amount, float dx, float dz, float knock) {
		Entity e = level.getEntity(id);
		if (!(e instanceof LivingEntity mob) || e instanceof Player || !e.isAlive() || amount <= 0.0F
			|| e.getClass().getName().startsWith("dev.skycraft")) {
			return;
		}
		mob.setInvulnerableTime(0);  // (ULTRAKILL's weapons hit fast: every hit counts)
		mob.hurtServer(level, level.damageSources().playerAttack(player), Math.min(amount, 1000.0F));
		if (knock > 0.0F && (dx != 0.0F || dz != 0.0F)) {
			mob.push(dx * knock, 0.1 * knock, dz * knock);
		}
	}

	/** The chainsaw: the blocks within radius of where it is break, and drop what they would. */
	private static void dig(ServerLevel level, ServerPlayer player, Vec3 at, float radius) {
		int r = (int) Math.ceil(radius);
		for (int dx = -r; dx <= r; dx++) {
			for (int dy = -r; dy <= r; dy++) {
				for (int dz = -r; dz <= r; dz++) {
					if (dx * dx + dy * dy + dz * dz > radius * radius) {
						continue;
					}
					BlockPos pos = BlockPos.containing(at.x + dx, at.y + dy, at.z + dz);
					BlockState state = level.getBlockState(pos);
					if (state.isAir() || state.getDestroySpeed(level, pos) < 0.0F || !state.getFluidState().isEmpty()) {
						continue;
					}
					level.destroyBlock(pos, true, player);
				}
			}
		}
	}

	private static void tunnel(ServerLevel level, Vec3 from, Vec3 to, float radius, boolean fire) {
		Vec3 along = to.subtract(from);
		double length = along.length();
		if (length < 1.0E-3 || length > 256.0 || radius <= 0.0F) {
			return;
		}
		Vec3 dir = along.scale(1.0 / length);
		int broken = 0;
		int r = (int) Math.ceil(radius);
		for (double t = 0.0; t <= length && broken < MAX_TUNNEL_BLOCKS; t += 0.5) {
			Vec3 c = from.add(dir.scale(t));
			for (int dx = -r; dx <= r; dx++) {
				for (int dy = -r; dy <= r; dy++) {
					for (int dz = -r; dz <= r; dz++) {
						if (dx * dx + dy * dy + dz * dz > radius * radius) {
							continue;
						}
						BlockPos pos = BlockPos.containing(c.x + dx, c.y + dy, c.z + dz);
						BlockState state = level.getBlockState(pos);
						if (state.isAir() || state.getDestroySpeed(level, pos) < 0.0F || !state.getFluidState().isEmpty()) {
							continue;
						}
						level.destroyBlock(pos, false);
						broken++;
						if (fire && level.getRandom().nextInt(4) == 0 && BaseFireBlock.canBePlacedAt(level, pos, net.minecraft.core.Direction.UP)) {
							level.setBlockAndUpdate(pos, BaseFireBlock.getState(level, pos));
						}
					}
				}
			}
		}
	}
}
