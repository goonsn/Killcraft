using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // ULTRAKILL's pause menu while watching someone (UltraLink's): RESUME, JOIN, OPTIONS, STOP
    // SPECTATING inside its box (CHECKPOINT, RESTART MISSION and QUIT MISSION put away, the box one
    // row shorter, KILLCRAFT under it moved up with it); all of it put back after. JOIN and STOP
    // SPECTATING are its OPTIONS copied (the box, the font, grey under the mouse, red pressed).
    internal static class PauseButtons
    {
        private static GameObject menu, join, stop;
        private static bool laidOut, loggedMenu;
        private static Vector2 savedSize, savedPos;
        private static readonly Dictionary<RectTransform, Vector2> savedChildren = new Dictionary<RectTransform, Vector2>();
        private static readonly List<GameObject> hidden = new List<GameObject>();

        public static bool LaidOut => laidOut;

        // Every frame: laid out for watching, or as ULTRAKILL made it.
        public static void Spectating(bool on, Action joinClick, Action stopClick)
        {
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            GameObject pause = om != null ? om.pauseMenu : null;
            if (pause != menu)
            {
                menu = pause;
                join = stop = null;
                laidOut = false;
                savedChildren.Clear();
                hidden.Clear();
            }
            if (pause == null || on == laidOut)
            {
                return;
            }
            // (Not while it's opening: its scale isn't its own yet.)
            if (pause.activeInHierarchy && Mathf.Abs(pause.transform.localScale.y - 1f) > 0.01f)
            {
                return;
            }
            try
            {
                if (on)
                {
                    LayOut(pause, joinClick, stopClick);
                }
                else
                {
                    Restore(pause);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"pause menu: couldn't lay it out for spectating ({e.Message})");
                laidOut = on;
            }
        }

        private static void LayOut(GameObject pause, Action joinClick, Action stopClick)
        {
            var root = (RectTransform)pause.transform;
            LogMenu(pause);
            Transform options = pause.transform.Find("Options");
            if (options == null)
            {
                laidOut = true;
                return;
            }
            if (join == null)
            {
                join = Make(pause, "Killcraft Join", "JOIN", joinClick);
                stop = Make(pause, "Killcraft Stop Spectating", "STOP SPECTATING", stopClick);
            }
            if (join == null || stop == null)
            {
                laidOut = true;
                return;
            }
            // ULTRAKILL's buttons in the box, top first.
            var buttons = new List<RectTransform>();
            foreach (Transform t in pause.transform)
            {
                if (t.gameObject.activeSelf && t.GetComponent<Button>() != null && !t.name.StartsWith("Killcraft", StringComparison.Ordinal))
                {
                    buttons.Add((RectTransform)t);
                }
            }
            float h = root.rect.height;
            buttons.Sort((a, b) => Top(b, h).CompareTo(Top(a, h)));
            if (buttons.Count < 4 || !buttons.Contains((RectTransform)options))
            {
                Plugin.Log.LogWarning($"pause menu: {buttons.Count} buttons, not ULTRAKILL's five; left as it is");
                laidOut = true;
                return;
            }
            float step = Top(buttons[0], h) - Top(buttons[1], h);
            var slots = new List<float>();
            foreach (RectTransform b in buttons)
            {
                slots.Add(Top(b, h));
            }
            savedSize = root.sizeDelta;
            savedPos = root.anchoredPosition;
            savedChildren.Clear();
            foreach (Transform t in pause.transform)
            {
                savedChildren[(RectTransform)t] = ((RectTransform)t).anchoredPosition;
            }
            hidden.Clear();
            foreach (RectTransform b in buttons)
            {
                if (b != buttons[0] && b != options)
                {
                    b.gameObject.SetActive(false);
                    hidden.Add(b.gameObject);
                }
            }
            // One row less (or as many as were put away, less the two added).
            float s = Mathf.Max(0f, (buttons.Count - 4) * step);
            var keep = new Dictionary<RectTransform, float>();
            foreach (Transform t in pause.transform)
            {
                var rt = (RectTransform)t;
                if (Mathf.Abs(rt.anchorMin.y - rt.anchorMax.y) < 0.0001f)
                {
                    keep[rt] = Top(rt, h);
                }
            }
            root.sizeDelta -= new Vector2(0f, s);
            root.anchoredPosition += new Vector2(0f, (1f - root.pivot.y) * s * root.localScale.y);
            float h2 = h - s;
            foreach (var kv in keep)
            {
                float rel = kv.Value;
                if (kv.Key.name == "Killcraft Button")
                {
                    rel += s;
                }
                SetTop(kv.Key, rel, h2);
            }
            var optRt = (RectTransform)options;
            var jr = (RectTransform)join.transform;
            var sr = (RectTransform)stop.transform;
            jr.anchoredPosition = new Vector2(optRt.anchoredPosition.x, 0f);
            sr.anchoredPosition = new Vector2(optRt.anchoredPosition.x, 0f);
            SetTop(jr, slots[1], h2);
            SetTop(optRt, slots[2], h2);
            SetTop(sr, slots[3], h2);
            join.SetActive(true);
            stop.SetActive(true);
            laidOut = true;
            Plugin.Log.LogInfo($"pause menu: laid out for spectating ({buttons.Count} buttons, box {s:0} shorter)");
        }

        private static void Restore(GameObject pause)
        {
            var root = (RectTransform)pause.transform;
            if (savedChildren.Count > 0)
            {
                root.sizeDelta = savedSize;
                root.anchoredPosition = savedPos;
                foreach (var kv in savedChildren)
                {
                    if (kv.Key != null)
                    {
                        kv.Key.anchoredPosition = kv.Value;
                    }
                }
            }
            foreach (GameObject g in hidden)
            {
                if (g != null)
                {
                    g.SetActive(true);
                }
            }
            hidden.Clear();
            savedChildren.Clear();
            if (join != null)
            {
                join.SetActive(false);
            }
            if (stop != null)
            {
                stop.SetActive(false);
            }
            laidOut = false;
            Plugin.Log.LogInfo("pause menu: put back");
        }

        // A child's height from the box's top edge (box-local units; its anchor point plus its offset).
        private static float Top(RectTransform rt, float h) => -(1f - rt.anchorMin.y) * h + rt.anchoredPosition.y;

        private static void SetTop(RectTransform rt, float rel, float h) =>
            rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rel + (1f - rt.anchorMin.y) * h);

        private static void LogMenu(GameObject pause)
        {
            if (loggedMenu)
            {
                return;
            }
            loggedMenu = true;
            var sb = new System.Text.StringBuilder("pause menu: ");
            var pr = (RectTransform)pause.transform;
            Image own = pause.GetComponent<Image>();
            sb.Append($"box {pr.rect.width:0}x{pr.rect.height:0} at {pr.anchoredPosition} anchors {pr.anchorMin}-{pr.anchorMax} pivot {pr.pivot}{(own != null ? " img " + (own.sprite != null ? own.sprite.name : "none") : "")};");
            foreach (Transform t in pause.transform)
            {
                if (t is RectTransform rt)
                {
                    Image img = t.GetComponent<Image>();
                    sb.Append($" {t.name}[{(t.gameObject.activeSelf ? "" : "off,")}{rt.rect.width:0}x{rt.rect.height:0} at {rt.anchoredPosition} a {rt.anchorMin}-{rt.anchorMax}{(img != null ? ",img " + (img.sprite != null ? img.sprite.name : "none") + " " + img.color : "")}]");
                }
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static GameObject Make(GameObject pause, string name, string label, Action click)
        {
            Transform options = pause.transform.Find("Options");
            GameObject b = null;
            try
            {
                b = UnityEngine.Object.Instantiate(options.gameObject, pause.transform);
                b.name = name;
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
                    click?.Invoke();
                });
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
                {
                    t.text = label;
                }
                foreach (Text t in b.GetComponentsInChildren<Text>(true))
                {
                    t.text = label;
                }
                b.SetActive(false);
                Plugin.Log.LogInfo($"pause menu: {label} added");
                return b;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"pause menu: couldn't add {label} ({e.Message})");
                if (b != null)
                {
                    UnityEngine.Object.Destroy(b);
                }
                return null;
            }
        }

        // Where KILLCRAFT goes under the box, and how far apart the box's buttons are.
        public static bool Step(GameObject pause, out float step, out float top, out float x)
        {
            step = 0f;
            top = 0f;
            x = 0f;
            Transform quit = pause != null ? pause.transform.Find("Quit Mission") : null;
            Transform options = pause != null ? pause.transform.Find("Options") : null;
            if (quit == null || options == null)
            {
                return false;
            }
            var quitRt = (RectTransform)quit;
            var optRt = (RectTransform)options;
            var panelRt = (RectTransform)pause.transform;
            Vector2 size = optRt.sizeDelta;
            step = Mathf.Abs(optRt.anchoredPosition.y - quitRt.anchoredPosition.y);
            if (step < 10f || step > 200f)
            {
                step = size.y * 1.2f;
            }
            float bottom = panelRt.rect.yMin - panelRt.rect.center.y;
            // (Under the pause menu's box, apart from it as its buttons are from each other.)
            top = Mathf.Min(quitRt.anchoredPosition.y - step * 1.15f, bottom - size.y * 0.5f - step * 0.45f);
            x = optRt.anchoredPosition.x;
            return true;
        }
    }
}
