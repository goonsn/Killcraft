package dev.killcraft.mc.mixin;

import net.minecraft.client.server.IntegratedServer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * A Killcraft party is up to 10 players: a world opened to friends takes 10 (Minecraft's own is 8).
 */
@Mixin(IntegratedServer.class)
public abstract class PartySizeMixin {
	@Inject(method = "getMaxPlayers", at = @At("HEAD"), cancellable = true)
	private void killcraft$tenPlayers(CallbackInfoReturnable<Integer> cir) {
		cir.setReturnValue(10);
	}
}
