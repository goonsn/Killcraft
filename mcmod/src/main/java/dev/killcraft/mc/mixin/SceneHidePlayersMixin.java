package dev.killcraft.mc.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.killcraft.mc.KillNetClient;
import java.util.ArrayList;
import java.util.List;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * SkyCraft draws Minecraft's entities into ULTRAKILL itself, from the level's entities (not through
 * Minecraft's own culling, HidePlayerMixin): another player whom ULTRAKILL shows as V1 is left out
 * of those too.
 */
@Mixin(targets = "dev.skycraft.client.render.AvatarExporter", remap = false)
public abstract class SceneHidePlayersMixin {
	@WrapOperation(method = "exportScene", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/client/multiplayer/ClientLevel;entitiesForRendering()Ljava/lang/Iterable;"))
	private Iterable<Entity> killcraft$withoutV1Players(ClientLevel level, Operation<Iterable<Entity>> original) {
		Iterable<Entity> all = original.call(level);
		if (!KillNetClient.hidingAny()) {
			return all;
		}
		List<Entity> shown = new ArrayList<>();
		for (Entity e : all) {
			if (!KillNetClient.hidden(e)) {
				shown.add(e);
			}
		}
		return shown;
	}
}
