using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // While V1 is in Minecraft's Nether (high above the level, see Coords) ULTRAKILL looks like it:
    // Minecraft's dark red Nether fog and sky, and the camera stops short of the level far below. The
    // enemies left in the level don't come after V1 (NetherWorld brings the Nether's own).
    internal static class Nether
    {
        private static readonly Color FogColor = new Color(0.2f, 0.03f, 0.03f);
        private static readonly Color Ambient = new Color(0.8f, 0.7f, 0.65f);
        // Unity units: Minecraft's render distance of 8 chunks is 256.
        private const float FogStart = 30f, FogEnd = 240f, FarClip = 400f;

        private static bool shown;
        private static bool savedFog;
        private static Color savedFogColor, savedAmbient;
        private static FogMode savedFogMode;
        private static float savedFogStart, savedFogEnd, savedFogDensity;
        private static readonly List<(Camera Cam, CameraClearFlags Flags, Color Background, float Far)> savedCams =
            new List<(Camera, CameraClearFlags, Color, float)>();

        private static readonly List<EnemyIdentifier> ignoring = new List<EnemyIdentifier>();

        public static void Frame(bool inNether, CameraController cc)
        {
            PadFrame();
            if (inNether != shown)
            {
                shown = inNether;
                if (inNether)
                {
                    Enter(cc);
                }
                else
                {
                    Leave();
                }
            }
            if (shown)
            {
                // Every frame: some levels set their fog every frame.
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = FogColor;
                RenderSettings.fogStartDistance = FogStart;
                RenderSettings.fogEndDistance = FogEnd;
                RenderSettings.ambientLight = Ambient;
                foreach (var c in savedCams)
                {
                    if (c.Cam != null)
                    {
                        c.Cam.clearFlags = CameraClearFlags.SolidColor;
                        c.Cam.backgroundColor = FogColor;
                        c.Cam.farClipPlane = FarClip;
                    }
                }
            }
        }

        // SkyCraft holds a player it has just placed (one arriving in the Nether too) until the host's
        // collision shows ground under it, or 6 seconds. The Nether is Minecraft's blocks, not
        // ULTRAKILL's: a short-lived invisible floor under the landing (level with its obsidian) lets
        // the player go at once.
        private static GameObject pad;
        private static float padTime;

        public static void Landed(Vector3 feet)
        {
            if (pad == null)
            {
                pad = new GameObject("Killcraft Nether landing") { layer = 8 };
                pad.AddComponent<BoxCollider>();
            }
            float u = Coords.U;
            pad.transform.position = feet - Vector3.up * (0.5f * u);
            pad.GetComponent<BoxCollider>().size = new Vector3(3f * u, u, 3f * u);
            pad.SetActive(true);
            padTime = 6f;
        }

        private static void PadFrame()
        {
            if (pad != null && pad.activeSelf && (padTime -= Time.unscaledDeltaTime) <= 0f)
            {
                pad.SetActive(false);
            }
        }

        // A new level brings its own fog and cameras: nothing of the old level's to put back.
        public static void LevelChanged()
        {
            if (shown)
            {
                savedCams.Clear();
                savedFog = RenderSettings.fog;
                savedFogColor = RenderSettings.fogColor;
                savedFogMode = RenderSettings.fogMode;
                savedFogStart = RenderSettings.fogStartDistance;
                savedFogEnd = RenderSettings.fogEndDistance;
                savedFogDensity = RenderSettings.fogDensity;
                savedAmbient = RenderSettings.ambientLight;
            }
        }

        private static void Enter(CameraController cc)
        {
            Plugin.Log.LogInfo("V1 is in the Nether");
            savedFog = RenderSettings.fog;
            savedFogColor = RenderSettings.fogColor;
            savedFogMode = RenderSettings.fogMode;
            savedFogStart = RenderSettings.fogStartDistance;
            savedFogEnd = RenderSettings.fogEndDistance;
            savedFogDensity = RenderSettings.fogDensity;
            savedAmbient = RenderSettings.ambientLight;
            savedCams.Clear();
            Camera main = cc != null ? cc.cam : null;
            if (main != null)
            {
                savedCams.Add((main, main.clearFlags, main.backgroundColor, main.farClipPlane));
                // ULTRAKILL draws the level through this one too.
                Transform virtualCam = main.transform.Find("Virtual Camera");
                if (virtualCam != null && virtualCam.TryGetComponent(out Camera v))
                {
                    savedCams.Add((v, v.clearFlags, v.backgroundColor, v.farClipPlane));
                }
            }
            // The level's enemies (only those: the Nether's own come after V1) ignore V1 meanwhile.
            ignoring.Clear();
            foreach (EnemyIdentifier eid in Object.FindObjectsOfType<EnemyIdentifier>())
            {
                if (eid != null && !eid.ignorePlayer)
                {
                    eid.ignorePlayer = true;
                    ignoring.Add(eid);
                }
            }
        }

        private static void Leave()
        {
            Plugin.Log.LogInfo("V1 is back from the Nether");
            RenderSettings.fog = savedFog;
            RenderSettings.fogColor = savedFogColor;
            RenderSettings.fogMode = savedFogMode;
            RenderSettings.fogStartDistance = savedFogStart;
            RenderSettings.fogEndDistance = savedFogEnd;
            RenderSettings.fogDensity = savedFogDensity;
            RenderSettings.ambientLight = savedAmbient;
            foreach (var c in savedCams)
            {
                if (c.Cam != null)
                {
                    c.Cam.clearFlags = c.Flags;
                    c.Cam.backgroundColor = c.Background;
                    c.Cam.farClipPlane = c.Far;
                }
            }
            savedCams.Clear();
            foreach (EnemyIdentifier eid in ignoring)
            {
                if (eid != null)
                {
                    eid.ignorePlayer = false;
                }
            }
            ignoring.Clear();
        }
    }
}
