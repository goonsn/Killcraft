using BepInEx.Configuration;
using UnityEngine;

namespace Killcraft
{
    // Killcraft's own options in the game: with a level paused, a panel on the left (the shaders' is on
    // the right). The same settings as in Killcraft's config file; most count at once, the ones
    // Minecraft reads as it starts (marked) from the next time it does.
    internal static class KillcraftOptions
    {
        private sealed class Toggle
        {
            public ConfigEntry<bool> Entry;
            public string Name;
            public bool NextStart;
        }

        private sealed class Slider
        {
            public ConfigEntry<float> Entry;
            public ConfigEntry<int> IntEntry;
            public string Name;
            public float Min, Max, Step;
            public bool NextStart;
            public float Value;
        }

        private static Toggle[] toggles;
        private static Slider[] sliders;
        private static GUIStyle title, label, note, check;
        private static Texture2D back;
        private static bool open;

        private static void Make()
        {
            toggles = new[]
            {
                new Toggle { Entry = Plugin.PvP, Name = "PvP (players hurt each other; the host's counts)" },
                new Toggle { Entry = Plugin.DamageByDifficulty, Name = "Enemies hit harder on harder difficulties" },
                new Toggle { Entry = Plugin.BossesPierceArmour, Name = "Bosses' hits go through armour" },
                new Toggle { Entry = Plugin.NetherMiniBosses, Name = "Mini-bosses in the Nether" },
                new Toggle { Entry = Plugin.MobsFightEnemies, Name = "Minecraft mobs fight enemies", NextStart = true },
                new Toggle { Entry = Plugin.MobsAttackYouInLevels, Name = "Minecraft mobs attack you in levels", NextStart = true },
                new Toggle { Entry = Plugin.AlwaysThorns, Name = "Thorns always hits back", NextStart = true },
                new Toggle { Entry = Plugin.DigIntoLevels, Name = "Dig into ULTRAKILL's levels", NextStart = true },
                new Toggle { Entry = Plugin.FreshWorld, Name = "Fresh Minecraft world each launch", NextStart = true },
            };
            sliders = new[]
            {
                new Slider { Entry = Plugin.PvpDamage, Name = "PvP damage (the host's counts)", Min = 0.1f, Max = 3f, Step = 0.05f },
                new Slider { Entry = Plugin.BlockBrightness, Name = "Block brightness", Min = 0.05f, Max = 2f, Step = 0.05f },
                new Slider { Entry = Plugin.DamageToUltrakill, Name = "Your damage to enemies", Min = 0.05f, Max = 2f, Step = 0.05f },
                new Slider { Entry = Plugin.DamageToMinecraft, Name = "Enemies' damage to you", Min = 0.1f, Max = 3f, Step = 0.05f },
                new Slider { Entry = Plugin.BossDamage, Name = "Bosses' extra damage", Min = 0.5f, Max = 3f, Step = 0.05f },
                new Slider { IntEntry = Plugin.NetherEnemies, Name = "Enemies at a time in the Nether", Min = 0f, Max = 20f, Step = 1f },
                new Slider { IntEntry = Plugin.TntFuseTicks, Name = "TNT fuse (ticks)", Min = 10f, Max = 80f, Step = 1f, NextStart = true },
            };
        }

        public static void Gui()
        {
            if (!ShaderPack.PanelsShown)
            {
                open = false;
                return;
            }
            if (toggles == null)
            {
                Make();
            }
            if (!open)
            {
                open = true;
                foreach (Slider s in sliders)
                {
                    s.Value = s.Entry != null ? s.Entry.Value : s.IntEntry.Value;
                }
            }
            title ??= new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            label ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            note ??= new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            check ??= new GUIStyle(GUI.skin.toggle) { fontSize = 14 };
            if (back == null)
            {
                back = new Texture2D(1, 1);
                back.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
                back.Apply();
            }
            const float rowT = 26f, rowS = 40f;
            float w = 340f, x = 20f, y = 60f;
            float height = 36f + toggles.Length * rowT + 8f + sliders.Length * rowS + 40f;
            GUI.DrawTexture(new Rect(x - 10f, y - 10f, w + 20f, height + 10f), back);
            GUI.Label(new Rect(x, y, w, 30f), Brand.Title, title);
            y += 36f;
            foreach (Toggle t in toggles)
            {
                bool now = GUI.Toggle(new Rect(x, y, w, 24f), t.Entry.Value, " " + t.Name + (t.NextStart ? " *" : ""), check);
                if (now != t.Entry.Value)
                {
                    t.Entry.Value = now;
                    Plugin.Log.LogInfo($"options: {t.Entry.Definition.Key} {(now ? "on" : "off")}");
                }
                y += rowT;
            }
            y += 8f;
            foreach (Slider s in sliders)
            {
                string shown = s.Step >= 1f ? $"{s.Value:0}" : $"{s.Value:0.00}";
                GUI.Label(new Rect(x, y, w, 20f), $"{s.Name}{(s.NextStart ? " *" : "")}: {shown}", label);
                float v = GUI.HorizontalSlider(new Rect(x, y + 20f, w, 16f), s.Value, s.Min, s.Max);
                v = Mathf.Round(v / s.Step) * s.Step;
                if (Mathf.Abs(v - s.Value) > s.Step * 0.5f)
                {
                    s.Value = v;
                    if (s.Entry != null)
                    {
                        s.Entry.Value = v;
                    }
                    else
                    {
                        s.IntEntry.Value = Mathf.RoundToInt(v);
                    }
                    if (s.Entry == Plugin.BlockBrightness)
                    {
                        WorldRender.BrightnessChanged();
                    }
                }
                y += rowS;
            }
            GUI.Label(new Rect(x, y + 2f, w, 36f), "* from the next time Minecraft starts. The rest count at once.", note);
        }
    }
}
