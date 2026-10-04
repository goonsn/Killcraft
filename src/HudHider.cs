using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // While Minecraft drives the player its own HUD (hotbar, hearts, crosshair) replaces ULTRAKILL's:
    // V1's health/weapon panel, style meter, crosshair and the cheats banner are hidden (by disabling
    // their canvases and graphics, so the scripts behind them keep running) and restored afterwards.
    // Pause menu, level-end screen and subtitles stay.
    internal static class HudHider
    {
        private static readonly Dictionary<Behaviour, bool> behaviours = new Dictionary<Behaviour, bool>();
        private static readonly Dictionary<GameObject, bool> objects = new Dictionary<GameObject, bool>();
        private static readonly List<Behaviour> targetBehaviours = new List<Behaviour>();
        private static readonly List<GameObject> targetObjects = new List<GameObject>();
        private static float rescan;

        public static void Frame(bool hide, NewMovement nm)
        {
            if (!hide)
            {
                Restore();
                return;
            }
            rescan -= Time.unscaledDeltaTime;
            if (rescan <= 0f)
            {
                rescan = 1f;
                Collect(nm);
            }
            foreach (Behaviour b in targetBehaviours)
            {
                if (b != null && b.enabled)
                {
                    if (!behaviours.ContainsKey(b))
                    {
                        behaviours[b] = true;
                    }
                    b.enabled = false;
                }
            }
            foreach (GameObject go in targetObjects)
            {
                if (go != null && go.activeSelf)
                {
                    if (!objects.ContainsKey(go))
                    {
                        objects[go] = true;
                    }
                    go.SetActive(false);
                }
            }
        }

        private static void Collect(NewMovement nm)
        {
            targetBehaviours.Clear();
            targetObjects.Clear();
            if (nm != null && nm.screenHud != null)
            {
                targetBehaviours.AddRange(nm.screenHud.GetComponentsInChildren<Canvas>(true));
            }
            foreach (HudController hud in Object.FindObjectsOfType<HudController>())
            {
                targetBehaviours.AddRange(hud.GetComponentsInChildren<Canvas>(true));
                if (hud.gunCanvas != null)
                {
                    targetBehaviours.AddRange(hud.gunCanvas.GetComponentsInChildren<Canvas>(true));
                }
            }
            foreach (Crosshair crosshair in Object.FindObjectsOfType<Crosshair>())
            {
                targetBehaviours.AddRange(crosshair.GetComponentsInChildren<UnityEngine.UI.Graphic>(true));
            }
            if (MonoSingleton.GetInstance(typeof(CheatsController)) is CheatsController cheats && cheats != null)
            {
                foreach (string field in new[] { "cheatsEnabledPanel", "cheatsInfoPanel" })
                {
                    if (Traverse.Create(cheats).Field(field).GetValue() is GameObject panel && panel != null)
                    {
                        targetObjects.Add(panel);
                    }
                }
            }
            if (MonoSingleton.GetInstance(typeof(PowerUpMeter)) is PowerUpMeter meter && meter != null)
            {
                targetBehaviours.AddRange(meter.GetComponentsInChildren<UnityEngine.UI.Graphic>(true));
            }
        }

        private static void Restore()
        {
            if (behaviours.Count == 0 && objects.Count == 0)
            {
                return;
            }
            foreach (var kv in behaviours)
            {
                if (kv.Key != null)
                {
                    kv.Key.enabled = kv.Value;
                }
            }
            foreach (var kv in objects)
            {
                if (kv.Key != null)
                {
                    kv.Key.SetActive(kv.Value);
                }
            }
            behaviours.Clear();
            objects.Clear();
            rescan = 0f;
        }
    }
}
