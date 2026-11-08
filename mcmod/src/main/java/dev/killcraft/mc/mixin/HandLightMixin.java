package dev.killcraft.mc.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.killcraft.mc.KillNetClient;
import net.minecraft.client.renderer.FirstPersonHandsAndItemsRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.level.FirstPersonHandsAndItemsRenderState;
import net.minecraft.client.renderer.state.level.PlayerRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * The hand and what it holds, in first person, lit as ULTRAKILL lights where V1 stands (Killcraft
 * sends that: KillNetClient.handLight), not by Minecraft's light there (daylight, in Killcraft's
 * empty world): drawn over ULTRAKILL's level, they'd look out of place.
 */
@Mixin(FirstPersonHandsAndItemsRenderer.class)
public abstract class HandLightMixin {
	@Inject(method = "submitHandsWithItems", at = @At("HEAD"))
	private void killcraft$ultrakillLight(float partialTick, PoseStack pose, SubmitNodeCollector collector, PlayerRenderState player,
		FirstPersonHandsAndItemsRenderState hands, CallbackInfo ci) {
		int light = KillNetClient.handLight;
		if (light >= 0 && player != null && player.avatarRenderState != null) {
			player.avatarRenderState.lightCoords = light;
		}
	}
}
