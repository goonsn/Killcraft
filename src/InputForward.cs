using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Killcraft
{
    // ULTRAKILL has the OS focus; its keyboard and mouse are replayed into the hidden Minecraft as
    // SDL scancodes (what SkyCraft's InputBridge expects). Esc stays ULTRAKILL's pause menu; O opens
    // Minecraft's own menu. Mouse look stays ULTRAKILL's camera (sent as the authoritative look).
    internal static class InputForward
    {
        // When the attack button was last pressed in the world (unscaled time; -1: never).
        public static float LastAttackPress = -1f;
        private static bool routing;
        private static bool wasScreenOpen;
        private static Vector2 cursor;
        private static bool textHooked;
        private static bool screenOpenNow;
        private static readonly Dictionary<Key, ushort> table = BuildTable();

        public static Vector2 Cursor => cursor;

        public static void Frame(bool route, bool screenOpen, int width, int height)
        {
            screenOpenNow = screenOpen;
            if (!route)
            {
                if (routing)
                {
                    Link.PushInput(Proto.InReleaseAll);
                    routing = false;
                }
                wasScreenOpen = false;
                return;
            }
            routing = true;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (keyboard != null && !textHooked)
            {
                keyboard.onTextInput += OnText;
                textHooked = true;
            }
            if (screenOpen && !wasScreenOpen)
            {
                cursor = new Vector2(width / 2f, height / 2f);
            }
            wasScreenOpen = screenOpen;

            if (keyboard != null)
            {
                foreach (KeyControl k in keyboard.allKeys)
                {
                    if (k == null)
                    {
                        continue;
                    }
                    bool down = k.wasPressedThisFrame;
                    bool up = k.wasReleasedThisFrame;
                    if (!down && !up)
                    {
                        continue;
                    }
                    Key key = k.keyCode;
                    // Minecraft's third person (F5): ULTRAKILL's camera stays in V1's head and doesn't
                    // draw Minecraft's player, so all that would change is the hand disappearing.
                    if (key == Key.F5)
                    {
                        continue;
                    }
                    if (!screenOpen)
                    {
                        if (key == Key.Escape)
                        {
                            continue;
                        }
                        if (key == Key.O)
                        {
                            if (down)
                            {
                                Link.PushInput(Proto.InReleaseAll);
                                Link.PushInput(Proto.InOpenMenu);
                            }
                            continue;
                        }
                    }
                    if (table.TryGetValue(key, out ushort sdl))
                    {
                        // A tap can start and end inside one frame: send both.
                        if (down)
                        {
                            Link.PushInput(Proto.InKey, sdl, 1);
                        }
                        if (up)
                        {
                            Link.PushInput(Proto.InKey, sdl, 0);
                        }
                    }
                }
            }

            if (mouse != null)
            {
                if (!screenOpen && mouse.leftButton.wasPressedThisFrame)
                {
                    LastAttackPress = Time.unscaledTime;
                    Combat.SwingParry();
                }
                Button(mouse.leftButton, 1);
                Button(mouse.rightButton, 3);
                Button(mouse.middleButton, 2);
                Button(mouse.backButton, 4);
                Button(mouse.forwardButton, 5);
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    // Raw wheel deltas are 120 per notch; some Input System versions report notches.
                    int a = Mathf.Abs(scroll) < 10f ? Mathf.RoundToInt(scroll * 120f) : Mathf.RoundToInt(scroll);
                    Link.PushInput(Proto.InScroll, 0, a);
                }
                if (screenOpen)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    if (d != Vector2.zero)
                    {
                        cursor.x = Mathf.Clamp(cursor.x + d.x, 0, width - 1);
                        cursor.y = Mathf.Clamp(cursor.y - d.y, 0, height - 1);
                        Link.PushInput(Proto.InCursor, 0, (int)cursor.x, (int)cursor.y);
                    }
                }
            }
        }

        private static void Button(ButtonControl button, ushort sdl)
        {
            if (button == null)
            {
                return;
            }
            if (button.wasPressedThisFrame)
            {
                Link.PushInput(Proto.InMouseButton, sdl, 1);
            }
            if (button.wasReleasedThisFrame)
            {
                Link.PushInput(Proto.InMouseButton, sdl, 0);
            }
        }

        private static void OnText(char c)
        {
            if (routing && screenOpenNow && !char.IsControl(c))
            {
                Link.PushInput(Proto.InText, 0, c);
            }
        }

        private static Dictionary<Key, ushort> BuildTable()
        {
            var t = new Dictionary<Key, ushort>();
            for (Key k = Key.A; k <= Key.Z; k++)
            {
                t[k] = (ushort)(4 + (k - Key.A));
            }
            for (Key k = Key.Digit1; k <= Key.Digit0; k++)
            {
                t[k] = (ushort)(30 + (k - Key.Digit1));
            }
            for (Key k = Key.F1; k <= Key.F12; k++)
            {
                t[k] = (ushort)(58 + (k - Key.F1));
            }
            t[Key.Enter] = 40; t[Key.Escape] = 41; t[Key.Backspace] = 42; t[Key.Tab] = 43; t[Key.Space] = 44;
            t[Key.Minus] = 45; t[Key.Equals] = 46; t[Key.LeftBracket] = 47; t[Key.RightBracket] = 48; t[Key.Backslash] = 49;
            t[Key.Semicolon] = 51; t[Key.Quote] = 52; t[Key.Backquote] = 53; t[Key.Comma] = 54; t[Key.Period] = 55; t[Key.Slash] = 56;
            t[Key.CapsLock] = 57; t[Key.PrintScreen] = 70; t[Key.ScrollLock] = 71; t[Key.Pause] = 72; t[Key.Insert] = 73;
            t[Key.Home] = 74; t[Key.PageUp] = 75; t[Key.Delete] = 76; t[Key.End] = 77; t[Key.PageDown] = 78;
            t[Key.RightArrow] = 79; t[Key.LeftArrow] = 80; t[Key.DownArrow] = 81; t[Key.UpArrow] = 82;
            t[Key.NumLock] = 83; t[Key.NumpadDivide] = 84; t[Key.NumpadMultiply] = 85; t[Key.NumpadMinus] = 86;
            t[Key.NumpadPlus] = 87; t[Key.NumpadEnter] = 88; t[Key.Numpad1] = 89; t[Key.Numpad2] = 90; t[Key.Numpad3] = 91;
            t[Key.Numpad4] = 92; t[Key.Numpad5] = 93; t[Key.Numpad6] = 94; t[Key.Numpad7] = 95; t[Key.Numpad8] = 96;
            t[Key.Numpad9] = 97; t[Key.Numpad0] = 98; t[Key.NumpadPeriod] = 99; t[Key.OEM1] = 100; t[Key.ContextMenu] = 101;
            t[Key.NumpadEquals] = 103; t[Key.LeftCtrl] = 224; t[Key.LeftShift] = 225; t[Key.LeftAlt] = 226; t[Key.LeftMeta] = 227;
            t[Key.RightCtrl] = 228; t[Key.RightShift] = 229; t[Key.RightAlt] = 230; t[Key.RightMeta] = 231;
            return t;
        }
    }
}
