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

        [HarmonyPatch(typeof(NewMovement), "Update"), HarmonyPrefix]
        private static bool MovementUpdate() => !OwnsPlayer;

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
            __instance.FakeHurt();
            return false;
        }
    }
}
