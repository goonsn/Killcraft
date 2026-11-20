using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // The party's score in a level: each player's kills (the enemies they dealt the last hit to: the
    // one who runs the enemies says whose each was, EnemySync), style points, deaths, revives, the best
    // style rank they reached, and their style meter now (its rank and how full: on their card, PartyHud).
    // Each player's game counts its own and says it to the others in the level twice a second.
    // The scoreboard: at the end of a level (beside ULTRAKILL's own results), and while ScoreboardKey
    // is held in one. In VERSUS, with each one's place (Versus).
    internal static class Score
    {
        public const byte Message = 62;
        private static readonly string[] Letters = { "D", "C", "B", "A", "S", "SS", "SSS", "U" };

        public sealed class Stats
        {
            public string Scene = "";
            public int Kills, Deaths, Revives, Style, Wave;
            public int Best = -1, Rank = -1;
            public float Meter;
            public float At = -100f;
            public bool Fresh => Time.unscaledTime - At < 4f;
        }

        public static readonly Stats Mine = new Stats();
        private static readonly Dictionary<int, Stats> others = new Dictionary<int, Stats>();
        private static float sentAt = -100f;
        private static bool wasDead;
        private static readonly AccessTools.FieldRef<StyleHUD, float> meterOf = AccessTools.FieldRefAccess<StyleHUD, float>("currentMeter");

        public static Stats Of(int id)
        {
            if (id == Room.MyId)
            {
                return Mine;
            }
            if (!others.TryGetValue(id, out Stats s))
            {
                others[id] = s = new Stats();
            }
            return s;
        }

        // (EnemySync: an enemy this player killed.)
        public static void Killed() => Mine.Kills++;

        // (Spectate: this player brought someone back.)
        public static void Revived() => Mine.Revives++;

        public static void Frame(NewMovement nm, bool inLevel)
        {
            // (Out of a level - the menu, a load: the next one, even the same again, counted afresh.)
            if (!inLevel)
            {
                Mine.Scene = "";
                Board.Frame(false);
                return;
            }
            string scene = SceneHelper.CurrentScene ?? "";
            if (scene != Mine.Scene)
            {
                // (A new level: counted from nothing.)
                Mine.Scene = scene;
                Mine.Kills = Mine.Deaths = Mine.Revives = Mine.Style = Mine.Wave = 0;
                Mine.Best = Mine.Rank = -1;
                Mine.Meter = 0f;
                wasDead = false;
                Board.Forget();
            }
            if (nm == null || ReplayPlayer.Active)
            {
                Board.Frame(false);
                return;
            }
            if (nm.dead && !wasDead)
            {
                Mine.Deaths++;
            }
            wasDead = nm.dead;
            try
            {
                StatsManager sm = MonoSingleton<StatsManager>.Instance;
                if (sm != null)
                {
                    Mine.Style = sm.stylePoints;
                }
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null && sh.ranks != null && sh.ranks.Count > 0)
                {
                    float meter = meterOf(sh);
                    int rank = sh.rankIndex;
                    Mine.Rank = meter > 0f || rank > 0 ? rank : -1;
                    Mine.Meter = Mathf.Clamp01(meter / Mathf.Max(1f, sh.currentRank.maxMeter));
                    if (Mine.Rank > Mine.Best)
                    {
                        Mine.Best = Mine.Rank;
                    }
                }
                EndlessGrid grid = MonoSingleton<EndlessGrid>.Instance;
                Mine.Wave = grid != null ? grid.currentWave : 0;
            }
            catch
            {
            }
            Mine.At = Time.unscaledTime;
            if (KillNet.Connected && Time.unscaledTime - sentAt > 0.5f)
            {
                sentAt = Time.unscaledTime;
                Stats m = Mine;
                KillNet.Send(Message, KillNet.ToRoom, w =>
                {
                    w.Write(m.Scene);
                    w.Write((short)Mathf.Clamp(m.Kills, 0, short.MaxValue));
                    w.Write((byte)Mathf.Clamp(m.Deaths, 0, 255));
                    w.Write((byte)Mathf.Clamp(m.Revives, 0, 255));
                    w.Write(m.Style);
                    w.Write((sbyte)m.Best);
                    w.Write((sbyte)m.Rank);
                    w.Write((byte)(m.Meter * 255f));
                    w.Write((short)Mathf.Clamp(m.Wave, 0, short.MaxValue));
                });
            }
            Board.Frame(true);
        }

        public static void Handle(int from, BinaryReader r)
        {
            Stats s = Of(from);
            s.Scene = r.ReadString();
            s.Kills = r.ReadInt16();
            s.Deaths = r.ReadByte();
            s.Revives = r.ReadByte();
            s.Style = r.ReadInt32();
            s.Best = r.ReadSByte();
            s.Rank = r.ReadSByte();
            s.Meter = r.ReadByte() / 255f;
            s.Wave = r.ReadInt16();
            s.At = Time.unscaledTime;
        }

        // A player's style meter now, in this level (for their card).
        public static bool MeterOf(int id, out int rank, out float meter)
        {
            Stats s = Of(id);
            rank = s.Rank;
            meter = s.Meter;
            return s.Fresh && s.Scene == (SceneHelper.CurrentScene ?? "") && rank >= 0;
        }

        // ULTRAKILL's own picture of a style rank (D .. ULTRAKILL), if its style meter's here.
        public static Sprite RankSprite(int rank)
        {
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null && sh.ranks != null && rank >= 0 && rank < sh.ranks.Count)
                {
                    return sh.ranks[rank].sprite;
                }
            }
            catch
            {
            }
            return null;
        }

        public static string RankLetter(int rank) => rank >= 0 && rank < Letters.Length ? Letters[rank] : "-";

        public static void Forget()
        {
            others.Clear();
        }

        // ---- the scoreboard ----

        private static class Board
        {
            private static Canvas canvas;
            private static RectTransform panel, rowsArea;
            private static TextMeshProUGUI title, header;
            private static float builtAt = -100f;
            private static string shownKey = "";
            private static bool atEnd;

            public static void Forget()
            {
                shownKey = "";
            }

            public static void Frame(bool inLevel)
            {
                bool done = Profiles.Mine.State == Profiles.StateDone || Versus.Finished;
                bool held = inLevel && InputKeys.Held(Plugin.ScoreboardKey.Value) && !Typing.Active && !PartyMenu.Open && !KillcraftPanel.Open;
                bool want = Plugin.Scoreboard.Value && inLevel && (held || (done && Party.Members.Count > 1)) && OthersHere() > 0;
                if (!want)
                {
                    if (canvas != null && canvas.gameObject.activeSelf)
                    {
                        canvas.gameObject.SetActive(false);
                    }
                    return;
                }
                if (canvas == null)
                {
                    Build();
                }
                if (!canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(true);
                    builtAt = -100f;
                }
                // (Held: in the middle. The level done: on the right, beside ULTRAKILL's results.)
                bool end = !held;
                if (end != atEnd)
                {
                    atEnd = end;
                    panel.anchorMin = panel.anchorMax = panel.pivot = end ? new Vector2(1f, 0.5f) : new Vector2(0.5f, 0.5f);
                    panel.anchoredPosition = end ? new Vector2(-24f, 0f) : Vector2.zero;
                    panel.localScale = end ? Vector3.one * 0.85f : Vector3.one;
                }
                if (Time.unscaledTime - builtAt > 0.25f)
                {
                    builtAt = Time.unscaledTime;
                    Fill();
                }
            }

            private static int OthersHere()
            {
                if (Versus.On && Versus.WholeLayer)
                {
                    return Versus.InLayer().Count - 1;
                }
                int n = 0;
                string scene = SceneHelper.CurrentScene ?? "";
                foreach (var kv in others)
                {
                    if (kv.Key != Room.MyId && kv.Value.Fresh && kv.Value.Scene == scene)
                    {
                        n++;
                    }
                }
                return n;
            }

            private const float W = 820f, RowH = 46f, Top = 104f;

            private static void Build()
            {
                canvas = Ui.Canvas("Killcraft scoreboard", 30, true);
                panel = Ui.Rect(canvas.transform, "Scoreboard", 0f, 0f, W, 200f);
                panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
                panel.anchoredPosition = Vector2.zero;
                atEnd = false;
                Ui.Picture(Ui.Fill(panel, "Back"), null, new Color(0f, 0f, 0f, 0.86f));
                Ui.Sliced(Ui.Picture(Ui.Fill(panel, "Frame"), Ui.BoxLine, Color.white), 3f);
                title = Ui.Text(panel, "", 34f, Color.white, TextAlignmentOptions.Center, 0f, 14f, W, 44f);
                header = Ui.Text(panel, "", 18f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 0f, 64f, W, 28f);
                header.overflowMode = TextOverflowModes.Overflow;
                Ui.Line(panel, Color.white, 24f, 96f, W - 48f, 2f);
                rowsArea = Ui.Rect(panel, "Rows", 0f, Top, W, 0f);
                canvas.gameObject.SetActive(false);
            }

            private struct Line
            {
                public int Id;
                public Stats S;
                public Profiles.Info P;
                public int Place;
            }

            // Columns: x, width, and what's in them.
            private static readonly float[] ColX = { 300f, 400f, 530f, 620f, 720f };

            private static void Fill()
            {
                string scene = SceneHelper.CurrentScene ?? "";
                bool versus = Versus.On;
                bool grind = scene == "Endless";
                bool race = versus && !grind;
                bool layer = race && Versus.WholeLayer;
                var lines = new List<Line>();
                if (race)
                {
                    // (The race's own order: Versus.)
                    foreach (Versus.Run run in Versus.Standings())
                    {
                        lines.Add(new Line { Id = run.Id, S = Of(run.Id), P = run.Id == Room.MyId ? Profiles.Mine : Profiles.Of(run.Id), Place = Versus.PlaceOf(run.Id) });
                    }
                }
                else
                {
                    lines.Add(new Line { Id = Room.MyId, S = Mine, P = Profiles.Mine });
                    foreach (var kv in others)
                    {
                        if (kv.Key != Room.MyId && kv.Value.Fresh && kv.Value.Scene == scene)
                        {
                            lines.Add(new Line { Id = kv.Key, S = kv.Value, P = Profiles.Of(kv.Key) });
                        }
                    }
                    for (int i = 0; i < lines.Count; i++)
                    {
                        Line l = lines[i];
                        l.Place = versus ? Versus.PlaceOf(l.Id) : 0;
                        lines[i] = l;
                    }
                    lines.Sort((a, b) =>
                    {
                        if (versus && a.Place != b.Place)
                        {
                            return (a.Place <= 0 ? 99 : a.Place).CompareTo(b.Place <= 0 ? 99 : b.Place);
                        }
                        return b.S.Style != a.S.Style ? b.S.Style.CompareTo(a.S.Style) : b.S.Kills.CompareTo(a.S.Kills);
                    });
                }
                // (What to say: built again only when it changed.)
                var key = new System.Text.StringBuilder();
                foreach (Line l in lines)
                {
                    Versus.Run vr = Versus.Of(l.Id);
                    key.Append(vr.Done).Append(vr.Points).Append(vr.Levels).Append((int)vr.LayerSeconds).Append(vr.Scene);
                    key.Append(l.Id).Append(':').Append(l.S.Kills).Append(',').Append(l.S.Style).Append(',').Append(l.S.Deaths).Append(',')
                        .Append(l.S.Revives).Append(',').Append(l.S.Best).Append(',').Append(l.S.Wave).Append(',').Append(l.Place).Append(',')
                        .Append(l.P.State).Append(',').Append((int)l.P.Seconds).Append(';');
                }
                key.Append(versus).Append(atEnd);
                if (key.ToString() == shownKey)
                {
                    return;
                }
                shownKey = key.ToString();
                title.text = !versus ? $"-- {Typing.Escape(Party.LevelName(scene).ToUpperInvariant())} --"
                    : grind ? "-- VERSUS: LAST ONE STANDING --"
                    : layer ? $"-- VERSUS: {Versus.LayerName(Versus.Mine.Layer)} --"
                    : $"-- VERSUS: {(Versus.ByScore ? "SCORE" : "RACE")} --";
                string first = layer ? "LEVEL" : "KILLS";
                string third = versus ? (grind ? "WAVE" : "TIME") : "DEATHS";
                string fourth = race && Versus.ByScore ? "RANK" : versus ? "DEATHS" : "REVIVES";
                header.text = Col("PLAYER", 30f) + Col(first, ColX[0]) + Col("STYLE", ColX[1]) + Col(third, ColX[2]) + Col(fourth, ColX[3]) + Col("BEST", ColX[4]);
                Ui.Clear(rowsArea);
                float y = 0f;
                foreach (Line l in lines)
                {
                    RectTransform row = Ui.Rect(rowsArea, "Row", 0f, y, W, RowH);
                    bool me = l.Id == Room.MyId;
                    if (me)
                    {
                        Ui.Image(row, null, new Color(1f, 1f, 1f, 0.07f), 12f, 2f, W - 24f, RowH - 4f);
                    }
                    Ui.Image(row, null, l.P.Color, 18f, 8f, 5f, RowH - 16f);
                    string name = Typing.Escape(me ? Profiles.Mine.SteamName : Profiles.NameOf(l.Id));
                    string place = versus && l.Place > 0 ? $"<color=#FFD02E>{Ordinal(l.Place)}</color>  " : "";
                    string dead = versus && grind && l.P.State == Profiles.StateDead ? " <color=#E05040>OUT</color>" : "";
                    Ui.Text(row, place + name + dead, 24f, Color.white, TextAlignmentOptions.MidlineLeft, 34f, 0f, ColX[0] - 44f, RowH);
                    Versus.Run run = Versus.Of(l.Id);
                    string firstV = layer ? Typing.Escape(LevelShort(run.Scene)) : l.S.Kills.ToString();
                    int style = layer ? run.LayerStyle + (run.Done ? 0 : run.Style) : race ? run.Style : l.S.Style;
                    Ui.Text(row, firstV, 24f, Color.white, TextAlignmentOptions.MidlineLeft, ColX[0], 0f, 90f, RowH);
                    Ui.Text(row, style.ToString("N0"), 24f, Ui.Yellow, TextAlignmentOptions.MidlineLeft, ColX[1], 0f, 120f, RowH);
                    float secs = layer ? run.LayerSeconds + (run.Done ? 0f : run.Seconds) : race ? run.Seconds : l.P.Seconds;
                    bool over = layer ? run.LayerDone : run.Done;
                    string thirdV = !versus ? l.S.Deaths.ToString()
                        : grind ? l.S.Wave.ToString()
                        : over ? PartyHud.Clock(secs) : "<color=#888888>" + PartyHud.Clock(secs) + "</color>";
                    string fourthV;
                    if (race && Versus.ByScore)
                    {
                        string letter = layer ? (run.Levels > 0 ? Versus.RankOf(Mathf.RoundToInt(run.LayerPoints / (float)run.Levels)) : "-") : run.Rank;
                        fourthV = $"<color=#{Ui.Hex(Versus.RankColor(letter))}>{letter}</color>";
                    }
                    else
                    {
                        fourthV = versus ? l.S.Deaths.ToString() : l.S.Revives.ToString();
                    }
                    Ui.Text(row, thirdV, 22f, Color.white, TextAlignmentOptions.MidlineLeft, ColX[2], 0f, 100f, RowH).overflowMode = TextOverflowModes.Overflow;
                    Ui.Text(row, fourthV, 24f, Color.white, TextAlignmentOptions.MidlineLeft, ColX[3], 0f, 90f, RowH);
                    Sprite best = RankSprite(l.S.Best);
                    if (best != null)
                    {
                        Image img = Ui.Image(row, best, Color.white, ColX[4] - 2f, 3f, RowH - 6f, RowH - 6f);
                        img.preserveAspect = true;
                    }
                    else
                    {
                        Ui.Text(row, RankLetter(l.S.Best), 24f, Ui.Red, TextAlignmentOptions.MidlineLeft, ColX[4], 0f, 80f, RowH);
                    }
                    y += RowH;
                }
                panel.sizeDelta = new Vector2(W, Top + y + 18f);
                Ui.Refont(panel);
            }

            private static string Col(string text, float x) => $"<pos={x:0}>{text}";

            // "Level 0-3" -> "0-3".
            private static string LevelShort(string scene) =>
                !string.IsNullOrEmpty(scene) && scene.StartsWith("Level ", System.StringComparison.Ordinal) ? scene.Substring(6) : "-";
        }

        public static string Ordinal(int n)
        {
            int t = n % 100;
            string s = t >= 11 && t <= 13 ? "TH" : (n % 10) == 1 ? "ST" : (n % 10) == 2 ? "ND" : (n % 10) == 3 ? "RD" : "TH";
            return n + s;
        }
    }
}
