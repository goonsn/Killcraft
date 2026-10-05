using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Killcraft
{
    [BepInPlugin("dev.killcraft", "Killcraft", "0.1.6")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<float> UnitsPerBlock;
        internal static ConfigEntry<float> DamageToUltrakill;
        internal static ConfigEntry<float> DamageToMinecraft;
        internal static ConfigEntry<bool> StartMinecraft;
        internal static ConfigEntry<string> LauncherPath;
        internal static ConfigEntry<string> LauncherArgs;
        internal static ConfigEntry<bool> Diagnostics;
        internal static ConfigEntry<float> BlockBrightness;
        internal static ConfigEntry<int> TntFuseTicks;
        internal static ConfigEntry<bool> DigIntoLevels;
        internal static ConfigEntry<bool> FreshWorld;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<bool> MobsFightEnemies;
        internal static ConfigEntry<bool> AlwaysThorns;
        internal static ConfigEntry<bool> MobsAttackYouInLevels;
        internal static ConfigEntry<int> NetherEnemies;

        private void Awake()
        {
            Log = Logger;
            UnitsPerBlock = Config.Bind("World", "UnitsPerBlock", 2.0f,
                "ULTRAKILL units per Minecraft block. 2 makes the Minecraft player (1.8 blocks) about V1's height (3.5 units).");
            DamageToUltrakill = Config.Bind("Combat", "DamageToUltrakill", 0.3f,
                "ULTRAKILL damage per point of Minecraft damage dealt to an enemy (a diamond sword hit is 7; a Filth has 0.5 health, a Swordsmachine 30).");
            DamageToMinecraft = Config.Bind("Combat", "DamageToMinecraft", 1.0f,
                "Multiplier on ULTRAKILL damage before it is sent to Minecraft, which divides it by 5 (ULTRAKILL's 100 health = Minecraft's 20).");
            StartMinecraft = Config.Bind("Minecraft", "StartWithUltrakill", true,
                "Start Minecraft (hidden) when ULTRAKILL starts. Off: start the SkyCraft Prism instance yourself.");
            LauncherPath = Config.Bind("Minecraft", "Launcher", "",
                "Empty: SkyCraft's bundled portable Prism Launcher (SkyCraft-Minecraft.zip next to this plugin). Or the full path of your own launcher.");
            LauncherArgs = Config.Bind("Minecraft", "Arguments", "--launch SkyCraft", "Arguments for the launcher.");
            BlockBrightness = Config.Bind("World", "BlockBrightness", 1.0f,
                "Brightness of Minecraft blocks drawn in ULTRAKILL (1 = Minecraft's daylight look; lower it for dark levels).");
            TntFuseTicks = Config.Bind("Minecraft", "TntFuseTicks", 40,
                "How long lit TNT burns before it blows, in Minecraft ticks (20 a second; Minecraft's own is 80). Applied when Minecraft starts.");
            DigIntoLevels = Config.Bind("Minecraft", "DigIntoLevels", false,
                "Let explosions and pickaxes dig holes into ULTRAKILL's level geometry (SkyCraft's 'destruction'). ULTRAKILL can't show those holes, " +
                "so they leave Minecraft blocks hanging in the air. Applied when Minecraft starts.");
            FreshWorld = Config.Bind("Minecraft", "FreshWorldEachLaunch", true,
                "Start every session with a fresh Minecraft world: blocks you placed, dropped items and holes are gone, and your inventory is " +
                "the starting kit again (weapons, armour, building blocks, TNT). Off: everything stays from one session to the next.");
            MobsFightEnemies = Config.Bind("Minecraft", "MobsFightEnemies", true,
                "Minecraft mobs (spawn eggs: zombies, skeletons, iron golems, wolves, creepers, ...) go after ULTRAKILL's enemies near them, " +
                "and their hits hurt the enemies. Applied when Minecraft starts.");
            AlwaysThorns = Config.Bind("Minecraft", "AlwaysThorns", true,
                "Thorns armour hits back every time an enemy hurts you (Minecraft's own: 15% a level). Applied when Minecraft starts.");
            MobsAttackYouInLevels = Config.Bind("Minecraft", "MobsAttackYouInLevels", false,
                "Minecraft mobs (zombies, skeletons, creepers, ...) attack you in ULTRAKILL's levels too. Off: they're on your side there " +
                "and only fight ULTRAKILL's enemies; in the Nether they always attack you. Applied when Minecraft starts.");
            NetherEnemies = Config.Bind("Minecraft", "NetherEnemies", 6,
                "How many ULTRAKILL enemies are around you at a time in the Nether (filth, strays, drones, schisms, soldiers, ... no bosses). 0: none.");
            ToggleKey = Config.Bind("Controls", "ToggleMinecraft", "F9",
                "Key that turns Minecraft off (V1 is plain ULTRAKILL again; Minecraft's player waits) and back on. A Unity Input System key name, e.g. F9, F10, Backquote.");
            Diagnostics = Config.Bind("Debug", "Diagnostics", false, "Extra logging (collision sources, timings).");

            new Harmony("dev.killcraft").PatchAll(typeof(Plugin).Assembly);

            EnsureHost();
            // ULTRAKILL's scene loader disables every script that isn't in the DontDestroyOnLoad
            // scene, and objects made this early aren't always moved there: re-check on every load.
            SceneManager.sceneLoaded += (_, __) => EnsureHost();

            if (!Link.Create())
            {
                Log.LogError("Killcraft can't create its shared memory; Minecraft won't connect");
                return;
            }
            if (StartMinecraft.Value)
            {
                Launcher.StartMinecraft();
            }
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
