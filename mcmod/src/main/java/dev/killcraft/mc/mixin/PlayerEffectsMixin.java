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
 * While Minecraft is frozen, a player's effects don't tick; with friends (the world doesn't wait), a
 * player whose ULTRAKILL is paused has theirs wait.
 */
@Mixin(LivingEntity.class)
public abstract class PlayerEffectsMixin {
	@Inject(method = "tickEffects", at = @At("HEAD"), cancellable = true)
	private void killcraft$pausedEffects(CallbackInfo ci) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (self instanceof Player player && (self.level().tickRateManager().isFrozen() || dev.killcraft.mc.Killcraft.paused(player))) {
			ci.cancel();
		}
	}
}
