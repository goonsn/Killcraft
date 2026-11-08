using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // Minecraft's chat while ULTRAKILL has V1 (F9): Minecraft's screen isn't shown then, so Killcraft's
    // mod sends each line here (KillNetClient.chatLine) and the latest few show at the bottom left,
    // fading after a while, as Minecraft's own chat does. Reading only (typing is Minecraft mode's).
    internal static class ChatLines
    {
        public const byte Line = 201;
        private const int Shown = 8;
        private const float Stay = 10f, Fade = 1f;

        private static readonly List<(string Text, float At)> lines = new List<(string, float)>();
        private static GUIStyle style;

        // Killcraft's own notices: the party's chat shows them.
        public static void Add(string text)
        {
            PartyChat.System(Typing.Escape(text));
            Keep(text);
        }

        // A line of Minecraft's chat.
        public static void AddFromMinecraft(string text)
        {
            PartyChat.FromMinecraft(text);
            Keep(text);
        }

        private static void Keep(string text)
        {
            lines.Add((text, Time.unscaledTime));
            if (lines.Count > 50)
            {
                lines.RemoveAt(0);
            }
        }

        // From the host's OnGUI.
        public static void Draw(bool show)
        {
            if (!show || lines.Count == 0)
            {
                return;
            }
            style ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 50), wordWrap = true };
            float lineHeight = style.fontSize * 1.4f;
            float width = Mathf.Min(Screen.width * 0.45f, 900f);
            float y = Screen.height - Screen.height * 0.22f;
            int drawn = 0;
            for (int i = lines.Count - 1; i >= 0 && drawn < Shown; i--)
            {
                float age = Time.unscaledTime - lines[i].At;
                if (age > Stay + Fade)
                {
                    break;
                }
                float alpha = age < Stay ? 1f : 1f - (age - Stay) / Fade;
                y -= lineHeight;
                var r = new Rect(16f, y, width, lineHeight);
                GUI.color = new Color(0f, 0f, 0f, alpha * 0.45f);
                GUI.DrawTexture(new Rect(r.x - 4f, r.y, r.width + 8f, r.height), Texture2D.whiteTexture);
                GUI.color = new Color(0f, 0f, 0f, alpha);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), lines[i].Text, style);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(r, lines[i].Text, style);
                drawn++;
            }
            GUI.color = Color.white;
        }
    }
}
