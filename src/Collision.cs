using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace Killcraft
{
    // Minecraft-space geometry for one region, gathered from ULTRAKILL's colliders on the main thread.
    internal struct Tri
    {
        public float[] V;  // 9 floats, Minecraft coords
        public uint Flags;
    }

    internal struct Obb
    {
        public Vector3 C, A0, A1, A2, Half;
    }

    internal struct Cap
    {
        public Vector3 A, B;
        public float R;
    }

    internal sealed class RegionJob
    {
        public int Rx, Ry, Rz, Epoch;
        public bool Clear;
        public readonly List<Tri> Tris = new List<Tri>();
        public readonly List<Obb> Boxes = new List<Obb>();
        public readonly List<Cap> Caps = new List<Cap>();
    }

    // Streams ULTRAKILL's level geometry around the player to Minecraft as SkyCraft collision regions
    // (8x8x8 blocks): the exact triangles (the Minecraft player's smooth collider) and an 1/8-block
    // voxel version (everything else in Minecraft). Same data the Skyrim plugin sends.
    internal static class Collision
    {
        public const int RegionSize = 8;
        private const int Radius = 5;
        private const int Below = 3;
        private const int Above = 2;
        private const float RefreshNearSeconds = 5.0f;
        private const float ChangeCheckSeconds = 0.1f;
        private const double FrameBudgetMs = 3.0;

        // Environment (6, 7, 8, 24) plus moving platforms (26): what V1's ground check stands on.
        private const int EnvMask = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24) | (1 << 26);

        private static int epoch;
        private static readonly Dictionary<long, float> sentAt = new Dictionary<long, float>();
        private static readonly Dictionary<long, ulong> sentSignature = new Dictionary<long, ulong>();
        private static float changeCheck;
        private static int farCheck;
        private const int FarChecksPerPass = 16;
        private static readonly Collider[] hits = new Collider[2048];
        private static readonly BlockingCollection<RegionJob> queue = new BlockingCollection<RegionJob>();
        private static Thread worker;
        private static (int dx, int dy, int dz)[] order;

        private sealed class MeshTris
        {
            public Vector3[] Verts;
            public int[] Indices;
        }

        private sealed class ColliderTris
        {
            public Matrix4x4 Matrix;
            public int MeshId;
            public float[] Verts;  // Minecraft coords, 9 per triangle
            public readonly Dictionary<long, List<int>> Buckets = new Dictionary<long, List<int>>();
        }

        private static readonly Dictionary<int, MeshTris> meshCache = new Dictionary<int, MeshTris>();
        private static readonly Dictionary<int, ColliderTris> colliderCache = new Dictionary<int, ColliderTris>();
        private static readonly HashSet<int> loggedUnreadable = new HashSet<int>();

        public static int Pending => queue.Count;
        public static int SentRegions => sentAt.Count;

        // Ring position just after the latest CLEAR; -1 while that CLEAR hasn't been written yet.
        private static long clearEnd = 0;

        // Minecraft has dropped the previous collision: a player placed now won't stand on stale ground.
        public static bool ClearConsumed
        {
            get
            {
                long end = Interlocked.Read(ref clearEnd);
                return end >= 0 && Link.CollisionTail() >= end;
            }
        }

        public static void Reset(int newEpoch)
        {
            Interlocked.Exchange(ref clearEnd, -1);
            epoch = newEpoch;
            sentAt.Clear();
            sentSignature.Clear();
            colliderCache.Clear();
            EnsureWorker();
            queue.Add(new RegionJob { Clear = true, Epoch = newEpoch });
        }

        public static void ForgetMeshes()
        {
            meshCache.Clear();
            colliderCache.Clear();
            loggedUnreadable.Clear();
        }

        private static void EnsureWorker()
        {
            if (worker != null)
            {
                return;
            }
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Killcraft collision" };
            worker.Start();
        }

        private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        public static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        private static (int, int, int)[] Order()
        {
            if (order != null)
            {
                return order;
            }
            var list = new List<(int dx, int dy, int dz)>();
            for (int dy = -Below; dy <= Above; dy++)
            {
                for (int dz = -Radius; dz <= Radius; dz++)
                {
                    for (int dx = -Radius; dx <= Radius; dx++)
                    {
                        list.Add((dx, dy, dz));
                    }
                }
            }
            // Ground under the player first: Minecraft holds the player until it has arrived.
            list.Sort((a, c) => Score(a).CompareTo(Score(c)));
            return order = list.ToArray();

            static int Score((int dx, int dy, int dz) o) => (o.dx * o.dx + o.dz * o.dz) * 4 + (o.dy > 0 ? o.dy * 8 : -o.dy * 2);
        }

        public static void Update(double px, double py, double pz)
        {
            if (queue.Count > 48)
            {
                return;  // the worker or Minecraft is behind
            }
            var clock = Stopwatch.StartNew();
            float now = Time.unscaledTime;
            int rx = FloorDiv((int)Math.Floor(px), RegionSize);
            int ry = FloorDiv((int)Math.Floor(py), RegionSize);
            int rz = FloorDiv((int)Math.Floor(pz), RegionSize);
            // Doors opening, platforms moving, walls switched off: regions around the player whose
            // colliders changed since they were sent go again now, not at the next refresh.
            changeCheck -= Time.unscaledDeltaTime;
            if (changeCheck <= 0f)
            {
                changeCheck = ChangeCheckSeconds;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            long key = Key(rx + dx, ry + dy, rz + dz);
                            // At most 4 times a second each, for things that never stop moving.
                            if (sentAt.TryGetValue(key, out float at) && now - at < 0.25f)
                            {
                                continue;
                            }
                            if (sentSignature.TryGetValue(key, out ulong sent) && Signature(rx + dx, ry + dy, rz + dz) != sent)
                            {
                                sentAt.Remove(key);
                                if (Plugin.Diagnostics.Value)
                                {
                                    Plugin.Log.LogInfo($"collision: region {rx + dx},{ry + dy},{rz + dz} changed; sending it again");
                                }
                            }
                        }
                    }
                }
                // The rest of what was sent, a few regions each time round (all of it every few
                // seconds): a door opening or an enemy dying further off would otherwise leave its old
                // collision in Minecraft, and arrows stuck in it hanging in the air.
                var all = Order();
                for (int i = 0; i < FarChecksPerPass; i++)
                {
                    var (dx, dy, dz) = all[farCheck++ % all.Length];
                    long key = Key(rx + dx, ry + dy, rz + dz);
                    if (sentAt.TryGetValue(key, out float at) && now - at >= 0.25f
                        && sentSignature.TryGetValue(key, out ulong sent) && Signature(rx + dx, ry + dy, rz + dz) != sent)
                    {
                        sentAt.Remove(key);
                    }
                }
            }
            foreach (var (dx, dy, dz) in Order())
            {
                long key = Key(rx + dx, ry + dy, rz + dz);
                bool near = Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 && dy >= -1 && dy <= 1;
                if (sentAt.TryGetValue(key, out float at) && !(near && now - at > RefreshNearSeconds))
                {
                    continue;
                }
                RegionJob job;
                try
                {
                    job = Gather(rx + dx, ry + dy, rz + dz);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"collision: gathering region {rx + dx},{ry + dy},{rz + dz} failed: {e}");
                    job = new RegionJob { Rx = rx + dx, Ry = ry + dy, Rz = rz + dz, Epoch = epoch };
                }
                sentAt[key] = now;
                queue.Add(job);
                if (clock.Elapsed.TotalMilliseconds > FrameBudgetMs)
                {
                    break;
                }
            }
        }

        // ---- gathering (main thread) ----------------------------------------------------------

        // ULTRAKILL's colliders overlapping a region (with the margin the voxels need).
        private static int Overlap(int rx, int ry, int rz)
        {
            Vector3 center = Coords.ToUnity(rx * RegionSize + RegionSize * 0.5, ry * RegionSize + RegionSize * 0.5, rz * RegionSize + RegionSize * 0.5);
            Vector3 half = Vector3.one * ((RegionSize * 0.5f + 0.5f) * Coords.U);
            return Physics.OverlapBoxNonAlloc(center, half, hits, Quaternion.identity, EnvMask, QueryTriggerInteraction.Ignore);
        }

        private static bool Counts(Collider c) => c != null && c.enabled && !c.isTrigger && !WorldRender.IsOurs(c);

        // Which colliders a region has and where they are, in one number (order doesn't matter).
        private static ulong Signature(int n)
        {
            ulong sum = 0;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i];
                if (!Counts(c))
                {
                    continue;
                }
                Bounds b = c.bounds;
                ulong h = (ulong)(uint)c.GetInstanceID() * 0x9E3779B97F4A7C15UL;
                h ^= (ulong)Mathf.RoundToInt(b.min.x * 20f) * 0xC2B2AE3D27D4EB4FUL;
                h ^= (ulong)Mathf.RoundToInt(b.min.y * 20f) * 0x165667B19E3779F9UL;
                h ^= (ulong)Mathf.RoundToInt(b.min.z * 20f) * 0xD6E8FEB86659FD93UL;
                h ^= (ulong)Mathf.RoundToInt(b.max.x * 20f) * 0xFF51AFD7ED558CCDUL;
                h ^= (ulong)Mathf.RoundToInt(b.max.y * 20f) * 0xC4CEB9FE1A85EC53UL;
                h ^= (ulong)Mathf.RoundToInt(b.max.z * 20f) * 0x94D049BB133111EBUL;
                h ^= h >> 31;
                sum += h * 0xBF58476D1CE4E5B9UL;
            }
            return sum;
        }

        private static ulong Signature(int rx, int ry, int rz) => Signature(Overlap(rx, ry, rz));

        private static RegionJob Gather(int rx, int ry, int rz)
        {
            var job = new RegionJob { Rx = rx, Ry = ry, Rz = rz, Epoch = epoch };
            int n = Overlap(rx, ry, rz);
            long key = Key(rx, ry, rz);
            sentSignature[key] = Signature(n);
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i];
                if (!Counts(c))
                {
                    continue;  // Minecraft's own blocks (as ULTRAKILL colliders) aren't ULTRAKILL's geometry
                }
                switch (c)
                {
                    case BoxCollider box:
                        AddBox(job, box);
                        break;
                    case SphereCollider sphere:
                        AddSphere(job, sphere);
                        break;
                    case CapsuleCollider capsule:
                        AddCapsule(job, capsule);
                        break;
                    case MeshCollider mesh:
                        AddMesh(job, mesh, key);
                        break;
                    case TerrainCollider terrain:
                        AddTerrain(job, terrain, rx, ry, rz);
                        break;
                }
            }
            return job;
        }

        private static Vector3 PointToMc(Vector3 p)
        {
            Coords.ToMc(p, out double x, out double y, out double z);
            return new Vector3((float)x, (float)y, (float)z);
        }

        private static Vector3 DirToMc(Vector3 d) => new Vector3(d.x, d.y, -d.z);

        private static void AddBox(RegionJob job, BoxCollider box)
        {
            Transform t = box.transform;
            Vector3 s = t.lossyScale;
            float k = 1f / Coords.U;
            job.Boxes.Add(new Obb
            {
                C = PointToMc(t.TransformPoint(box.center)),
                A0 = DirToMc(t.right),
                A1 = DirToMc(t.up),
                A2 = DirToMc(t.forward),
                Half = new Vector3(Mathf.Abs(box.size.x * s.x), Mathf.Abs(box.size.y * s.y), Mathf.Abs(box.size.z * s.z)) * (0.5f * k),
            });
        }

        private static void AddBounds(RegionJob job, Bounds b)
        {
            float k = 1f / Coords.U;
            job.Boxes.Add(new Obb { C = PointToMc(b.center), A0 = Vector3.right, A1 = Vector3.up, A2 = Vector3.forward, Half = b.extents * k });
        }

        private static void AddSphere(RegionJob job, SphereCollider sphere)
        {
            Vector3 s = sphere.transform.lossyScale;
            float r = sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)) / Coords.U;
            Vector3 c = PointToMc(sphere.transform.TransformPoint(sphere.center));
            job.Caps.Add(new Cap { A = c, B = c, R = r });
        }

        private static void AddCapsule(RegionJob job, CapsuleCollider capsule)
        {
            Transform t = capsule.transform;
            Vector3 s = t.lossyScale;
            int dir = capsule.direction;
            Vector3 axisLocal = dir == 0 ? Vector3.right : dir == 1 ? Vector3.up : Vector3.forward;
            float axisScale = Mathf.Abs(s[dir]);
            float radiusScale = Mathf.Max(Mathf.Abs(s[(dir + 1) % 3]), Mathf.Abs(s[(dir + 2) % 3]));
            float r = capsule.radius * radiusScale;
            float seg = Mathf.Max(0f, capsule.height * axisScale * 0.5f - r);
            Vector3 c = t.TransformPoint(capsule.center);
            Vector3 axis = t.TransformDirection(axisLocal).normalized;
            job.Caps.Add(new Cap { A = PointToMc(c + axis * seg), B = PointToMc(c - axis * seg), R = r / Coords.U });
        }

        private static void AddMesh(RegionJob job, MeshCollider collider, long key)
        {
            Mesh mesh = collider.sharedMesh;
            if (mesh == null)
            {
                return;
            }
            ColliderTris ct = GetColliderTris(collider, mesh);
            if (ct == null)
            {
                // No vertex data to read: a box is right for small or convex pieces; a big concave
                // one (a whole room) would wall the player in, so it's left out (and logged).
                Bounds b = collider.bounds;
                if (collider.convex || b.size.magnitude < 6f * Coords.U)
                {
                    AddBounds(job, b);
                }
                else if (loggedUnreadable.Add(mesh.GetInstanceID()))
                {
                    Plugin.Log.LogWarning($"collision: can't read mesh '{mesh.name}' on '{collider.name}' ({b.size}); left out");
                }
                return;
            }
            if (!ct.Buckets.TryGetValue(key, out List<int> list))
            {
                return;
            }
            foreach (int t in list)
            {
                var v = new float[9];
                Array.Copy(ct.Verts, t * 9, v, 0, 9);
                job.Tris.Add(new Tri { V = v });
            }
        }

        private static ColliderTris GetColliderTris(MeshCollider collider, Mesh mesh)
        {
            int id = collider.GetInstanceID();
            Matrix4x4 m = collider.transform.localToWorldMatrix;
            if (colliderCache.TryGetValue(id, out ColliderTris cached) && cached.MeshId == mesh.GetInstanceID() && cached.Matrix == m)
            {
                return cached;
            }
            MeshTris mt = GetMeshTris(mesh);
            if (mt == null)
            {
                colliderCache.Remove(id);
                return null;
            }
            var ct = new ColliderTris { Matrix = m, MeshId = mesh.GetInstanceID() };
            int count = mt.Indices.Length / 3;
            ct.Verts = new float[count * 9];
            var world = new Vector3[mt.Verts.Length];
            for (int i = 0; i < world.Length; i++)
            {
                world[i] = PointToMc(m.MultiplyPoint3x4(mt.Verts[i]));
            }
            for (int t = 0; t < count; t++)
            {
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    // Vertices 0, 2, 1: the Z mirror turns Unity's winding inward; this makes it
                    // outward again, as SkyCraft's triangles are.
                    Vector3 p = world[mt.Indices[t * 3 + (k == 0 ? 0 : 3 - k)]];
                    ct.Verts[t * 9 + k * 3] = p.x;
                    ct.Verts[t * 9 + k * 3 + 1] = p.y;
                    ct.Verts[t * 9 + k * 3 + 2] = p.z;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                }
                if (float.IsNaN(minX) || maxX - minX > 4096 || maxY - minY > 4096 || maxZ - minZ > 4096)
                {
                    continue;
                }
                int x0 = FloorDiv((int)Math.Floor(minX - 0.5f), RegionSize), x1 = FloorDiv((int)Math.Floor(maxX + 0.5f), RegionSize);
                int y0 = FloorDiv((int)Math.Floor(minY - 0.5f), RegionSize), y1 = FloorDiv((int)Math.Floor(maxY + 0.5f), RegionSize);
                int z0 = FloorDiv((int)Math.Floor(minZ - 0.5f), RegionSize), z1 = FloorDiv((int)Math.Floor(maxZ + 0.5f), RegionSize);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = Key(x, y, z);
                            if (!ct.Buckets.TryGetValue(key, out List<int> list))
                            {
                                ct.Buckets[key] = list = new List<int>();
                            }
                            list.Add(t);
                        }
                    }
                }
            }
            colliderCache[id] = ct;
            return ct;
        }

        private static MeshTris GetMeshTris(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (meshCache.TryGetValue(id, out MeshTris cached))
            {
                return cached;
            }
            MeshTris result = null;
            try
            {
                if (mesh.isReadable)
                {
                    var indices = new List<int>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) == MeshTopology.Triangles)
                        {
                            indices.AddRange(mesh.GetTriangles(s));
                        }
                    }
                    result = new MeshTris { Verts = mesh.vertices, Indices = indices.ToArray() };
                }
                else
                {
                    result = ReadFromGpu(mesh);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"collision: reading mesh '{mesh.name}' failed: {e.Message}");
            }
            meshCache[id] = result;
            return result;
        }

        // Non-readable meshes have no CPU copy, but their GPU buffers can be read back.
        private static MeshTris ReadFromGpu(Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
            {
                return null;
            }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            int stride = mesh.GetVertexBufferStride(stream);
            int vertexCount = mesh.vertexCount;
            var raw = new byte[vertexCount * stride];
            using (GraphicsBuffer vb = mesh.GetVertexBuffer(stream))
            {
                if (vb == null)
                {
                    return null;
                }
                vb.GetData(raw);
            }
            var verts = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int o = i * stride + offset;
                verts[i] = new Vector3(BitConverter.ToSingle(raw, o), BitConverter.ToSingle(raw, o + 4), BitConverter.ToSingle(raw, o + 8));
            }
            int[] all;
            using (GraphicsBuffer ib = mesh.GetIndexBuffer())
            {
                if (ib == null)
                {
                    return null;
                }
                all = new int[ib.count];
                if (mesh.indexFormat == IndexFormat.UInt16)
                {
                    var shorts = new ushort[ib.count];
                    ib.GetData(shorts);
                    for (int i = 0; i < shorts.Length; i++)
                    {
                        all[i] = shorts[i];
                    }
                }
                else
                {
                    ib.GetData(all);
                }
            }
            var indices = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                SubMeshDescriptor d = mesh.GetSubMesh(s);
                if (d.topology != MeshTopology.Triangles)
                {
                    continue;
                }
                for (int i = 0; i < d.indexCount; i++)
                {
                    indices.Add(all[d.indexStart + i] + d.baseVertex);
                }
            }
            if (Plugin.Diagnostics.Value)
            {
                Plugin.Log.LogInfo($"collision: read non-readable mesh '{mesh.name}' back from the GPU ({vertexCount} vertices)");
            }
            return new MeshTris { Verts = verts, Indices = indices.ToArray() };
        }

        private static void AddTerrain(RegionJob job, TerrainCollider collider, int rx, int ry, int rz)
        {
            TerrainData data = collider.terrainData;
            if (data == null)
            {
                return;
            }
            Vector3 origin = collider.transform.position;
            Vector3 size = data.size;
            int res = data.heightmapResolution;
            float cellX = size.x / (res - 1), cellZ = size.z / (res - 1);
            // The region's Unity footprint (Minecraft Z flips into Unity -Z).
            Vector3 c0 = Coords.ToUnity(rx * RegionSize - 0.5, 0, (rz + 1) * RegionSize + 0.5);
            Vector3 c1 = Coords.ToUnity((rx + 1) * RegionSize + 0.5, 0, rz * RegionSize - 0.5);
            float ux0 = c0.x, ux1 = c1.x, uz0 = c0.z, uz1 = c1.z;
            int ix0 = Mathf.Clamp(Mathf.FloorToInt((ux0 - origin.x) / cellX), 0, res - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt((ux1 - origin.x) / cellX), 0, res - 1);
            int iz0 = Mathf.Clamp(Mathf.FloorToInt((uz0 - origin.z) / cellZ), 0, res - 1);
            int iz1 = Mathf.Clamp(Mathf.CeilToInt((uz1 - origin.z) / cellZ), 0, res - 1);
            if (ix1 <= ix0 || iz1 <= iz0)
            {
                return;
            }
            float[,] h = data.GetHeights(ix0, iz0, ix1 - ix0 + 1, iz1 - iz0 + 1);
            Vector3 P(int x, int z) => PointToMc(origin + new Vector3((ix0 + x) * cellX, h[z, x] * size.y, (iz0 + z) * cellZ));
            for (int z = 0; z < iz1 - iz0; z++)
            {
                for (int x = 0; x < ix1 - ix0; x++)
                {
                    Vector3 a = P(x, z), b = P(x + 1, z), c = P(x, z + 1), d = P(x + 1, z + 1);
                    job.Tris.Add(new Tri { V = new[] { a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z }, Flags = Proto.TriTerrain });
                    job.Tris.Add(new Tri { V = new[] { b.x, b.y, b.z, d.x, d.y, d.z, c.x, c.y, c.z }, Flags = Proto.TriTerrain });
                }
            }
        }

        // ---- worker: triangles + voxels -> collision ring ----------------------------------------

        private static void WorkerLoop()
        {
            var buf = new ByteBuf();
            foreach (RegionJob job in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (job.Clear)
                    {
                        buf.Reset();
                        buf.I32(job.Epoch);
                        Send(Proto.ColClear, buf);
                        if (job.Epoch == Volatile.Read(ref epoch))
                        {
                            Interlocked.Exchange(ref clearEnd, Link.CollisionHead());
                        }
                    }
                    else if (job.Epoch == Volatile.Read(ref epoch))
                    {
                        SendTriangles(job, buf);
                        Voxelize(job, buf);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"collision worker: {e}");
                }
            }
        }

        private static void Send(uint type, ByteBuf buf)
        {
            for (int attempt = 0; attempt < 2000; attempt++)
            {
                if (Link.WriteCollision(type, buf.Data, buf.Length))
                {
                    return;
                }
                Thread.Sleep(1);  // ring full: Minecraft is behind (or not running)
            }
            Plugin.Log.LogWarning("collision ring stayed full; dropped a message");
        }

        private static void Header(ByteBuf buf, RegionJob job, int count)
        {
            buf.Reset();
            int x = job.Rx * RegionSize, y = job.Ry * RegionSize, z = job.Rz * RegionSize;
            buf.I32(x); buf.I32(y); buf.I32(z);
            buf.I32(x + RegionSize - 1); buf.I32(y + RegionSize - 1); buf.I32(z + RegionSize - 1);
            buf.I32(job.Epoch);
            buf.I32(count);
        }

        // Boxes and capsules (as boxes) become outward-wound triangles, like the Skyrim plugin does.
        private static List<Tri> Triangulate(RegionJob job)
        {
            var all = new List<Tri>(job.Tris);
            void Box(Vector3 c, Vector3 a0, Vector3 a1, Vector3 a2, Vector3 half)
            {
                var corner = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    float sx = (i & 1) != 0 ? 1 : -1, sy = (i & 2) != 0 ? 1 : -1, sz = (i & 4) != 0 ? 1 : -1;
                    corner[i] = c + a0 * (half.x * sx) + a1 * (half.y * sy) + a2 * (half.z * sz);
                }
                void Emit(Vector3 p, Vector3 q, Vector3 r)
                {
                    Vector3 n = Vector3.Cross(q - p, r - p);
                    Vector3 outward = (p + q + r) / 3f - c;
                    if (Vector3.Dot(n, outward) < 0)
                    {
                        (q, r) = (r, q);
                    }
                    all.Add(new Tri { V = new[] { p.x, p.y, p.z, q.x, q.y, q.z, r.x, r.y, r.z } });
                }
                void Quad(int i0, int i1, int i2, int i3)
                {
                    Emit(corner[i0], corner[i1], corner[i2]);
                    Emit(corner[i0], corner[i2], corner[i3]);
                }
                Quad(0, 1, 3, 2);
                Quad(4, 5, 7, 6);
                Quad(0, 1, 5, 4);
                Quad(2, 3, 7, 6);
                Quad(0, 2, 6, 4);
                Quad(1, 3, 7, 5);
            }
            foreach (Obb o in job.Boxes)
            {
                Box(o.C, o.A0, o.A1, o.A2, o.Half);
            }
            foreach (Cap cap in job.Caps)
            {
                Vector3 ab = cap.B - cap.A;
                float len = ab.magnitude;
                Vector3 z = len > 1e-4f ? ab / len : Vector3.up;
                Vector3 reference = Mathf.Abs(z.y) < 0.9f ? Vector3.up : Vector3.right;
                Vector3 x = Vector3.Cross(reference, z).normalized;
                Vector3 y = Vector3.Cross(z, x);
                Box((cap.A + cap.B) * 0.5f, x, y, z, new Vector3(cap.R, cap.R, len * 0.5f + cap.R));
            }
            return all;
        }

        private static void SendTriangles(RegionJob job, ByteBuf buf)
        {
            List<Tri> solid = Triangulate(job);
            float lx = job.Rx * RegionSize - 0.5f, ly = job.Ry * RegionSize - 0.5f, lz = job.Rz * RegionSize - 0.5f;
            float hx = lx + RegionSize + 1, hy = ly + RegionSize + 1, hz = lz + RegionSize + 1;
            var keep = new List<Tri>(solid.Count);
            foreach (Tri t in solid)
            {
                float[] v = t.V;
                float minX = Math.Min(v[0], Math.Min(v[3], v[6])), maxX = Math.Max(v[0], Math.Max(v[3], v[6]));
                float minY = Math.Min(v[1], Math.Min(v[4], v[7])), maxY = Math.Max(v[1], Math.Max(v[4], v[7]));
                float minZ = Math.Min(v[2], Math.Min(v[5], v[8])), maxZ = Math.Max(v[2], Math.Max(v[5], v[8]));
                if (maxX >= lx && minX <= hx && maxY >= ly && minY <= hy && maxZ >= lz && minZ <= hz && !float.IsNaN(minX + minY + minZ))
                {
                    keep.Add(t);
                }
            }
            Header(buf, job, keep.Count);
            foreach (Tri t in keep)
            {
                for (int k = 0; k < 9; k++)
                {
                    buf.F32(t.V[k]);
                }
                buf.U32(t.Flags);
            }
            Send(Proto.ColTris, buf);
        }

        private const int G = RegionSize * 8;  // voxels per region edge
        private const float SteepMin = 0.1f;   // |n.y| below this is a wall: keep it fine-grained
        private const float SteepMax = 0.643f; // |n.y| below this (steeper than ~50 deg) gets block-coarsened
        private const float PrimMargin = 0.5f / 8f;

        private static void Voxelize(RegionJob job, ByteBuf buf)
        {
            var solid = new ulong[G * G];
            var steep = new ulong[G * G];
            float ox = job.Rx * RegionSize, oy = job.Ry * RegionSize, oz = job.Rz * RegionSize;
            int ClampLo(float v) => Math.Max(0, Math.Min(G - 1, (int)Math.Floor(v)));
            int ClampHi(float v) => Math.Max(0, Math.Min(G - 1, (int)Math.Ceiling(v) - 1));

            var a = new float[3];
            var b = new float[3];
            var c = new float[3];
            var n = new float[3];
            var lo = new float[3];
            var hi = new float[3];
            var cen = new float[3];
            foreach (Tri tri in job.Tris)
            {
                float[] v = tri.V;
                for (int i = 0; i < 3; i++)
                {
                    float o = i == 0 ? ox : i == 1 ? oy : oz;
                    a[i] = (v[i] - o) * 8f;
                    b[i] = (v[3 + i] - o) * 8f;
                    c[i] = (v[6 + i] - o) * 8f;
                }
                float e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2];
                float e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
                n[0] = e1y * e2z - e1z * e2y;
                n[1] = e1z * e2x - e1x * e2z;
                n[2] = e1x * e2y - e1y * e2x;
                float len = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (len < 1e-9f)
                {
                    continue;
                }
                n[0] /= len; n[1] /= len; n[2] /= len;
                // ULTRAKILL's geometry is grid-aligned, so faces often lie exactly on a voxel
                // boundary: push each a hair into its solid side (normals face outward) so the
                // voxel behind it, not the one in front, fills.
                for (int i = 0; i < 3; i++)
                {
                    a[i] -= n[i] * 0.02f;
                    b[i] -= n[i] * 0.02f;
                    c[i] -= n[i] * 0.02f;
                    lo[i] = Math.Min(a[i], Math.Min(b[i], c[i]));
                    hi[i] = Math.Max(a[i], Math.Max(b[i], c[i]));
                }
                if (hi[0] < 0 || hi[1] < 0 || hi[2] < 0 || lo[0] > G || lo[1] > G || lo[2] > G)
                {
                    continue;
                }
                float ny = Math.Abs(n[1]);
                ulong[] grid = ny >= SteepMax || ny < SteepMin ? solid : steep;

                int dom = 0;
                if (Math.Abs(n[1]) > Math.Abs(n[dom])) dom = 1;
                if (Math.Abs(n[2]) > Math.Abs(n[dom])) dom = 2;
                int ua = (dom + 1) % 3, va = (dom + 2) % 3;
                float d = n[0] * a[0] + n[1] * a[1] + n[2] * a[2];
                float r = 0.5f * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
                int iu0 = ClampLo(lo[ua]), iu1 = ClampHi(hi[ua]), iv0 = ClampLo(lo[va]), iv1 = ClampHi(hi[va]);
                int id0 = ClampLo(lo[dom] - 0.5f), id1 = ClampHi(hi[dom] + 0.5f);
                for (int iu = iu0; iu <= iu1; iu++)
                {
                    for (int iv = iv0; iv <= iv1; iv++)
                    {
                        float cu = iu + 0.5f, cv = iv + 0.5f;
                        float s0 = (d - r - n[ua] * cu - n[va] * cv) / n[dom];
                        float s1 = (d + r - n[ua] * cu - n[va] * cv) / n[dom];
                        int a0 = Math.Max(id0, (int)Math.Floor(Math.Min(s0, s1) - 0.5f));
                        int a1 = Math.Min(id1, (int)Math.Ceiling(Math.Max(s0, s1) - 0.5f));
                        for (int id = a0; id <= a1; id++)
                        {
                            cen[dom] = id + 0.5f;
                            cen[ua] = cu;
                            cen[va] = cv;
                            if (TriBoxOverlap(cen, 0.5f, a, b, c, n))
                            {
                                int px = dom == 0 ? id : ua == 0 ? iu : iv;
                                int py = dom == 1 ? id : ua == 1 ? iu : iv;
                                int pz = dom == 2 ? id : ua == 2 ? iu : iv;
                                grid[py * G + pz] |= 1UL << px;
                            }
                        }
                    }
                }
            }

            // Convex primitives: voxel-centre containment with a small margin.
            void Fill(Vector3 pLo, Vector3 pHi, Func<Vector3, bool> inside)
            {
                float vx0 = (pLo.x - ox) * 8, vy0 = (pLo.y - oy) * 8, vz0 = (pLo.z - oz) * 8;
                float vx1 = (pHi.x - ox) * 8, vy1 = (pHi.y - oy) * 8, vz1 = (pHi.z - oz) * 8;
                if (vx1 < 0 || vy1 < 0 || vz1 < 0 || vx0 > G || vy0 > G || vz0 > G)
                {
                    return;
                }
                for (int y = ClampLo(vy0 - 1); y <= ClampHi(vy1 + 1); y++)
                {
                    for (int z = ClampLo(vz0 - 1); z <= ClampHi(vz1 + 1); z++)
                    {
                        for (int x = ClampLo(vx0 - 1); x <= ClampHi(vx1 + 1); x++)
                        {
                            if (inside(new Vector3(ox + (x + 0.5f) / 8f, oy + (y + 0.5f) / 8f, oz + (z + 0.5f) / 8f)))
                            {
                                solid[y * G + z] |= 1UL << x;
                            }
                        }
                    }
                }
            }
            foreach (Obb box in job.Boxes)
            {
                Vector3 ext = new Vector3(
                    Math.Abs(box.A0.x) * box.Half.x + Math.Abs(box.A1.x) * box.Half.y + Math.Abs(box.A2.x) * box.Half.z,
                    Math.Abs(box.A0.y) * box.Half.x + Math.Abs(box.A1.y) * box.Half.y + Math.Abs(box.A2.y) * box.Half.z,
                    Math.Abs(box.A0.z) * box.Half.x + Math.Abs(box.A1.z) * box.Half.y + Math.Abs(box.A2.z) * box.Half.z);
                Obb o = box;
                Fill(o.C - ext, o.C + ext, p =>
                {
                    Vector3 dp = p - o.C;
                    return Math.Abs(Vector3.Dot(dp, o.A0)) <= o.Half.x + PrimMargin
                        && Math.Abs(Vector3.Dot(dp, o.A1)) <= o.Half.y + PrimMargin
                        && Math.Abs(Vector3.Dot(dp, o.A2)) <= o.Half.z + PrimMargin;
                });
            }
            foreach (Cap cap in job.Caps)
            {
                Cap k = cap;
                Vector3 r3 = Vector3.one * k.R;
                Fill(Vector3.Min(k.A, k.B) - r3, Vector3.Max(k.A, k.B) + r3, p =>
                {
                    Vector3 ab = k.B - k.A;
                    float len2 = ab.sqrMagnitude;
                    float t = len2 > 0 ? Mathf.Clamp01(Vector3.Dot(p - k.A, ab) / len2) : 0f;
                    return (k.A + ab * t - p).sqrMagnitude <= (k.R + PrimMargin) * (k.R + PrimMargin);
                });
            }

            // Steep (50-84 degree) surfaces snap to whole-block footprints so the risers between
            // neighbouring columns exceed Minecraft's 0.6 step height: cliffs behave like blocks.
            for (int by = 0; by < RegionSize; by++)
            {
                for (int bz = 0; bz < RegionSize; bz++)
                {
                    for (int bx = 0; bx < RegionSize; bx++)
                    {
                        ulong xmask = 0xFFUL << (bx * 8);
                        int minY = 99, maxY = -1;
                        for (int y = by * 8; y < by * 8 + 8; y++)
                        {
                            for (int z = bz * 8; z < bz * 8 + 8; z++)
                            {
                                if ((steep[y * G + z] & xmask) != 0)
                                {
                                    minY = Math.Min(minY, y);
                                    maxY = Math.Max(maxY, y);
                                }
                            }
                        }
                        for (int y = minY; y <= maxY; y++)
                        {
                            for (int z = bz * 8; z < bz * 8 + 8; z++)
                            {
                                solid[y * G + z] |= xmask;
                            }
                        }
                    }
                }
            }

            var blocks = new List<(int x, int y, int z, ulong[] bits)>();
            for (int by = 0; by < RegionSize; by++)
            {
                for (int bz = 0; bz < RegionSize; bz++)
                {
                    for (int bx = 0; bx < RegionSize; bx++)
                    {
                        var bits = new ulong[8];
                        bool any = false;
                        for (int sy = 0; sy < 8; sy++)
                        {
                            ulong layer = 0;
                            for (int sz = 0; sz < 8; sz++)
                            {
                                ulong row = (solid[(by * 8 + sy) * G + (bz * 8 + sz)] >> (bx * 8)) & 0xFF;
                                layer |= row << (sz * 8);
                            }
                            bits[sy] = layer;
                            any |= layer != 0;
                        }
                        if (any)
                        {
                            blocks.Add((job.Rx * RegionSize + bx, job.Ry * RegionSize + by, job.Rz * RegionSize + bz, bits));
                        }
                    }
                }
            }
            Header(buf, job, blocks.Count);
            foreach (var blk in blocks)
            {
                buf.I32(blk.x); buf.I32(blk.y); buf.I32(blk.z); buf.I32(0);
                for (int i = 0; i < 8; i++)
                {
                    buf.U64(blk.bits[i]);
                }
            }
            Send(Proto.ColRegion, buf);
        }

        private static bool AxisTest(float[] v0, float[] v1, float[] v2, float ax, float ay, float az, float h)
        {
            float p0 = v0[0] * ax + v0[1] * ay + v0[2] * az;
            float p1 = v1[0] * ax + v1[1] * ay + v1[2] * az;
            float p2 = v2[0] * ax + v2[1] * ay + v2[2] * az;
            float mn = Math.Min(p0, Math.Min(p1, p2)), mx = Math.Max(p0, Math.Max(p1, p2));
            float r = h * (Math.Abs(ax) + Math.Abs(ay) + Math.Abs(az));
            return !(mn > r || mx < -r);
        }

        [ThreadStatic] private static float[] tv0, tv1, tv2;

        // Akenine-Moller triangle/box SAT, voxel units: box centred at c with half-size h.
        private static bool TriBoxOverlap(float[] c, float h, float[] ta, float[] tb, float[] tc, float[] n)
        {
            float[] v0 = tv0 ??= new float[3], v1 = tv1 ??= new float[3], v2 = tv2 ??= new float[3];
            for (int i = 0; i < 3; i++)
            {
                v0[i] = ta[i] - c[i];
                v1[i] = tb[i] - c[i];
                v2[i] = tc[i] - c[i];
                float mn = Math.Min(v0[i], Math.Min(v1[i], v2[i])), mx = Math.Max(v0[i], Math.Max(v1[i], v2[i]));
                if (mn > h || mx < -h)
                {
                    return false;
                }
            }
            float d = n[0] * v0[0] + n[1] * v0[1] + n[2] * v0[2];
            float r = h * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
            if (Math.Abs(d) > r)
            {
                return false;
            }
            for (int e = 0; e < 3; e++)
            {
                float[] p = e == 0 ? v0 : e == 1 ? v1 : v2;
                float[] q = e == 0 ? v1 : e == 1 ? v2 : v0;
                float ex = q[0] - p[0], ey = q[1] - p[1], ez = q[2] - p[2];
                // edge x unit axes
                if (!AxisTest(v0, v1, v2, 0, ez, -ey, h)) return false;
                if (!AxisTest(v0, v1, v2, -ez, 0, ex, h)) return false;
                if (!AxisTest(v0, v1, v2, ey, -ex, 0, h)) return false;
            }
            return true;
        }
    }

    internal sealed unsafe class ByteBuf
    {
        public byte[] Data = new byte[1 << 16];
        public int Length;

        public void Reset() => Length = 0;

        private void Ensure(int more)
        {
            if (Length + more > Data.Length)
            {
                Array.Resize(ref Data, Math.Max(Data.Length * 2, Length + more));
            }
        }

        public void I32(int v)
        {
            Ensure(4);
            fixed (byte* p = &Data[Length]) *(int*)p = v;
            Length += 4;
        }

        public void U32(uint v) => I32(unchecked((int)v));

        public void F32(float v)
        {
            Ensure(4);
            fixed (byte* p = &Data[Length]) *(float*)p = v;
            Length += 4;
        }

        public void U64(ulong v)
        {
            Ensure(8);
            fixed (byte* p = &Data[Length]) *(ulong*)p = v;
            Length += 8;
        }
    }
}
