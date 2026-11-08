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
    //    in for the next; the run's over (ULTRAKILL's results) only when nobody's left.
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
