using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // What the other players shoot, seen here (KillNet): only the look of it (their hits are theirs:
    // EnemySync takes those to the enemies). Each ULTRAKILL tells the others, once a frame:
    //  - its player's beams (revolver, railcannon, coin ricochets): what beam, from where to where;
    //  - the things its player's weapons send flying (shotgun pellets, nails and saws, rockets and
    //    grenades, cannonballs, coins, screws): what, where, how fast, then where they are 10 times a
    //    second, and when they're gone;
    //  - the explosions they make: which, where, how big.
    // Here each is made from ULTRAKILL's own thing of that name (the same weapons are loaded here),
    // made harmless: beams ULTRAKILL's own "fake" ones (drawn, fading, hitting nothing), flying things
    // their looks only (moved here where the shooter's are), explosions harmless ones (seen, heard,
    // felt as a shake, hurting nothing). One this ULTRAKILL hasn't got (a weapon this player has never
    // had) is drawn as a plain line instead.
    internal static class Shots
    {
        public const byte Fired = 20;
        private const byte KindBeam = 0, KindSpawn = 1, KindMove = 2, KindGone = 3, KindBoom = 4;
        private const float MoveEvery = 0.1f;
        private const int PerMessage = 250;

        // ---- made here, for show ----

        private static GameObject maker;
        private static readonly HashSet<int> made = new HashSet<int>();

        // A copy of something switched off (made inside a switched-off holder, so none of its scripts
        // start): for it to be stripped or set up first, then moved out and switched on.
        public static GameObject MakeOff(GameObject prefab, Vector3 at, Quaternion rotation)
        {
            if (maker == null)
            {
                maker = new GameObject("Killcraft (making other players' things)");
                maker.SetActive(false);
            }
            GameObject go = UnityEngine.Object.Instantiate(prefab, at, rotation, maker.transform);
            made.Add(go.GetInstanceID());
            return go;
        }

        // One of those (an explosion or beam of another player's: not this player's to send or to hurt with).
        public static bool IsMade(Component c) => c != null && made.Contains(c.transform.root.gameObject.GetInstanceID());

        public static string PrefabName(GameObject go)
        {
            string n = go.name;
            while (n.EndsWith("(Clone)", StringComparison.Ordinal))
            {
                n = n.Substring(0, n.Length - 7).TrimEnd();
            }
            return n;
        }

        // ULTRAKILL's loaded things by name (its prefabs: weapons' bullets, rockets, explosions...).
        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        private static float scannedAt = -100f;

        private static GameObject Prefab(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (prefabs.TryGetValue(name, out GameObject go) && go != null)
            {
                return go;
            }
            if (Time.unscaledTime - scannedAt < 30f)
            {
                return null;
            }
            scannedAt = Time.unscaledTime;
            foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (g != null && !g.scene.IsValid() && g.transform.parent == null && !prefabs.ContainsKey(g.name))
                {
                    prefabs[g.name] = g;
                }
            }
            return prefabs.TryGetValue(name, out go) ? go : null;
        }

        // ---- this player's ----

        private sealed class Mine
        {
            public Component C;
            public uint Id;
            public Vector3 Last;
        }

        private static readonly Dictionary<int, Mine> mine = new Dictionary<int, Mine>();
        private static readonly List<Action<BinaryWriter>> pending = new List<Action<BinaryWriter>>();
        private static readonly Dictionary<int, float> boomed = new Dictionary<int, float>();
        private static uint nextId = 1;
        private static float moveTimer;

        public static void Install(Harmony harmony)
        {
            Hook(harmony, typeof(Grenade), nameof(GrenadeAfter));
            Hook(harmony, typeof(Cannonball), nameof(CannonballAfter));
            Hook(harmony, typeof(Coin), nameof(CoinAfter));
            Hook(harmony, typeof(Harpoon), nameof(HarpoonAfter));
        }

        private static void Hook(Harmony harmony, Type type, string postfix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(type, "Start"), postfix: new HarmonyMethod(typeof(Shots), postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's {type.Name}.Start ({e.Message}); other players won't see those");
            }
        }

        private static void GrenadeAfter(Grenade __instance)
        {
            if (!__instance.enemy && __instance.sourceWeapon != null)
            {
                Track(__instance);
            }
        }

        private static void CannonballAfter(Cannonball __instance)
        {
            if (__instance.sourceWeapon != null)
            {
                Track(__instance);
            }
        }

        private static void CoinAfter(Coin __instance)
        {
            if (__instance.sourceWeapon != null)
            {
                Track(__instance);
            }
        }

        private static void HarpoonAfter(Harpoon __instance)
        {
            if (__instance.sourceWeapon != null)
            {
                Track(__instance);
            }
        }

        public static void Beam(RevolverBeam beam)
        {
            if (!Room.Sharing || beam == null || IsMade(beam) || beam.sourceWeapon == null)
            {
                return;
            }
            var lr = beam.GetComponent<LineRenderer>();
            if (lr == null || lr.positionCount < 2)
            {
                return;
            }
            Vector3 a = lr.GetPosition(0), b = lr.GetPosition(lr.positionCount - 1);
            if (!lr.useWorldSpace)
            {
                a = lr.transform.TransformPoint(a);
                b = lr.transform.TransformPoint(b);
            }
            string name = PrefabName(beam.gameObject);
            byte rail = (byte)(beam.beamType == BeamType.Railgun ? 1 : 0);
            pending.Add(w =>
            {
                w.Write(KindBeam);
                w.Write(name);
                w.Write(rail);
                Write(w, a);
                Write(w, b);
            });
        }

        // Something this player's weapon sent flying (its first frame).
        public static void Track(Component thing)
        {
            if (!Room.Sharing || thing == null || IsMade(thing))
            {
                return;
            }
            int key = thing.GetInstanceID();
            if (mine.ContainsKey(key))
            {
                return;
            }
            var m = new Mine { C = thing, Id = nextId++, Last = thing.transform.position };
            mine[key] = m;
            Rigidbody rb = thing.GetComponent<Rigidbody>();
            Vector3 v = rb != null && !rb.isKinematic && rb.velocity.sqrMagnitude > 1f ? rb.velocity
                : thing is Projectile p ? thing.transform.forward * Mathf.Max(p.speed, 50f)
                : thing.transform.forward * 50f;
            string name = PrefabName(thing.gameObject);
            Vector3 at = thing.transform.position;
            Quaternion rot = thing.transform.rotation;
            uint id = m.Id;
            pending.Add(w =>
            {
                w.Write(KindSpawn);
                w.Write(id);
                w.Write(name);
                Write(w, at);
                Write(w, rot);
                Write(w, v);
            });
        }

        public static void Explosion(Explosion e)
        {
            if (!Room.Sharing || e == null || IsMade(e) || e.enemy || (e.sourceWeapon == null && !e.playerProjectileForceDirection.HasValue))
            {
                return;
            }
            // (Its own prefab's top: the first copy going up; ULTRAKILL puts some under a room.)
            GameObject root = e.gameObject;
            for (Transform t = e.transform; t != null; t = t.parent)
            {
                if (t.name.EndsWith("(Clone)", StringComparison.Ordinal))
                {
                    root = t.gameObject;
                    break;
                }
            }
            int key = root.GetInstanceID();
            if (boomed.ContainsKey(key))
            {
                return;
            }
            boomed[key] = Time.unscaledTime;
            string name = PrefabName(root);
            Vector3 at = root.transform.position, scale = root.transform.lossyScale;
            Quaternion rot = root.transform.rotation;
            var sizes = new List<Vector2>();
            foreach (Explosion x in root.GetComponentsInChildren<Explosion>(true))
            {
                sizes.Add(new Vector2(x.maxSize, x.speed));
            }
            pending.Add(w =>
            {
                w.Write(KindBoom);
                w.Write(name);
                Write(w, at);
                Write(w, rot);
                Write(w, scale);
                w.Write((byte)Mathf.Min(sizes.Count, 255));
                for (int i = 0; i < sizes.Count && i < 255; i++)
                {
                    w.Write(sizes[i].x);
                    w.Write(sizes[i].y);
                }
            });
        }

        public static void Frame()
        {
            if (boomed.Count > 0)
            {
                var old = new List<int>();
                foreach (var kv in boomed)
                {
                    if (Time.unscaledTime - kv.Value > 10f)
                    {
                        old.Add(kv.Key);
                    }
                }
                foreach (int k in old)
                {
                    boomed.Remove(k);
                }
            }
            // This player's flying things: gone, or where they are now.
            bool move = (moveTimer -= Time.unscaledDeltaTime) <= 0f;
            if (move)
            {
                moveTimer = MoveEvery;
            }
            if (mine.Count > 0)
            {
                var gone = new List<int>();
                foreach (var kv in mine)
                {
                    Mine m = kv.Value;
                    uint id = m.Id;
                    if (m.C == null || !m.C.gameObject.activeInHierarchy || !Room.Sharing)
                    {
                        gone.Add(kv.Key);
                        if (Room.Sharing)
                        {
                            pending.Add(w =>
                            {
                                w.Write(KindGone);
                                w.Write(id);
                            });
                        }
                    }
                    else if (move)
                    {
                        Vector3 at = m.C.transform.position;
                        Vector3 v = (at - m.Last) / MoveEvery;
                        m.Last = at;
                        pending.Add(w =>
                        {
                            w.Write(KindMove);
                            w.Write(id);
                            Write(w, at);
                            Write(w, v);
                        });
                    }
                }
                foreach (int k in gone)
                {
                    mine.Remove(k);
                }
            }
            if (pending.Count > 0)
            {
                if (Room.Sharing)
                {
                    for (int start = 0; start < pending.Count; start += PerMessage)
                    {
                        var part = pending.GetRange(start, Mathf.Min(PerMessage, pending.Count - start));
                        KillNet.Send(Fired, KillNet.ToRoom, w =>
                        {
                            w.Write(part.Count);
                            foreach (Action<BinaryWriter> entry in part)
                            {
                                entry(w);
                            }
                        });
                    }
                }
                pending.Clear();
            }
            Animate();
        }

        private static void Write(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static void Write(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Quaternion ReadRotation(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ---- the others' ----

        private sealed class Flying
        {
            public GameObject Go;
            public LineRenderer Line;  // (a plain line: nothing of that name here)
            public Vector3 Pos, Vel, Shown;
            public float At;
            public bool Face;
            public string Name;
        }

        private static readonly Dictionary<long, Flying> flying = new Dictionary<long, Flying>();

        private sealed class Tracer
        {
            public LineRenderer Line;
            public float Born, Life;
        }

        private static readonly List<Tracer> tracers = new List<Tracer>();
        private static Material material;

        public static void Handle(int from, BinaryReader r)
        {
            // (A replay's watched player whose guns are firing here themselves: not drawn twice.)
            if (ReplayPlayer.RealGuns(from))
            {
                return;
            }
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                byte kind = r.ReadByte();
                switch (kind)
                {
                    case KindBeam:
                    {
                        string name = r.ReadString();
                        byte rail = r.ReadByte();
                        Vector3 a = ReadVector(r), b = ReadVector(r);
                        ShowBeam(name, rail == 1, a, b);
                        RemotePlayers.Fired(from);
                        break;
                    }
                    case KindSpawn:
                    {
                        uint id = r.ReadUInt32();
                        string name = r.ReadString();
                        Vector3 at = ReadVector(r);
                        Quaternion rot = ReadRotation(r);
                        Vector3 v = ReadVector(r);
                        Spawn(Key(from, id), name, at, rot, v);
                        if (name.IndexOf("Coin", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            RemotePlayers.Fired(from);
                        }
                        break;
                    }
                    case KindMove:
                    {
                        uint id = r.ReadUInt32();
                        Vector3 at = ReadVector(r), v = ReadVector(r);
                        if (flying.TryGetValue(Key(from, id), out Flying f))
                        {
                            f.Pos = at;
                            f.Vel = v;
                            f.At = Time.time;
                        }
                        break;
                    }
                    case KindGone:
                    {
                        long key = Key(from, r.ReadUInt32());
                        if (flying.TryGetValue(key, out Flying f))
                        {
                            Remove(f);
                            flying.Remove(key);
                        }
                        break;
                    }
                    case KindBoom:
                    {
                        string name = r.ReadString();
                        Vector3 at = ReadVector(r);
                        Quaternion rot = ReadRotation(r);
                        Vector3 scale = ReadVector(r);
                        int m = r.ReadByte();
                        var sizes = new Vector2[m];
                        for (int j = 0; j < m; j++)
                        {
                            sizes[j] = new Vector2(r.ReadSingle(), r.ReadSingle());
                        }
                        Boom(name, at, rot, scale, sizes);
                        break;
                    }
                    default:
                        return;  // (from a different Killcraft: the rest can't be read)
                }
            }
        }

        private static long Key(int from, uint id) => ((long)from << 32) | id;

        // A shotgun pellet or a nail (not a sawblade: those are drawn as themselves).
        private static bool IsPellet(string name) =>
            name.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0
            || (name.StartsWith("Nail", StringComparison.Ordinal) && !name.StartsWith("NailAlt", StringComparison.Ordinal));

        // Another player's flying things within reach in front (a punch's): taken out of the air here
        // (Pvp: parried), each told by whose it is and its number there. Not coins (a punch hits those on).
        public static void TakeNear(Vector3 eye, Vector3 forward, float reach, List<(int Owner, uint Id, string Name)> got)
        {
            if (flying.Count == 0)
            {
                return;
            }
            var taken = new List<long>();
            foreach (var kv in flying)
            {
                Flying f = kv.Value;
                if (f.Go == null || f.Name == null || f.Name.IndexOf("Coin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                Vector3 to = f.Shown - eye;
                float d = to.magnitude;
                if (d < reach && (d < 1.5f || Vector3.Dot(forward, to / d) > 0.45f))
                {
                    taken.Add(kv.Key);
                }
            }
            foreach (long k in taken)
            {
                got.Add(((int)(k >> 32), (uint)(k & 0xFFFFFFFF), flying[k].Name));
                Remove(flying[k]);
                flying.Remove(k);
            }
        }

        // This player's flying thing, parried by another player: gone (it won't hit them now).
        public static bool Cancel(uint id)
        {
            foreach (var kv in mine)
            {
                if (kv.Value.Id == id && kv.Value.C != null)
                {
                    UnityEngine.Object.Destroy(kv.Value.C.gameObject);
                    return true;
                }
            }
            return false;
        }

        private static void ShowBeam(string name, bool rail, Vector3 a, Vector3 b)
        {
            GameObject prefab = Prefab(name);
            if (prefab != null && prefab.GetComponent<RevolverBeam>() != null && prefab.GetComponent<LineRenderer>() != null)
            {
                try
                {
                    GameObject go = MakeOff(prefab, a, Quaternion.LookRotation(b - a));
                    go.GetComponent<RevolverBeam>().fake = true;
                    var lr = go.GetComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.positionCount = 2;
                    lr.SetPosition(0, a);
                    lr.SetPosition(1, b);
                    go.transform.SetParent(null, true);
                    go.SetActive(true);
                    return;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"KillNet: couldn't show another player's {name} ({e.Message})");
                }
            }
            LineTracer(a, b, rail ? new Color(0.3f, 0.8f, 1f) : new Color(1f, 0.95f, 0.5f), rail ? 0.6f : 0.25f, rail ? 0.5f : 0.15f);
        }

        private static void Spawn(long key, string name, Vector3 at, Quaternion rot, Vector3 v)
        {
            if (flying.Count > 400)
            {
                return;
            }
            var f = new Flying { Pos = at, Vel = v, Shown = at, At = Time.time, Face = name.IndexOf("Coin", StringComparison.OrdinalIgnoreCase) < 0, Name = name };
            // Shotgun pellets and nails: a bright streak (their own model, without the script that sizes
            // and lights it, was a big dark blob, a shadow of each pellet).
            bool streak = IsPellet(name);
            GameObject prefab = streak ? null : Prefab(name);
            if (prefab != null)
            {
                try
                {
                    GameObject go = MakeOff(prefab, at, rot);
                    LooksOnly(go);
                    go.transform.SetParent(null, true);
                    go.SetActive(true);
                    f.Go = go;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"KillNet: couldn't show another player's {name} ({e.Message})");
                }
            }
            if (f.Go == null)
            {
                f.Line = streak ? Line(new Color(1f, 0.9f, 0.55f), name.StartsWith("Nail", StringComparison.Ordinal) ? 0.05f : 0.08f)
                    : Line(new Color(1f, 0.85f, 0.4f), 0.1f);
                if (f.Line == null)
                {
                    return;
                }
                f.Go = f.Line.gameObject;
                f.Line.SetPosition(0, at);
                f.Line.SetPosition(1, at);
            }
            if (flying.TryGetValue(key, out Flying old))
            {
                Remove(old);
            }
            flying[key] = f;
        }

        private static void Boom(string name, Vector3 at, Quaternion rot, Vector3 scale, Vector2[] sizes)
        {
            GameObject prefab = Prefab(name);
            if (prefab == null || prefab.GetComponentInChildren<Explosion>(true) == null)
            {
                return;
            }
            try
            {
                GameObject go = MakeOff(prefab, at, rot);
                go.transform.localScale = scale;
                Explosion[] all = go.GetComponentsInChildren<Explosion>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Explosion x = all[i];
                    if (i < sizes.Length)
                    {
                        x.maxSize = sizes[i].x;
                        x.speed = sizes[i].y;
                    }
                    x.harmless = true;
                    x.damage = 0;
                    x.enemyDamageMultiplier = 0f;
                    x.sourceWeapon = null;
                }
                go.transform.SetParent(null, true);
                go.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't show another player's {name} ({e.Message})");
            }
        }

        // Its looks: models, particles, trails, lights, sounds (no scripts, nothing to hit or be hit).
        private static void LooksOnly(GameObject go)
        {
            foreach (Joint j in go.GetComponentsInChildren<Joint>(true))
            {
                UnityEngine.Object.DestroyImmediate(j);
            }
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (MonoBehaviour c in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (c != null)
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                }
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                UnityEngine.Object.DestroyImmediate(c);
            }
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                UnityEngine.Object.DestroyImmediate(rb);
            }
        }

        private static void Remove(Flying f)
        {
            if (f.Go != null)
            {
                UnityEngine.Object.Destroy(f.Go);
            }
        }

        private static Material Mat()
        {
            if (material == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    return null;
                }
                material = new Material(shader);
            }
            return material;
        }

        private static LineRenderer Line(Color color, float width)
        {
            Material mat = Mat();
            if (mat == null)
            {
                return null;
            }
            var go = new GameObject("Killcraft other player's shot");
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = mat;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 2;
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = width;
            return line;
        }

        private static void LineTracer(Vector3 a, Vector3 b, Color color, float width, float life)
        {
            if (tracers.Count > 300)
            {
                return;
            }
            LineRenderer line = Line(color, width);
            if (line == null)
            {
                return;
            }
            line.SetPosition(0, a);
            line.SetPosition(1, b);
            tracers.Add(new Tracer { Line = line, Born = Time.time, Life = life });
        }

        private static void Animate()
        {
            for (int i = tracers.Count - 1; i >= 0; i--)
            {
                Tracer t = tracers[i];
                float age = Time.time - t.Born;
                if (t.Line == null || age > t.Life)
                {
                    if (t.Line != null)
                    {
                        UnityEngine.Object.Destroy(t.Line.gameObject);
                    }
                    tracers.RemoveAt(i);
                    continue;
                }
                Color c = t.Line.startColor;
                c.a = 1f - age / t.Life;
                t.Line.startColor = t.Line.endColor = c;
            }

            // Their flying things: where the shooter's are, carried on by their speed until told again
            // (a quarter second at most), and gone if not heard of for a while.
            if (flying.Count == 0)
            {
                return;
            }
            var stale = new List<long>();
            float now = Time.time;
            foreach (var kv in flying)
            {
                Flying f = kv.Value;
                if (f.Go == null || now - f.At > 8f)
                {
                    stale.Add(kv.Key);
                    continue;
                }
                Vector3 target = f.Pos + f.Vel * Mathf.Min(now - f.At, 0.25f);
                Vector3 from = f.Shown;
                f.Shown = (target - f.Shown).sqrMagnitude < 30f * 30f ? Vector3.Lerp(f.Shown, target, 1f - Mathf.Exp(-25f * Time.deltaTime)) : target;
                Vector3 step = f.Shown - from;
                if (step.sqrMagnitude > 0.0001f && Physics.Raycast(from, step.normalized, out RaycastHit hit, step.magnitude, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
                {
                    f.Shown = hit.point;
                }
                if (f.Line != null)
                {
                    f.Line.SetPosition(0, Vector3.Lerp(from, f.Shown, -2f));
                    f.Line.SetPosition(1, f.Shown);
                }
                else
                {
                    f.Go.transform.position = f.Shown;
                    if (f.Face && f.Vel.sqrMagnitude > 1f)
                    {
                        f.Go.transform.rotation = Quaternion.LookRotation(f.Vel);
                    }
                    else if (!f.Face)
                    {
                        f.Go.transform.Rotate(Vector3.up, 1440f * Time.deltaTime, Space.Self);  // (a coin, spinning)
                    }
                }
            }
            foreach (long k in stale)
            {
                Remove(flying[k]);
                flying.Remove(k);
            }
        }

        public static void LevelChanged()
        {
            foreach (Tracer t in tracers)
            {
                if (t.Line != null)
                {
                    UnityEngine.Object.Destroy(t.Line.gameObject);
                }
            }
            tracers.Clear();
            foreach (Flying f in flying.Values)
            {
                Remove(f);
            }
            flying.Clear();
            pending.Clear();
            mine.Clear();
            boomed.Clear();
            made.Clear();
            prefabs.Clear();
            scannedAt = -100f;
        }
    }
}
