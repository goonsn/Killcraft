using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Killcraft
{
    // ULTRAKILL's enemies in Minecraft's Nether. The Nether is Minecraft's blocks only, so around V1:
    //  - the blocks get ULTRAKILL colliders (merged into big boxes, within SolidSections of V1), which
    //    enemies stand on and their shots hit;
    //  - a navmesh is built over those (in the background, again as V1 moves on), for the walking ones;
    //  - a few enemies are kept around V1: flying ones in the air, walking ones on the navmesh, never
    //    right next to it. They go when V1 leaves the Nether.
    internal static class NetherWorld
    {
        private const int SolidSections = 3;      // sideways, in sections (16 blocks)
        private const int SolidSectionsUp = 2;    // up and down
        private const float SpawnEvery = 3f;      // seconds
        private const float SpawnNear = 12f, SpawnFar = 26f, DespawnBlocks = 64f;  // blocks

        private sealed class SectionSolids
        {
            public ulong[] Bits;
            public GameObject Colliders;
            public bool Dirty;
        }

        private static readonly Dictionary<long, SectionSolids> sections = new Dictionary<long, SectionSolids>();
        private static GameObject root;
        private static bool collidersChanged;
        private static float solidsTimer, navTimer, spawnTimer;

        // ---- solids ---------------------------------------------------------------------------------

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        // A Nether section's solid blocks (bit x + 16 z + 256 y), from Minecraft (WorldRender.OnSolids).
        public static unsafe void Solids(int sx, int sy, int sz, int count, ulong* bits)
        {
            long key = Key(sx, sy, sz);
            if (count == 0)
            {
                if (sections.TryGetValue(key, out SectionSolids gone))
                {
                    if (gone.Colliders != null)
                    {
                        UnityEngine.Object.Destroy(gone.Colliders);
                        collidersChanged = true;
                    }
                    sections.Remove(key);
                }
                return;
            }
            if (!sections.TryGetValue(key, out SectionSolids s))
            {
                sections[key] = s = new SectionSolids { Bits = new ulong[64] };
            }
            // (Minecraft sends a section again whenever anything in it changes, fire and lava too: only
            // a change of its solid blocks needs new colliders and a new navmesh.)
            bool changed = s.Colliders == null;
            for (int i = 0; i < 64; i++)
            {
                changed |= s.Bits[i] != bits[i];
                s.Bits[i] = bits[i];
            }
            s.Dirty |= changed;
        }

        // Minecraft changed dimension (or reconnected): everything goes.
        public static void Clear()
        {
            sections.Clear();
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
            collidersChanged = true;
        }

        private static void UpdateColliders(Vector3 v1)
        {
            Coords.ToMc(v1, out double px, out double py, out double pz);
            int cx = (int)Math.Floor(px / 16), cy = (int)Math.Floor(py / 16), cz = (int)Math.Floor(pz / 16);
            if (root == null)
            {
                root = new GameObject("Killcraft Nether solids");
                root.transform.SetParent(WorldRender.OurRoot, false);
            }
            foreach (var kv in sections)
            {
                SectionSolids s = kv.Value;
                long k = kv.Key;
                int sx = (int)(k >> 42), sy = (int)((k >> 21) & 0x1FFFFF), sz = (int)(k & 0x1FFFFF);
                sx = (sx << 11) >> 11;  // back to signed 21-bit
                sy = (sy << 11) >> 11;
                sz = (sz << 11) >> 11;
                bool near = Math.Abs(sx - cx) <= SolidSections && Math.Abs(sz - cz) <= SolidSections && Math.Abs(sy - cy) <= SolidSectionsUp;
                if (!near)
                {
                    if (s.Colliders != null)
                    {
                        UnityEngine.Object.Destroy(s.Colliders);
                        s.Colliders = null;
                        collidersChanged = true;
                    }
                    continue;
                }
                if (s.Colliders != null && !s.Dirty)
                {
                    continue;
                }
                if (s.Colliders != null)
                {
                    UnityEngine.Object.Destroy(s.Colliders);
                }
                s.Colliders = Build(sx, sy, sz, s.Bits);
                s.Dirty = false;
                collidersChanged = true;
            }
        }

        // The section's solid blocks as few boxes as possible (greedy: along x, then z, then y).
        private static readonly bool[] taken = new bool[4096];

        private static GameObject Build(int sx, int sy, int sz, ulong[] bits)
        {
            bool Solid(int x, int y, int z)
            {
                int bit = x + 16 * z + 256 * y;
                return (bits[bit >> 6] & (1UL << (bit & 63))) != 0 && !taken[bit];
            }
            Array.Clear(taken, 0, taken.Length);
            // (Tagged as floor: ULTRAKILL's enemies only count tagged ground as somewhere to stand.)
            var go = new GameObject($"Nether solids {sx} {sy} {sz}") { layer = 8, tag = "Floor" };
            go.transform.SetParent(root.transform, false);
            go.transform.position = Coords.ToUnity(sx * 16, sy * 16, sz * 16);
            float u = Coords.U;
            for (int y = 0; y < 16; y++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        if (!Solid(x, y, z))
                        {
                            continue;
                        }
                        int w = 1;
                        while (x + w < 16 && Solid(x + w, y, z))
                        {
                            w++;
                        }
                        int d = 1;
                        while (z + d < 16 && Row(x, w, y, z + d))
                        {
                            d++;
                        }
                        int h = 1;
                        while (y + h < 16 && Layer(x, w, y + h, z, d))
                        {
                            h++;
                        }
                        for (int yy = y; yy < y + h; yy++)
                        {
                            for (int zz = z; zz < z + d; zz++)
                            {
                                for (int xx = x; xx < x + w; xx++)
                                {
                                    taken[xx + 16 * zz + 256 * yy] = true;
                                }
                            }
                        }
                        var box = go.AddComponent<BoxCollider>();
                        box.center = new Vector3((x + w * 0.5f) * u, (y + h * 0.5f) * u, -(z + d * 0.5f) * u);
                        box.size = new Vector3(w * u, h * u, d * u);
                        x += w - 1;
                    }
                }
            }
            return go;

            bool Row(int x0, int w, int y, int z)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    if (!Solid(x, y, z))
                    {
                        return false;
                    }
                }
                return true;
            }

            bool Layer(int x0, int w, int y, int z0, int d)
            {
                for (int z = z0; z < z0 + d; z++)
                {
                    if (!Row(x0, w, y, z))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        // ---- navmesh --------------------------------------------------------------------------------

        private sealed class AgentMesh
        {
            public int AgentType;
            public NavMeshData Data;
            public NavMeshDataInstance Instance;
        }

        private static readonly List<AgentMesh> meshes = new List<AgentMesh>();
        private static readonly List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        private static readonly List<NavMeshBuildMarkup> noMarkups = new List<NavMeshBuildMarkup>();
        private static AsyncOperation building;
        private static int buildIndex;
        private static Vector3 builtAround;
        private static bool haveBuilt;
        private static float lastBuildAt = -100f;
        private const float RebuildEvery = 4f;  // seconds

        private static void UpdateNavmesh(Vector3 v1)
        {
            if (building != null && !building.isDone)
            {
                return;
            }
            building = null;
            float u = Coords.U;
            bool moved = !haveBuilt || (v1 - builtAround).sqrMagnitude > 12f * u * 12f * u;
            // (Blocks broken or placed: not more often than every few seconds. Gathering the colliders
            // for a build is the costly part, on the main thread.)
            bool changedNow = collidersChanged && Time.time - lastBuildAt > RebuildEvery;
            if (buildIndex == 0 && !moved && !changedNow)
            {
                return;
            }
            if (buildIndex == 0)
            {
                lastBuildAt = Time.time;
            }
            if (meshes.Count == 0)
            {
                for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
                {
                    meshes.Add(new AgentMesh { AgentType = NavMesh.GetSettingsByIndex(i).agentTypeID });
                }
            }
            if (meshes.Count == 0)
            {
                return;
            }
            if (buildIndex == 0)
            {
                builtAround = v1;
                collidersChanged = false;
                sources.Clear();
                NavMeshBuilder.CollectSources(Bounds(builtAround), 1 << 8, NavMeshCollectGeometry.PhysicsColliders, 0, noMarkups, sources);
            }
            AgentMesh m = meshes[buildIndex];
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(m.AgentType);
            settings.overrideVoxelSize = true;
            settings.voxelSize = Mathf.Max(0.2f, settings.agentRadius / 2f);
            settings.overrideTileSize = true;
            settings.tileSize = 64;
            // Up one block (Minecraft's own step): the Nether is all one-block steps.
            settings.agentClimb = Mathf.Max(settings.agentClimb, 1.05f * u);
            if (m.Data == null)
            {
                m.Data = new NavMeshData(m.AgentType);
                m.Instance = NavMesh.AddNavMeshData(m.Data);
            }
            building = NavMeshBuilder.UpdateNavMeshDataAsync(m.Data, settings, sources, Bounds(builtAround));
            buildIndex = (buildIndex + 1) % meshes.Count;
            haveBuilt = true;
        }

        private static Bounds Bounds(Vector3 around)
        {
            float u = Coords.U;
            return new Bounds(around, new Vector3(2f * 16f * SolidSections * u, 2f * 16f * SolidSectionsUp * u, 2f * 16f * SolidSections * u));
        }

        private static void ClearNavmesh()
        {
            foreach (AgentMesh m in meshes)
            {
                if (m.Data != null)
                {
                    NavMesh.RemoveNavMeshData(m.Instance);
                    UnityEngine.Object.Destroy(m.Data);
                }
            }
            meshes.Clear();
            building = null;
            buildIndex = 0;
            haveBuilt = false;
        }

        // ---- enemies --------------------------------------------------------------------------------

        // What turns up, and how often: the common husks and machines (no bosses, nor the strong or
        // heavy ones: Virtues, Mindflayers, Cerberi, Swordsmachines, Malicious Faces).
        private static readonly (EnemyType Type, int Weight)[] Kinds =
        {
            (EnemyType.Filth, 35), (EnemyType.Stray, 28), (EnemyType.Schism, 14), (EnemyType.Soldier, 12),
            (EnemyType.Drone, 8), (EnemyType.Streetcleaner, 3),
        };

        // And now and then a mini-boss, one at a time (Plugin.NetherMiniBosses), with a boss bar.
        private static readonly (EnemyType Type, int Weight)[] MiniBosses =
        {
            (EnemyType.Swordsmachine, 5), (EnemyType.Cerberus, 4), (EnemyType.Guttertank, 3), (EnemyType.Gutterman, 3),
            (EnemyType.Mindflayer, 2), (EnemyType.HideousMass, 2), (EnemyType.Ferryman, 2),
        };
        private const float MiniBossEvery = 50f;      // seconds between tries
        private const double MiniBossChance = 0.5;
        private static EnemyIdentifier miniBoss;
        private static float miniBossTimer = MiniBossEvery;

        // One of the Nether's own enemies (Nether leaves those after V1).
        public static bool IsOurs(EnemyIdentifier eid) => spawned.Contains(eid);

        private static SpawnableObjectsDatabase database;
        private static bool databaseMissing, announced;
        private static readonly List<EnemyIdentifier> spawned = new List<EnemyIdentifier>();
        private static readonly System.Random random = new System.Random();

        private static SpawnableObject Pick(EnemyType? only = null)
        {
            if (database == null && !databaseMissing)
            {
                foreach (SpawnableObjectsDatabase db in Resources.FindObjectsOfTypeAll<SpawnableObjectsDatabase>())
                {
                    if (db != null && db.enemies != null && db.enemies.Length > 0)
                    {
                        database = db;
                        break;
                    }
                }
                // (Or through the cheat menu's spawner, which holds it.)
                if (database == null)
                {
                    var field = HarmonyLib.AccessTools.Field(typeof(SpawnMenu), "objects");
                    foreach (SpawnMenu menu in Resources.FindObjectsOfTypeAll<SpawnMenu>())
                    {
                        if (field?.GetValue(menu) is SpawnableObjectsDatabase db && db.enemies != null && db.enemies.Length > 0)
                        {
                            database = db;
                            break;
                        }
                    }
                }
                if (database == null)
                {
                    databaseMissing = true;
                    Plugin.Log.LogWarning("Nether: ULTRAKILL's list of enemies isn't loaded, so no enemies in the Nether");
                }
            }
            if (database == null)
            {
                return null;
            }
            if (!announced)
            {
                announced = true;
                Plugin.Log.LogInfo($"Nether: ULTRAKILL enemies turn up there ({database.enemies.Length} kinds in ULTRAKILL's list, {NavMesh.GetSettingsCount()} navmesh agent types)");
            }
            EnemyType type = only ?? Roll(Kinds);
            foreach (SpawnableObject o in database.enemies)
            {
                if (o != null && o.gameObject != null && o.enemyType == type)
                {
                    return o;
                }
            }
            return null;
        }

        private static EnemyType Roll((EnemyType Type, int Weight)[] kinds)
        {
            int total = 0;
            foreach (var k in kinds)
            {
                total += k.Weight;
            }
            int roll = random.Next(total);
            foreach (var k in kinds)
            {
                if ((roll -= k.Weight) < 0)
                {
                    return k.Type;
                }
            }
            return kinds[0].Type;
        }

        private static EnemyIdentifier Spawn(Vector3 v1, EnemyType? only = null)
        {
            SpawnableObject what = Pick(only);
            if (what == null)
            {
                if (only != null)
                {
                    Plugin.Log.LogInfo($"Nether: no {only} in ULTRAKILL's list of enemies");
                }
                return null;
            }
            float u = Coords.U;
            NavMeshAgent agent = what.gameObject.GetComponentInChildren<NavMeshAgent>(true);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = (float)(random.NextDouble() * Math.PI * 2);
                float dist = (SpawnNear + (float)random.NextDouble() * (SpawnFar - SpawnNear)) * u;
                Vector3 at = v1 + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * dist;
                if (agent != null)
                {
                    // Walking: on the navmesh, about at V1's height.
                    var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
                    if (!NavMesh.SamplePosition(at, out NavMeshHit hit, 6f * u, filter) || Mathf.Abs(hit.position.y - v1.y) > 8f * u)
                    {
                        continue;
                    }
                    at = hit.position;
                }
                else
                {
                    // Flying: open air a little above V1's height.
                    at.y += (3f + (float)random.NextDouble() * 5f) * u;
                    if (Physics.CheckSphere(at, 2f * u, 1 << 8, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                }
                Quaternion facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(v1 - at, Vector3.up).normalized + Vector3.forward * 1e-4f);
                GameObject go = UnityEngine.Object.Instantiate(what.gameObject, at, facing);
                go.SetActive(true);
                EnemyIdentifier eid = go.GetComponentInChildren<EnemyIdentifier>(true);
                if (eid != null)
                {
                    eid.ignorePlayer = false;
                    spawned.Add(eid);
                }
                Plugin.Log.LogInfo($"Nether: a {what.objectName} turns up ({(agent != null ? "walking" : "flying")})");
                return eid;
            }
            return null;
        }

        // A mini-boss now and then, while none is about.
        private static void MiniBoss(Vector3 v1, float dt)
        {
            if (miniBoss != null && !miniBoss.dead)
            {
                return;
            }
            miniBoss = null;
            if (!Plugin.NetherMiniBosses.Value || (miniBossTimer -= dt) > 0f)
            {
                return;
            }
            miniBossTimer = MiniBossEvery;
            if (random.NextDouble() >= MiniBossChance)
            {
                return;
            }
            EnemyIdentifier eid = Spawn(v1, Roll(MiniBosses));
            if (eid != null)
            {
                miniBoss = eid;
                try
                {
                    eid.BossBar(true);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Nether: no boss bar for the {eid.enemyType}: {e.Message}");
                }
            }
        }

        private static void TidyEnemies(Vector3 v1)
        {
            float far = DespawnBlocks * Coords.U;
            for (int i = spawned.Count - 1; i >= 0; i--)
            {
                EnemyIdentifier eid = spawned[i];
                if (eid == null)
                {
                    spawned.RemoveAt(i);
                }
                else if (!eid.dead && (eid.transform.position - v1).sqrMagnitude > far * far)
                {
                    UnityEngine.Object.Destroy(eid.gameObject);
                    spawned.RemoveAt(i);
                }
            }
        }

        private static int Alive()
        {
            int n = 0;
            foreach (EnemyIdentifier eid in spawned)
            {
                if (eid != null && !eid.dead)
                {
                    n++;
                }
            }
            return n;
        }

        // ---- every frame ----------------------------------------------------------------------------

        private static bool wasActive;

        // active: V1 in the Nether with Minecraft driving it (not paused, not dead).
        public static void Frame(bool active, Vector3 v1)
        {
            if (!active)
            {
                // (Paused or loading in the Nether: everything waits. Out of it: everything goes.)
                if (wasActive && !Coords.InNether)
                {
                    Leave();
                }
                return;
            }
            wasActive = true;
            float dt = Time.deltaTime;
            if ((solidsTimer -= dt) <= 0f)
            {
                solidsTimer = 0.5f;
                UpdateColliders(v1);
            }
            if ((navTimer -= dt) <= 0f)
            {
                navTimer = 0.5f;
                UpdateNavmesh(v1);
            }
            int max = Plugin.NetherEnemies.Value;
            if ((spawnTimer -= dt) <= 0f)
            {
                spawnTimer = SpawnEvery;
                TidyEnemies(v1);
                if (Alive() < max)
                {
                    Spawn(v1);
                }
            }
            if (max > 0)
            {
                MiniBoss(v1, dt);
            }
        }

        // V1 left the Nether: its enemies, navmesh and colliders go.
        public static void Leave()
        {
            foreach (EnemyIdentifier eid in spawned)
            {
                if (eid != null)
                {
                    UnityEngine.Object.Destroy(eid.gameObject);
                }
            }
            spawned.Clear();
            ClearNavmesh();
            foreach (SectionSolids s in sections.Values)
            {
                if (s.Colliders != null)
                {
                    UnityEngine.Object.Destroy(s.Colliders);
                    s.Colliders = null;
                }
                s.Dirty = true;
            }
            spawnTimer = SpawnEvery;
            miniBoss = null;
            miniBossTimer = MiniBossEvery;
            wasActive = false;
        }
    }
}
