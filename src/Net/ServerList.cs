using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Killcraft
{
    // The party is a Steam lobby (ULTRAKILL's own Steam, as UltraLink's games are): its host makes it,
    // the others join it (from the server list, a code, a Steam invite) and stay in it; KillNet's
    // messages go between its members (SteamNet). It also holds what the server list shows (who, which
    // level, how many, PvP), whether its host plays with Minecraft ("mc"), and their Minecraft world's
    // e4mc link, which the others' Minecraft joins (as /join) if they play with Minecraft too. A party
    // whose host plays without Minecraft is played without it by everyone in it.
    // Listed for everyone (ListWorld 2), the host's Steam friends (1), or nobody (0: invites, codes).
    internal static class ServerList
    {
        private const string Key = "killcraft", Version = "3";

        public sealed class Entry
        {
            public ulong Lobby;
            public string Name = "", Level = "";
            public int Players, Max = 10;
            public string Title = "";
            public bool Cheats;
            public bool Pvp, Friend, Minecraft;
        }

        public static readonly List<Entry> Found = new List<Entry>();
        public static bool Searching { get; private set; }
        public static string Status { get; private set; } = "";

        // The party this player is in (its Steam lobby), theirs or someone else's.
        private static Lobby? party;
        private static Task<Lobby?> making;
        private static int madeAs = -1;
        private static float updatedAt = -100f, failedAt = -100f;
        private static string shown = "";

        private static Task<Lobby[]> search;
        private static readonly List<Lobby> friendLobbies = new List<Lobby>();
        private static float friendsAskedAt;

        public static Lobby? Party => party;

        // The party made here: its name (HOST PARTY's LOBBY NAME), how many it takes.
        public static string Title => string.IsNullOrWhiteSpace(Plugin.PartyTitle.Value) ? SafeName() + "'s Lobby" : Plugin.PartyTitle.Value.Trim();
        public static int MaxPlayers => Mathf.Clamp(Plugin.PartyMax.Value, 2, 10);

        // The party's own: its name, its size, whether cheats can be on in it (made before these: as then).
        public static string PartyTitle
        {
            get
            {
                string t = party == null ? "" : party.Value.GetData("title");
                return string.IsNullOrEmpty(t) ? "" : Clip(t, 32);
            }
        }
        public static int PartyMax
        {
            get
            {
                return party != null && int.TryParse(party.Value.GetData("max"), out int m) && m >= 2 ? m : 10;
            }
        }
        public static bool CheatsBlocked => party != null && party.Value.GetData("cheats") == "0" && SceneHelper.CurrentScene != "uk_construct";

        // The last party joined (for REJOIN): its lobby, its name.
        public static bool LastParty(out ulong id, out string name)
        {
            id = 0;
            name = "";
            string v = Plugin.LastParty.Value ?? "";
            int bar = v.IndexOf('|');
            if (bar <= 0 || !ulong.TryParse(v.Substring(0, bar), out id))
            {
                return false;
            }
            name = v.Substring(bar + 1);
            return true;
        }
        public static bool InParty => party != null;
        public static bool Making => making != null;
        public static bool JoinPending => joinTask != null && !joinTask.IsCompleted;

        // Why the last join didn't work ("" if it did, or none yet), when; and whether it was because the
        // party's gone (Steam deletes a lobby once the last one in it leaves).
        public static string JoinProblem { get; private set; } = "";
        public static float JoinProblemAt { get; private set; } = -100f;
        public static bool LastJoinGone { get; private set; }

        private static void Failed(string problem, bool gone, ulong id)
        {
            JoinProblem = problem;
            JoinProblemAt = Time.unscaledTime;
            LastJoinGone = gone;
            // (Gone for good: REJOIN stops offering it.)
            if (gone && LastParty(out ulong last, out string _) && last == id)
            {
                Plugin.LastParty.Value = "";
            }
        }

        public static bool PartyOwner
        {
            get
            {
                if (party == null)
                {
                    return false;
                }
                try
                {
                    return party.Value.Owner.Id.Value == SteamClient.SteamId.Value;
                }
                catch
                {
                    return false;
                }
            }
        }

        // Minecraft's part of Killcraft allowed here: not in a party made by someone playing without it.
        public static bool MinecraftAllowed
        {
            get
            {
                // (On one PC, second-player.bat: no lobby; the host's game says, with the rooms.)
                if (LocalLink.Wanted)
                {
                    return !SteamNet.InLobby || SteamNet.IAmOwner || Room.HostMinecraft;
                }
                if (party == null)
                {
                    return true;
                }
                try
                {
                    return party.Value.GetData("mc") != "0";
                }
                catch
                {
                    return true;
                }
            }
        }

        public static bool SteamUp
        {
            get
            {
                try
                {
                    return SteamClient.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        // Each frame.
        public static void Frame()
        {
            try
            {
                Listen();
                Made();
                Publish();
                Collect();
                CodeFound();
                Joining();
                Rejoin.Frame();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - failedAt > 30f)
                {
                    failedAt = Time.unscaledTime;
                    Plugin.Log.LogWarning($"server list: Steam failed ({e.Message})");
                }
            }
        }

        // ---- making a party, joining one, leaving ----

        public static void CreateParty()
        {
            if (!SteamUp)
            {
                PartyChat.System("Steam isn't running: no party.");
                return;
            }
            if (party != null || making != null)
            {
                return;
            }
            Plugin.Log.LogInfo("server list: making a party (a Steam lobby)");
            making = SteamMatchmaking.CreateLobbyAsync(MaxPlayers);
            // (Playing with Minecraft: its world opened to friends too, for the others' Minecraft.)
            if (Plugin.WithMinecraft)
            {
                Killcraft.Party.OpenMinecraftWorld();
            }
        }

        private static void Made()
        {
            if (making == null || !making.IsCompleted)
            {
                return;
            }
            Lobby? made = making.Status == TaskStatus.RanToCompletion ? making.Result : null;
            making = null;
            if (made == null)
            {
                failedAt = Time.unscaledTime;
                Plugin.Log.LogWarning("server list: Steam wouldn't make a lobby for the party");
                PartyChat.System("Steam wouldn't make the party; try again in a moment.");
                return;
            }
            Lobby l = made.Value;
            int listAs = Mathf.Clamp(Plugin.ListWorld.Value, 0, 2);
            ApplyPrivacy(l, listAs);
            l.SetJoinable(true);
            l.SetData(Key, Version);
            l.SetData("mc", Plugin.WithMinecraft ? "1" : "0");
            if (string.IsNullOrEmpty(code))
            {
                code = NewCode();
            }
            l.SetData("code", code);
            l.SetData("title", Clip(Title, 32));
            l.SetData("max", MaxPlayers.ToString());
            l.SetData("cheats", Plugin.PartyCheats.Value ? "1" : "0");
            Entered(l, Title);
            if (announced != code)
            {
                announced = code;
                PartyChat.System($"Party open ({(listAs == 2 ? "PUBLIC" : listAs == 1 ? "FRIENDS" : "PRIVATE")}) - code {code}");
                PartyChat.Event($"PARTY CODE {code}");
            }
            Plugin.Log.LogInfo($"server list: party made ({(listAs == 2 ? "listed for everyone" : listAs == 1 ? "listed for Steam friends" : "for invites and its code only")}, {(Plugin.WithMinecraft ? "with" : "without")} Minecraft)");
            SendInvites();
            if (overlayWanted)
            {
                overlayWanted = false;
                InviteOverlay();
            }
        }

        private static void ApplyPrivacy(Lobby l, int listAs)
        {
            // (Found by Steam's search either way, for its code; the server list shows a friends' party
            // to friends only, a private one to nobody.)
            if (listAs == 0)
            {
                l.SetInvisible();
            }
            else
            {
                l.SetPublic();
            }
            l.SetData("listed", listAs.ToString());
            madeAs = listAs;
        }

        private static void Entered(Lobby l, string fallbackName)
        {
            party = l;
            shown = "";
            updatedAt = -100f;
            SteamNet.PartyChanged();
            // (For REJOIN, and for Rejoin: back in it after a dropped connection or a crash.)
            string title = l.GetData("title");
            title = string.IsNullOrEmpty(title) ? fallbackName : Clip(title, 32);
            Plugin.LastParty.Value = l.Id.Value + "|" + title.Replace("|", " ");
            Plugin.PartyOpen.Value = true;
            // (Steam's "Join game" on this player in the friends list: the others' Killcraft joins.)
            try
            {
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + l.Id.Value);
            }
            catch
            {
            }
        }

        // (rejoining: left only to come straight back (Rejoin), so still "in" it should ULTRAKILL crash.)
        public static void LeaveParty(bool rejoining = false)
        {
            if (!rejoining)
            {
                Plugin.PartyOpen.Value = false;
                Rejoin.Cancel();
            }
            if (party == null)
            {
                return;
            }
            bool wasMine = PartyOwner;
            try
            {
                party.Value.Leave();
            }
            catch
            {
            }
            party = null;
            madeAs = -1;
            pendingLink = "";
            SteamNet.PartyChanged();
            try
            {
                SteamFriends.SetRichPresence("connect", null);
            }
            catch
            {
            }
            Plugin.Log.LogInfo("server list: left the party");
            // (In a friend's Minecraft world for it: back to this player's own.)
            if (!wasMine && Plugin.WithMinecraft && Killcraft.Party.McWorldKnown && !Killcraft.Party.McOwnWorld)
            {
                Killcraft.Party.LeaveMinecraftWorld();
            }
        }

        private static void Publish()
        {
            if (party == null || !PartyOwner)
            {
                return;
            }
            Lobby lobby = party.Value;
            int listAs = Mathf.Clamp(Plugin.ListWorld.Value, 0, 2);
            if (listAs != madeAs)
            {
                ApplyPrivacy(lobby, listAs);
                Plugin.Log.LogInfo($"server list: the party is {(listAs == 2 ? "public" : listAs == 1 ? "for friends" : "private")} now");
            }
            if (Time.unscaledTime - updatedAt < 2f)
            {
                return;
            }
            updatedAt = Time.unscaledTime;
            string name = SafeName();
            string level = Killcraft.Party.LevelName(SceneHelper.CurrentScene);
            int players = Mathf.Max(lobby.MemberCount, 1);
            string pvp = Room.Pvp || Plugin.PvP.Value ? "1" : "0";
            // (The Minecraft world, once open: for the others' Minecraft.)
            string link = Plugin.WithMinecraft && Killcraft.Party.McOwnWorld && Killcraft.Party.McOpen ? Killcraft.Party.McLink : "";
            string mc = Plugin.WithMinecraft ? "1" : "0";
            string now = name + "|" + level + "|" + players + "|" + pvp + "|" + link + "|" + mc;
            if (now == shown)
            {
                return;
            }
            shown = now;
            lobby.SetData("name", name);
            lobby.SetData("level", level);
            lobby.SetData("players", players.ToString());
            lobby.SetData("pvp", pvp);
            lobby.SetData("link", Killcraft.Party.IsWorldLink(link) ? link : "");
            lobby.SetData("mc", mc);
        }

        // ---- party codes ----

        private static string code = "", announced = "";
        private static Task<Lobby[]> codeSearch;
        private static string codeSought = "";
        private const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        // This player's party's code (theirs or the one they joined), else "".
        public static string Code
        {
            get
            {
                if (party == null)
                {
                    return "";
                }
                try
                {
                    return party.Value.GetData("code") ?? "";
                }
                catch
                {
                    return "";
                }
            }
        }

        public static bool CodeSearching => codeSearch != null;

        private static string NewCode()
        {
            var rng = new System.Random();
            var c = new char[8];
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = CodeLetters[rng.Next(CodeLetters.Length)];
            }
            return new string(c);
        }

        public static string CleanCode(string typed)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (typed ?? "").ToUpperInvariant())
            {
                if (CodeLetters.IndexOf(ch) >= 0)
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        // A party (private ones too) by its code.
        public static void JoinByCode(string typed)
        {
            string c = CleanCode(typed);
            if (c.Length < 4)
            {
                PartyChat.System("That code's too short.");
                return;
            }
            if (!SteamUp)
            {
                PartyChat.System("Steam isn't running: no joining by code.");
                return;
            }
            if (codeSearch != null)
            {
                return;
            }
            codeSought = c;
            PartyChat.System($"Looking for party {c}...");
            Plugin.Log.LogInfo($"server list: looking for party code {c}");
            codeSearch = SteamMatchmaking.LobbyList.WithKeyValue(Key, Version).WithKeyValue("code", c).FilterDistanceWorldwide().WithMaxResults(5).RequestAsync();
        }

        private static void CodeFound()
        {
            if (codeSearch == null || !codeSearch.IsCompleted)
            {
                return;
            }
            Lobby[] found = codeSearch.Status == TaskStatus.RanToCompletion ? codeSearch.Result : null;
            codeSearch = null;
            if (found != null)
            {
                foreach (Lobby l in found)
                {
                    if (l.GetData("code") != codeSought)
                    {
                        continue;
                    }
                    if (party != null && party.Value.Id.Value == l.Id.Value)
                    {
                        PartyChat.System("You're already in that party.");
                        return;
                    }
                    string who = Clip(l.GetData("name"), 32);
                    Plugin.Log.LogInfo($"server list: party {codeSought} is {who}'s");
                    JoinParty(l.Id.Value, string.IsNullOrEmpty(who) ? "the party" : who);
                    return;
                }
            }
            Plugin.Log.LogInfo($"server list: no party with code {codeSought}");
            PartyChat.System($"No open party has the code {codeSought}.");
        }

        private static string SafeName()
        {
            try
            {
                return SteamClient.Name;
            }
            catch
            {
                return Killcraft.Party.MyName;
            }
        }

        // ---- looking ----

        // Ask Steam again: everyone's listed parties, and the Steam friends' (friends-only ones are only
        // found through the friends playing ULTRAKILL).
        public static void Refresh()
        {
            if (!SteamUp)
            {
                Status = "Steam isn't running: no server list.";
                return;
            }
            if (search != null)
            {
                return;
            }
            Searching = true;
            Status = "Looking...";
            search = SteamMatchmaking.LobbyList.WithKeyValue(Key, Version).FilterDistanceWorldwide().WithMaxResults(100).RequestAsync();
            friendLobbies.Clear();
            foreach (Friend f in SteamFriends.GetFriends())
            {
                if (!f.IsPlayingThisGame)
                {
                    continue;
                }
                Friend.FriendGameInfo? game = f.GameInfo;
                Lobby? lobby = game.HasValue ? game.Value.Lobby : null;
                if (lobby != null)
                {
                    lobby.Value.Refresh();
                    friendLobbies.Add(lobby.Value);
                }
            }
            friendsAskedAt = Time.unscaledTime;
        }

        private static void Collect()
        {
            if (search == null || !search.IsCompleted)
            {
                return;
            }
            // (The friends' lobbies' data comes a moment after asking.)
            if (Time.unscaledTime - friendsAskedAt < 1f)
            {
                return;
            }
            Lobby[] lobbies = search.Status == TaskStatus.RanToCompletion ? search.Result : null;
            search = null;
            Searching = false;
            Found.Clear();
            var seen = new HashSet<ulong>();
            if (lobbies != null)
            {
                foreach (Lobby l in lobbies)
                {
                    Add(l, false, seen);
                }
            }
            foreach (Lobby l in friendLobbies)
            {
                Add(l, true, seen);
            }
            Found.Sort((a, b) => a.Friend != b.Friend ? (a.Friend ? -1 : 1) : b.Players.CompareTo(a.Players));
            Status = Found.Count == 0 ? "No open parties right now." : $"{Found.Count} open part{(Found.Count == 1 ? "y" : "ies")}.";
            Plugin.Log.LogInfo($"server list: {Found.Count} open parties ({(lobbies != null ? lobbies.Length : 0)} listed for everyone, {friendLobbies.Count} friends' lobbies)");
        }

        private static void Add(Lobby l, bool friend, HashSet<ulong> seen)
        {
            string listed = l.GetData("listed");
            ulong id = l.Id.Value;
            string skip = l.GetData(Key) != Version ? $"Killcraft version {l.GetData(Key)}"
                : listed == "0" ? "private"
                : party != null && party.Value.Id.Value == id ? "this player's own"
                : null;
            if (skip != null || !seen.Add(id))
            {
                if (skip != null)
                {
                    Plugin.Log.LogInfo($"server list: {Clip(l.GetData("name"), 32)}'s party not shown ({skip})");
                }
                return;
            }
            int.TryParse(l.GetData("players"), out int players);
            bool isFriend = friend;
            try
            {
                isFriend |= l.Owner.IsFriend;
            }
            catch
            {
            }
            // (A friends' party: for the host's friends.)
            if (listed == "1" && !isFriend)
            {
                Plugin.Log.LogInfo($"server list: {Clip(l.GetData("name"), 32)}'s party not shown (for their Steam friends only)");
                return;
            }
            Found.Add(new Entry
            {
                Lobby = id,
                Name = Clip(l.GetData("name"), 32),
                Level = Clip(l.GetData("level"), 40),
                Players = players,
                Pvp = l.GetData("pvp") == "1",
                Friend = isFriend,
                Minecraft = l.GetData("mc") != "0",
                Title = string.IsNullOrEmpty(l.GetData("title")) ? "" : Clip(l.GetData("title"), 32),
                Max = int.TryParse(l.GetData("max"), out int max) && max >= 2 ? max : 10,
                Cheats = l.GetData("cheats") != "0",
            });
        }

        // ---- Steam invites ----

        private static readonly List<SteamId> toInvite = new List<SteamId>();
        private static bool listening;
        private static Task joinTask;
        private static string pendingLink = "", pendingFrom = "";
        private static float pendingSince = -100f;

        // Steam friends online now (those playing ULTRAKILL first), for the party menu's invites.
        public static List<(ulong Id, string Name, bool InGame)> Friends()
        {
            var list = new List<(ulong Id, string Name, bool InGame)>();
            if (!SteamUp)
            {
                return list;
            }
            try
            {
                foreach (Friend f in SteamFriends.GetFriends())
                {
                    if (f.IsOnline || f.IsPlayingThisGame)
                    {
                        list.Add((f.Id.Value, f.Name ?? "?", f.IsPlayingThisGame));
                    }
                }
            }
            catch
            {
            }
            list.Sort((a, b) => a.InGame != b.InGame ? (a.InGame ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static bool CanInvite => party != null;

        // Invite a Steam friend into this party (made first, if there isn't one yet).
        public static void Invite(ulong friend)
        {
            if (!SteamUp)
            {
                return;
            }
            toInvite.Add(new SteamId { Value = friend });
            if (party != null)
            {
                SendInvites();
            }
            else
            {
                Plugin.Log.LogInfo("server list: making a party for a Steam invite");
                ChatLines.Add("Making your party for the invite...");
                CreateParty();
            }
        }

        private static bool overlayWanted;

        // Steam's own invite window (the overlay); the party made first if there isn't one yet.
        public static void InviteOverlay()
        {
            if (!SteamUp)
            {
                PartyChat.System("Steam isn't running: no invites.");
                return;
            }
            if (party != null)
            {
                SteamFriends.OpenGameInviteOverlay(party.Value.Id);
                Plugin.Log.LogInfo("server list: Steam's invite window opened");
                return;
            }
            overlayWanted = true;
            Plugin.Log.LogInfo("server list: making a party to invite friends");
            PartyChat.System("Making your party: Steam's invite window comes up in a moment...");
            CreateParty();
        }

        private static void SendInvites()
        {
            if (party == null)
            {
                return;
            }
            foreach (SteamId f in toInvite)
            {
                bool sent = party.Value.InviteFriend(f);
                string name = new Friend(f).Name;
                Plugin.Log.LogInfo($"server list: Steam invite to {name} {(sent ? "sent" : "not sent")}");
                ChatLines.Add(sent ? $"Steam invite sent to {name}" : $"Steam wouldn't send the invite to {name}");
            }
            toInvite.Clear();
        }

        // A friend's invite accepted (or "Join game" in Steam's friends list), here or as ULTRAKILL
        // started: their party joined.
        private static void Listen()
        {
            if (listening || !SteamUp)
            {
                return;
            }
            listening = true;
            SteamFriends.OnGameLobbyJoinRequested += (lobby, friend) => JoinParty(lobby.Id.Value, new Friend(friend).Name);
            SteamFriends.OnGameRichPresenceJoinRequested += (friend, connect) =>
            {
                if (TryLobbyId(connect, out ulong id))
                {
                    JoinParty(id, friend.Name);
                }
            };
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                {
                    Plugin.Log.LogInfo("server list: ULTRAKILL started from a Steam invite");
                    JoinParty(id, "a friend");
                }
            }
        }

        private static bool TryLobbyId(string connect, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(connect))
            {
                return false;
            }
            string[] parts = connect.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "+connect_lobby")
                {
                    return ulong.TryParse(parts[i + 1], out id);
                }
            }
            return false;
        }

        // (quiet: Rejoin's tries, which say themselves how they went.)
        public static void JoinParty(ulong id, string from, bool quiet = false)
        {
            if (joinTask != null && !joinTask.IsCompleted)
            {
                return;
            }
            if (party != null && party.Value.Id.Value == id)
            {
                return;
            }
            Plugin.Log.LogInfo($"server list: joining {from}'s party (Steam lobby {id})");
            JoinProblem = "";
            LastJoinGone = false;
            if (!quiet)
            {
                ChatLines.Add($"Joining {from}'s party...");
            }
            joinTask = JoinAsync(id, from, quiet);
        }

        private static async Task JoinAsync(ulong id, string from, bool quiet)
        {
            var lobby = new Lobby(id);
            RoomEnter entered = await lobby.Join();
            if (entered != RoomEnter.Success)
            {
                Plugin.Log.LogWarning($"server list: couldn't get into {from}'s party ({entered})");
                Failed(entered == RoomEnter.DoesntExist ? "THAT PARTY IS GONE: NOBODY'S IN IT ANY MORE"
                    : entered == RoomEnter.Full ? "THAT PARTY IS FULL"
                    : entered == RoomEnter.Banned ? "YOU CAN'T JOIN THAT PARTY"
                    : $"COULDN'T JOIN ({entered.ToString().ToUpperInvariant()})", entered == RoomEnter.DoesntExist, id);
                if (!quiet)
                {
                    ChatLines.Add($"Couldn't join {from}'s party ({(entered == RoomEnter.Full ? "it's full" : "the invite's gone?")})");
                }
                return;
            }
            if (lobby.GetData(Key) != Version)
            {
                lobby.Leave();
                Plugin.Log.LogWarning($"server list: {from}'s party is from another Killcraft/KillNet version ({lobby.GetData(Key)})");
                Failed("THAT PARTY RUNS ANOTHER VERSION", false, id);
                ChatLines.Add($"{from}'s party runs another version of Killcraft/KillNet");
                return;
            }
            // (One party at a time.)
            if (party != null)
            {
                LeaveParty();
            }
            Entered(lobby, from + "'s Lobby");
            bool mc = lobby.GetData("mc") != "0";
            ChatLines.Add($"In {from}'s party" + (!mc && Plugin.WithMinecraft ? " (played without Minecraft)" : ""));
            Plugin.Log.LogInfo($"server list: in {from}'s party ({(mc ? "with" : "without")} Minecraft)");
            // (Playing with Minecraft, in a party played with it: Minecraft goes to the host's world.)
            string link = lobby.GetData("link");
            if (mc && Plugin.WithMinecraft && Killcraft.Party.IsWorldLink(link))
            {
                pendingLink = link;
                pendingFrom = from;
                pendingSince = Time.unscaledTime;
            }
        }

        // (Once this player's Minecraft is in a world, it goes to the party host's.)
        private static void Joining()
        {
            // (The host's world opened after this player joined: its link comes later.)
            if (string.IsNullOrEmpty(pendingLink) && party != null && !PartyOwner && Plugin.WithMinecraft && MinecraftAllowed
                && Killcraft.Party.McWorldKnown && Killcraft.Party.McOwnWorld && Time.unscaledTime - pendingSince > 10f)
            {
                string link = party.Value.GetData("link");
                if (Killcraft.Party.IsWorldLink(link))
                {
                    pendingLink = link;
                    pendingFrom = "the host";
                    pendingSince = Time.unscaledTime;
                }
            }
            if (string.IsNullOrEmpty(pendingLink))
            {
                return;
            }
            if (Time.unscaledTime - pendingSince > 180f)
            {
                Plugin.Log.LogWarning("server list: Minecraft wasn't ready to join the party's world in 3 minutes; given up");
                pendingLink = "";
                return;
            }
            if (Killcraft.Party.McWorldKnown && Killcraft.Party.McInWorld)
            {
                if (Killcraft.Party.McLink != pendingLink)
                {
                    Killcraft.Party.JoinMinecraftWorld(pendingLink);
                }
                ChatLines.Add($"Minecraft going to {pendingFrom}'s world");
                pendingLink = "";
            }
        }

        private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "?" : s.Length > n ? s.Substring(0, n) : s;
    }
}
