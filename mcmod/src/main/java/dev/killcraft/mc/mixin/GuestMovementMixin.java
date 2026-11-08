package dev.killcraft.mc.mixin;

import dev.skycraft.link.SkyLink;
import net.minecraft.server.network.ServerGamePacketListenerImpl;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * A friend's Minecraft player follows their own V1: ULTRAKILL moves it, often far in one go (into
 * the level, to a checkpoint, a dash). Minecraft's server sends a player who moves more than a few
 * blocks at once back where it was ("moved too quickly"): the friend was held out in the void,
 * where they'd joined. The host's own player was never checked (Minecraft doesn't check the
 * world's owner); with Killcraft running, nobody is.
 */
@Mixin(ServerGamePacketListenerImpl.class)
public abstract class GuestMovementMixin {
	@Inject(method = "shouldCheckPlayerMovement", at = @At("HEAD"), cancellable = true)
	private void killcraft$friendsFollowTheirV1(boolean elytra, CallbackInfoReturnable<Boolean> cir) {
		if (SkyLink.active()) {
			cir.setReturnValue(false);
		}
	}
}
