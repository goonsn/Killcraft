using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Killcraft
{
    // Each enemy's picture: ULTRAKILL's own, from its list of what the sandbox spawns (the spawn
    // menu's and the terminal's bestiary pictures), by enemy type.
    internal static class Icons
    {
        private static Dictionary<string, Sprite> byType;
        private static float triedAt = -100f;

        public static Sprite For(string enemyType)
        {
            if ((byType == null || byType.Count == 0) && Time.unscaledTime - triedAt > 30f)
            {
                Load();
            }
            return byType != null && byType.TryGetValue(enemyType ?? "", out Sprite s) ? s : null;
        }

        private static void Load()
        {
            triedAt = Time.unscaledTime;
            byType = new Dictionary<string, Sprite>();
            try
            {
                var dbs = new List<SpawnableObjectsDatabase>(Resources.FindObjectsOfTypeAll<SpawnableObjectsDatabase>());
                if (dbs.Count == 0)
                {
                    SpawnableObjectsDatabase db = FromAddressables();
                    if (db != null)
                    {
                        dbs.Add(db);
                    }
                }
                foreach (SpawnableObjectsDatabase db in dbs)
                {
                    if (db == null || db.enemies == null)
                    {
                        continue;
                    }
                    foreach (SpawnableObject o in db.enemies)
                    {
                        if (o != null && o.gridIcon != null)
                        {
                            string k = o.enemyType.ToString();
                            if (!byType.ContainsKey(k))
                            {
                                byType[k] = o.gridIcon;
                            }
                        }
                    }
                }
                Plugin.Log.LogInfo($"replay: {byType.Count} enemy pictures ({dbs.Count} lists)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't find the enemies' pictures ({e.Message})");
            }
        }

        private static SpawnableObjectsDatabase FromAddressables()
        {
            foreach (IResourceLocator loc in Addressables.ResourceLocators)
            {
                foreach (object key in loc.Keys)
                {
                    if (!(key is string s) || s.IndexOf("pawnable", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (loc.Locate(key, typeof(SpawnableObjectsDatabase), out IList<IResourceLocation> found) && found != null && found.Count > 0)
                    {
                        Plugin.Log.LogInfo($"replay: the enemies' pictures from {s}");
                        return Addressables.LoadAssetAsync<SpawnableObjectsDatabase>(s).WaitForCompletion();
                    }
                }
            }
            return null;
        }
    }
}
