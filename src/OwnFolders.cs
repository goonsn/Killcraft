using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // A second ULTRAKILL on this PC (second-player.bat) can't share the first one's Preferences folder:
    // ULTRAKILL keeps Prefs.json and LocalPrefs.json open the whole time it runs, so the second copy's
    // PrefsManager fails ("Sharing violation") and its intro stays black. Player 2 and up get their own
    // Preferences-pN and Saves-pN instead, copied from player 1's the first time (settings, binds,
    // progress), so their saving never touches player 1's either.
    internal static class OwnFolders
    {
        private static string prefs, saves;

        public static void Install(Harmony harmony)
        {
            if (Plugin.PlayerSlot == 1)
            {
                return;
            }
            string game = Directory.GetParent(Application.dataPath).FullName;
            prefs = Prepare(Path.Combine(game, "Preferences"), Path.Combine(game, $"Preferences-p{Plugin.PlayerSlot}"));
            saves = Prepare(Path.Combine(game, "Saves"), Path.Combine(game, $"Saves-p{Plugin.PlayerSlot}"));
            try
            {
                harmony.Patch(AccessTools.PropertyGetter(typeof(PrefsManager), nameof(PrefsManager.PrefsPath)),
                    new HarmonyMethod(typeof(OwnFolders), nameof(PrefsPath)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(GameProgressSaver), nameof(GameProgressSaver.BaseSavePath)),
                    new HarmonyMethod(typeof(OwnFolders), nameof(SavePath)));
                Plugin.Log.LogInfo($"player {Plugin.PlayerSlot}: ULTRAKILL settings in {prefs}, saves in {saves}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"couldn't give player {Plugin.PlayerSlot} its own ULTRAKILL settings folder ({e.Message}); it may stay on a black screen");
            }
        }

        private static bool PrefsPath(ref string __result)
        {
            __result = prefs;
            return false;
        }

        private static bool SavePath(ref string __result)
        {
            __result = saves;
            return false;
        }

        private static string Prepare(string from, string to)
        {
            if (!Directory.Exists(to) && Directory.Exists(from))
            {
                try
                {
                    Copy(from, to);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"couldn't copy {from} to {to} ({e.Message}); player {Plugin.PlayerSlot} starts with defaults there");
                }
            }
            Directory.CreateDirectory(to);
            return to;
        }

        // (Read alongside player 1's ULTRAKILL, which has some of these files open.)
        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = new FileStream(Path.Combine(to, Path.GetFileName(file)), FileMode.Create);
                src.CopyTo(dst);
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
