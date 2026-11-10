using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // WAKE (UltraLink's): the party host picks a level from the main menu, and instead of going at once
    // everyone's party screen counts down together - a big 3 (each number in big and shrinking), the
    // screen's frame pale red and ULTRAKILL's main menu fall seen behind; at 2 the number goes small to the corner and the level comes in (its number, its name, its picture), its details
    // typed out after; at 0 everyone loads it. Each squad row says WAIT FOR WAKE until that player's
    // game has the wake (. . . OK). ABORT WAKE: the host's stops it for everyone, anyone else's only for
    // themselves (they stay).
    internal static class PartyWake
    {
        public const byte WakeMessage = 51;
        private const byte KindStart = 0, KindReady = 1, KindAbort = 2;
        private const float Length = 3f;

        public static bool Active { get; private set; }
        private static string scene = "";
        private static bool mine;
        private static float startedAt;
        private static readonly HashSet<int> ready = new HashSet<int>();

        // The panel over the party screen's left side.
        private static RectTransform panel, info;
        private static CanvasGroup infoGroup;
        private static Image dim;
        private static TextMeshProUGUI digit, number, levelName, details;
        private static Image picture, pictureFrame, line;
        private static string detailsText = "";
        private static int detailsLetters;

        public static bool Ready(int id) => ready.Contains(id);

        // SELECT LEVEL on the party screen: the level picked next is the party's (a WAKE). Forgotten
        // once out of the level select (the main menu's first screen again) or out of the menu.
        public static bool ForParty { get; private set; }
        private static float forPartyAt;

        public static void PickingForParty()
        {
            ForParty = true;
            forPartyAt = Time.unscaledTime;
        }

        // (The level this wake just went to: the old-style "going" that follows it isn't a second trip.)
        private static string loaded = "";
        private static float loadedAt = -100f;

        public static bool JustLoaded(string level) => level == loaded && Time.unscaledTime - loadedAt < 15f;

        // ---- starting, stopping ----

        // The host's pick (Party's LoadScene patch): a wake instead of going now.
        public static void Begin(string level)
        {
            ForParty = false;
            Plugin.Log.LogInfo($"wake: taking the party to {level} in {Length:0} s");
            KillNet.Send(WakeMessage, KillNet.ToEveryone, w =>
            {
                w.Write(KindStart);
                w.Write(level);
            });
            Start(level, true);
        }

        private static void Start(string level, bool host)
        {
            scene = level;
            mine = host;
            startedAt = Time.unscaledTime;
            ready.Clear();
            ready.Add(Room.MyId);
            Active = true;
            detailsLetters = -1;
            LoadingScreen.StartFilming();
            PartyChat.Event("<color=#DB5447>WAKE INITIATED</color>");
            PartyMenu.OpenForWake();
            try
            {
                Build();
                Fill();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"wake: couldn't show it ({e}); loading as it ends");
            }
        }

        public static void Handle(int from, BinaryReader r)
        {
            byte kind = r.ReadByte();
            if (kind == KindReady)
            {
                ready.Add(from);
                return;
            }
            if (!Party.FromHost(from))
            {
                return;
            }
            if (kind == KindAbort)
            {
                if (Active)
                {
                    Plugin.Log.LogInfo("wake: the host stopped it");
                    PartyChat.Event("WAKE ABORTED BY THE HOST");
                    End();
                }
                return;
            }
            string level = r.ReadString();
            string who = Party.NameOf(from);
            if (!Party.IsLevel(level))
            {
                return;
            }
            if (!Plugin.FollowHost.Value)
            {
                ChatLines.Add($"{who} is taking the party to {Party.LevelName(level)} (you're not following: the party screen)");
                return;
            }
            string here = SceneHelper.CurrentScene ?? "";
            if (Party.IsLevel(here))
            {
                ChatLines.Add($"{who} is starting {Party.LevelName(level)} (you're in a level of your own: SPECTATE them from the party menu)");
                return;
            }
            KillNet.Send(WakeMessage, KillNet.ToEveryone, w => w.Write(KindReady));
            Start(level, false);
        }

        private static void Abort()
        {
            if (!Active)
            {
                return;
            }
            if (mine)
            {
                KillNet.Send(WakeMessage, KillNet.ToEveryone, w => w.Write(KindAbort));
                PartyChat.Event("WAKE ABORTED");
            }
            else
            {
                PartyChat.Event("STAYING BEHIND");
                ChatLines.Add("You stayed behind: the party goes without you.");
            }
            Plugin.Log.LogInfo(mine ? "wake: stopped for everyone" : "wake: staying behind");
            End();
        }

        private static void End(bool loading = false)
        {
            Active = false;
            ready.Clear();
            PartyMenu.TintFrame(Color.white);
            if (panel != null)
            {
                panel.gameObject.SetActive(false);
            }
            if (dim != null)
            {
                dim.gameObject.SetActive(false);
            }
            PartyMenu.ShowFall(false);
            if (loading)
            {
                PartyMenu.WakeDone();
            }
            else
            {
                PartyMenu.CloseAfterWake();
            }
        }

        // ---- each frame ----

        public static void Frame()
        {
            if (ForParty && (SceneHelper.CurrentScene != "Main Menu" || (Time.unscaledTime - forPartyAt > 0.6f && PartyMenu.MenuTopShown)))
            {
                ForParty = false;
            }
            if (!Active)
            {
                return;
            }
            float t = Time.unscaledTime - startedAt;
            if (t >= Length)
            {
                string level = scene;
                bool host = mine;
                End(true);
                Plugin.Log.LogInfo($"wake: loading {level}");
                loaded = level;
                loadedAt = Time.unscaledTime;
                if (host)
                {
                    Party.SendGo(level);
                }
                LoadingScreen.Show();
                Party.LoadQuiet(level);
                return;
            }
            // (The party screen closed meanwhile: back with it, the wake still on.)
            if (panel == null || !PartyMenu.Open)
            {
                PartyMenu.OpenForWake();
                if (panel == null)
                {
                    try
                    {
                        Build();
                        Fill();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"wake: couldn't show it ({e.Message})");
                    }
                    if (panel == null)
                    {
                        return;
                    }
                }
            }
            if (!panel.gameObject.activeSelf)
            {
                panel.gameObject.SetActive(true);
            }
            panel.SetAsLastSibling();
            // (The main menu's black and its circuit diagram away: ULTRAKILL's fall behind it, darkened.)
            PartyMenu.ShowFall(true);
            LoadingScreen.Film();
            if (dim != null)
            {
                if (!dim.gameObject.activeSelf)
                {
                    dim.gameObject.SetActive(true);
                }
                dim.transform.SetSiblingIndex(1);
                dim.color = new Color(0f, 0f, 0f, Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(t / 0.4f)));
            }
            PartyMenu.TintFrame(Color.Lerp(Color.white, Pale, Mathf.Clamp01(t / 0.25f)));
            Animate(t);
        }

        // (The screen's frame during it: a pale red, nearly white.)
        private static readonly Color Pale = new Color(0.99f, 0.78f, 0.73f);

        private static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
        }

        // At 60 a second (each frame, from the clock): 3 big; at 1 s the number to the corner and the level
        // in (half a second), then the details typed; at 3 s, gone to the level.
        private static void Animate(float t)
        {
            int count = Mathf.Clamp(3 - Mathf.FloorToInt(t), 1, 3);
            digit.text = count.ToString();
            float move = Ease((t - 1f) / 0.5f);
            // The number: big in the middle of the left side, then small at its top right.
            RectTransform d = digit.rectTransform;
            d.anchoredPosition = Vector2.Lerp(new Vector2(315f, -560f), new Vector2(530f, -205f), move);
            digit.fontSize = Mathf.Lerp(420f, 120f, move);
            // Each number comes in big and shrinks, until the next comes in big.
            float into = t - Mathf.Floor(t);
            float shrink = 1f - Mathf.Pow(1f - Mathf.Clamp01(into / 0.6f), 3f);
            float pulse = Mathf.Lerp(1.45f, 0.88f, shrink);
            d.localScale = new Vector3(pulse, pulse, 1f);
            // The level coming in.
            infoGroup.alpha = move;
            number.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(-30f, 30f, move), -150f);
            levelName.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(-10f, 33f, move), -266f);
            float ps = Mathf.Lerp(0.86f, 1f, move);
            pictureFrame.rectTransform.localScale = new Vector3(ps, ps, 1f);
            line.rectTransform.sizeDelta = new Vector2(570f * Ease((t - 1.25f) / 0.4f), 3f);
            // The details, typed (after the moving's done).
            int letters = Mathf.Max(0, (int)((t - 1.6f) * 95f));
            if (letters != detailsLetters)
            {
                detailsLetters = letters;
                details.maxVisibleCharacters = letters;
            }
        }

        // ---- made ----

        private static void Build()
        {
            if (panel != null)
            {
                return;
            }
            Canvas canvas = PartyMenu.MenuCanvas;
            if (canvas == null)
            {
                return;
            }
            panel = Ui.Rect(canvas.transform, "Wake", 12f, 12f, 628f, 1056f);
            panel.gameObject.AddComponent<UiRevealSkip>();
            Image black = Ui.Picture(Ui.Fill(panel, "Black"), null, Color.black);
            black.raycastTarget = true;
            // (Inside it, the party screen's 1920x1080 places: x and y as on the whole screen, less 12.)
            RectTransform inner = Ui.Rect(panel, "Inner", -12f, -12f, 1920f, 1080f);
            TextMeshProUGUI title = Ui.Text(inner, "--WAKE--", 84f, Color.white, TextAlignmentOptions.Center, 30f, 18f, 570f, 110f);
            title.overflowMode = TextOverflowModes.Overflow;

            info = Ui.Rect(inner, "Level", 0f, 0f, 1920f, 1080f);
            infoGroup = info.gameObject.AddComponent<CanvasGroup>();
            number = Ui.Text(info, "", 118f, Ui.Red, TextAlignmentOptions.TopLeft, 30f, 150f, 420f, 120f);
            number.overflowMode = TextOverflowModes.Overflow;
            levelName = Ui.Text(info, "", 32f, Color.white, TextAlignmentOptions.MidlineLeft, 33f, 266f, 570f, 40f);
            // (ULTRAKILL's frame: a thick white line, its corners cut, on black.)
            // (Its black, the picture, then the line over the picture's edges.)
            pictureFrame = Ui.Image(info, Ui.BigChamFill, Color.black, 30f, 327f, 570f, 430f);
            pictureFrame.pixelsPerUnitMultiplier = 1.15f;
            pictureFrame.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            pictureFrame.rectTransform.anchoredPosition += new Vector2(285f, -215f);
            picture = Ui.Picture(Ui.Fill(pictureFrame.rectTransform, "Picture", 10f), null, Color.white);
            picture.preserveAspect = false;
            Ui.Picture(Ui.Fill(pictureFrame.rectTransform, "Line"), Ui.FrameLine, Color.white).pixelsPerUnitMultiplier = 1.15f;
            line = Ui.Image(info, null, Color.white, 30f, 772f, 570f, 3f);
            details = Ui.Text(info, "", 25f, Color.white, TextAlignmentOptions.TopLeft, 33f, 786f, 570f, 180f);
            details.enableWordWrapping = false;
            details.overflowMode = TextOverflowModes.Overflow;
            details.lineSpacing = 22f;

            // The number (its own: it moves from the middle to the corner).
            digit = Ui.Text(inner, "3", 420f, Color.white, TextAlignmentOptions.Center, 0f, 0f, 600f, 460f);
            digit.overflowMode = TextOverflowModes.Overflow;
            RectTransform d = digit.rectTransform;
            d.pivot = new Vector2(0.5f, 0.5f);

            // (Over the fall, under the whole party screen: it darkened.)
            dim = Ui.Picture(Ui.Fill(canvas.transform, "Wake dim"), null, Color.black);
            dim.gameObject.AddComponent<UiRevealSkip>();
            dim.transform.SetSiblingIndex(1);

            // (Its writing red in a white box.)
            Ui.Button(inner, $"<color=#{Ui.Hex(Ui.Red)}>ABORT WAKE</color>", 32f, Abort, 30f, 978f, 570f, 72f, TextAlignmentOptions.MidlineLeft);
            Ui.Refont(panel);
        }

        // The level's number, name, picture and details.
        private static void Fill()
        {
            if (panel == null)
            {
                return;
            }
            panel.gameObject.SetActive(true);
            string num = scene == "Endless" ? "CG" : scene == "uk_construct" ? "SB" : scene.Replace("Level ", "");
            string name = scene == "Endless" ? "THE CYBER GRIND" : scene == "uk_construct" ? "SANDBOX" : "";
            Sprite sprite = null;
            try
            {
                foreach (int n in GetMissionName.EnumerateMissionNumbers())
                {
                    if (GetMissionName.GetSceneName(n) == scene)
                    {
                        num = GetMissionName.GetMissionNumberOnly(n);
                        name = GetMissionName.GetMissionNameOnly(n);
                        sprite = Thumbnail(n);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"wake: couldn't name {scene} ({e.Message})");
            }
            number.text = num;
            levelName.text = (name ?? "").ToUpperInvariant();
            picture.sprite = sprite;
            picture.color = sprite != null ? Color.white : new Color(0.08f, 0.08f, 0.08f);

            int dif = 2;
            try
            {
                dif = MonoSingleton<PrefsManager>.Instance.GetInt("difficulty", 2);
            }
            catch
            {
            }
            string[] difficulties = { "HARMLESS", "LENIENT", "STANDARD", "VIOLENT", "BRUTAL", "UKMD" };
            string layerDigit = num.Length > 0 ? num.Substring(0, 1) : "";
            string[] layers = { "PRELUDE", "LIMBO", "LUST", "GLUTTONY", "GREED", "WRATH", "HERESY", "VIOLENCE", "FRAUD", "TREACHERY" };
            string layer = int.TryParse(layerDigit, out int li) && li >= 0 && li < layers.Length ? $"{li} {layers[li]}" : layerDigit == "P" ? "PRIME" : "-";
            string act = !int.TryParse(layerDigit, out int ln) ? "-" : ln == 0 ? "PRELUDE" : ln <= 3 ? "I" : ln <= 6 ? "II" : "III";
            int party = Mathf.Max(1, Party.Members.Count);
            detailsText = Row("DIFFICULTY", difficulties[Mathf.Clamp(dif, 0, 5)]) + "\n"
                + Row("LAYER", layer) + "\n"
                + Row("ACT", act) + "\n"
                + Row("PARTY", $"{party} {(party == 1 ? "PLAYER" : "PLAYERS")}");
            details.text = detailsText;
            details.maxVisibleCharacters = 0;
            Animate(0f);
        }

        private static string Row(string label, string value) =>
            $"<color=#9A9A9A>{label} <color=#5A5A5A>{new string('.', Mathf.Max(3, 12 - label.Length)).Replace(".", ". ").TrimEnd()}</color></color>  {value}";

        // The level's picture, as its panel in ULTRAKILL's level select shows it.
        private static readonly AccessTools.FieldRef<LevelSelectPanel, Sprite> origSprite = AccessTools.FieldRefAccess<LevelSelectPanel, Sprite>("origSprite");

        private static Sprite Thumbnail(int mission)
        {
            foreach (LevelSelectPanel p in Resources.FindObjectsOfTypeAll<LevelSelectPanel>())
            {
                if (p == null || p.levelNumber != mission)
                {
                    continue;
                }
                Sprite s = origSprite(p);
                if (s == null)
                {
                    Transform img = p.transform.Find("Image");
                    Image i = img != null ? img.GetComponent<Image>() : null;
                    s = i != null ? i.sprite : null;
                }
                if (s != null)
                {
                    return s;
                }
            }
            return null;
        }
    }
}
