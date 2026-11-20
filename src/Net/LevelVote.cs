using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Killcraft
{
    // The level vote (the party host's to open: PartyRules.Vote): on the party screen everyone picks the
    // level they'd play next (LEVEL VOTE, under CONTROL); each one's pick is shown by their name, and the
    // host starts the one with the most votes (START) - or any other, through ULTRAKILL's level select.
    // The votes go once a level starts.
    internal static class LevelVote
    {
        public const byte Message = 61;
        private static readonly Dictionary<int, string> votes = new Dictionary<int, string>();
        private static string mine = "", lastScene = "";
        private static float sentAt = -100f;
        private static bool wasOn;

        public static string Mine => mine;

        public static void Frame()
        {
            bool on = PartyRules.Vote;
            string scene = SceneHelper.CurrentScene ?? "";
            // (A level started, the vote closed, out of the party: the votes go.)
            if ((scene != lastScene && Party.IsLevel(scene)) || (!on && wasOn) || !SteamNet.InLobby)
            {
                if (votes.Count > 0 || mine.Length > 0)
                {
                    votes.Clear();
                    mine = "";
                }
            }
            lastScene = scene;
            wasOn = on;
            if (!on)
            {
                return;
            }
            // (Said again now and then: for whoever joined since.)
            if (mine.Length > 0 && Time.unscaledTime - sentAt > 5f)
            {
                Send();
            }
        }

        // This player's pick (the same again: taken back).
        public static void Cast(string scene)
        {
            if (!PartyRules.Vote)
            {
                return;
            }
            mine = mine == scene ? "" : scene;
            Plugin.Log.LogInfo(mine.Length > 0 ? $"vote: {mine}" : "vote: taken back");
            Send();
        }

        private static void Send()
        {
            sentAt = Time.unscaledTime;
            string v = mine;
            KillNet.Send(Message, KillNet.ToEveryone, w => w.Write(v));
        }

        public static void Handle(int from, BinaryReader r)
        {
            string scene = r.ReadString();
            if (scene.Length > 64)
            {
                return;
            }
            string had = votes.TryGetValue(from, out string h) ? h : "";
            if (scene.Length == 0 || !Party.IsLevel(scene))
            {
                votes.Remove(from);
                return;
            }
            votes[from] = scene;
            if (had != scene && PartyRules.Vote)
            {
                PartyChat.System($"{Profiles.Colored(from)} votes for {Typing.Escape(Party.LevelName(scene))}.");
            }
        }

        // A player's pick, "" if none.
        public static string Of(int id)
        {
            if (id == Room.MyId)
            {
                return mine;
            }
            return votes.TryGetValue(id, out string s) && Still(id) ? s : "";
        }

        private static bool Still(int id) => Party.Members.Exists(m => m.Id == id);

        // How many picked each level (this player's pick counted).
        public static Dictionary<string, int> Tally()
        {
            var t = new Dictionary<string, int>();
            if (mine.Length > 0)
            {
                t[mine] = 1;
            }
            foreach (var kv in votes)
            {
                if (kv.Key != Room.MyId && Still(kv.Key))
                {
                    t[kv.Value] = t.TryGetValue(kv.Value, out int n) ? n + 1 : 1;
                }
            }
            return t;
        }

        // The level with the most votes (a tie: the earlier in ULTRAKILL's order), "" if none.
        public static string Winner(out int count)
        {
            Dictionary<string, int> t = Tally();
            string best = "";
            count = 0;
            foreach (var (scene, _) in Party.Levels())
            {
                if (t.TryGetValue(scene, out int n) && n > count)
                {
                    best = scene;
                    count = n;
                }
            }
            return best;
        }

        // The squad's line for a player: what they voted for.
        public static string RowTag(int id)
        {
            if (!PartyRules.Vote)
            {
                return "";
            }
            string v = Of(id);
            return v.Length == 0 ? "" : $" · <color=#FFD02E>VOTED {Typing.Escape(Short(v))}</color>";
        }

        // "4-3" for "4-3: A SHOT IN THE DARK"; the Sandbox and the Cyber Grind by their names.
        public static string Short(string scene)
        {
            string name = Party.LevelName(scene).ToUpperInvariant();
            int colon = name.IndexOf(':');
            return colon > 0 && colon <= 6 ? name.Substring(0, colon) : name;
        }

        // The host: the winner, for everyone (the party's WAKE, from the main menu).
        public static void StartWinner()
        {
            string scene = Winner(out int n);
            if (scene.Length == 0 || !Party.IAmHost)
            {
                return;
            }
            Plugin.Log.LogInfo($"vote: starting {scene} ({n} votes)");
            PartyChat.System($"Starting {Typing.Escape(Party.LevelName(scene))} ({n} vote{(n == 1 ? "" : "s")}).");
            PartyWake.PickingForParty();
            try
            {
                SceneHelper.LoadScene(scene);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"vote: couldn't start {scene} ({e.Message})");
            }
        }
    }
}
