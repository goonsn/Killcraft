using System;
using UnityEngine.InputSystem;

namespace Killcraft
{
    // A key from the settings by its name: a Unity Input System key (G, Tab, CapsLock, LeftAlt, ...) or a
    // mouse button (Mouse3: the wheel's, Mouse4 / Mouse5: the side ones). Held now, or pressed this frame.
    internal static class InputKeys
    {
        public static bool Held(string name) => Read(name, false);

        public static bool Pressed(string name) => Read(name, true);

        private static bool Read(string name, bool down)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
            {
                return false;
            }
            Mouse mouse = Mouse.current;
            if (name.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
            {
                if (mouse == null)
                {
                    return false;
                }
                UnityEngine.InputSystem.Controls.ButtonControl b =
                    name.Equals("Mouse3", StringComparison.OrdinalIgnoreCase) ? mouse.middleButton
                    : name.Equals("Mouse4", StringComparison.OrdinalIgnoreCase) ? mouse.backButton
                    : name.Equals("Mouse5", StringComparison.OrdinalIgnoreCase) ? mouse.forwardButton
                    : null;
                return b != null && (down ? b.wasPressedThisFrame : b.isPressed);
            }
            Keyboard kb = Keyboard.current;
            if (kb == null || !Enum.TryParse(name, true, out Key key) || key == Key.None)
            {
                return false;
            }
            try
            {
                return down ? kb[key].wasPressedThisFrame : kb[key].isPressed;
            }
            catch
            {
                return false;
            }
        }
    }
}
