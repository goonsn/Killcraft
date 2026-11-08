package dev.killcraft.mc.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import net.minecraft.core.Direction;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * SkyCraft sends Minecraft's blocks to ULTRAKILL as triangles marked cutout or see-through: water
 * among the see-through ones, like glass. Its see-through fluid (water: lava is solid) is marked as
 * water too (bit 0x100), so Killcraft's shaders can draw it as water.
 */
@Mixin(targets = "dev.skycraft.client.render.WorldExporter$MeshBuilder", remap = false)
public abstract class WaterFlagMixin {
	@WrapOperation(method = "addVertex(FFFIFFIIFFF)V", at = @At(value = "INVOKE",
		target = "Ldev/skycraft/client/render/WorldExporter$MeshBuilder;flags(ZLnet/minecraft/core/Direction;)I"))
	private int killcraft$markWater(boolean translucent, Direction normal, Operation<Integer> original) {
		int flags = original.call(translucent, normal);
		return translucent ? flags | 0x100 : flags;
	}
}
