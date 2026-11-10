using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Killcraft
{
    // The level's enemies, shared by everyone in it (KillNet). One player's ULTRAKILL, the room's
    // authority, runs them as ever, and 20 times a second says where each is. The others' copies of
    // the same enemies ("puppets": the level is the same, so they are the same objects, named by
    // their place in the level) are put there, so everyone sees the same enemies in the same places:
    //  - a puppet keeps its own ULTRAKILL behaviour otherwise: it attacks this player from where it
    //    is (so each player is fought by the enemies near them);
    //  - this player's hits on a puppet go to the authority (where the enemy takes them), not here;
    //  - a puppet dies when the authority's enemy does, and only then (as ULTRAKILL's InstaKill), so
    //    the level's own waves and doors go on alike for everyone;
    //  - enemies the authority has and this one hasn't woken yet (an arena it isn't in) wake here, and
    //    ones woken here first (this player walked into the arena) wake there too;
    //  - the authority's enemies go after whichever player is nearest (a marker where each other
    //    player is: RemotePlayers.Target);
    //  - checkpoints: with others in the level, dying only sends that player back to their
    //    checkpoint; nobody's rooms are made again (ULTRAKILL alone makes them again, bringing back
    //    the enemies everyone else had killed). Alone, as ever;
    //  - bosses that move themselves about (the Insurrectionist, V2, Gabriel, the Primes...) aren't
    //    put where the authority's is: their bodies fly, swing and lunge by their own hand, and pulled
    //    back each frame they came apart (an arm swinging from nowhere). Each player fights their own,
    //    with the same health (hits go to the authority) and the same death.
    // Alone in the level, or the authority, nothing here changes how ULTRAKILL plays.
    internal static class EnemySync
    {
        public const byte Snapshot = 10, Damage = 11, Died = 12, Woken = 13, Reset = 14;
        private const float SnapshotEvery = 0.05f;

        private struct State
        {
            public Vector3 Pos, Vel;
            public Quaternion Rot;
            public float At;
        }

        private static readonly Dictionary<EnemyIdentifier, uint> ids = new Dictionary<EnemyIdentifier, uint>();
        private static readonly Dictionary<uint, EnemyIdentifier> byId = new Dictionary<uint, EnemyIdentifier>();
        private static readonly Dictionary<uint, State> states = new Dictionary<uint, State>();
        private static readonly Dictionary<uint, Vector3> shown = new Dictionary<uint, Vector3>();
        private static readonly HashSet<uint> deadThere = new HashSet<uint>();
        private static readonly Dictionary<uint, float> wokenSent = new Dictionary<uint, float>();
        private static float snapshotTimer, rescanAt = -100f, heardAt = -100f, quietUntil;
        private static string scene = "";
        // Applying what the authority says (a death): let it through the puppet guards.
        private static bool applying;
        // Checkpoints' kept copies of their rooms (CheckPoint.defaultRooms: switched off, far away): not
        // the rooms played in, so not counted among those as their places are named.
        private static readonly HashSet<GameObject> templates = new HashSet<GameObject>();
        private static float templatesAt = -100f;

        // This ULTRAKILL's enemies are the authority's puppets now.
        public static bool Puppets => Room.Shared && !Room.IAmAuthority;
        private static bool Authority => Room.Shared && Room.IAmAuthority;
        // Alone, recording a replay: where the enemies are kept for it (not sent).
        private static bool Recorder => !Room.Shared && Replay.Recording;

        // A replay playing, recorded alone: its checkpoint restarts make the rooms again (as they
        // were made again then).
        public static bool ReplayResets;

        // A replay's player started again from a checkpoint: what had died there may be back.
        public static void ReplayRestarted()
        {
            deadThere.Clear();
            states.Clear();
            shown.Clear();
        }

        public static void LevelChanged()
        {
            toldSent = toldTaken = toldLocal = 0;
            wokenHere.Clear();
            refusedTold.Clear();
            ids.Clear();
            byId.Clear();
            states.Clear();
            shown.Clear();
            deadThere.Clear();
            dying.Clear();
            wokenSent.Clear();
            rescanAt = -100f;
            templates.Clear();
            templatesAt = -100f;
            scene = SceneHelper.CurrentScene ?? "";
        }

        // A checkpoint made or swapped its kept rooms (as it starts, as it's reached): what's a kept
        // copy changed, and with it the places of what's in the rooms; named again from now.
        private static void CheckpointsChanged()
        {
            ids.Clear();
            byId.Clear();
            templatesAt = -100f;
            rescanAt = -100f;
        }

        private static void RefreshTemplates()
        {
            if (Time.unscaledTime - templatesAt < 1f)
            {
                return;
            }
            templatesAt = Time.unscaledTime;
            templates.Clear();
            foreach (CheckPoint c in Resources.FindObjectsOfTypeAll<CheckPoint>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || !c.gameObject.scene.isLoaded || c.defaultRooms == null)
                {
                    continue;
                }
                foreach (GameObject g in c.defaultRooms)
                {
                    if (g != null)
                    {
                        templates.Add(g);
                    }
                }
            }
        }

        // A name as every player's ULTRAKILL has it: without the "(Clone)"s a checkpoint adds each time it
        // copies a room (how many depends on when each player reached it, or died).
        private static string Clean(string name)
        {
            int at = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return at < 0 ? name : name.Replace("(Clone)", "").TrimEnd();
        }

        // ---- names: an enemy's place in the level, the same in every player's ULTRAKILL ----

        public static uint IdOf(EnemyIdentifier eid)
        {
            if (eid == null)
            {
                return 0;
            }
            if (!ids.TryGetValue(eid, out uint id))
            {
                // (Spawned in the sandbox: its number from SandboxSync, the same in every game; none yet,
                // none here.)
                if (SandboxSync.EnemyId(eid, out uint spawned))
                {
                    if (spawned != 0)
                    {
                        ids[eid] = spawned;
                        byId[spawned] = eid;
                    }
                    return spawned;
                }
                id = Fnv(scene + "/" + PathOf(eid.transform, null));
                // (Not kept while a checkpoint's rooms are being made again: the old ones, still there
                // until the frame ends, would count among the new ones' places.)
                if (Time.unscaledTime >= quietUntil)
                {
                    ids[eid] = id;
                    if (byId.TryGetValue(id, out EnemyIdentifier had) && had != null && had != eid && !had.dead
                        && had.gameObject.activeInHierarchy && !eid.gameObject.activeInHierarchy)
                    {
                        return id;  // (the switched-off one of the two doesn't take it)
                    }
                    byId[id] = eid;
                }
            }
            return id;
        }

        // Anything's place in the level, as a number (WorldSync: glass, breakables).
        public static uint PathId(Transform t) => Fnv(scene + "/" + PathOf(t, null));

        // Each object up to the scene: its name, and which of its parent's children of that name it is.
        private static string PathOf(Transform t, Transform stop)
        {
            RefreshTemplates();
            var parts = new List<string>();
            for (; t != null && t != stop; t = t.parent)
            {
                int same = 0;
                string name = Clean(t.name);
                bool kept = templates.Count > 0 && templates.Contains(t.gameObject);
                Transform parent = t.parent;
                if (parent != null)
                {
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform c = parent.GetChild(i);
                        if (c == t)
                        {
                            break;
                        }
                        if (Clean(c.name) == name && (templates.Count > 0 && templates.Contains(c.gameObject)) == kept)
                        {
                            same++;
                        }
                    }
                }
                parts.Add((kept ? name + "~kept" : name) + "#" + same);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static Transform Find(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return root;
            }
            Transform t = root;
            foreach (string part in path.Split('/'))
            {
                int hash = part.LastIndexOf('#');
                string name = part.Substring(0, hash);
                int same = int.Parse(part.Substring(hash + 1));
                Transform next = null;
                for (int i = 0; i < t.childCount && next == null; i++)
                {
                    Transform c = t.GetChild(i);
                    if (Clean(c.name) == name && same-- == 0)
                    {
                        next = c;
                    }
                }
                if (next == null)
                {
                    return root;
                }
                t = next;
            }
            return t;
        }

        private static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                h = (h ^ b) * 16777619;
            }
            return h == 0 ? 1 : h;
        }

        // An enemy by its id, also one not woken yet (switched off in the level, waiting for its arena).
        private static EnemyIdentifier Lookup(uint id, float every = -1f)
        {
            if (every < 0f)
            {
                every = ReplayPlayer.Active ? 0.5f : 5f;
            }
            if (byId.TryGetValue(id, out EnemyIdentifier eid) && eid != null)
            {
                return eid;
            }
            if (Time.unscaledTime - rescanAt < every)
            {
                return null;
            }
            rescanAt = Time.unscaledTime;
            foreach (EnemyIdentifier e in Resources.FindObjectsOfTypeAll<EnemyIdentifier>())
            {
                if (e != null && e.gameObject.scene.IsValid() && e.gameObject.scene.isLoaded)
                {
                    IdOf(e);
                }
            }
            return byId.TryGetValue(id, out eid) && eid != null ? eid : null;
        }

        // (LevelEvents: an enemy by its name.)
        internal static EnemyIdentifier Find(uint id) => Lookup(id, 0.5f);

        // ---- each frame ----

        // Killed by the authority before this player had found them (just woken, just made): killed
        // as soon as they're found.
        private static readonly Dictionary<uint, float> dying = new Dictionary<uint, float>();
        private static readonly List<uint> dyingDone = new List<uint>();

        // Just after a respawn with the rooms kept: V1 falling far below where it came back (no floor
        // there) goes back again, the checkpoint's rooms made again this time.
        private static void FellOut()
        {
            if (respawnedAt == null || Time.unscaledTime - respawnTime > 6f)
            {
                return;
            }
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null || nm.dead || nm.transform.position.y > respawnPlace.y - 30f)
            {
                return;
            }
            CheckPoint cp = respawnedAt;
            respawnedAt = null;
            Plugin.Log.LogWarning($"KillNet: V1 fell out of the level after coming back at checkpoint {cp.name}; back again, its rooms made again");
            nextRespawnOwn = true;
            try
            {
                cp.OnRespawn();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't respawn again ({e.Message})");
            }
        }

        private static void Dying()
        {
            if (dying.Count == 0)
            {
                return;
            }
            dyingDone.Clear();
            foreach (var kv in dying)
            {
                EnemyIdentifier eid = Lookup(kv.Key, 0.25f);
                if (eid != null && !eid.dead && Puppets)
                {
                    Kill(eid);
                }
                if (eid != null || Time.unscaledTime - kv.Value > 10f)
                {
                    dyingDone.Add(kv.Key);
                }
            }
            foreach (uint id in dyingDone)
            {
                dying.Remove(id);
            }
        }

        // A hit as the replay recorded it: dealt here for real (the enemy flinches, bleeds, is thrown).
        public static void ReplayHit(BinaryReader r)
        {
            applying = true;
            try
            {
                long start = r.BaseStream.Position;
                uint id = r.ReadUInt32();
                r.ReadString();
                r.BaseStream.Position += 8 * 4 + 1;  // (force, where, how hard, crit; its flags)
                // (Newer: what hit it, "ground slam" and the like: ULTRAKILL's gore goes by it.)
                if (r.BaseStream.Length - r.BaseStream.Position > 0)
                {
                    string hitter = r.ReadString();
                    EnemyIdentifier eid = Lookup(id, 0.25f);
                    if (eid != null && hitter.Length > 0)
                    {
                        eid.hitter = hitter;
                    }
                }
                r.BaseStream.Position = start;
                HandleDamage(-1, r);
            }
            finally
            {
                applying = false;
            }
        }

        // Cybergrind: each wave's enemies are new ones in the same places as the last's (named alike):
        // the last wave's dead aren't these.
        public static void NewWave()
        {
            deadThere.Clear();
            dying.Clear();
            states.Clear();
            shown.Clear();
            byId.Clear();
            rescanAt = -100f;
        }

        public static void Frame()
        {
            FellOut();
            if (!Room.Sharing)
            {
                return;
            }
            Dying();
            var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
            List<EnemyIdentifier> current = tracker != null ? tracker.GetCurrentEnemies() : null;
            if (current == null)
            {
                return;
            }
            if (Authority || Recorder)
            {
                if ((snapshotTimer -= Time.unscaledDeltaTime) <= 0f)
                {
                    snapshotTimer = SnapshotEvery;
                    SendSnapshot(current);
                }
                return;
            }
            if (Time.unscaledTime < quietUntil)
            {
                return;  // (rooms just made again: the old ones go at the end of the frame)
            }
            // Ones woken here that the authority hasn't (as far as it said lately): wake them there.
            // Ones the authority has killed, back here (this player started again from a checkpoint):
            // dead again.
            var woken = new List<uint>();
            var again = new List<EnemyIdentifier>();
            foreach (EnemyIdentifier eid in current)
            {
                if (eid == null || eid.dead || Pvp.Is(eid))
                {
                    continue;
                }
                uint id = IdOf(eid);
                if (id == 0)
                {
                    continue;
                }
                if (deadThere.Contains(id))
                {
                    again.Add(eid);
                    continue;
                }
                if (!states.ContainsKey(id) && !deadThere.Contains(id) && Time.unscaledTime - heardAt < 1f
                    && (!wokenSent.TryGetValue(id, out float at) || Time.unscaledTime - at > 3f))
                {
                    wokenSent[id] = Time.unscaledTime;
                    woken.Add(id);
                }
            }
            foreach (EnemyIdentifier eid in again)
            {
                Kill(eid);
            }
            if (woken.Count > 0)
            {
                KillNet.Send(Woken, KillNet.ToAuthority, w =>
                {
                    w.Write(woken.Count);
                    foreach (uint id in woken)
                    {
                        w.Write(id);
                    }
                });
            }
        }

        // After ULTRAKILL has moved its enemies this frame: the puppets where the authority has them.
        public static void LateFrame()
        {
            if (!Puppets)
            {
                return;
            }
            float now = Time.unscaledTime;
            foreach (var kv in states)
            {
                if (!byId.TryGetValue(kv.Key, out EnemyIdentifier eid) || eid == null || eid.dead || !eid.gameObject.activeInHierarchy || MovesItself(eid) || SandboxSync.HeldHere(eid))
                {
                    continue;
                }
                State s = kv.Value;
                if (now - s.At > 1f)
                {
                    continue;  // (not heard of lately: left as it is)
                }
                Vector3 target = s.Pos + s.Vel * Mathf.Min(now - s.At, 0.15f);
                Vector3 at = shown.TryGetValue(kv.Key, out Vector3 was) && (target - was).sqrMagnitude < 100f
                    ? Vector3.Lerp(was, target, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime))
                    : target;
                shown[kv.Key] = at;
                Transform t = eid.transform;
                t.SetPositionAndRotation(at, Quaternion.Slerp(t.rotation, s.Rot, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime)));
                NavMeshAgent agent = eid.GetComponent<NavMeshAgent>();
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    agent.nextPosition = at;
                }
                Rigidbody rb = eid.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic)
                {
                    rb.position = at;
                    rb.velocity = s.Vel;
                }
            }
        }

        // Bosses whose bodies their own attacks throw about (see the top).
        private static bool MovesItself(EnemyIdentifier eid)
        {
            switch (eid.enemyType)
            {
                case EnemyType.Sisyphus:
                case EnemyType.SisyphusPrime:
                case EnemyType.MinosPrime:
                case EnemyType.Gabriel:
                case EnemyType.GabrielSecond:
                case EnemyType.V2:
                case EnemyType.V2Second:
                case EnemyType.Ferryman:
                case EnemyType.Geryon:
                    return true;
                default:
                    return false;
            }
        }

        // ---- the authority: where its enemies are ----

        // (As many as fit in one message, each radiant: 4 + 450 x (44 + 4 + 1 + 16) bytes, under KillNet's 30000.)
        private const int SnapshotMax = 450;

        private static void SendSnapshot(List<EnemyIdentifier> current)
        {
            var list = new List<EnemyIdentifier>();
            foreach (EnemyIdentifier eid in current)
            {
                if (eid != null && !eid.dead && eid.gameObject.activeInHierarchy && !Pvp.Is(eid) && IdOf(eid) != 0)
                {
                    list.Add(eid);
                }
            }
            KillNet.Send(Snapshot, KillNet.ToRoom, w =>
            {
                w.Write(Mathf.Min(list.Count, SnapshotMax));
                for (int i = 0; i < list.Count && i < SnapshotMax; i++)
                {
                    EnemyIdentifier eid = list[i];
                    Transform t = eid.transform;
                    Vector3 v = Vector3.zero;
                    Rigidbody rb = eid.GetComponent<Rigidbody>();
                    NavMeshAgent agent = eid.GetComponent<NavMeshAgent>();
                    if (rb != null && !rb.isKinematic)
                    {
                        v = rb.velocity;
                    }
                    else if (agent != null && agent.enabled)
                    {
                        v = agent.velocity;
                    }
                    w.Write(IdOf(eid));
                    w.Write(t.position.x);
                    w.Write(t.position.y);
                    w.Write(t.position.z);
                    w.Write(t.rotation.x);
                    w.Write(t.rotation.y);
                    w.Write(t.rotation.z);
                    w.Write(t.rotation.w);
                    w.Write(v.x);
                    w.Write(v.y);
                    w.Write(v.z);
                }
                // (Newer: each one's health, in the same order.)
                for (int i = 0; i < list.Count && i < SnapshotMax; i++)
                {
                    w.Write(HealthOf(list[i]));
                }
                // (Newer still: each one's radiance - an Idol's, a sand zone's, a level's - as flags; then
                // for each radiant one its tier and how much each buff does.)
                for (int i = 0; i < list.Count && i < SnapshotMax; i++)
                {
                    w.Write(RadianceOf(list[i]));
                }
                for (int i = 0; i < list.Count && i < SnapshotMax; i++)
                {
                    EnemyIdentifier eid = list[i];
                    if (RadianceOf(eid) != 0)
                    {
                        w.Write(eid.radianceTier);
                        w.Write(eid.speedBuffModifier);
                        w.Write(eid.healthBuffModifier);
                        w.Write(eid.damageBuffModifier);
                    }
                }
            });
        }

        private static byte RadianceOf(EnemyIdentifier eid) => (byte)((eid.speedBuff ? 1 : 0) | (eid.healthBuff ? 2 : 0) | (eid.damageBuff ? 4 : 0));

        // A puppet radiant as the authority's enemy is (its glow, its speed, its hits), changed only when
        // it differs (each change is ULTRAKILL's radiance flash).
        private static void SetRadiance(EnemyIdentifier eid, byte flags, float tier, float speed, float health, float damage)
        {
            bool same = RadianceOf(eid) == flags && (flags == 0 || (Mathf.Abs(eid.radianceTier - tier) < 0.01f
                && Mathf.Abs(eid.speedBuffModifier - speed) < 0.01f && Mathf.Abs(eid.healthBuffModifier - health) < 0.01f
                && Mathf.Abs(eid.damageBuffModifier - damage) < 0.01f));
            if (same)
            {
                return;
            }
            if (flags != 0)
            {
                eid.radianceTier = tier;
                eid.speedBuffModifier = speed;
                eid.healthBuffModifier = health;
                eid.damageBuffModifier = damage;
            }
            eid.speedBuffRequests = (flags & 1) != 0 ? 1 : 0;
            eid.healthBuffRequests = (flags & 2) != 0 ? 1 : 0;
            eid.damageBuffRequests = (flags & 4) != 0 ? 1 : 0;
            try
            {
                eid.UpdateBuffs();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't make {eid.name} radiant as the authority's is ({e.Message})");
            }
        }

        private static float HealthOf(EnemyIdentifier eid)
        {
            Enemy e = eid.GetComponent<Enemy>();
            return e != null ? e.health : eid.health;
        }

        // A puppet's health as the authority's enemy has it (never its death: that's the authority's Died).
        private static void SetHealth(EnemyIdentifier eid, float h)
        {
            h = Mathf.Max(h, 0.01f);
            Enemy e = eid.GetComponent<Enemy>();
            if (e != null)
            {
                if (Mathf.Abs(e.health - h) < 0.01f)
                {
                    return;
                }
                e.health = h;
            }
            eid.health = h;
        }

        // ---- messages ----

        public static void Handle(int from, byte type, BinaryReader r)
        {
            switch (type)
            {
                case Snapshot:
                    if (Puppets && from == Room.AuthorityId)
                    {
                        HandleSnapshot(r);
                    }
                    break;
                case Damage:
                    // (The Minos player's: hits on their Minos, MinosControl.)
                    if (Authority || MinosMode.IAmMinos)
                    {
                        HandleDamage(from, r);
                    }
                    break;
                case Died:
                    if (Puppets)
                    {
                        uint id = r.ReadUInt32();
                        deadThere.Add(id);
                        states.Remove(id);
                        EnemyIdentifier eid = Lookup(id, 0.2f);
                        if (eid != null && !eid.dead)
                        {
                            Kill(eid);
                        }
                        else if (eid == null)
                        {
                            dying[id] = Time.unscaledTime;
                        }
                    }
                    break;
                case Reset:
                    // (An older Killcraft's authority starting again from a checkpoint: rooms aren't made
                    // again for anyone now. See the top.)
                    break;
                case Woken:
                    if (Authority)
                    {
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            uint id = r.ReadUInt32();
                            EnemyIdentifier eid = Lookup(id);
                            if (eid != null && eid.dead)
                            {
                                // Killed here before they came: theirs dies too (its wave goes on there).
                                KillNet.Send(Died, KillNet.ToPlayer, w => w.Write(id), from);
                            }
                            else if (eid != null && !eid.gameObject.activeInHierarchy)
                            {
                                string why = WakeRefused(eid);
                                if (why == null)
                                {
                                    Plugin.Log.LogInfo($"KillNet: player {from} woke {eid.name} first: waking it here too");
                                    Wake(eid);
                                }
                                else if (refusedTold.Add(eid.GetInstanceID()))
                                {
                                    Plugin.Log.LogInfo($"KillNet: player {from} woke {eid.name} first: not woken here ({why})");
                                }
                            }
                        }
                    }
                    break;
            }
        }

        private static void HandleSnapshot(BinaryReader r)
        {
            heardAt = Time.unscaledTime;
            int n = r.ReadInt32();
            var seen = new HashSet<uint>();
            var order = new uint[n];
            for (int i = 0; i < n; i++)
            {
                uint id = r.ReadUInt32();
                order[i] = id;
                var s = new State
                {
                    Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    At = Time.unscaledTime,
                };
                states[id] = s;
                seen.Add(id);
                // Woken there and not here yet (an arena this player isn't in): wake it here.
                EnemyIdentifier eid = Lookup(id);
                if (eid != null && !eid.dead && !eid.gameObject.activeInHierarchy && !deadThere.Contains(id) && !InTemplate(eid))
                {
                    Wake(eid);
                }
            }
            if (r.BaseStream.Length - r.BaseStream.Position >= 4L * n)
            {
                for (int i = 0; i < n; i++)
                {
                    float h = r.ReadSingle();
                    if (byId.TryGetValue(order[i], out EnemyIdentifier e) && e != null && !e.dead)
                    {
                        if (!MinosControl.IsBody(e))
                        {
                            SetHealth(e, h);
                        }
                    }
                }
                if (r.BaseStream.Length - r.BaseStream.Position >= n)
                {
                    byte[] flags = r.ReadBytes(n);
                    for (int i = 0; i < n; i++)
                    {
                        float tier = 1f, speed = 1.5f, health = 1.5f, damage = 1.5f;
                        if (flags[i] != 0)
                        {
                            if (r.BaseStream.Length - r.BaseStream.Position < 16)
                            {
                                break;
                            }
                            tier = r.ReadSingle();
                            speed = r.ReadSingle();
                            health = r.ReadSingle();
                            damage = r.ReadSingle();
                        }
                        if (byId.TryGetValue(order[i], out EnemyIdentifier e) && e != null && !e.dead)
                        {
                            SetRadiance(e, flags[i], tier, speed, health, damage);
                        }
                    }
                }
            }
            var stale = new List<uint>();
            foreach (uint id in states.Keys)
            {
                if (!seen.Contains(id))
                {
                    stale.Add(id);
                }
            }
            foreach (uint id in stale)
            {
                states.Remove(id);
                shown.Remove(id);
            }
        }

        // Woken here before (and switched off again since: the level's own doing), a checkpoint's kept
        // copy of its room, or a boss whose fight is already on: not woken (why), else null.
        private static readonly HashSet<int> wokenHere = new HashSet<int>(), refusedTold = new HashSet<int>();

        private static bool InTemplate(EnemyIdentifier eid)
        {
            RefreshTemplates();
            for (Transform t = eid.transform; t != null; t = t.parent)
            {
                if (templates.Contains(t.gameObject))
                {
                    return true;
                }
            }
            return false;
        }

        private static string WakeRefused(EnemyIdentifier eid)
        {
            if (wokenHere.Contains(eid.GetInstanceID()))
            {
                return "woken once already";
            }
            if (InTemplate(eid))
            {
                return "it's a checkpoint's kept copy";
            }
            if (eid.isBoss || eid.GetComponentInChildren<BossHealthBar>(true) != null)
            {
                var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
                List<EnemyIdentifier> current = tracker != null ? tracker.GetCurrentEnemies() : null;
                if (current != null)
                {
                    foreach (EnemyIdentifier e in current)
                    {
                        if (e != null && e != eid && !e.dead && e.enemyType == eid.enemyType && e.gameObject.activeInHierarchy)
                        {
                            return $"{e.name} is already fighting";
                        }
                    }
                }
            }
            return null;
        }

        // Switched on, with whatever it's in (its arena's wave, switched off until then).
        private static void Wake(EnemyIdentifier eid)
        {
            wokenHere.Add(eid.GetInstanceID());
            var chain = new List<GameObject>();
            for (Transform t = eid.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    chain.Add(t.gameObject);
                }
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                chain[i].SetActive(true);
            }
        }

        // ---- checkpoints ----

        // This player died and starts again from a checkpoint. With others in the level its rooms
        // aren't made again (ULTRAKILL's CheckPoint.ResetRoom would): it's given stand-in rooms to
        // make again instead, empty, so the rest of starting again (the player put back at the
        // checkpoint, health, the camera) goes as ever; then its own rooms are put back, as they were.
        private sealed class KeptRooms
        {
            public CheckPoint Checkpoint;
            public List<GameObject> NewRooms, DefaultRooms;
            public GameObject StandIn, StandInCopy, ToActivate;
            public UnityEngine.Events.UnityEvent OnRestart;
        }

        private static KeptRooms kept;

        // The last respawn: where, when, at which checkpoint; and whether the next is ULTRAKILL's own
        // (its rooms made again for this player), after one that left them out of the level.
        private static CheckPoint respawnedAt;
        private static Vector3 respawnPlace;
        private static float respawnTime = -100f;
        private static bool nextRespawnOwn;

        private static void BeforeRespawn(CheckPoint __instance)
        {
            RestoreRooms();
            if (nextRespawnOwn)
            {
                nextRespawnOwn = false;
                Plugin.Log.LogInfo($"KillNet: back to checkpoint {(__instance != null ? __instance.name : "?")} with its rooms made again (the last respawn left V1 out of the level)");
                return;
            }
            if (!Room.Shared || ReplayResets || __instance == null || __instance.newRooms == null || __instance.newRooms.Count == 0)
            {
                return;
            }
            // (Its rooms switched on: the level switches rooms off behind the players as they go on,
            // and one left behind is where this player comes back to.)
            int woken = 0;
            foreach (GameObject room in __instance.newRooms)
            {
                if (room == null)
                {
                    continue;
                }
                for (Transform t = room.transform; t != null; t = t.parent)
                {
                    if (!t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(true);
                        woken++;
                    }
                }
            }
            if (woken > 0)
            {
                Plugin.Log.LogInfo($"KillNet: switched {woken} of checkpoint {__instance.name}'s rooms back on to come back to");
            }
            var standIn = new GameObject("Killcraft: rooms kept");
            var copy = new GameObject("Killcraft: rooms kept (copy)");
            copy.SetActive(false);
            kept = new KeptRooms
            {
                Checkpoint = __instance,
                NewRooms = __instance.newRooms,
                DefaultRooms = __instance.defaultRooms,
                StandIn = standIn,
                StandInCopy = copy,
                ToActivate = __instance.toActivate,
                OnRestart = __instance.onRestart,
            };
            __instance.newRooms = new List<GameObject> { standIn };
            __instance.defaultRooms = new List<GameObject> { copy };
            // (What the level does as its rooms are made again: theirs aren't.)
            __instance.toActivate = null;
            __instance.onRestart = null;
            Plugin.Log.LogInfo($"KillNet: died with others in the level: back to checkpoint {__instance.name}, its rooms left as they are (for everyone)");
        }

        private static void RestoreRooms()
        {
            KeptRooms k = kept;
            if (k == null)
            {
                return;
            }
            kept = null;
            CheckPoint cp = k.Checkpoint;
            if (cp != null)
            {
                if (cp.newRooms != null)
                {
                    foreach (GameObject g in cp.newRooms)
                    {
                        if (g != null)
                        {
                            UnityEngine.Object.Destroy(g);
                        }
                    }
                }
                cp.newRooms = k.NewRooms;
                cp.defaultRooms = k.DefaultRooms;
                cp.toActivate = k.ToActivate;
                cp.onRestart = k.OnRestart;
            }
            if (k.StandIn != null)
            {
                UnityEngine.Object.Destroy(k.StandIn);
            }
            if (k.StandInCopy != null)
            {
                UnityEngine.Object.Destroy(k.StandInCopy);
            }
        }

        private static void AfterRespawn(CheckPoint __instance)
        {
            RestoreRooms();
            respawnedAt = __instance;
            respawnTime = Time.unscaledTime;
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            respawnPlace = nm != null ? nm.transform.position : Vector3.zero;
            quietUntil = Time.unscaledTime + 0.3f;
            snapshotTimer = 0.3f;
        }

        private static Exception RespawnFinalizer(Exception __exception)
        {
            RestoreRooms();
            return __exception;
        }

        private static void Kill(EnemyIdentifier eid)
        {
            applying = true;
            try
            {
                if (!eid.gameObject.activeInHierarchy)
                {
                    Wake(eid);
                }
                eid.InstaKill();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't kill {eid.name} as the authority did ({e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        // Another player hit one of these enemies: as they did, here (the hit place by its place in the enemy).
        private static void HandleDamage(int from, BinaryReader r)
        {
            uint id = r.ReadUInt32();
            string part = r.ReadString();
            var force = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var point = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float multiplier = r.ReadSingle(), crit = r.ReadSingle();
            byte flags = r.ReadByte();
            int kind = -1;
            Vector3 at = point;
            string hitter = "";
            if (r.BaseStream.Length - r.BaseStream.Position >= 16)
            {
                kind = r.ReadInt32();
                at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (r.BaseStream.Length - r.BaseStream.Position > 0)
                {
                    hitter = r.ReadString();
                }
            }
            EnemyIdentifier eid = Lookup(id, 0.5f);
            // (The Minos player: hits on their Minos, whatever he's called here.)
            if (MinosMode.IAmMinos && kind == (int)EnemyType.MinosPrime && MinosControl.Eid != null)
            {
                eid = MinosControl.Eid;
            }
            bool byPlace = false;
            if ((eid == null || eid.dead) && kind >= 0)
            {
                eid = Nearest(kind, at);
                byPlace = eid != null;
            }
            if (toldTaken < 8)
            {
                toldTaken++;
                Plugin.Log.LogInfo(eid == null
                    ? $"KillNet: player {from} hit enemy {id} ({(kind >= 0 ? ((EnemyType)kind).ToString() : "?")}), which isn't here (or is dead)"
                    : $"KillNet: player {from} hit {eid.name} ({hitter}, x{multiplier:0.##}, health {eid.health:0.#}{(byPlace ? $"; found by where it is, its name {id} isn't here" : "")})");
            }
            if (eid == null || eid.dead)
            {
                return;
            }
            if (!eid.gameObject.activeInHierarchy)
            {
                Wake(eid);
            }
            Transform hit = byPlace ? eid.transform : Find(eid.transform, part);
            try
            {
                if (hitter.Length > 0)
                {
                    eid.hitter = hitter;
                }
                OthersHit = true;
                eid.DeliverDamage(hit.gameObject, force, point, multiplier, (flags & 1) != 0, crit, null, (flags & 2) != 0, (flags & 4) != 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: player {from}'s hit on {eid.name} failed ({e.Message})");
            }
            finally
            {
                OthersHit = false;
            }
        }

        // (Another player's hit being taken here: DamageNumbers leaves it to them.)
        internal static bool OthersHit;

        // The authority's living enemy of this kind nearest to where another player's copy is.
        private static EnemyIdentifier Nearest(int kind, Vector3 at)
        {
            var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
            List<EnemyIdentifier> current = tracker != null ? tracker.GetCurrentEnemies() : null;
            if (current == null)
            {
                return null;
            }
            EnemyIdentifier best = null;
            float bestD = 8f * 8f;
            foreach (EnemyIdentifier e in current)
            {
                if (e == null || e.dead || (int)e.enemyType != kind || Pvp.Is(e))
                {
                    continue;
                }
                float d = (e.transform.position - at).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = e;
                }
            }
            return best;
        }

        // ---- ULTRAKILL's own, on puppets ----

        // Hooked one by one (Plugin): if ULTRAKILL changed one of these, only that part is off.
        public static void Install(Harmony harmony)
        {
            Hook(harmony, nameof(EnemyIdentifier.DeliverDamage), nameof(OnDeliverDamage), null);
            Hook(harmony, nameof(EnemyIdentifier.InstaKill), nameof(OnInstaKill), null);
            Hook(harmony, nameof(EnemyIdentifier.ProcessDeath), nameof(OnProcessDeath), nameof(AfterProcessDeath));
            Hook(harmony, nameof(EnemyIdentifier.UpdateTarget), null, nameof(AfterUpdateTarget));
            try
            {
                harmony.Patch(AccessTools.Method(typeof(CheckPoint), nameof(CheckPoint.OnRespawn)),
                    prefix: new HarmonyMethod(typeof(EnemySync), nameof(BeforeRespawn)),
                    postfix: new HarmonyMethod(typeof(EnemySync), nameof(AfterRespawn)),
                    finalizer: new HarmonyMethod(typeof(EnemySync), nameof(RespawnFinalizer)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's CheckPoint.OnRespawn ({e.Message}); dying brings rooms back with others in the level");
            }
            foreach (string m in new[] { "Start", nameof(CheckPoint.ActivateCheckPoint) })
            {
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(CheckPoint), m), postfix: new HarmonyMethod(typeof(EnemySync), nameof(CheckpointsChanged)));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's CheckPoint.{m} ({e.Message}); enemies may not match after checkpoints");
                }
            }
        }

        private static void Hook(Harmony harmony, string method, string prefix, string postfix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(EnemyIdentifier), method),
                    prefix != null ? new HarmonyMethod(typeof(EnemySync), prefix) : null,
                    postfix != null ? new HarmonyMethod(typeof(EnemySync), postfix) : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's EnemyIdentifier.{method} ({e.Message}); shared enemies won't fully work");
            }
        }

        // A puppet: an enemy the authority has, alive (lately said so). Others here (ones only this
        // ULTRAKILL has: a checkpoint brought them back here, the authority has none of them) are
        // this player's own, hit and killed here as ever.
        private static bool IsPuppet(EnemyIdentifier eid, out uint id)
        {
            id = IdOf(eid);
            return states.TryGetValue(id, out State s) && Time.unscaledTime - s.At < 2f;
        }

        // This player hit a puppet: the hit goes to the authority instead (where the enemy is), with
        // ULTRAKILL's blood here (its healing too) as the hit would have spilled.
        public static void Bleed(EnemyIdentifier eid, GameObject target, float multiplier, float critMultiplier, bool fromExplosion)
        {
            var bsm = MonoSingleton.GetInstance(typeof(BloodsplatterManager)) as BloodsplatterManager;
            float amount = multiplier + multiplier * critMultiplier;
            if (bsm == null || amount <= 0f || eid.hitter == "fire")
            {
                return;
            }
            GameObject gore = bsm.GetGore(amount >= 1f ? GoreType.Limb : GoreType.Small, eid, fromExplosion);
            if (gore == null)
            {
                return;
            }
            gore.transform.position = (target != null ? target.transform : eid.transform).position;
            GoreZone zone = eid.GetGoreZone();
            if (zone != null && zone.goreZone != null)
            {
                gore.transform.SetParent(zone.goreZone, true);
            }
            Bloodsplatter splatter = gore.GetComponent<Bloodsplatter>();
            if (splatter != null)
            {
                splatter.GetReady();
            }
        }

        private static bool ForwardHit(EnemyIdentifier eid, GameObject target, Vector3 force, Vector3 hitPoint, float multiplier, bool tryForExplode,
            float critMultiplier, bool ignoreTotalDamageTakenMultiplier, bool fromExplosion, int toPlayer = int.MinValue)
        {
            uint id = IdOf(eid);
            if (toPlayer == int.MinValue && deadThere.Contains(id))
            {
                return true;
            }
            string part = target != null && target.transform != eid.transform && target.transform.IsChildOf(eid.transform)
                ? PathOf(target.transform, eid.transform) : "";
            byte flags = (byte)((tryForExplode ? 1 : 0) | (ignoreTotalDamageTakenMultiplier ? 2 : 0) | (fromExplosion ? 4 : 0));
            int kind = (int)eid.enemyType;
            Vector3 at = eid.transform.position;
            string hitter = eid.hitter ?? "";
            if (toldSent < 5)
            {
                toldSent++;
                Plugin.Log.LogInfo($"KillNet: hit {eid.name} ({id}, {hitter}, x{multiplier:0.##}): sent to player {Room.AuthorityId}, who runs it");
            }
            KillNet.Send(Damage, toPlayer != int.MinValue ? KillNet.ToPlayer : KillNet.ToAuthority, w =>
            {
                w.Write(id);
                w.Write(part);
                w.Write(force.x);
                w.Write(force.y);
                w.Write(force.z);
                w.Write(hitPoint.x);
                w.Write(hitPoint.y);
                w.Write(hitPoint.z);
                w.Write(multiplier);
                w.Write(critMultiplier);
                w.Write(flags);
                // (Newer: what it is and where it is here, should its name not find it there; what hit it.)
                w.Write(kind);
                w.Write(at.x);
                w.Write(at.y);
                w.Write(at.z);
                w.Write(hitter);
            }, toPlayer != int.MinValue ? toPlayer : 0);
            return true;
        }

        // (A Minos Prime round's Minos: his player's game says how he is, MinosControl.)
        internal static void KillNow(EnemyIdentifier eid) => Kill(eid);

        internal static void ShowHealth(EnemyIdentifier eid, float h) => SetHealth(eid, h);

        private static int toldSent, toldTaken, toldLocal;

        private static bool OnDeliverDamage(EnemyIdentifier __instance, GameObject target, Vector3 force, Vector3 hitPoint, float multiplier, bool tryForExplode,
            float critMultiplier, bool ignoreTotalDamageTakenMultiplier, bool fromExplosion, GameObject sourceWeapon)
        {
            // (A replay: its enemies are hurt only as it recorded them being hurt: what the guns here
            // fire, as the recorded player's did, is for show.)
            if ((ReplayPlayer.Active || ReplayPlayer.Pending) && !applying)
            {
                return false;
            }
            // Another player (PvP): the hit goes to them.
            if (Pvp.Is(__instance))
            {
                try
                {
                    Pvp.Hit(__instance, target, force, hitPoint, multiplier, critMultiplier, sourceWeapon, fromExplosion);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"PvP: couldn't send a hit ({e.Message})");
                }
                return false;
            }
            if (MinosControl.CopyHitting && !applying)
            {
                return false;
            }
            if (Replay.Recording && __instance != null && !__instance.dead)
            {
                try
                {
                    Replay.EnemyHit(IdOf(__instance), target != null && target.transform != __instance.transform && target.transform.IsChildOf(__instance.transform)
                        ? PathOf(target.transform, __instance.transform) : "", force, hitPoint, multiplier, critMultiplier,
                        (byte)((tryForExplode ? 1 : 0) | (ignoreTotalDamageTakenMultiplier ? 2 : 0) | (fromExplosion ? 4 : 0)), __instance.hitter);
                }
                catch
                {
                }
            }
            // A Minos Prime round's Minos: his player's (hit there), whoever runs the level's enemies.
            if (!applying && MinosControl.IsBody(__instance))
            {
                if (MinosMode.IAmMinos)
                {
                    return true;
                }
                if (__instance.hitter == "fire")
                {
                    return false;  // (his burning here is for show: he burns in his player's game too, LevelEvents)
                }
                if (!__instance.dead)
                {
                    try
                    {
                        ForwardHit(__instance, target, force, hitPoint, multiplier, tryForExplode, critMultiplier, ignoreTotalDamageTakenMultiplier, fromExplosion, MinosMode.MinosId);
                        DamageNumbers.Sent(__instance, target, hitPoint, multiplier, critMultiplier);
                        Bleed(__instance, target, multiplier, critMultiplier, fromExplosion);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"minos: couldn't send a hit on him ({e.Message})");
                    }
                }
                return false;
            }
            if (!Puppets || applying || __instance == null)
            {
                return true;
            }
            if (!IsPuppet(__instance, out uint id))
            {
                if (toldLocal < 5)
                {
                    toldLocal++;
                    Plugin.Log.LogInfo($"KillNet: hit {__instance.name} ({id}) here: the authority hasn't said where it is lately ({states.Count} of its enemies heard of)");
                }
                return !deadThere.Contains(id);  // (one the authority's killed: dies here soon)
            }
            if (__instance.hitter == "fire")
            {
                return false;  // (its burning here is for show: the authority's enemy burns too, LevelEvents)
            }
            try
            {
                ForwardHit(__instance, target, force, hitPoint, multiplier, tryForExplode, critMultiplier, ignoreTotalDamageTakenMultiplier, fromExplosion);
                DamageNumbers.Sent(__instance, target, hitPoint, multiplier, critMultiplier);
                Bleed(__instance, target, multiplier, critMultiplier, fromExplosion);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't send a hit ({e.Message})");
            }
            return false;
        }

        // A puppet only dies when the authority's enemy does (Kill), not of anything here.
        private static bool OnInstaKill(EnemyIdentifier __instance) => MinosControl.IsBody(__instance) ? MinosMode.IAmMinos || applying
            : !Pvp.Is(__instance) && (!Puppets || applying || __instance == null || !IsPuppet(__instance, out _));

        private static bool OnProcessDeath(EnemyIdentifier __instance, out bool __state)
        {
            __state = __instance != null && !__instance.dead;
            if (Pvp.Is(__instance))
            {
                __state = false;
                return false;  // (another player's target never dies)
            }
            if (MinosControl.IsBody(__instance))
            {
                return MinosMode.IAmMinos || applying;  // (Minos dies on his player's game, then everyone's)
            }
            return !Puppets || applying || __instance == null || !IsPuppet(__instance, out _);
        }

        // The authority's enemy died: everyone's dies.
        private static void AfterProcessDeath(EnemyIdentifier __instance, bool __state)
        {
            if (!__state || !(Authority || Recorder) || __instance == null)
            {
                return;
            }
            uint id = IdOf(__instance);
            KillNet.Send(Died, KillNet.ToRoom, w => w.Write(id));
        }

        // The authority's enemies go after the nearest player, this one or another.
        private static void AfterUpdateTarget(EnemyIdentifier __instance)
        {
            // (A Minos Prime round's Minos: whoever his player goes at.)
            if (MinosControl.TargetFor(__instance, out EnemyTarget minosTarget))
            {
                __instance.target = minosTarget;
                return;
            }
            // (Not Stalkers: theirs is the player or an enemy, and another player's marker is neither:
            // they broke on it every frame, and stood still.)
            if (!Authority || __instance == null || __instance.target == null || !__instance.target.isPlayer || __instance.enemyType == EnemyType.Stalker)
            {
                AtMinos(__instance);
                return;
            }
            try
            {
                Vector3 me = __instance.transform.position;
                float best = (__instance.target.position - me).sqrMagnitude * 0.64f;  // (another must be clearly nearer)
                EnemyTarget chosen = null;
                foreach (EnemyTarget other in RemotePlayers.Targets())
                {
                    float d = (other.position - me).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        chosen = other;
                    }
                }
                if (chosen != null)
                {
                    __instance.target = chosen;
                }
            }
            catch
            {
            }
            AtMinos(__instance);
        }

        // This player is Minos (their V1 put away inside him): what was after V1 goes after him, as an
        // enemy after another (its shots, swings and black holes land on him).
        private static void AtMinos(EnemyIdentifier eid)
        {
            if (!MinosMode.IAmMinos || eid == null || eid.target == null || !eid.target.isPlayer || MinosControl.IsBody(eid))
            {
                return;
            }
            EnemyTarget him = MinosControl.SelfTarget;
            if (him != null)
            {
                eid.target = him;
            }
        }
    }
}
