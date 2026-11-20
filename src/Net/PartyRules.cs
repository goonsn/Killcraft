using System.IO;
using UnityEngine;

namespace Killcraft
{
    // The party host's rules, for everyone in the party: the game mode (CO-OP, or VERSUS: each plays the
    // level on their own, the first to finish wins; in the Cyber Grind, the last one standing), how much
    // tougher enemies and bosses get for each other player in the level, and whether the others vote on
    // the next level. The host's game says them (at once when they change, every 3 seconds anyway);
    // everyone plays by the last heard.
    // And the party going on without its host: should they leave or drop, Steam makes someone else the
    // lobby's owner, the party's host from then on (SteamNet: its messages go through them now). They
    // carry on with the party's settings as they were (PvP, collision, these, who sees the party), not
    // their own, and everyone hears who the host is now.
    internal static class PartyRules
    {
        public const byte Message = 60;
        public const int ModeCoop = 0, ModeVersus = 1;

        private static int heardMode;
        private static float heardEnemy = 0.25f, heardBoss = 0.5f, heardAt = -100f;
        private static bool heardVote, heardScore = true, heardLayer;
        private static float sentAt = -100f;
        private static string sent = "";
        private static bool wasOwner;
        private static ulong lobbyWas;

        // This player's settings are the party's: alone, or its host (the lobby's owner).
        public static bool Mine => !SteamNet.InLobby || SteamNet.IAmOwner;

        public static int Mode => Mine ? Mathf.Clamp(Plugin.PartyMode.Value, 0, 1) : heardMode;
        public static float EnemyHealth => Mine ? Mathf.Clamp(Plugin.EnemyHealthPerPlayer.Value, 0f, 2f) : heardEnemy;
        public static float BossHealth => Mine ? Mathf.Clamp(Plugin.BossHealthPerPlayer.Value, 0f, 2f) : heardBoss;
        public static bool Vote => SteamNet.InLobby && (Mine ? Plugin.LevelVote.Value : heardVote);

        // VERSUS: won by score (ULTRAKILL's rank, then style, then time), or by time alone (the first to
        // finish); each level its own race (everyone waits for everyone), or the whole layer.
        public static bool VersusScore => Mine ? Plugin.VersusScore.Value : heardScore;
        public static bool VersusLayer => Mine ? Plugin.VersusLayer.Value : heardLayer;

        public static string ModeName(int m) => m == ModeVersus ? "VERSUS" : "CO-OP";

        public static string WinByName(bool score) => score ? "SCORE (RANK, STYLE, TIME)" : "TIME (FIRST TO FINISH)";

        public static string RaceName(bool layer) => layer ? "WHOLE LAYER (NOBODY WAITS)" : "EACH LEVEL (WAIT FOR ALL)";

        public static void SetVersusScore(bool on)
        {
            if (Allowed("how versus is won"))
            {
                Plugin.VersusScore.Value = on;
            }
        }

        public static void SetVersusLayer(bool on)
        {
            if (Allowed("the versus race"))
            {
                Plugin.VersusLayer.Value = on;
            }
        }

        // A change from the menus: only the host's counts (else a note why not).
        public static void SetMode(int m)
        {
            if (Allowed("the game mode"))
            {
                Plugin.PartyMode.Value = Mathf.Clamp(m, 0, 1);
            }
        }

        public static void SetVote(bool on)
        {
            if (Allowed("the level vote"))
            {
                Plugin.LevelVote.Value = on;
            }
        }

        public static void SetEnemyHealth(float v)
        {
            if (Allowed("enemy health"))
            {
                Plugin.EnemyHealthPerPlayer.Value = v;
            }
        }

        public static void SetBossHealth(float v)
        {
            if (Allowed("boss health"))
            {
                Plugin.BossHealthPerPlayer.Value = v;
            }
        }

        private static bool Allowed(string what)
        {
            if (Mine)
            {
                return true;
            }
            PartyChat.System($"Only the party host can change {what}.");
            return false;
        }

