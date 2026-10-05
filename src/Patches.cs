using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // While Minecraft drives the player, ULTRAKILL's own movement, weapons, arms and hook stay off
    // (SkyCraft does the same to Skyrim's player controls), and damage to V1 goes to Minecraft.
    [HarmonyPatch]
    internal static class Patches
    {
        public static bool OwnsPlayer;
        public static bool McScreenOpen;
        public static bool AllowDamage;

        private static readonly AccessTools.FieldRef<NewMovement, Color> hurtTint = AccessTools.FieldRefAccess<NewMovement, Color>("currentColor");

        [HarmonyPatch(typeof(NewMovement), "Update"), HarmonyPrefix]
        private static bool MovementUpdate(NewMovement __instance)
        {
            if (!OwnsPlayer)
            {
                return true;
            }
            // ULTRAKILL's red hurt tint fades out in this Update, which doesn't run while Minecraft
            // has the player: fade it here (else a death just before Minecraft took over stays red).
            ref Color tint = ref hurtTint(__instance);
            if (tint.a > 0f)
            {
                tint.a = Mathf.Max(0f, tint.a - Time.deltaTime);
                Shader.SetGlobalColor("_HurtScreenColor", tint);
            }
            return false;
        }

        [HarmonyPatch(typeof(NewMovement), "FixedUpdate"), HarmonyPrefix]
        private static bool MovementFixedUpdate() => !OwnsPlayer;

        [HarmonyPatch(typeof(GunControl), "Update"), HarmonyPrefix]
        private static bool GunUpdate() => !OwnsPlayer;

        [HarmonyPatch(typeof(FistControl), "Update"), HarmonyPrefix]
        private static bool FistUpdate() => !OwnsPlayer;

        [HarmonyPatch(typeof(HookArm), "Update"), HarmonyPrefix]
        private static bool HookUpdate() => !OwnsPlayer;

        [HarmonyPatch(typeof(WeaponWheel), "Update"), HarmonyPrefix]
        private static bool WheelUpdate() => !OwnsPlayer;

        // The cheat menu (Home / ~) and the cheat code: not while Minecraft has the player.
        [HarmonyPatch(typeof(CheatsController), "Update"), HarmonyPrefix]
        private static bool CheatsUpdate() => !OwnsPlayer;

        // Esc closes an open Minecraft screen instead of pausing ULTRAKILL.
        [HarmonyPatch(typeof(OptionsManager), "Update"), HarmonyPrefix]
        private static bool OptionsUpdate() => !(OwnsPlayer && McScreenOpen);

        // With a Minecraft screen open the mouse moves Minecraft's cursor, not the view.
        [HarmonyPatch(typeof(CameraController), "LateUpdate"), HarmonyPrefix]
        private static void CameraBefore(CameraController __instance, out Vector2 __state)
        {
            __state = new Vector2(__instance.rotationX, __instance.rotationY);
        }

        [HarmonyPatch(typeof(CameraController), "LateUpdate"), HarmonyPostfix]
        private static void CameraAfter(CameraController __instance, Vector2 __state)
        {
            if (OwnsPlayer && McScreenOpen)
            {
                __instance.rotationX = __state.x;
                __instance.rotationY = __state.y;
                __instance.ApplyRotations();
            }
        }

        // Who is hitting V1 right now: set around ULTRAKILL's enemy melee and projectile hits.
        private static ushort hitKind = Proto.HurtOther;
        private static EnemyIdentifier hitEnemy;
        private static Vector3 hitFrom, hitDir;

        private static void ClearHit()
        {
            hitKind = Proto.HurtOther;
            hitEnemy = null;
        }

        [HarmonyPatch(typeof(SwingCheck2), "CheckCollision"), HarmonyPrefix]
        private static void MeleeBefore(SwingCheck2 __instance)
        {
            hitKind = Proto.HurtMelee;
            hitEnemy = __instance.eid;
            hitFrom = __instance.transform.position;
            hitDir = Vector3.zero;
        }

        [HarmonyPatch(typeof(SwingCheck2), "CheckCollision"), HarmonyFinalizer]
        private static System.Exception MeleeAfter(System.Exception __exception)
        {
            ClearHit();
            return __exception;
        }

        [HarmonyPatch(typeof(Projectile), "Collided"), HarmonyPrefix]
        private static void ProjectileBefore(Projectile __instance)
        {
            hitKind = Proto.HurtProjectile;
            hitEnemy = null;
            hitFrom = __instance.transform.position;
            var rb = __instance.GetComponent<Rigidbody>();
            hitDir = rb != null && rb.velocity.sqrMagnitude > 0.01f ? rb.velocity.normalized : __instance.transform.forward;
        }

        [HarmonyPatch(typeof(Projectile), "Collided"), HarmonyFinalizer]
        private static System.Exception ProjectileAfter(System.Exception __exception)
        {
            ClearHit();
            return __exception;
        }

        // Enemy attacks that hurt V1 by other paths than a melee swing or a Projectile. Each sets who
        // the hit is from, so it's a mob's hit in Minecraft (a shield blocks it), not an unblockable one.
        private static void Attack(ushort kind, Component from, Vector3 dir)
        {
            hitKind = kind;
            hitEnemy = from.GetComponentInParent<EnemyIdentifier>();
            hitFrom = from.transform.position;
            hitDir = dir;
        }

        // (A method an ULTRAKILL update renamed is skipped, not a failure of all of Killcraft's patches.)
        private static IEnumerable<MethodBase> Targets(params (System.Type Type, string Method)[] targets)
        {
            var found = new List<MethodBase>();
            foreach (var (type, name) in targets)
            {
                MethodInfo m = null;
                try
                {
                    m = AccessTools.Method(type, name);
                }
                catch (System.Exception)
                {
                }
                if (m != null)
                {
                    found.Add(m);
                }
                else
                {
                    Plugin.Log.LogWarning($"no {type.Name}.{name} to patch: those hits stay unblockable");
                }
            }
            return found;
        }

        [HarmonyPatch]
        private static class RangedAttacks
        {
            private static IEnumerable<MethodBase> TargetMethods() => Targets(
                (typeof(Projectile), "TimeToDie"),              // energy balls etc. hit a moment after touching V1
                (typeof(RevolverBeam), "ExecuteHits"),          // V2's and others' revolver beams
                (typeof(Coin), "ShootAtPlayer"),                // V2's coin shots
                (typeof(ThrownSword), "RecheckPlayerHit"),      // Gabriel's thrown swords
                (typeof(MassSpear), "DelayedPlayerCheck"),      // the Hideous Mass's spear
                (typeof(ContinuousBeam), "FixedUpdate"),        // lasers
                (typeof(BeamgunBeam), "Update"));

            private static void Prefix(Component __instance) => Attack(Proto.HurtProjectile, __instance, __instance.transform.forward);

            private static System.Exception Finalizer(System.Exception __exception)
            {
                ClearHit();
                return __exception;
            }
        }

        [HarmonyPatch]
        private static class MeleeAttacks
        {
            private static IEnumerable<MethodBase> TargetMethods() => Targets(
                (typeof(MinosPrime), "AirRaycastAttack"),
                (typeof(SisyphusPrime), "DropAttackActivate"));

            private static void Prefix(Component __instance) => Attack(Proto.HurtMelee, __instance, Vector3.zero);

            private static System.Exception Finalizer(System.Exception __exception)
            {
                ClearHit();
                return __exception;
            }
        }

        // Minecraft's health is the real one: ULTRAKILL's hits are sent to Minecraft, which applies
        // them with armour, shields, i-frames and knockback (and divides by 5: 100 health vs 20).
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt)), HarmonyPrefix]
        private static bool GetHurt(NewMovement __instance, int damage, bool explosion)
        {
            if (!OwnsPlayer || AllowDamage)
            {
                return true;
            }
            if (damage <= 0 || __instance.dead)
            {
                return false;
            }
            int scaled = Mathf.RoundToInt(damage * 100f * Plugin.DamageToMinecraft.Value);
            // A known attacker makes it a mob hit in Minecraft, which a raised shield blocks;
            // hazards (lava, pits, hurt zones) stay unblockable.
            uint attacker = hitKind != Proto.HurtOther ? Combat.AttackerFor(hitEnemy, hitFrom, hitDir) : 0;
            ushort kind = attacker != 0 ? hitKind : explosion ? Proto.HurtMagic : Proto.HurtOther;
            Link.PushInput(Proto.InHurt, kind, scaled, (int)attacker, 0);
            Combat.HurtBy(attacker);
            __instance.FakeHurt();
            return false;
        }
    }
}
