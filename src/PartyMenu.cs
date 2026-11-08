using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Killcraft
{
    // The party menu: PARTY under PLAY in the main menu (UltraLink's layout). Out of a party it opens on
    // CONNECT: on the left HOST PARTY, JOIN BY CODE, PUBLIC LOBBIES, REJOIN; in the middle Killcraft's
    // logo and its start-up lines; on the right what the chosen one needs (the new party's name, size,
    // privacy and cheats; a code; the open parties). In a party: on the left the party (its name, code,
    // privacy, difficulty) and CONTROL (invite, the level, leave); in the middle the SQUAD (their Steam
    // picture and name, where they are, their ping, the host's crown, a ... for the rest); on the right
    // the CHAT. VOICE (the microphone, a test, deafen, your colour), BACK and SETTINGS in both. Its
    // parts appear row by row, top down, when it opens (as ULTRAKILL's menus do).
    internal static class PartyMenu
    {
        private sealed class MemberRow
        {
            public RectTransform Root;
            public Image Strip, Crown, Glow;
            public RawImage Avatar;
            public TextMeshProUGUI Name, Status, Ping;
            public Image[] Bars;
            public UiButton More, Watch;
        }

        private static Canvas canvas;
        private static GameObject mainMenu, playButton;
        private static Scene builtFor;
        private static float refreshAt, buttonTriedAt = -100f;

        // The screen's parts: shared, out of a party (connect), in one (party).
        private static RectTransform common, connectView, partyView;
        private static bool? inPartyShown;
        private static float viewShownAt;

        private static TextMeshProUGUI partyName, codeText, infoText, squadHeader, chat, log, centerTitle;
        private static UiButton copy, back, settings, chatBox, privacy;
        private static float chatHeight = 796f;
        private static readonly UiButton[] control = new UiButton[3];
        private static readonly UiButton[] voice = new UiButton[4];
        private static Image swatch, ownBlack;
        private static GameObject ownCode, ownFrame;
        private static UiButton playAlone;
        private static readonly List<GameObject> hiddenMenuParts = new List<GameObject>();
        private static RectTransform squad, servers, popup;
        private static GameObject squadView, serversView;
        private static readonly Dictionary<int, MemberRow> rows = new Dictionary<int, MemberRow>();
        private static string rowsKey = "", serversKey = "", lobbiesKey = "";
        private static bool showServers;
        private static float serversAskedAt = -100f;
        private static int popupFor = int.MinValue;
        private static int chatShown = -1;
        private static readonly object chatOwner = new object(), codeOwner = new object(), nameOwner = new object(), maxOwner = new object();

        // CONNECT's parts.
        private enum Mode { Host, Join, Lobbies }
        private static Mode mode = Mode.Host;
        private static readonly UiButton[] connect = new UiButton[4];
        private static string bootShown = "";
        private static TextMeshProUGUI boot, panelTitle, lobbiesStatus, joinHint, rejoinNote;
        private static GameObject hostPanel, joinPanel, lobbiesPanel;
        private static UiButton nameField, maxField, typeButton, cheatsButton, createButton, codeField, joinButton;
        private static RectTransform lobbies;

        public static bool Open => canvas != null && canvas.gameObject.activeSelf;
        internal static Canvas MenuCanvas => canvas;
        // (The main menu's own first screen up, the party screen not: back out of the level select.)
        internal static bool MenuTopShown => mainMenu != null && mainMenu.activeInHierarchy && !Open;

        // QUIT MISSION in a party: the party screen when the main menu's back.
        public static bool OpenOnMenu;

        // A wake (PartyWake): the party screen up, on the party's view.
        // (The main menu's other screens beside it - the level select the host picked from - put away
        // for the wake, and given back as they were if it's stopped.)
        private static readonly List<GameObject> wakeHidden = new List<GameObject>();
        private static bool menuWasUp = true, wakeOpened;

        internal static void OpenForWake()
        {
            if (!Open)
            {
                if (!wakeOpened)
                {
                    menuWasUp = mainMenu == null || mainMenu.activeSelf;
                }
                wakeOpened = true;
                Show();
            }
            Transform parent = mainMenu != null ? mainMenu.transform.parent : null;
            if (parent == null)
            {
                return;
            }
            foreach (Transform t in parent)
            {
                GameObject g = t.gameObject;
                if (g == mainMenu || !g.activeSelf || (canvas != null && t == canvas.transform))
                {
                    continue;
                }
                g.SetActive(false);
                wakeHidden.Add(g);
            }
        }

        // A wake stopped: the party screen away, the screen picked from back.
        internal static void CloseAfterWake()
        {
            if (!wakeOpened)
            {
                return;
            }
            wakeOpened = false;
            if (Typing.Owner == chatOwner)
            {
                Typing.End();
            }
            if (canvas != null)
            {
                canvas.gameObject.SetActive(false);
            }
            KillcraftPanel.Hide();
            ShowMenu();
            foreach (GameObject g in wakeHidden)
            {
                if (g != null)
                {
                    g.SetActive(true);
                }
            }
            wakeHidden.Clear();
            if (mainMenu != null)
            {
                mainMenu.SetActive(menuWasUp);
            }
        }

        // (Gone to the level: nothing to give back.)
        internal static void WakeDone()
        {
            wakeOpened = false;
            wakeHidden.Clear();
        }

        // During a wake: the main menu's black and circuit diagram (or this screen's own black) away, and
        // ULTRAKILL's main menu fall - the 3D scene under the menus - seen behind the party screen.
        private static readonly List<GameObject> fallHidden = new List<GameObject>();
        private static bool fallShown;

        internal static void ShowFall(bool on)
        {
            if (on == fallShown)
            {
                return;
            }
            fallShown = on;
            if (on)
            {
                if (mainMenu != null)
                {
                    foreach (string name in new[] { "Image", "BackgroundSwapper" })
                    {
                        Transform t = mainMenu.transform.Find(name);
                        if (t != null && t.gameObject.activeSelf)
                        {
                            t.gameObject.SetActive(false);
                            fallHidden.Add(t.gameObject);
                        }
                    }
                }
                if (ownBlack != null)
                {
                    ownBlack.enabled = false;
                }
                if (ownCode != null)
                {
                    ownCode.SetActive(false);
                }
                return;
            }
            foreach (GameObject g in fallHidden)
            {
                if (g != null)
                {
                    g.SetActive(true);
                }
            }
            fallHidden.Clear();
            if (ownBlack != null)
            {
                ownBlack.enabled = true;
                if (ownCode != null)
                {
                    ownCode.SetActive(ownBlack.color.a > 0.5f);
                }
            }
        }

        // The screen's frame in a colour (red during a wake), its own or the main menu's.
        private static Color[] borderWas;

        internal static void TintFrame(Color c)
        {
            if (ownFrame != null)
            {
                Image f = ownFrame.GetComponent<Image>();
                if (f != null && f.color != c)
                {
                    f.color = c;
                }
            }
            Transform border = mainMenu != null ? mainMenu.transform.Find("Border") : null;
            if (border == null)
            {
                return;
            }
            Image[] parts = border.GetComponentsInChildren<Image>(true);
            if (borderWas == null || borderWas.Length != parts.Length)
            {
                borderWas = new Color[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    borderWas[i] = parts[i].color;
                }
            }
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].color = c == Color.white ? borderWas[i] : new Color(c.r, c.g, c.b, borderWas[i].a);
            }
        }

        public static void Frame(bool inGame, bool paused, bool mainMenuUp, bool loading)
        {
            Scene scene = SceneManager.GetActiveScene();
            // (Back from QUIT MISSION in a party: tried every frame, the loading screen over it till then.)
            if (mainMenuUp && !loading && builtFor != scene && (OpenOnMenu || Time.unscaledTime - buttonTriedAt > 1f))
            {
                buttonTriedAt = Time.unscaledTime;
                if (AddToMainMenu())
                {
                    builtFor = scene;
                    if (OpenOnMenu)
                    {
                        OpenOnMenu = false;
                        if (SteamNet.InLobby)
                        {
                            Plugin.Log.LogInfo("party menu: back from a level in a party: the party screen");
                            Show();
                        }
                        LoadingScreen.Hide();
                    }
                }
            }
            if (!Open)
            {
                return;
            }
            if (!mainMenuUp || loading)
            {
                Close();
                return;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && !PartyWake.Active && !Typing.Active && Typing.EndedFrame != Time.frameCount && !KillcraftPanel.Open && KillcraftPanel.ClosedFrame != Time.frameCount)
            {
                if (popup != null && popup.gameObject.activeSelf)
                {
                    popup.gameObject.SetActive(false);
                    popupFor = int.MinValue;
                }
                else
                {
                    Close();
                    return;
                }
            }
            Live();
            if (Time.unscaledTime >= refreshAt)
            {
                refreshAt = Time.unscaledTime + 0.25f;
                try
                {
                    Refresh();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"party menu: {e}");
                    refreshAt = Time.unscaledTime + 5f;
                }
            }
        }

        // ---- PARTY in the main menu ----

        private static bool AddToMainMenu()
        {
            Transform main = null;
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Transform t = root.transform.Find("Main Menu (1)");
                if (t != null)
                {
                    main = t;
                    break;
                }
            }
            Transform left = main != null ? main.Find("LeftSide") : null;
            Transform play = left != null ? left.Find("Continue") : null;
            Transform options = left != null ? left.Find("Options") : null;
            if (play == null || options == null)
            {
                return false;
            }
            if (left.Find("Killcraft Party") != null)
            {
                return true;
            }
            Transform museum = left.Find("Credits");
            Transform after = museum != null ? museum : options;
            var afterRt = (RectTransform)after;
            var playRt = (RectTransform)play;
            float step = playRt.anchoredPosition.y - ((RectTransform)options).anchoredPosition.y;
            if (step < 20f || step > 200f)
            {
                step = 75f;
            }
            float y = afterRt.anchoredPosition.y;
            foreach (Transform t in left)
            {
                if (t is RectTransform rt && t != after && rt.anchoredPosition.y < y - 1f)
                {
                    rt.anchoredPosition -= new Vector2(0f, step);
                }
            }
            // Under MUSEUM, half a row each: REPLAYS left, PARTY right (UltraLink's main menu).
            GameObject replays = Half(options.gameObject, left, after, "Killcraft Replays", "REPLAYS", y - step, true);
            GameObject b = Half(options.gameObject, left, replays.transform, "Killcraft Party", "PARTY", y - step, false);
            mainMenu = main.gameObject;
            playButton = play.gameObject;
            Wire(replays, Replays.Show);
            Wire(b, Show);
            var seq = left.GetComponent<ObjectActivateInSequence>();
            if (seq != null && seq.objectsToActivate != null)
            {
                var list = new List<GameObject>(seq.objectsToActivate);
                int at = list.IndexOf(after.gameObject);
                list.Insert(at >= 0 ? at + 1 : list.Count, replays);
                list.Insert(at >= 0 ? at + 2 : list.Count, b);
                seq.objectsToActivate = list.ToArray();
            }
            Plugin.Log.LogInfo("party menu: REPLAYS and PARTY added to the main menu");
            return true;
        }

        private static void Wire(GameObject b, UnityEngine.Events.UnityAction click)
        {
            var button = b.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(click);
        }

        // Half a row (OPTIONS's look), the left or the right half of where OPTIONS's row sits.
        private static GameObject Half(GameObject template, Transform left, Transform after, string name, string label, float y, bool first)
        {
            GameObject b = UnityEngine.Object.Instantiate(template, left);
            b.name = name;
            b.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);
            var rt = (RectTransform)b.transform;
            var tr = (RectTransform)template.transform;
            const float gap = 10f;
            float w = tr.rect.width;
            if (Mathf.Abs(tr.anchorMin.x - tr.anchorMax.x) > 0.001f)
            {
                // (Stretched across its parent: an edge pulled in to the middle.)
                if (first)
                {
                    rt.offsetMax = new Vector2(tr.offsetMax.x - (w + gap) / 2f, tr.offsetMax.y);
                }
                else
                {
                    rt.offsetMin = new Vector2(tr.offsetMin.x + (w + gap) / 2f, tr.offsetMin.y);
                }
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y);
            }
            else
            {
                float nw = (w - gap) / 2f;
                float leftEdge = tr.anchoredPosition.x - tr.pivot.x * w;
                rt.sizeDelta = new Vector2(nw, tr.sizeDelta.y);
                rt.anchoredPosition = new Vector2(leftEdge + (first ? 0f : nw + gap) + tr.pivot.x * nw, y);
            }
            foreach (GamepadObjectSelector g in b.GetComponents<GamepadObjectSelector>())
            {
                UnityEngine.Object.DestroyImmediate(g);
            }
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.text = label;
            }
            return b;
        }

        private static void Show()
        {
            try
            {
                if (canvas == null)
                {
                    Build();
                }
                Ui.Refont(canvas.transform);
                Party.PlayAlone = false;
                HideMenu();
                canvas.gameObject.SetActive(true);
                rowsKey = "";
                serversKey = "";
                lobbiesKey = "";
                chatShown = -1;
                refreshAt = 0f;
                inPartyShown = null;
                Refresh();
                // (Row by row, top down: the shared parts and the view shown.)
                Ui.Reveal((RectTransform)canvas.transform, common, InParty ? partyView : connectView);
                if (ServerList.Found.Count == 0 && !ServerList.Searching)
                {
                    ServerList.Refresh();
                }
                Plugin.Log.LogInfo("party menu: opened");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party menu: couldn't make it ({e})");
                if (canvas != null)
                {
                    UnityEngine.Object.Destroy(canvas.gameObject);
                    canvas = null;
                }
                ShowMenu();
            }
        }

        // The main menu put away under the party menu, but for its background: its black, its frame,
        // and its code and circuit diagram taking turns (BackgroundSwapper), as UltraLink's.
        // (The replays screen keeps the menu's V1 picture.)
        internal static void HideMenu(bool keepV1 = false)
        {
            hiddenMenuParts.Clear();
            bool own = true;
            if (mainMenu != null)
            {
                if (mainMenu.transform.Find("BackgroundSwapper") != null)
                {
                    own = false;
                    foreach (Transform t in mainMenu.transform)
                    {
                        if (t.name == "Image" || t.name == "BackgroundSwapper" || t.name == "Border" || (keepV1 && t.name == "V1"))
                        {
                            continue;
                        }
                        if (t.gameObject.activeSelf)
                        {
                            t.gameObject.SetActive(false);
                            hiddenMenuParts.Add(t.gameObject);
                        }
                    }
                    mainMenu.SetActive(true);
                }
                else
                {
                    mainMenu.SetActive(false);
                }
            }
            if (ownBlack != null)
            {
                ownBlack.color = own ? Color.black : new Color(0f, 0f, 0f, 0f);
            }
            if (ownCode != null)
            {
                ownCode.SetActive(own);
            }
            if (ownFrame != null)
            {
                ownFrame.SetActive(own);
            }
        }

        internal static void ShowMenu()
        {
            foreach (GameObject g in hiddenMenuParts)
            {
                if (g != null)
                {
                    g.SetActive(true);
                }
            }
            hiddenMenuParts.Clear();
            if (mainMenu != null)
            {
                mainMenu.SetActive(true);
            }
        }

        private static void Close()
        {
            CommitMax();
            if (Typing.Owner == chatOwner || Typing.Owner == codeOwner || Typing.Owner == nameOwner)
            {
                Typing.End();
            }
            if (canvas != null)
            {
                canvas.gameObject.SetActive(false);
            }
            KillcraftPanel.Hide();
            ShowMenu();
        }

        // ---- made ----

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft party menu", 60, false);
            Transform root = canvas.transform;
            rows.Clear();

            // The main menu's look: black, framed in white, its code faintly behind.
            ownBlack = Ui.Picture(Ui.Fill(root, "Black"), null, Color.black);
            ownBlack.raycastTarget = true;
            TextMeshProUGUI code = Ui.Text(root, string.Join("\n", Backdrop), 22f, new Color(0.17f, 0.17f, 0.17f), TextAlignmentOptions.TopLeft, 30f, 34f, 1000f, 520f);
            code.overflowMode = TextOverflowModes.Overflow;
            ownCode = code.gameObject;
            ownFrame = Ui.Picture(Ui.Fill(root, "Frame", 4f), Ui.FrameLine, Color.white).gameObject;

            common = Ui.Fill(root, "Common");
            connectView = Ui.Fill(root, "Connect");
            partyView = Ui.Fill(root, "Party");

            // ---- shared: the title, VOICE, BACK, SETTINGS, the version ----
            Ui.Text(common, "--PARTY--", 96f, Color.white, TextAlignmentOptions.TopLeft, 36f, 18f, 600f, 110f);
            Header(common, "-- VOICE --", 608f);
            for (int i = 0; i < voice.Length; i++)
            {
                int n = i;
                voice[i] = Ui.Button(common, "", 32f, () => VoiceClicked(n), 30f, 668f + i * 73f, 570f, 68f, TextAlignmentOptions.MidlineLeft);
            }
            swatch = Ui.Image(voice[3].transform, Ui.SmallFill, Color.white, 494f, 20f, 50f, 28f);
            back = Ui.Button(common, "BACK", 32f, Close, 30f, 984f, 278f, 70f);
            settings = Ui.Button(common, "SETTINGS", 32f, () => KillcraftPanel.Show(null), 322f, 984f, 278f, 70f);
            settings.Accent = Ui.Red;
            settings.Refresh();
            Ui.Text(common, $"<color=#{Ui.Hex(Ui.Red)}>[ {Brand.Name} ] v2.0</color>", 22f, Color.white, TextAlignmentOptions.BottomRight, 1300f, 1030f, 596f, 36f);

            BuildConnect();
            BuildParty();

            // The ... menu.
            popup = Ui.Rect(root, "More", 0f, 0f, 260f, 10f);
            popup.gameObject.AddComponent<UiRevealSkip>();
            popup.gameObject.SetActive(false);
            canvas.gameObject.SetActive(false);
        }

        // Out of a party: CONNECT on the left, the logo in the middle, the chosen one's panel on the right.
        private static void BuildConnect()
        {
            RectTransform v = connectView;
            Header(v, "-- CONNECT --", 160f);
            string[] labels = { "HOST PARTY", "JOIN BY CODE", "PUBLIC LOBBIES", "REJOIN" };
            for (int i = 0; i < connect.Length; i++)
            {
                int n = i;
                connect[i] = Ui.Button(v, labels[i], 32f, () => ConnectClicked(n), 30f, 228f + i * 83f, 570f, 72f, TextAlignmentOptions.MidlineLeft);
            }
            // (Under REJOIN: why it didn't work, a while.)
            rejoinNote = Ui.Text(v, "", 18f, Ui.Red, TextAlignmentOptions.TopLeft, 34f, 556f, 566f, 44f);
            rejoinNote.enableWordWrapping = true;

            // The middle: the logo, KILLCRAFT, what's ready.
            // (On the screen's middle, whatever its shape: the start-up lines' block too, its width theirs.)
            Image logoImage = Ui.Image(v, Brand.Logo, Ui.Red, 0f, 0f, 120f, 120f);
            logoImage.preserveAspect = true;
            Centered(logoImage.rectTransform, 138f, 120f, 120f);
            TextMeshProUGUI name = Ui.Text(v, Brand.Name, 96f, Ui.Red, TextAlignmentOptions.Center, 0f, 0f, 620f, 110f);
            Centered(name.rectTransform, 268f, 700f, 110f);
            boot = Ui.Text(v, "", 22f, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.TopLeft, 0f, 0f, 560f, 260f);
            boot.overflowMode = TextOverflowModes.Overflow;
            Centered(boot.rectTransform, 410f, 560f, 260f);

            // The right: what the chosen one needs.
            const float X = 1316f, Wd = 574f;
            panelTitle = Ui.Text(v, "", 30f, Color.white, TextAlignmentOptions.Center, X, 160f, Wd, 46f);
            Ui.Line(v, Color.white, X, 210f, Wd, 3f);

            hostPanel = Ui.Rect(v, "Host", X, 228f, Wd, 600f).gameObject;
            Transform h = hostPanel.transform;
            Ui.Text(h, "LOBBY NAME", 20f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 2f, 4f, Wd, 28f);
            nameField = Ui.Field(h, 26f, () =>
            {
                CommitMax();
                if (Typing.Owner == nameOwner)
                {
                    Typing.End();
                    return;
                }
                Typing.Begin(nameOwner, 32, text => Plugin.PartyTitle.Value = text.Trim(), Plugin.PartyTitle.Value ?? "");
            }, 0f, 36f, Wd, 58f);
            Ui.Text(h, "MAX PLAYERS", 20f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 2f, 116f, Wd, 28f);
            maxField = Ui.Field(h, 26f, () =>
            {
                if (Typing.Owner == maxOwner)
                {
                    CommitMax();
                    return;
                }
                if (Typing.Owner == nameOwner)
                {
                    Plugin.PartyTitle.Value = Typing.Text.Trim();
                }
                Typing.Begin(maxOwner, 2, SetMax);
            }, 0f, 148f, 176f, 58f);
            Ui.Text(h, "CLICK, TYPE 2-10, ENTER", 16f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 196f, 148f, 360f, 58f);
            typeButton = Ui.Button(h, "", 32f, CyclePrivacy, 0f, 240f, Wd, 72f, TextAlignmentOptions.MidlineLeft);
            cheatsButton = Ui.Button(h, "", 32f, () => Plugin.PartyCheats.Value = !Plugin.PartyCheats.Value, 0f, 324f, Wd, 72f, TextAlignmentOptions.MidlineLeft);
            createButton = Ui.Button(h, "CREATE", 32f, Create, 0f, 444f, Wd, 72f);
            createButton.On = true;
            createButton.Refresh();

            joinPanel = Ui.Rect(v, "Join", X, 228f, Wd, 600f).gameObject;
            Transform j = joinPanel.transform;
            Ui.Text(j, "PARTY CODE", 20f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 2f, 4f, Wd, 28f);
            codeField = Ui.Field(j, 26f, () =>
            {
                if (Typing.Owner == codeOwner)
                {
                    Typing.End();
                    return;
                }
                Typing.Begin(codeOwner, 12, ServerList.JoinByCode);
            }, 0f, 36f, Wd, 58f);
            joinButton = Ui.Button(j, "JOIN", 32f, () =>
            {
                if (Typing.Owner == codeOwner && Typing.Text.Trim().Length > 0)
                {
                    string typed = Typing.Text;
                    Typing.End();
                    ServerList.JoinByCode(typed);
                }
                else if (Typing.Owner != codeOwner)
                {
                    Typing.Begin(codeOwner, 12, ServerList.JoinByCode);
                }
            }, 0f, 124f, Wd, 72f);
            joinButton.On = true;
            joinButton.Refresh();
            joinHint = Ui.Text(j, "", 20f, Ui.Grey, TextAlignmentOptions.TopLeft, 2f, 220f, Wd, 120f);
            joinHint.enableWordWrapping = true;

            lobbiesPanel = Ui.Rect(v, "Lobbies", X, 228f, Wd, 760f).gameObject;
            Transform l = lobbiesPanel.transform;
            lobbiesStatus = Ui.Text(l, "", 18f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 2f, 0f, 420f, 38f);
            Ui.Button(l, "REFRESH", 18f, ServerList.Refresh, Wd - 140f, 0f, 140f, 38f, TextAlignmentOptions.Center, true, 3f);
            lobbies = Ui.Scroll(l, 0f, 52f, Wd, 700f, 8f, out _);
        }

        // Anchored at the top middle of the screen, y down from its top.
        private static void Centered(RectTransform rt, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(0f, -y);
        }

        // In a party: the party, CONTROL, the squad (or the open parties), the chat.
        private static void BuildParty()
        {
            RectTransform v = partyView;
            partyName = Ui.Text(v, "", 34f, Color.white, TextAlignmentOptions.MidlineLeft, 30f, 134f, 600f, 42f);
            codeText = Ui.Text(v, "", 22f, Color.white, TextAlignmentOptions.MidlineLeft, 30f, 180f, 350f, 34f);
            copy = Ui.Button(v, "COPY", 18f, () =>
            {
                string c = Profiles.PartyCode();
                if (c.Length > 0)
                {
                    GUIUtility.systemCopyBuffer = c;
                    PartyChat.Event("PARTY CODE COPIED");
                }
            }, 385f, 176f, 104f, 38f, TextAlignmentOptions.Center, true, 3f);
            infoText = Ui.Text(v, "", 18f, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.MidlineLeft, 30f, 221f, 350f, 30f);
            privacy = Ui.Button(v, "CHANGE", 16f, CyclePrivacy, 385f, 220f, 104f, 32f, TextAlignmentOptions.Center, true, 2.5f);

            Header(v, "-- CONTROL --", 270f);
            for (int i = 0; i < control.Length; i++)
            {
                int n = i;
                control[i] = Ui.Button(v, "", 32f, () => ControlClicked(n), 30f, 340f + i * 83f, 570f, 72f, TextAlignmentOptions.MidlineLeft);
            }
            // (Over SELECT LEVEL: a level of this player's own, the party staying where it is.)
            playAlone = Ui.Button(control[1].transform, "PLAY ALONE", 18f, () =>
            {
                Party.PlayAlone = true;
                SelectLevel();
            }, 418f, 17f, 138f, 38f, TextAlignmentOptions.Center, true, 3f);
            playAlone.gameObject.SetActive(false);

            // Middle: the squad, or the open parties.
            squadHeader = Ui.Text(v, "", 34f, Color.white, TextAlignmentOptions.Center, 650f, 30f, 620f, 50f);
            Ui.Line(v, Color.white, 650f, 80f, 620f, 3f);
            squadView = Ui.Rect(v, "Squad", 650f, 88f, 620f, 900f).gameObject;
            squad = Ui.Scroll(squadView.transform, 0f, 0f, 620f, 900f, 0f, out _);
            serversView = Ui.Rect(v, "Servers", 650f, 88f, 620f, 900f).gameObject;
            Ui.Button(serversView.transform, "REFRESH", 18f, ServerList.Refresh, 480f, 10f, 140f, 38f, TextAlignmentOptions.Center, true, 3f);
            centerTitle = Ui.Text(serversView.transform, "", 18f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 6f, 10f, 460f, 38f);
            servers = Ui.Scroll(serversView.transform, 0f, 60f, 620f, 840f, 8f, out _);
            serversView.SetActive(false);

            // Right: the chat (newest at the bottom of what's there, from the top down).
            Ui.Text(v, "-- CHAT --", 34f, Color.white, TextAlignmentOptions.MidlineLeft, 1350f, 42f, 540f, 48f);
            Ui.Line(v, Color.white, 1316f, 92f, 574f, 3f);
            RectTransform chatArea = Ui.Rect(v, "Chat", 1316f, 104f, 574f, 800f);
            chatArea.gameObject.AddComponent<RectMask2D>();
            chat = Ui.Label(Ui.Fill(chatArea, "Lines", 2f), "", 20f, Color.white, TextAlignmentOptions.TopLeft);
            chat.enableWordWrapping = true;
            chat.overflowMode = TextOverflowModes.Overflow;
            chatHeight = 796f;
            Ui.Line(v, Color.white, 1316f, 915f, 574f, 3f);
            chatBox = Ui.Button(v, "TYPE HERE...", 24f, () =>
            {
                if (Typing.Owner != chatOwner)
                {
                    Typing.Begin(chatOwner, 200, text => PartyChat.Say(text));
                }
            }, 1316f, 931f, 574f, 56f, TextAlignmentOptions.MidlineLeft, false, 3.5f);

            // Along the bottom: what's happened.
            log = Ui.Text(v, "", 16f, Color.white, TextAlignmentOptions.BottomLeft, 656f, 1000f, 640f, 70f);
        }

        // (The main menu's backdrop of code, faint.)
        private static readonly string[] Backdrop =
        {
            "#INCLUDE { above, so } FROM \"dHJpc21lZ2lzdHVz\";",
            "#INCLUDE { permutation , transmutation, exhalation } FROM \"cHJpbmNpcGl\";",
            "",
            "void* below = x => compose(so, () => above)(x);",
            "void* reify = partial_apply(below, transmutation);",
            "void* deify = #MACRO_INVERT(x) partial_apply(reify, x);",
            "",
            "typedef matter(t_form) = #DERIVE reify(t_form);",
            "",
            "#INCLUDE { blood, formal_blood } FROM \"d29tYg==\"",
            "",
            "typedef leaf = matter(blood);",
            "struct branch {",
            "    leaf* leafPtr;",
            "};",
            "",
            "struct tree {",
            "    branch* branch_arr;",
            "};",
        };

        private static void Header(Transform parent, string text, float y)
        {
            Ui.Text(parent, text, 30f, Color.white, TextAlignmentOptions.MidlineLeft, 62f, y, 540f, 46f);
            Ui.Line(parent, Color.white, 30f, y + 50f, 570f, 3f);
        }

        // ---- what the buttons do ----

        private static bool InParty => Party.WorldKnown && (Party.Open || !Party.OwnWorld);

        private static void ConnectClicked(int n)
        {
            if (!Party.WorldKnown)
            {
                return;
            }
            switch (n)
            {
                case 0:
                    mode = Mode.Host;
                    break;
                case 1:
                    mode = Mode.Join;
                    if (Typing.Owner != codeOwner)
                    {
                        Typing.Begin(codeOwner, 12, ServerList.JoinByCode);
                    }
                    break;
                case 2:
                    mode = Mode.Lobbies;
                    lobbiesKey = "";
                    serversAskedAt = Time.unscaledTime;
                    ServerList.Refresh();
                    break;
                case 3:
                    if (ServerList.LastParty(out ulong id, out string name))
                    {
                        ServerList.JoinParty(id, name);
                        PartyChat.System($"Rejoining {Typing.Escape(name)}...");
                    }
                    break;
            }
            if (n != 1 && Typing.Owner == codeOwner)
            {
                Typing.End();
            }
            if (n != 0 && Typing.Owner == nameOwner)
            {
                Typing.End();
            }
            if (n != 0)
            {
                CommitMax();
            }
            refreshAt = 0f;
        }

        // MAX PLAYERS typed: its digits, 2 to 10 (anything else: as it was).
        private static void SetMax(string typed)
        {
            string digits = new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(typed ?? "", char.IsDigit)));
            if (int.TryParse(digits, out int m) && digits.Length > 0)
            {
                Plugin.PartyMax.Value = Mathf.Clamp(m, 2, 10);
            }
        }

        // (Typing in MAX PLAYERS and clicked elsewhere: what's typed counts, as Enter would.)
        private static void CommitMax()
        {
            if (Typing.Owner == maxOwner)
            {
                string typed = Typing.Text;
                Typing.End();
                SetMax(typed);
            }
        }

        // HOST PARTY's CREATE: the party, as set above it.
        private static void Create()
        {
            CommitMax();
            if (Typing.Owner == nameOwner)
            {
                Plugin.PartyTitle.Value = Typing.Text.Trim();
                Typing.End();
            }
            if (ServerList.Making || !Party.WorldKnown)
            {
                return;
            }
            Party.OpenWorld();
            PartyChat.System($"Creating {Typing.Escape(ServerList.Title)}...");
        }

        private static void ControlClicked(int n)
        {
            if (!Party.WorldKnown || !InParty)
            {
                return;
            }
            switch (n)
            {
                case 0:
                    ServerList.InviteOverlay();
                    break;
                case 1:
                    if (Party.IAmHost)
                    {
                        Party.PlayAlone = false;
                        SelectLevel();
                    }
                    break;
                case 2:
                    if (Party.OwnWorld)
                    {
                        ToggleServers();
                    }
                    else
                    {
                        Party.LeaveToOwnWorld();
                        PartyChat.System("Leaving the party (back to your own world)...");
                    }
                    break;
            }
        }

        private static void ToggleServers()
        {
            showServers = !showServers;
            serversKey = "";
            if (showServers)
            {
                serversAskedAt = Time.unscaledTime;
                ServerList.Refresh();
            }
        }

        private static void CyclePrivacy()
        {
            // PUBLIC (2) -> FRIENDS (1) -> PRIVATE (0) -> PUBLIC.
            int v = Mathf.Clamp(Plugin.ListWorld.Value, 0, 2);
            Plugin.ListWorld.Value = v == 0 ? 2 : v - 1;
            Plugin.Log.LogInfo($"party menu: privacy {Privacy()}");
        }

        public static string Privacy()
        {
            switch (Mathf.Clamp(Plugin.ListWorld.Value, 0, 2))
            {
                case 2: return "PUBLIC";
                case 1: return "FRIENDS";
                default: return "PRIVATE";
            }
        }

        // ULTRAKILL's own level select (PLAY); the party follows once the host's in a level.
        private static void SelectLevel()
        {
            // (Picked from here: the party's WAKE, unless it's PLAY ALONE.)
            if (!Party.PlayAlone)
            {
                PartyWake.PickingForParty();
            }
            Close();
            try
            {
                if (playButton != null && playButton.TryGetComponent(out Button b))
                {
                    b.onClick.Invoke();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party menu: couldn't open the level select ({e.Message})");
            }
        }

        private static void VoiceClicked(int n)
        {
            switch (n)
            {
                case 0:
                    Plugin.VoiceMode.Value = (Plugin.VoiceMode.Value + 1) % 3;
                    break;
                case 1:
                    Voice.TestingMic = !Voice.TestingMic;
                    break;
                case 2:
                    Voice.Deafened = !Voice.Deafened;
                    break;
                case 3:
                    int c = Plugin.MyColor.Value + 1;
                    Plugin.MyColor.Value = c >= Profiles.Colors.Length ? -1 : c;
                    break;
            }
        }

        public static string MicLabel()
        {
            switch (Plugin.VoiceMode.Value)
            {
                case 0: return "OFF";
                case 2: return "OPEN MIC";
                default: return $"PUSH TO TALK ({(Plugin.VoiceKey.Value ?? "V").ToUpperInvariant()})";
            }
        }

        // ---- shown ----

        // Every frame: what's typed, who's talking, the start-up lines typing out.
        private static void Live()
        {
            if (chatBox != null)
            {
                chatBox.Set(Typing.Owner == chatOwner ? Typing.ShownIn(chatBox.Text, "", "TYPE HERE...") : "<color=#777777><i>TYPE HERE...</i></color>");
            }
            if (nameField != null)
            {
                string placeholder = $"<color=#777777><i>{Typing.Escape(ServerList.Title)}</i></color>";
                nameField.Set(Typing.Owner == nameOwner ? Typing.ShownIn(nameField.Text, "", "TYPE A NAME")
                    : string.IsNullOrWhiteSpace(Plugin.PartyTitle.Value) ? placeholder : Typing.Escape(Plugin.PartyTitle.Value.Trim()));
            }
            if (maxField != null)
            {
                maxField.Set(Typing.Owner == maxOwner ? Typing.ShownIn(maxField.Text, "", "2-10") : ServerList.MaxPlayers.ToString());
            }
            if (codeField != null)
            {
                codeField.Set(Typing.Owner == codeOwner ? Typing.ShownIn(codeField.Text, "", "TYPE THE CODE")
                    : ServerList.CodeSearching ? "LOOKING..." : "<color=#777777><i>CLICK, THEN TYPE THE CODE</i></color>");
            }
            if (boot != null && connectView.gameObject.activeSelf)
            {
                // (Typed out after the view appears, ~90 letters a second.)
                boot.maxVisibleCharacters = Mathf.Max(0, (int)((Time.unscaledTime - viewShownAt - 0.3f) * 90f));
                bool blink = (int)(Time.unscaledTime * 2f) % 2 == 0;
                string last = BootLines();
                if (last != bootShown)
                {
                    // (The block as wide as its longest line, so it sits in the middle under the name.)
                    bootShown = last;
                    float w = boot.GetPreferredValues(last + "_", 2000f, 400f).x;
                    boot.rectTransform.sizeDelta = new Vector2(Mathf.Ceil(w) + 4f, boot.rectTransform.sizeDelta.y);
                }
                boot.text = last + (blink ? "_" : " ");
            }
            foreach (var kv in rows)
            {
                bool talking = kv.Key == Room.MyId ? Voice.Talking : Voice.IsSpeaking(kv.Key);
                for (int i = 0; i < kv.Value.Bars.Length; i++)
                {
                    float h = talking ? 6f + 14f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * (9f + i * 4f) + i)) : 4f;
                    kv.Value.Bars[i].rectTransform.sizeDelta = new Vector2(4f, h);
                    kv.Value.Bars[i].color = talking ? Color.white : Ui.DarkGrey;
                }
            }
        }

        // CONNECT's start-up lines: what's ready.
        private static string BootLines()
        {
            string red = Ui.Hex(Ui.Red);
            bool steam = ServerList.SteamUp;
            string mc = !Brand.Craft ? "OFF" : Party.McWorldKnown ? "OK" : "STARTING";
            string state = !steam ? "WAITING FOR STEAM" : ServerList.Making ? "CREATING PARTY" : ServerList.CodeSearching ? "LOOKING FOR PARTY" : "AWAITING PARTY";
            return $"INIT <color=#{red}>[ {Brand.Name} ]</color> v2.0 ...OK\n" +
                   $"STEAM LINK ...{(steam ? "OK" : "WAITING")}\n" +
                   "VOICE SUBSYSTEM ...OK\n" +
                   $"P2P TRANSPORT ...{(steam ? "OK" : "--")}\n" +
                   (Brand.Craft ? $"MINECRAFT LINK ...{mc}\n" : "") +
                   "\n" +
                   state;
        }

        private static void Refresh()
        {
            bool known = Party.WorldKnown;
            bool inParty = InParty;

            // The view: CONNECT out of a party, the party in one (appearing row by row when it changes).
            if (inPartyShown != inParty)
            {
                bool first = inPartyShown == null;
                inPartyShown = inParty;
                connectView.gameObject.SetActive(!inParty);
                partyView.gameObject.SetActive(inParty);
                viewShownAt = Time.unscaledTime;
                rowsKey = "";
                serversKey = "";
                lobbiesKey = "";
                popup.gameObject.SetActive(false);
                if (!first)
                {
                    Ui.Reveal((RectTransform)canvas.transform, inParty ? partyView : connectView);
                }
            }

            // VOICE.
            voice[0].Set("MIC: " + MicLabel());
            voice[1].Set(Voice.TestingMic ? "TEST MIC: SPEAK NOW" : "TEST MIC");
            voice[1].On = Voice.TestingMic;
            voice[2].Set("DEAFEN: " + (Voice.Deafened ? "ON" : "OFF"));
            voice[2].On = Voice.Deafened;
            int ci = Plugin.MyColor.Value;
            voice[3].Set("MY COLOR: " + (ci >= 0 && ci < Profiles.Colors.Length ? Profiles.Colors[ci].Name : "AUTO"));
            swatch.color = Profiles.Mine.Color;
            foreach (UiButton b in voice)
            {
                b.Refresh();
            }

            if (inParty)
            {
                RefreshParty(known);
            }
            else
            {
                RefreshConnect(known);
            }

            // Chat and log.
            if (PartyChat.Version != chatShown)
            {
                chatShown = PartyChat.Version;
                int from = Mathf.Max(0, PartyChat.Lines.Count - 40);
                string text = Join(from);
                // (From the top down: the oldest dropped till the newest fit.)
                while (from < PartyChat.Lines.Count - 1 && chat.GetPreferredValues(text, chat.rectTransform.rect.width > 10f ? chat.rectTransform.rect.width : 570f, 0f).y > chatHeight)
                {
                    from++;
                    text = Join(from);
                }
                chat.text = text;
                var lsb = new System.Text.StringBuilder();
                int lf = Mathf.Max(0, PartyChat.Log.Count - 3);
                for (int i = lf; i < PartyChat.Log.Count; i++)
                {
                    lsb.AppendLine(PartyChat.Log[i]);
                }
                log.text = lsb.ToString();
            }
        }

        private static void RefreshConnect(bool known)
        {
            bool hasLast = ServerList.LastParty(out _, out string lastName);
            for (int i = 0; i < connect.Length; i++)
            {
                UiButton b = connect[i];
                b.Interactable = known;
                b.On = known && ((i == 0 && mode == Mode.Host) || (i == 1 && mode == Mode.Join) || (i == 2 && mode == Mode.Lobbies));
            }
            bool joining = ServerList.JoinPending;
            connect[3].Set(joining ? "REJOINING..." : hasLast ? "REJOIN: " + Typing.Escape(lastName.ToUpperInvariant()) : "REJOIN");
            connect[3].Interactable = known && hasLast && !joining;
            rejoinNote.text = !joining && ServerList.JoinProblem.Length > 0 && Time.unscaledTime - ServerList.JoinProblemAt < 10f ? ServerList.JoinProblem : "";
            foreach (UiButton b in connect)
            {
                b.Refresh();
            }

            hostPanel.SetActive(mode == Mode.Host);
            joinPanel.SetActive(mode == Mode.Join);
            lobbiesPanel.SetActive(mode == Mode.Lobbies);
            panelTitle.text = mode == Mode.Host ? "-- HOST PARTY --" : mode == Mode.Join ? "-- JOIN BY CODE --" : "-- PUBLIC LOBBIES --";
            switch (mode)
            {
                case Mode.Host:
                    typeButton.Set("TYPE: " + Privacy());
                    cheatsButton.Set("CHEATS: " + (Plugin.PartyCheats.Value ? "ON" : "OFF"));
                    createButton.Set(ServerList.Making ? "CREATING..." : "CREATE");
                    createButton.Interactable = known && !ServerList.Making;
                    createButton.Refresh();
                    break;
                case Mode.Join:
                    joinHint.text = ServerList.CodeSearching ? "LOOKING FOR THAT PARTY..." : "ASK THE HOST FOR THEIR PARTY CODE (IN THEIR PARTY SCREEN).\nTYPE IT, THEN PRESS ENTER OR JOIN.";
                    joinButton.Interactable = known && !ServerList.CodeSearching;
                    joinButton.Refresh();
                    break;
                case Mode.Lobbies:
                    if (Time.unscaledTime - serversAskedAt > 15f && !ServerList.Searching)
                    {
                        serversAskedAt = Time.unscaledTime;
                        ServerList.Refresh();
                    }
                    lobbiesStatus.text = ServerList.Searching ? "LOOKING..." : ServerList.Found.Count == 0 ? "NO OPEN PARTIES" : (ServerList.Status ?? "").ToUpperInvariant();
                    ListParties(lobbies, 574f, ref lobbiesKey);
                    break;
            }
        }

        private static void RefreshParty(bool known)
        {
            string code = Profiles.PartyCode();
            string hostName = "";
            foreach (Party.Member m in Party.Members)
            {
                if (m.PartyHost)
                {
                    hostName = m.Id == Room.MyId ? Profiles.Mine.SteamName : Profiles.NameOf(m.Id);
                }
            }
            string owner = hostName.Length > 0 ? hostName : Profiles.Mine.SteamName ?? "";
            string title = ServerList.PartyTitle;
            partyName.text = title.Length > 0 ? Typing.Escape(title.ToUpperInvariant()) : Typing.Escape(owner.ToUpperInvariant()) + "'S PARTY";
            codeText.text = code.Length > 0 ? $"CODE: {code}" : "CODE: ...";
            copy.gameObject.SetActive(code.Length > 0);
            string diff = Profiles.DifficultyName(Profiles.MyDifficulty());
            bool cheats = !ServerList.CheatsBlocked;
            infoText.text = $"{(Party.OwnWorld ? Privacy() : "JOINED")}  ·  {diff}" + (cheats ? "" : "  ·  NO CHEATS") + (Room.Pvp ? "  ·  PVP" : "");

            // CONTROL.
            privacy.gameObject.SetActive(Party.OwnWorld);
            Control(0, Party.OwnWorld ? "INVITE FRIENDS" : "INVITE (OWNER ONLY)", Party.OwnWorld);
            Control(1, Party.IAmHost ? "SELECT LEVEL" : "HOST PICKS THE LEVEL", Party.IAmHost);
            bool othersPlaying = false;
            foreach (Party.Member m in Party.Members)
            {
                Profiles.Info pi = Profiles.Of(m.Id);
                if (m.Id != Room.MyId && pi.Fresh && pi.State != Profiles.StateMenu && Party.IsLevel(pi.Scene))
                {
                    othersPlaying = true;
                }
            }
            bool alone = othersPlaying || !Party.IAmHost;
            if (playAlone.gameObject.activeSelf != alone)
            {
                playAlone.gameObject.SetActive(alone);
            }
            Control(2, Party.OwnWorld ? (showServers ? "SHOW SQUAD" : "BROWSE PARTIES") : "LEAVE PARTY", true, true, !Party.OwnWorld);

            // The middle.
            squadView.SetActive(!showServers);
            serversView.SetActive(showServers);
            if (showServers)
            {
                // (Asked again every 15 seconds while it's shown: parties opened since appear.)
                if (Time.unscaledTime - serversAskedAt > 15f && !ServerList.Searching)
                {
                    serversAskedAt = Time.unscaledTime;
                    ServerList.Refresh();
                }
                squadHeader.text = "-- PARTIES --";
                centerTitle.text = ServerList.Searching ? "LOOKING..." : (ServerList.Status ?? "").ToUpperInvariant();
                ListParties(servers, 620f, ref serversKey);
            }
            else
            {
                Squad();
            }
        }

        private static string Join(int from)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = from; i < PartyChat.Lines.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(PartyChat.Lines[i].Text);
            }
            return sb.ToString();
        }

        private static void Control(int i, string label, bool enabled, bool visible = true, bool red = false)
        {
            UiButton b = control[i];
            if (b.gameObject.activeSelf != visible)
            {
                b.gameObject.SetActive(visible);
            }
            b.Set(label);
            b.Interactable = enabled;
            b.Accent = red ? Ui.Red : Color.white;
            b.Refresh();
        }

        // ---- the squad ----

        private static List<(int Id, bool Host, bool Owner)> Members()
        {
            var list = new List<(int, bool, bool)>();
            if (Party.Heard && Party.Members.Count > 0)
            {
                foreach (Party.Member m in Party.Members)
                {
                    list.Add((m.Id, m.PartyHost, m.WorldHost));
                }
            }
            else
            {
                list.Add((Room.MyId, true, true));
            }
            return list;
        }

        private static void Squad()
        {
            List<(int Id, bool Host, bool Owner)> members = Members();
            squadHeader.text = $"-- SQUAD {members.Count}/{ServerList.PartyMax} --";
            var sb = new System.Text.StringBuilder();
            foreach (var m in members)
            {
                sb.Append(m.Id).Append(',');
            }
            string key = sb.ToString();
            if (key != rowsKey)
            {
                rowsKey = key;
                Ui.Clear(squad);
                rows.Clear();
                foreach (var m in members)
                {
                    rows[m.Id] = MakeRow(m.Id);
                }
            }
            foreach (var m in members)
            {
                if (rows.TryGetValue(m.Id, out MemberRow row))
                {
                    FillRow(row, m.Id, m.Host);
                }
            }
        }

        private static MemberRow MakeRow(int id)
        {
            bool me = id == Room.MyId;
            var r = new MemberRow();
            r.Root = Ui.Row(squad, 96f);
            // (Lit red once this player's game has the wake.)
            r.Glow = Ui.Image(r.Root, null, new Color(0f, 0f, 0f, 0f), 0f, 0f, 620f, 94f);
            Ui.Image(r.Root, null, new Color(0.36f, 0.29f, 0.24f, 1f), 0f, 94f, 620f, 2f);
            r.Strip = Ui.Image(r.Root, null, Color.white, 2f, 12f, 6f, 70f);
            Ui.Image(r.Root, null, new Color(0.12f, 0.12f, 0.12f, 1f), 22f, 14f, 66f, 66f);
            RectTransform av = Ui.Rect(r.Root, "Avatar", 22f, 14f, 66f, 66f);
            r.Avatar = av.gameObject.AddComponent<RawImage>();
            r.Avatar.raycastTarget = false;
            // (The host's crown on the picture's corner, inside the row.)
            r.Crown = Ui.Image(r.Root, Ui.Crown, Color.white, 8f, 6f, 30f, 22f);
            r.Crown.rectTransform.localEulerAngles = new Vector3(0f, 0f, 18f);
            r.Name = Ui.Text(r.Root, "", 30f, Color.white, TextAlignmentOptions.MidlineLeft, 103f, 12f, 380f, 40f);
            r.Status = Ui.Text(r.Root, "", 18f, new Color(0.62f, 0.62f, 0.62f), TextAlignmentOptions.MidlineLeft, 103f, 52f, 330f, 26f);
            r.Ping = Ui.Text(r.Root, "", 16f, Ui.Green, TextAlignmentOptions.MidlineRight, 440f, 14f, 162f, 26f);
            r.Bars = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                RectTransform bar = Ui.Rect(r.Root, "Bar", 0f, 0f, 4f, 4f);
                bar.anchorMin = bar.anchorMax = new Vector2(0f, 1f);
                bar.pivot = new Vector2(0.5f, 0.5f);
                bar.anchoredPosition = new Vector2(0f, -42f);
                r.Bars[i] = Ui.Picture(bar, null, Ui.DarkGrey);
            }
            int captured = id;
            if (!me)
            {
                r.More = Ui.MoreButton(r.Root, () => More(captured), 555f, 56f, 47f, 33f);
                r.Watch = Ui.Button(r.Root, "SPECTATE", 17f, () => Watch(captured), 433f, 56f, 114f, 33f, TextAlignmentOptions.Center, true, 3f);
                r.Watch.Accent = Ui.Orange;
                r.Watch.Refresh();
                r.Watch.gameObject.SetActive(false);
            }
            return r;
        }

        private static void FillRow(MemberRow r, int id, bool host)
        {
            bool me = id == Room.MyId;
            Profiles.Info i = me ? Profiles.Mine : Profiles.Of(id);
            r.Strip.color = i.Color;
            r.Avatar.texture = i.Avatar;
            r.Avatar.color = i.Avatar != null ? Color.white : new Color(0f, 0f, 0f, 0f);
            // (UltraLink's rows: no crown, the name white as the player wrote it.)
            r.Crown.gameObject.SetActive(false);
            string name = me ? Profiles.Mine.SteamName : Profiles.NameOf(id);
            r.Name.text = Typing.Escape(name ?? "");
            float nameW = Mathf.Min(r.Name.preferredWidth, 380f);
            for (int b = 0; b < r.Bars.Length; b++)
            {
                r.Bars[b].rectTransform.anchoredPosition = new Vector2(103f + nameW + 18f + b * 7f, -32f);
            }
            bool waking = PartyWake.Active;
            bool ok = waking && PartyWake.Ready(id);
            r.Status.text = !waking ? StatusLine(i, me) : ok ? "<color=#FFFFFF>. . . OK</color>" : "WAIT FOR WAKE";
            Color glow = ok ? new Color(0.4f, 0.18f, 0.125f, 1f) : new Color(0.4f, 0.18f, 0.125f, 0f);
            r.Glow.color = Color.Lerp(r.Glow.color, glow, 0.5f);
            r.Ping.text = "";
            r.Ping.color = i.Ping < 80f ? Ui.Green : i.Ping < 160f ? Ui.Yellow : Ui.Red;
            if (r.Watch != null)
            {
                bool playing = i.Fresh && i.State != Profiles.StateMenu && (i.Flags & Profiles.FlagWatching) == 0 && Party.IsLevel(i.Scene);
                if (r.Watch.gameObject.activeSelf != playing)
                {
                    r.Watch.gameObject.SetActive(playing);
                }
            }
        }

        // SPECTATE: to their level, watching them; they can let this player in.
        private static void Watch(int id)
        {
            Profiles.Info i = Profiles.Of(id);
            if (!Party.IsLevel(i.Scene))
            {
                return;
            }
            Close();
            PartyChat.Event($"SPECTATING '{Profiles.NameOf(id).ToUpperInvariant()}'");
            Spectate.WatchFromMenu(id, i.Scene);
        }

        public static string StatusLine(Profiles.Info i, bool me)
        {
            if (!me && !i.Fresh)
            {
                return "<color=#888888>CONNECTING...</color>";
            }
            string level = Typing.Escape(Party.LevelName(i.Scene).ToUpperInvariant());
            switch (i.State)
            {
                case Profiles.StateMenu: return "<color=#BDBDBD>IN LOBBY</color>";
                case Profiles.StateDead: return (i.Flags & Profiles.FlagWatching) != 0 ? $"<color=#FFA030>WATCHING</color> · {level}" : $"<color=#E05040>DEAD</color>" + ((i.Flags & Profiles.FlagSpectating) != 0 ? " · <color=#FFA030>SPECTATING</color>" : "") + $" · {level}";
                case Profiles.StateDone: return $"<color=#FFD02E>FINISHED</color> · {level}" + (string.IsNullOrEmpty(i.Rank) ? "" : $" · {Typing.Escape(i.Rank)}");
                default: return $"IN {level} · {Mathf.Max(0, i.Hp)} HP";
            }
        }

        // The ... menu for a player: make them host, kick them, mute them, their Steam profile.
        private static void More(int id)
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
                Profiles.Info i = Profiles.Of(id);
                items.Add((i.Muted ? "UNMUTE" : "MUTE", () => i.Muted = !i.Muted));
            }
            items.Add(("STEAM PROFILE", () => Profiles.OpenSteamProfile(id)));
            float h = 18f + items.Count * 50f;
            popup.sizeDelta = new Vector2(260f, h);
            Ui.Sliced(Ui.Picture(Ui.Fill(popup, "Back"), Ui.BoxLineBlack, Color.white), 3f).raycastTarget = true;
            for (int n = 0; n < items.Count; n++)
            {
                Action act = items[n].Act;
                string label = items[n].Label;
                UiButton b = Ui.Button(popup, label, 20f, () =>
                {
                    act();
                    popup.gameObject.SetActive(false);
                    popupFor = int.MinValue;
                }, 10f, 10f + n * 50f, 240f, 42f, TextAlignmentOptions.Center, true, 2.5f);
                if (label == "KICK")
                {
                    b.Accent = Ui.Red;
                    b.Refresh();
                }
            }
            // (By the row's ...)
            if (rows.TryGetValue(id, out MemberRow row))
            {
                var corners = new Vector3[4];
                ((RectTransform)row.More.transform).GetWorldCorners(corners);
                popup.position = corners[0];
                popup.anchoredPosition += new Vector2(-213f, -6f);
            }
            popup.SetAsLastSibling();
            popup.gameObject.SetActive(true);
        }

        // ---- the open parties (PUBLIC LOBBIES, BROWSE PARTIES) ----

        private static void ListParties(RectTransform list, float Wide, ref string shownKey)
        {
            var sb = new System.Text.StringBuilder();
            foreach (ServerList.Entry e in ServerList.Found)
            {
                sb.Append(e.Lobby).Append(e.Players).Append(e.Level).Append(e.Title).Append(',');
            }
            string key = sb.ToString();
            if (key == shownKey)
            {
                return;
            }
            shownKey = key;
            Ui.Clear(list);
            foreach (ServerList.Entry e in ServerList.Found)
            {
                RectTransform row = Ui.Row(list, 84f);
                Ui.Sliced(Ui.Picture(Ui.Fill(row, "Line"), Ui.SmallLine, Color.white), 2.5f);
                string title = e.Title.Length > 0 ? e.Title : e.Name + "'s Lobby";
                Ui.Text(row, Typing.Escape(title.ToUpperInvariant()) + (e.Friend ? " <size=16><color=#4FE6FF>FRIEND</color></size>" : ""), 26f, Color.white,
                    TextAlignmentOptions.MidlineLeft, 18f, 8f, Wide - 200f, 36f);
                Ui.Text(row, $"{Typing.Escape(e.Level.ToUpperInvariant())} · {e.Players}/{e.Max}" + (e.Pvp ? " · <color=#E05040>PVP</color>" : "") + (e.Minecraft ? " · <color=#7FCF5A>MINECRAFT</color>" : ""), 17f,
                    new Color(0.8f, 0.8f, 0.8f), TextAlignmentOptions.MidlineLeft, 18f, 46f, Wide - 200f, 28f);
                ulong lobby = e.Lobby;
                string name = e.Name;
                bool full = e.Players >= e.Max;
                UiButton join = Ui.Button(row, full ? "FULL" : "JOIN", 22f, () =>
                {
                    ServerList.JoinParty(lobby, name);
                    PartyChat.System($"Joining {Typing.Escape(name)}'s party...");
                    showServers = false;
                }, Wide - 150f, 18f, 130f, 48f, TextAlignmentOptions.Center, false, 3.2f);
                join.Interactable = !full;
                join.Refresh();
            }
        }
    }
}
