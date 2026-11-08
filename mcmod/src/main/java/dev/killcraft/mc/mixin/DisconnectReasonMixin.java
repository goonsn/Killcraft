package dev.killcraft.mc.mixin;

import dev.killcraft.mc.KillcraftClient;
import net.minecraft.client.gui.screens.DisconnectedScreen;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.DisconnectionDetails;
import net.minecraft.network.chat.Component;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Why a friend's world couldn't be joined (or dropped us). SkyCraft takes Minecraft's disconnect
 * screen away at once, back to the player's own world, with only "Couldn't stay in ...": Minecraft's
 * own reason is kept here, logged and said in chat (KillcraftClient).
 */
@Mixin(DisconnectedScreen.class)
public abstract class DisconnectReasonMixin {
	@Inject(method = "<init>(Lnet/minecraft/client/gui/screens/Screen;Lnet/minecraft/network/chat/Component;Lnet/minecraft/network/DisconnectionDetails;Lnet/minecraft/network/chat/Component;)V",
		at = @At("RETURN"))
	private void killcraft$keepReason(Screen parent, Component title, DisconnectionDetails details, Component buttonText, CallbackInfo ci) {
		KillcraftClient.disconnected(title, details.reason());
	}
}
