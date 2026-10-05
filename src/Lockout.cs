using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Killcraft
{
    // While Minecraft has the player, ULTRAKILL's own controls are off: its movement, weapon, arm and
    // HUD key bindings (but not mouse-look) and every cheat keybind (teleport menu, ...), so keys meant for Minecraft (the
    // hotbar, E, Q, ...) don't also do ULTRAKILL things. Its pause menu stays. ULTRAKILL's scripts
    // also bring the guns and the arm back now and then (level events, cheats); they're put away
    // again every frame. Everything is switched back on when ULTRAKILL gets the player back.
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
                    // Mouse-look is in the Movement map and still turns the camera, so it stays on.
                    foreach (InputAction action in map.actions)
                    {
                        if (action.enabled && action.name != "Look")
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
            if (MonoSingleton.GetInstance(typeof(FistControl)) is FistControl fists && fists.activated)
            {
                fists.NoFist();
            }
        }

        private static void Unlock()
        {
            locked = false;
            foreach (InputAction action in disabled)
            {
                action?.Enable();
            }
            disabled.Clear();
        }
    }
}
