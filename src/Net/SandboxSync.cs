using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Sandbox;
using Sandbox.Arm;
using UnityEngine;

namespace Killcraft
{
    // The sandbox's spawned things (the spawner arm's enemies, props, blocks; a loaded save), shared by
    // everyone in the level (KillNet). Each is sent as ULTRAKILL's own sandbox saves keep it (SandboxSaver:
    // SavedBlock / SavedProp / SavedEnemy, as JSON), and made again from that in the others' games.
    //  - the room's authority keeps the list: each thing has a number there, the same in every game;
    //  - what another player spawns goes to the authority, which numbers it and tells everyone (the
    //    spawner's own copy is kept, given that number);
    //  - destroyed (the arm, the sandbox's CLEAR) in any game: destroyed in all;
    //  - moved with the arm, frozen or unfrozen, resized or altered: the same in all. Things falling
    //    about with physics on go where the authority's do;
    //  - spawned enemies are numbered by this list for EnemySync (their place in the level is no name:
    //    all of them sit at the top, named alike), so they're the authority's like the level's own.
    internal static class SandboxSync
    {
        public const byte Placed = 45, Place = 46, Removed = 47, Moved = 48, Want = 49, Changed = 50;
        private const byte KindBlock = 1, KindProp = 2, KindEnemy = 3;

        private sealed class Item
        {
            public uint Net;
            public SpawnableInstance Inst;
            public byte Kind;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public bool Frozen, Alive = true;
            public string Data = "";
            public float SentAt = -100f;
            // (Just made or changed from another game's word: what it settles into here is no change of
            // this player's, not sent back.)
            public float QuietUntil;
        }

        private static readonly Dictionary<uint, Item> byNet = new Dictionary<uint, Item>();
        private static readonly Dictionary<int, Item> byInst = new Dictionary<int, Item>();
        // Here when the level started (the level's own, in every game alike): not this list's.
        private static readonly HashSet<int> native = new HashSet<int>();
        // Spawned here, sent to the authority, not numbered yet (by the instance's id).
        private static readonly Dictionary<int, (SpawnableInstance Inst, float At)> pending = new Dictionary<int, (SpawnableInstance, float)>();
        private static readonly Dictionary<string, SpawnableObject> spawnables = new Dictionary<string, SpawnableObject>();
        private static readonly System.Random random = new System.Random();
        private static HashSet<int> lastSeen = new HashSet<int>();
        private static string scene = "";
        private static bool nativesMarked, armSeen;
        private static float scanAt, checkAt, armLookAt, dataAt;
        private static int wantedFrom = int.MinValue;
        private static int dataNext;
        private static SpawnableInstance held;
        private static SandboxArm arm;
        private static float armFindAt = -100f;
        private static EnemyIdentifier heldEnemy;

        private static bool Authority => Room.Shared && Room.IAmAuthority;

        public static void LevelChanged()
        {
            byNet.Clear();
            byInst.Clear();
            native.Clear();
            pending.Clear();
            nativesMarked = false;
            armSeen = false;
            wantedFrom = int.MinValue;
            held = null;
            heldEnemy = null;
            arm = null;
            scene = SceneHelper.CurrentScene ?? "";
        }

        // ---- EnemySync's names for spawned enemies ----

        // A spawned enemy (not one of the level's own): true, and its number here (0 until it has one).
        public static bool EnemyId(EnemyIdentifier eid, out uint id)
        {
            id = 0;
            // (Only where things are spawned: a level's own enemies are named by their place, EnemySync.)
            if (eid == null || (!armSeen && byNet.Count == 0 && pending.Count == 0))
            {
                return false;
            }
            EnemySpawnableInstance inst = eid.GetComponentInParent<EnemySpawnableInstance>();
            if (inst == null)
            {
                inst = eid.GetComponentInChildren<EnemySpawnableInstance>(true);
            }
            if (inst == null || native.Contains(inst.GetInstanceID()))
            {
                return false;
            }
            if (byInst.TryGetValue(inst.GetInstanceID(), out Item item))
            {
                id = item.Net;
            }
            return true;
        }

        // An enemy this player is moving with the arm (the authority's copy follows this one meanwhile).
        public static bool HeldHere(EnemyIdentifier eid) => heldEnemy != null && eid == heldEnemy;

        // ---- each frame ----

