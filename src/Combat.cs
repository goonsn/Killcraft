using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's enemies become SkyCraft "actors": Minecraft spawns an invisible, hittable stand-in
    // for each, so swords, crits, sweeps, bows and tridents all work on them with vanilla code. Hits
    // come back as events and are applied through ULTRAKILL's own damage pipeline.
    internal static class Combat
    {
        private const float RangeBlocks = 64f;
        private static readonly Dictionary<EnemyIdentifier, uint> ids = new Dictionary<EnemyIdentifier, uint>();
        private static readonly Dictionary<uint, EnemyIdentifier> byId = new Dictionary<uint, EnemyIdentifier>();
        private static readonly Dictionary<uint, float> maxHealth = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, byte[]> names = new Dictionary<uint, byte[]>();
        private static readonly Dictionary<EnemyIdentifier, Collider[]> hurtboxes = new Dictionary<EnemyIdentifier, Collider[]>();
        private static readonly ActorRecord[] records = new ActorRecord[Proto.MaxActors];
        private static uint next = 1;
        private static float timer;
        private static float hurtboxRefresh;

        public static void Reset()
        {
            ids.Clear();
            byId.Clear();
            maxHealth.Clear();
            names.Clear();
            hurtboxes.Clear();
            Link.WriteActors(records, 0);
        }

        public static void Frame(bool active, NewMovement nm)
        {
            timer -= Time.unscaledDeltaTime;
            if (timer > 0f)
            {
                return;
            }
            timer = 0.05f;
            hurtboxRefresh -= 0.05f;
            if (hurtboxRefresh <= 0f)
            {
                hurtboxRefresh = 2f;
                hurtboxes.Clear();
            }
            int count = 0;
            var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
            if (active && nm != null && tracker != null)
            {
                Vector3 p = nm.transform.position;
                float range = RangeBlocks * Coords.U;
                foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
                {
                    if (eid == null || count >= Proto.MaxActors || !TryBounds(eid, out Bounds b) || (b.center - p).sqrMagnitude > range * range)
                    {
                        continue;
                    }
                    uint id = IdFor(eid);
                    float hp = Mathf.Max(eid.health, 0f);
                    if (!maxHealth.TryGetValue(id, out float max) || hp > max)
                    {
                        maxHealth[id] = max = Mathf.Max(hp, 0.01f);
                    }
                    Coords.ToMc(new Vector3(b.center.x, b.min.y, b.center.z), out double x, out double y, out double z);
                    if (!names.TryGetValue(id, out byte[] name))
                    {
                        names[id] = name = Encoding.UTF8.GetBytes(SafeName(eid));
                    }
                    records[count++] = new ActorRecord
                    {
                        FormId = id,
                        Flags = Proto.ActorHostile | Proto.ActorInCombat | (eid.dead ? Proto.ActorDead : 0),
                        X = (float)x,
                        Y = (float)y,
                        Z = (float)z,
                        Yaw = Coords.YawToMc(eid.transform.eulerAngles.y),
                        Width = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) / Coords.U, 0.3f, 8f),
                        Height = Mathf.Clamp(b.size.y / Coords.U, 0.3f, 16f),
                        HealthFrac = Mathf.Clamp01(hp / max),
                        Level = 1,
                        Name = name,
                    };
                }
            }
            Link.WriteActors(records, count);
        }

        // The stand-in id of whoever hit V1: the enemy itself (melee), or for a projectile the enemy
        // nearest the line it came along (dir: its flight direction). 0: none known.
        public static uint AttackerFor(EnemyIdentifier eid, Vector3 from, Vector3 dir)
        {
            if (eid != null && ids.TryGetValue(eid, out uint known))
            {
                return known;
            }
            uint best = 0;
            float bestDist = 15f;
            foreach (var kv in byId)
            {
                EnemyIdentifier e = kv.Value;
                if (e == null || e.dead || !TryBounds(e, out Bounds b))
                {
                    continue;
                }
                Vector3 to = b.center - from;
                float dist;
                if (dir.sqrMagnitude > 0f)
                {
                    float back = Vector3.Dot(to, -dir);
                    if (back < -2f)
                    {
                        continue;  // ahead of the projectile: not where it came from
                    }
                    dist = (to + dir * back).magnitude;
                }
                else
                {
                    dist = to.magnitude;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = kv.Key;
                }
            }
            return best;
        }

        private static string SafeName(EnemyIdentifier eid)
        {
            try
            {
                return eid.FullName ?? eid.enemyType.ToString();
            }
            catch
            {
                return eid.enemyType.ToString();
            }
        }

        private static uint IdFor(EnemyIdentifier eid)
        {
            if (!ids.TryGetValue(eid, out uint id))
            {
                id = next++;
                ids[eid] = id;
                byId[id] = eid;
            }
            return id;
        }

        // The enemy's hurtboxes (layers 10/11), or any solid collider it has.
        private static bool TryBounds(EnemyIdentifier eid, out Bounds bounds)
        {
            if (!hurtboxes.TryGetValue(eid, out Collider[] cols))
            {
                var all = eid.GetComponentsInChildren<Collider>();
                var hurt = new List<Collider>();
                foreach (Collider c in all)
                {
                    if (c != null && !c.isTrigger && (c.gameObject.layer == 10 || c.gameObject.layer == 11))
                    {
                        hurt.Add(c);
                    }
                }
                if (hurt.Count == 0)
                {
                    foreach (Collider c in all)
                    {
                        if (c != null && !c.isTrigger)
                        {
                            hurt.Add(c);
                        }
                    }
                }
                hurtboxes[eid] = cols = hurt.ToArray();
            }
            bounds = default;
            bool any = false;
            foreach (Collider c in cols)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = c.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(c.bounds);
                }
            }
            return any;
        }

        public static void Handle(in McEvent e, NewMovement nm)
        {
            switch (e.Type)
            {
                case Proto.EvHitActor:
                    if (byId.TryGetValue(e.FormId, out EnemyIdentifier eid) && eid != null && !eid.dead)
                    {
                        Hit(eid, e);
                    }
                    break;
                case Proto.EvArrowStuck:
                    if (byId.TryGetValue(e.FormId, out EnemyIdentifier stuckIn) && stuckIn != null)
                    {
                        WorldRender.StickArrow(stuckIn, new Vector3(e.A, e.B, e.C), e.D, BitConverter.Int32BitsToSingle((int)e.Flags));
                    }
                    break;
                case Proto.EvExplosion:
                    Explode(Coords.ToUnity(e.A, e.B, e.C), e.D);
                    break;
                case Proto.EvPlayerDied:
                    if (nm != null && !nm.dead)
                    {
                        Plugin.Log.LogInfo("Minecraft player died: killing V1");
                        Patches.AllowDamage = true;
                        try
                        {
                            nm.GetHurt(999999, false, 1f, false, false, 0.35f, true);
                        }
                        finally
                        {
                            Patches.AllowDamage = false;
                        }
                    }
                    break;
            }
        }

        // A Minecraft explosion (TNT, creepers, ...) is also an ULTRAKILL one, the same size: its blast,
        // light, shake and debris, and it throws ULTRAKILL's enemies around. It can't hurt V1:
        // Minecraft's own explosion already hurt its player.
        private static void Explode(Vector3 center, float radiusBlocks)
        {
            var refs = MonoSingleton.GetInstance(typeof(DefaultReferenceManager)) as DefaultReferenceManager;
            if (refs == null || refs.explosion == null || radiusBlocks <= 0f)
            {
                return;
            }
            GameObject go = UnityEngine.Object.Instantiate(refs.explosion, center, Quaternion.identity);
            Explosion[] parts = go.GetComponentsInChildren<Explosion>();
            float biggest = 0f;
            foreach (Explosion x in parts)
            {
                biggest = Mathf.Max(biggest, x.maxSize);
            }
            float k = biggest > 0f ? radiusBlocks * Coords.U / biggest : 1f;
            foreach (Explosion x in parts)
            {
                x.maxSize *= k;
                x.speed = (x.speed == 0f ? 1f : x.speed) * k;
                x.canHit = AffectedSubjects.EnemiesOnly;
            }
            if (Plugin.Diagnostics.Value)
            {
                Plugin.Log.LogInfo($"combat: Minecraft explosion of {radiusBlocks} blocks at {center}");
            }
        }

        private static void Hit(EnemyIdentifier eid, in McEvent e)
        {
            float damage = e.A * Plugin.DamageToUltrakill.Value;
            if (damage <= 0f)
            {
                return;
            }
            Vector3 dir = Coords.DirToUnity(e.B, 0f, e.C);
            if (dir.sqrMagnitude > 1e-6f)
            {
                dir.Normalize();
            }
            Vector3 force = dir * (5000f + e.D * 10000f);
            Vector3 point = TryBounds(eid, out Bounds b) ? b.center : eid.transform.position;
            bool projectile = (e.Flags & Proto.HitProjectile) != 0;
            eid.hitter = projectile ? "revolver" : "punch";
            try
            {
                eid.DeliverDamage(eid.gameObject, force, point, damage, false, 0f, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"combat: damaging {SafeName(eid)} failed: {ex.Message}");
            }
            if (Plugin.Diagnostics.Value)
            {
                Plugin.Log.LogInfo($"combat: Minecraft hit {SafeName(eid)} for {e.A} -> {damage} ULTRAKILL damage, health now {eid.health}");
            }
        }
    }
}
