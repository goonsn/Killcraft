package dev.killcraft.mc.mixin;

import dev.killcraft.mc.KillNetClient;
import net.minecraft.client.gui.components.ChatComponent;
import net.minecraft.client.multiplayer.chat.GuiMessageSource;
import net.minecraft.client.multiplayer.chat.GuiMessageTag;
import net.minecraft.network.chat.Component;
import net.minecraft.network.chat.MessageSignature;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Minecraft's chat in ULTRAKILL mode: Minecraft's screen (and its chat) isn't shown then, so every
 * line also goes to ULTRAKILL, which shows the latest ones (Killcraft's ChatLines). Reading only:
 * typing is Minecraft mode's.
 */
@Mixin(ChatComponent.class)
public abstract class ChatToUltrakillMixin {
	@Inject(method = "addMessage", at = @At("HEAD"))
	private void killcraft$toUltrakill(Component message, MessageSignature signature, GuiMessageSource source, GuiMessageTag tag, CallbackInfo ci) {
		KillNetClient.chatLine(message.getString());
	}
}
