package dev.killcraft.mc.mixin;

import dev.killcraft.mc.KillNetClient;
import net.minecraft.client.renderer.culling.Frustum;
import net.minecraft.client.renderer.entity.EntityRenderDispatcher;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Another player in ULTRAKILL mode: their Minecraft body only follows their V1 about (teleported,
 * sliding without walking), and ULTRAKILL draws their V1 instead. It isn't drawn (nor its armour
 * and held item).
 */
@Mixin(EntityRenderDispatcher.class)
public abstract class HidePlayerMixin {
	@Inject(method = "shouldRender", at = @At("HEAD"), cancellable = true)
	private <E extends Entity> void killcraft$hideFollower(E entity, Frustum frustum, double x, double y, double z, float partialTick,
		CallbackInfoReturnable<Boolean> cir) {
		if (KillNetClient.hidden(entity)) {
			cir.setReturnValue(false);
		}
	}
}