        public static void Frame(bool inLevel)
        {
            string now = SceneHelper.CurrentScene ?? "";
            if (now != scene)
            {
                LevelChanged();
            }
            if (!inLevel)
            {
                return;
            }
            if (!nativesMarked)
            {
                nativesMarked = true;
                // (Also those switched off, waiting for their arena.)
                foreach (SpawnableInstance s in Resources.FindObjectsOfTypeAll<SpawnableInstance>())
                {
                    if (s != null && s.gameObject.scene.IsValid() && s.gameObject.scene.isLoaded)
                    {
                        native.Add(s.GetInstanceID());
                    }
                }
            }
            // (Only where things can be spawned: the sandbox, or a level with the spawner arm out.)
            if (!armSeen && Time.unscaledTime - armLookAt > 2f)
            {
                armLookAt = Time.unscaledTime;
                armSeen = scene == "uk_construct" || UnityEngine.Object.FindObjectOfType<SandboxArm>() != null;
            }
            if (!Room.Shared)
            {
                held = null;
                heldEnemy = null;
                return;
            }
            // (Just with these players, or another authority now: everything the authority has.)
            if (!Room.IAmAuthority && wantedFrom != Room.AuthorityId)
            {
                wantedFrom = Room.AuthorityId;
                KillNet.Send(Want, KillNet.ToAuthority, null);
            }
            else if (Room.IAmAuthority)
            {
                wantedFrom = int.MinValue;
            }
            if (!armSeen && byNet.Count == 0)
            {
                return;
            }
            Held();
            if (Time.unscaledTime - scanAt > 0.5f)
            {
                scanAt = Time.unscaledTime;
                Scan();
            }
            if (Time.unscaledTime - checkAt > 0.1f)
            {
                checkAt = Time.unscaledTime;
                Check();
            }
        }

