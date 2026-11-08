using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // The Cybergrind with others (KillNet): everyone's the same.
    //  - the same waves: ULTRAKILL's dice for the arena's patterns and enemies are thrown alike in
    //    everyone's ULTRAKILL (the same numbers for the same wave), so the same enemies come in the
    //    same places, as EnemySync needs (each wave's enemies are new: the last's dead aren't them);
    //  - dying with others still alive: watching them (Spectate) until the wave's cleared, then back
    //    in for the next; the run's over (ULTRAKILL's results) only when nobody's left;
    //  - a run with others has its own best (PARTY BEST, kept in Killcraft's own file): it isn't put
    //    against this player's solo best, nor sent to Steam's leaderboard.
    [HarmonyPatch]
    internal static class Cybergrind
    {
        private static int calls;
        private static bool seeded;
        private static UnityEngine.Random.State saved;
        public static bool AllowGameOver;

        private static bool Shared => Room.Shared;

        private static void Seed(EndlessGrid grid, int which)
        {
            if (!Shared || seeded)
            {
                return;
            }
            saved = UnityEngine.Random.state;
            seeded = true;
            int wave = grid != null ? grid.currentWave : 0;
            unchecked
            {
                int seed = 1469598103 ^ (wave * 7919) ^ (which * 104729) ^ (calls++ * 31337);
                UnityEngine.Random.InitState(seed);
            }
        }

        // ---- the party's best ----

        // (This run has had others in it: from the arena's start, in this load of the level.)
        private static int partyRunScene = -1;

        public static bool PartyRun => Shared || partyRunScene == UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;

        private static string PartyBestFile => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "killcraft-party-cybergrind.txt");

        // One line a difficulty: waves (with the part of the last), kills, style, seconds.
        private static CyberRankData ReadPartyBest()
        {
            var d = new CyberRankData();
            try
            {
                if (System.IO.File.Exists(PartyBestFile))
                {
                    string[] lines = System.IO.File.ReadAllLines(PartyBestFile);
                    for (int i = 0; i < lines.Length && i < 6; i++)
                    {
                        string[] p = lines[i].Split(' ');
                        if (p.Length >= 4)
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            d.preciseWavesByDifficulty[i] = float.Parse(p[0], inv);
                            d.kills[i] = int.Parse(p[1], inv);
                            d.style[i] = int.Parse(p[2], inv);
                            d.time[i] = float.Parse(p[3], inv);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"cybergrind: couldn't read the party's best ({e.Message})");
            }
            return d;
        }

        [HarmonyPatch(typeof(GameProgressSaver), nameof(GameProgressSaver.GetBestCyber)), HarmonyPrefix]
        private static bool GetBest(ref CyberRankData __result)
        {
            if (!PartyRun)
            {
                return true;
            }
            __result = ReadPartyBest();
            return false;
        }

        [HarmonyPatch(typeof(GameProgressSaver), nameof(GameProgressSaver.SetBestCyber)), HarmonyPrefix]
        private static bool SetBest(FinalCyberRank fcr)
        {
            if (!PartyRun)
            {
                return true;
            }
            try
            {
                CyberRankData d = ReadPartyBest();
                int dif = Mathf.Clamp(MonoSingleton<PrefsManager>.Instance.GetInt("difficulty"), 0, 5);
                d.preciseWavesByDifficulty[dif] = fcr.savedWaves;
                d.kills[dif] = fcr.savedKills;
                d.style[dif] = fcr.savedStyle;
                d.time[dif] = fcr.savedTime;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var lines = new string[6];
                for (int i = 0; i < 6; i++)
                {
                    lines[i] = string.Format(inv, "{0} {1} {2} {3}", d.preciseWavesByDifficulty[i], d.kills[i], d.style[i], d.time[i]);
                }
                System.IO.File.WriteAllLines(PartyBestFile, lines);
                Plugin.Log.LogInfo($"cybergrind: new party best ({fcr.savedWaves:0.##} waves); the solo best is kept as it was");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"cybergrind: couldn't save the party's best ({e.Message})");
            }
            return false;
        }

        // (A party's run isn't a solo score: not on Steam's leaderboard.)
        [HarmonyPatch(typeof(LeaderboardController), nameof(LeaderboardController.SubmitCyberGrindScore)), HarmonyPrefix]
        private static bool Submit()
        {
            if (!PartyRun)
            {
                return true;
            }
            Plugin.Log.LogInfo("cybergrind: a party's run, not sent to Steam's leaderboard");
            return false;
        }

        // The results say whose best it is.
        [HarmonyPatch(typeof(FinalCyberRank), nameof(FinalCyberRank.GameOver)), HarmonyPostfix]
        private static void Results(FinalCyberRank __instance)
        {
            try
            {
                if (PartyRun && __instance.bestWaveText != null)
                {
                    __instance.bestWaveText.text += "\n<color=#DB5447><size=16>PARTY BEST</size></color>";
                }
            }
            catch
            {
            }
        }

        private static void Unseed()
        {
            if (seeded)
            {
                seeded = false;
                UnityEngine.Random.state = saved;
            }
        }

        // The patterns' order: the same for everyone (made again when the run starts: each ULTRAKILL
        // shuffled them its own way when the level loaded, before it knew of the others).
        [HarmonyPatch(typeof(EndlessGrid), "OnTriggerEnter"), HarmonyPrefix]
        private static void Starting(EndlessGrid __instance, Collider other)
        {
            try
            {
                if (!Shared || other == null || !other.CompareTag("Player"))
                {
                    return;
                }
                calls = 0;
                partyRunScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                var pool = Traverse.Create(__instance).Property("CurrentPatternPool").GetValue<ArenaPattern[]>();
                if (pool == null || pool.Length < 2)
                {
                    return;
                }
                Array.Sort(pool, (a, b) => string.CompareOrdinal(a != null ? a.name : "", b != null ? b.name : ""));
                var rng = new System.Random(20260108);
                for (int k = 0; k < pool.Length; k++)
                {
                    int j = rng.Next(k, pool.Length);
                    ArenaPattern t = pool[k];
                    pool[k] = pool[j];
                    pool[j] = t;
                }
                Plugin.Log.LogInfo($"cybergrind: the patterns in the party's order ({pool.Length})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"cybergrind: couldn't order the patterns ({e.Message})");
            }
        }

        [HarmonyPatch(typeof(EndlessGrid), "NextWave"), HarmonyPrefix]
        private static void NextWaveBefore(EndlessGrid __instance)
        {
            calls = 0;
            if (Shared)
            {
                EnemySync.NewWave();
            }
            Seed(__instance, 1);
        }

        [HarmonyPatch(typeof(EndlessGrid), "NextWave"), HarmonyFinalizer]
        private static Exception NextWaveAfter(Exception __exception)
        {
            Unseed();
            Spectate.WaveCleared();
            return __exception;
        }

        [HarmonyPatch(typeof(EndlessGrid), "ShuffleDecks"), HarmonyPrefix]
        private static void ShuffleBefore(EndlessGrid __instance) => Seed(__instance, 2);

        [HarmonyPatch(typeof(EndlessGrid), "ShuffleDecks"), HarmonyFinalizer]
        private static Exception ShuffleAfter(Exception __exception)
        {
            Unseed();
            return __exception;
        }

        [HarmonyPatch(typeof(EndlessGrid), "GetEnemies"), HarmonyPrefix]
        private static void EnemiesBefore(EndlessGrid __instance) => Seed(__instance, 3);

        [HarmonyPatch(typeof(EndlessGrid), "GetEnemies"), HarmonyFinalizer]
        private static Exception EnemiesAfter(Exception __exception)
        {
            Unseed();
            return __exception;
        }

        [HarmonyPatch(typeof(EndlessGrid), "GetNextEnemy"), HarmonyPrefix]
        private static void NextEnemyBefore(EndlessGrid __instance) => Seed(__instance, 4);

        [HarmonyPatch(typeof(EndlessGrid), "GetNextEnemy"), HarmonyFinalizer]
        private static Exception NextEnemyAfter(Exception __exception)
        {
            Unseed();
            return __exception;
        }

        // Dead with others still fighting: not the run's end (Spectate watches them till the wave's done).
        [HarmonyPatch(typeof(FinalCyberRank), nameof(FinalCyberRank.GameOver)), HarmonyPrefix]
        private static bool GameOverBefore() => AllowGameOver || !Spectate.CyberWaiting();

        public static void GameOver()
        {
            try
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                FinalCyberRank fcr = nm != null ? nm.GetComponentInChildren<FinalCyberRank>(true) : null;
                if (fcr == null)
                {
                    fcr = UnityEngine.Object.FindObjectOfType<FinalCyberRank>(true);
                }
                AllowGameOver = true;
                fcr?.GameOver();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"cybergrind: couldn't end the run ({e.Message})");
            }
            finally
            {
                AllowGameOver = false;
            }
        }
    }
}