        public static void Frame()
        {
            if (!SteamNet.InLobby)
            {
                wasOwner = false;
                lobbyWas = 0;
                return;
            }
            ulong lobby = LobbyId();
            bool owner = SteamNet.IAmOwner;
            // (Made the host of a party already going - the last one left or dropped: as it was.)
            if (owner && !wasOwner && lobby != 0 && lobby == lobbyWas)
            {
                TakeOver();
            }
            wasOwner = owner;
            lobbyWas = lobby;
            if (!owner)
            {
                return;
            }
            string now = $"{Mode}|{EnemyHealth:0.00}|{BossHealth:0.00}|{Vote}|{VersusScore}|{VersusLayer}";
            if (now == sent && Time.unscaledTime - sentAt < 3f)
            {
                return;
            }
            sent = now;
            sentAt = Time.unscaledTime;
            int mode = Mode;
            float enemy = EnemyHealth, boss = BossHealth;
            bool vote = Vote, score = VersusScore, layer = VersusLayer;
            KillNet.Send(Message, KillNet.ToEveryone, w =>
            {
                w.Write((byte)mode);
                w.Write(enemy);
                w.Write(boss);
                w.Write(vote);
                // (Newer: versus's own two.)
                w.Write((byte)((score ? 1 : 0) | (layer ? 2 : 0)));
            });
        }

        public static void Handle(int from, BinaryReader r)
        {
            int mode = r.ReadByte();
            float enemy = r.ReadSingle(), boss = r.ReadSingle();
            bool vote = r.ReadBoolean();
            byte versus = r.BaseStream.Length - r.BaseStream.Position >= 1 ? r.ReadByte() : (byte)1;
            if (!Party.FromHost(from) || Mine)
            {
                return;
            }
            bool score = (versus & 1) != 0, layer = (versus & 2) != 0;
            if (heardAt > 0f && mode == ModeVersus && (score != heardScore || layer != heardLayer))
            {
                PartyChat.System($"The host set versus to win by {WinByName(score).ToLowerInvariant()}, {RaceName(layer).ToLowerInvariant()}.");
            }
            heardScore = score;
            heardLayer = layer;
            if (heardAt > 0f && mode != heardMode)
            {
                PartyChat.System($"The host set the game mode to {ModeName(mode)}" + (mode == ModeVersus
                    ? ": everyone plays the next level on their own, the first to finish wins (in the Cyber Grind, the last one standing)."
                    : ": ULTRAKILL's levels together."));
            }
            if (heardAt > 0f && vote != heardVote)
            {
                PartyChat.System(vote ? "The host opened the level vote: pick a level on the party screen." : "The host closed the level vote.");
            }
            heardMode = Mathf.Clamp(mode, 0, 1);
            heardEnemy = Mathf.Clamp(enemy, 0f, 2f);
            heardBoss = Mathf.Clamp(boss, 0f, 2f);
            heardVote = vote;
            heardAt = Time.unscaledTime;
        }

        private static ulong LobbyId()
        {
            try
            {
                return ServerList.Party != null ? ServerList.Party.Value.Id.Value : 0;
            }
            catch
            {
                return 0;
            }
        }

        // This player's the party's host now, in the middle of it: its settings stay as they were (theirs
        // become those), and the party hears so.
        private static void TakeOver()
        {
            if (heardAt > 0f)
            {
                Plugin.PartyMode.Value = heardMode;
                Plugin.EnemyHealthPerPlayer.Value = heardEnemy;
                Plugin.BossHealthPerPlayer.Value = heardBoss;
                Plugin.LevelVote.Value = heardVote;
                Plugin.VersusScore.Value = heardScore;
                Plugin.VersusLayer.Value = heardLayer;
            }
            Plugin.PvP.Value = Room.Pvp;
            Plugin.PvpDamage.Value = Room.PvpDamage;
            Plugin.PlayerCollision.Value = Room.Collision;
            try
            {
                if (int.TryParse(ServerList.Party.Value.GetData("listed"), out int listed))
                {
                    Plugin.ListWorld.Value = Mathf.Clamp(listed, 0, 2);
                }
                Plugin.PartyCheats.Value = ServerList.Party.Value.GetData("cheats") != "0";
            }
            catch
            {
            }
            Plugin.Log.LogInfo("party: this player is the party host now (the last one left or dropped); the party's settings carried on");
            PartyChat.System("You're the party host now. The party's settings stay as they were.");
            ChatLines.Add("You're the party host now");
        }

        // (Party: the party's host changed - left, dropped, or handed it on.)
        public static void HostChanged(string before, bool beforeStillHere, string after, bool afterIsMe)
        {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || afterIsMe)
            {
                return;
            }
            PartyChat.System(beforeStillHere
                ? $"{Typing.Escape(after)} is the party host now."
                : $"{Typing.Escape(before)} left: {Typing.Escape(after)} is the party host now.");
        }
    }
}
