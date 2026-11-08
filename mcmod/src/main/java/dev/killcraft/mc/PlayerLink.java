package dev.killcraft.mc;

import dev.skycraft.link.SkyLink;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import net.fabricmc.fabric.api.networking.v1.PayloadTypeRegistry;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;
import net.minecraft.server.level.ServerPlayer;

/**
 * Each player's own Killcraft. Every player has their own ULTRAKILL, talking to their own Minecraft:
 * the host's reaches this (integrated) server through SkyCraft's shared memory, a friend's through
 * these packets. A friend's Minecraft client reads what its Killcraft publishes (Killcraft's own
 * actors: V1's state, destruction to do) and sends it here; Killcraft's events for that player go
 * back the same way, into their own ULTRAKILL.
 */
final class PlayerLink {
	/** A friend's Killcraft actors: as SkyLink.Actor, the ones in Killcraft's own id ranges. */
	record Actors(List<SkyLink.Actor> actors) implements CustomPacketPayload {
		static final Type<Actors> TYPE = new Type<>(Killcraft.id("actors"));
		static final StreamCodec<RegistryFriendlyByteBuf, Actors> CODEC = StreamCodec.of((buf, p) -> {
			buf.writeVarInt(p.actors.size());
			for (SkyLink.Actor a : p.actors) {
				buf.writeInt(a.formId());
				buf.writeVarInt(a.flags());
				buf.writeFloat(a.x());
				buf.writeFloat(a.y());
				buf.writeFloat(a.z());
				buf.writeFloat(a.yaw());
				buf.writeFloat(a.width());
				buf.writeFloat(a.height());
				buf.writeFloat(a.healthFrac());
				buf.writeVarInt(a.level());
				buf.writeUtf(a.name(), 32);
			}
		}, buf -> {
			int n = Math.min(buf.readVarInt(), MAX_ACTORS);
			List<SkyLink.Actor> actors = new ArrayList<>(n);
			for (int i = 0; i < n; i++) {
				actors.add(new SkyLink.Actor(buf.readInt(), buf.readVarInt(), buf.readFloat(), buf.readFloat(), buf.readFloat(),
					buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readVarInt(), buf.readUtf(32)));
			}
			return new Actors(actors);
		});

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/**
	 * A friend's ULTRAKILL enemies near them (as its Killcraft publishes them for SkyCraft), so this
	 * server has stand-ins for them to hit (GuestStandIns): SkyCraft only makes the host's.
	 */
	record Enemies(List<SkyLink.Actor> actors) implements CustomPacketPayload {
		static final Type<Enemies> TYPE = new Type<>(Killcraft.id("enemies"));
		static final StreamCodec<RegistryFriendlyByteBuf, Enemies> CODEC = StreamCodec.of(
			(buf, p) -> Actors.CODEC.encode(buf, new Actors(p.actors)),
			buf -> new Enemies(Actors.CODEC.decode(buf).actors()));

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** A hit on one of a friend's enemies' stand-ins, for their ULTRAKILL (as SkyCraft's own, EV_HIT_ACTOR). */
	record Hit(int formId, float damage, float pushX, float pushZ, float push, int flags, int weapon) implements CustomPacketPayload {
		static final Type<Hit> TYPE = new Type<>(Killcraft.id("hit"));
		static final StreamCodec<RegistryFriendlyByteBuf, Hit> CODEC = StreamCodec.of((buf, p) -> {
			buf.writeInt(p.formId);
			buf.writeFloat(p.damage);
			buf.writeFloat(p.pushX);
			buf.writeFloat(p.pushZ);
			buf.writeFloat(p.push);
			buf.writeInt(p.flags);
			buf.writeInt(p.weapon);
		}, buf -> new Hit(buf.readInt(), buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readInt(), buf.readInt()));

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** One of Killcraft's events (Killcraft.EV_...) for a friend's ULTRAKILL. */
	record Event(int kind, float a, float b, float c, float d, int flags) implements CustomPacketPayload {
		static final Type<Event> TYPE = new Type<>(Killcraft.id("event"));
		static final StreamCodec<RegistryFriendlyByteBuf, Event> CODEC = StreamCodec.of((buf, p) -> {
			buf.writeVarInt(p.kind);
			buf.writeFloat(p.a);
			buf.writeFloat(p.b);
			buf.writeFloat(p.c);
			buf.writeFloat(p.d);
			buf.writeVarInt(p.flags);
		}, buf -> new Event(buf.readVarInt(), buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readFloat(), buf.readVarInt()));

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/**
	 * A friend's player moved far in one go by their Killcraft (to V1: into the level, a checkpoint,
	 * after dying): their client moves it, and asks this server to put it there too, as its own
	 * teleport (it doesn't take a big move from a client's own movement).
	 */
	record Jump(double x, double y, double z, float yaw, float pitch) implements CustomPacketPayload {
		static final Type<Jump> TYPE = new Type<>(Killcraft.id("jump"));
		static final StreamCodec<RegistryFriendlyByteBuf, Jump> CODEC = StreamCodec.of((buf, p) -> {
			buf.writeDouble(p.x);
			buf.writeDouble(p.y);
			buf.writeDouble(p.z);
			buf.writeFloat(p.yaw);
			buf.writeFloat(p.pitch);
		}, buf -> new Jump(buf.readDouble(), buf.readDouble(), buf.readDouble(), buf.readFloat(), buf.readFloat()));

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	static final int MAX_ACTORS = 96;
	// A friend's actors go stale (their Minecraft stopped sending: ULTRAKILL closed, ...) after this.
	private static final long STALE_MS = 3000;

	private record Received(List<SkyLink.Actor> actors, long at) {
	}

	private static final Map<UUID, Received> FRIENDS = new ConcurrentHashMap<>();
	private static final Map<UUID, Received> FRIENDS_ENEMIES = new ConcurrentHashMap<>();

	private PlayerLink() {
	}

	static void init() {
		PayloadTypeRegistry.serverboundPlay().register(Actors.TYPE, Actors.CODEC);
		PayloadTypeRegistry.clientboundPlay().register(Event.TYPE, Event.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Actors.TYPE, (payload, context) -> {
			UUID id = context.player().getUUID();
			List<SkyLink.Actor> actors = List.copyOf(payload.actors());
			context.server().execute(() -> FRIENDS.put(id, new Received(actors, System.currentTimeMillis())));
		});
		PayloadTypeRegistry.serverboundPlay().register(Enemies.TYPE, Enemies.CODEC);
		PayloadTypeRegistry.clientboundPlay().register(Hit.TYPE, Hit.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Enemies.TYPE, (payload, context) -> {
			UUID id = context.player().getUUID();
			List<SkyLink.Actor> actors = List.copyOf(payload.actors());
			context.server().execute(() -> FRIENDS_ENEMIES.put(id, new Received(actors, System.currentTimeMillis())));
		});
		PayloadTypeRegistry.serverboundPlay().register(Jump.TYPE, Jump.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Jump.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			context.server().execute(() -> {
				if (!Double.isFinite(payload.x()) || !Double.isFinite(payload.y()) || !Double.isFinite(payload.z()) || player.isRemoved()) {
					return;
				}
				player.connection.teleport(payload.x(), payload.y(), payload.z(), payload.yaw(), payload.pitch());
				player.resetFallDistance();
				Killcraft.LOG.info("Killcraft: {} moved to their V1 at {} {} {}", player.getPlainTextName(),
					Math.round(payload.x()), Math.round(payload.y()), Math.round(payload.z()));
			});
		});
		ServerPlayConnectionEvents.DISCONNECT.register((handler, server) -> {
			FRIENDS.remove(handler.getPlayer().getUUID());
			FRIENDS_ENEMIES.remove(handler.getPlayer().getUUID());
		});
	}

	/** True if this player plays on this machine (their ULTRAKILL is on the shared-memory link). */
	static boolean isHost(ServerPlayer player) {
		var server = player.level().getServer();
		return server != null && server.isSingleplayerOwner(player.nameAndId());
	}

	/** What this player's Killcraft publishes (the host's: read this tick, hostActors). */
	static List<SkyLink.Actor> actors(ServerPlayer player, List<SkyLink.Actor> hostActors) {
		if (isHost(player)) {
			return hostActors;
		}
		Received r = FRIENDS.get(player.getUUID());
		return r != null && System.currentTimeMillis() - r.at < STALE_MS ? r.actors : List.of();
	}

	/** A friend's enemies near them (none if their Minecraft stopped saying). */
	static List<SkyLink.Actor> enemies(ServerPlayer player) {
		Received r = FRIENDS_ENEMIES.get(player.getUUID());
		return r != null && System.currentTimeMillis() - r.at < STALE_MS ? r.actors : List.of();
	}

	/** A hit on a friend's enemy (its stand-in here), for their ULTRAKILL. */
	static void pushHit(ServerPlayer player, int formId, float[] hit) {
		if (ServerPlayNetworking.canSend(player, Hit.TYPE)) {
			ServerPlayNetworking.send(player, new Hit(formId, hit[0], hit[1], hit[2], hit[3], Float.floatToRawIntBits(hit[4]), Float.floatToRawIntBits(hit[5])));
		}
	}

	/** One of Killcraft's events for this player's ULTRAKILL. */
	static void push(ServerPlayer player, int kind, float a, float b, float c, float d, int flags) {
		if (isHost(player)) {
			Link.push(kind, a, b, c, d, flags);
		} else if (ServerPlayNetworking.canSend(player, Event.TYPE)) {
			ServerPlayNetworking.send(player, new Event(kind, a, b, c, d, flags));
		}
	}

	/** Killcraft's own actors (not SkyCraft's ULTRAKILL enemies): its state and its requests. */
	static boolean ours(SkyLink.Actor a) {
		return a.formId() == HeldItems.STATE_ID
			|| (a.formId() >= Destruction.REQUEST_IDS && a.formId() <= Destruction.REQUEST_IDS + Destruction.REQUEST_COUNT);
	}
}
