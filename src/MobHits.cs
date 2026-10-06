using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // With Minecraft off (F9) V1 fights with ULTRAKILL's weapons, and Minecraft's mobs have nothing in
    // ULTRAKILL to hit: so their boxes are known here (where Minecraft draws their shadows, SkyCraft's
    // world entities) and what ULTRAKILL's weapons do is checked against them: revolver and railcannon
    // beams, shotgun pellets and other shots, nails and saws, explosions, punches, the chainsaw. What
    // hits a mob goes to Killcraft's mod (Destruction: a damage request), which hurts it as the player
    // did (so it's angry with the player, drops its loot and experience).
    internal static class MobHits
    {
        // ULTRAKILL damage (a revolver shot is 1) to Minecraft's (a zombie has 20).
        private const float DamageToMc = 8f;

        private struct Mob
        {
            public uint Id;
            public Vector3 Center, Half;
        }

        private static readonly List<Mob> mobs = new List<Mob>();
        private static readonly Dictionary<uint, float> damage = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, Vector3> push = new Dictionary<uint, Vector3>();
        private static readonly Dictionary<int, Vector3> lastPos = new Dictionary<int, Vector3>();
        private static readonly HashSet<long> hitOnce = new HashSet<long>();
        private static float cleanup;

        public static bool Active { get; private set; }

        // on: ULTRAKILL has V1, Minecraft is in its world. player: Minecraft's own player (not a mob).
        public static void Frame(bool on, Vector3 player)
        {
            Active = on;
            mobs.Clear();
            if (!on)
            {
                damage.Clear();
                push.Clear();
                lastPos.Clear();
                hitOnce.Clear();
                return;
            }
            if ((cleanup -= Time.deltaTime) <= 0f)
            {
                cleanup = 5f;
                lastPos.Clear();
                hitOnce.Clear();
            }
            float u = Coords.U;
            if (Link.ReadWorldEntities(out Link.WorldEntity[] entities, out int count, out _, out _, out _))
            {
                for (int i = 0; i < count; i++)
                {
                    ref Link.WorldEntity e = ref entities[i];
                    if (e.Kind != 6)  // a living thing's shadow: players and mobs
                    {
                        continue;
                    }
                    Vector3 feet = Coords.ToUnity(e.X, e.Y, e.Z);
                    if (Mathf.Abs(feet.x - player.x) < 0.3f * u && Mathf.Abs(feet.z - player.z) < 0.3f * u && Mathf.Abs(feet.y - player.y) < 1f * u)
                    {
                        continue;  // the player itself
                    }
                    // (Only the width comes along: the tall ones are about two blocks, the wide ones as tall as wide.)
                    float w = Mathf.Max(e.Scale, 0.3f);
                    float h = w < 1f ? 2f : w;
                    mobs.Add(new Mob { Id = e.Id, Center = feet + Vector3.up * (h * 0.5f * u), Half = new Vector3(w * 0.5f * u, h * 0.5f * u, w * 0.5f * u) });
                }
            }
            foreach (var kv in damage)
            {
                push.TryGetValue(kv.Key, out Vector3 dir);
                Destruction.Damage(kv.Key, kv.Value, dir);
            }
            damage.Clear();
            push.Clear();
        }

        private static void Hurt(Mob m, float ukDamage, Vector3 dir)
        {
            float amount = ukDamage * DamageToMc;
            if (amount <= 0f)
            {
                return;
            }
            damage[m.Id] = (damage.TryGetValue(m.Id, out float d) ? d : 0f) + amount;
            push[m.Id] = (push.TryGetValue(m.Id, out Vector3 p) ? p : Vector3.zero) + dir.normalized;
        }

        // Where along a to b (0..1) the segment, thickened by radius, enters the mob's box; or -1.
        private static float Enter(Mob m, Vector3 a, Vector3 b, float radius)
        {
            Vector3 min = m.Center - m.Half - Vector3.one * radius, max = m.Center + m.Half + Vector3.one * radius;
            Vector3 d = b - a;
            float t0 = 0f, t1 = 1f;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(d[k]) < 1e-6f)
                {
                    if (a[k] < min[k] || a[k] > max[k])
                    {
                        return -1f;
                    }
                    continue;
                }
                float ta = (min[k] - a[k]) / d[k], tb = (max[k] - a[k]) / d[k];
                if (ta > tb)
                {
                    (ta, tb) = (tb, ta);
                }
                t0 = Mathf.Max(t0, ta);
                t1 = Mathf.Min(t1, tb);
                if (t0 > t1)
                {
                    return -1f;
                }
            }
            return t0;
        }

        // A beam or shot along a to b: the first mob it meets, or every one (piercing).
        public static bool Line(Vector3 a, Vector3 b, float radiusBlocks, float ukDamage, bool pierce, long once = 0)
        {
            if (!Active || mobs.Count == 0)
            {
                return false;
            }
            float r = radiusBlocks * Coords.U;
            int best = -1;
            float bestT = 2f;
            bool any = false;
            for (int i = 0; i < mobs.Count; i++)
            {
                float t = Enter(mobs[i], a, b, r);
                if (t < 0f)
                {
                    continue;
                }
                if (pierce)
                {
                    if (once == 0 || hitOnce.Add(once * 100003 + mobs[i].Id))
                    {
                        Hurt(mobs[i], ukDamage, b - a);
                        any = true;
                    }
                }
                else if (t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
            if (best >= 0 && (once == 0 || hitOnce.Add(once * 100003 + mobs[best].Id)))
            {
                Hurt(mobs[best], ukDamage, b - a);
                any = true;
            }
            return any;
        }

        // A blast: everything within it, less further out.
        public static void Sphere(Vector3 center, float radiusUnits, float ukDamage, bool falloff)
        {
            if (!Active || mobs.Count == 0 || radiusUnits <= 0f)
            {
                return;
            }
            foreach (Mob m in mobs)
            {
                Vector3 closest = Vector3.Max(m.Center - m.Half, Vector3.Min(center, m.Center + m.Half));
                float dist = (closest - center).magnitude;
                if (dist > radiusUnits)
                {
                    continue;
                }
                float k = falloff ? Mathf.Lerp(1f, 0.35f, dist / radiusUnits) : 1f;
                Hurt(m, ukDamage * k, m.Center - center + Vector3.up * 0.3f * radiusUnits);
            }
        }

        // A shot that moves (pellets, nails, saws, the chainsaw): what it passed through since last time.
        // Once per mob, unless it keeps cutting (saws, the chainsaw: every tick it's in there).
        public static void Moving(Object shot, Vector3 now, float radiusBlocks, float ukDamage, bool keepsCutting)
        {
            if (!Active || mobs.Count == 0 || shot == null)
            {
                return;
            }
            int id = shot.GetInstanceID();
            Vector3 from = lastPos.TryGetValue(id, out Vector3 p) ? p : now;
            lastPos[id] = now;
            Line(from, now, radiusBlocks, ukDamage, true, keepsCutting ? 0 : id);
        }

        public static void Punch(Punch punch)
        {
            if (!Active || punch == null || mobs.Count == 0)
            {
                return;
            }
            Transform cam = punch.transform.parent != null ? punch.transform.parent : punch.transform;
            if (MonoSingleton.GetInstance(typeof(CameraController)) is CameraController cc && cc != null && cc.cam != null)
            {
                cam = cc.cam.transform;
            }
            float u = Coords.U;
            bool heavy = punch.type == FistType.Heavy;
            Line(cam.position, cam.position + cam.forward * (3f * u), 0.8f, heavy ? 2.5f : 1f, false);
        }

        // A revolver's or railcannon's beam, along what it drew (the railcannon's goes through them all).
        public static void Beam(RevolverBeam beam)
        {
            if (!Active || beam == null || beam.fake || mobs.Count == 0)
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
            bool pierce = beam.beamType == BeamType.Railgun || beam.hitAmount > 1;
            Line(a, b, 0.35f, Mathf.Max(beam.damage, 0.5f), pierce);
        }

        // The chainsaw: cuts what it's in, every tenth of a second.
        private static readonly Dictionary<int, float> sawnAt = new Dictionary<int, float>();

        public static void Chainsaw(Chainsaw saw)
        {
            if (!Active || saw == null || mobs.Count == 0)
            {
                return;
            }
            int id = saw.GetInstanceID();
            if (sawnAt.TryGetValue(id, out float at) && Time.time - at < 0.1f)
            {
                return;
            }
            sawnAt[id] = Time.time;
            if (sawnAt.Count > 64)
            {
                sawnAt.Clear();
            }
            Sphere(saw.transform.position, 1.2f * Coords.U, 0.35f, false);
        }

        public static void Explosion(Explosion e)
        {
            if (!Active || e == null || e.harmless || e.enemy || Destruction.IsFromMinecraft(e))
            {
                return;
            }
            float radius = e.maxSize;
            float uk = Mathf.Clamp(radius / Coords.U * 0.6f, 1f, 6f) * (e.halved ? 0.5f : 1f);
            Sphere(e.transform.position, radius, uk, true);
        }
    }
}
