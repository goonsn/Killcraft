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
        private const float PickupRangeBlocks = 24f;
        private const int MaxLooseArrows = 48;
        public const float PickupPoke = 13.25f;
        // When Killcraft's Minecraft mod last said the ULTRAKILL effect is on, or to stop it (Host).
        public static float LastSignal = -100f, LastStop = -100f;
        // Which world Minecraft's player is in, as Killcraft's mod says (and when it last said so).
        public static bool DimensionNether;
        public static float DimensionAt = -100f;
        private static readonly List<(uint Id, Vector3 Pos)> loose = new List<(uint, Vector3)>();
        private static readonly byte[] looseName = Encoding.UTF8.GetBytes(WorldRender.LooseArrowName);
        private static readonly byte[] heldName = Encoding.UTF8.GetBytes(HeldItems.StateName);
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

        // active: enemies and loose arrows for Minecraft (Minecraft has V1). linked: what Killcraft's mod
        // reads (V1's skulls, destruction), also while ULTRAKILL has V1; following: ULTRAKILL has V1 in
        // the Nether and Minecraft's player only follows it.
        private const int Reserved = 1 + 16;

        public static void Frame(bool active, bool linked, bool following, NewMovement nm)
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
            // (The loose arrows also where ULTRAKILL tracks no enemies, as in the Sandbox.)
            if (linked && nm != null)
            {
                // What V1 holds, for Killcraft's mod (HeldItems): dead, so SkyCraft makes no stand-in.
                records[count++] = new ActorRecord
                {
                    FormId = HeldItems.StateId,
                    Flags = Proto.ActorDead,
                    Level = (ushort)HeldItems.HeldType,
                    X = HeldItems.Carried(ItemType.SkullBlue),
                    Y = HeldItems.Carried(ItemType.SkullRed),
                    Z = HeldItems.Carried(ItemType.SkullGreen),
                    Yaw = following ? 1f : 0f,
                    Width = Host.FreezeWanted ? 1f : 0f,  // ULTRAKILL paused: Minecraft freezes too
                    Name = heldName,
                };
                count = Destruction.Publish(records, count);
            }
            if (active && nm != null)
            {
                Vector3 p = nm.transform.position;
                float range = RangeBlocks * Coords.U;
                foreach (EnemyIdentifier eid in tracker != null ? tracker.GetCurrentEnemies() : (IEnumerable<EnemyIdentifier>)Array.Empty<EnemyIdentifier>())
                {
                    if (eid == null || count >= Proto.MaxActors - Reserved || !TryBounds(eid, out Bounds b) || (b.center - p).sqrMagnitude > range * range)
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
                // Arrows on corpses and the floor, to be picked up (see WorldRender.LooseArrows).
                WorldRender.LooseArrows(loose, p, PickupRangeBlocks * Coords.U, Math.Max(0, Math.Min(MaxLooseArrows, Proto.MaxActors - count)));
                foreach (var (id, at) in loose)
                {
                    Coords.ToMc(at - Vector3.up * (0.125f * Coords.U), out double x, out double y, out double z);
                    records[count++] = new ActorRecord
                    {
                        FormId = id,
                        Flags = 0,
                        X = (float)x,
                        Y = (float)y,
                        Z = (float)z,
                        Width = 0.25f,
                        Height = 0.25f,
                        HealthFrac = 1f,
                        Level = 1,
                        Name = looseName,
                    };
                }
            }
            Link.WriteActors(records, count);
        }

        // The stand-in id of whoever hit V1: the enemy itself (melee), or for a projectile the enemy
        // nearest the line it came along (dir: its flight direction). 0: no enemy near.
        public static uint AttackerFor(EnemyIdentifier eid, Vector3 from, Vector3 dir)
        {
            if (eid != null && ids.TryGetValue(eid, out uint known))
            {
                return known;
            }
            uint best = 0, nearest = 0;
            float bestDist = 15f, nearestDist = float.MaxValue;
            foreach (var kv in byId)
            {
                EnemyIdentifier e = kv.Value;
                if (e == null || e.dead || !TryBounds(e, out Bounds b))
                {
                    continue;
                }
                Vector3 to = b.center - from;
                if (to.sqrMagnitude < nearestDist)
                {
                    nearestDist = to.sqrMagnitude;
                    nearest = kv.Key;
                }
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
            // None near the line (a boss's attack from far off, a thrown sword's odd path): the
            // nearest enemy, so the hit still counts as theirs and a shield can block it.
            return best != 0 ? best : nearest;
        }

        public static bool IsBoss(uint id) =>
            byId.TryGetValue(id, out EnemyIdentifier eid) && eid != null && (eid.isBoss || eid.GetComponent<BossHealthBar>() != null);

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
                    // A loose arrow's stand-in, poked by the data pack (its own damage amount, not a
                    // sword sweeping through it): a player picked the arrow up.
                    if (WorldRender.IsLooseArrow(e.FormId))
                    {
                        if (Math.Abs(e.A - PickupPoke) < 0.01f)
                        {
                            WorldRender.PickedUp(e.FormId);
                        }
                        break;
                    }
                    if (byId.TryGetValue(e.FormId, out EnemyIdentifier eid) && eid != null && !eid.dead)
                    {
                        Hit(eid, e);
                    }
                    break;
                case Proto.EvKcUltrakillMoves:
                    LastSignal = Time.unscaledTime;
                    break;
                case Proto.EvKcUltrakillStop:
                    LastStop = Time.unscaledTime;
                    break;
                case Proto.EvKcDimension:
                    DimensionNether = e.A > 0.5f;
                    DimensionAt = Time.unscaledTime;
                    break;
                case Proto.EvKcFollowerHurt:
                    // (Minecraft's damage is ULTRAKILL's / 5; ULTRAKILL's own hurt cooldown paces lava.)
                    if (nm != null && !nm.dead && !Patches.OwnsPlayer && e.A > 0f)
                    {
                        nm.GetHurt(Mathf.Max(1, Mathf.RoundToInt(e.A * 5f)), true);
                    }
                    break;
                case Proto.EvKcHeldSelected:
                    HeldItems.Selected((int)e.A);
                    break;
                case Proto.EvKcTerminal:
                    SmileOs.Seen((int)e.A, (int)e.B, (int)e.C, (int)e.D);
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
                Destruction.FromMinecraft(x);  // (Minecraft's explosion already broke its blocks)
            }
            if (Plugin.Diagnostics.Value)
            {
                Plugin.Log.LogInfo($"combat: Minecraft explosion of {radiusBlocks} blocks at {center}");
            }
        }

        private static uint lastHurtBy;
        private static float lastHurtTime = -1f;

        // An enemy's hit on the player was just sent to Minecraft (attacker: its stand-in id, 0: none).
        public static void HurtBy(uint attacker)
        {
            lastHurtBy = attacker;
            lastHurtTime = Time.unscaledTime;
        }

        // Melee is a punch, so hitting an enemy mid-attack parries it as V1's punch would. ULTRAKILL's
        // punch hits use V1's current arm, which its fist script only sets up in its own Update (off
        // while Minecraft has the player): set it up here, kept hidden. With no arm equipped at all,
        // the hammer's hit, which needs none.
        private static string MeleeHitter()
        {
            if (!(MonoSingleton.GetInstance(typeof(FistControl)) is FistControl fists))
            {
                return "hammer";
            }
            if (fists.currentPunch == null)
            {
                fists.RefreshArm();
                if (Patches.OwnsPlayer)
                {
                    fists.NoFist();
                }
            }
            return fists.currentPunch != null ? "punch" : "hammer";
        }

        // A swing also parries a projectile in front of V1 (an energy ball, a Schism's shot), as
        // ULTRAKILL's punch does: its active frames, run on V1's (hidden) Feedbacker arm.
        private static readonly System.Reflection.MethodInfo punchActive = HarmonyLib.AccessTools.Method(typeof(Punch), "ActiveStart");
        private static bool parryFailedLogged;

        public static void SwingParry()
        {
            if (punchActive == null || !(MonoSingleton.GetInstance(typeof(FistControl)) is FistControl fists))
            {
                return;
            }
            try
            {
                if (fists.currentPunch == null)
                {
                    fists.RefreshArm();
                    if (Patches.OwnsPlayer)
                    {
                        fists.NoFist();
                    }
                }
                Punch punch = fists.currentPunch;
                if (punch != null && punch.type == FistType.Standard)
                {
                    punchActive.Invoke(punch, null);
                }
            }
            catch (Exception ex)
            {
                if (!parryFailedLogged)
                {
                    parryFailedLogged = true;
                    Plugin.Log.LogWarning($"combat: parrying with a swing failed: {ex.InnerException?.Message ?? ex.Message}");
                }
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
            bool fire = (e.Flags & Proto.HitFire) != 0;
            Vector3 force = fire ? Vector3.zero : dir * (5000f + e.D * 10000f);
            Vector3 point = TryBounds(eid, out Bounds b) ? b.center : eid.transform.position;
            bool projectile = (e.Flags & Proto.HitProjectile) != 0;
            // Thorns hits back whoever just hurt the player, and Minecraft reports that like a melee hit:
            // as a punch it would parry their attack (ULTRAKILL then makes V1 invincible for a moment,
            // so its shots pass through). A hit on that enemy just after, with no swing, is thorns.
            bool thorns = !projectile && !fire && e.FormId == lastHurtBy && Time.unscaledTime - lastHurtTime < 0.5f
                && Time.unscaledTime - InputForward.LastAttackPress > 0.5f;
            eid.hitter = fire ? "fire" : projectile ? "revolver" : thorns ? "thorns" : MeleeHitter();
            try
            {
                eid.DeliverDamage(eid.gameObject, force, point, damage, false, 0f, null);
                // Minecraft fire (fire and lava blocks, Fire Aspect, Flame arrows) also sets it alight
                // in ULTRAKILL: its flames, and a couple of seconds of its own burning after.
                if (fire && !eid.dead)
                {
                    eid.StartBurning(1f);
                }
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
