package dev.killcraft.mc;

import dev.skycraft.combat.SkyCombat;
import dev.skycraft.combat.SkyrimActorEntity;
import dev.skycraft.link.SkyLink;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;

/**
 * Hittable stand-ins for friends' ULTRAKILL enemies. SkyCraft makes them (invisible mobs where each
 * enemy is, which Minecraft's swords, arrows, tridents and mobs hit) only from the host's ULTRAKILL,
 * so a friend's arrows went through the enemies round them and did nothing. Each friend's Minecraft
 * sends where its enemies are (PlayerLink.Enemies); they get stand-ins here the same way, and hits on
 * those go back to that friend's ULTRAKILL as SkyCraft's own hits do to the host's.
 * (Not where the host's stand-in for the same enemy already is: one enemy, one stand-in.)
 */
final class GuestStandIns {
	private static final Map<UUID, Map<Integer, SkyrimActorEntity>> BY_PLAYER = new HashMap<>();

	private GuestStandIns() {
	}

	static void init() {
		ServerTickEvents.END_SERVER_TICK.register(GuestStandIns::tick);
	}

	private static void tick(MinecraftServer server) {
		Set<UUID> seen = new HashSet<>();
		for (ServerPlayer player : server.getPlayerList().getPlayers()) {
			if (PlayerLink.isHost(player)) {
				continue;
			}
			UUID id = player.getUUID();
			seen.add(id);
			Map<Integer, SkyrimActorEntity> mine = BY_PLAYER.computeIfAbsent(id, k -> new HashMap<>());
			sync(player, PlayerLink.enemies(player), mine);
			for (SkyrimActorEntity proxy : mine.values()) {
				float[] hit = proxy.takeHit();
				if (hit != null && (hit[0] > 0.0F || hit[3] > 0.0F)) {
					PlayerLink.pushHit(player, proxy.formId(), hit);
					Killcraft.LOG.info("Killcraft: {} hit {} for {}", player.getPlainTextName(), proxy.getName().getString(), hit[0]);
				}
			}
		}
		for (Iterator<Map.Entry<UUID, Map<Integer, SkyrimActorEntity>>> it = BY_PLAYER.entrySet().iterator(); it.hasNext(); ) {
			Map.Entry<UUID, Map<Integer, SkyrimActorEntity>> e = it.next();
			if (!seen.contains(e.getKey())) {
				e.getValue().values().forEach(Entity::discard);
				it.remove();
			}
		}
	}

	private static void sync(ServerPlayer player, List<SkyLink.Actor> actors, Map<Integer, SkyrimActorEntity> mine) {
		ServerLevel level = player.level();
		Map<Integer, SkyLink.Actor> live = new HashMap<>();
		for (SkyLink.Actor a : actors) {
			if (!a.dead() && !coveredElsewhere(player, a, level)) {
				live.put(a.formId(), a);
			}
		}
		int before = mine.size();
		for (Iterator<Map.Entry<Integer, SkyrimActorEntity>> it = mine.entrySet().iterator(); it.hasNext(); ) {
			Map.Entry<Integer, SkyrimActorEntity> e = it.next();
			SkyrimActorEntity proxy = e.getValue();
			if (!live.containsKey(e.getKey()) || proxy.isRemoved() || proxy.level() != level) {
				proxy.discard();
				it.remove();
			}
		}
		for (SkyLink.Actor a : live.values()) {
			SkyrimActorEntity proxy = mine.get(a.formId());
			if (proxy == null) {
				proxy = new SkyrimActorEntity(SkyCombat.SKYRIM_ACTOR, level);
				proxy.setFormId(a.formId());
				proxy.setSize(a.width(), a.height());
				proxy.snapTo(a.x(), a.y(), a.z(), a.yaw(), 0.0F);
				if (!a.name().isEmpty()) {
					proxy.setCustomName(Component.literal(a.name()));
				}
				if (level.addFreshEntity(proxy)) {
					mine.put(a.formId(), proxy);
				}
				continue;
			}
			proxy.setSize(a.width(), a.height());
			proxy.setPos(a.x(), a.y(), a.z());
			proxy.setYRot(a.yaw());
			proxy.setYHeadRot(a.yaw());
		}
		if (mine.size() != before && (mine.size() < 5 || mine.size() % 10 == 0)) {
			Killcraft.LOG.info("Killcraft: {} of {}'s ULTRAKILL enemies have stand-ins here", mine.size(), player.getPlainTextName());
		}
	}

	// The same enemy already has a stand-in near there: the host's (SkyCraft's), or another friend's.
	private static boolean coveredElsewhere(ServerPlayer player, SkyLink.Actor a, ServerLevel level) {
		SkyrimActorEntity host = SkyCombat.proxy(a.formId());
		if (host != null && !host.isRemoved() && host.level() == level && host.distanceToSqr(a.x(), a.y(), a.z()) < 2.0 * 2.0) {
			return true;
		}
		for (Map.Entry<UUID, Map<Integer, SkyrimActorEntity>> e : BY_PLAYER.entrySet()) {
			if (e.getKey().equals(player.getUUID())) {
				continue;
			}
			SkyrimActorEntity other = e.getValue().get(a.formId());
			if (other != null && !other.isRemoved() && other.level() == level && other.distanceToSqr(a.x(), a.y(), a.z()) < 2.0 * 2.0) {
				return true;
			}
		}
		return false;
	}
}