        // What this player's arm is moving now (MoveMode's object).
        private static void Held()
        {
            held = null;
            heldEnemy = null;
            if (!armSeen)
            {
                return;
            }
            // (Looked for now and then, not each frame: searching the whole scene each frame cost
            // milliseconds a frame in the sandbox.)
            if (arm == null && Time.unscaledTime - armFindAt > 0.5f)
            {
                armFindAt = Time.unscaledTime;
                arm = UnityEngine.Object.FindObjectOfType<SandboxArm>();
            }
            if (arm == null)
            {
                return;
            }
            try
            {
                object mode = Traverse.Create(arm).Field("currentMode").GetValue();
                if (mode is MoveMode)
                {
                    object m = Traverse.Create(mode).Field("manipulatedObject").GetValue();
                    if (m != null)
                    {
                        held = Traverse.Create(m).Field("spawnable").GetValue() as SpawnableInstance;
                        if (held is EnemySpawnableInstance e)
                        {
                            heldEnemy = e.eid;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static byte KindOf(SpawnableInstance s)
        {
            if (s is EnemySpawnableInstance)
            {
                return KindEnemy;
            }
            if (s is BrushBlock)
            {
                return KindBlock;
            }
            return s is SandboxProp ? KindProp : (byte)0;
        }

        private static SavedGeneric Save(SpawnableInstance s, byte kind)
        {
            switch (kind)
            {
                case KindBlock: return ((BrushBlock)s).SaveBrushBlock();
                case KindProp: return ((SandboxProp)s).SaveProp();
                case KindEnemy: return ((EnemySpawnableInstance)s).SaveEnemy();
            }
            return null;
        }

        private static string Json(SpawnableInstance s, byte kind)
        {
            try
            {
                SavedGeneric saved = Save(s, kind);
                return saved != null ? JsonConvert.SerializeObject(saved) : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"sandbox: couldn't write {s.name} down ({e.Message})");
                return null;
            }
        }

        private static string DataOf(SpawnableInstance s, byte kind)
        {
            try
            {
                SavedGeneric saved = Save(s, kind);
                if (saved == null)
                {
                    return "";
                }
                string data = saved.Data != null ? JsonConvert.SerializeObject(saved.Data) : "";
                if (saved is SavedEnemy e && e.Radiance != null)
                {
                    data += JsonConvert.SerializeObject(e.Radiance);
                }
                return data;
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static bool Usable(SpawnableInstance s) => s != null && s.enabled && s.sourceObject != null && !string.IsNullOrEmpty(s.sourceObject.identifier);

        private static uint NewNet()
        {
            uint n;
            do
            {
                n = (uint)random.Next(1, int.MaxValue) ^ ((uint)random.Next(0, 2) << 31);
            }
            while (n == 0 || byNet.ContainsKey(n));
            return n;
        }

        private static Item Track(uint net, SpawnableInstance s, byte kind)
        {
            var item = new Item { Net = net, Inst = s, Kind = kind, QuietUntil = Time.unscaledTime + 1.5f };
            Remember(item);
            item.Data = DataOf(s, kind);
            byNet[net] = item;
            byInst[s.GetInstanceID()] = item;
            return item;
        }

        private static void Remember(Item item)
        {
            Transform t = item.Inst.transform;
            item.Pos = t.position;
            item.Rot = t.rotation;
            item.Scale = t.localScale;
            item.Frozen = item.Inst.frozen;
            if (item.Inst is EnemySpawnableInstance e)
            {
                item.Alive = e.eid != null && !e.eid.dead;
            }
        }

        // New things spawned here, and things gone.
        private static void Scan()
        {
            var seen = new HashSet<int>();
            foreach (SpawnableInstance s in UnityEngine.Object.FindObjectsOfType<SpawnableInstance>())
            {
                int key = s.GetInstanceID();
                seen.Add(key);
                if (native.Contains(key) || byInst.ContainsKey(key) || !Usable(s))
                {
                    continue;
                }
                // (Seen in the last look too: not the arm's preview of it, stripped as soon as it's made.)
                if (!lastSeen.Contains(key))
                {
                    continue;
                }
                byte kind = KindOf(s);
                if (kind == 0)
                {
                    continue;
                }
                if (pending.TryGetValue(key, out var p))
                {
                    if (Time.unscaledTime - p.At < 3f)
                    {
                        continue;
                    }
                    // (No answer: asked again; or this player is the authority now, and numbers it.)
                    pending.Remove(key);
                }
                string json = Json(s, kind);
                if (json == null)
                {
                    continue;
                }
                if (Room.IAmAuthority)
                {
                    Item item = Track(NewNet(), s, kind);
                    SendPlaced(item, json, 0, 0, KillNet.ToRoom, 0);
                }
                else
                {
                    pending[key] = (s, Time.unscaledTime);
                    KillNet.Send(Place, KillNet.ToAuthority, w =>
                    {
                        w.Write(key);
                        w.Write(kind);
                        w.Write(json);
                    });
                }
            }
            lastSeen = seen;
            // Gone here: gone for everyone (an enemy only if it went alive: the arm took it, not its death).
            var gone = new List<Item>();
            foreach (Item item in byNet.Values)
            {
                if (item.Inst == null || !seen.Contains(item.Inst.GetInstanceID()))
                {
                    gone.Add(item);
                }
            }
            foreach (Item item in gone)
            {
                Forget(item);
                if (item.Kind != KindEnemy || Room.IAmAuthority || item.Alive)
                {
                    uint net = item.Net;
                    KillNet.Send(Removed, KillNet.ToRoom, w => w.Write(net));
                }
            }
        }

        private static void Forget(Item item)
        {
            byNet.Remove(item.Net);
            var stale = new List<int>();
            foreach (var kv in byInst)
            {
                if (kv.Value == item)
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (int k in stale)
            {
                byInst.Remove(k);
            }
        }

        // What changed here: moved with the arm, frozen, resized, altered; physics (the authority's).
        private static void Check()
        {
            bool checkData = Time.unscaledTime - dataAt > 0.25f;
            if (checkData)
            {
                dataAt = Time.unscaledTime;
            }
            int i = 0, dataEvery = Mathf.Max(1, byNet.Count / 4 + 1);
            foreach (Item item in byNet.Values)
            {
                i++;
                SpawnableInstance s = item.Inst;
                if (s == null)
                {
                    continue;
                }
                if (s is EnemySpawnableInstance e)
                {
                    item.Alive = e.eid != null && !e.eid.dead;
                }
                Transform t = s.transform;
                bool frozenChanged = s.frozen != item.Frozen;
                bool moved = (t.position - item.Pos).sqrMagnitude > 0.0004f || Quaternion.Angle(t.rotation, item.Rot) > 0.5f || (t.localScale - item.Scale).sqrMagnitude > 0.0001f;
                bool mine = s == held;
                if (!mine && Time.unscaledTime < item.QuietUntil)
                {
                    if (moved || frozenChanged)
                    {
                        Remember(item);
                    }
                    if (checkData)
                    {
                        item.Data = DataOf(s, item.Kind);
                    }
                    continue;
                }
                bool send = frozenChanged;
                if (moved && !send)
                {
                    if (mine)
                    {
                        send = !(item.Kind == KindEnemy && Room.IAmAuthority);  // (the authority's enemies: EnemySync)
                    }
                    else if (item.Kind != KindEnemy)
                    {
                        // Frozen: only the arm or the alter menu moves it (this player's); physics: the authority's.
                        send = s.frozen || Room.IAmAuthority;
                    }
                }
                if (send && (frozenChanged || Time.unscaledTime - item.SentAt > (mine ? 0.1f : 0.2f)))
                {
                    item.SentAt = Time.unscaledTime;
                    Remember(item);
                    SendMoved(item);
                }
                else if (moved && item.Kind == KindEnemy)
                {
                    Remember(item);
                }
                // (Altered with the alter menu: now and then, a few each time.)
                if (checkData && (dataNext + i) % dataEvery == 0)
                {
                    string data = DataOf(s, item.Kind);
                    if (data != item.Data)
                    {
                        item.Data = data;
                        string json = Json(s, item.Kind);
                        if (json != null)
                        {
                            uint net = item.Net;
                            KillNet.Send(Changed, KillNet.ToRoom, w =>
                            {
                                w.Write(net);
                                w.Write(json);
                            });
                        }
                    }
                }
            }
            if (checkData)
            {
                dataNext++;
            }
        }

        private static void SendMoved(Item item)
        {
            Rigidbody rb = item.Inst.GetComponent<Rigidbody>();
            Vector3 v = rb != null && !rb.isKinematic ? rb.velocity : Vector3.zero;
            uint net = item.Net;
            Vector3 p = item.Pos, sc = item.Scale;
            Quaternion q = item.Rot;
            bool frozen = item.Frozen;
            KillNet.Send(Moved, KillNet.ToRoom, w =>
            {
                w.Write(net);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                w.Write(sc.x); w.Write(sc.y); w.Write(sc.z);
                w.Write(frozen);
                w.Write(v.x); w.Write(v.y); w.Write(v.z);
            });
        }

        private static void SendPlaced(Item item, string json, int origin, int originKey, byte route, int to)
        {
            uint net = item.Net;
            byte kind = item.Kind;
            KillNet.Send(Placed, route, w =>
            {
                w.Write(net);
                w.Write(origin);
                w.Write(originKey);
                w.Write(kind);
                w.Write(json);
            }, to);
        }

        // ---- made again from what another game sent ----

        private static SpawnableObject Spawnable(string identifier)
        {
            if (spawnables.TryGetValue(identifier, out SpawnableObject so) && so != null)
            {
                return so;
            }
            spawnables.Clear();
            foreach (SpawnableObject o in Resources.FindObjectsOfTypeAll<SpawnableObject>())
            {
                if (o != null && !string.IsNullOrEmpty(o.identifier) && !spawnables.ContainsKey(o.identifier))
                {
                    spawnables[o.identifier] = o;
                }
            }
            return spawnables.TryGetValue(identifier, out so) ? so : null;
        }

        private static SpawnableInstance Make(byte kind, string json)
        {
            SavedGeneric saved;
            switch (kind)
            {
                case KindBlock: saved = JsonConvert.DeserializeObject<SavedBlock>(json); break;
                case KindProp: saved = JsonConvert.DeserializeObject<SavedProp>(json); break;
                case KindEnemy: saved = JsonConvert.DeserializeObject<SavedEnemy>(json); break;
                default: return null;
            }
            if (saved == null || string.IsNullOrEmpty(saved.ObjectIdentifier))
            {
                return null;
            }
            SpawnableObject so = Spawnable(saved.ObjectIdentifier);
            if (so == null)
            {
                Plugin.Log.LogWarning($"sandbox: another player spawned {saved.ObjectIdentifier}, which this ULTRAKILL hasn't got");
                return null;
            }
            saved.Spawnable = so;
            SpawnableInstance s = so.InstantiateSpawnable(saved, null, true);
            if (s == null)
            {
                return null;
            }
            if (saved.Rotation != null)
            {
                s.transform.rotation = saved.Rotation.ToQuaternion();
            }
            if (kind != KindEnemy && s.frozen && s is SandboxProp && MonoSingleton<SandboxNavmesh>.Instance != null)
            {
                MonoSingleton<SandboxNavmesh>.Instance.MarkAsDirty(s);
            }
            return s;
        }

        // ---- messages ----

        public static void Handle(int from, byte type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Placed:
                        HandlePlaced(r);
                        break;
                    case Place:
                        HandlePlace(from, r);
                        break;
                    case Removed:
                        HandleRemoved(r.ReadUInt32());
                        break;
                    case Moved:
                        HandleMoved(r);
                        break;
                    case Want:
                        HandleWant(from);
                        break;
                    case Changed:
                        HandleChanged(r);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"sandbox: couldn't take message {type} from player {from} ({e.Message})");
            }
        }

        private static void HandlePlaced(BinaryReader r)
        {
            uint net = r.ReadUInt32();
            int origin = r.ReadInt32();
            int originKey = r.ReadInt32();
            byte kind = r.ReadByte();
            string json = r.ReadString();
            if (byNet.ContainsKey(net))
            {
                return;
            }
            // Spawned here first: this one is it (now numbered).
            if (origin == Room.MyId && pending.TryGetValue(originKey, out var p))
            {
                pending.Remove(originKey);
                if (p.Inst != null)
                {
                    Track(net, p.Inst, kind);
                    return;
                }
            }
            SpawnableInstance s = Make(kind, json);
            if (s != null)
            {
                Track(net, s, kind);
            }
        }

        // The authority: another player spawned something.
        private static void HandlePlace(int from, BinaryReader r)
        {
            int key = r.ReadInt32();
            byte kind = r.ReadByte();
            string json = r.ReadString();
            if (!Room.IAmAuthority)
            {
                return;
            }
            SpawnableInstance s = Make(kind, json);
            if (s == null)
            {
                return;
            }
            Item item = Track(NewNet(), s, kind);
            SendPlaced(item, json, from, key, KillNet.ToRoom, 0);
        }

        private static void HandleRemoved(uint net)
        {
            if (!byNet.TryGetValue(net, out Item item))
            {
                return;
            }
            Forget(item);
            if (item.Inst != null)
            {
                UnityEngine.Object.Destroy(item.Inst.gameObject);
            }
        }

        private static void HandleMoved(BinaryReader r)
        {
            uint net = r.ReadUInt32();
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var sc = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool frozen = r.ReadBoolean();
            var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!byNet.TryGetValue(net, out Item item) || item.Inst == null || item.Inst == held)
            {
                return;
            }
            item.QuietUntil = Time.unscaledTime + 0.5f;
            SpawnableInstance s = item.Inst;
            if (s.frozen != frozen)
            {
                if (frozen)
                {
                    s.Pause();
                }
                else
                {
                    s.Resume();
                }
            }
            // (Enemies: where the authority's are, EnemySync; but one moved with another player's arm,
            // here at the authority, goes where they put it.)
            if (item.Kind != KindEnemy || Room.IAmAuthority)
            {
                Transform t = s.transform;
                t.SetPositionAndRotation(p, q);
                t.localScale = sc;
                Rigidbody rb = s.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic)
                {
                    rb.position = p;
                    rb.rotation = q;
                    rb.velocity = v;
                }
            }
            Remember(item);
        }

        // The authority: a player just here wants everything spawned.
        private static void HandleWant(int from)
        {
            if (!Room.IAmAuthority)
            {
                return;
            }
            int sent = 0;
            foreach (Item item in byNet.Values)
            {
                if (item.Inst == null || (item.Kind == KindEnemy && !item.Alive))
                {
                    continue;
                }
                string json = Json(item.Inst, item.Kind);
                if (json != null)
                {
                    SendPlaced(item, json, 0, 0, KillNet.ToPlayer, from);
                    sent++;
                }
            }
            if (sent > 0)
            {
                Plugin.Log.LogInfo($"sandbox: sent player {from} the {sent} things spawned here");
            }
        }

        private static void HandleChanged(BinaryReader r)
        {
            uint net = r.ReadUInt32();
            string json = r.ReadString();
            if (!byNet.TryGetValue(net, out Item item) || item.Inst == null)
            {
                return;
            }
            if (item.Kind == KindEnemy)
            {
                // (Its radiance: the rest is EnemySync's.)
                SavedEnemy saved = JsonConvert.DeserializeObject<SavedEnemy>(json);
                if (saved != null && saved.Radiance != null && item.Inst is EnemySpawnableInstance e)
                {
                    try
                    {
                        e.RestoreRadiance(saved.Radiance);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogInfo($"sandbox: {e.name}'s radiance not changed here ({ex.Message})");
                    }
                }
                item.Data = DataOf(item.Inst, item.Kind);
                item.QuietUntil = Time.unscaledTime + 1.5f;
                return;
            }
            // Props and blocks: made again as it is now (its size, its options).
            SpawnableInstance old = item.Inst;
            SpawnableInstance s = Make(item.Kind, json);
            if (s == null)
            {
                return;
            }
            Forget(item);
            UnityEngine.Object.Destroy(old.gameObject);
            Track(net, s, item.Kind);
        }
    }
}
