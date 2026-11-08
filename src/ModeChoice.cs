using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // Which one this time, asked as ULTRAKILL's main menu first comes up (the full Killcraft download
    // only: KillNet's has no Minecraft to choose): KILLNET (multiplayer only) or KILLCRAFT (with
    // Minecraft). Minecraft isn't started until it's answered. Not asked when the launcher started the
    // game (it chose already: its MINECRAFT ON/OFF), or with ASK EVERY START off (then the last choice,
    // StartWithUltrakill, counts). Left alone, the last choice is taken after a countdown.
    internal static class ModeChoice
    {
        private const float Countdown = 10f;
        private const string LauncherMark = "launcher-start.txt";

        // Asked and not answered yet (Minecraft waits for it).
        public static bool Pending { get; private set; }

        private static Canvas canvas;
        private static TextMeshProUGUI countdown, starting;
        private static float shownAt;
        private static bool counting;
        private static int highlighted;
        private static CanvasGroup topGroup, bottomGroup;

        // A choice's parts: drawn here (not by its button), so the one under the mouse is plain to see.
        private sealed class Card
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public Image Outline, Glow, Logo, Line;
            public TextMeshProUGUI Name, What, Words;
            public float X, Lit;
        }

        private static Card[] cards;

        // After the pick: the other one goes, the picked one comes to the middle, then the screen
        // switches off as an old one does (a line, then a dot), the main menu under it.
        private static bool animating;
        private static float pickedAt;
        private static int picked;
        private const float FlashEnd = 0.14f, MoveEnd = 0.55f, CollapseAt = 0.85f, LineEnd = 1.02f, DotEnd = 1.12f;

        // At the start (Plugin.Awake), before Minecraft would be started: ask this time?
        public static bool Decide()
        {
            bool fromLauncher = FromLauncher();
            Pending = Plugin.MinecraftThere && Plugin.AskMode.Value && !fromLauncher && Plugin.PlayerSlot == 1;
            if (Pending)
            {
                Plugin.Log.LogInfo("mode: asking KILLNET or KILLCRAFT in the main menu (Minecraft waits)");
            }
            return Pending;
        }

        // (The launcher leaves this just before starting the game; taken away here either way.)
        private static bool FromLauncher()
        {
            try
            {
                string file = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", LauncherMark);
                if (!File.Exists(file))
                {
                    return false;
                }
                bool fresh = long.TryParse(File.ReadAllText(file).Trim(), out long ticks)
                    && Math.Abs((DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes) < 3.0;
                File.Delete(file);
                if (fresh)
                {
                    Plugin.Log.LogInfo("mode: started by the launcher, which chose already");
                }
                return fresh;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // (Made as soon as the main menu's scene is loaded, before its first frame is drawn: the main
        // menu isn't seen for a moment first. Scenes are named by ULTRAKILL's ids: SceneHelper says which.)
        public static void Hook()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
            {
                if (!Pending || canvas != null || animating || !LoadingScreen.IsMainMenu(s))
                {
                    return;
                }
                try
                {
                    Build();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"mode: couldn't show the choice yet ({e.Message}); in a moment");
                    if (canvas != null)
                    {
                        UnityEngine.Object.Destroy(canvas.gameObject);
                        canvas = null;
                    }
                }
            };
        }

        public static void Frame(bool mainMenu, bool loading)
        {
            if (animating)
            {
                Animate();
                return;
            }
            if (!Pending)
            {
                return;
            }
            if (canvas == null)
            {
                if (mainMenu && !loading)
                {
                    try
                    {
                        Build();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"mode: couldn't show the choice ({e}); going on as last time");
                        Choose(Plugin.StartMinecraft.Value);
                        Close();
                    }
                }
                return;
            }
            Ui.Refont(canvas.transform);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            float since = Time.unscaledTime - shownAt;
            // (Touched at all: no countdown, it's theirs to pick.)
            if (counting && since > 0.6f && ((kb != null && kb.anyKey.wasPressedThisFrame) || (mouse != null && mouse.delta.ReadValue().sqrMagnitude > 9f)))
            {
                counting = false;
                countdown.text = "";
            }
            if (kb != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame)
                {
                    Highlight(0);
                }
                else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame)
                {
                    Highlight(1);
                }
                else if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)
                {
                    Pick(highlighted);
                    return;
                }
            }
            Shade(Time.unscaledDeltaTime);
            if (counting)
            {
                float left = Countdown - since;
                if (left <= 0f)
                {
                    Pick(highlighted);
                    return;
                }
                countdown.text = $"{(highlighted == 1 ? "KILLCRAFT" : "KILLNET")} IN {Mathf.CeilToInt(left)}";
            }
        }

        private static void Highlight(int which)
        {
            highlighted = which;
        }

        // Each card toward how it should look: the chosen one lit (red outline, red glow inside, its
        // logo and name bright, a little bigger), the other dimmed (grey outline, its colours dark).
        private static void Shade(float dt)
        {
            for (int i = 0; i < cards.Length; i++)
            {
                Card c = cards[i];
                c.Lit = Mathf.MoveTowards(c.Lit, i == highlighted ? 1f : 0f, dt * 8f);
                float l = c.Lit;
                c.Outline.color = Color.Lerp(new Color(0.32f, 0.32f, 0.32f), Ui.Red, l);
                c.Glow.color = new Color(Ui.Red.r, Ui.Red.g, Ui.Red.b, 0.13f * l);
                Color dimRed = new Color(0.36f, 0.13f, 0.12f);
                c.Logo.color = Color.Lerp(dimRed, Ui.Red, l);
                c.Name.color = Color.Lerp(dimRed, Ui.Red, l);
                c.What.color = Color.Lerp(new Color(0.45f, 0.45f, 0.45f), Color.white, l);
                c.Line.color = Color.Lerp(new Color(0.3f, 0.3f, 0.3f), Ui.Red, l);
                c.Words.color = Color.Lerp(new Color(0.35f, 0.35f, 0.35f), new Color(0.85f, 0.85f, 0.85f), l);
                float sc = Mathf.Lerp(0.96f, 1.03f, l);
                c.Root.localScale = new Vector3(sc, sc, 1f);
            }
        }

        private static void Pick(int which)
        {
            if (animating)
            {
                return;
            }
            Ui.ClickSound();
            highlighted = which;
            Choose(which == 1);
            // (Picked: the screen plays out, then goes.)
            animating = true;
            pickedAt = Time.unscaledTime;
            picked = which;
            countdown.text = "";
            starting.text = "";
            foreach (Card c in cards)
            {
                foreach (UiButton b in c.Root.GetComponentsInChildren<UiButton>())
                {
                    b.Interactable = false;
                }
            }
        }

        private static void Choose(bool craft)
        {
            Pending = false;
            Plugin.StartMinecraft.Value = craft;
            Plugin.Log.LogInfo($"mode: {(craft ? "KILLCRAFT (with Minecraft)" : "KILLNET (multiplayer only)")} chosen");
            if (craft)
            {
                Launcher.StartMinecraft();
            }
        }

        private static void Close()
        {
            animating = false;
            if (canvas != null)
            {
                UnityEngine.Object.Destroy(canvas.gameObject);
                canvas = null;
            }
        }

        private static float Ease(float t) => 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);

        private static void Animate()
        {
            if (canvas == null)
            {
                animating = false;
                return;
            }
            float t = Time.unscaledTime - pickedAt;
            Card chosen = cards[picked], other = cards[1 - picked];
            // The picked one: a white flash, then lit, to the middle, a little bigger.
            float flash = t < FlashEnd ? 1f - t / FlashEnd : 0f;
            chosen.Glow.color = Color.Lerp(new Color(Ui.Red.r, Ui.Red.g, Ui.Red.b, 0.2f), new Color(1f, 1f, 1f, 0.85f), flash);
            chosen.Outline.color = Color.Lerp(Ui.Red, Color.white, flash);
            float move = Ease((t - FlashEnd) / (MoveEnd - FlashEnd));
            chosen.Root.anchoredPosition = new Vector2(Mathf.Lerp(chosen.X, 0f, move), chosen.Root.anchoredPosition.y);
            float sc = Mathf.Lerp(1.03f, 1.1f, move);
            chosen.Root.localScale = new Vector3(sc, sc, 1f);
            // The other one, and the rest: away.
            float away = Ease(t / 0.3f);
            other.Group.alpha = 1f - away;
            other.Root.anchoredPosition = new Vector2(other.X + Mathf.Sign(other.X) * 120f * away, other.Root.anchoredPosition.y);
            topGroup.alpha = 1f - away;
            bottomGroup.alpha = 1f - away;
            // What it's doing now, typed out under the card.
            string line = picked == 1 ? "> KILLCRAFT ONLINE. STARTING MINECRAFT..." : "> KILLNET ONLINE.";
            int letters = Mathf.Clamp((int)((t - MoveEnd + 0.1f) * 70f), 0, line.Length);
            bool caret = (int)(t * 6f) % 2 == 0;
            starting.text = line.Substring(0, letters) + (letters < line.Length || caret ? "_" : " ");
            // The screen switching off: down to a line, then to a dot.
            var root = (RectTransform)canvas.transform.Find("Screen");
            if (t >= CollapseAt)
            {
                float y = 1f - Ease((t - CollapseAt) / (LineEnd - CollapseAt));
                float x = t < LineEnd ? 1f : 1f - Ease((t - LineEnd) / (DotEnd - LineEnd));
                root.localScale = new Vector3(Mathf.Max(0.002f, x), Mathf.Max(0.004f, y), 1f);
            }
            if (t >= DotEnd + 0.05f)
            {
                Close();
            }
        }

        // ---- made ----

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft mode choice", 90, true);
            // (All of it in one rectangle the size of the screen: the switching-off squashes it.)
            RectTransform root = Ui.Fill(canvas.transform, "Screen");
            Image black = Ui.Picture(Ui.Fill(root, "Black"), null, Color.black);
            black.raycastTarget = true;
            Ui.Picture(Ui.Fill(root, "Frame", 4f), Ui.FrameLine, Color.white);

            RectTransform top = Ui.Fill(root, "Top");
            RectTransform middle = Ui.Fill(root, "Cards");
            RectTransform bottom = Ui.Fill(root, "Bottom");
            topGroup = top.gameObject.AddComponent<CanvasGroup>();
            bottomGroup = bottom.gameObject.AddComponent<CanvasGroup>();

            Ui.Text(top, "--START--", 96f, Color.white, TextAlignmentOptions.TopLeft, 36f, 18f, 700f, 110f);
            TextMeshProUGUI ask = Ui.Text(top, "HOW DO YOU WANT TO PLAY?", 34f, Color.white, TextAlignmentOptions.Center, 0f, 0f, 1000f, 50f);
            Centered(ask.rectTransform, 0f, 172f, 1000f, 50f);

            cards = new[]
            {
                MakeCard(middle, 0, -310f, Ui.NetLogo, "KILLNET", "MULTIPLAYER ONLY",
                    "Co-op and versus with the KillNet party.\nPlain ULTRAKILL: starts at once."),
                MakeCard(middle, 1, 310f, Ui.Logo, "KILLCRAFT", "WITH MINECRAFT",
                    "Minecraft runs alongside: blocks, mobs,\nitems. Takes longer to start."),
            };
            starting = Ui.Text(middle, "", 26f, Color.white, TextAlignmentOptions.Center, 0f, 0f, 900f, 40f);
            Centered(starting.rectTransform, 0f, 860f, 900f, 40f);

            // ASK EVERY START, and the countdown.
            TextMeshProUGUI askLabel = Ui.Text(bottom, "ASK EVERY START", 26f, Color.white, TextAlignmentOptions.MidlineRight, 0f, 0f, 300f, 34f);
            Centered(askLabel.rectTransform, -40f, 868f, 300f, 34f);
            UiSwitch sw = Ui.Switch(bottom, 0f, 0f, Plugin.AskMode.Value, on => Plugin.AskMode.Value = on);
            Centered((RectTransform)sw.transform, 152f, 873f, 46f, 24f);
            TextMeshProUGUI hint = Ui.Text(bottom, "OFF: THIS CHOICE IS KEPT. TURN ASKING BACK ON IN OPTIONS.", 18f, Ui.Grey, TextAlignmentOptions.Center, 0f, 0f, 1000f, 26f);
            Centered(hint.rectTransform, 0f, 914f, 1000f, 26f);
            countdown = Ui.Text(bottom, "", 26f, Ui.Red, TextAlignmentOptions.Center, 0f, 0f, 600f, 34f);
            Centered(countdown.rectTransform, 0f, 966f, 600f, 34f);
            Ui.Text(bottom, $"<color=#{Ui.Hex(Ui.Red)}>[ KILLCRAFT ] v2.0</color>", 22f, Color.white, TextAlignmentOptions.BottomRight, 1300f, 1030f, 596f, 36f);

            shownAt = Time.unscaledTime;
            counting = true;
            highlighted = Plugin.StartMinecraft.Value ? 1 : 0;
            foreach (Card c in cards)
            {
                c.Lit = cards[highlighted] == c ? 1f : 0f;
            }
            Shade(0f);
            Ui.Refont(root);
            Ui.Reveal(root, top, middle, bottom);
        }

        // A big choice: its outline and glow, the logo, the name, what it is, a few words; the whole
        // of it clickable (an undrawn button), the mouse onto it lighting it.
        private static Card MakeCard(Transform parent, int index, float cx, Sprite logo, string name, string what, string words)
        {
            const float W = 560f, H = 560f;
            var c = new Card { X = cx };
            c.Root = Ui.Rect(parent, "Choice " + name, 0f, 0f, W, H);
            c.Root.anchorMin = c.Root.anchorMax = new Vector2(0.5f, 1f);
            c.Root.pivot = new Vector2(0.5f, 0.5f);
            c.Root.sizeDelta = new Vector2(W, H);
            c.Root.anchoredPosition = new Vector2(cx, -(250f + H / 2f));
            c.Group = c.Root.gameObject.AddComponent<CanvasGroup>();
            Transform t = c.Root;
            c.Glow = Ui.Picture(Ui.Fill(t, "Glow"), Ui.BoxFill, Color.clear);
            c.Outline = Ui.Sliced(Ui.Picture(Ui.Fill(t, "Outline"), Ui.BoxLine, Color.white), 5f);
            c.Logo = Ui.Image(t, logo, Ui.Red, (W - 170f) / 2f, 50f, 170f, 170f);
            c.Logo.preserveAspect = true;
            c.Name = Ui.Text(t, name, 80f, Ui.Red, TextAlignmentOptions.Center, 0f, 244f, W, 96f);
            c.What = Ui.Text(t, what, 30f, Color.white, TextAlignmentOptions.Center, 0f, 346f, W, 44f);
            c.Line = Ui.Line(t, Color.white, 80f, 400f, W - 160f, 3f);
            c.Words = Ui.Text(t, words, 21f, new Color(0.8f, 0.8f, 0.8f), TextAlignmentOptions.Top, 20f, 424f, W - 40f, 90f);
            c.Words.enableWordWrapping = false;
            c.Words.overflowMode = TextOverflowModes.Overflow;
            UiButton b = Ui.Button(t, "", 10f, () => Pick(index), 0f, 0f, W, H);
            b.Bare = true;
            b.Hovered = () =>
            {
                if (!animating)
                {
                    Highlight(index);
                }
            };
            b.Refresh();
            return c;
        }

        // From the top middle: cx off the middle, y down from the top.
        private static void Centered(RectTransform rt, float cx, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(cx, -y);
        }
    }
}
