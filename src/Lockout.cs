using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Killcraft
{
    // While Minecraft has the player, ULTRAKILL's own controls are off: its movement, weapon, arm and
    // HUD key bindings (but not mouse-look) and every cheat keybind (teleport menu, ...), so keys meant for Minecraft (the
    // hotbar, E, Q, ...) don't also do ULTRAKILL things. Its pause menu stays. ULTRAKILL's scripts
    // also bring the guns and the arm back now and then (level events, cheats); they're put away
    // again every frame (the dual wield power-up's guns too). Everything is switched back on when
    // ULTRAKILL gets the player back.
    internal static class Lockout
    {
        private static bool locked;
        private static readonly List<InputAction> disabled = new List<InputAction>();

        public static void Frame(bool minecraftHasPlayer)
        {
            if (minecraftHasPlayer)
            {
                Lock();
            }
            else if (locked)
            {
                Unlock();
            }
        }

        private static void Lock()
        {
            locked = true;
            if (MonoSingleton.GetInstance(typeof(InputManager)) is InputManager input && input.InputSource?.Actions != null)
            {
                var actions = input.InputSource.Actions;
                foreach (InputActionMap map in new InputActionMap[] { actions.Movement, actions.Fist, actions.Weapon, actions.HUD })
                {
                    if (map == null)
                    {
                        continue;
                    }
                    // Mouse-look is in the Movement map and still turns the camera, so it stays on. So
                    // does fire: it's also how ULTRAKILL's terminals and shops (SMILEOS) are clicked,
                    // and the guns it would fire are put away.
                    foreach (InputAction action in map.actions)
                    {
                        if (action.enabled && action.name != "Look" && action != actions.Weapon.PrimaryFire)
                        {
                            action.Disable();
                            disabled.Add(action);
                        }
                    }
                }
            }
            if (MonoSingleton.GetInstance(typeof(CheatBinds)) is CheatBinds binds && binds.registeredCheatBinds != null)
            {
                foreach (InputActionState state in binds.registeredCheatBinds.Values)
                {
                    InputAction action = state?.Action;
                    if (action != null && action.enabled)
                    {
                        action.Disable();
                        disabled.Add(action);
                    }
                }
            }
            if (MonoSingleton.GetInstance(typeof(GunControl)) is GunControl guns && guns.currentWeapon != null && guns.currentWeapon.activeSelf)
            {
                guns.NoWeapon();
            }
            if (MonoSingleton.GetInstance(typeof(FistControl)) is FistControl fists)
            {
                if (fists.activated)
                {
                    fists.NoFist();
                }
                // Some things bring an arm out without equipping it (SMILEOS terminals' tap
                // animation, an arm refreshed for a parry): every arm stays put away.
                foreach (UnityEngine.GameObject arm in spawnedArms(fists))
                {
                    if (arm != null && arm.activeSelf)
                    {
                        arm.SetActive(false);
                    }
                }
            }
            // The dual wield power-up's second gun is its own object, which NoWeapon leaves out.
            dualCheck -= UnityEngine.Time.unscaledDeltaTime;
            if (dualCheck <= 0f)
            {
                dualCheck = 0.25f;
                foreach (DualWield dual in UnityEngine.Object.FindObjectsOfType<DualWield>())
                {
                    dual.gameObject.SetActive(false);
                    hiddenDuals.Add(dual.gameObject);
                }
            }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<FistControl, List<UnityEngine.GameObject>> spawnedArms =
            HarmonyLib.AccessTools.FieldRefAccess<FistControl, List<UnityEngine.GameObject>>("spawnedArms");
        private static float dualCheck;
        private static readonly List<UnityEngine.GameObject> hiddenDuals = new List<UnityEngine.GameObject>();

        private static void Unlock()
        {
            locked = false;
            foreach (InputAction action in disabled)
            {
                action?.Enable();
            }
            disabled.Clear();
            foreach (UnityEngine.GameObject dual in hiddenDuals)
            {
                if (dual != null)
                {
                    dual.SetActive(true);
                }
            }
            hiddenDuals.Clear();
        }
    }
}
