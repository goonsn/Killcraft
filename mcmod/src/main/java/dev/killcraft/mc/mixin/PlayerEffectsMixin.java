package dev.killcraft.mc.mixin;

import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Potion effects wait while ULTRAKILL is paused. Killcraft pauses Minecraft by freezing its ticking,
 * which stops everything but players: their effects kept running out (and kept queueing particles).
 * While Minecraft is frozen, a player's effects don't tick.
 */
@Mixin(LivingEntity.class)
public abstract class PlayerEffectsMixin {
	@Inject(method = "tickEffects", at = @At("HEAD"), cancellable = true)
	private void killcraft$pausedEffects(CallbackInfo ci) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (self instanceof Player && self.level().tickRateManager().isFrozen()) {
			ci.cancel();
		}
	}
}
