using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // The party: everyone in its Steam lobby (ServerList; its owner's ULTRAKILL says, KillNet's 101),
    // whichever level each is in, and who's its host (the lobby's owner). The party host picks levels for
    // everyone, kicks players out and hands the party on; their ULTRAKILL runs the enemies of the levels
    // they're in.
    // And which world this player's Minecraft is in (its own, open to friends or not, or a friend's),
    // from their Minecraft (local 203): a party played with Minecraft has its members' Minecraft in the
    // host's world.
    [HarmonyPatch]
    internal static class Party
    {
        // PLAY ALONE (the party menu): the next level this player starts isn't the party's.
        public static bool PlayAlone;
        private static bool quietLoad;

        public const byte PartyMessage = 101, GoingMessage = 102, WorldMessage = 203;
        private const byte Kick = 4, Lead = 5, Go = 6, Action = 204;
        private const byte ActionHost = 1, ActionJoin = 2, ActionLeave = 3;

        public sealed class Member
        {
            public int Id;
            public Guid Uuid;
            public string Name = "";
            public string Scene = "";
            public bool WorldHost, PartyHost, Nether;
        }

        public static readonly List<Member> Members = new List<Member>();
        private static float heardAt = -100f, worldAt = -100f;

        // This player's Minecraft: in a world; its own (else a friend's); open to friends; the world's
        // e4mc link (once open: this player's own; or the friend's address); their Minecraft name.
        public static bool McInWorld { get; private set; }
        public static bool McOwnWorld { get; private set; }
        public static bool McOpen { get; private set; }
        public static string McLink { get; private set; } = "";
        private static string mcName = "";
        // This player's Minecraft UUID (0, 0: none known), for the party (Minecraft hides the others' bodies by it).
        public static long McUuidMsb, McUuidLsb;
        public static bool McWorldKnown => Time.unscaledTime - worldAt < 4f;

        // The party, as the menus show it: Steam's up (a party can be made); this player's own party (or
        // none yet: theirs to make); in one.
        public static bool WorldKnown => ServerList.SteamUp;
        public static bool OwnWorld => !SteamNet.InLobby || SteamNet.IAmOwner;
        public static bool Open => SteamNet.InLobby;

        public static string MyName
        {
            get
            {
                try
                {
                    return Steamworks.SteamClient.Name;
                }
                catch
                {
                    return mcName;
                }
            }
        }

        public static bool Heard => SteamNet.InLobby && Time.unscaledTime - heardAt < 6f;

        public static Member Me
        {
            get
            {
                foreach (Member m in Members)
                {
                    if (m.Id == Room.MyId && Room.MyId != 0)
                    {
                        return m;
                    }
                }
                return null;
            }
        }

        public static bool IAmHost
        {
            get
            {
                Member me = Me;
                return me != null ? me.PartyHost : !SteamNet.InLobby || SteamNet.IAmOwner || !Heard || Members.Count <= 1;
            }
        }

        public static bool IAmWorldHost
        {
            get
            {
                Member me = Me;
                return me != null ? me.WorldHost : OwnWorld;  // (the party's owner)
            }
        }

        public static void HandleParty(BinaryReader r)
        {
            int n = r.ReadInt32();
            var now = new List<Member>();
            for (int i = 0; i < n && i < 200; i++)
            {
                var m = new Member { Id = r.ReadInt32() };
                long msb = r.ReadInt64(), lsb = r.ReadInt64();
                m.Uuid = ToGuid(msb, lsb);
                m.Name = r.ReadString();
                m.Scene = r.ReadString();
                byte flags = r.ReadByte();
                m.WorldHost = (flags & 1) != 0;
                m.PartyHost = (flags & 2) != 0;
                m.Nether = (flags & 4) != 0;
                now.Add(m);
            }
            string before = HostName();
            Members.Clear();
            Members.AddRange(now);
            heardAt = Time.unscaledTime;
            string after = HostName();
            if (before != after && after != "")
            {
                Plugin.Log.LogInfo($"party: {after} is the party host ({Members.Count} in the world)");
            }
        }

        private static string HostName()
        {
            foreach (Member m in Members)
            {
                if (m.PartyHost)
                {
                    return m.Name;
                }
            }
            return "";
        }

        public static void HandleWorld(BinaryReader r)
        {
            byte flags = r.ReadByte();
            McInWorld = (flags & 1) != 0;
            McOwnWorld = (flags & 2) != 0;
            McOpen = (flags & 4) != 0;
            McLink = r.ReadString();
            mcName = r.ReadString();
            // (Newer Killcraft mods: this player's Minecraft UUID.)
            if (r.BaseStream.Length - r.BaseStream.Position >= 16)
            {
                McUuidMsb = r.ReadInt64();
                McUuidLsb = r.ReadInt64();
            }
            worldAt = Time.unscaledTime;
        }

        // The party host took everyone to a level.
        public static void HandleGoing(int from, BinaryReader r)
        {
            string scene = r.ReadString();
            if (!FromHost(from))
            {
                return;
            }
            string who = NameOf(from);
            if (!Plugin.FollowHost.Value)
            {
                Plugin.Log.LogInfo($"party: {who} took everyone to {scene}; staying here (FollowPartyHost off)");
                ChatLines.Add($"{who} took the party to {LevelName(scene)} (you're not following: the party screen)");
                return;
            }
            if (!IsLevel(scene) || scene == SceneHelper.CurrentScene || PartyWake.JustLoaded(scene))
            {
                return;
            }
            // (Only from the menu, or from the level they were in with the host: someone playing a
            // level of their own isn't pulled out of it.)
            string here = SceneHelper.CurrentScene ?? "";
            Profiles.Info host = Profiles.Of(from);
            if (IsLevel(here) && host.Scene != here)
            {
                Plugin.Log.LogInfo($"party: {who} started {scene}; staying in {here} (playing alone)");
                ChatLines.Add($"{who} started {LevelName(scene)} (you're in a level of your own: SPECTATE them from the party menu)");
                return;
            }
            Plugin.Log.LogInfo($"party: {who} took everyone to {scene}: going");
            ChatLines.Add($"{who} took the party to {LevelName(scene)}");
            Load(scene);
        }

        // From the party's host (or nobody's known as it yet: just joined).
        public static bool FromHost(int id)
        {
            bool known = false;
            foreach (Member m in Members)
            {
                if (m.PartyHost)
                {
                    known = true;
                    if (m.Id == id)
                    {
                        return true;
                    }
                }
            }
            return !known;
        }

        public static string NameOf(int id)
        {
            foreach (Member m in Members)
            {
                if (m.Id == id)
                {
                    return m.Name;
                }
            }
            return $"player {id}";
        }

        // ---- what the party host does ----

        public static void KickOut(Member m)
        {
            Plugin.Log.LogInfo($"party: kicking {m.Name} ({m.Id})");
            KillNet.Send(Kick, KillNet.ToServer, w => w.Write(m.Id));
        }

        public static void MakeHost(Member m)
        {
            Plugin.Log.LogInfo($"party: making {m.Name} ({m.Id}) the party host");
            KillNet.Send(Lead, KillNet.ToServer, w => w.Write(m.Id));
        }

        // Everyone to a level (this player too).
        public static void TakeEveryone(string scene)
        {
            if (!IsLevel(scene))
            {
                return;
            }
            Plugin.Log.LogInfo($"party: taking everyone to {scene}");
            KillNet.Send(Go, KillNet.ToServer, w => w.Write(scene));
            if (scene != SceneHelper.CurrentScene)
            {
                Load(scene);
            }
        }

        private static void Load(string scene) => LoadQuiet(scene);

        // (The end of a wake: the party's going said the old way too, for players on a Killcraft from
        // before wakes; those who had the wake are already on their way and pay it no mind.)
        public static void SendGo(string scene) => KillNet.Send(Go, KillNet.ToServer, w => w.Write(scene));

        // A level loaded for this player only (following, spectating): not a trip for the party.
        public static void LoadQuiet(string scene)
        {
            quietLoad = true;
            try
            {
                SceneHelper.LoadScene(scene);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party: couldn't load {scene} ({e.Message})");
            }
            finally
            {
                quietLoad = false;
            }
        }

        // The party host starting a level (ULTRAKILL's level select, its next level): everyone comes
        // (those in the menu, and those in the level the host was in). Not with PLAY ALONE.
        // From the main menu with others in the party: a WAKE first (PartyWake), everyone loading together.
        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadScene)), HarmonyPrefix]
        private static bool Starting(string sceneName)
        {
            try
            {
                if (quietLoad)
                {
                    return true;
                }
                bool alone = PlayAlone;
                if (IsLevel(sceneName))
                {
                    PlayAlone = false;
                }
                // (Picked through the party screen's SELECT LEVEL, hosting a party - even alone in it: the WAKE.
                // A level opened the usual way goes at once, the party along as ever.)
                if (!alone && PartyWake.ForParty && IsLevel(sceneName) && SceneHelper.CurrentScene == "Main Menu" && SteamNet.InLobby && IAmHost)
                {
                    if (!PartyWake.Active)
                    {
                        PartyWake.Begin(sceneName);
                    }
                    return false;
                }
                if (alone || !KillNet.Connected || !IAmHost || Members.Count < 2 || !IsLevel(sceneName) || sceneName == SceneHelper.CurrentScene)
                {
                    if (alone && IsLevel(sceneName))
                    {
                        Plugin.Log.LogInfo($"party: playing {sceneName} alone");
                    }
                    return true;
                }
                if (PartyWake.Active)
                {
                    return false;
                }
                Plugin.Log.LogInfo($"party: the party host started {sceneName}: taking everyone");
                KillNet.Send(Go, KillNet.ToServer, w => w.Write(sceneName));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party: couldn't take the party along ({e.Message})");
            }
            return true;
        }

        [HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.QuitMission)), HarmonyPrefix]
        private static void QuittingMission()
        {
            if (SteamNet.InLobby)
            {
                // (Black from the main menu's first frame until the party screen's up: LoadingScreen.)
                PartyMenu.OpenOnMenu = true;
            }
        }

        // ---- the party (its Steam lobby) ----

        public static void OpenWorld() => ServerList.CreateParty();

        public static void LeaveToOwnWorld() => ServerList.LeaveParty();

        // ---- this player's Minecraft ----

        public static void OpenMinecraftWorld() => SendAction(ActionHost, "");

        public static void JoinMinecraftWorld(string link)
        {
            if (IsWorldLink(link))
            {
                Plugin.Log.LogInfo($"party: Minecraft joining {link}");
                SendAction(ActionJoin, link);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex WorldLink =
            new System.Text.RegularExpressions.Regex(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.e4mc\.link$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool IsWorldLink(string link) => !string.IsNullOrEmpty(link) && link.Length < 200 && WorldLink.IsMatch(link);

        public static void LeaveMinecraftWorld() => SendAction(ActionLeave, "");

        private static void SendAction(byte action, string arg)
        {
            KillNet.Send(Action, KillNet.ToMinecraft, w =>
            {
                w.Write(action);
                w.Write(arg ?? "");
            });
        }

        // ---- levels ----

        public static bool IsLevel(string scene) =>
            !string.IsNullOrEmpty(scene) && (scene.StartsWith("Level ", StringComparison.Ordinal) || scene == "uk_construct" || scene == "Endless");

        public static string LevelName(string scene)
        {
            if (string.IsNullOrEmpty(scene))
            {
                return "the menu";
            }
            if (scene == "uk_construct")
            {
                return "the Sandbox";
            }
            if (scene == "Endless")
            {
                return "the Cyber Grind";
            }
            if (scene == "Main Menu")
            {
                return "the menu";
            }
            foreach (var (s, name) in Levels())
            {
                if (s == scene)
                {
                    return name;
                }
            }
            return scene;
        }

        private static List<(string Scene, string Name)> levels;

        // Every level ULTRAKILL has (its scene, "4-3: A SHOT IN THE DARK"), then the Sandbox and the Cyber Grind.
        public static List<(string Scene, string Name)> Levels()
        {
            if (levels != null)
            {
                return levels;
            }
            var list = new List<(string, string)>();
            try
            {
                foreach (int n in GetMissionName.EnumerateMissionNumbers())
                {
                    string scene = GetMissionName.GetSceneName(n);
                    string number = GetMissionName.GetMissionNumberOnly(n);
                    string name = GetMissionName.GetMissionNameOnly(n);
                    if (!string.IsNullOrEmpty(scene) && scene != "Main Menu")
                    {
                        list.Add((scene, $"{number}: {name}"));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party: couldn't list ULTRAKILL's levels ({e.Message})");
            }
            list.Add(("uk_construct", "Sandbox"));
            list.Add(("Endless", "The Cyber Grind"));
            levels = list;
            return list;
        }

        private static Guid ToGuid(long msb, long lsb)
        {
            var b = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                b[i] = (byte)(msb >> (56 - 8 * i));
                b[8 + i] = (byte)(lsb >> (56 - 8 * i));
            }
            return new Guid(b);
        }
    }
}
