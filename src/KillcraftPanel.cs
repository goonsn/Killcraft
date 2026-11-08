using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // The KILLCRAFT panel: KILLCRAFT under ULTRAKILL's pause menu (and SETTINGS in the party menu)
    // opens it. A panel framed twice in Killcraft's red (corners cut), its header (KILLCRAFT, the
    // level and difficulty, X), tabs: PARTY (everyone here: their picture, crown, health, ping, mute
    // and volume; the party's code, invites, leaving), VOICE, REPLAY and HUD (the settings are in
    // OPTIONS > KILLCRAFT, as UltraLink's).
    //
    // ULTRAKILL's pause menu stays up underneath, only made invisible: its "pause" state (the cursor
    // free, the player held) lasts while its menu is active, and hiding it locked the mouse.
    internal static class KillcraftPanel
    {
        private static readonly string[] TabNames = { "PARTY", "VOICE", "REPLAY", "HUD" };
        private static readonly string[] Keys = { "V", "B", "T", "G", "X", "Z", "CapsLock", "LeftAlt", "Mouse4", "Mouse5" };
        private const float W = 920f, H = 680f, In = 30f, PageY = 138f, Foot = 50f;

        private sealed class PlayerRow
        {
            public RectTransform Root;
            public Image Strip, Crown, MeterFill;
            public RawImage Avatar;
            public TextMeshProUGUI Name, Status, Ping, Volume, Talk;
            public RectTransform You;
            public UiButton Mute;
            public UiSlider Slider;
        }

        private static Canvas canvas;
        private static RectTransform panel, popup;
        private static int tab;
        private static readonly GameObject[] pages = new GameObject[4];
        private static readonly UiButton[] tabs = new UiButton[4];
        private static readonly List<Action>[] refreshers = { new List<Action>(), new List<Action>(), new List<Action>(), new List<Action>() };
        private static GameObject returnTo, pauseButton;
        private static CanvasGroup hiddenGroup;
        private static float hiddenAlpha = 1f;
        private static bool hiddenInteractable = true, hiddenBlocks = true;
        private static TextMeshProUGUI level, squadLine, squadInfo, codeText, footRun;
        private static UiButton leave, invite, copyCode;
        private static RectTransform players;
        private static readonly Dictionary<int, PlayerRow> rows = new Dictionary<int, PlayerRow>();
        private static string rowsKey = "";
        private static float refreshAt, buttonAt = -100f;
        private static bool heldPause, wasDontUnpause;
        private static int closedFrame = -10;

        public static readonly Color KillRed = Ui.Red;

        public static bool Open => canvas != null && canvas.gameObject.activeSelf;

        public static int ClosedFrame => closedFrame;

        public static void Frame(bool inGame, bool paused, bool mainMenu, bool loading)
        {
            if (inGame && Time.unscaledTime - buttonAt > 1f)
            {
                buttonAt = Time.unscaledTime;
                AddToPauseMenu();
            }
            if (inGame && paused)
            {
                PlaceButton();
            }
            // (ULTRAKILL's pause menu doesn't close under it: its own Esc is the panel's meanwhile.)
            if (heldPause && !Open && Time.frameCount > closedFrame + 1)
            {
                heldPause = false;
                OptionsManager om = MonoSingleton<OptionsManager>.Instance;
                if (om != null)
                {
                    om.dontUnpause = wasDontUnpause;
                }
            }
            if (!Open)
            {
                return;
            }
            if (loading || (inGame && !paused) || (!inGame && !mainMenu))
            {
                Close();
                return;
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && !Typing.Active && Typing.EndedFrame != Time.frameCount)
            {
                if (popup != null && popup.gameObject.activeSelf)
                {
                    popup.gameObject.SetActive(false);
                }
                else
                {
                    Close();
                    return;
                }
            }
            LiveRows();
            if (Time.unscaledTime >= refreshAt)
            {
                refreshAt = Time.unscaledTime + 0.25f;
                try
                {
                    Refresh();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"KILLCRAFT panel: {e}");
                    refreshAt = Time.unscaledTime + 5f;
                }
            }
        }

        // ---- KILLCRAFT under the pause menu ----

        // ULTRAKILL's own OPTIONS button copied (its box, its font, grey under the mouse, red pressed),
        // in Killcraft's red, under the pause menu's box.
        private static void AddToPauseMenu()
        {
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            GameObject pause = om != null ? om.pauseMenu : null;
            if (pause == null || (pauseButton != null && pauseButton.transform.parent == pause.transform))
            {
                return;
            }
            Transform quit = pause.transform.Find("Quit Mission");
            Transform options = pause.transform.Find("Options");
            if (quit == null || options == null)
            {
                return;
            }
            var optRt = (RectTransform)options;
            Vector2 size = optRt.sizeDelta;
            // (Under the pause menu's box, apart from it as its buttons are from each other; under
            // spectating's JOIN and STOP SPECTATING when those are up.)
            PauseButtons.Step(pause, out float step, out float top, out _);
            float y = top;
            GameObject b = null;
            try
            {
                b = UnityEngine.Object.Instantiate(options.gameObject, pause.transform);
                b.name = "Killcraft Button";
                // (Only Unity's UI parts kept: none of ULTRAKILL's scripts that came with it.)
                foreach (MonoBehaviour mb in b.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    string ns = mb.GetType().Namespace ?? "";
                    if (!ns.StartsWith("UnityEngine", StringComparison.Ordinal) && !ns.StartsWith("TMPro", StringComparison.Ordinal))
                    {
                        UnityEngine.Object.DestroyImmediate(mb);
                    }
                }
                var button = b.GetComponent<Button>();
                if (button == null)
                {
                    throw new Exception("no Button on OPTIONS");
                }
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() =>
                {
                    Ui.ClickSound();
                    Show(pause);
                });
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
                {
                    t.text = Brand.Name;
                    t.color = KillRed;
                }
                foreach (Text t in b.GetComponentsInChildren<Text>(true))
                {
                    t.text = Brand.Name;
                    t.color = KillRed;
                }
                RedOutline(b, button);
                var rt = (RectTransform)b.transform;
                rt.anchoredPosition = new Vector2(optRt.anchoredPosition.x, y);
                b.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KILLCRAFT panel: couldn't copy OPTIONS ({e.Message}); drawing it instead");
                if (b != null)
                {
                    UnityEngine.Object.Destroy(b);
                }
                float font = 20f;
                TMP_Text t = options.GetComponentInChildren<TMP_Text>(true);
                if (t != null)
                {
                    font = t.fontSize;
                }
                UiButton ub = Ui.Button(pause.transform, Brand.Name, font, () => Show(pause), 0f, 0f, size.x, size.y, TextAlignmentOptions.Center, false, 2.2f);
                var rt = (RectTransform)ub.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(optRt.anchoredPosition.x, y);
                ub.Accent = KillRed;
                ub.Refresh();
                b = ub.gameObject;
                b.name = "Killcraft Button";
            }
            pauseButton = b;
            Plugin.Log.LogInfo("KILLCRAFT panel: KILLCRAFT added under the pause menu");
        }

        // OPTIONS's copy kept for its place and size, drawn by Killcraft instead: see-through black
        // inside, a red outline, KILLCRAFT in red (ULTRAKILL's font and size).
        private static void RedOutline(GameObject b, Button button)
        {
            var parts = new System.Text.StringBuilder();
            foreach (Image img in b.GetComponentsInChildren<Image>(true))
            {
                parts.Append($" {img.name}:{(img.sprite != null ? img.sprite.name : "none")} {img.color}");
            }
            foreach (CanvasGroup g in b.GetComponentsInChildren<CanvasGroup>(true))
            {
                parts.Append($" canvas group {g.name} {g.alpha:0.00}");
                g.alpha = 1f;
            }
            Plugin.Log.LogInfo($"KILLCRAFT panel: OPTIONS is drawn with{parts}");
            TMP_FontAsset font = null;
            float size = 0f;
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                if (font == null)
                {
                    font = t.font;
                    size = t.fontSize;
                }
                t.enabled = false;
            }
            foreach (Text t in b.GetComponentsInChildren<Text>(true))
            {
                t.enabled = false;
            }
            Image back = b.GetComponent<Image>();
            foreach (Image img in b.GetComponentsInChildren<Image>(true))
            {
                if (img != back)
                {
                    img.enabled = false;
                }
            }
            if (back != null)
            {
                // (Still there, for the mouse; not drawn.)
                back.color = new Color(0f, 0f, 0f, 0f);
            }
            RectTransform fr = Ui.Fill(b.transform, "Killcraft Fill");
            Image fill = Ui.Picture(fr, Ui.BoxFill, new Color(0f, 0f, 0f, 0.6f));
            fill.raycastTarget = false;
            RectTransform lr = Ui.Fill(b.transform, "Killcraft Outline");
            Image line = Ui.Picture(lr, Ui.BoxLine, KillRed);
            line.raycastTarget = false;
            if (back != null && back.type == Image.Type.Sliced)
            {
                line.pixelsPerUnitMultiplier = back.pixelsPerUnitMultiplier;
                fill.pixelsPerUnitMultiplier = back.pixelsPerUnitMultiplier;
            }
            TextMeshProUGUI label = Ui.Label(Ui.Fill(b.transform, "Killcraft Label"), Brand.Name, size > 0f ? size : 24f, KillRed, TextAlignmentOptions.Center);
            if (font != null)
            {
                label.font = font;
            }
            if (button != null)
            {
                button.transition = Selectable.Transition.ColorTint;
                button.targetGraphic = line;
            }
        }

        // Its row: under the pause menu's box.
        private static void PlaceButton()
        {
            if (pauseButton == null)
            {
                return;
            }
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            GameObject pause = om != null ? om.pauseMenu : null;
            if (pause == null || PauseButtons.LaidOut || !PauseButtons.Step(pause, out float step, out float top, out float x))
            {
                return;
            }
            var rt = (RectTransform)pauseButton.transform;
            var at = new Vector2(x, top);
            if ((rt.anchoredPosition - at).sqrMagnitude > 0.01f)
            {
                rt.anchoredPosition = at;
            }
        }

        // ---- open, closed ----

        // From the pause menu (invisible meanwhile), or from the party menu (from: null).
        public static void Show(GameObject from)
        {
            try
            {
                if (canvas == null)
                {
                    Build();
                }
                Ui.Refont(canvas.transform);
                returnTo = from;
                if (from != null)
                {
                    HideUnder(from);
                }
                OptionsManager om = MonoSingleton<OptionsManager>.Instance;
                if (om != null && !heldPause)
                {
                    wasDontUnpause = om.dontUnpause;
                    om.dontUnpause = true;
                    heldPause = true;
                }
                canvas.gameObject.SetActive(true);
                Ui.Open(panel);
                ShowTab(tab);
                rowsKey = "";
                refreshAt = 0f;
                Plugin.Log.LogInfo("KILLCRAFT panel: opened");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KILLCRAFT panel: couldn't make it ({e})");
                if (canvas != null)
                {
                    UnityEngine.Object.Destroy(canvas.gameObject);
                    canvas = null;
                }
                ShowUnder();
            }
        }

        private static void HideUnder(GameObject from)
        {
            hiddenGroup = from.GetComponent<CanvasGroup>();
            if (hiddenGroup == null)
            {
                hiddenGroup = from.AddComponent<CanvasGroup>();
            }
            hiddenAlpha = hiddenGroup.alpha;
            hiddenInteractable = hiddenGroup.interactable;
            hiddenBlocks = hiddenGroup.blocksRaycasts;
            hiddenGroup.alpha = 0f;
            hiddenGroup.interactable = false;
            hiddenGroup.blocksRaycasts = false;
        }

        private static void ShowUnder()
        {
            if (hiddenGroup != null)
            {
                hiddenGroup.alpha = hiddenAlpha;
                hiddenGroup.interactable = hiddenInteractable;
                hiddenGroup.blocksRaycasts = hiddenBlocks;
            }
            hiddenGroup = null;
        }

        // Closed, back to where it came from.
        private static void Close()
        {
            Hide();
        }

        public static void Hide()
        {
            ShowUnder();
            returnTo = null;
            if (canvas != null && canvas.gameObject.activeSelf)
            {
                canvas.gameObject.SetActive(false);
                closedFrame = Time.frameCount;
                ShaderPack.SaveNow();
            }
        }

        // ---- made ----

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft panel", 80, true);
            panel = Ui.Rect(canvas.transform, "Panel", 0f, 0f, W, H);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            // (Framed twice, as ULTRAKILL's HUD panels: an outer line, another inside it.)
            // (Black only inside the inner outline: between the two, the game shows through.)
            Ui.Picture(Ui.Fill(panel, "Catch"), null, new Color(0f, 0f, 0f, 0f)).raycastTarget = true;
            Ui.Picture(Ui.Fill(panel, "Back", 12f), Ui.BigChamFill, new Color(0.02f, 0.02f, 0.02f, 0.86f));
            // (A dark band along the outer outline, half way in to the inner one, as UltraLink's.)
            Ui.Picture(Ui.Fill(panel, "Band"), Ui.BandLine, new Color(0f, 0f, 0f, 0.5f));
            Ui.Picture(Ui.Fill(panel, "Outer"), Ui.BigChamLine, Ui.Orange);
            Ui.Picture(Ui.Fill(panel, "Inner", 11f), Ui.BigChamLine, Ui.Orange);

            Ui.Text(panel, Brand.Name, 34f, Ui.Orange, TextAlignmentOptions.MidlineLeft, In, 26f, 220f, 40f);
            Ui.Text(panel, "v2.0", 14f, Ui.Grey, TextAlignmentOptions.BottomLeft, In + 194f, 26f, 60f, 34f);
            level = Ui.Text(panel, "", 16f, Color.white, TextAlignmentOptions.MidlineRight, 300f, 28f, W - In - 300f - 52f, 36f);
            UiButton x = Ui.Button(panel, "X", 18f, Close, W - In - 38f, 26f, 36f, 36f, TextAlignmentOptions.Center, true, 3f);
            x.Line = Ui.CircleLine;
            x.Filled = Ui.CircleFill;
            x.Back.type = Image.Type.Simple;
            x.Refresh();
            Ui.Line(panel, Color.white, In, 72f, W - 2f * In, 2f);
            float tw = (W - 2f * In - 3f * 10f) / 4f;
            for (int i = 0; i < TabNames.Length; i++)
            {
                int n = i;
                // (UltraLink's tabs: dark inside.)
                Ui.Picture(Ui.Rect(panel, "Tab back", In + i * (tw + 10f), 86f, tw, 36f), Ui.SmallFill, new Color(0.07f, 0.07f, 0.07f, 0.95f));
                tabs[i] = Ui.Button(panel, TabNames[i], 19f, () => ShowTab(n), In + i * (tw + 10f), 86f, tw, 36f, TextAlignmentOptions.Center, true, 3f);
                tabs[i].TextOn = Color.white;
            }
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = Ui.Rect(panel, TabNames[i], In, PageY, W - 2f * In, H - PageY - In - Foot).gameObject;
                pages[i].SetActive(false);
                refreshers[i].Clear();
            }
            PartyPage(pages[0].transform);
            VoicePage(pages[1].transform);
            ReplayPage(pages[2].transform);
            HudPage(pages[3].transform);
            // The footer (UltraLink's): this run's time, SAVE REPLAY.
            footRun = Ui.Text(panel, "", 16f, Ui.Grey, TextAlignmentOptions.MidlineLeft, In, H - In - 40f, 400f, 36f);
            Ui.Button(panel, "SAVE REPLAY", 17f, Replay.SaveNow, W - In - 180f, H - In - 40f, 180f, 36f, TextAlignmentOptions.Center, true, 3f);
            popup = Ui.Rect(panel, "More", 0f, 0f, 230f, 10f);
            popup.gameObject.SetActive(false);
            canvas.gameObject.SetActive(false);
        }

        private static float PageW => W - 2f * In;

        private static void ShowTab(int n)
        {
            tab = n;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].SetActive(i == n);
                tabs[i].On = i == n;
                tabs[i].Accent = i == n ? Ui.Orange : new Color(0.62f, 0.62f, 0.62f);
                tabs[i].Refresh();
            }
            if (popup != null)
            {
                popup.gameObject.SetActive(false);
            }
            refreshAt = 0f;
        }

        private static void Refresh()
        {
            string scene = SceneHelper.CurrentScene ?? "";
            string diff = Profiles.DifficultyName(Profiles.MyDifficulty());
            if (Party.IsLevel(scene))
            {
                string name = Party.LevelName(scene).ToUpperInvariant();
                int colon = name.IndexOf(':');
                string number = colon > 0 ? name.Substring(0, colon) : "";
                string title = colon > 0 ? name.Substring(colon + 1).Trim() : name;
                level.text = (number.Length > 0 ? $"<color=#{Ui.Hex(Ui.Orange)}>{Typing.Escape(number)}</color>  " : "") + $"{Typing.Escape(title)}  <color=#999999>·  {diff}</color>";
            }
            else
            {
                level.text = $"<color=#999999>{diff}</color>";
            }
            int count = Mathf.Max(1, Party.Members.Count);
            tabs[0].Set($"PARTY <size=13>{count}/10</size>");
            foreach (Action a in refreshers[tab])
            {
                a();
            }
            if (tab == 0)
            {
                Players();
            }
        }

        // ---- PARTY ----

        private static void PartyPage(Transform p)
        {
            squadLine = Ui.Text(p, "", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 0f, 400f, 28f);
            squadInfo = Ui.Text(p, "", 16f, Ui.Grey, TextAlignmentOptions.MidlineRight, 400f, 0f, PageW - 400f, 28f);
            Image codeBox = Ui.Picture(Ui.Rect(p, "Code", 0f, 36f, 170f, 32f), Ui.SmallLine, Color.white);
            Ui.Sliced(codeBox, 2.5f);
            codeText = Ui.Text(p, "", 18f, Color.white, TextAlignmentOptions.Center, 6f, 36f, 158f, 32f);
            copyCode = Ui.Button(p, "COPY", 17f, () =>
            {
                string code = Profiles.PartyCode();
                if (code.Length > 0)
                {
                    GUIUtility.systemCopyBuffer = code;
                    PartyChat.Event("PARTY CODE COPIED");
                }
            }, 180f, 36f, 74f, 32f, TextAlignmentOptions.Center, true, 3f);
            invite = Ui.Button(p, "INVITE FRIENDS", 17f, ServerList.InviteOverlay, 264f, 36f, 170f, 32f, TextAlignmentOptions.Center, true, 3f);
            leave = Ui.Button(p, "HOLD TO LEAVE", 17f, () =>
            {
                Party.LeaveToOwnWorld();
                PartyChat.System("Leaving the party (back to your own world)...");
            }, PageW - 170f, 36f, 170f, 32f, TextAlignmentOptions.Center, true, 3f);
            leave.Accent = KillRed;
            leave.Hold = 1.2f;
            leave.Refresh();
            players = Ui.Scroll(p, 0f, 80f, PageW, H - PageY - In - Foot - 80f, 8f, out _);
            refreshers[0].Add(() =>
            {
                int n = Mathf.Max(1, Party.Members.Count);
                squadLine.text = $"-- SQUAD  {n}/10 --";
                bool cheats = false;
                try
                {
                    cheats = MonoSingleton<CheatsController>.Instance != null && MonoSingleton<CheatsController>.Instance.cheatsEnabled;
                }
                catch
                {
                }
                string role = Party.IAmHost ? "HOST" : "MEMBER";
                string privacy = Party.OwnWorld ? PartyMenu.Privacy() : "JOINED";
                squadInfo.text = $"{role}  ·  {privacy}  ·  CHEATS {(cheats ? "ON" : "OFF")}" + (Room.Pvp ? "  ·  PVP" : "");
                string code = Profiles.PartyCode();
                codeText.text = code.Length > 0 ? code : Party.Open || !Party.OwnWorld ? "..." : "NOT OPEN";
                copyCode.Interactable = code.Length > 0;
                copyCode.Refresh();
                invite.Interactable = Party.WorldKnown && Party.OwnWorld;
                invite.Set(Party.WorldKnown && Party.OwnWorld && !Party.Open ? "OPEN + INVITE" : "INVITE FRIENDS");
                invite.Refresh();
                leave.gameObject.SetActive(Party.WorldKnown && !Party.OwnWorld);
            });
        }

        private static void Players()
        {
            var ids = new List<(int Id, bool Host, bool Owner)>();
            if (Party.Heard && Party.Members.Count > 0)
            {
                foreach (Party.Member m in Party.Members)
                {
                    ids.Add((m.Id, m.PartyHost, m.WorldHost));
                }
            }
            else
            {
                ids.Add((Room.MyId, true, true));
            }
            var sb = new System.Text.StringBuilder();
            foreach (var m in ids)
            {
                sb.Append(m.Id).Append(',');
            }
            if (sb.ToString() != rowsKey)
            {
                rowsKey = sb.ToString();
                Ui.Clear(players);
                rows.Clear();
                foreach (var m in ids)
                {
                    rows[m.Id] = MakePlayer(m.Id);
                }
            }
            foreach (var m in ids)
            {
                if (rows.TryGetValue(m.Id, out PlayerRow r))
                {
                    FillPlayer(r, m.Id, m.Host);
                }
            }
        }

        private static PlayerRow MakePlayer(int id)
        {
            bool me = id == Room.MyId;
            var r = new PlayerRow();
            float w = PageW;
            r.Root = Ui.Row(players, 62f);
            Ui.Sliced(Ui.Picture(Ui.Fill(r.Root, "Line"), Ui.SmallLine, Color.white), 2.5f);
            r.Strip = Ui.Image(r.Root, null, Color.white, 10f, 11f, 4f, 40f);
            Ui.Image(r.Root, null, new Color(0.12f, 0.12f, 0.12f, 1f), 22f, 11f, 40f, 40f);
            RectTransform av = Ui.Rect(r.Root, "Avatar", 22f, 11f, 40f, 40f);
            r.Avatar = av.gameObject.AddComponent<RawImage>();
            r.Avatar.raycastTarget = false;
            r.Crown = Ui.Image(r.Root, Ui.Crown, Color.white, 72f, 8f, 20f, 15f);
            r.Crown.rectTransform.localEulerAngles = new Vector3(0f, 0f, 18f);
            r.Name = Ui.Text(r.Root, "", 19f, Color.white, TextAlignmentOptions.MidlineLeft, 74f, 5f, 240f, 26f);
            r.You = Ui.Rect(r.Root, "You", 0f, 9f, 34f, 18f);
            Ui.Sliced(Ui.Picture(r.You, Ui.SmallLine, new Color(0.6f, 0.6f, 0.6f)), 1.6f);
            Ui.Label(Ui.Fill(r.You, "Text"), "YOU", 11f, new Color(0.7f, 0.7f, 0.7f), TextAlignmentOptions.Center);
            r.You.gameObject.SetActive(me);
            r.Status = Ui.Text(r.Root, "", 13f, new Color(0.8f, 0.8f, 0.8f), TextAlignmentOptions.MidlineLeft, 74f, 33f, 300f, 22f);
            int captured = id;
            if (me)
            {
                // (Your microphone's meter.)
                Image box = Ui.Picture(Ui.Rect(r.Root, "Meter", 360f, 26f, 220f, 10f), Ui.SmallLine, new Color(0.45f, 0.45f, 0.45f));
                Ui.Sliced(box, 1.6f);
                RectTransform fill = Ui.Rect(box.transform, "Fill", 2f, 2f, 0f, 6f);
                r.MeterFill = Ui.Picture(fill, null, Color.white);
            }
            else
            {
                r.Ping = Ui.Text(r.Root, "", 14f, Ui.Green, TextAlignmentOptions.MidlineRight, 360f, 17f, 80f, 28f);
                r.Talk = Ui.Text(r.Root, "", 16f, Ui.Orange, TextAlignmentOptions.MidlineLeft, 448f, 17f, 46f, 28f);
                r.Mute = Ui.Button(r.Root, "MUTE", 13f, () =>
                {
                    Profiles.Info i = Profiles.Of(captured);
                    i.Muted = !i.Muted;
                }, 500f, 17f, 62f, 28f, TextAlignmentOptions.Center, true, 2.2f);
                Ui.Text(r.Root, "VOL", 13f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 572f, 17f, 36f, 28f);
                r.Slider = Ui.Slider(r.Root, 608f, 18f, 150f, 26f, Profiles.Of(id).Volume / 2f, v => Profiles.Of(captured).Volume = v * 2f);
                r.Volume = Ui.Text(r.Root, "", 14f, Color.white, TextAlignmentOptions.MidlineRight, 760f, 17f, 50f, 28f);
            }
            UiButton more = Ui.MoreButton(r.Root, () => More(captured, r.Root), w - 46f, 17f, 36f, 28f);
            return r;
        }

        private static void FillPlayer(PlayerRow r, int id, bool host)
        {
            bool me = id == Room.MyId;
            Profiles.Info i = me ? Profiles.Mine : Profiles.Of(id);
            r.Strip.color = i.Color;
            r.Avatar.texture = i.Avatar;
            r.Avatar.color = i.Avatar != null ? Color.white : new Color(0f, 0f, 0f, 0f);
            r.Crown.gameObject.SetActive(host);
            float x = host ? 96f : 74f;
            r.Name.rectTransform.anchoredPosition = new Vector2(x, -5f);
            string name = me ? Profiles.Mine.SteamName : Profiles.NameOf(id);
            r.Name.text = $"<color=#{Ui.Hex(i.Color)}>{Typing.Escape((name ?? "").ToUpperInvariant())}</color>";
            if (r.You != null)
            {
                r.You.anchoredPosition = new Vector2(x + Mathf.Min(r.Name.preferredWidth, 240f) + 8f, -9f);
            }
            r.Status.text = Status(i, me);
            if (r.Ping != null)
            {
                r.Ping.text = i.Ping < 0f ? "" : $"{Mathf.RoundToInt(i.Ping)} MS";
                r.Ping.color = i.Ping < 80f ? Ui.Green : i.Ping < 160f ? Ui.Yellow : KillRed;
            }
            if (r.Mute != null)
            {
                r.Mute.Set(i.Muted ? "MUTED" : "MUTE");
                r.Mute.On = i.Muted;
                r.Mute.Accent = i.Muted ? KillRed : Color.white;
                r.Mute.TextOn = Color.white;
                r.Mute.Refresh();
                r.Volume.text = $"{Mathf.RoundToInt(i.Volume * 100f)}%";
            }
        }

        // Every frame: who's talking.
        private static void LiveRows()
        {
            if (footRun != null)
            {
                footRun.text = $"THIS RUN  {Replay.RunClock}";
            }
            if (micMeter != null && tab == 1 && micMeter.transform.parent is RectTransform meterBox)
            {
                float level = Voice.Talking ? 0.35f + 0.6f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 13f)) * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5.3f + 1f)) : 0f;
                micMeter.rectTransform.sizeDelta = new Vector2(Mathf.Max(0f, meterBox.rect.width - 4f) * level, 10f);
                micMeter.enabled = level > 0.01f;
            }
            foreach (var kv in rows)
            {
                PlayerRow r = kv.Value;
                if (r.MeterFill != null)
                {
                    float level = Voice.Talking ? 0.35f + 0.6f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 13f)) * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5.3f + 1f)) : 0f;
                    r.MeterFill.rectTransform.sizeDelta = new Vector2(216f * level, 6f);
                    r.MeterFill.enabled = level > 0.01f;
                }
                if (r.Talk != null)
                {
                    bool talking = Voice.IsSpeaking(kv.Key);
                    int dots = talking ? 1 + (int)(Time.unscaledTime * 6f) % 3 : 0;
                    r.Talk.text = talking ? new string('·', dots) : "";
                }
            }
        }

        private static string Status(Profiles.Info i, bool me)
        {
            if (!me && !i.Fresh)
            {
                return "<color=#888888>CONNECTING...</color>";
            }
            bool here = i.Scene == SceneHelper.CurrentScene;
            string where = here ? "" : " · " + Typing.Escape(Party.LevelName(i.Scene).ToUpperInvariant());
            switch (i.State)
            {
                case Profiles.StateMenu: return "<color=#BDBDBD>IN LOBBY</color>";
                case Profiles.StateDead: return (i.Flags & Profiles.FlagWatching) != 0 ? "<color=#FFA030>WATCHING</color>" + where : "<color=#E05040>DEAD</color>" + ((i.Flags & Profiles.FlagSpectating) != 0 ? " · <color=#FFA030>SPECTATING</color>" : "") + where;
                case Profiles.StateDone: return $"<color=#FFD02E>LEVEL COMPLETE</color> · {Typing.Escape(i.Rank)} {PartyHud.Clock(i.Seconds)}" + where;
                default: return $"ALIVE · {Mathf.Max(0, i.Hp)} HP" + where;
            }
        }

        // (Its "..." again: closed.)
        private static int popupFor = int.MinValue;

        private static void More(int id, RectTransform row)
        {
            if (popupFor == id && popup.gameObject.activeSelf)
            {
                popup.gameObject.SetActive(false);
                popupFor = int.MinValue;
                return;
            }
            popupFor = id;
            Ui.Clear(popup);
            bool me = id == Room.MyId;
            Party.Member member = null;
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == id)
                {
                    member = m;
                }
            }
            var items = new List<(string Label, Action Act)>();
            if (!me && member != null && Party.IAmHost && !member.PartyHost)
            {
                items.Add(("MAKE HOST", () => Party.MakeHost(member)));
            }
            if (!me && member != null && Party.IAmHost && !member.WorldHost)
            {
                items.Add(("KICK", () => Party.KickOut(member)));
            }
            if (!me)
            {
                Profiles.Info info = Profiles.Of(id);
                items.Add((info.Muted ? "UNMUTE" : "MUTE", () => info.Muted = !info.Muted));
            }
            items.Add(("STEAM PROFILE", () => Profiles.OpenSteamProfile(id)));
            const float pw = 196f, ih = 32f, gap = 6f;
            float ph = 16f + items.Count * (ih + gap) - gap;
            popup.sizeDelta = new Vector2(pw, ph);
            Image back = Ui.Picture(Ui.Fill(popup, "Back"), Ui.BoxLineBlack, Color.white);
            Ui.Sliced(back, 2.5f);
            back.raycastTarget = true;
            for (int n = 0; n < items.Count; n++)
            {
                Action act = items[n].Act;
                UiButton b = Ui.Button(popup, items[n].Label, 16f, () =>
                {
                    act();
                    popup.gameObject.SetActive(false);
                    popupFor = int.MinValue;
                }, 8f, 8f + n * (ih + gap), pw - 16f, ih, TextAlignmentOptions.Center, true, 2.2f);
                if (items[n].Label == "KICK")
                {
                    b.Accent = KillRed;
                    b.Refresh();
                }
            }
            var corners = new Vector3[4];
            row.GetWorldCorners(corners);
            popup.position = corners[2];
            // (Right under the "..." button, its right edge at the button's; above it if it'd leave the panel.)
            Vector2 at = popup.anchoredPosition;
            var below = new Vector2(at.x - 10f - pw, at.y - (17f + 28f + 4f));
            popup.anchoredPosition = -below.y + ph > H - 16f ? new Vector2(below.x, at.y - 13f + ph) : below;
            popup.SetAsLastSibling();
            popup.gameObject.SetActive(true);
        }

        // ---- VOICE, REPLAY, HUD ----

        private static Image micMeter;
        private static TextMeshProUGUI micCaption, talkingCount, lastSaved, marksText;
        private static RectTransform voicePlayers;
        private static string voiceRowsKey = "";
        private static int lastMicMode = 1;
        private static readonly List<Action> voiceRowRefresh = new List<Action>();

        private static void VoicePage(Transform p)
        {
            List<Action> r = refreshers[1];
            float colW = PageW * 0.5f - 24f;
            float pageH = H - PageY - In - Foot;
            // Left: the microphone, then listening.
            Ui.Text(p, "-- MICROPHONE --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 0f, colW, 30f);
            UiButton mic = Ui.Button(p, "", 19f, () =>
            {
                if (Plugin.VoiceMode.Value == 0)
                {
                    Plugin.VoiceMode.Value = Mathf.Max(1, lastMicMode);
                }
                else
                {
                    lastMicMode = Plugin.VoiceMode.Value;
                    Plugin.VoiceMode.Value = 0;
                }
            }, 0f, 38f, 160f, 34f, TextAlignmentOptions.Center, true, 3f);
            UiButton mode = Ui.Dropdown(p, 19f, 0f, 80f, colW, 34f, new[] { "PUSH TO TALK", "VOICE ACTIVATION" },
                () => (Plugin.VoiceMode.Value == 0 ? lastMicMode : Plugin.VoiceMode.Value) == 2 ? 1 : 0,
                i =>
                {
                    int v = i == 1 ? 2 : 1;
                    lastMicMode = v;
                    if (Plugin.VoiceMode.Value != 0)
                    {
                        Plugin.VoiceMode.Value = v;
                    }
                });
            RectTransform meter = Ui.Rect(p, "Meter", 0f, 129f, colW - 92f, 12f);
            Ui.Picture(meter, null, new Color(0.08f, 0.08f, 0.08f, 1f));
            Ui.Sliced(Ui.Picture(Ui.Fill(meter, "Line"), Ui.SmallLine, new Color(0.45f, 0.45f, 0.45f)), 1.6f);
            micMeter = Ui.Picture(Ui.Rect(meter, "Fill", 2f, 2f, 0f, 8f), null, Color.white);
            UiButton test = Ui.Button(p, "TEST", 18f, () => Voice.TestingMic = !Voice.TestingMic, colW - 82f, 121f, 82f, 30f, TextAlignmentOptions.Center, true, 3f);
            test.TextOn = Color.black;
            micCaption = Ui.Text(p, "", 15f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 0f, 158f, colW, 26f);
            Ui.Text(p, "-- LISTENING --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 196f, colW, 30f);
            SwitchRow(p, r, 234f, colW, "HEAR PLAYERS", () => !Voice.Deafened, v => Voice.Deafened = !v);
            Ui.Text(p, "VOLUME", 19f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 270f, 150f, 32f);
            TextMeshProUGUI volText = Ui.Text(p, "", 18f, Color.white, TextAlignmentOptions.MidlineRight, colW - 70f, 270f, 70f, 32f);
            UiSlider vol = Ui.Slider(p, 160f, 273f, colW - 240f, 26f, Mathf.InverseLerp(0f, 3f, Plugin.VoiceVolume.Value), v =>
            {
                Plugin.VoiceVolume.Value = Mathf.Lerp(0f, 3f, v);
                volText.text = $"{Mathf.RoundToInt(Plugin.VoiceVolume.Value * 100f)}%";
            });
            SwitchRow(p, r, 306f, colW, "ENVIRONMENT FX", () => Plugin.VoiceEffects.Value, v => Plugin.VoiceEffects.Value = v);
            TextMeshProUGUI foot = Ui.Text(p, $"Quality, device and activation level are Steam's (its voice settings). The push-to-talk key is in OPTIONS > {Brand.Name} > VOICE CHAT.",
                14f, new Color(0.45f, 0.45f, 0.45f), TextAlignmentOptions.TopLeft, 0f, 348f, colW, 52f);
            foot.enableWordWrapping = true;
            Ui.Line(p, new Color(0.35f, 0.35f, 0.35f), PageW * 0.5f, 0f, 2f, pageH);
            // Right: the players.
            float rx = PageW * 0.5f + 24f;
            Ui.Text(p, "-- PLAYERS --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, rx, 0f, 300f, 30f);
            talkingCount = Ui.Text(p, "", 16f, Ui.Grey, TextAlignmentOptions.MidlineRight, rx, 0f, colW, 30f);
            voicePlayers = Ui.Scroll(p, rx, 38f, colW, pageH - 38f, 6f, out _);
            voiceRowsKey = "";
            r.Add(() =>
            {
                int m = Plugin.VoiceMode.Value;
                mic.Set(m != 0 ? "·  MIC ON" : "<color=#888888>MIC OFF</color>");
                mic.On = false;
                mic.Refresh();
                mode.Refresh();
                test.On = Voice.TestingMic;
                test.Refresh();
                micCaption.text = m == 0 ? "MIC OFF" : m == 2 ? "ALWAYS LISTENING" : $"HOLD {(Plugin.VoiceKey.Value ?? "V").ToUpperInvariant()} TO TALK";
                vol.Set(Mathf.InverseLerp(0f, 3f, Plugin.VoiceVolume.Value));
                volText.text = $"{Mathf.RoundToInt(Plugin.VoiceVolume.Value * 100f)}%";
                VoiceRows(colW);
            });
        }

        private static void SwitchRow(Transform p, List<Action> refresh, float y, float w, string label, Func<bool> get, Action<bool> set)
        {
            Ui.Text(p, label, 19f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, y, w - 60f, 32f);
            UiSwitch sw = Ui.Switch(p, w - 50f, y + 4f, get(), set);
            refresh.Add(() => sw.Set(get()));
        }

        private static void VoiceRows(float w)
        {
            var ids = new List<int>();
            int talking = 0;
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id != Room.MyId)
                {
                    ids.Add(m.Id);
                    if (Voice.IsSpeaking(m.Id))
                    {
                        talking++;
                    }
                }
            }
            talkingCount.text = $"{talking} TALKING";
            string key = string.Join(",", ids);
            if (key != voiceRowsKey)
            {
                voiceRowsKey = key;
                Ui.Clear(voicePlayers);
                voiceRowRefresh.Clear();
                if (ids.Count == 0)
                {
                    RectTransform none = Ui.Row(voicePlayers, 40f);
                    Ui.Text(none, "NOBODY ELSE IN THE PARTY", 14f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 0f, 0f, w, 40f);
                }
                foreach (int id in ids)
                {
                    int captured = id;
                    RectTransform row = Ui.Row(voicePlayers, 80f);
                    TextMeshProUGUI name = Ui.Text(row, "", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 0f, w - 180f, 30f);
                    TextMeshProUGUI talk = Ui.Text(row, "", 16f, KillRed, TextAlignmentOptions.MidlineRight, w - 180f, 0f, 70f, 30f);
                    UiButton mute = Ui.Button(row, "MUTE", 15f, () =>
                    {
                        Profiles.Info x = Profiles.Of(captured);
                        x.Muted = !x.Muted;
                    }, w - 90f, 1f, 90f, 28f, TextAlignmentOptions.Center, true, 2.2f);
                    Ui.Text(row, "VOLUME", 13f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 0f, 38f, 70f, 28f);
                    TextMeshProUGUI pct = Ui.Text(row, "", 17f, Color.white, TextAlignmentOptions.MidlineRight, w - 60f, 38f, 60f, 28f);
                    Ui.Slider(row, 80f, 40f, w - 150f, 26f, Mathf.Clamp01(Profiles.Of(id).Volume / 2f), v =>
                    {
                        Profiles.Of(captured).Volume = v * 2f;
                        pct.text = $"{Mathf.RoundToInt(v * 200f)}%";
                    });
                    Ui.Line(row, new Color(0.3f, 0.3f, 0.3f), 0f, 76f, w, 1f);
                    voiceRowRefresh.Add(() =>
                    {
                        Profiles.Info x = Profiles.Of(captured);
                        name.text = $"<color=#{Ui.Hex(x.Color)}>{Typing.Escape(Profiles.NameOf(captured).ToUpperInvariant())}</color>";
                        talk.text = Voice.IsSpeaking(captured) ? "---" : "<color=#555555>---</color>";
                        mute.Set(x.Muted ? "MUTED" : "MUTE");
                        mute.On = x.Muted;
                        mute.Accent = x.Muted ? KillRed : Color.white;
                        mute.TextOn = Color.white;
                        mute.Refresh();
                        pct.text = $"{Mathf.RoundToInt(x.Volume * 100f)}%";
                    });
                }
            }
            foreach (Action a in voiceRowRefresh)
            {
                a();
            }
        }

        private static void ReplayPage(Transform p)
        {
            float colW = PageW * 0.5f - 24f;
            float pageH = H - PageY - In - Foot;
            Ui.Text(p, "-- REPLAY --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 0f, colW, 28f);
            TextMeshProUGUI t = Ui.Text(p, "The run is kept from level start. A replay is saved at the end, or when you press SAVE.",
                15f, Ui.Grey, TextAlignmentOptions.TopLeft, 0f, 30f, colW, 52f);
            t.enableWordWrapping = true;
            Ui.Button(p, "SAVE NOW", 19f, Replay.SaveNow, 0f, 84f, colW, 40f, TextAlignmentOptions.Center, false, 3f);
            Ui.Text(p, "-- LAST SAVED --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 0f, 160f, colW, 28f);
            lastSaved = Ui.Text(p, "", 17f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 10f, 196f, colW - 100f, 30f);
            Ui.Button(p, "FOLDER", 15f, Replay.OpenFolder, colW - 80f, 198f, 80f, 26f, TextAlignmentOptions.Center, true, 2.2f);
            Ui.Line(p, new Color(0.3f, 0.3f, 0.3f), 0f, 232f, colW, 1f);
            TextMeshProUGUI f = Ui.Text(p, $"Replays can be turned off in OPTIONS > {Brand.Name} > REPLAYS.", 15f, Ui.Grey, TextAlignmentOptions.TopLeft, 0f, 242f, colW, 40f);
            f.enableWordWrapping = true;
            Ui.Line(p, new Color(0.35f, 0.35f, 0.35f), PageW * 0.5f, 0f, 2f, pageH);
            float rx = PageW * 0.5f + 24f;
            Ui.Text(p, "-- MARKS --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, rx, 0f, colW, 28f);
            Ui.Button(p, "MARK THIS MOMENT", 19f, Replay.Mark, rx, 36f, colW, 40f, TextAlignmentOptions.Center, false, 3f);
            TextMeshProUGUI c = Ui.Text(p, "Saved with the file. Shows on the playback timeline as a bookmark.", 15f, Ui.Grey, TextAlignmentOptions.TopLeft, rx, 84f, colW, 44f);
            c.enableWordWrapping = true;
            marksText = Ui.Text(p, "", 16f, Ui.Grey, TextAlignmentOptions.TopLeft, rx, 132f, colW, pageH - 140f);
            marksText.enableWordWrapping = true;
            refreshers[2].Add(() =>
            {
                lastSaved.text = Replay.LastSavedLabel;
                marksText.text = Replay.MarksLabel;
            });
        }

        private static void HudPage(Transform p)
        {
            RectTransform list = Ui.Scroll(p, 0f, 0f, PageW, H - PageY - In - Foot, 4f, out _);
            List<Action> r = refreshers[3];
            Section(list, "PARTY HUD", "In a level.");
            Toggle(list, r, "The others' cards (top left)", () => Plugin.PartyHudOn.Value, v => Plugin.PartyHudOn.Value = v);
            Slider(list, r, "Cards' size", 0.5f, 2f, () => Plugin.HudScale.Value, v => Plugin.HudScale.Value = v, v => $"{Mathf.RoundToInt(v * 100f)}%");
            Toggle(list, r, "Names over their V1s", () => Plugin.NameTags.Value, v => Plugin.NameTags.Value = v);
            Toggle(list, r, "Chat (bottom left; Enter to type)", () => Plugin.ShowChat.Value, v => Plugin.ShowChat.Value = v);
            Section(list, "SPECTATING", "Through their eyes; with this on, F switches to behind them.");
            Toggle(list, r, "Third person (F)", () => Plugin.SpectateThirdPerson.Value, v => Plugin.SpectateThirdPerson.Value = v);
            Section(list, "YOUR LOOK", "How the others see your V1.");
            Palette(list, r, "V1's colour", () => Plugin.BodyColour.Value, v => Plugin.BodyColour.Value = v);
            Palette(list, r, "Lights", () => Plugin.LightsColour.Value, v => Plugin.LightsColour.Value = v);
            Palette(list, r, "Wings' glow", () => Plugin.WingsColour.Value, v => Plugin.WingsColour.Value = v);
            Cycle(list, r, "Your colour (name, card)", () => Plugin.MyColor.Value >= 0 && Plugin.MyColor.Value < Profiles.Colors.Length ? Profiles.Colors[Plugin.MyColor.Value].Name : "AUTO",
                () => Plugin.MyColor.Value = Plugin.MyColor.Value + 1 >= Profiles.Colors.Length ? -1 : Plugin.MyColor.Value + 1);
        }

        // ---- rows ----

        private const float Control = 500f;

        private static void Section(Transform list, string title, string note = null)
        {
            RectTransform row = Ui.Row(list, note != null ? 62f : 44f);
            Ui.Text(row, $"-- {title} --", 20f, Color.white, TextAlignmentOptions.MidlineLeft, 4f, 8f, 600f, 30f);
            if (note != null)
            {
                Ui.Text(row, note, 15f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 4f, 38f, PageW - 8f, 20f);
            }
        }

        private static RectTransform Labeled(Transform list, string label)
        {
            RectTransform row = Ui.Row(list, 40f);
            Ui.Text(row, label.ToUpperInvariant(), 18f, Color.white, TextAlignmentOptions.MidlineLeft, 12f, 4f, Control - 20f, 32f);
            return row;
        }

        private static void Toggle(Transform list, List<Action> refresh, string label, Func<bool> get, Action<bool> set)
        {
            RectTransform row = Labeled(list, label);
            UiSwitch sw = Ui.Switch(row, Control, 8f, get(), set);
            refresh.Add(() => sw.Set(get()));
        }

        private static void Cycle(Transform list, List<Action> refresh, string label, Func<string> shown, Action next)
        {
            RectTransform row = Labeled(list, label);
            UiButton b = null;
            b = Ui.Button(row, "", 17f, () =>
            {
                next();
                b.Set($"<  {shown()}  >");
            }, Control, 5f, 330f, 30f, TextAlignmentOptions.Center, true, 3f);
            refresh.Add(() => b.Set($"<  {shown()}  >"));
            b.Set($"<  {shown()}  >");
        }

        private static void Slider(Transform list, List<Action> refresh, string label, float min, float max, Func<float> get, Action<float> set, Func<float, string> shown)
        {
            RectTransform row = Labeled(list, label);
            TextMeshProUGUI value = Ui.Text(row, "", 15f, Color.white, TextAlignmentOptions.MidlineRight, Control + 250f, 4f, PageW - Control - 258f, 32f);
            UiSlider s = Ui.Slider(row, Control, 7f, 240f, 26f, Mathf.InverseLerp(min, max, get()), v =>
            {
                float real = Mathf.Lerp(min, max, v);
                set(real);
                value.text = shown(get());
            });
            refresh.Add(() =>
            {
                s.Set(Mathf.InverseLerp(min, max, get()));
                value.text = shown(get());
            });
            value.text = shown(get());
        }

        private static void Button(Transform list, string label, string button, Action click)
        {
            RectTransform row = Labeled(list, label);
            Ui.Button(row, button, 15f, click, Control, 5f, 120f, 30f, TextAlignmentOptions.Center, true, 3f);
        }

        // A colour from a palette (filled: the one chosen), or V1's own.
        private static readonly string[] PaletteHex =
        {
            "FFFFFF", "BFBFBF", "606060", "151515", "E03030", "FF7A1A", "FFC81E", "B8E62E",
            "35C04A", "1FB89A", "2FD4F0", "4A9BFF", "2B4FE0", "7A3FD1", "B83FD6", "FF5FA8",
        };

        private static void Palette(Transform list, List<Action> refresh, string label, Func<string> get, Action<string> set)
        {
            RectTransform row = Ui.Row(list, 44f);
            Ui.Text(row, label.ToUpperInvariant(), 18f, Color.white, TextAlignmentOptions.MidlineLeft, 12f, 6f, 176f, 32f);
            var buttons = new List<(UiButton B, string Hex)>();
            UiButton own = null;
            Action show = () =>
            {
                string now = (get() ?? "").Trim().TrimStart('#').ToUpperInvariant();
                foreach (var b in buttons)
                {
                    b.B.On = b.Hex == now;
                    b.B.Refresh();
                }
                own.On = now.Length == 0;
                own.Refresh();
            };
            own = Ui.Button(row, "V1'S OWN", 12f, () =>
            {
                set("");
                show();
            }, 190f, 9f, 92f, 26f, TextAlignmentOptions.Center, true, 2f);
            own.TextOn = Color.black;
            for (int k = 0; k < PaletteHex.Length; k++)
            {
                string hex = PaletteHex[k];
                ColorUtility.TryParseHtmlString("#" + hex, out Color c);
                UiButton b = Ui.Button(row, "", 10f, () =>
                {
                    set(hex);
                    show();
                }, 292f + k * 32f, 9f, 26f, 26f, TextAlignmentOptions.Center, true, 3f);
                b.Accent = c;
                b.Refresh();
                buttons.Add((b, hex));
            }
            refresh.Add(show);
            show();
        }
    }
}
