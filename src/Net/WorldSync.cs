using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // The level's things, alike for everyone in it (KillNet):
    //  - glass and breakables: one broken in one player's ULTRAKILL breaks in the others' (named, as
    //    enemies are, by their place in the level: EnemySync.PathId);
    //  - doors: ones that open for the player walking up to them (their DoorController) open for
    //    another player walking up too, as for an enemy (locked ones stay locked), so nobody is seen
    //    walking through a door that is shut here;
    //  - skulls on altars (ItemPlaceZone): one put on an altar in one player's ULTRAKILL is put on the
    //    same altar in the others' (their copy of the same skull, or another of its colour), and taken
    //    off it there as it's taken off, so the doors it opens open for everyone;
    //  - hook points (the whiplash's): what grabbing one, being pulled to one or pulling a switch one does
    //    in the level (its events: a door, a lift, a bridge) happens in the others' too, and a pulled
    //    switch goes dark and comes back there as here;
    //  - lifts (Elevator, Fraud's): sent to a floor by one player, they go there in the others' too.
    internal static class WorldSync
    {
        public const byte Broke = 21;
        private const byte KindGlass = 0, KindBreakable = 1, KindPlaced = 2, KindTaken = 3, KindWave = 4, KindHook = 5, KindLift = 6;
        private const byte HookGrabbed = 0, HookLetGo = 1, HookReached = 2, HookSwitch = 3;

        private static bool applying;
        private static readonly Dictionary<uint, Component> things = new Dictionary<uint, Component>();
        private static float rescanAt = -100f, doorsAt = -100f;
        private static DoorController[] doors = new DoorController[0];
        private static readonly HashSet<DoorController> openedForOthers = new HashSet<DoorController>();
        // (When each was last near another player: one goes on being open a moment after, so a player
        // standing at its edge doesn't open and shut it with each step.)
        private static readonly Dictionary<DoorController, float> nearAt = new Dictionary<DoorController, float>();
        // Skulls, by where each was in the level when first seen (the same in everyone's ULTRAKILL).
        private static readonly Dictionary<ItemIdentifier, uint> itemIds = new Dictionary<ItemIdentifier, uint>();
        private static readonly Dictionary<uint, ItemPlaceZone> zones = new Dictionary<uint, ItemPlaceZone>();
        private static readonly AccessTools.FieldRef<DoorController, bool> enemyIn = AccessTools.FieldRefAccess<DoorController, bool>("enemyIn");
        private static readonly AccessTools.FieldRef<DoorController, List<EnemyIdentifier>> doorUsers =
            AccessTools.FieldRefAccess<DoorController, List<EnemyIdentifier>>("doorUsers");

        public static void LevelChanged()
        {
            things.Clear();
            openedForOthers.Clear();
            nearAt.Clear();
            itemIds.Clear();
            zones.Clear();
            wavesFromOthers.Clear();
            doors = new DoorController[0];
            doorsAt = -100f;
            arenaInside.Clear();
            arenasAt = -100f;
        }

        public static void Install(Harmony harmony)
        {
            Hook(harmony, typeof(Glass), nameof(Glass.Shatter), nameof(GlassBefore), nameof(GlassAfter));
            Hook(harmony, typeof(Breakable), nameof(Breakable.Break), nameof(BreakableBefore), nameof(BreakableAfter), new[] { typeof(float) });
            Hook(harmony, typeof(Punch), nameof(Punch.PlaceHeldObject), nameof(PlaceBefore), nameof(PlaceAfter));
            Hook(harmony, typeof(Punch), nameof(Punch.ForceHold), nameof(HoldBefore), nameof(HoldAfter));
            Hook(harmony, typeof(ActivateNextWave), "FixedUpdate", nameof(WaveBefore), nameof(WaveAfter));
            After(harmony, typeof(HookPoint), nameof(HookPoint.Hooked), nameof(HookGrabbedAfter));
            After(harmony, typeof(HookPoint), nameof(HookPoint.Unhooked), nameof(HookLetGoAfter));
            After(harmony, typeof(HookPoint), nameof(HookPoint.Reached), nameof(HookReachedAfter), new[] { typeof(Vector3) });
            After(harmony, typeof(HookPoint), nameof(HookPoint.SwitchPulled), nameof(HookSwitchAfter));
            Hook(harmony, typeof(Elevator), nameof(Elevator.MoveToFloor), nameof(LiftBefore), nameof(LiftAfter));
        }

        private static void After(Harmony harmony, Type type, string method, string postfix, Type[] args = null)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method, args), postfix: new HarmonyMethod(typeof(WorldSync), postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's {type.Name}.{method} ({e.Message}); it won't happen for other players");
            }
        }

        // ---- hook points ----
        // (Only this player's own grabs: the others' are theirs to tell. A player's heal at a Providence
        // hook point is theirs too: the others get its events, not the heal.)

        private static void HookGrabbedAfter(HookPoint __instance) => TellHook(__instance, HookGrabbed);
        private static void HookLetGoAfter(HookPoint __instance) => TellHook(__instance, HookLetGo);
        private static void HookReachedAfter(HookPoint __instance) => TellHook(__instance, HookReached);
        private static void HookSwitchAfter(HookPoint __instance) => TellHook(__instance, HookSwitch);

        private static void TellHook(HookPoint hook, byte what)
        {
            if (applying || hook == null || !Room.Sharing || ReplayPlayer.Active)
            {
                return;
            }
            uint id = EnemySync.PathId(hook.transform);
            KillNet.Send(Broke, KillNet.ToRoom, w =>
            {
                w.Write(KindHook);
                w.Write(id);
                w.Write(what);
            });
        }

        private static void HandleHook(uint id, BinaryReader r)
        {
            byte what = r.ReadByte();
            HookPoint hook = Lookup(KindHook, id) as HookPoint;
            if (hook == null)
            {
                return;
            }
            applying = true;
            try
            {
                switch (what)
                {
                    case HookGrabbed:
                        hook.onHook?.Invoke();
                        break;
                    case HookLetGo:
                        hook.onUnhook?.Invoke();
                        break;
                    case HookReached:
                        hook.onReach?.Invoke();
                        break;
                    case HookSwitch:
                        // (As pulled here: its events, dark, back on after its time.)
                        if (hook.active)
                        {
                            hook.SwitchPulled();
                            Plugin.Log.LogInfo($"KillNet: another player pulled {hook.name}: pulled here too");
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't do {hook.name}'s hook point as another player did ({e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        // ---- lifts ----

        private static void LiftBefore(Elevator __instance, out int __state) => __state = __instance != null ? __instance.targetStop : -1;

        private static void LiftAfter(Elevator __instance, int target, int __state)
        {
            if (applying || __instance == null || __state == target || __instance.targetStop != target || !Room.Sharing || ReplayPlayer.Active)
            {
                return;
            }
            uint id = EnemySync.PathId(__instance.transform);
            KillNet.Send(Broke, KillNet.ToRoom, w =>
            {
                w.Write(KindLift);
                w.Write(id);
                w.Write(target);
            });
        }

        private static void HandleLift(uint id, BinaryReader r)
        {
            int target = r.ReadInt32();
            Elevator lift = Lookup(KindLift, id) as Elevator;
            if (lift == null || lift.stops == null || target < 0 || target >= lift.stops.Length || lift.targetStop == target)
            {
                return;
            }
            applying = true;
            try
            {
                lift.MoveToFloor(target);
                Plugin.Log.LogInfo($"KillNet: another player sent {lift.name} to floor {target}: here too");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't move {lift.name} as another player did ({e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        // ---- arenas' waves ----
        // An arena's wave done in one player's ULTRAKILL (its enemies all dead there: the next wave, or
        // its doors open) is done in everyone's: whoever kills the last of them, the fight goes on for
        // all (an enemy that didn't die here, or was never woken here, doesn't hold anyone up).

        private static readonly HashSet<ActivateNextWave> wavesFromOthers = new HashSet<ActivateNextWave>();

        private static void WaveBefore(ActivateNextWave __instance, out bool __state) => __state = __instance != null && __instance.activated;

        private static void WaveAfter(ActivateNextWave __instance, bool __state)
        {
            try
            {
                if (__state || __instance == null || !__instance.activated || wavesFromOthers.Contains(__instance) || !Room.Sharing || ReplayPlayer.Active)
                {
                    return;
                }
                uint id = EnemySync.PathId(__instance.transform);
                byte index = (byte)Array.IndexOf(__instance.GetComponents<ActivateNextWave>(), __instance);
                KillNet.Send(Broke, KillNet.ToRoom, w =>
                {
                    w.Write(KindWave);
                    w.Write(id);
                    w.Write(index);
                });
            }
            catch
            {
            }
        }

        private static void HandleWave(uint id, BinaryReader r)
        {
            byte index = r.ReadByte();
            foreach (ActivateNextWave anw in Resources.FindObjectsOfTypeAll<ActivateNextWave>())
            {
                if (anw == null || !anw.gameObject.scene.IsValid() || EnemySync.PathId(anw.transform) != id)
                {
                    continue;
                }
                ActivateNextWave[] all = anw.GetComponents<ActivateNextWave>();
                if (Array.IndexOf(all, anw) != index)
                {
                    continue;
                }
                if (!anw.activated)
                {
                    wavesFromOthers.Add(anw);
                    anw.deadEnemies = Mathf.Max(anw.deadEnemies, anw.enemyCount);
                    Plugin.Log.LogInfo($"KillNet: another player cleared {anw.name}'s wave: cleared here too");
                }
                return;
            }
        }

        // ---- skulls ----

        private static uint ItemId(ItemIdentifier item)
        {
            if (!itemIds.TryGetValue(item, out uint id))
            {
                id = EnemySync.PathId(item.transform);
                itemIds[item] = id;
            }
            return id;
        }

        private static void PlaceBefore(Punch __instance, out ItemIdentifier __state) => __state = __instance != null ? __instance.heldItem : null;

        private static void PlaceAfter(Transform target, ItemIdentifier __state)
        {
            if (applying || !Room.Sharing || __state == null || target == null || target.GetComponent<ItemPlaceZone>() == null)
            {
                return;
            }
            uint zone = EnemySync.PathId(target), item = ItemId(__state);
            int type = (int)__state.itemType;
            KillNet.Send(Broke, KillNet.ToRoom, w =>
            {
                w.Write(KindPlaced);
                w.Write(zone);
                w.Write(item);
                w.Write(type);
            });
        }

        // (Its place noted before it's in this player's hand.)
        private static void HoldBefore(ItemIdentifier itid, out bool __state)
        {
            __state = false;
            if (itid == null)
            {
                return;
            }
            ItemId(itid);
            __state = itid.GetComponentInParent<ItemPlaceZone>() != null;
        }

        private static void HoldAfter(ItemIdentifier itid, bool __state)
        {
            if (applying || !Room.Sharing || !__state || itid == null)
            {
                return;
            }
            uint item = ItemId(itid);
            int type = (int)itid.itemType;
            KillNet.Send(Broke, KillNet.ToRoom, w =>
            {
                w.Write(KindTaken);
                w.Write(0u);
                w.Write(item);
                w.Write(type);
            });
        }

        private static ItemPlaceZone Zone(uint id)
        {
            if (zones.TryGetValue(id, out ItemPlaceZone z) && z != null)
            {
                return z;
            }
            foreach (ItemPlaceZone p in Resources.FindObjectsOfTypeAll<ItemPlaceZone>())
            {
                if (p != null && p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded)
                {
                    zones[EnemySync.PathId(p.transform)] = p;
                }
            }
            return zones.TryGetValue(id, out z) ? z : null;
        }

        // This ULTRAKILL's copy of a skull another player has: the same one, else one of its colour
        // that's lying about (not in this player's hand, not on an altar).
        private static ItemIdentifier Item(uint id, int type, Punch punch)
        {
            ItemIdentifier spare = null;
            foreach (ItemIdentifier it in Resources.FindObjectsOfTypeAll<ItemIdentifier>())
            {
                if (it == null || !it.gameObject.scene.IsValid() || !it.gameObject.scene.isLoaded || (int)it.itemType != type)
                {
                    continue;
                }
                bool held = punch != null && punch.heldItem == it;
                if (!held && !itemIds.ContainsKey(it) && !it.pickedUp)
                {
                    ItemId(it);
                }
                if (itemIds.TryGetValue(it, out uint itsId) && itsId == id)
                {
                    return it;
                }
                if (spare == null && !held && !it.infiniteSource && it.GetComponentInParent<ItemPlaceZone>() == null)
                {
                    spare = it;
                }
            }
            return spare;
        }

        private static void HandleItem(byte kind, uint zoneId, BinaryReader r)
        {
            uint itemId = r.ReadUInt32();
            int type = r.ReadInt32();
            var fists = MonoSingleton.GetInstance(typeof(FistControl)) as FistControl;
            Punch punch = fists != null ? fists.currentPunch : null;
            ItemIdentifier item = Item(itemId, type, punch);
            if (item == null)
            {
                Plugin.Log.LogInfo($"KillNet: another player {(kind == KindPlaced ? "put a skull on an altar" : "took a skull")}, and there's no such skull here");
                return;
            }
            applying = true;
            try
            {
                ItemPlaceZone[] were = item.GetComponentsInParent<ItemPlaceZone>(true);
                if (kind == KindTaken)
                {
                    if (were.Length == 0 || (punch != null && punch.heldItem == item))
                    {
                        return;
                    }
                    // (In their hand now: not here.)
                    item.transform.SetParent(null, true);
                    item.gameObject.SetActive(false);
                    foreach (ItemPlaceZone z in were)
                    {
                        z.CheckItem();
                    }
                    Plugin.Log.LogInfo($"KillNet: another player took {item.name} off its altar");
                    return;
                }
                ItemPlaceZone zone = Zone(zoneId);
                if (zone == null)
                {
                    return;
                }
                Transform target = zone.transform;
                if (item.transform.parent == target)
                {
                    return;
                }
                ItemPlaceZone[] onTarget = target.GetComponents<ItemPlaceZone>();
                if (punch != null && punch.heldItem == item)
                {
                    // (This player had it: put down there, as they would have.)
                    punch.PlaceHeldObject(onTarget, target);
                }
                else
                {
                    item.gameObject.SetActive(true);
                    item.transform.SetParent(target);
                    item.pickedUp = false;
                    if (item.reverseTransformSettings)
                    {
                        item.transform.localPosition = Vector3.zero;
                        item.transform.localScale = Vector3.one;
                        item.transform.localRotation = Quaternion.identity;
                    }
                    else
                    {
                        item.transform.localPosition = item.putDownPosition;
                        item.transform.localScale = item.putDownScale;
                        item.transform.localRotation = Quaternion.Euler(item.putDownRotation);
                    }
                    foreach (Transform t in item.GetComponentsInChildren<Transform>(true))
                    {
                        t.gameObject.layer = 22;
                    }
                    foreach (Rigidbody body in item.GetComponentsInChildren<Rigidbody>(true))
                    {
                        body.isKinematic = true;
                    }
                    foreach (Collider c in item.GetComponentsInChildren<Collider>(true))
                    {
                        c.enabled = true;
                    }
                    item.SendMessage("PutDown", SendMessageOptions.DontRequireReceiver);
                    foreach (ItemPlaceZone z in were)
                    {
                        if (z != null && z.transform != target)
                        {
                            z.CheckItem();
                        }
                    }
                    foreach (ItemPlaceZone z in onTarget)
                    {
                        z.CheckItem();
                    }
                }
                Plugin.Log.LogInfo($"KillNet: another player put {item.name} on {zone.name}: here too");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't move a skull as another player did ({e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        private static void Hook(Harmony harmony, Type type, string method, string prefix, string postfix, Type[] args = null)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, method, args), new HarmonyMethod(typeof(WorldSync), prefix), new HarmonyMethod(typeof(WorldSync), postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's {type.Name}.{method} ({e.Message}); it won't break for other players");
            }
        }

        private static void GlassBefore(Glass __instance, out bool __state) => __state = __instance != null && !__instance.broken;

        private static void GlassAfter(Glass __instance, bool __state)
        {
            if (__state && __instance.broken)
            {
                Tell(KindGlass, __instance);
            }
        }

        private static void BreakableBefore(Breakable __instance, out bool __state) => __state = __instance != null && !__instance.broken;

        private static void BreakableAfter(Breakable __instance, bool __state)
        {
            if (__state && __instance.broken)
            {
                Tell(KindBreakable, __instance);
            }
        }

        private static void Tell(byte kind, Component thing)
        {
            if (applying || !Room.Sharing)
            {
                return;
            }
            uint id = EnemySync.PathId(thing.transform);
            KillNet.Send(Broke, KillNet.ToRoom, w =>
            {
                w.Write(kind);
                w.Write(id);
            });
        }

        public static void Handle(BinaryReader r)
        {
            byte kind = r.ReadByte();
            uint id = r.ReadUInt32();
            if (kind == KindWave)
            {
                HandleWave(id, r);
                return;
            }
            if (kind == KindPlaced || kind == KindTaken)
            {
                HandleItem(kind, id, r);
                return;
            }
            if (kind == KindHook)
            {
                HandleHook(id, r);
                return;
            }
            if (kind == KindLift)
            {
                HandleLift(id, r);
                return;
            }
            Component thing = Lookup(kind, id);
            if (thing == null)
            {
                return;
            }
            applying = true;
            try
            {
                if (thing is Glass glass)
                {
                    glass.Shatter();
                }
                else if (thing is Breakable breakable)
                {
                    breakable.Break();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't break {thing.name} as another player did ({e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        private static Component Lookup(byte kind, uint id)
        {
            if (things.TryGetValue(id, out Component c) && c != null)
            {
                return c;
            }
            if (Time.unscaledTime - rescanAt < 5f)
            {
                return null;
            }
            rescanAt = Time.unscaledTime;
            foreach (Glass g in Resources.FindObjectsOfTypeAll<Glass>())
            {
                if (g != null && g.gameObject.scene.IsValid() && g.gameObject.scene.isLoaded)
                {
                    things[EnemySync.PathId(g.transform)] = g;
                }
            }
            foreach (Breakable b in Resources.FindObjectsOfTypeAll<Breakable>())
            {
                if (b != null && b.gameObject.scene.IsValid() && b.gameObject.scene.isLoaded)
                {
                    things[EnemySync.PathId(b.transform)] = b;
                }
            }
            foreach (HookPoint h in Resources.FindObjectsOfTypeAll<HookPoint>())
            {
                if (h != null && h.gameObject.scene.IsValid() && h.gameObject.scene.isLoaded)
                {
                    things[EnemySync.PathId(h.transform)] = h;
                }
            }
            foreach (Elevator e in Resources.FindObjectsOfTypeAll<Elevator>())
            {
                if (e != null && e.gameObject.scene.IsValid() && e.gameObject.scene.isLoaded)
                {
                    things[EnemySync.PathId(e.transform)] = e;
                }
            }
            return things.TryGetValue(id, out c) ? c : null;
        }

        // Each frame: doors near the other players.
        public static void Frame(List<Vector3> others)
        {
            // (Alone: nothing to do; its searches of the level were stutters for nothing.)
            if (Room.Shared && others.Count > 0)
            {
                ArenaDoors(others);
            }
            if (!Room.Shared || others.Count == 0)
            {
                if (openedForOthers.Count > 0)
                {
                    foreach (DoorController d in openedForOthers)
                    {
                        Release(d);
                    }
                    openedForOthers.Clear();
                }
                return;
            }
            if (Time.unscaledTime - doorsAt > 4f)
            {
                doorsAt = Time.unscaledTime;
                doors = UnityEngine.Object.FindObjectsOfType<DoorController>();
            }
            foreach (DoorController d in doors)
            {
                if (d == null || !d.isActiveAndEnabled || d.type == 2)
                {
                    continue;
                }
                Collider trigger = d.GetComponent<Collider>();
                if (trigger == null)
                {
                    continue;
                }
                Bounds b = trigger.bounds;
                b.Expand(openedForOthers.Contains(d) ? 3f : 1f);
                bool near = false;
                foreach (Vector3 p in others)
                {
                    if (b.Contains(p))
                    {
                        near = true;
                        break;
                    }
                }
                float now = Time.unscaledTime;
                if (near)
                {
                    nearAt[d] = now;
                    openedForOthers.Add(d);
                    // (Each frame: an enemy of its own leaving clears it.)
                    enemyIn(d) = true;
                }
                else if (openedForOthers.Contains(d) && (!nearAt.TryGetValue(d, out float was) || now - was > 1.5f))
                {
                    openedForOthers.Remove(d);
                    nearAt.Remove(d);
                    Release(d);
                }
            }
        }

        // ---- arenas ----
        // A fight's doors lock as the first player walks in, also in the others' games (the fight's
        // enemies woken there): the others were shut out. A locked door of a fight lets a player in
        // from outside (taken through it), never out: another player is in there, or the fight goes
        // on without anyone (a player who died in it is back at their checkpoint, and with others in
        // the level its rooms aren't made again: EnemySync).

        // Each fight's door, and the fight's middle (its trigger's), kept: an arena of doors only
        // goes as it starts.
        private static readonly Dictionary<Door, Vector3> arenaInside = new Dictionary<Door, Vector3>();
        private static float arenasAt = -100f, passedAt = -100f;
        private const int LevelGeometry = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24);

        private static void ArenaDoors(List<Vector3> others)
        {
            if (Time.unscaledTime - arenasAt > 4f)
            {
                arenasAt = Time.unscaledTime;
                foreach (ActivateArena a in UnityEngine.Object.FindObjectsOfType<ActivateArena>(true))
                {
                    Collider t = a != null && a.doors != null ? a.GetComponent<Collider>() : null;
                    if (t == null)
                    {
                        continue;
                    }
                    foreach (Door d in a.doors)
                    {
                        if (d != null && !arenaInside.ContainsKey(d))
                        {
                            arenaInside[d] = t.bounds.center;
                        }
                    }
                }
            }
            if (!Room.Shared || others.Count == 0 || Time.unscaledTime - passedAt < 1f)
            {
                return;
            }
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            if (nm == null || nm.dead || !nm.activated)
            {
                return;
            }
            Vector3 me = nm.transform.position;
            foreach (KeyValuePair<Door, Vector3> kv in arenaInside)
            {
                Door d = kv.Key;
                if (d == null || !d.locked || !d.gameObject.activeInHierarchy)
                {
                    continue;
                }
                // The door itself (its solid parts): its middle, and across it its thinner way.
                bool have = false;
                Bounds door = default;
                foreach (Collider c in d.GetComponentsInChildren<Collider>())
                {
                    if (c != null && c.enabled && !c.isTrigger)
                    {
                        if (!have)
                        {
                            door = c.bounds;
                            have = true;
                        }
                        else
                        {
                            door.Encapsulate(c.bounds);
                        }
                    }
                }
                if (!have)
                {
                    continue;
                }
                Bounds near = door;
                near.Expand(new Vector3(5f, 2f, 5f));
                if (!near.Contains(me))
                {
                    continue;
                }
                Vector3 mid = door.center;
                Vector3 across = door.size.x < door.size.z ? Vector3.right : Vector3.forward;
                float side = Vector3.Dot(kv.Value - mid, across);
                if (Mathf.Abs(side) < 0.5f)
                {
                    continue;
                }
                Vector3 inward = across * Mathf.Sign(side);
                // (Already in there: no way out.)
                if (Vector3.Dot(me - mid, inward) > 0f)
                {
                    continue;
                }
                float half = Mathf.Abs(Vector3.Dot(door.extents, across));
                Vector3 to = new Vector3(mid.x, me.y, mid.z) + inward * (half + 3f);
                if (Physics.CheckSphere(to, 0.9f, LevelGeometry, QueryTriggerInteraction.Ignore))
                {
                    to += inward * 3f;
                }
                passedAt = Time.unscaledTime;
                nm.transform.position = to;
                nm.rb.position = to;
                nm.rb.velocity = Vector3.zero;
                Plugin.Log.LogInfo($"KillNet: let in through {d.name}, locked for a fight going on");
                return;
            }
        }

        // (Closes as for an enemy leaving: unless enemies of its own still hold it.)
        private static void Release(DoorController d)
        {
            if (d != null && doorUsers(d).Count == 0)
            {
                enemyIn(d) = false;
            }
        }
    }
}
