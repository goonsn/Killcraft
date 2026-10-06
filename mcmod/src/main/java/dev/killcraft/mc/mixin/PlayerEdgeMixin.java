package dev.killcraft.mc.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.skycraft.link.SkyLink;
import dev.skycraft.world.SkyCollision;
import net.minecraft.core.BlockPos;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.MoverType;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;

/**
 * Crouching stops at edges again (bridging). SkyCraft turns Minecraft's edge check off while linked:
 * it looks for blocks under the player, and ULTRAKILL's floors aren't blocks, so every step looked
 * like a drop. Here it's Minecraft's own check, with ULTRAKILL's floors (SkyCraft's collision) counted
 * as ground too. (This wraps the whole method, SkyCraft's change included.)
 */
@Mixin(Player.class)
public abstract class PlayerEdgeMixin {
	@Shadow
	protected abstract boolean isStayingOnGroundSurface();

	@WrapMethod(method = "maybeBackOffFromEdge")
	private Vec3 killcraft$edges(Vec3 delta, MoverType type, Operation<Vec3> original) {
		if (!SkyLink.active()) {
			return original.call(delta, type);
		}
		Player self = (Player) (Object) this;
		float step = self.maxUpStep();
		if (self.getAbilities().flying || delta.y > 0.0 || (type != MoverType.SELF && type != MoverType.PLAYER) || !isStayingOnGroundSurface()
			|| !(self.onGround() || self.fallDistance < step && !killcraft$canFall(self, 0.0, 0.0, step - self.fallDistance))) {
			return delta;
		}
		double dx = delta.x, dz = delta.z;
		double sx = Math.signum(dx) * 0.05, sz = Math.signum(dz) * 0.05;
		while (dx != 0.0 && killcraft$canFall(self, dx, 0.0, step)) {
			if (Math.abs(dx) <= 0.05) {
				dx = 0.0;
				break;
			}
			dx -= sx;
		}
		while (dz != 0.0 && killcraft$canFall(self, 0.0, dz, step)) {
			if (Math.abs(dz) <= 0.05) {
				dz = 0.0;
				break;
			}
			dz -= sz;
		}
		while (dx != 0.0 && dz != 0.0 && killcraft$canFall(self, dx, dz, step)) {
			dx = Math.abs(dx) <= 0.05 ? 0.0 : dx - sx;
			dz = Math.abs(dz) <= 0.05 ? 0.0 : dz - sz;
		}
		return new Vec3(dx, delta.y, dz);
	}

	/** Nothing to stand on below the player moved by dx, dz: no blocks, and none of ULTRAKILL's floor. */
	private static boolean killcraft$canFall(Player self, double dx, double dz, double height) {
		AABB box = self.getBoundingBox();
		AABB below = new AABB(box.minX + dx, box.minY - height - 1.0E-5, box.minZ + dz, box.maxX + dx, box.minY, box.maxZ + dz);
		if (!self.level().noCollision(self, below)) {
			return false;
		}
		int x0 = Mth.floor(below.minX), x1 = Mth.floor(below.maxX - 1.0E-7);
		int y0 = Mth.floor(below.minY), y1 = Mth.floor(below.maxY - 1.0E-7);
		int z0 = Mth.floor(below.minZ), z1 = Mth.floor(below.maxZ - 1.0E-7);
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		for (int x = x0; x <= x1; x++) {
			for (int y = y0; y <= y1; y++) {
				for (int z = z0; z <= z1; z++) {
					if (SkyCollision.hasGeometry(pos.set(x, y, z))) {
						return false;
					}
				}
			}
		}
		return true;
	}
}
