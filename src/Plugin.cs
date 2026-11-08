using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Killcraft
{
    [BepInPlugin("dev.killcraft", "Killcraft", "2.0.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<float> UnitsPerBlock;
        internal static ConfigEntry<float> DamageToUltrakill;
        internal static ConfigEntry<float> DamageToMinecraft;
        internal static ConfigEntry<bool> StartMinecraft;
        // Playing with Minecraft (Killcraft), not multiplayer only: switched on, and a Minecraft there to
        // start (SkyCraft's next to this plugin, or a launcher of the player's own). KillNet's download has
        // no Minecraft: multiplayer only, whatever the setting.
        internal static bool WithMinecraft => StartMinecraft != null && StartMinecraft.Value && MinecraftThere;
        private static int minecraftThere = -1;
        internal static bool MinecraftThere
        {
            get
            {
                if (minecraftThere < 0)
                {
                    string dir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "";
                    bool own = LauncherPath != null && !string.IsNullOrWhiteSpace(LauncherPath.Value);
                    minecraftThere = own || System.IO.File.Exists(System.IO.Path.Combine(dir, "SkyCraft-Minecraft.zip")) ? 1 : 0;
                }
                return minecraftThere == 1;
            }
        }
        internal static ConfigEntry<string> LauncherPath;
        internal static ConfigEntry<string> LauncherArgs;
        internal static ConfigEntry<bool> Diagnostics;
        internal static ConfigEntry<int> SimLatencyMs;
        internal static ConfigEntry<float> SimLossPercent;
        internal static ConfigEntry<float> BlockBrightness;
        internal static ConfigEntry<int> TntFuseTicks;
        internal static ConfigEntry<bool> DigIntoLevels;
        internal static ConfigEntry<bool> FreshWorld;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<string> ThirdPersonKey;
        internal static ConfigEntry<bool> MobsFightEnemies;
        internal static ConfigEntry<bool> AlwaysThorns;
        internal static ConfigEntry<bool> MobsAttackYouInLevels;
        internal static ConfigEntry<int> NetherEnemies;
        internal static ConfigEntry<bool> NetherMiniBosses;
        internal static ConfigEntry<string> DiscordAppId;
        // Killcraft's application on Discord's developer portal (public; not a secret).
        internal const string KillcraftDiscordApp = "1557101551343112355";
        internal static ConfigEntry<bool> DamageByDifficulty;
        internal static ConfigEntry<float> BossDamage;
        internal static ConfigEntry<bool> BossesPierceArmour;
        internal static ConfigEntry<bool> HostForSecondPlayer;
        internal static ConfigEntry<bool> PvP;
        internal static ConfigEntry<bool> PlayerCollision;
        internal static ConfigEntry<bool> OutOfMapRescue;
        internal static ConfigEntry<float> PvpDamage;
        internal static ConfigEntry<bool> FollowHost;
        internal static ConfigEntry<int> ListWorld;
        internal static ConfigEntry<string> PartyTitle, LastParty;
        internal static ConfigEntry<int> PartyMax;
        internal static ConfigEntry<bool> PartyCheats, AutoRejoin, PartyOpen;
        internal static ConfigEntry<bool> AskMode;
        internal static ConfigEntry<int> VoiceMode;
        internal static ConfigEntry<string> VoiceKey;
        internal static ConfigEntry<float> VoiceVolume;
        internal static ConfigEntry<bool> VoiceProximity;
        internal static ConfigEntry<bool> BootLine;
        internal static ConfigEntry<int> MyColor;
        internal static ConfigEntry<string> BodyColour, LightsColour, WingsColour;
        internal static ConfigEntry<bool> ReplaysOn;
        internal static ConfigEntry<bool> PartyHudOn;
        internal static ConfigEntry<bool> NameTags;
        internal static ConfigEntry<float> HudScale;
        internal static ConfigEntry<bool> ShowChat;
        internal static ConfigEntry<bool> VoiceEffects;
        internal static ConfigEntry<float> BossHealthPerPlayer;
        internal static ConfigEntry<bool> SpectateOnDeath, Revive, SpectateThirdPerson;
        internal static ConfigEntry<float> ReviveSeconds;
        internal static ConfigEntry<bool> VariantGuns;
        // The second player's world port on this PC (HostForSecondPlayer).
        internal const int SecondPlayerPort = 25565;
        // This ULTRAKILL's KillNet port, which Killcraft's mod in its Minecraft connects to (each player on a PC its own).
        internal static int NetPort => 25590 + PlayerSlot - 1;

        // Which Killcraft on this PC this is: 1, or 2 (and up) for a second ULTRAKILL started with
        // -killcraftPlayer 2 (second-player.bat), to try multiplayer alone. Each has its own config, link
        // to its own Minecraft (its own copy of the Prism instance, offline) and port.
        internal static int PlayerSlot { get; private set; } = 1;
        internal static string InstanceName => PlayerSlot == 1 ? "SkyCraft" : $"SkyCraft-P{PlayerSlot}";

        private static int ReadPlayerSlot()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i].Equals("-killcraftPlayer", System.StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int slot) && slot >= 1 && slot <= 8)
                {
                    return slot;
                }
            }
            return 1;
        }

        private void Awake()
        {
            Log = Logger;
            PlayerSlot = ReadPlayerSlot();
            ConfigFile cfg = PlayerSlot == 1 ? Config
                : new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, $"dev.killcraft.p{PlayerSlot}.cfg"), true, Info.Metadata);
            if (PlayerSlot != 1)
            {
                Log.LogInfo($"Killcraft player {PlayerSlot} on this PC (Minecraft instance {InstanceName}, config {cfg.ConfigFilePath})");
            }
            UnitsPerBlock = cfg.Bind("World", "UnitsPerBlock", 2.0f,
                "ULTRAKILL units per Minecraft block. 2 makes the Minecraft player (1.8 blocks) about V1's height (3.5 units).");
            DamageToUltrakill = cfg.Bind("Combat", "DamageToUltrakill", 0.3f,
                "ULTRAKILL damage per point of Minecraft damage dealt to an enemy (a diamond sword hit is 7; a Filth has 0.5 health, a Swordsmachine 30).");
            DamageToMinecraft = cfg.Bind("Combat", "DamageToMinecraft", 1.0f,
                "Multiplier on ULTRAKILL damage before it is sent to Minecraft, which divides it by 5 (ULTRAKILL's 100 health = Minecraft's 20).");
            DamageByDifficulty = cfg.Bind("Combat", "DamageByDifficulty", true,
                "Enemies hit harder on harder ULTRAKILL difficulties: Harmless x0.5, Lenient x0.75, Standard x1, Violent x1.5, Brutal x2, UKMD x2.5.");
            BossDamage = cfg.Bind("Combat", "BossDamage", 1.5f,
                "Extra multiplier on the damage bosses (V2, Gabriel, the Primes, ...) do to you.");
            BossesPierceArmour = cfg.Bind("Combat", "BossesPierceArmour", true,
                "Bosses' hits go through Minecraft armour (Protection enchantments still count). A raised shield still blocks them.");
            StartMinecraft = cfg.Bind("Minecraft", "StartWithUltrakill", true,
                "Start Minecraft (hidden) when ULTRAKILL starts. Off: start the SkyCraft Prism instance yourself.");
            AskMode = cfg.Bind("Minecraft", "AskAtStart", true,
                "Ask KILLNET (multiplayer only) or KILLCRAFT (with Minecraft) when ULTRAKILL starts (not when the launcher starts it: it asks there). " +
                "Off: StartWithUltrakill decides.");
            LauncherPath = cfg.Bind("Minecraft", "Launcher", "",
                "Empty: SkyCraft's bundled portable Prism Launcher (SkyCraft-Minecraft.zip next to this plugin). Or the full path of your own launcher.");
            LauncherArgs = cfg.Bind("Minecraft", "Arguments",
                PlayerSlot == 1 ? "--launch SkyCraft" : $"--launch {InstanceName} --offline KillcraftP{PlayerSlot}", "Arguments for the launcher.");
            BlockBrightness = cfg.Bind("World", "BlockBrightness", 1.0f,
                "Brightness of Minecraft blocks drawn in ULTRAKILL (1 = Minecraft's daylight look; lower it for dark levels).");
            TntFuseTicks = cfg.Bind("Minecraft", "TntFuseTicks", 40,
                "How long lit TNT burns before it blows, in Minecraft ticks (20 a second; Minecraft's own is 80). Applied when Minecraft starts.");
            DigIntoLevels = cfg.Bind("Minecraft", "DigIntoLevels", false,
                "Let explosions and pickaxes dig holes into ULTRAKILL's level geometry (SkyCraft's 'destruction'). ULTRAKILL can't show those holes, " +
                "so they leave Minecraft blocks hanging in the air. Applied when Minecraft starts.");
            FreshWorld = cfg.Bind("Minecraft", "FreshWorldEachLaunch", true,
                "Start every session with a fresh Minecraft world: blocks you placed, dropped items and holes are gone, and your inventory is " +
                "the starting kit again (weapons, armour, building blocks, TNT). Off: everything stays from one session to the next.");
            MobsFightEnemies = cfg.Bind("Minecraft", "MobsFightEnemies", true,
                "Minecraft mobs (spawn eggs: zombies, skeletons, iron golems, wolves, creepers, ...) go after ULTRAKILL's enemies near them, " +
                "and their hits hurt the enemies. Applied when Minecraft starts.");
            AlwaysThorns = cfg.Bind("Minecraft", "AlwaysThorns", true,
                "Thorns armour hits back every time an enemy hurts you (Minecraft's own: 15% a level). Applied when Minecraft starts.");
            MobsAttackYouInLevels = cfg.Bind("Minecraft", "MobsAttackYouInLevels", false,
                "Minecraft mobs (zombies, skeletons, creepers, ...) attack you in ULTRAKILL's levels too. Off: they're on your side there " +
                "and only fight ULTRAKILL's enemies; in the Nether they always attack you. Applied when Minecraft starts.");
            NetherEnemies = cfg.Bind("Minecraft", "NetherEnemies", 6,
                "How many ULTRAKILL enemies are around you at a time in the Nether (filth, strays, drones, schisms, soldiers, ... no bosses). 0: none.");
            NetherMiniBosses = cfg.Bind("Minecraft", "NetherMiniBosses", true,
                "Now and then one of ULTRAKILL's mini-bosses turns up in the Nether too (a Swordsmachine, Cerberus, Mindflayer, Hideous Mass, " +
                "Ferryman, Gutterman or Guttertank), one at a time, with a boss bar.");
            DiscordAppId = cfg.Bind("Discord", "ApplicationId", PlayerSlot == 1 ? "" : "0",
                "Your Discord status while playing. Empty: Killcraft's. Or another Discord application's id; skycraft: SkyCraft's own status; 0: none. Applied when Minecraft starts.");
            PvP = cfg.Bind("Multiplayer", "PvP", false,
                "In your world (/host), players can hurt each other, with Minecraft's weapons and ULTRAKILL's. Off: you play together, not against " +
                "each other. Also in the pause menu (Killcraft's options), where a change counts at once.");
            PvpDamage = cfg.Bind("Multiplayer", "PvpDamage", 1f,
                "With PvP on in your world: how hard players hit each other (1: about as ULTRAKILL's own weapons hurt V1, a revolver shot a tenth " +
                "of V1's health). Counts for everyone in your world. Also in the pause menu.");
            PlayerCollision = cfg.Bind("Multiplayer", "PlayerCollision", true,
                "In your party: players bump into each other instead of walking through (never pushed into a wall or out of the map). " +
                "The party host's setting counts for everyone. Also in the pause menu.");
            OutOfMapRescue = cfg.Bind("Multiplayer", "OutOfMapRescue", true,
                "In a party: falling with nothing of the level below you (through a wall or a floor) puts you back where you last stood on solid ground.");
            FollowHost = cfg.Bind("Multiplayer", "FollowPartyHost", true,
                "When the party host picks a level for everyone (the party screen), go there too. Off: you stay where you are.");
            ListWorld = cfg.Bind("Multiplayer", "ListWorld", 1,
                "Once your world is open (/host, or Open to friends on the party screen), who sees it in the server list: 0 nobody, " +
                "1 your Steam friends, 2 everyone (anyone with Killcraft can join).");
            PartyTitle = cfg.Bind("Multiplayer", "PartyName", "", "The name your party shows in the server list (HOST PARTY on the party screen). Empty: <your Steam name>'s Lobby.");
            PartyMax = cfg.Bind("Multiplayer", "PartyMaxPlayers", 10, "How many can be in a party you make, 2 to 10.");
            PartyCheats = cfg.Bind("Multiplayer", "PartyCheats", false, "In a party you make: whether ULTRAKILL's cheats can be turned on (by anyone in it). The sandbox always has them.");
            LastParty = cfg.Bind("Multiplayer", "LastParty", "", "The last party you were in (its Steam lobby and name), for REJOIN on the party screen.");
            AutoRejoin = cfg.Bind("Multiplayer", "AutoRejoin", true,
                "Lost the connection to your party, or ULTRAKILL closed without leaving it (a crash): join it again by itself.");
            PartyOpen = cfg.Bind("Multiplayer", "InPartyNow", false,
                "Set by KillNet while you're in a party (cleared when you leave it or quit): still set at the next start means ULTRAKILL crashed in one.");
            UnityEngine.Application.quitting += () => PartyOpen.Value = false;
            VoiceMode = cfg.Bind("Voice", "Mode", 1,
                "Voice chat with the others in your world (through Steam: its microphone and levels): 0 off, 1 push to talk (VoiceKey), 2 open microphone.");
            VoiceKey = cfg.Bind("Voice", "PushToTalkKey", "V",
                "Hold to talk (push to talk). A Unity Input System key name (V, B, T, CapsLock, LeftAlt, ...) or Mouse4 / Mouse5.");
            VoiceVolume = cfg.Bind("Voice", "Volume", 1f, "How loud the others' voices are, 0 to 3.");
            VoiceProximity = cfg.Bind("Voice", "FromTheirV1", true,
                "In the same level, a player's voice comes from where their V1 is (quieter far away). Off: everyone's heard the same everywhere.");
            BootLine = cfg.Bind("General", "BootScreenLine", true, "KILLCRAFT ... in ULTRAKILL's start-up check (the intro).");
            MyColor = cfg.Bind("Party", "MyColor", -1,
                "Your colour in the party (your name, your card): -1 one picked for you, 0 red, 1 orange, 2 yellow, 3 green, 4 cyan, 5 blue, 6 purple, 7 pink, 8 white.");
            BodyColour = cfg.Bind("Party", "V1Body", "", "Your V1's main colour, as the others see it: a hex colour like 7A3FD1, or empty for V1's own blue.");
            LightsColour = cfg.Bind("Party", "V1Lights", "", "Your V1's lights' colour (hex, or empty for its own yellow).");
            WingsColour = cfg.Bind("Party", "V1Wings", "", "Your V1's wings' glow (hex, or empty for its own orange).");
            ReplaysOn = cfg.Bind("Replays", "Record", true, "Keep a replay of each run (saved at the level's end, or from the KILLCRAFT panel's REPLAY tab).");
            PartyHudOn = cfg.Bind("Party", "PartyHud", true, "In a level: the other party members' cards at the top left (health, checkpoints, skulls, dead, finished).");
            NameTags = cfg.Bind("Party", "NameTags", true, "The other players' names over their V1s.");
            HudScale = cfg.Bind("Party", "HudSize", 1f, "How big the party's cards are, 0.5 to 2.");
            ShowChat = cfg.Bind("Party", "Chat", true, "The party's chat at the bottom left in a level (Enter to type; or T with Minecraft off).");
            BossHealthPerPlayer = cfg.Bind("Multiplayer", "BossHealthPerPlayer", 0.5f,
                "Bosses' health goes up with more players in the level: this much more for each other player there (0.5: +50% each; 0: off).");
            SpectateOnDeath = cfg.Bind("Multiplayer", "SpectateOnDeath", true,
                "Dying while someone else in the party is still alive in the level: watch them until one of them reaches a checkpoint, then come back there (instead of restarting).");
            SpectateThirdPerson = cfg.Bind("Multiplayer", "SpectateThirdPerson", false,
                "Spectating is through the watched player's eyes; on: F switches to behind them and back.");
            Revive = cfg.Bind("Multiplayer", "Revive", false,
                "While you're dead and spectating, a teammate standing by where you died for a few seconds brings you back there.");
            ReviveSeconds = cfg.Bind("Multiplayer", "ReviveSeconds", 3f, "How long a teammate stands by you to revive you (1 to 10 seconds).");
            VariantGuns = cfg.Bind("Multiplayer", "VariantGuns", true, "Draw other players' guns as their variant's own model (the Sharpshooter, the Sawed-On's saw, ...), not just its colour.");
            VoiceEffects = cfg.Bind("Voice", "EchoAndUnderwater", true, "Voices echo in big rooms and are muffled underwater (theirs or yours).");
            HostForSecondPlayer = cfg.Bind("Multiplayer", "HostForSecondPlayer", false,
                "For trying multiplayer alone on one PC: your Minecraft world opens to a second Killcraft started with second-player.bat " +
                $"(port {SecondPlayerPort}, without Minecraft's sign-in check, so its offline player can join). Off for normal play. Applied when Minecraft starts.");
            ToggleKey = cfg.Bind("Controls", "ToggleMinecraft", "F9",
                "Key that turns Minecraft off (V1 is plain ULTRAKILL again; Minecraft's player waits) and back on. A Unity Input System key name, e.g. F9, F10, Backquote.");
            ThirdPersonKey = cfg.Bind("Controls", "ThirdPerson", "F5",
                "Key that switches V1 between first and third person in ULTRAKILL mode (not while spectating). A Unity Input System key name.");
            Diagnostics = cfg.Bind("Debug", "Diagnostics", false, "Extra logging (collision sources, timings). With it on, F8 in a level shows the other players' network and animation state (their last reported position in red, where they're drawn in green).");
            SimLatencyMs = cfg.Bind("Debug", "SimLatencyMs", 0, "Pretend lag, in milliseconds, on what the other players say of their V1 (to test with two Killcrafts on one PC). 0 for none.");
            SimLossPercent = cfg.Bind("Debug", "SimLossPercent", 0f, "Pretend packet loss, in percent, on what the other players say of their V1. 0 for none.");
            ShaderPack.Bind(cfg);

            var harmony = new Harmony("dev.killcraft");
            OwnFolders.Install(harmony);
            harmony.PatchAll(typeof(Plugin).Assembly);
            EnemySync.Install(harmony);
            WorldSync.Install(harmony);
            Shots.Install(harmony);
            V1Body.Install(harmony);

            EnsureHost();
            // ULTRAKILL's scene loader disables every script that isn't in the DontDestroyOnLoad
            // scene, and objects made this early aren't always moved there: re-check on every load.
            SceneManager.sceneLoaded += (_, __) => EnsureHost();

            if (!Link.Create())
            {
                Log.LogError("Killcraft can't create its shared memory; Minecraft won't connect");
                return;
            }
            KillNet.Start();
            // (Asked in the main menu first, ModeChoice: Minecraft starts once it's answered.)
            if (!ModeChoice.Decide() && WithMinecraft)
            {
                Launcher.StartMinecraft();
            }
            ModeChoice.Hook();
            LoadingScreen.Hook();
        }

        private static Host host;

        private static void EnsureHost()
        {
            if (host == null)
            {
                var go = new GameObject("Killcraft");
                go.hideFlags = HideFlags.HideInHierarchy;
                host = go.AddComponent<Host>();
            }
            if (host.gameObject.scene.name != "DontDestroyOnLoad")
            {
                DontDestroyOnLoad(host.gameObject);
            }
            if (!host.enabled)
            {
                Log.LogInfo("host script was disabled by a scene load; re-enabling it");
                host.enabled = true;
            }
        }
    }
}
