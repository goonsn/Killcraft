using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Killcraft
{
    // REPLAYS (the main menu, beside PARTY): the saved replays, newest first (UltraLink's screen):
    // each its picture, its level, the players, its length and date, its rank, its ranks for time,
    // kills and style, PLAY, X; CASE FILE opens the run's record under it: how it went (damage,
    // restarts, secrets, assists), its health through the run with its checkpoints and arenas, each
    // gun's time in hand, the kills by enemy, the top style bonuses, and where it stood on each rank.
    internal static class Replays
    {
        private static Canvas canvas;
        private static RectTransform page, list;
        private static TextMeshProUGUI empty;
        private static UiButton search;
        private static readonly object searchOwner = new object();
        private static string filter = "", shownKey = "", expanded = "", grow = "";
        // (Names with a thin dark edge, as UltraLink's: given it once the font is set.)
        private static readonly List<TextMeshProUGUI> outlined = new List<TextMeshProUGUI>();
        private static float listedAt = -100f;
        internal static string armedFile = "";
        internal static float armedAt = -100f;
        private static bool revealNext;
        private static List<Replay.Info> infos = new List<Replay.Info>();
        private static readonly Dictionary<string, Texture2D> pictures = new Dictionary<string, Texture2D>();
        private static readonly List<Texture2D> pulses = new List<Texture2D>();
        private static Texture2D fade;

        private const float RowW = 1200f, HeadH = 112f, CaseH = 305f;

        public static bool Open => canvas != null && canvas.gameObject.activeSelf;

        public static void Show()
        {
            try
            {
                if (canvas == null)
                {
                    Build();
                }
                Ui.Refont(canvas.transform);
                PartyMenu.HideMenu(true);
                canvas.gameObject.SetActive(true);
                revealNext = true;
                shownKey = "";
                listedAt = -100f;
                Plugin.Log.LogInfo("replays: opened");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replays: couldn't open ({e})");
                Close();
            }
        }

        public static void Close()
        {
            CloseQuietly();
            PartyMenu.ShowMenu();
        }

        // (Going into a replay: the menu isn't shown again on the way.)
        public static void CloseQuietly()
        {
            if (Typing.Owner == searchOwner)
            {
                Typing.End();
            }
            if (canvas != null)
            {
                canvas.gameObject.SetActive(false);
            }
        }

        public static void Frame(bool mainMenu)
        {
            if (!Open)
            {
                return;
            }
            if (!mainMenu)
            {
                CloseQuietly();
                return;
            }
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && !Typing.Active && Typing.EndedFrame != Time.frameCount)
            {
                Close();
                return;
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (Typing.Owner == searchOwner)
            {
                filter = Typing.Text;
                search.Set(Typing.ShownIn(search.Text, "", "SEARCH..."));
            }
            else
            {
                search.Set(filter.Length > 0 ? Typing.Escape(filter) : "<color=#666666><i>SEARCH...</i></color>");
            }
            if (Time.unscaledTime - listedAt > 3f)
            {
                listedAt = Time.unscaledTime;
                infos = Replay.List();
            }
            Refresh();
        }

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft replays", 60, false);
            page = Ui.Fill(canvas.transform, "Page");
            Ui.Button(page, "<  BACK", 18f, Close, 390f, 110f, 120f, 34f, TextAlignmentOptions.MidlineLeft, true, 2.2f);
            Ui.Text(page, "REPLAYS", 30f, Ui.Red, TextAlignmentOptions.Center, 760f, 108f, 400f, 36f);
            search = Ui.Button(page, "", 16f, () =>
            {
                if (Typing.Owner != searchOwner)
                {
                    Typing.Begin(searchOwner, 40, t => filter = t, filter);
                }
            }, 360f, 164f, RowW, 30f, TextAlignmentOptions.MidlineLeft, true, 2.2f);
            list = Ui.Scroll(page, 360f, 206f, RowW + 14f, 770f, 10f, out _);
            empty = Ui.Text(page, "", 20f, Ui.Grey, TextAlignmentOptions.Center, 360f, 520f, RowW, 80f);
            fade = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 64; x++)
            {
                fade.SetPixel(x, 0, new Color(0f, 0f, 0f, Mathf.SmoothStep(0f, 1f, x / 63f)));
            }
            fade.Apply();
            canvas.gameObject.SetActive(false);
        }

        // What a replay is found by: its level, players, difficulty, rank, and its CASE FILE's tags.
        private static string Tags(Replay.Info i)
        {
            var t = new System.Text.StringBuilder();
            t.Append(i.Level).Append(' ').Append(string.Join(" ", i.Players)).Append(' ').Append(i.Difficulty);
            if (!string.IsNullOrEmpty(i.Rank))
            {
                t.Append(' ').Append(i.Rank).Append(" RANK ").Append(i.Rank).Append("-RANK");
            }
            t.Append(i.TookDamage ? " DAMAGE TAKEN" : " NO DAMAGE");
            t.Append(i.MajorUsed ? " MAJOR ASSISTS" : " NO MAJOR ASSISTS");
            t.Append(i.Finished ? " FINISHED" : " UNFINISHED");
            t.Append(i.Players.Length > 1 ? " MULTIPLAYER CO-OP" : " SOLO");
            if (i.Restarts == 0)
            {
                t.Append(" NO RESTARTS");
            }
            if (i.SecretsTotal > 0 && i.SecretsFound >= i.SecretsTotal)
            {
                t.Append(" ALL SECRETS");
            }
            foreach (string m in i.MarkLabels)
            {
                t.Append(' ').Append(m);
            }
            return t.ToString().ToUpperInvariant();
        }

        private static void Refresh()
        {
            var shown = new List<Replay.Info>();
            string f = filter.Trim().ToUpperInvariant();
            foreach (Replay.Info i in infos)
            {
                if (f.Length == 0 || Tags(i).Contains(f))
                {
                    shown.Add(i);
                }
            }
            var sb = new System.Text.StringBuilder(f).Append('|').Append(expanded);
            foreach (Replay.Info i in shown)
            {
                sb.Append('|').Append(i.File);
            }
            if (sb.ToString() == shownKey)
            {
                return;
            }
            shownKey = sb.ToString();
            Ui.Clear(list);
            foreach (Texture2D t in pulses)
            {
                UnityEngine.Object.Destroy(t);
            }
            pulses.Clear();
            outlined.Clear();
            empty.text = shown.Count > 0 ? "" : infos.Count > 0 ? "NOTHING MATCHES"
                : "NO REPLAYS YET\n<size=14><color=#777777>A REPLAY IS SAVED AT A LEVEL'S END, OR FROM ESC > KILLCRAFT > REPLAY</color></size>";
            foreach (Replay.Info i in shown)
            {
                try
                {
                    Row(i);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"replays: couldn't show {Path.GetFileName(i.File)} ({e.Message})");
                }
            }
            Ui.Refont(list);
            // (Just opened: the page and its replays appear row by row, top down, as the party screen's.)
            if (revealNext)
            {
                revealNext = false;
                Canvas.ForceUpdateCanvases();
                Ui.Reveal((RectTransform)canvas.transform, page, list);
            }
            foreach (TextMeshProUGUI t in outlined)
            {
                try
                {
                    t.outlineWidth = 0.18f;
                    t.outlineColor = new Color32(24, 24, 24, 255);
                }
                catch
                {
                }
            }
        }

        private static void Row(Replay.Info i)
        {
            bool open = expanded == i.File;
            RectTransform row = Ui.Row(list, open ? HeadH + CaseH : HeadH);
            Ui.Picture(Ui.Fill(row, "Black", 3f), null, new Color(0.02f, 0.02f, 0.02f, 0.95f));
            // The run's picture (not stretched), fading to black.
            Texture2D pic = Picture(i);
            if (pic != null)
            {
                const float pw = 820f, ph = HeadH - 8f;
                RectTransform pr = Ui.Rect(row, "Picture", 4f, 4f, pw, ph);
                RawImage raw = pr.gameObject.AddComponent<RawImage>();
                raw.texture = pic;
                raw.raycastTarget = false;
                float v = Mathf.Clamp01((float)pic.width / pic.height / (pw / ph));
                raw.uvRect = new Rect(0f, (1f - v) / 2f, 1f, v);
                RectTransform fr = Ui.Rect(pr, "Fade", pw - 400f, 0f, 400f, ph);
                RawImage fi = fr.gameObject.AddComponent<RawImage>();
                fi.texture = fade;
                fi.raycastTarget = false;
            }
            Ui.Sliced(Ui.Picture(Ui.Fill(row, "Line"), Ui.SmallLine, open ? KillcraftPanel.KillRed : Color.white), 2.5f);
            string name = (i.Level ?? "").ToUpperInvariant();
            int colon = name.IndexOf(':');
            string number = colon > 0 ? name.Substring(0, colon).Trim() : "";
            string title = colon > 0 ? name.Substring(colon + 1).Trim() : name;
            float tx = 22f;
            if (number.Length > 0)
            {
                TextMeshProUGUI nt = Ui.Text(row, Typing.Escape(number), 19f, KillcraftPanel.KillRed, TextAlignmentOptions.MidlineLeft, tx, 10f, 80f, 26f);
                outlined.Add(nt);
                tx += number.Length * 12f + 12f;
            }
            if (!string.IsNullOrEmpty(i.Difficulty))
            {
                RectTransform tag = Ui.Rect(row, "Difficulty", tx, 13f, i.Difficulty.Length * 8.4f + 22f, 20f);
                Ui.Picture(tag, Ui.SmallFill, new Color(0.33f, 0.33f, 0.37f, 1f));
                Ui.Label(Ui.Fill(tag, "Text"), Typing.Escape(i.Difficulty), 12f, Color.white, TextAlignmentOptions.Midline);
            }
            outlined.Add(Ui.Text(row, Typing.Escape(title), 33f, Color.white, TextAlignmentOptions.MidlineLeft, 22f, 40f, 560f, 50f));
            Ui.Text(row, "PLAYERS:", 13f, Color.white, TextAlignmentOptions.TopLeft, 592f, 26f, 120f, 18f);
            var players = new List<string>();
            for (int k = 0; k < i.Players.Length && k < 3; k++)
            {
                string p = Typing.Escape(i.Players[k]);
                players.Add(k == 0 && i.Players.Length > 1 ? $"<color=#{Ui.Hex(KillcraftPanel.KillRed)}>{p}</color>" : p);
            }
            TextMeshProUGUI who = Ui.Text(row, string.Join("\n", players), 15f, Color.white, TextAlignmentOptions.TopLeft, 592f, 46f, 140f, 62f);
            who.enableWordWrapping = false;
            who.overflowMode = TextOverflowModes.Truncate;
            Ui.Text(row, "REPLAY LENGTH", 13f, Color.white, TextAlignmentOptions.MidlineRight, 690f, 16f, 160f, 18f);
            Ui.Text(row, Replay.Clock(i.Length), 29f, Color.white, TextAlignmentOptions.MidlineRight, 690f, 32f, 160f, 32f);
            Ui.Text(row, "REPLAY DATE", 13f, Color.white, TextAlignmentOptions.MidlineRight, 690f, 64f, 160f, 18f);
            Ui.Text(row, i.When.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant(), 25f, Color.white, TextAlignmentOptions.MidlineRight, 690f, 80f, 160f, 28f);
            // The rank: its colour's box, the letter white in the middle.
            string rank = string.IsNullOrEmpty(i.Rank) ? "-" : i.Rank.ToUpperInvariant();
            RectTransform rb = Ui.Rect(row, "Rank", 868f, 22f, 68f, 68f);
            Ui.Picture(rb, Ui.SmallFill, RankColor(rank));
            Ui.Label(Ui.Fill(rb, "Text"), Typing.Escape(rank), 52f, Color.white, TextAlignmentOptions.Midline);
            if (i.Finished || i.TimeRank.Length > 0)
            {
                Ranked(row, 952f, 22f, i.TimeRank, "TIME", PartyHud.Clock(i.Seconds));
                Ranked(row, 952f, 48f, i.KillsRank, "KILLS", i.Kills.ToString());
                Ranked(row, 952f, 74f, i.StyleRank, "STYLE", i.Style.ToString("N0"));
            }
            UiButton play = Ui.Button(row, "PLAY", 18f, () => ReplayPlayer.Play(i, true), 1060f, 26f, 88f, 36f, TextAlignmentOptions.Center, true, 2.5f);
            play.On = true;
            play.Accent = KillcraftPanel.KillRed;
            play.TextOn = Color.white;
            play.Refresh();
            // X: deletes it, clicked twice (the first turns it red: sure?).
            UiButton x = null;
            x = Ui.Button(row, "X", 18f, () =>
            {
                if (armedFile == i.File && Time.unscaledTime - armedAt < 3f)
                {
                    armedFile = "";
                    Replay.Delete(i);
                    listedAt = -100f;
                    return;
                }
                armedFile = i.File;
                armedAt = Time.unscaledTime;
                x.On = true;
                x.Refresh();
            }, 1154f, 26f, 40f, 36f, TextAlignmentOptions.Center, true, 2.5f);
            x.Accent = KillcraftPanel.KillRed;
            x.TextOn = Color.white;
            x.On = armedFile == i.File && Time.unscaledTime - armedAt < 3f;
            x.Refresh();
            row.gameObject.AddComponent<UiDisarm>().Watch(x, i.File);
            UiButton cf = Ui.Button(row, open ? "CLOSE  <size=9>▲</size>" : "CASE FILE  <size=9>▼</size>", 12f, () =>
            {
                expanded = expanded == i.File ? "" : i.File;
                grow = expanded;
            }, 1060f, 70f, 134f, 24f, TextAlignmentOptions.Center, true, 0.01f);
            cf.Bare = true;
            cf.Refresh();
            if (open)
            {
                // (Inside the row, so it's cut off while the row opens: UltraLink's opening.)
                row.gameObject.AddComponent<RectMask2D>();
                RectTransform box = Ui.Fill(row, "Case File");
                CanvasGroup group = box.gameObject.AddComponent<CanvasGroup>();
                CaseFile(box, i);
                if (grow == i.File)
                {
                    grow = "";
                    row.gameObject.AddComponent<UiGrow>().Play(HeadH, HeadH + CaseH, group);
                }
            }
        }

        // "S  TIME: 7:44": the rank's letter in its colour in a small white-edged box, then the line.
        private static void Ranked(RectTransform row, float x, float y, string rank, string what, string value)
        {
            RectTransform box = Ui.Rect(row, "Chip", x, y, 20f, 20f);
            Ui.Picture(Ui.Fill(box, "Fill", 1f), Ui.SmallFill, new Color(0.16f, 0.04f, 0.03f, 1f));
            Ui.Sliced(Ui.Picture(Ui.Fill(box, "Line"), Ui.SmallLine, new Color(0.82f, 0.82f, 0.82f)), 1.6f);
            Ui.Label(Ui.Fill(box, "Text"), Typing.Escape((rank ?? "").ToUpperInvariant()), 14f, RankColor(rank), TextAlignmentOptions.Midline);
            Ui.Text(row, $"{what}: {Typing.Escape(value)}", 15f, Color.white, TextAlignmentOptions.MidlineLeft, x + 28f, y - 4f, 100f, 28f).overflowMode = TextOverflowModes.Ellipsis;
        }

        // ULTRAKILL's rank colours (StatsManager's).
        public static Color RankColor(string rank)
        {
            switch ((rank ?? "").ToUpperInvariant())
            {
                case "S": return new Color(1f, 0f, 0f);
                case "A": return new Color(1f, 0.416f, 0f);
                case "B": return new Color(1f, 0.847f, 0f);
                case "C": return new Color(0.3f, 1f, 0f);
                case "D": return new Color(0f, 0.58f, 1f);
                case "P": return new Color(1f, 0.686f, 0f);
                default: return new Color(0.35f, 0.35f, 0.35f);
            }
        }

        private static Texture2D Picture(Replay.Info i)
        {
            if (pictures.TryGetValue(i.File, out Texture2D t))
            {
                return t;
            }
            t = null;
            try
            {
                if (File.Exists(i.Picture))
                {
                    t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (!ImageConversion.LoadImage(t, File.ReadAllBytes(i.Picture)))
                    {
                        UnityEngine.Object.Destroy(t);
                        t = null;
                    }
                }
            }
            catch
            {
                t = null;
            }
            pictures[i.File] = t;
            return t;
        }

        // The run's marks (checkpoints, arenas) in order, those a moment after one with the same
        // name left out (arenas started one after another).
        public static List<KeyValuePair<float, string>> Marks(Replay.Info i)
        {
            var marks = new List<KeyValuePair<float, string>>();
            for (int k = 0; k < i.MarkTimes.Length && k < i.MarkLabels.Length; k++)
            {
                if (marks.Count > 0 && i.MarkTimes[k] - marks[marks.Count - 1].Key < 3f && i.MarkLabels[k] == "ARENA")
                {
                    continue;
                }
                marks.Add(new KeyValuePair<float, string>(i.MarkTimes[k], i.MarkLabels[k]));
            }
            return marks;
        }

        // ---- the CASE FILE ----

        private static void CaseFile(RectTransform row, Replay.Info i)
        {
            float y0 = HeadH;
            Ui.Line(row, new Color(0.45f, 0.45f, 0.45f), 18f, y0, RowW - 36f, 1f);
            // How it went.
            float cx = 20f;
            foreach (string tag in new[]
            {
                i.TookDamage ? "DAMAGE TAKEN" : "NO DAMAGE",
                $"RESTARTS {i.Restarts}",
                $"SECRETS {i.SecretsFound} / {i.SecretsTotal}",
                i.MajorUsed ? "MAJOR ASSISTS" : "NO MAJOR ASSISTS",
                string.IsNullOrEmpty(i.Difficulty) ? "-" : i.Difficulty,
            })
            {
                float w = 22f + tag.Length * 8.4f;
                RectTransform chip = Ui.Rect(row, "Tag", cx, y0 + 12f, w, 23f);
                Ui.Sliced(Ui.Picture(chip, Ui.SmallLine, Color.white), 2f);
                Ui.Label(Ui.Fill(chip, "Text"), tag, 12f, Color.white, TextAlignmentOptions.Midline);
                cx += w + 7f;
            }
            // Health through the run: the marks' names in the band along its top, each cut short
            // where the next begins.
            Header(row, 20f, y0 + 44f, "RUN PULSE", "HP");
            Ui.Text(row, $"LOWEST HP {i.LowestHp}", 13f, Color.white, TextAlignmentOptions.MidlineRight, RowW - 320f, y0 + 44f, 300f, 18f);
            const float gw = RowW - 40f, sh = 19f, gh = 56f;
            float gy = y0 + 66f;
            Texture2D pulse = ReplayPlayer.Pulse(i, (int)gw, (int)(sh + gh), (int)sh);
            pulses.Add(pulse);
            RectTransform gr = Ui.Rect(row, "Pulse", 20f, gy, gw, sh + gh);
            RawImage raw = gr.gameObject.AddComponent<RawImage>();
            raw.texture = pulse;
            raw.raycastTarget = false;
            float len = Mathf.Max(1f, i.Length);
            var marks = Marks(i);
            marks.Insert(0, new KeyValuePair<float, string>(0f, "START"));
            for (int k = 0; k < marks.Count; k++)
            {
                float mx = marks[k].Key / len * gw;
                float next = k + 1 < marks.Count ? marks[k + 1].Key / len * gw : gw;
                float room = next - mx - 7f;
                if (room < 18f)
                {
                    continue;
                }
                string label = k == 0 ? "START" : $"{marks[k].Value} {Short(marks[k].Key)}";
                Ui.Text(gr, label, 11f, Color.white, TextAlignmentOptions.MidlineLeft, mx + 5f, 0f, room, sh).overflowMode = TextOverflowModes.Ellipsis;
            }
            Ui.Text(row, "0:00", 11f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 20f, gy + sh + gh + 3f, 60f, 15f);
            Ui.Text(row, Replay.Clock(len / 2f), 11f, Ui.Grey, TextAlignmentOptions.Midline, 20f + gw / 2f - 30f, gy + sh + gh + 3f, 60f, 15f);
            Ui.Text(row, Replay.Clock(len), 11f, Ui.Grey, TextAlignmentOptions.MidlineRight, 20f + gw - 60f, gy + sh + gh + 3f, 60f, 15f);
            float cy = gy + sh + gh + 26f;
            Weapons(row, i, 20f, cy);
            Kills(row, i, 418f, cy);
            Style(row, i, 682f, cy);
            Ladders(row, i, 946f, cy);
        }

        private static string Short(float t) => t < 60f ? $"{(int)t}S" : Replay.Clock(t);

        private static void Header(RectTransform row, float x, float y, string a, string b)
        {
            Ui.Text(row, $"<color=#{Ui.Hex(KillcraftPanel.KillRed)}>{a}</color>   <color=#{Ui.Hex(KillcraftPanel.KillRed)}>{b}</color>", 13f, Color.white, TextAlignmentOptions.MidlineLeft, x, y, 300f, 18f);
        }

        private static readonly Color[] VariantColors =
        {
            new Color(0.25f, 0.65f, 1f), new Color(0.3f, 0.95f, 0.3f), new Color(1f, 0.25f, 0.2f), new Color(1f, 0.8f, 0.2f),
        };

        private const float LineStep = 18f, FirstLine = 22f;

        private static void Weapons(RectTransform row, Replay.Info i, float x, float y)
        {
            Header(row, x, y, "WEAPON TIME", "IN HAND");
            float total = 0f;
            foreach (float s in i.WeaponSeconds)
            {
                total += s;
            }
            for (int k = 0; k < i.Weapons.Length && k < 6; k++)
            {
                float ry = y + FirstLine + k * LineStep;
                float s = i.WeaponSeconds[k];
                float pct = total > 0f ? s / total : 0f;
                Ui.Text(row, Typing.Escape(i.Weapons[k]), 12f, Color.white, TextAlignmentOptions.MidlineLeft, x, ry, 186f, 16f).overflowMode = TextOverflowModes.Ellipsis;
                Ui.Image(row, null, new Color(0.15f, 0.15f, 0.15f), x + 190f, ry + 4f, 100f, 8f);
                int c = k < i.WeaponColors.Length ? Mathf.Clamp(i.WeaponColors[k], 0, VariantColors.Length - 1) : 0;
                Ui.Image(row, null, VariantColors[c], x + 190f, ry + 4f, Mathf.Max(2f, 100f * pct / Mathf.Max(0.01f, i.WeaponSeconds[0] / total)), 8f);
                Ui.Text(row, s >= 60f ? Replay.Clock(s) : $"{(int)s}S", 12f, Color.white, TextAlignmentOptions.MidlineRight, x + 294f, ry, 46f, 16f);
                Ui.Text(row, $"{Mathf.RoundToInt(pct * 100f)}%", 12f, Ui.Grey, TextAlignmentOptions.MidlineRight, x + 340f, ry, 40f, 16f);
            }
        }

        private static void Kills(RectTransform row, Replay.Info i, float x, float y)
        {
            Header(row, x, y, "KILLS", Mathf.Max(i.Kills, i.KillTypes.Length).ToString());
            var counts = new Dictionary<string, int>();
            foreach (string t in i.KillTypes)
            {
                counts.TryGetValue(t, out int n);
                counts[t] = n + 1;
            }
            var sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int k = 0; k < sorted.Count && k < 24; k++)
            {
                float ix = x + (k % 4) * 62f, iy = y + FirstLine + (k / 4) * LineStep;
                Sprite s = Icons.For(sorted[k].Key);
                if (s != null)
                {
                    Image img = Ui.Image(row, s, Color.white, ix, iy - 1f, 18f, 18f);
                    img.preserveAspect = true;
                }
                else
                {
                    Ui.Text(row, Abbrev(sorted[k].Key), 10f, Color.white, TextAlignmentOptions.Midline, ix, iy, 18f, 16f);
                }
                Ui.Text(row, $"<color=#777777>·</color>{sorted[k].Value}", 12f, Color.white, TextAlignmentOptions.MidlineLeft, ix + 21f, iy, 40f, 16f);
            }
        }

        private static string Abbrev(string type) => string.IsNullOrEmpty(type) ? "?" : type.Substring(0, Math.Min(2, type.Length)).ToUpperInvariant();

        private static void Style(RectTransform row, Replay.Info i, float x, float y)
        {
            Header(row, x, y, "STYLE", "TOP BONUSES");
            for (int k = 0; k < i.StyleNames.Length && k < 6; k++)
            {
                float ry = y + FirstLine + k * LineStep;
                Ui.Text(row, Typing.Escape(i.StyleNames[k]), 12f, Color.white, TextAlignmentOptions.MidlineLeft, x, ry, 150f, 16f).overflowMode = TextOverflowModes.Ellipsis;
                Ui.Text(row, $"·{(k < i.StyleCounts.Length ? i.StyleCounts[k] : 0)}", 12f, Ui.Grey, TextAlignmentOptions.MidlineRight, x + 150f, ry, 40f, 16f);
                Ui.Text(row, (k < i.StylePoints.Length ? i.StylePoints[k] : 0).ToString("N0"), 12f, Color.white, TextAlignmentOptions.MidlineRight, x + 190f, ry, 62f, 16f);
            }
        }

        private static void Ladders(RectTransform row, Replay.Info i, float x, float y)
        {
            Header(row, x, y, "RANK LADDERS", "");
            Ladder(row, x, y + FirstLine + 2f, "TIME", i.TimeRanks, i.Seconds, true, PartyHud.Clock(i.Seconds), i.TimeRank);
            Ladder(row, x, y + FirstLine + 26f, "KILLS", i.KillRanks, i.Kills, false, i.Kills.ToString(), i.KillsRank);
            Ladder(row, x, y + FirstLine + 50f, "STYLE", i.StyleRanks, i.Style, false, i.Style.ToString("N0"), i.StyleRank);
        }

        // A rank's ladder: D to S along a bar, its steps marked, where the run got to.
        private static void Ladder(RectTransform row, float x, float y, string label, int[] ranks, float value, bool reverse, string shown, string rank)
        {
            Ui.Text(row, label, 12f, Color.white, TextAlignmentOptions.MidlineLeft, x, y, 52f, 16f);
            const float bw = 106f;
            float bx = x + 54f;
            Ui.Image(row, null, new Color(0.14f, 0.14f, 0.14f), bx, y + 3f, bw, 10f);
            if (ranks != null && ranks.Length >= 4)
            {
                float top = Mathf.Max(1f, (reverse ? ranks[0] : ranks[ranks.Length - 1]) * 1.25f);
                Func<float, float> at = v => Mathf.Clamp01(reverse ? 1f - v / top : v / top) * bw;
                string[] letters = { "C", "B", "A", "S" };
                for (int k = 0; k < 4; k++)
                {
                    float px = at(ranks[k]);
                    Ui.Image(row, null, new Color(0.45f, 0.45f, 0.45f), bx + px, y + 2f, 1f, 12f);
                    Ui.Text(row, letters[k], 8f, new Color(0.6f, 0.6f, 0.6f), TextAlignmentOptions.Midline, bx + px - 10f, y + 2f, 10f, 12f);
                }
                Ui.Image(row, null, Color.white, bx + at(value) - 1f, y, 3f, 16f);
            }
            Ui.Text(row, Typing.Escape(shown), 12f, Color.white, TextAlignmentOptions.MidlineRight, bx + bw + 2f, y, 70f, 16f);
            Ui.Text(row, Typing.Escape(rank ?? ""), 13f, RankColor(rank), TextAlignmentOptions.MidlineRight, bx + bw + 74f, y, 14f, 16f);
        }
    }

    // A row opening (the CASE FILE): it grows to its height, what's in it fading in.
    internal sealed class UiGrow : MonoBehaviour
    {
        private float from, to, at = -1f;
        private CanvasGroup fade;
        private const float Length = 0.24f;

        public void Play(float from, float to, CanvasGroup fade)
        {
            this.from = from;
            this.to = to;
            this.fade = fade;
            at = Time.unscaledTime;
            Apply(0f);
        }

        private void Update()
        {
            if (at < 0f)
            {
                return;
            }
            float t = Mathf.Clamp01((Time.unscaledTime - at) / Length);
            Apply(t);
            if (t >= 1f)
            {
                at = -1f;
            }
        }

        private void Apply(float t)
        {
            float e = 1f - Mathf.Pow(1f - t, 3f);
            float h = Mathf.Lerp(from, to, e);
            var rt = (RectTransform)transform;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
            LayoutElement le = GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredHeight = h;
                le.minHeight = h;
            }
            if (fade != null)
            {
                fade.alpha = Mathf.Clamp01((t - 0.2f) / 0.8f);
            }
            if (transform.parent is RectTransform parent)
            {
                LayoutRebuilder.MarkLayoutForRebuild(parent);
            }
        }
    }

    // (A replay's X, red after its first click: back to white if the second doesn't come in 3 s.)
    internal sealed class UiDisarm : MonoBehaviour
    {
        private UiButton button;
        private string file;

        public void Watch(UiButton b, string f)
        {
            button = b;
            file = f;
        }

        private void Update()
        {
            if (button != null && button.On && (Replays.armedFile != file || Time.unscaledTime - Replays.armedAt >= 3f))
            {
                button.On = false;
                button.Refresh();
            }
        }
    }
}
