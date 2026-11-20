using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // VERSUS (the party host's game mode, PartyRules): each player plays the level on their own - their
    // own enemies, doors, arenas and waves, nothing shared - still seeing the others' V1s run it (their
    // bodies only: no collision, no PvP, no enemies going after them).
    //
    // How it's won (the host's): by SCORE - ULTRAKILL's own grades of the run (its time, kills and style
    // ranks, less a point a restart: the level's rank), then the most style, then the quickest - or by
    // TIME, the first to finish. And the race: EACH LEVEL on its own (nobody's taken on to the next one
    // until everyone's finished this one: the host then takes the party on, together), or the WHOLE
    // LAYER (nobody waits: each level's time and score added up, the result once everyone's finished the
    // layer's last level). In the Cyber Grind, the last one standing (once everyone's out: the highest wave).
    // Taken as the level starts: a change of mode counts from the next one.
    [HarmonyPatch]
    internal static class Versus
    {
        public const byte Message = 64;

        public static bool On { get; private set; }
        public static bool ByScore { get; private set; }
        public static bool WholeLayer { get; private set; }
        public static bool Grind => scene == "Endless";

        // A player's run, as they say it (this player's own: counted here).
        public sealed class Run
        {
            public int Id;
            public string Scene = "", Layer = "";
            public bool Done, LayerDone;
            public float Seconds;
            public int Points = -1;  // (ULTRAKILL's rank score, 0-12: -1 not finished)
            public int Style, Kills;
            public int Levels;  // (this layer's levels finished)
            public float LayerSeconds;  // (those levels' time, added up)
            public int LayerPoints, LayerStyle;
            public float At = -100f;
            public bool Fresh => Id == Room.MyId || Time.unscaledTime - At < 4f;
            public string Rank => RankOf(Points);
        }

        private static readonly Run mine = new Run();
        private static readonly Dictionary<int, Run> others = new Dictionary<int, Run>();
        private static string scene = "";
        private static bool wasIn, menuSeen = true, countedHere;
        private static float sentAt = -100f;

        // The race in this level: who's been told of finishing, the order they did.
        private static readonly HashSet<int> told = new HashSet<int>();
        private static readonly List<int> finishOrder = new List<int>();
        private static int levelWinner, layerWinner;
        private static bool levelDecided, layerDecided;

        // The Cyber Grind.
        private static readonly List<int> alive = new List<int>();
        private static readonly List<(int Id, int Wave, float Seconds)> outs = new List<(int, int, float)>();
        private static int startedWith, grindWinner;
        private static bool grindDecided;

        public static Run Mine
        {
            get
            {
                mine.Id = Room.MyId;
                return mine;
            }
        }

        public static Run Of(int id)
        {
            if (id == Room.MyId)
            {
                mine.Id = id;
                return mine;
            }
            if (!others.TryGetValue(id, out Run r))
            {
                others[id] = r = new Run { Id = id };
            }
            return r;
        }

        // ---- a level starts ----

        // A level just loaded (from the menu, or another level): its mode taken. (The same one again -
        // RESTART MISSION - keeps how the race stands.)
        private static void LevelChanged(string s)
        {
            s = s ?? "";
            bool fresh = s != scene || menuSeen;
            bool fromMenu = menuSeen;
            menuSeen = false;
            scene = s;
            if (!fresh)
            {
                return;
            }
            bool was = On;
            On = SteamNet.InLobby && PartyRules.Mode == PartyRules.ModeVersus && Party.IsLevel(scene) && scene != "uk_construct" && !MinosMode.Active;
            ByScore = PartyRules.VersusScore;
            WholeLayer = PartyRules.VersusLayer && !Grind;
            told.Clear();
            finishOrder.Clear();
            outs.Clear();
            alive.Clear();
            startedWith = 0;
            levelWinner = grindWinner = 0;
            levelDecided = grindDecided = false;
            countedHere = false;
            VersusHud.LevelStarted();
            string layer = LayerOf(scene);
            // (A new layer, or a race begun from the menu: its totals from nothing.)
            if (!On || fromMenu || layer != mine.Layer || layer.Length == 0)
            {
                mine.Levels = 0;
                mine.LayerSeconds = 0f;
                mine.LayerPoints = mine.LayerStyle = 0;
                mine.LayerDone = false;
                layerWinner = 0;
                layerDecided = false;
            }
            mine.Layer = layer;
            mine.Scene = scene;
            mine.Done = false;
            mine.Points = -1;
            mine.Seconds = 0f;
            mine.Style = mine.Kills = 0;
            if (On)
            {
                Plugin.Log.LogInfo($"versus: {scene} is played on your own ({(Grind ? "the last one standing wins" : $"won by {(ByScore ? "score" : "time")}, {(WholeLayer ? $"the whole layer {layer}" : "this level")}")})");
                PartyChat.System(Grind ? "VERSUS: everyone plays their own Cyber Grind. The last one standing wins."
                    : WholeLayer ? $"VERSUS: a race through the whole layer, won by {(ByScore ? "score" : "time")}. Nobody waits between levels."
                    : $"VERSUS: everyone plays the level on their own, won by {(ByScore ? "score (rank, then style, then time)" : "time: the first to finish")}.");
            }
            else if (was)
            {
                Plugin.Log.LogInfo("versus: off for this level");
            }
        }

        // "Level 1-3" -> "1" (its layer); not a numbered level: "".
        public static string LayerOf(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("Level ", StringComparison.Ordinal))
            {
                return "";
            }
            int dash = s.IndexOf('-', 6);
            return dash > 6 ? s.Substring(6, dash - 6) : "";
        }

        public static string LayerName(string layer)
        {
            switch (layer)
            {
                case "0": return "PRELUDE";
                case "1": return "LIMBO";
                case "2": return "LUST";
                case "3": return "GLUTTONY";
                case "4": return "GREED";
                case "5": return "WRATH";
                case "6": return "HERESY";
                case "7": return "VIOLENCE";
                case "8": return "FRAUD";
                case "P": return "PRIME";
                default: return "LAYER " + layer;
            }
        }

        // Messages that still go to the others in VERSUS: where this player's V1 is and what it does.
        public static bool GhostType(byte type) => type == KillNet.PlayerState || type == V1Body.Moves || type == RemotePlayers.HeldMessage;

        // This player's done: finished the race, or out of the Cyber Grind (or its winner).
        public static bool Finished => On && (Grind ? Profiles.Mine.State == Profiles.StateDead || grindWinner == Room.MyId : Profiles.Mine.State == Profiles.StateDone);

        // ---- each frame (Room: before the level's goings-on are shared or not) ----

        public static void Frame(bool inLevel)
        {
            mine.Id = Room.MyId;
            if (!inLevel)
            {
                wasIn = false;
                if (SceneHelper.CurrentScene == "Main Menu")
                {
                    menuSeen = true;
                    On = false;
                }
                VersusHud.Hide();
                return;
            }
            string now = SceneHelper.CurrentScene ?? "";
            if (!wasIn || now != scene)
            {
                wasIn = true;
                LevelChanged(now);
            }
            if (!On || !SteamNet.InLobby)
            {
                VersusHud.Hide();
                return;
            }
            Count();
            Send();
            VersusHud.Frame();
            if (Grind)
            {
                GrindFrame();
            }
            else
            {
                RaceFrame();
            }
        }

        // This player's run, from ULTRAKILL's own counts.
        private static void Count()
        {
            StatsManager sm = MonoSingleton<StatsManager>.Instance;
            if (sm == null || mine.Done)
            {
                return;
            }
            mine.Seconds = sm.seconds;
            mine.Style = sm.stylePoints;
            mine.Kills = sm.kills;
            if (!sm.infoSent || sm.endlessMode)
            {
                return;
            }
            // Finished: its grades as ULTRAKILL works them out (StatsManager.GetRanks / GetFinalRank).
            mine.Done = true;
            mine.Points = Points(sm);
            if (!countedHere)
            {
                countedHere = true;
                mine.Levels++;
                mine.LayerSeconds += mine.Seconds;
                mine.LayerPoints += mine.Points;
                mine.LayerStyle += mine.Style;
                // (The layer's last level: what comes after it isn't one of the layer's.)
                string next = "";
                try
                {
                    FinalRank fr = MonoSingleton<FinalRank>.Instance;
                    next = fr != null ? fr.targetLevelName ?? "" : "";
                }
                catch
                {
                }
                mine.LayerDone = mine.Layer.Length > 0 && LayerOf(next) != mine.Layer;
            }
            Plugin.Log.LogInfo($"versus: finished {scene} in {mine.Seconds:0.000} s, rank {mine.Rank} ({mine.Points}), style {mine.Style}"
                + (WholeLayer ? $"; layer {mine.Layer}: {mine.Levels} levels, {mine.LayerSeconds:0.000} s{(mine.LayerDone ? ", done" : "")}" : ""));
        }

        private static int Grade(int[] ranks, float value, bool lower)
        {
            int n = 0;
            while (ranks != null && n < ranks.Length && (lower ? value <= ranks[n] : value >= ranks[n]))
            {
                n++;
            }
            return Mathf.Min(n, 4);
        }

        private static int Points(StatsManager sm)
        {
            try
            {
                int p = Grade(sm.timeRanks, sm.seconds, true) + Grade(sm.killRanks, sm.kills, false) + Grade(sm.styleRanks, sm.stylePoints, false);
                p = Mathf.Max(0, p - sm.restarts);
                if (sm.majorUsed && p == 12)
                {
                    p = 11;
                }
                return p;
            }
            catch
            {
                return 0;
            }
        }

        // ULTRAKILL's rank letter of a rank score (P: every grade an S, no restarts).
        public static string RankOf(int points) =>
            points < 0 ? "-" : points >= 12 ? "P" : new[] { "D", "C", "B", "A", "S" }[Mathf.Clamp(Mathf.RoundToInt(points / 3f), 0, 4)];

        public static Color RankColor(string rank)
        {
            switch (rank)
            {
                case "D": return new Color(0f, 0.58f, 1f);
                case "C": return new Color(0.3f, 1f, 0f);
                case "B": return new Color(1f, 0.85f, 0f);
                case "A": return new Color(1f, 0.42f, 0f);
                case "S": return new Color(1f, 0f, 0f);
                case "P": return new Color(1f, 0.69f, 0f);
                default: return Ui.Grey;
            }
        }

        private static void Send()
        {
            if (Time.unscaledTime - sentAt < 0.5f)
            {
                return;
            }
            sentAt = Time.unscaledTime;
            Run m = mine;
            KillNet.Send(Message, KillNet.ToEveryone, w =>
            {
                w.Write(m.Scene ?? "");
                w.Write(m.Layer ?? "");
                w.Write((byte)((m.Done ? 1 : 0) | (m.LayerDone ? 2 : 0)));
                w.Write(m.Seconds);
                w.Write((sbyte)Mathf.Clamp(m.Points, -1, 12));
                w.Write(m.Style);
                w.Write((short)Mathf.Clamp(m.Kills, 0, short.MaxValue));
                w.Write((byte)Mathf.Clamp(m.Levels, 0, 255));
                w.Write(m.LayerSeconds);
                w.Write((short)Mathf.Clamp(m.LayerPoints, 0, short.MaxValue));
                w.Write(m.LayerStyle);
            });
        }

        public static void Handle(int from, BinaryReader r)
        {
            Run o = Of(from);
            o.Scene = r.ReadString();
            o.Layer = r.ReadString();
            byte flags = r.ReadByte();
            o.Done = (flags & 1) != 0;
            o.LayerDone = (flags & 2) != 0;
            o.Seconds = r.ReadSingle();
            o.Points = r.ReadSByte();
            o.Style = r.ReadInt32();
            o.Kills = r.ReadInt16();
            o.Levels = r.ReadByte();
            o.LayerSeconds = r.ReadSingle();
            o.LayerPoints = r.ReadInt16();
            o.LayerStyle = r.ReadInt32();
            o.At = Time.unscaledTime;
        }

        // ---- who's in it ----

        // Those in this level's race: this player, and those playing (or finished) this same level.
        public static List<Run> InLevel()
        {
            var list = new List<Run> { mine };
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == Room.MyId)
                {
                    continue;
                }
                Run o = Of(m.Id);
                if (o.Fresh && o.Scene == scene)
                {
                    list.Add(o);
                }
            }
            return list;
        }

        // Those in this layer's race: anyone in a level of it (any of its levels, finished or not).
        public static List<Run> InLayer()
        {
            var list = new List<Run> { mine };
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == Room.MyId)
                {
                    continue;
                }
                Run o = Of(m.Id);
                if (o.Fresh && o.Layer.Length > 0 && o.Layer == mine.Layer)
                {
                    list.Add(o);
                }
            }
            return list;
        }

        // Best first. Score: the rank's score, then style, then time; time: the quickest. Not finished:
        // after those who have.
        private static int Compare(Run a, Run b, bool layer)
        {
            bool ad = layer ? a.LayerDone : a.Done, bd = layer ? b.LayerDone : b.Done;
            if (ad != bd)
            {
                return ad ? -1 : 1;
            }
            if (layer && a.Levels != b.Levels)
            {
                return b.Levels.CompareTo(a.Levels);
            }
            if (ByScore)
            {
                int ap = layer ? a.LayerPoints : a.Points, bp = layer ? b.LayerPoints : b.Points;
                if (ap != bp)
                {
                    return bp.CompareTo(ap);
                }
                int ast = layer ? a.LayerStyle : a.Style, bst = layer ? b.LayerStyle : b.Style;
                if (ast != bst)
                {
                    return bst.CompareTo(ast);
                }
            }
            float at = layer ? a.LayerSeconds : a.Seconds, bt = layer ? b.LayerSeconds : b.Seconds;
            return at.CompareTo(bt);
        }

        public static List<Run> Standings()
        {
            List<Run> list = WholeLayer ? InLayer() : InLevel();
            list.Sort((a, b) => Compare(a, b, WholeLayer));
            return list;
        }

        // ---- a race ----

        private static void RaceFrame()
        {
            List<Run> here = InLevel();
            foreach (Run r in here)
            {
                if (!r.Done || !told.Add(r.Id))
                {
                    continue;
                }
                finishOrder.Add(r.Id);
                string who = r.Id == Room.MyId ? "You" : Profiles.Colored(r.Id);
                string score = ByScore ? $" - rank {r.Rank}, {r.Style:N0} style" : "";
                PartyChat.System($"{who} finished {PartyHud.Clock(r.Seconds)}{score}.");
                Plugin.Log.LogInfo($"versus: {Profiles.NameOf(r.Id)} finished {scene} ({r.Seconds:0.000} s, rank {r.Rank}, style {r.Style})");
                if (r.Id != Room.MyId)
                {
                    VersusHud.Finished(r.Id, ByScore ? 0 : finishOrder.Count, r.Seconds);
                }
            }
            // The level's winner: by time, the first to finish; by score, the best once everyone has.
            if (!levelDecided && finishOrder.Count > 0 && here.Count > 1)
            {
                bool all = here.TrueForAll(r => r.Done);
                if (!ByScore || all)
                {
                    levelDecided = true;
                    var order = new List<Run>(here);
                    order.Sort((a, b) => Compare(a, b, false));
                    levelWinner = ByScore ? order[0].Id : finishOrder[0];
                    Announce(levelWinner, false);
                }
            }
            if (!WholeLayer || layerDecided || !mine.LayerDone)
            {
                return;
            }
            List<Run> layer = InLayer();
            if (layer.Count > 1 && layer.TrueForAll(r => r.LayerDone))
            {
                layerDecided = true;
                layer.Sort((a, b) => Compare(a, b, true));
                layerWinner = layer[0].Id;
                Announce(layerWinner, true);
            }
        }

        private static void Announce(int id, bool layer)
        {
            string what = layer ? $"the {LayerName(mine.Layer).ToLowerInvariant()} layer" : "the level";
            string who = id == Room.MyId ? "You win" : Profiles.Colored(id) + " wins";
            PartyChat.System($"{who} {what}!");
            Plugin.Log.LogInfo($"versus: {Profiles.NameOf(id)} wins {what}");
            // (A whole layer's race: its card is the layer's result, not each level's.)
            if (layer || !WholeLayer)
            {
                VersusHud.Result(id, layer);
            }
        }

        // ---- going on (EACH LEVEL: together, once everyone's finished) ----

        public static bool LevelRace => On && !Grind && !WholeLayer;

        // Who's still playing this level (not finished), besides this player.
        public static List<int> StillPlaying()
        {
            var ids = new List<int>();
            foreach (Run r in InLevel())
            {
                if (r.Id != Room.MyId && !r.Done)
                {
                    ids.Add(r.Id);
                }
            }
            return ids;
        }

        private static float pressedAt = -100f;

        // ULTRAKILL's results screen, clicked on to the next level: not yet, while others still race
        // (everyone goes on together, once the last of those still in the level has finished); and not
        // on one's own, the host here (the host takes everyone on, together).
        [HarmonyPatch(typeof(FinalRank), nameof(FinalRank.LevelChange)), HarmonyPrefix]
        private static bool GoingOn(bool force)
        {
            try
            {
                if (force || !LevelRace || !SteamNet.InLobby || !mine.Done)
                {
                    return true;
                }
                List<int> playing = StillPlaying();
                bool hostHere = false;
                foreach (Run r in InLevel())
                {
                    hostHere |= r.Id != Room.MyId && Party.HostId() == r.Id;
                }
                float now = Time.unscaledTime;
                if (now - pressedAt < 0.3f)
                {
                    return false;
                }
                pressedAt = now;
                if (Party.IAmHost)
                {
                    if (playing.Count == 0)
                    {
                        return true;
                    }
                    VersusHud.Note($"WAITING FOR {Names(playing)}");
                    return false;
                }
                if (!hostHere)
                {
                    return true;
                }
                VersusHud.Note(playing.Count > 0 ? $"WAITING FOR {Names(playing)}" : "THE HOST TAKES EVERYONE ON");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"versus: {e.Message}");
                return true;
            }
        }

        private static string Names(List<int> ids)
        {
            var names = new List<string>();
            foreach (int id in ids)
            {
                names.Add(Profiles.NameOf(id).ToUpperInvariant());
            }
            return Typing.Escape(string.Join(", ", names));
        }

        // (Party: the host going on.) Someone still racing in VERSUS stays in their level: in a race of
        // each level until they've finished it (then they follow by themselves), in the whole layer's always.
        public static bool KeepsPlaying(string here)
        {
            if (!On || Grind || here != scene || !Party.IsLevel(here))
            {
                return false;
            }
            return WholeLayer || !mine.Done;
        }

        // ---- the Cyber Grind ----

        private static void GrindFrame()
        {
            alive.Clear();
            int here = 0;
            foreach (Run run in InLevel())
            {
                int id = run.Id;
                here++;
                Profiles.Info i = id == Room.MyId ? Profiles.Mine : Profiles.Of(id);
                Score.Stats s = Score.Of(id);
                if (i.State == Profiles.StateDead)
                {
                    if (told.Add(id))
                    {
                        outs.Add((id, s.Wave, i.Seconds));
                        string who = id == Room.MyId ? "You're" : Profiles.Colored(id) + " is";
                        PartyChat.System($"{who} out, on wave {s.Wave}.");
                        Plugin.Log.LogInfo($"versus: {Profiles.NameOf(id)} is out on wave {s.Wave}");
                    }
                }
                else if (i.State == Profiles.StatePlaying)
                {
                    alive.Add(id);
                }
            }
            startedWith = Mathf.Max(startedWith, here);
            if (grindDecided || startedWith < 2)
            {
                return;
            }
            if (alive.Count == 1 && outs.Count > 0)
            {
                GrindWon(alive[0], "the last one standing");
            }
            else if (alive.Count == 0 && outs.Count > 0)
            {
                // (Everyone out at once: the furthest wave, then the longest run.)
                outs.Sort((a, b) => a.Wave != b.Wave ? b.Wave.CompareTo(a.Wave) : b.Seconds.CompareTo(a.Seconds));
                GrindWon(outs[0].Id, $"the furthest, wave {outs[0].Wave}");
            }
        }

        private static void GrindWon(int id, string how)
        {
            grindDecided = true;
            grindWinner = id;
            string who = id == Room.MyId ? "You win" : Profiles.Colored(id) + " wins";
            PartyChat.System($"{who} - {how}!");
            Plugin.Log.LogInfo($"versus: {Profiles.NameOf(id)} wins ({how})");
            VersusHud.GrindResult(id, how);
        }

        // ---- places ----

        // A player's place (1st, 2nd, ...), 0 while it's not known yet.
        public static int PlaceOf(int id)
        {
            if (!On)
            {
                return 0;
            }
            if (!Grind)
            {
                List<Run> order = Standings();
                for (int i = 0; i < order.Count; i++)
                {
                    if (order[i].Id == id)
                    {
                        bool done = WholeLayer ? order[i].LayerDone : order[i].Done;
                        return done ? i + 1 : 0;
                    }
                }
                return 0;
            }
            if (grindWinner == id)
            {
                return 1;
            }
            // (Out: behind everyone still in, the later out the better.)
            var outOrder = new List<(int Id, int Wave, float Seconds)>(outs);
            outOrder.Sort((a, b) => a.Wave != b.Wave ? b.Wave.CompareTo(a.Wave) : b.Seconds.CompareTo(a.Seconds));
            int ahead = grindWinner != 0 ? 1 : alive.Count;
            int n = 0;
            foreach (var o in outOrder)
            {
                if (o.Id == grindWinner)
                {
                    continue;
                }
                n++;
                if (o.Id == id)
                {
                    return ahead + n;
                }
            }
            return 0;
        }

        public static void Forget()
        {
            others.Clear();
        }
    }
}
