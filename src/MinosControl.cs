using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Killcraft
{
    // The Minos Prime game mode's Minos (MinosMode): ULTRAKILL's own Minos Prime from P-1, his AI's
    // choosing switched off, played by one player.
    //  - The Minos player: walks him (the move keys, from behind him: this camera), and each of his
    //    attacks is a key, ULTRAKILL's own attack as his AI would start it (his own animations, hits,
    //    voice lines, his tiredness after a run of them). He goes at whichever V1 is nearest the
    //    middle of the screen. That player's own V1 is put away meanwhile (unseen, not hurt, no keys).
    //  - Everyone else: their copy of the same Minos goes where his is (20 times a second), and each
    //    attack he starts starts on theirs too, at that moment, from where he was, at the same V1: so
    //    on each player's screen it's their own copy's hits that land on them (dodged and parried
    //    there, as in ULTRAKILL), and his health is the Minos player's (EnemySync: hits go there).
    [HarmonyPatch]
    internal static class MinosControl
    {
        public const byte AttCombo = 1, AttBoxing = 2, AttDropkick = 3, AttUppercut = 4, AttProjectile = 5, AttJump = 6,
            AttRiderKick = 7, AttDropAttack = 8, AttDownSwing = 9, AttTeleportGround = 10, AttTeleportAir = 11,
            AttChain = 12, AttCut = 13;
        private static readonly string[] Methods =
        {
            null, "Combo", "Boxing", "Dropkick", "Uppercut", "ProjectilePunch", "Jump", "RiderKick", "DropAttack", "DownSwing",
            "TeleportOnGround", "TeleportAnywhere",
        };

        private const float SendEvery = 0.05f, TeleportCooldown = 1.5f, BufferFor = 0.3f, CameraDistance = 11f, CameraRaise = 1.5f;
        // A player's Minos: a short breath at most between attacks (his AI's tiredness, 2-4 s after a run
        // of them, was most of the time spent unable to do anything), his animations at least as quick
        // as on Brutal, and a key pressed mid-attack kept until it can start (up to HoldFor).
        private const float MaxRest = 0.15f, MinAnimSpeed = 1.2f, HoldFor = 1.5f;
        private const float Delay = 0.1f;
        private const int CameraBlockers = (1 << 8) | (1 << 24) | (1 << 26);

        private static readonly AccessTools.FieldRef<MinosPrime, bool> inActionOf = AccessTools.FieldRefAccess<MinosPrime, bool>("inAction");
        private static readonly AccessTools.FieldRef<MinosPrime, float> cooldownOf = AccessTools.FieldRefAccess<MinosPrime, float>("cooldown");
        private static readonly AccessTools.FieldRef<MinosPrime, bool> activatedOf = AccessTools.FieldRefAccess<MinosPrime, bool>("activated");
        private static readonly AccessTools.FieldRef<MinosPrime, bool> jumpingOf = AccessTools.FieldRefAccess<MinosPrime, bool>("jumping");
        private static readonly AccessTools.FieldRef<MinosPrime, bool> trackingOf = AccessTools.FieldRefAccess<MinosPrime, bool>("tracking");
        private static readonly AccessTools.FieldRef<MinosPrime, bool> fullTrackingOf = AccessTools.FieldRefAccess<MinosPrime, bool>("fullTracking");
        private static readonly AccessTools.FieldRef<MinosPrime, Vector3> playerPosOf = AccessTools.FieldRefAccess<MinosPrime, Vector3>("playerPos");
        private static readonly AccessTools.FieldRef<MinosPrime, GroundCheckEnemy> gceOf = AccessTools.FieldRefAccess<MinosPrime, GroundCheckEnemy>("gce");
        private static readonly AccessTools.FieldRef<MinosPrime, NavMeshAgent> nmaOf = AccessTools.FieldRefAccess<MinosPrime, NavMeshAgent>("nma");
        private static readonly AccessTools.FieldRef<MinosPrime, Animator> animOf = AccessTools.FieldRefAccess<MinosPrime, Animator>("anim");
        private static readonly AccessTools.FieldRef<MinosPrime, Rigidbody> rbOf = AccessTools.FieldRefAccess<MinosPrime, Rigidbody>("rb");
        private static readonly AccessTools.FieldRef<MinosPrime, MPAttack> previousComboOf = AccessTools.FieldRefAccess<MinosPrime, MPAttack>("previousCombo");
        private static readonly AccessTools.FieldRef<MinosPrime, MPAttack> lastAttackOf = AccessTools.FieldRefAccess<MinosPrime, MPAttack>("lastAttack");
        private static readonly AccessTools.FieldRef<MinosPrime, bool> hasRiderKickedOf = AccessTools.FieldRefAccess<MinosPrime, bool>("hasRiderKicked");
        private static readonly AccessTools.FieldRef<MinosPrime, int> downSwingAmountOf = AccessTools.FieldRefAccess<MinosPrime, int>("downSwingAmount");
        private static readonly AccessTools.FieldRef<MinosPrime, EnemyIdentifier> eidOf = AccessTools.FieldRefAccess<MinosPrime, EnemyIdentifier>("eid");
        private static readonly AccessTools.FieldRef<MinosPrime, float> originalHpOf = AccessTools.FieldRefAccess<MinosPrime, float>("originalHp");
        private static readonly AccessTools.FieldRef<MinosPrime, int> attackAmountOf = AccessTools.FieldRefAccess<MinosPrime, int>("attackAmount");
        private static float busyAt = -100f;
        private static readonly MethodInfo customPhysics = AccessTools.Method(typeof(MinosPrime), "CustomPhysics");
        private static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();

        // The arena's Minos (MinosArena), and whether he's been woken (the round's GO).
        public static MinosPrime Body;
        public static bool Awake => Body != null && Body.gameObject.activeInHierarchy;

        // Who he's going at (a V1: a player id; TargetEnemy: one of the level's enemies, TargetEnemyId;
        // int.MinValue: a point ahead, no one in front).
        public static int TargetId { get; private set; } = int.MinValue;
        public const int TargetEnemy = int.MinValue + 1;
        private static uint targetEnemyId;
        private static readonly Dictionary<EnemyIdentifier, EnemyTarget> enemyTargets = new Dictionary<EnemyIdentifier, EnemyTarget>();
        private const float EnemyCone = 25f, EnemyReach = 150f;
        private static EnemyTarget chosen, playerTarget;
        private static Transform aimMarker;
        private static EnemyTarget aimTarget;

        // The Minos player's: what was pressed (a moment's grace while he's busy), the teleport's rest.
        private static int buffered;
        private static float bufferedAt = -100f, teleportedAt = -100f, sentAt = -100f;
        private static Vector3 lastSentPos;
        private static bool loggedDrive;

        // The others': where he's said to be, by his clock.
        private struct Snap
        {
            public float T, Yaw, AnimSpeed;
            public Vector3 Pos;
            public byte Flags;
        }

        private const byte SnapGrounded = 1, SnapInAction = 2, SnapWalking = 4, SnapDead = 8, SnapFree = 16;
        private static readonly List<Snap> snaps = new List<Snap>();
        private static float offset = float.NaN;
        private static float animSpeed = -1f;
        private static float divergedFor;

        // A Minos the mode drives (the arena's, in a round): not his AI.
        public static bool Controls(MinosPrime mp) => MinosMode.Active && mp != null && mp == Body;

        // The mode's Minos (EnemySync: hits on him go to his player, he dies on their game).
        public static bool IsBody(EnemyIdentifier eid) => MinosMode.Active && Body != null && eid != null && eidOf(Body) == eid;

        // ---- his AI off ----

        [HarmonyPatch(typeof(MinosPrime), "PickGroundedAttack"), HarmonyPrefix]
        private static bool NoGroundedAttack(MinosPrime __instance) => !Controls(__instance);

        [HarmonyPatch(typeof(MinosPrime), "PickAirAttack"), HarmonyPrefix]
        private static bool NoAirAttack(MinosPrime __instance) => !Controls(__instance);

        // His FixedUpdate (ULTRAKILL's walks him to the player): his own physics still (gravity, landing,
        // the run's tiredness), his walking the Minos player's (or, on the others' screens, where he is).
        [HarmonyPatch(typeof(MinosPrime), "FixedUpdate"), HarmonyPrefix]
        private static bool Fixed(MinosPrime __instance)
        {
            if (!Controls(__instance))
            {
                return true;
            }
            try
            {
                Drive(__instance);
            }
            catch (Exception e)
            {
                if (!loggedDrive)
                {
                    loggedDrive = true;
                    Plugin.Log.LogWarning($"minos: couldn't move him ({e})");
                }
            }
            return false;
        }

        private static void Drive(MinosPrime mp)
        {
            if (!activatedOf(mp))
            {
                return;
            }
            ChainTick(mp);
            if (MinosMode.IAmMinos)
            {
                attackAmountOf(mp) = 0;
                if (cooldownOf(mp) > MaxRest)
                {
                    cooldownOf(mp) = MaxRest;
                }
                Animator a = animOf(mp);
                if (a != null && a.speed < MinAnimSpeed && !chainWaiting)
                {
                    a.speed = MinAnimSpeed;
                }
            }
            customPhysics.Invoke(mp, null);
            Animator anim = animOf(mp);
            GroundCheckEnemy gce = gceOf(mp);
            bool inAction = inActionOf(mp);
            bool grounded = gce != null && gce.onGround && !jumpingOf(mp);
            float dt = Time.fixedDeltaTime;
            Transform t = mp.transform;
            if (!MinosMode.IAmMinos && !(!inAction && grounded) && Sample(out Snap s2))
            {
                // (In the air here and on the ground there for long: put where he is.)
                bool there = (s2.Flags & SnapGrounded) != 0 && (s2.Flags & SnapInAction) == 0;
                divergedFor = there && !inAction ? divergedFor + dt : 0f;
                if (divergedFor > 1.5f)
                {
                    divergedFor = 0f;
                    Warp(mp, s2.Pos);
                }
            }
            if (anim != null && !inAction)
            {
                anim.SetBool("Walking", walkingNow);
            }
            // (As his own FixedUpdate turns him to the one he's going at, mid-attack.)
            EnemyIdentifier eid = eidOf(mp);
            EnemyTarget target = eid != null ? eid.target : null;
            if (target != null && (trackingOf(mp) || fullTrackingOf(mp)))
            {
                if (!fullTrackingOf(mp))
                {
                    t.LookAt(playerPosOf(mp));
                }
                else
                {
                    t.rotation = Quaternion.LookRotation(target.position - new Vector3(t.position.x, mp.aimingBone.position.y, t.position.z));
                }
            }
        }

        // His walking, each frame (Update, not the physics tick: what the camera follows moves as often
        // as it's drawn): the Minos player's move keys; on the others' games, where his stream says.
        private const float WalkSpeed = 24f, AirSpeed = 22f, AirAccel = 90f;
        private static bool walkingNow;
        private static float toldDriveAt = -100f, controlledSince = -1f;

        private static void Walk(MinosPrime mp, float dt)
        {
            walkingNow = false;
            if (!activatedOf(mp) || inActionOf(mp))
            {
                return;
            }
            GroundCheckEnemy gce = gceOf(mp);
            NavMeshAgent nma = nmaOf(mp);
            bool grounded = gce != null && gce.onGround && !jumpingOf(mp);
            bool agent = nma != null && nma.enabled && nma.isOnNavMesh;
            Transform t = mp.transform;
            if (MinosMode.IAmMinos)
            {
                Vector3 wish = Wish();
                Diagnose(mp, wish, grounded, agent);
                if (FirstPerson && CanAct())
                {
                    t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.Euler(0f, CameraYaw(), 0f), 1440f * dt);
                }
                if (grounded && wish.sqrMagnitude > 0.01f)
                {
                    float speed = Mathf.Max(nma != null ? nma.speed : 0f, WalkSpeed);
                    Step(mp, nma, agent, wish * speed * dt);
                    if (!FirstPerson)
                    {
                        t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(new Vector3(wish.x, 0f, wish.z)), 720f * dt);
                    }
                    walkingNow = true;
                }
                else if (!grounded && wish.sqrMagnitude > 0.01f)
                {
                    Rigidbody rb = rbOf(mp);
                    if (rb != null && !rb.isKinematic)
                    {
                        Vector3 v = rb.velocity;
                        Vector3 flat = Vector3.MoveTowards(new Vector3(v.x, 0f, v.z), wish * AirSpeed, AirAccel * dt);
                        rb.velocity = new Vector3(flat.x, v.y, flat.z);
                    }
                    if (!FirstPerson)
                    {
                        t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(new Vector3(wish.x, 0f, wish.z)), 540f * dt);
                    }
                }
            }
            else if (!grounded && Sample(out Snap air))
            {
                Vector3 p = t.position;
                float k = 1f - Mathf.Exp(-8f * dt);
                t.position = new Vector3(Mathf.Lerp(p.x, air.Pos.x, k), p.y, Mathf.Lerp(p.z, air.Pos.z, k));
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, air.Yaw, 0f), 1f - Mathf.Exp(-12f * dt));
            }
            else if (grounded && Sample(out Snap s))
            {
                Vector3 delta = s.Pos - t.position;
                if (delta.sqrMagnitude > 12f * 12f)
                {
                    Warp(mp, s.Pos);
                }
                else
                {
                    Step(mp, nma, agent, delta);
                }
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, s.Yaw, 0f), 1f - Mathf.Exp(-20f * dt));
                walkingNow = (s.Flags & SnapWalking) != 0;
            }
        }

        // A step: along the navmesh where there is one, else straight (not into a wall).
        private static void Step(MinosPrime mp, NavMeshAgent nma, bool agent, Vector3 step)
        {
            if (step.sqrMagnitude < 1e-8f)
            {
                return;
            }
            if (agent)
            {
                nma.isStopped = true;
                nma.Move(step);
            }
            else if (!Physics.Raycast(mp.transform.position + Vector3.up * 2f, step.normalized, step.magnitude + 1.5f,
                LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
            {
                Warp(mp, mp.transform.position + step);
            }
        }

        // (The first 10 seconds as Minos: how he's doing, once a second, for the log.)
        private static void Diagnose(MinosPrime mp, Vector3 wish, bool grounded, bool agent)
        {
            float now = Time.unscaledTime;
            if (controlledSince < 0f)
            {
                controlledSince = now;
            }
            if (now - controlledSince > 10f || now - toldDriveAt < 1f)
            {
                return;
            }
            toldDriveAt = now;
            NavMeshAgent nma = nmaOf(mp);
            Rigidbody rb = rbOf(mp);
            Plugin.Log.LogInfo($"minos: driving: keys {wish.magnitude:0.00}, grounded {grounded}, navmesh {(nma != null ? (nma.enabled ? (nma.isOnNavMesh ? "on" : "off it") : "disabled") : "none")}, "
                + $"in action {inActionOf(mp)}, kinematic {(rb != null && rb.isKinematic)}, can act {CanAct()}, at {mp.transform.position}");
        }

        private static void Warp(MinosPrime mp, Vector3 at)
        {
            NavMeshAgent nma = nmaOf(mp);
            if (nma != null && nma.enabled && nma.isOnNavMesh)
            {
                nma.Warp(at);
            }
            mp.transform.position = at;
            Rigidbody rb = rbOf(mp);
            if (rb != null)
            {
                rb.position = at;
                if (!rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                }
            }
        }

        // The move keys, from where the camera faces (flat).
        private static Vector3 Wish()
        {
            if (!CanAct())
            {
                return Vector3.zero;
            }
            InputManager im = MonoSingleton<InputManager>.Instance;
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (im == null || im.InputSource == null || cc == null)
            {
                return Vector3.zero;
            }
            Vector2 move = im.InputSource.Move.ReadValue<Vector2>();
            Vector3 forward = Vector3.ProjectOnPlane(cc.transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            return Vector3.ClampMagnitude(forward * move.y + right * move.x, 1f);
        }

        private static bool CanAct()
        {
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            return MinosMode.IAmMinos && MinosMode.Fighting && Awake && !(om != null && om.paused) && !Typing.Active && !McCommand.Busy;
        }

        // Who he goes at: kept by ULTRAKILL's own choosing of an enemy's target (EnemySync's hook).
        public static bool TargetFor(EnemyIdentifier eid, out EnemyTarget target)
        {
            target = null;
            if (!MinosMode.Active || Body == null || eid == null || eidOf(Body) != eid)
            {
                return false;
            }
            target = chosen;
            return target != null;
        }

        // ---- each frame (Host) ----

        public static void Frame()
        {
            if (!MinosMode.Active || Body == null)
            {
                return;
            }
            EnemyIdentifier eid = eidOf(Body);
            if (Awake && eid != null && !eid.dead)
            {
                try
                {
                    Walk(Body, Time.deltaTime);
                }
                catch (Exception e)
                {
                    if (!loggedDrive)
                    {
                        loggedDrive = true;
                        Plugin.Log.LogWarning($"minos: couldn't move him ({e})");
                    }
                }
            }
            if (MinosMode.IAmMinos)
            {
                OptionsManager om = MonoSingleton<OptionsManager>.Instance;
                if (!(om != null && om.paused) && !Typing.Active && UkThirdPerson.Pressed())
                {
                    FirstPerson = !FirstPerson;
                    Plugin.Log.LogInfo($"minos: {(FirstPerson ? "his eyes" : "behind him")}");
                }
                Choose();
                Keys();
                Send();
            }
            else
            {
                chosen = TargetOf(TargetId, targetEnemyId);
                if (animSpeed > 0f && Awake && eid != null && !eid.dead)
                {
                    Animator anim = animOf(Body);
                    if (anim != null && !chainWaiting && Mathf.Abs(anim.speed - animSpeed) > 0.001f)
                    {
                        anim.speed = animSpeed;
                    }
                }
            }
            if (eid != null && chosen != null && Awake)
            {
                eid.target = chosen;
            }
        }

        // The V1 nearest the middle of the screen (a little in front: then the nearest), kept while he's
        // busy with an attack; no one: a point ahead where the screen's middle is.
        private static void Choose()
        {
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (cc == null)
            {
                return;
            }
            if (Awake && inActionOf(Body) && chosen != null && TargetId != int.MinValue
                && (TargetId == TargetEnemy ? chosen.enemyIdentifier != null && !chosen.enemyIdentifier.dead : MinosMode.InPlay(TargetId)))
            {
                return;
            }
            Vector3 eye = cc.transform.position, forward = cc.transform.forward;
            int best = int.MinValue;
            float bestScore = float.MaxValue;
            foreach (int id in MinosMode.V1s)
            {
                if (!MinosMode.InPlay(id) || !RemotePlayers.HeadOf(id, out Vector3 head))
                {
                    continue;
                }
                Vector3 to = head - eye;
                float angle = Vector3.Angle(forward, to);
                float score = angle < 35f ? angle : 1000f + to.magnitude;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }
            // (The level's enemies: one in the middle of the screen, a little nearer it than any V1 is.)
            EnemyIdentifier bestEnemy = null;
            var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
            List<EnemyIdentifier> enemies = tracker != null ? tracker.GetCurrentEnemies() : null;
            EnemyIdentifier self = Eid;
            if (enemies != null)
            {
                foreach (EnemyIdentifier e in enemies)
                {
                    if (e == null || e.dead || e == self || !e.gameObject.activeInHierarchy || Pvp.Is(e))
                    {
                        continue;
                    }
                    Vector3 to = EnemyAim(e) - eye;
                    float angle = Vector3.Angle(forward, to);
                    if (angle > EnemyCone || to.magnitude > EnemyReach || angle >= bestScore)
                    {
                        continue;
                    }
                    bestScore = angle;
                    bestEnemy = e;
                }
            }
            targetEnemyId = 0;
            if (bestEnemy != null)
            {
                TargetId = TargetEnemy;
                targetEnemyId = EnemySync.IdOf(bestEnemy);
                chosen = EnemyTargetFor(bestEnemy);
                return;
            }
            TargetId = best;
            chosen = best != int.MinValue ? TargetOf(best) : null;
            if (chosen == null)
            {
                if (aimMarker == null)
                {
                    aimMarker = new GameObject("Killcraft Minos aim").transform;
                    aimTarget = new EnemyTarget(aimMarker);
                }
                aimMarker.position = Physics.Raycast(eye, forward, out RaycastHit hit, 80f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)
                    ? hit.point : eye + forward * 40f;
                chosen = aimTarget;
                TargetId = int.MinValue;
            }
        }

        private static Vector3 EnemyAim(EnemyIdentifier e) =>
            e.weakPoint != null ? e.weakPoint.transform.position : e.transform.position + Vector3.up * 1.5f;

        private static EnemyTarget EnemyTargetFor(EnemyIdentifier e)
        {
            if (e == null)
            {
                return null;
            }
            if (!enemyTargets.TryGetValue(e, out EnemyTarget t) || t == null || t.targetTransform == null)
            {
                enemyTargets[e] = t = new EnemyTarget(e);
            }
            return t;
        }

        // Where the one he's going at is (the reticle).
        public static bool TargetHead(out Vector3 at)
        {
            at = Vector3.zero;
            if (TargetId == TargetEnemy)
            {
                EnemyIdentifier e = chosen != null ? chosen.enemyIdentifier : null;
                if (e == null || e.dead)
                {
                    return false;
                }
                at = EnemyAim(e) + Vector3.up * 1f;
                return true;
            }
            return TargetId != int.MinValue && RemotePlayers.HeadOf(TargetId, out at);
        }

        // As each game has them: an enemy by its name in the level, else a player.
        private static EnemyTarget TargetOf(int id, uint enemy)
        {
            if (id == TargetEnemy)
            {
                EnemyIdentifier e = enemy != 0 ? EnemySync.Find(enemy) : null;
                return e != null && !e.dead ? EnemyTargetFor(e) : TargetOf(int.MinValue);
            }
            return TargetOf(id);
        }

        // A player as this game's enemies see them: this player's V1, or another's marker.
        private static EnemyTarget TargetOf(int id)
        {
            if (id == int.MinValue)
            {
                if (Body == null)
                {
                    return null;
                }
                if (aimMarker == null)
                {
                    aimMarker = new GameObject("Killcraft Minos aim").transform;
                    aimTarget = new EnemyTarget(aimMarker);
                }
                aimMarker.position = Body.transform.position + Body.transform.forward * 20f + Vector3.up * 3f;
                return aimTarget;
            }
            if (id == Room.MyId)
            {
                if (playerTarget == null || playerTarget.targetTransform == null)
                {
                    try
                    {
                        playerTarget = EnemyTarget.TrackPlayer();
                    }
                    catch
                    {
                        playerTarget = null;
                    }
                }
                return playerTarget;
            }
            return RemotePlayers.TargetOf(id);
        }

        // ---- the Minos player's keys ----

        private static void Keys()
        {
            if (!CanAct())
            {
                buffered = 0;
                return;
            }
            InputManager im = MonoSingleton<InputManager>.Instance;
            if (im == null || im.InputSource == null)
            {
                return;
            }
            PlayerInput src = im.InputSource;
            MinosPrime mp = Body;
            GroundCheckEnemy gce = gceOf(mp);
            bool grounded = gce != null && gce.onGround && !jumpingOf(mp);
            bool chaining = Chaining(mp);
            if (chaining && src.Fire1.WasPerformedThisFrame && Chain(mp))
            {
                SendMark(AttChain);
            }
            int want = 0;
            if (grounded)
            {
                want = src.Fire1.WasPerformedThisFrame && !chaining ? AttCombo
                    : src.Fire2.WasPerformedThisFrame ? AttProjectile
                    : src.Punch.WasPerformedThisFrame ? AttUppercut
                    : src.Slide.WasPerformedThisFrame ? AttDropkick
                    : src.Jump.WasPerformedThisFrame ? AttJump
                    : src.Dodge.WasPerformedThisFrame ? AttTeleportGround : 0;
            }
            else
            {
                want = src.Fire1.WasPerformedThisFrame && !chaining ? AttDownSwing
                    : src.Fire2.WasPerformedThisFrame ? AttRiderKick
                    : src.Slide.WasPerformedThisFrame || src.Punch.WasPerformedThisFrame ? AttDropAttack
                    : src.Dodge.WasPerformedThisFrame ? AttTeleportAir : 0;
            }
            float now = Time.unscaledTime;
            if (want != 0)
            {
                buffered = want;
                bufferedAt = now;
            }
            if (inActionOf(mp))
            {
                busyAt = now;
            }
            if (buffered == 0 || now - bufferedAt > HoldFor || now - Mathf.Max(bufferedAt, busyAt) > BufferFor)
            {
                buffered = 0;
                return;
            }
            if (chainWaiting && buffered != 0 && buffered != AttCombo)
            {
                CutCombo(mp, true);  // (another move pressed while he hangs for a click: the combo's over, that move now)
            }
            if (inActionOf(mp) && grounded && IsGroundAttack(buffered) && Settling(mp))
            {
                mp.StopAction();
            }
            if (!Ready(buffered, grounded))
            {
                return;
            }
            int code = buffered;
            buffered = 0;
            Do(code);
        }

        // ---- his left-click combo, one click a hit ----
        // His Combo (two swings and a shot) and Boxing (four punches) are each one animation. At each
        // hit's end (its DamageStop) with another hit still to come: on if this player clicked during
        // it; otherwise he hangs there (his animation slowed right down) for a click, and with none in
        // ChainWindow the combo stops (before its next teleport). The others' copies do the same by his
        // messages (a click: AttChain; stopped: AttCut), waiting longer for them.
        private const float ChainWindow = 0.35f, ChainSlow = 0.15f;
        private static bool chainPressed, chainWaiting;
        private static float chainUntil, chainSpeed = 1f;
        private static readonly Dictionary<AnimationClip, float[]> hitTimes = new Dictionary<AnimationClip, float[]>();

        private static bool InCombo(Animator anim)
        {
            AnimatorStateInfo st = anim.GetCurrentAnimatorStateInfo(0);
            return st.IsName("Combo") || st.IsName("Boxing");
        }

        // Another hit (a swing or his shot) still to come in the animation playing.
        private static bool HitsAhead(Animator anim)
        {
            AnimatorClipInfo[] ci = anim.GetCurrentAnimatorClipInfo(0);
            if (ci.Length == 0 || ci[0].clip == null)
            {
                return false;
            }
            AnimationClip clip = ci[0].clip;
            if (!hitTimes.TryGetValue(clip, out float[] times))
            {
                var found = new List<float>();
                foreach (UnityEngine.AnimationEvent e in clip.events)
                {
                    if (e.functionName == "SnakeSwingStart" || e.functionName == "ProjectileShoot")
                    {
                        found.Add(e.time);
                    }
                }
                hitTimes[clip] = times = found.ToArray();
            }
            float now = Mathf.Min(anim.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f) * clip.length;
            foreach (float t in times)
            {
                if (t > now + 0.01f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Chaining(MinosPrime mp)
        {
            Animator anim = animOf(mp);
            return inActionOf(mp) && anim != null && InCombo(anim) && HitsAhead(anim);
        }

        // A click: on at once if he's hanging for it, else counted for this hit's end (one at most:
        // whether this one counted).
        private static bool Chain(MinosPrime mp)
        {
            if (chainWaiting)
            {
                Animator anim = animOf(mp);
                if (anim != null)
                {
                    anim.speed = chainSpeed;
                }
                chainWaiting = false;
                chainPressed = false;
                return true;
            }
            if (chainPressed)
            {
                return false;
            }
            chainPressed = true;
            return true;
        }

        private static void ChainReset(MinosPrime mp)
        {
            if (chainWaiting)
            {
                Animator anim = animOf(mp);
                if (anim != null)
                {
                    anim.speed = chainSpeed;
                }
            }
            chainWaiting = false;
            chainPressed = false;
        }

        private static void ChainTick(MinosPrime mp)
        {
            if (!chainWaiting)
            {
                return;
            }
            Animator anim = animOf(mp);
            if (!inActionOf(mp) || anim == null || !InCombo(anim))
            {
                ChainReset(mp);
                return;
            }
            if (Time.unscaledTime > chainUntil)
            {
                CutCombo(mp, MinosMode.IAmMinos);
            }
        }

        // The combo stopped where it is: back to standing.
        private static void CutCombo(MinosPrime mp, bool send)
        {
            Animator anim = animOf(mp);
            ChainReset(mp);
            mp.DamageStop();
            mp.StopAction();
            mp.StopTracking();
            if (anim != null)
            {
                anim.CrossFadeInFixedTime("Idle", 0.15f, 0);
            }
            if (send)
            {
                SendMark(AttCut);
            }
        }

        // His down swing over in the air: sent down (ULTRAKILL holds him still through it, and he only
        // fell slowly after).
        private const float DropAfterSwing = 45f;

        [HarmonyPatch(typeof(MinosPrime), "StopAction"), HarmonyPrefix]
        private static void SwingOverBefore(MinosPrime __instance, out bool __state)
        {
            __state = false;
            if (!MinosMode.Active || __instance == null || __instance != Body || !inActionOf(__instance))
            {
                return;
            }
            Animator anim = animOf(__instance);
            __state = anim != null && anim.GetCurrentAnimatorStateInfo(0).IsName("DownSwing");
        }

        [HarmonyPatch(typeof(MinosPrime), "StopAction"), HarmonyPostfix]
        private static void SwingOver(MinosPrime __instance, bool __state)
        {
            if (!__state)
            {
                return;
            }
            GroundCheckEnemy gce = gceOf(__instance);
            Rigidbody rb = rbOf(__instance);
            if (rb == null || (gce != null && gce.onGround))
            {
                return;
            }
            rb.isKinematic = false;
            Vector3 v = rb.velocity;
            rb.velocity = new Vector3(v.x, Mathf.Min(v.y, -DropAfterSwing), v.z);
        }

        [HarmonyPatch(typeof(MinosPrime), "DamageStop"), HarmonyPrefix]
        private static void HitEndingBefore(MinosPrime __instance, out bool __state)
        {
            SwingCheck2 sc = __instance != null ? scOf(__instance) : null;
            __state = __instance != null && (swingingOf(__instance) || (sc != null && sc.damaging));
        }

        [HarmonyPatch(typeof(MinosPrime), "DamageStop"), HarmonyPostfix]
        private static void HitEnded(MinosPrime __instance, bool __state)
        {
            if (!__state || !MinosMode.Active || __instance == null || __instance != Body || !inActionOf(__instance) || chainWaiting)
            {
                return;
            }
            Animator anim = animOf(__instance);
            if (anim == null || !InCombo(anim) || !HitsAhead(anim))
            {
                return;
            }
            if (chainPressed)
            {
                chainPressed = false;
                return;
            }
            chainWaiting = true;
            chainUntil = Time.unscaledTime + (MinosMode.IAmMinos ? ChainWindow : ChainWindow * 2f + 0.6f);
            chainSpeed = anim.speed;
            anim.speed = chainSpeed * ChainSlow;
        }

        // A mark in his attacks (a combo's click, its cut) for the others' copies.
        private static void SendMark(byte code)
        {
            MinosPrime mp = Body;
            if (mp == null)
            {
                return;
            }
            Vector3 from = mp.transform.position;
            float yaw = mp.transform.eulerAngles.y;
            int target = TargetId;
            uint enemy = targetEnemyId;
            KillNet.Send(MinosMode.AttackMessage, KillNet.ToRoom, w =>
            {
                w.Write(code);
                w.Write(Time.unscaledTime);
                w.Write(from.x);
                w.Write(from.y);
                w.Write(from.z);
                w.Write(yaw);
                w.Write(target);
                w.Write(enemy);
            });
        }

        // An attack's last part: his swing done and its animation most of the way through (him coming
        // back to stand): cut short for the next one, as his flowing from one into another.
        private const float SettleFrom = 0.68f;
        private static readonly AccessTools.FieldRef<MinosPrime, bool> swingingOf = AccessTools.FieldRefAccess<MinosPrime, bool>("swinging");
        private static readonly AccessTools.FieldRef<MinosPrime, SwingCheck2> scOf = AccessTools.FieldRefAccess<MinosPrime, SwingCheck2>("sc");
        private static readonly string[] Settlable = { "Combo", "Boxing", "Dropkick", "ProjectilePunch", "DropRecovery" };

        private static bool IsGroundAttack(int code) =>
            code == AttCombo || code == AttBoxing || code == AttProjectile || code == AttUppercut || code == AttDropkick;

        private static bool Settling(MinosPrime mp)
        {
            Animator anim = animOf(mp);
            SwingCheck2 sc = scOf(mp);
            if (anim == null || swingingOf(mp) || anim.IsInTransition(0) || (sc != null && sc.damaging))
            {
                return false;
            }
            AnimatorStateInfo st = anim.GetCurrentAnimatorStateInfo(0);
            if (st.normalizedTime < SettleFrom)
            {
                return false;
            }
            foreach (string s in Settlable)
            {
                if (st.IsName(s))
                {
                    return true;
                }
            }
            return false;
        }

        // His rider kick and drop attack go along a line to where it meets the level and put him there;
        // a line meeting nothing (a kick aimed at the sky, the far side of a big room) put him at the
        // world's middle, mostly under the map. Then: aimed lower until it meets the floor; nothing
        // below him at all: no kick (falling on).
        [HarmonyPatch(typeof(MinosPrime), "AirRaycastAttack"), HarmonyPrefix]
        private static bool SafeAirAttack(MinosPrime __instance, ref Vector3 direction)
        {
            if (!Controls(__instance) || __instance.aimingBone == null)
            {
                return true;
            }
            Vector3 from = __instance.aimingBone.position;
            int mask = LayerMaskDefaults.Get(LMD.Environment);
            if (Physics.Raycast(from, direction, 250f, mask))
            {
                return true;
            }
            Vector3 flat = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            foreach (float down in new[] { 0.5f, 1f, 2f, 4f })
            {
                Vector3 d = (flat + Vector3.down * down).normalized;
                if (Physics.Raycast(from, d, 250f, mask))
                {
                    direction = d;
                    return true;
                }
            }
            if (Physics.Raycast(from, Vector3.down, 250f, mask))
            {
                direction = Vector3.down;
                return true;
            }
            Plugin.Log.LogInfo("minos: his air attack met nothing: not done");
            inActionOf(__instance) = false;
            hasRiderKickedOf(__instance) = true;
            Animator anim = animOf(__instance);
            if (anim != null)
            {
                anim.Play("Falling", 0, 0f);
            }
            return false;
        }

        // Whether that can start now: not mid-attack, rested (his own tiredness: ULTRAKILL's), and as his
        // AI would allow it (one rider kick a jump, two down swings).
        public static bool Ready(int code, bool grounded)
        {
            MinosPrime mp = Body;
            if (mp == null || !activatedOf(mp) || inActionOf(mp))
            {
                return false;
            }
            Animator anim = animOf(mp);
            if (anim != null && anim.IsInTransition(0) && !MinosMode.IAmMinos)
            {
                return false;
            }
            switch (code)
            {
                case AttJump:
                    return grounded;
                case AttTeleportGround:
                case AttTeleportAir:
                    return Time.unscaledTime - teleportedAt > TeleportCooldown;
                case AttRiderKick:
                    return !grounded && !hasRiderKickedOf(mp) && cooldownOf(mp) <= 0f;
                case AttDownSwing:
                    return !grounded && downSwingAmountOf(mp) < 2 && cooldownOf(mp) <= 0f;
                case AttDropAttack:
                    return !grounded && cooldownOf(mp) <= 0f;
                default:
                    return grounded && cooldownOf(mp) <= 0f;
            }
        }

        public static float TeleportLeft => Mathf.Max(0f, TeleportCooldown - (Time.unscaledTime - teleportedAt));

        public static bool Tired => Body != null && cooldownOf(Body) > 0f && !inActionOf(Body);

        // Who he's going at, as one number (the target marker's: a new one, a new lock-on).
        public static long TargetKey => ((long)TargetId << 32) ^ targetEnemyId;

        private static void Do(int code)
        {
            MinosPrime mp = Body;
            Vector3 from = mp.transform.position;
            float yaw = mp.transform.eulerAngles.y;
            Aim(mp);
            ChainReset(mp);
            Invoke(mp, code);
            int sent = code;
            if (code == AttCombo && lastAttackOf(mp) == MPAttack.Boxing)
            {
                sent = AttBoxing;  // (his Combo alternates with Boxing by itself: which one it was)
            }
            if (code == AttTeleportGround || code == AttTeleportAir)
            {
                teleportedAt = Time.unscaledTime;
            }
            int target = TargetId;
            uint enemy = targetEnemyId;
            KillNet.Send(MinosMode.AttackMessage, KillNet.ToRoom, w =>
            {
                w.Write((byte)sent);
                w.Write(Time.unscaledTime);
                w.Write(from.x);
                w.Write(from.y);
                w.Write(from.z);
                w.Write(yaw);
                w.Write(target);
                w.Write(enemy);
            });
        }

        // His target, and where it stands (his attacks go by it at once).
        private static void Aim(MinosPrime mp)
        {
            EnemyIdentifier eid = eidOf(mp);
            if (eid != null && chosen != null)
            {
                eid.target = chosen;
                Vector3 p = chosen.position;
                playerPosOf(mp) = new Vector3(p.x, mp.transform.position.y, p.z);
            }
        }

        private static void Invoke(MinosPrime mp, int code)
        {
            if (code <= 0 || code >= Methods.Length || Methods[code] == null)
            {
                return;
            }
            string name = Methods[code];
            if (!methods.TryGetValue(name, out MethodInfo m))
            {
                methods[name] = m = AccessTools.Method(typeof(MinosPrime), name);
            }
            if (m == null)
            {
                Plugin.Log.LogWarning($"minos: ULTRAKILL's Minos has no {name}");
                return;
            }
            m.Invoke(mp, null);
        }

        // Where he is, 20 times a second.
        private static void Send()
        {
            float now = Time.unscaledTime;
            if (now - sentAt < SendEvery || Body == null)
            {
                return;
            }
            float dt = Mathf.Max(now - sentAt, 0.01f);
            sentAt = now;
            MinosPrime mp = Body;
            Vector3 pos = mp.transform.position;
            Vector3 vel = (pos - lastSentPos) / dt;
            lastSentPos = pos;
            GroundCheckEnemy gce = gceOf(mp);
            Animator anim = animOf(mp);
            EnemyIdentifier eid = eidOf(mp);
            bool grounded = gce != null && gce.onGround && !jumpingOf(mp);
            bool walking = anim != null && anim.GetBool("Walking");
            byte flags = (byte)((grounded ? SnapGrounded : 0) | (inActionOf(mp) ? SnapInAction : 0) | (walking ? SnapWalking : 0)
                | (eid != null && eid.dead ? SnapDead : 0) | (MinosMode.Free ? SnapFree : 0));
            // (While he hangs for a click his animation's slowed: the others' copies hang by their own.)
            float speed = anim != null ? (chainWaiting ? chainSpeed : anim.speed) : 1f;
            int sceneHash = MinosMode.FreeSceneHash;
            int serial = MinosMode.FreeSerial;
            uint targetEnemy = targetEnemyId;
            Enemy enemy = mp.GetComponent<Enemy>();
            float health = enemy != null ? enemy.health : eid != null ? eid.health : 0f;
            int target = TargetId;
            KillNet.Send(MinosMode.StateMessage, KillNet.ToRoom, w =>
            {
                w.Write(now);
                w.Write(pos.x);
                w.Write(pos.y);
                w.Write(pos.z);
                w.Write(mp.transform.eulerAngles.y);
                w.Write(vel.x);
                w.Write(vel.y);
                w.Write(vel.z);
                w.Write(flags);
                w.Write(target);
                w.Write(speed);
                w.Write(health);
                w.Write(sceneHash);
                w.Write(serial);
                w.Write(targetEnemy);
            });
        }

        // ---- the others': his copy ----

        public static void HandleState(int from, BinaryReader r)
        {
            float at = r.ReadSingle();
            var s = new Snap { T = at, Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Yaw = r.ReadSingle() };
            r.ReadSingle();
            r.ReadSingle();
            r.ReadSingle();
            s.Flags = r.ReadByte();
            int target = r.ReadInt32();
            s.AnimSpeed = r.ReadSingle();
            float health = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadSingle() : -1f;
            int sceneHash = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadInt32() : 0;
            int serial = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadInt32() : 0;
            uint heardEnemy = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadUInt32() : 0u;
            if (!MinosMode.Active && (s.Flags & SnapFree) != 0 && (s.Flags & SnapDead) == 0)
            {
                MinosMode.JoinFree(from, sceneHash, serial);
            }
            if (!MinosMode.Active || from != MinosMode.MinosId || MinosMode.IAmMinos)
            {
                return;
            }
            // (Minos in any level: his copy here, where he is there.)
            if (MinosMode.Free && Body == null)
            {
                if ((s.Flags & SnapDead) != 0 || !MinosMode.Spawn(s.Pos, s.Yaw))
                {
                    return;
                }
                snaps.Clear();
                offset = float.NaN;
            }
            TargetId = target;
            targetEnemyId = heardEnemy;
            animSpeed = s.AnimSpeed;
            // (His health and death as his player's game has them.)
            EnemyIdentifier eid = Eid;
            if (eid != null && Awake && !eid.dead)
            {
                if ((s.Flags & SnapDead) != 0)
                {
                    EnemySync.KillNow(eid);
                }
                else if (health > 0f)
                {
                    EnemySync.ShowHealth(eid, health);
                }
            }
            float now = Time.unscaledTime;
            float sample = now - at;
            if (float.IsNaN(offset) || Mathf.Abs(sample - offset) > 5f)
            {
                offset = sample;
                snaps.Clear();
            }
            else
            {
                offset = sample < offset ? sample : offset + (sample - offset) * 0.01f;
            }
            if (snaps.Count > 0 && at <= snaps[snaps.Count - 1].T)
            {
                return;
            }
            snaps.Add(s);
            while (snaps.Count > 40)
            {
                snaps.RemoveAt(0);
            }
        }

        // Where he was a moment ago by his clock (between two of what he said).
        private static bool Sample(out Snap s)
        {
            s = default;
            if (snaps.Count == 0 || float.IsNaN(offset))
            {
                return false;
            }
            float t = Time.unscaledTime - offset - Delay;
            if (t <= snaps[0].T)
            {
                s = snaps[0];
                return true;
            }
            for (int i = snaps.Count - 1; i >= 1; i--)
            {
                Snap a = snaps[i - 1], b = snaps[i];
                if (a.T > t)
                {
                    continue;
                }
                if (t > b.T)
                {
                    break;
                }
                float u = (t - a.T) / Mathf.Max(b.T - a.T, 0.0001f);
                s = a;
                s.Pos = (b.Pos - a.Pos).sqrMagnitude > 30f * 30f ? b.Pos : Vector3.Lerp(a.Pos, b.Pos, u);
                s.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, u);
                return true;
            }
            s = snaps[snaps.Count - 1];
            return true;
        }

        // An attack of his: started on this copy now, from where he started it, at the same V1.
        public static void HandleAttack(int from, BinaryReader r)
        {
            if (!MinosMode.Active || from != MinosMode.MinosId || MinosMode.IAmMinos)
            {
                return;
            }
            byte code = r.ReadByte();
            r.ReadSingle();
            Vector3 at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadSingle();
            int target = r.ReadInt32();
            uint enemy = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadUInt32() : 0u;
            MinosPrime mp = Body;
            if (mp == null || !Awake || !activatedOf(mp))
            {
                return;
            }
            if (code == AttCut)
            {
                Animator ca = animOf(mp);
                if (inActionOf(mp) && ca != null && InCombo(ca))
                {
                    CutCombo(mp, false);
                }
                return;
            }
            if (code == AttChain)
            {
                Chain(mp);
                return;
            }
            try
            {
                TargetId = target;
                targetEnemyId = enemy;
                chosen = TargetOf(target, enemy);
                if ((mp.transform.position - at).sqrMagnitude < 8f * 8f)
                {
                    Warp(mp, at);
                    mp.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                }
                if (inActionOf(mp))
                {
                    mp.StopAction();
                    mp.DamageStop();
                }
                Aim(mp);
                ChainReset(mp);
                if (code == AttCombo)
                {
                    previousComboOf(mp) = MPAttack.Boxing;
                }
                else if (code == AttBoxing)
                {
                    previousComboOf(mp) = MPAttack.Combo;
                }
                Invoke(mp, code);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"minos: couldn't play his attack {code} ({e.Message})");
            }
        }

        // ---- the Minos player's camera, and V1 put away ----

        private static readonly List<Collider> offColliders = new List<Collider>();
        private static bool v1Away;

        // Each frame, while this player is Minos: V1 unseen, untouchable, without keys or weapons, kept
        // where he is (its camera's what this player sees him by).
        public static void KeepV1Away(NewMovement nm)
        {
            if (nm == null)
            {
                return;
            }
            try
            {
                v1Away = true;
                nm.activated = false;
                if (nm.rb != null)
                {
                    nm.rb.isKinematic = true;
                }
                foreach (Collider c in nm.GetComponents<Collider>())
                {
                    if (c != null && c.enabled)
                    {
                        c.enabled = false;
                        offColliders.Add(c);
                    }
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    gc.activated = false;
                    if (gc.currentWeapon != null && gc.currentWeapon.activeSelf)
                    {
                        gc.currentWeapon.SetActive(false);
                    }
                }
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null && fc.activated)
                {
                    fc.NoFist();
                }
                HookArm hook = MonoSingleton<HookArm>.Instance;
                if (hook != null && hook.enabled)
                {
                    hook.enabled = false;
                }
                if (Body != null)
                {
                    nm.transform.position = Body.transform.position + Vector3.up * 2f;
                }
            }
            catch
            {
            }
        }

        // V1 back as it was (the round over).
        public static void Release()
        {
            Uncollapse();
            LiveBounds(false);
            rigTop = null;
            rigOf = null;
            armRoots.Clear();
            chainWaiting = false;
            chainPressed = false;
            ShowHead();
            headBone = null;
            headOf = null;
            arms = null;
            armsOf = null;
            armWeight = 0f;
            Undim();
            selfTarget = null;
            enemyTargets.Clear();
            targetEnemyId = 0;
            CopyHitting = false;
            blasts.Clear();
            try
            {
                // (A black hole's hit on him took V1's health away for later: not now.)
                NewMovement v1 = MonoSingleton<NewMovement>.Instance;
                if (v1 != null && v1Away)
                {
                    v1.antiHp = 0f;
                }
            }
            catch
            {
            }
            ShowBody();
            controlledSince = -1f;
            Body = null;
            chosen = null;
            playerTarget = null;
            TargetId = int.MinValue;
            snaps.Clear();
            offset = float.NaN;
            animSpeed = -1f;
            buffered = 0;
            teleportedAt = -100f;
            if (!v1Away)
            {
                return;
            }
            v1Away = false;
            try
            {
                foreach (Collider c in offColliders)
                {
                    if (c != null)
                    {
                        c.enabled = true;
                    }
                }
                offColliders.Clear();
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm != null)
                {
                    if (nm.rb != null)
                    {
                        nm.rb.isKinematic = false;
                    }
                    nm.activated = true;
                }
                HookArm hook = MonoSingleton<HookArm>.Instance;
                if (hook != null)
                {
                    hook.enabled = true;
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    gc.activated = true;
                    if (gc.currentWeapon != null)
                    {
                        gc.currentWeapon.SetActive(true);
                    }
                }
                MonoSingleton<FistControl>.Instance?.YesFist();
            }
            catch
            {
            }
        }

        // (Not hurt while Minos: his own hits reach the V1 put away inside him.)
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool NotHurtAsMinos(int damage)
        {
            if (!MinosMode.IAmMinos)
            {
                return true;
            }
            try
            {
                HurtHim(damage);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"minos: couldn't pass a hit on to him ({e.Message})");
            }
            return false;
        }

        // A hit meant for this player's V1: on Minos, a quarter as hard for his health as for V1's.
        private static void HurtHim(int damage)
        {
            EnemyIdentifier eid = Eid;
            if (eid == null || eid.dead || !Awake || damage <= 0)
            {
                return;
            }
            float max = originalHpOf(Body);
            if (max <= 0f)
            {
                Enemy mach = Body.GetComponent<Enemy>();
                max = mach != null ? mach.health : 10f;
            }
            float units = Mathf.Clamp(damage, 0, 100) / 100f * max * 0.25f;
            eid.hitter = "enemy";
            eid.DeliverDamage(eid.gameObject, Vector3.zero, Body.transform.position + Vector3.up * 3f, units, tryForExplode: false);
        }

        // What this player's enemies go after while they're Minos: him (EnemySync's choosing).
        public static EnemyTarget SelfTarget
        {
            get
            {
                EnemyIdentifier eid = Eid;
                if (eid == null || eid.dead)
                {
                    return null;
                }
                if (selfTarget == null || selfTarget.enemyIdentifier != eid)
                {
                    selfTarget = new EnemyTarget(eid);
                }
                return selfTarget;
            }
        }

        private static EnemyTarget selfTarget;

        // ---- the others' copy: its hits on the level's enemies are for show (his player's count) ----

        public static bool CopyHitting { get; private set; }

        private static bool IsCopy(EnemyIdentifier eid) => MinosMode.Active && !MinosMode.IAmMinos && eid != null && eid == Eid;

        [HarmonyPatch(typeof(SwingCheck2), "CheckCollision"), HarmonyPrefix]
        private static void SwingBefore(SwingCheck2 __instance, out bool __state)
        {
            __state = __instance != null && IsCopy(__instance.eid);
            if (__state)
            {
                CopyHitting = true;
            }
        }

        [HarmonyPatch(typeof(SwingCheck2), "CheckCollision"), HarmonyFinalizer]
        private static Exception SwingAfter(bool __state, Exception __exception)
        {
            if (__state)
            {
                CopyHitting = false;
            }
            return __exception;
        }

        [HarmonyPatch(typeof(Projectile), "Collided"), HarmonyPrefix]
        private static void ShotBefore(Projectile __instance, out bool __state)
        {
            __state = __instance != null && __instance.safeEnemyType == EnemyType.MinosPrime && MinosMode.Active && !MinosMode.IAmMinos && Body != null;
            if (__state)
            {
                CopyHitting = true;
            }
        }

        [HarmonyPatch(typeof(Projectile), "Collided"), HarmonyFinalizer]
        private static Exception ShotAfter(bool __state, Exception __exception)
        {
            if (__state)
            {
                CopyHitting = false;
            }
            return __exception;
        }

        [HarmonyPatch(typeof(PhysicalShockwave), "CheckCollision"), HarmonyPrefix]
        private static void WaveBefore(PhysicalShockwave __instance, out bool __state)
        {
            __state = __instance != null && __instance.enemyType == EnemyType.MinosPrime && MinosMode.Active && !MinosMode.IAmMinos && Body != null;
            if (__state)
            {
                CopyHitting = true;
            }
        }

        [HarmonyPatch(typeof(PhysicalShockwave), "CheckCollision"), HarmonyFinalizer]
        private static Exception WaveAfter(bool __state, Exception __exception)
        {
            if (__state)
            {
                CopyHitting = false;
            }
            return __exception;
        }

        // His blast (made where he stands): its id kept while it grows.
        private static readonly HashSet<int> blasts = new HashSet<int>();

        [HarmonyPatch(typeof(Explosion), "Start"), HarmonyPostfix]
        private static void BlastMade(Explosion __instance)
        {
            if (__instance == null || !MinosMode.Active || MinosMode.IAmMinos || Body == null || !__instance.enemy)
            {
                return;
            }
            Transform root = __instance.transform.root;
            if ((root.position - Body.transform.position).sqrMagnitude < 9f)
            {
                if (blasts.Count > 64)
                {
                    blasts.Clear();
                }
                blasts.Add(__instance.GetInstanceID());
            }
        }

        [HarmonyPatch(typeof(Explosion), "Collide"), HarmonyPrefix]
        private static void BlastBefore(Explosion __instance, out bool __state)
        {
            __state = __instance != null && blasts.Contains(__instance.GetInstanceID());
            if (__state)
            {
                CopyHitting = true;
            }
        }

        [HarmonyPatch(typeof(Explosion), "Collide"), HarmonyFinalizer]
        private static Exception BlastAfter(bool __state, Exception __exception)
        {
            if (__state)
            {
                CopyHitting = false;
            }
            return __exception;
        }

        // After ULTRAKILL has placed the camera: behind him, where this player looks, kept out of walls.
        public static void Place()
        {
            if (!MinosMode.IAmMinos || Body == null)
            {
                return;
            }
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (cc == null || cc.transform.parent == null)
            {
                return;
            }
            Transform t = cc.transform;
            Quaternion look = cc.transform.parent.rotation * Quaternion.AngleAxis(-cc.rotationX, Vector3.right);
            Vector3 forward = look * Vector3.forward;
            EnemyIdentifier eid = eidOf(Body);
            Vector3 head = eid != null && eid.weakPoint != null ? eid.weakPoint.transform.position : Body.transform.position + Vector3.up * 5f;
            Dim();
            if (FirstPerson)
            {
                Transform hb = HeadBone();
                if (hb != null)
                {
                    ShowBody();
                    Vector3 eye = hb.position;
                    Vector3 flat = Quaternion.Euler(0f, CameraYaw(), 0f) * Vector3.forward;
                    t.position = eye + Vector3.up * 0.15f + flat * 0.35f;
                    HandsUp(t.position, look);
                    if (!Collapse())
                    {
                        hb.localScale = Vector3.one * 0.001f;
                        headHidden = true;
                    }
                }
                else
                {
                    HideBody();
                    t.position = head + forward * 0.9f;
                }
                t.rotation = look;
                return;
            }
            ShowHead();
            ShowBody();
            LiveBounds(false);
            Vector3 pivot = head + Vector3.up * CameraRaise;
            Vector3 want = pivot - forward * CameraDistance;
            Vector3 dir = want - pivot;
            t.position = Physics.SphereCast(pivot, 0.4f, dir.normalized, out RaycastHit hit, dir.magnitude, CameraBlockers, QueryTriggerInteraction.Ignore)
                ? pivot + dir.normalized * Mathf.Max(0.5f, hit.distance - 0.2f)
                : want;
            t.rotation = look;
        }

        // From his eyes (the third-person key: F5 unless set otherwise) or from behind him. From his eyes
        // his body casts only its shadow here.
        public static bool FirstPerson { get; private set; }
        private static readonly List<(Renderer R, UnityEngine.Rendering.ShadowCastingMode Was)> hidden = new List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();

        private static void HideBody()
        {
            if (hidden.Count > 0 || Body == null)
            {
                return;
            }
            foreach (Renderer r in Body.GetComponentsInChildren<Renderer>())
            {
                if ((r is SkinnedMeshRenderer || r is MeshRenderer) && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                {
                    hidden.Add((r, r.shadowCastingMode));
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
            }
        }

        // The camera's turn (left and right), from what turns it (not from where it points: looking
        // straight down, that flipped from ahead to behind with the slightest move).
        private static float CameraYaw()
        {
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (cc != null && cc.transform.parent != null)
            {
                return cc.transform.parent.eulerAngles.y;
            }
            return Body != null ? Body.transform.eulerAngles.y : 0f;
        }

        // ---- first person: his hands, held up in front of this player's eyes ----
        // (Between attacks: his own idle and walk keep them at his sides, out of sight. In an attack his
        // own animation has them, swinging through the view as it does.) Each arm: its upper arm, its
        // forearm and its hand, bent by a two-bone solve towards a point before the camera.

        private struct Arm
        {
            public Transform Upper, Fore, Hand, Root;
            public float Side;
        }

        private static Arm[] arms;
        private static MinosPrime armsOf;
        private static float armWeight;
        private static readonly string[] NotHand = { "finger", "thumb", "index", "middle", "ring", "pinky", "little", "nail", "end", "tip" };

        private static Arm[] Arms()
        {
            if (armsOf == Body && arms != null)
            {
                return arms;
            }
            armsOf = Body;
            arms = new Arm[0];
            var found = new List<Arm>();
            var bones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones != null)
                {
                    foreach (Transform b in smr.bones)
                    {
                        if (b != null)
                        {
                            bones.Add(b);
                        }
                    }
                }
            }
            // Hands by name ("Hand", not a finger of one), the highest up of each side.
            var best = new Dictionary<int, (Transform T, int Depth)>();
            foreach (Transform b in bones)
            {
                string n = b.name.ToLowerInvariant();
                if (n.IndexOf("hand", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                bool finger = false;
                foreach (string x in NotHand)
                {
                    finger |= n.IndexOf(x, StringComparison.Ordinal) >= 0;
                }
                if (finger || b.parent == null || b.parent.parent == null)
                {
                    continue;
                }
                int side = Body.transform.InverseTransformPoint(b.position).x >= 0f ? 1 : -1;
                int depth = 0;
                for (Transform p = b; p != null && p != Body.transform; p = p.parent)
                {
                    depth++;
                }
                if (!best.TryGetValue(side, out var had) || depth < had.Depth)
                {
                    best[side] = (b, depth);
                }
            }
            foreach (var kv in best)
            {
                Transform hand = kv.Value.T;
                Transform fore = null;
                for (Transform p = hand.parent; p != null && p != Body.transform; p = p.parent)
                {
                    string pn = p.name.ToLowerInvariant();
                    if (pn.Contains("lowerarm") || pn.Contains("lower_arm") || pn.Contains("forearm") || pn.Contains("elbow"))
                    {
                        fore = p;
                        break;
                    }
                }
                fore = fore ?? hand.parent;
                Transform upper = fore.parent;
                if (upper == null)
                {
                    continue;
                }
                Transform root = upper;
                if (upper.parent != null)
                {
                    string un = upper.parent.name.ToLowerInvariant();
                    if (un.Contains("shoulder") || un.Contains("clavicle") || un.Contains("collar"))
                    {
                        root = upper.parent;
                    }
                }
                found.Add(new Arm { Hand = hand, Fore = fore, Upper = upper, Root = root, Side = kv.Key });
            }
            arms = found.ToArray();
            var told = new List<string>();
            foreach (Arm a in arms)
            {
                told.Add($"{(a.Side > 0 ? "right" : "left")}: {a.Upper.name} > {a.Fore.name} > {a.Hand.name}");
            }
            Plugin.Log.LogInfo($"minos: first-person hands: {(told.Count > 0 ? string.Join("; ", told) : "none found by name")}");
            return arms;
        }

        private static void HandsUp(Vector3 eye, Quaternion look)
        {
            float want = Body != null && !inActionOf(Body) && activatedOf(Body) ? 1f : 0f;
            // (Quickly out of the way for an attack; back up a moment after.)
            armWeight = Mathf.MoveTowards(armWeight, want, Time.deltaTime * (want > armWeight ? 4f : 12f));
            if (armWeight <= 0.001f)
            {
                return;
            }
            Vector3 f = look * Vector3.forward, r = look * Vector3.right, u = look * Vector3.up;
            foreach (Arm a in Arms())
            {
                if (a.Upper == null || a.Fore == null || a.Hand == null)
                {
                    continue;
                }
                float reach = Vector3.Distance(a.Upper.position, a.Fore.position) + Vector3.Distance(a.Fore.position, a.Hand.position);
                Vector3 target = eye + f * (reach * 0.85f) + r * (a.Side * reach * 0.42f) - u * (reach * 0.42f);
                Vector3 pole = eye + r * (a.Side * reach * 1.2f) - u * (reach * 1.2f) - f * (reach * 0.3f);
                Solve(a.Upper, a.Fore, a.Hand, target, pole, armWeight);
                // (The fist towards where he looks.)
                a.Hand.rotation = Quaternion.Slerp(a.Hand.rotation, Quaternion.FromToRotation(a.Hand.position - a.Fore.position, f) * a.Hand.rotation, armWeight * 0.6f);
            }
        }

        // First person: everything of his but his arms shrunk to points where it is, for this frame's
        // drawing only (put back at the frame's end, before anything moves or hits again). Each bone
        // keeps its place; only its own size goes, the arms' sizes made up for, so this player sees his
        // two arms and nothing of his chest, head or legs whichever way they look.
        private const float Shrunk = 0.001f;
        private static readonly List<(Transform T, Vector3 Pos, Vector3 Scale)> collapsed = new List<(Transform, Vector3, Vector3)>();
        private static readonly HashSet<Transform> armRoots = new HashSet<Transform>();
        private static Transform rigTop;
        private static MinosPrime rigOf;
        private static FrameEnd frameEnd;

        private static bool Collapse()
        {
            Arm[] a = Arms();
            if (a.Length == 0 || Body == null)
            {
                return false;
            }
            if (rigOf != Body || rigTop == null)
            {
                rigOf = Body;
                armRoots.Clear();
                var bones = new HashSet<Transform>();
                foreach (SkinnedMeshRenderer smr in Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.bones != null)
                    {
                        foreach (Transform b in smr.bones)
                        {
                            if (b != null)
                            {
                                bones.Add(b);
                            }
                        }
                    }
                }
                foreach (Arm arm in a)
                {
                    armRoots.Add(arm.Root != null ? arm.Root : arm.Upper);
                }
                rigTop = a[0].Root != null ? a[0].Root : a[0].Upper;
                while (rigTop.parent != null && rigTop.parent != Body.transform && bones.Contains(rigTop.parent))
                {
                    rigTop = rigTop.parent;
                }
                Plugin.Log.LogInfo($"minos: first person: all but his arms shrunk from {rigTop.name}");
            }
            if (frameEnd == null)
            {
                var go = new GameObject("Killcraft Minos Frame End");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                frameEnd = go.AddComponent<FrameEnd>();
            }
            LiveBounds(true);
            Uncollapse();
            Shrink(rigTop, 1f, false);
            return true;
        }

        // His skin's drawn or not by a box round his root bone; that bone shrunk, the box is a point at
        // his hips, out of view looking up: nothing of him drawn, the arms neither. In first person the
        // box is worked out from his pose each frame instead.
        private static readonly List<(SkinnedMeshRenderer R, bool Was)> liveBounds = new List<(SkinnedMeshRenderer, bool)>();

        private static void LiveBounds(bool on)
        {
            if (on)
            {
                if (liveBounds.Count > 0 || Body == null)
                {
                    return;
                }
                foreach (SkinnedMeshRenderer smr in Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    liveBounds.Add((smr, smr.updateWhenOffscreen));
                    smr.updateWhenOffscreen = true;
                }
                return;
            }
            foreach (var l in liveBounds)
            {
                if (l.R != null)
                {
                    l.R.updateWhenOffscreen = l.Was;
                }
            }
            liveBounds.Clear();
        }

        private static void Shrink(Transform b, float parentFactor, bool inArm)
        {
            inArm |= armRoots.Contains(b);
            float own = inArm ? 1f : Shrunk;
            if (own != 1f || parentFactor != 1f)
            {
                collapsed.Add((b, b.localPosition, b.localScale));
                b.localScale *= own / parentFactor;
                b.localPosition /= parentFactor;
            }
            if (inArm)
            {
                return;  // (below an arm's root nothing changes)
            }
            for (int i = 0; i < b.childCount; i++)
            {
                Shrink(b.GetChild(i), own, false);
            }
        }

        private static void Uncollapse()
        {
            for (int i = collapsed.Count - 1; i >= 0; i--)
            {
                var c = collapsed[i];
                if (c.T != null)
                {
                    c.T.localPosition = c.Pos;
                    c.T.localScale = c.Scale;
                }
            }
            collapsed.Clear();
        }

        private sealed class FrameEnd : MonoBehaviour
        {
            private System.Collections.IEnumerator Start()
            {
                var end = new WaitForEndOfFrame();
                while (true)
                {
                    yield return end;
                    Uncollapse();
                }
            }
        }

        // Two bones (upper arm and forearm) turned so the hand reaches the target, the elbow bending
        // towards the pole; weight: how much of the way from his own pose.
        private static void Solve(Transform upper, Transform fore, Transform hand, Vector3 target, Vector3 pole, float weight)
        {
            Vector3 a = upper.position, b = fore.position, c = hand.position;
            float la = (b - a).magnitude, lb = (c - b).magnitude;
            if (la < 1e-4f || lb < 1e-4f)
            {
                return;
            }
            Vector3 toT = target - a;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-3f, (la + lb) * 0.999f);
            Vector3 dir = toT.sqrMagnitude > 1e-6f ? toT.normalized : (c - a).normalized;
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
            float angA = Mathf.Acos(cosA);
            Vector3 bend = Vector3.ProjectOnPlane(pole - a, dir);
            bend = bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            Vector3 elbow = a + (dir * Mathf.Cos(angA) + bend * Mathf.Sin(angA)) * la;
            Quaternion upperTo = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            upper.rotation = Quaternion.Slerp(upper.rotation, upperTo, weight);
            b = fore.position;
            c = hand.position;
            Vector3 reachTo = a + dir * d;
            Quaternion foreTo = Quaternion.FromToRotation(c - b, reachTo - b) * fore.rotation;
            fore.rotation = Quaternion.Slerp(fore.rotation, foreTo, weight);
        }

        // His head's bone (the one of his skin's bones named for it, highest up): shrunk to nothing in
        // first person, so this player sees from it, his arms and body below.
        private static Transform headBone;
        private static Vector3 headScale = Vector3.one;
        private static bool headHidden;
        private static MinosPrime headOf;

        private static Transform HeadBone()
        {
            if (headOf == Body && headBone != null)
            {
                return headBone;
            }
            ShowHead();
            headOf = Body;
            headBone = null;
            int bestDepth = int.MaxValue;
            foreach (SkinnedMeshRenderer smr in Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null)
                {
                    continue;
                }
                foreach (Transform b in smr.bones)
                {
                    if (b == null || b.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    int depth = 0;
                    for (Transform p = b; p != null && p != Body.transform; p = p.parent)
                    {
                        depth++;
                    }
                    if (depth < bestDepth)
                    {
                        bestDepth = depth;
                        headBone = b;
                    }
                }
            }
            if (headBone != null)
            {
                headScale = headBone.localScale;
            }
            Plugin.Log.LogInfo($"minos: first person from {(headBone != null ? headBone.name : "nothing: his body hidden instead")}");
            return headBone;
        }

        private static void ShowHead()
        {
            if (headHidden && headBone != null)
            {
                headBone.localScale = headScale;
            }
            headHidden = false;
        }

        // His lights (his glow, his rage's fire), dimmed on his player's own screen: right next to the
        // camera they were all this player saw.
        private const float DimTo = 0.3f;
        private static readonly Dictionary<Light, float> dimmed = new Dictionary<Light, float>();
        private static float dimAt = -100f;

        private static void Dim()
        {
            if (Time.unscaledTime - dimAt < 1f || Body == null)
            {
                return;
            }
            dimAt = Time.unscaledTime;
            foreach (Light l in Body.GetComponentsInChildren<Light>(true))
            {
                if (l != null && !dimmed.ContainsKey(l))
                {
                    dimmed[l] = l.intensity;
                    l.intensity *= DimTo;
                }
            }
        }

        private static void Undim()
        {
            foreach (var kv in dimmed)
            {
                if (kv.Key != null)
                {
                    kv.Key.intensity = kv.Value;
                }
            }
            dimmed.Clear();
            dimAt = -100f;
        }

        private static void ShowBody()
        {
            foreach (var h in hidden)
            {
                if (h.R != null)
                {
                    h.R.shadowCastingMode = h.Was;
                }
            }
            hidden.Clear();
        }

        // Where his head is (his voice, his name: RemotePlayers).
        public static bool HeadOf(out Vector3 at)
        {
            at = Vector3.zero;
            if (Body == null)
            {
                return false;
            }
            EnemyIdentifier eid = eidOf(Body);
            at = eid != null && eid.weakPoint != null ? eid.weakPoint.transform.position : Body.transform.position + Vector3.up * 5f;
            return true;
        }

        public static bool Dead
        {
            get
            {
                EnemyIdentifier eid = Body != null ? eidOf(Body) : null;
                return eid != null && eid.dead;
            }
        }

        public static EnemyIdentifier Eid => Body != null ? eidOf(Body) : null;
    }
}
