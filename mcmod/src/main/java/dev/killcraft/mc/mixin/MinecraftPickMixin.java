package dev.killcraft.mc.mixin;

import dev.skycraft.link.SkyLink;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.projectile.ProjectileUtil;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Melee reaches enemies at ULTRAKILL's walls and floors. SkyCraft ends the crosshair at ULTRAKILL's
 * surfaces, at the block cell a placed block would take (up to a block short of the surface), so an
 * enemy standing at a wall, a step or a ledge could only be hit from some angles. When the crosshair
 * stops at ULTRAKILL's geometry (not a real block), a mob or enemy within reach just past it
 * (1.5 blocks) is targeted instead.
 */
@Mixin(Minecraft.class)
public abstract class MinecraftPickMixin {
	@Shadow
	public HitResult hitResult;
	@Shadow
	public Entity crosshairPickEntity;
	private static int killcraft$badLogged;

	@Inject(method = "pick", at = @At("TAIL"))
	private void killcraft$reachPastLevel(float partialTick, CallbackInfo ci) {
		Minecraft self = (Minecraft) (Object) this;
		if (!SkyLink.active() || !(hitResult instanceof BlockHitResult block) || block.getType() != HitResult.Type.BLOCK
			|| self.level == null || !(self.getCameraEntity() instanceof Player player)) {
			return;
		}
		Vec3 eye = player.getEyePosition(partialTick);
		// A target out of reach or outside the world's height (a bad hit on ULTRAKILL's geometry):
		// nothing is targeted, rather than Minecraft refusing a block "above the height limit".
		Vec3 at = block.getLocation();
		if (!Double.isFinite(at.x) || !Double.isFinite(at.y) || !Double.isFinite(at.z)
			|| eye.distanceTo(at) > player.blockInteractionRange() + 2.0 || self.level.isOutsideBuildHeight(block.getBlockPos())) {
			if (killcraft$badLogged++ < 5) {
				dev.killcraft.mc.Killcraft.LOG.info("Killcraft: ignored a crosshair target out of reach: {} at {} (eye {}){}", block.getBlockPos(), at, eye,
					block.getClass().getSimpleName().contains("Skyrim") ? ", on ULTRAKILL's geometry" : "");
			}
			hitResult = BlockHitResult.miss(eye, block.getDirection(), player.blockPosition());
			return;
		}
		if (!self.level.getBlockState(block.getBlockPos()).isAir()) {
			return;
		}
		double reach = Math.min(player.entityInteractionRange(), eye.distanceTo(block.getLocation()) + 1.5);
		Vec3 look = player.getViewVector(partialTick);
		Vec3 end = eye.add(look.scale(reach));
		AABB area = player.getBoundingBox().expandTowards(look.scale(reach)).inflate(1.0);
		EntityHitResult hit = ProjectileUtil.getEntityHitResult(player, eye, end, area, e -> !e.isSpectator() && e.isPickable(), reach * reach);
		if (hit != null && eye.distanceToSqr(hit.getLocation()) <= reach * reach) {
			hitResult = hit;
			crosshairPickEntity = hit.getEntity();
		}
	}
}
