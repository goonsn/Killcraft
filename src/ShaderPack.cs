using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace Killcraft
{
    // Killcraft's shaders, as a Minecraft shader pack is: K turns them on and off; with the game paused,
    // a panel on the right sets each effect. On ULTRAKILL's picture of the world (Minecraft's blocks,
    // mobs and items in it too), after its main camera has drawn it and before its HUD and its own
    // palette and pixelation: ambient occlusion, sun shadows, fog, god rays, bloom, tone mapping and
    // colour grading.
    //
    // The shaders are built (Unity 2022.3.29f1, as ULTRAKILL's) into killcraft-shaders.bundle, next to
    // this plugin; the shaders folder of Killcraft's source builds it.
    internal static class ShaderPack
    {
        private const string BundleName = "killcraft-shaders.bundle", PostName = "Hidden/Killcraft/Post", SunDepthName = "Hidden/Killcraft/SunDepth",
            SunColorName = "Hidden/Killcraft/SunColor", WaterName = "Hidden/Killcraft/Water";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Key, Quality;

        // The presets, as a Minecraft shader pack's profiles: how much each draws (its shadow map's
        // size and reach, how soft its shadows, how many samples its occlusion and light shafts take,
        // coloured light through stained glass or not) and the look it starts from (the sliders, in
        // the panel's order; still each changeable after).
        private sealed class Tier
        {
            public string Name;
            public bool Ao, Shadows, Vol, Coloured, Pcss, Water;
            public int ShadowSize, Taps, AoSamples, VolSteps, ColourSize, Reflections, CloudSteps;
            public float ShadowHalf, Soft, VolReach;
            public float[] Look;
            // x: sunlit brighter; y: shade darker; z: the sun's colour; w: skylight by facing.
            public Vector4 Light = new Vector4(0.12f, 0.5f, 0.4f, 0f);
            // x: cloud shadows; y: contact shadows; z: sun glare; w: light through stained glass brighter.
            public Vector4 Extras;
        }

        private static readonly Tier[] tiers =
        {
            new Tier { Name = "Potato", Look = new[] { 0f, 0f, 0.6f, 0f, 0.15f, 0.8f, 1f, 1.15f, 1.25f, 0.5f, 0.25f, 0.3f } },
            new Tier { Name = "Low", Ao = true, AoSamples = 6, Shadows = true, ShadowSize = 1024, ShadowHalf = 40f, Taps = 4, Soft = 1f,
                Look = new[] { 0.7f, 0.7f, 0.7f, 0f, 0.2f, 0.9f, 1f, 1.2f, 1.3f, 0.6f, 0.3f, 0.35f } },
            new Tier { Name = "Standard", Ao = true, AoSamples = 8, Shadows = true, ShadowSize = 2048, ShadowHalf = 48f, Taps = 9, Soft = 1.5f,
                Vol = true, VolSteps = 12, VolReach = 48f, Water = true, Reflections = 12,
                Look = new[] { 0.85f, 0.8f, 0.8f, 0.8f, 0.2f, 1f, 1f, 1.2f, 1.35f, 0.65f, 0.3f, 0.35f } },
            new Tier { Name = "High", Ao = true, AoSamples = 10, Shadows = true, ShadowSize = 2048, ShadowHalf = 48f, Taps = 12, Soft = 1.5f,
                Vol = true, VolSteps = 16, VolReach = 64f, Coloured = true, ColourSize = 1024, Water = true, Reflections = 20,
                Light = new Vector4(0.18f, 0.52f, 0.45f, 0.5f), Extras = new Vector4(0f, 0f, 0.5f, 0.6f),
                Look = new[] { 0.9f, 0.85f, 0.9f, 1f, 0.25f, 1.05f, 1f, 1.15f, 1.3f, 0.6f, 0.3f, 0.5f } },
            new Tier { Name = "Very High", Ao = true, AoSamples = 12, Shadows = true, ShadowSize = 4096, ShadowHalf = 56f, Taps = 16, Soft = 2f, Pcss = true,
                Vol = true, VolSteps = 24, VolReach = 80f, Coloured = true, ColourSize = 2048, Water = true, Reflections = 28,
                Light = new Vector4(0.25f, 0.55f, 0.5f, 1f), Extras = new Vector4(0.45f, 0f, 0.8f, 0.7f), CloudSteps = 12,
                Look = new[] { 0.95f, 0.9f, 1f, 1.1f, 0.18f, 1.1f, 1.05f, 1.15f, 1.3f, 0.6f, 0.3f, 0.75f } },
            // (Trimmed where it didn't show: fewer shadow taps, shaft and cloud steps, reflection steps.)
            new Tier { Name = "Ultra", Ao = true, AoSamples = 12, Shadows = true, ShadowSize = 4096, ShadowHalf = 64f, Taps = 24, Soft = 2f, Pcss = true,
                Vol = true, VolSteps = 32, VolReach = 96f, Coloured = true, ColourSize = 2048, Water = true, Reflections = 32,
                Light = new Vector4(0.32f, 0.58f, 0.55f, 1f), Extras = new Vector4(0.55f, 1f, 1f, 0.8f), CloudSteps = 16,
                Look = new[] { 1f, 0.95f, 1.1f, 1.1f, 0.15f, 1.15f, 1.1f, 1.18f, 1.3f, 0.6f, 0.3f, 0.85f } },
        };

        private static int tier = 3, builtTier = -1;
        private static Tier T => tiers[tier];

        private static int TierOf(string name)
        {
            for (int i = 0; i < tiers.Length; i++)
            {
                if (string.Equals(tiers[i].Name.Replace(" ", ""), (name ?? "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return 3;
        }
        internal static ConfigEntry<float> Bloom, Exposure, Saturation, Contrast, Warmth, Vignette, Filmic, Occlusion, Shadows, GodRays, Fog, LightShafts;

        // The panel's sliders: each a setting, its name, its range.
        private sealed class Setting
        {
            public ConfigEntry<float> Entry;
            public string Name;
            public float Min, Max, Value;
        }

        private static Setting[] settings;
        private static Setting bloom, exposure, saturation, contrast, warmth, vignette, filmic, occlusion, shadows, godRays, fog, lightShafts;

        // (Changed by the panel as it's dragged, saved to the config when it closes.)
        private static bool on;
        private static bool panelWasOpen, dirty;

        private static Material post;
        private static Shader sunDepth, sunColor, water;
        // Killcraft's water: what's drawn before ULTRAKILL's see-through things, which it reflects and shows through.
        private static CommandBuffer cbWater;
        private static RenderTexture sceneColor, sceneDepth;
        private static bool loadTried;
        private static CommandBuffer cb;
        private static Camera builtCam;
        private static RenderTexture builtFor, builtDepth;
        private static UnityEngine.InputSystem.Key key = UnityEngine.InputSystem.Key.None;
        private static bool inGame, paused, hooked;

        // The sun, and its shadow map (its size and reach the preset's).
        private const float ShadowDepth = 600f;
        private const int ShadowCasters = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24);
        private const int Geometry = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24);
        private static Light sun;
        private static float sunLookAt, openAt, open, openTarget = -1f, openLoggedAt = -100f;
        private const float RoofReach = 120f;
        private static string sunScene = "";
        private static Camera shadowCam;
        private static RenderTexture shadowMap, colourMap;
        private static bool colourDrawn;
        private static bool shadowsDrawn;
        private static int drawnLogged;

        private static readonly int ThresholdId = Shader.PropertyToID("_KcThreshold"),
            BloomIntensityId = Shader.PropertyToID("_KcBloomIntensity"), ExposureId = Shader.PropertyToID("_KcExposure"),
            SaturationId = Shader.PropertyToID("_KcSaturation"), ContrastId = Shader.PropertyToID("_KcContrast"),
            WarmthId = Shader.PropertyToID("_KcWarmth"), VignetteId = Shader.PropertyToID("_KcVignette"),
            FilmicId = Shader.PropertyToID("_KcFilmic"), BloomTexId = Shader.PropertyToID("_KcBloom"),
            AoTexId = Shader.PropertyToID("_KcAoTex"), RaysTexId = Shader.PropertyToID("_KcRays"),
            DepthId = Shader.PropertyToID("_KcDepth"), ShadowMapId = Shader.PropertyToID("_KcShadowMap"),
            ZId = Shader.PropertyToID("_KcZ"), TanId = Shader.PropertyToID("_KcTan"), DepthTexelId = Shader.PropertyToID("_KcDepthTexel"),
            CamToWorldId = Shader.PropertyToID("_KcCamToWorld"), WorldToShadowId = Shader.PropertyToID("_KcWorldToShadow"),
            ShadowTexelId = Shader.PropertyToID("_KcShadowTexel"), AoId = Shader.PropertyToID("_KcAo"), ShadowId = Shader.PropertyToID("_KcShadow"),
            SunViewId = Shader.PropertyToID("_KcSunView"), SunColorId = Shader.PropertyToID("_KcSunColor"),
            SunScreenId = Shader.PropertyToID("_KcSunScreen"), RaysColorId = Shader.PropertyToID("_KcRaysColor"),
            FogId = Shader.PropertyToID("_KcFog"), FogColorId = Shader.PropertyToID("_KcFogColor"),
            SourceId = Shader.PropertyToID("_KcSourceCopy"), AoAId = Shader.PropertyToID("_KcAoA"), AoBId = Shader.PropertyToID("_KcAoB"),
            RaysAId = Shader.PropertyToID("_KcRaysA"), RaysBId = Shader.PropertyToID("_KcRaysB"),
            VolId = Shader.PropertyToID("_KcVol"), VolTexId = Shader.PropertyToID("_KcVolTex"),
            VolAId = Shader.PropertyToID("_KcVolA"), VolBId = Shader.PropertyToID("_KcVolB"),
            ShadowColorMapId = Shader.PropertyToID("_KcShadowColorMap"), ShadowSoftId = Shader.PropertyToID("_KcShadowSoft"),
            QualityId = Shader.PropertyToID("_KcQuality"), SceneColorId = Shader.PropertyToID("_KcSceneColor"), SceneDepthId = Shader.PropertyToID("_KcSceneDepth"),
            WaterSunId = Shader.PropertyToID("_KcWaterSun"), WaterSunColorId = Shader.PropertyToID("_KcWaterSunColor"), WaterSkyId = Shader.PropertyToID("_KcWaterSky"),
            WaterHorizonId = Shader.PropertyToID("_KcWaterHorizon"), WaterQId = Shader.PropertyToID("_KcWaterQ"),
            CloudsId = Shader.PropertyToID("_KcClouds"), CloudTexId = Shader.PropertyToID("_KcCloudTex"), CloudRtId = Shader.PropertyToID("_KcCloudRt"), LightId = Shader.PropertyToID("_KcLight"), ExtrasId = Shader.PropertyToID("_KcUltra");
        private static readonly int[] mipIds = new int[8];

        private const int PassPrefilter = 0, PassDown = 1, PassUp = 2, PassComposite = 3, PassAo = 4, PassAoBlurX = 5, PassAoBlurY = 6,
            PassRaysMask = 7, PassRays = 8, PassVol = 9, PassVolBlurX = 10, PassVolBlurY = 11, PassCopyDepth = 12, PassClouds = 13;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Shaders", "Enabled", false, "Killcraft's shaders on. K in the game turns them on and off; with the game paused, a panel on the right sets them.");
            Key = cfg.Bind("Shaders", "ToggleKey", "K", "Key that turns Killcraft's shaders on and off (as Iris's K in Minecraft). A Unity Input System key name.");
            Quality = cfg.Bind("Shaders", "Quality", "High", "Potato, Low, Standard, High, Very High or Ultra: how much the shaders draw (shadow detail and softness, light shafts, coloured light through stained glass). Picking one in the panel also sets the sliders to its look.");
            tier = TierOf(Quality.Value);
            Occlusion = cfg.Bind("Shaders", "AmbientOcclusion", 0.85f, "Soft shade in corners and where things meet, 0 to 1.");
            Shadows = cfg.Bind("Shaders", "SunShadows", 0.8f, "Shadows from the sun, outdoors (things in the way of it, the far sides of things), 0 to 1.");
            GodRays = cfg.Bind("Shaders", "GodRays", 0.8f, "Light streaming from the sun past what's in front of it, 0 to 2.");
            Fog = cfg.Bind("Shaders", "Fog", 0.2f, "Haze with distance (brighter towards the sun), 0 to 1.");
            LightShafts = cfg.Bind("Shaders", "LightShafts", 0.8f, "Sunlight in the air, in shafts between the shadows of trees, pillars and walls (outdoors), 0 to 2.");
            Bloom = cfg.Bind("Shaders", "Bloom", 1f, "Glow round bright things, 0 (none) to 2.");
            Exposure = cfg.Bind("Shaders", "Exposure", 1f, "Overall brightness, 0.5 to 1.5.");
            Saturation = cfg.Bind("Shaders", "Saturation", 1.2f, "Colour, 0 (grey) to 2; 1 as ULTRAKILL.");
            Contrast = cfg.Bind("Shaders", "Contrast", 1.35f, "0.5 (flat) to 2; 1 as ULTRAKILL.");
            Warmth = cfg.Bind("Shaders", "Warmth", 0.65f, "Light warmer and shade cooler, 0 (as ULTRAKILL) to 1.");
            Vignette = cfg.Bind("Shaders", "Vignette", 0.3f, "Darker edges of the screen, 0 to 1.");
            Filmic = cfg.Bind("Shaders", "Filmic", 0.35f, "A film-like tone curve (deeper darks, softer highlights), 0 to 1.");
            settings = new[]
            {
                occlusion = Make(Occlusion, "Ambient occlusion", 0f, 1f),
                shadows = Make(Shadows, "Sun shadows", 0f, 1f),
                godRays = Make(GodRays, "God rays", 0f, 2f),
                lightShafts = Make(LightShafts, "Light shafts", 0f, 2f),
                fog = Make(Fog, "Fog", 0f, 1f),
                bloom = Make(Bloom, "Bloom", 0f, 2f),
                exposure = Make(Exposure, "Exposure", 0.5f, 1.5f),
                saturation = Make(Saturation, "Saturation", 0f, 2f),
                contrast = Make(Contrast, "Contrast", 0.5f, 2f),
                warmth = Make(Warmth, "Warmth", 0f, 1f),
                vignette = Make(Vignette, "Vignette", 0f, 1f),
                filmic = Make(Filmic, "Filmic", 0f, 1f),
            };
            for (int i = 0; i < mipIds.Length; i++)
            {
                mipIds[i] = Shader.PropertyToID("_KcMip" + i);
            }
            on = Enabled.Value;
        }

        private static Setting Make(ConfigEntry<float> e, string name, float min, float max) =>
            new Setting { Entry = e, Name = name, Min = min, Max = max, Value = Mathf.Clamp(e.Value, min, max) };

        private static void Save()
        {
            if (!dirty)
            {
                return;
            }
            dirty = false;
            Enabled.Value = on;
            Quality.Value = T.Name;
            foreach (Setting s in settings)
            {
                s.Entry.Value = s.Value;
            }
        }

        // Each frame (Update): the key, and the shaders on ULTRAKILL's camera or not.
        public static void Frame(bool levelRunning, bool isPaused, bool typing)
        {
            inGame = levelRunning;
            paused = isPaused;
            if (levelRunning && !isPaused && !typing && Pressed())
            {
                on = !on;
                dirty = true;
                Save();
                Plugin.Log.LogInfo($"shaders: {(on ? "on" : "off")}");
            }
            if (panelWasOpen && !(levelRunning && isPaused))
            {
                Save();
            }
            panelWasOpen = levelRunning && isPaused;

            var h = MonoSingleton.GetInstance(typeof(PostProcessV2_Handler)) as PostProcessV2_Handler;
            Camera cam = h != null ? h.mainCam : null;
            RenderTexture main = h != null ? h.mainTex : null, depth = h != null ? h.depthBuffer : null;
            bool want = on && levelRunning && cam != null && main != null && depth != null && Load();
            if (!want || cam != builtCam || main != builtFor || depth != builtDepth || builtTier != tier)
            {
                Detach();
            }
            if (want && cb == null)
            {
                Attach(cam, main, depth);
            }
            if (cb != null)
            {
                post.SetVector(ThresholdId, new Vector4(2.6f, 1.0f, 12f, 0f));
                post.SetFloat(BloomIntensityId, bloom.Value * 0.12f);
                float dark = Darkness();
                post.SetFloat(ExposureId, exposure.Value * (1f + 0.35f * dark));
                post.SetFloat(SaturationId, saturation.Value);
                post.SetFloat(ContrastId, Mathf.Lerp(contrast.Value, Mathf.Min(contrast.Value, 1f), dark));
                post.SetFloat(WarmthId, warmth.Value);
                post.SetFloat(VignetteId, vignette.Value * (1f - 0.75f * dark));
                post.SetFloat(FilmicId, filmic.Value * (1f - 0.5f * dark));
                post.SetVector(AoId, new Vector4(T.Ao ? occlusion.Value * (1f - 0.75f * dark) : 0f, 1.6f, 0f, 0f));
                post.SetVector(DepthTexelId, new Vector4(1f / depth.width, 1f / depth.height, 0f, 0f));
                FindSun();
                Openness(cam);
            }
            if (!hooked)
            {
                hooked = true;
                Camera.onPreRender += BeforeRender;
            }
        }

        // After the cameras have been placed (Host's LateUpdate): the sun's shadow map, round V1.
        public static void LateFrame()
        {
            shadowsDrawn = false;
            colourDrawn = false;
            Tier t = T;
            if (cb == null || !t.Shadows || sun == null || (shadows.Value <= 0.001f && lightShafts.Value <= 0.001f) || open <= 0.001f || builtCam == null || sunDepth == null)
            {
                return;
            }
            if (shadowCam == null)
            {
                var go = new GameObject("Killcraft sun shadows") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                shadowCam = go.AddComponent<Camera>();
                shadowCam.enabled = false;
                shadowCam.orthographic = true;
                shadowCam.aspect = 1f;
                shadowCam.nearClipPlane = 0.05f;
                shadowCam.farClipPlane = ShadowDepth;
                shadowCam.clearFlags = CameraClearFlags.SolidColor;
                shadowCam.backgroundColor = Color.white;
                shadowCam.allowHDR = false;
                shadowCam.allowMSAA = false;
                shadowCam.useOcclusionCulling = false;
                shadowCam.renderingPath = RenderingPath.Forward;
            }
            shadowCam.orthographicSize = t.ShadowHalf;
            if (shadowMap != null && shadowMap.width != t.ShadowSize)
            {
                shadowMap.Release();
                UnityEngine.Object.Destroy(shadowMap);
                shadowMap = null;
            }
            if (shadowMap == null)
            {
                shadowMap = new RenderTexture(t.ShadowSize, t.ShadowSize, 24, RenderTextureFormat.RFloat)
                {
                    name = "Killcraft sun shadows",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                };
                shadowMap.Create();
            }
            shadowCam.targetTexture = shadowMap;
            shadowCam.cullingMask = ShadowCasters;
            // Round the camera, a little ahead of it, stepped a texel at a time (no shimmering edges).
            Transform view = builtCam.transform;
            Vector3 centre = view.position + view.forward * (t.ShadowHalf * 0.4f);
            Quaternion rot = Quaternion.LookRotation(sun.transform.forward, Mathf.Abs(sun.transform.forward.y) > 0.99f ? Vector3.forward : Vector3.up);
            Vector3 local = Quaternion.Inverse(rot) * centre;
            float texel = 2f * t.ShadowHalf / t.ShadowSize;
            local.x = Mathf.Floor(local.x / texel) * texel;
            local.y = Mathf.Floor(local.y / texel) * texel;
            centre = rot * local;
            shadowCam.transform.SetPositionAndRotation(centre - sun.transform.forward * (ShadowDepth * 0.5f), rot);
            Matrix4x4 toMap = Matrix4x4.TRS(new Vector3(0.5f, 0.5f, 0f), Quaternion.identity, new Vector3(0.5f / t.ShadowHalf, 0.5f / t.ShadowHalf, 1f / ShadowDepth))
                * shadowCam.transform.worldToLocalMatrix;
            Shader.SetGlobalMatrix(WorldToShadowId, toMap);
            bool outline = WorldRender.HideForShadows();
            HideEffects();
            try
            {
                shadowCam.RenderWithShader(sunDepth, "");
                shadowsDrawn = true;
                if (t.Coloured && sunColor != null)
                {
                    if (colourMap != null && colourMap.width != t.ColourSize)
                    {
                        colourMap.Release();
                        UnityEngine.Object.Destroy(colourMap);
                        colourMap = null;
                    }
                    if (colourMap == null)
                    {
                        colourMap = new RenderTexture(t.ColourSize, t.ColourSize, 24, RenderTextureFormat.ARGBHalf)
                        {
                            name = "Killcraft sun colour",
                            filterMode = FilterMode.Point,
                            wrapMode = TextureWrapMode.Clamp,
                            useMipMap = false,
                        };
                        colourMap.Create();
                    }
                    shadowCam.targetTexture = colourMap;
                    shadowCam.RenderWithShader(sunColor, "");
                    colourDrawn = true;
                }
                if (drawnLogged != tier + 1)
                {
                    drawnLogged = tier + 1;
                    Plugin.Log.LogInfo($"shaders: sun shadow map drawn ({t.Name}: {t.ShadowSize}x{t.ShadowSize}, {2f * t.ShadowHalf} units across"
                        + $"{(colourDrawn ? $", coloured light through glass ({t.ColourSize}x{t.ColourSize})" : "")})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"shaders: sun shadows couldn't be drawn: {e.Message}");
                shadows.Value = 0f;
            }
            finally
            {
                shadowCam.targetTexture = shadowMap;
                if (outline)
                {
                    WorldRender.ShowAfterShadows();
                }
                ShowEffects();
            }
            post.SetTexture(ShadowMapId, shadowMap);
            post.SetTexture(ShadowColorMapId, colourDrawn ? colourMap : (Texture)Texture2D.whiteTexture);
            post.SetVector(ShadowTexelId, new Vector4(1f / t.ShadowSize, 0.0015f, t.Soft, 0f));
            post.SetVector(ShadowSoftId, new Vector4(t.Taps, t.Pcss ? 1f : 0f, ShadowDepth, t.ShadowSize / (2f * t.ShadowHalf)));
        }

        // Just before the main camera draws: where it is and how it sees, as it draws.
        private static void BeforeRender(Camera c)
        {
            if (c == null || c != builtCam || post == null)
            {
                return;
            }
            float far = c.farClipPlane, near = c.nearClipPlane;
            float zx = SystemInfo.usesReversedZBuffer ? -1f + far / near : 1f - far / near;
            float zy = SystemInfo.usesReversedZBuffer ? 1f : far / near;
            post.SetVector(ZId, new Vector4(zx, zy, zx / far, zy / far));
            float tanY = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
            post.SetVector(TanId, new Vector4(tanY * c.aspect, tanY, far, 0f));
            post.SetMatrix(CamToWorldId, Matrix4x4.TRS(c.transform.position, c.transform.rotation, Vector3.one));

            Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
            Vector3 sunView = c.transform.InverseTransformDirection(toSun);
            post.SetVector(SunViewId, sunView);
            Color sc = sun != null ? sun.color * Mathf.Clamp(sun.intensity, 0f, 2f) : Color.black;
            Vector3 sunLinear = new Vector3(Mathf.GammaToLinearSpace(sc.r), Mathf.GammaToLinearSpace(sc.g), Mathf.GammaToLinearSpace(sc.b));
            post.SetVector(SunColorId, sunLinear);
            float shadow = shadowsDrawn ? shadows.Value * open : 0f;
            post.SetVector(ShadowId, new Vector4(shadow, 0f, 0f, 0f));
            post.SetVector(VolId, new Vector4(shadowsDrawn && T.Vol ? lightShafts.Value * open : 0f, 0f, 0f, 0f));
            post.SetVector(QualityId, new Vector4(T.AoSamples, T.VolSteps, T.VolReach, colourDrawn ? 1f : 0f));
            post.SetVector(LightId, T.Light);
            post.SetVector(ExtrasId, T.Extras);
            post.SetVector(CloudsId, new Vector4(sun != null ? T.CloudSteps : 0f, 1f, 0f, 0f));

            // The sun on screen (rays only while it's ahead, fading as it goes off to the side).
            float ahead = sun != null ? Mathf.Clamp01(sunView.z * 3f) : 0f;
            Vector2 screen = sunView.z > 0.01f ? new Vector2(0.5f + 0.5f * sunView.x / sunView.z / (tanY * c.aspect), 0.5f + 0.5f * sunView.y / sunView.z / tanY) : new Vector2(0.5f, 0.5f);
            post.SetVector(SunScreenId, new Vector4(screen.x, screen.y, ahead, 0f));
            post.SetVector(RaysColorId, sunLinear * (godRays.Value * ahead * 0.5f));

            Color fc = RenderSettings.fog ? RenderSettings.fogColor : (Color)(RenderSettings.ambientSkyColor.maxColorComponent > 0.01f ? RenderSettings.ambientSkyColor : new Color(0.6f, 0.65f, 0.75f));
            post.SetVector(FogColorId, new Vector3(Mathf.GammaToLinearSpace(fc.r), Mathf.GammaToLinearSpace(fc.g), Mathf.GammaToLinearSpace(fc.b)));
            // (Over ULTRAKILL's own fog: a dark level's black fog doubled made it darker still.)
            float fogLight = Mathf.Clamp01(Mathf.Max(fc.r, Mathf.Max(fc.g, fc.b)) * 3f);
            post.SetVector(FogId, new Vector4(fog.Value * 0.003f * fogLight * (1f - Darkness()), sun != null ? 0.6f : 0f, 0f, 0f));
            if (cbWater != null)
            {
                Vector3 horizon = new Vector3(Mathf.GammaToLinearSpace(fc.r), Mathf.GammaToLinearSpace(fc.g), Mathf.GammaToLinearSpace(fc.b));
                Shader.SetGlobalVector(WaterSunId, new Vector4(toSun.x, toSun.y, toSun.z, sun != null ? 1f : 0f));
                Shader.SetGlobalVector(WaterSunColorId, sunLinear);
                Shader.SetGlobalVector(WaterHorizonId, horizon);
                Shader.SetGlobalVector(WaterSkyId, Vector3.Lerp(horizon, new Vector3(0.12f, 0.27f, 0.62f), 0.6f));
                Shader.SetGlobalVector(WaterQId, new Vector4(T.Reflections, 1f, 0f, 0f));
            }
        }

        // The level's sun: its sun, or its brightest directional light (looked for again now and then:
        // lights come on as a level goes).
        private static void FindSun()
        {
            string scene = SceneHelper.CurrentScene ?? "";
            if (scene == sunScene && Time.unscaledTime < sunLookAt && (sun == null || sun.isActiveAndEnabled))
            {
                return;
            }
            sunScene = scene;
            sunLookAt = Time.unscaledTime + 3f;
            Light best = RenderSettings.sun != null && RenderSettings.sun.isActiveAndEnabled && RenderSettings.sun.type == LightType.Directional ? RenderSettings.sun : null;
            if (best == null)
            {
                foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (l.type == LightType.Directional && l.isActiveAndEnabled && l.intensity > 0.05f && (best == null || l.intensity > best.intensity))
                    {
                        best = l;
                    }
                }
            }
            if (best != sun)
            {
                Plugin.Log.LogInfo(best != null ? $"shaders: the sun is {best.name} ({best.color}, {best.intensity:0.00})" : "shaders: no sun in this level (no shadows or rays from one)");
            }
            sun = best;
        }

        // Whether the camera's out under the sky: sun shadows only there (indoors, under a roof, all
        // of it would be in the roof's shadow). Out in one of the level's outdoor areas, or open to
        // the sun and the sky above.
        private static void Openness(Camera cam)
        {
            if (Time.unscaledTime >= openAt)
            {
                openAt = Time.unscaledTime + 0.25f;
                Vector3 p = cam.transform.position;
                bool zone = BlockLight.Outdoors(p);
                // (Only a roof near counts: some levels' skies are a big model round them, which a ray
                // any way out always hit, and shadows were never on.)
                bool toSun = sun != null && !Physics.Raycast(p, -sun.transform.forward, RoofReach, Geometry, QueryTriggerInteraction.Ignore);
                bool up = !Physics.Raycast(p, Vector3.up, RoofReach, Geometry, QueryTriggerInteraction.Ignore);
                float was = openTarget;
                openTarget = sun == null ? 0f : zone || (toSun && up) ? 1f : toSun || up ? 0.5f : 0f;
                if (openTarget != was && Time.unscaledTime - openLoggedAt > 10f)
                {
                    openLoggedAt = Time.unscaledTime;
                    Plugin.Log.LogInfo($"shaders: sun shadows {(openTarget >= 1f ? "on" : openTarget > 0f ? "half" : "off")} here (sun {(sun != null ? sun.name : "none")}, "
                        + $"outdoor area {zone}, open to the sun {toSun}, open above {up})");
                }
            }
            open = Mathf.MoveTowards(open, openTarget, Time.unscaledDeltaTime * 1.5f);
        }

        private static bool Pressed()
        {
            if (key == UnityEngine.InputSystem.Key.None
                && (!Enum.TryParse(Key.Value, true, out key) || key == UnityEngine.InputSystem.Key.None))
            {
                key = UnityEngine.InputSystem.Key.K;
            }
            if (Typing.Active)
            {
                return false;
            }
            foreach (UnityEngine.InputSystem.InputDevice device in UnityEngine.InputSystem.InputSystem.devices)
            {
                if (device is UnityEngine.InputSystem.Keyboard kb && kb.enabled && kb[key] is var control && control != null && control.wasPressedThisFrame)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Load()
        {
            if (post != null)
            {
                return true;
            }
            if (loadTried)
            {
                return false;
            }
            loadTried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), BundleName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"shaders: no {BundleName} next to the plugin: off");
                return false;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.Log.LogWarning($"shaders: {BundleName} didn't load: off");
                return false;
            }
            Shader postShader = null;
            foreach (Shader s in bundle.LoadAllAssets<Shader>())
            {
                if (s.name == PostName)
                {
                    postShader = s;
                }
                else if (s.name == SunDepthName)
                {
                    sunDepth = s;
                }
                else if (s.name == SunColorName)
                {
                    sunColor = s;
                }
                else if (s.name == WaterName)
                {
                    water = s;
                }
            }
            if (postShader == null || !postShader.isSupported)
            {
                Plugin.Log.LogWarning($"shaders: {PostName} {(postShader == null ? "isn't in the bundle" : "isn't supported here")}: off");
                return false;
            }
            if (sunDepth != null && !sunDepth.isSupported)
            {
                sunDepth = null;
            }
            if (sunColor != null && !sunColor.isSupported)
            {
                sunColor = null;
            }
            if (water != null && !water.isSupported)
            {
                water = null;
            }
            post = new Material(postShader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            post.SetTexture(ShadowMapId, Texture2D.whiteTexture);
            post.SetTexture(ShadowColorMapId, Texture2D.whiteTexture);
            Plugin.Log.LogInfo($"shaders: loaded ({PostName}{(sunDepth != null ? ", " + SunDepthName : ", no sun shadows")}{(sunColor != null ? ", " + SunColorName : ", no coloured light")}{(water != null ? ", " + WaterName : ", no water")})");
            return true;
        }

        // After the main camera has drawn the world (and ULTRAKILL its outlines): the picture copied;
        // its occlusion (half size) and the sun's rays (a quarter) worked out from it and its depth; its
        // bright parts halved down a chain and back up as the glow; and the picture with all of it,
        // graded, put back.
        private static void Attach(Camera cam, RenderTexture main, RenderTexture depth)
        {
            int w = main.width, h = main.height;
            cb = new CommandBuffer { name = "Killcraft shaders" };
            cb.SetGlobalTexture(DepthId, new RenderTargetIdentifier(depth), RenderTextureSubElement.Depth);
            cb.GetTemporaryRT(SourceId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGB32);
            cb.Blit(main, SourceId);
            if (lumRt == null)
            {
                lumRt = new RenderTexture(32, 18, 0, RenderTextureFormat.ARGB32) { name = "Killcraft brightness", filterMode = FilterMode.Bilinear };
                lumRt.Create();
            }
            cb.Blit(SourceId, lumRt);

            Tier t = T;
            int hw = Mathf.Max(1, w / 2), hh = Mathf.Max(1, h / 2);
            if (t.Ao)
            {
                cb.GetTemporaryRT(AoAId, hw, hh, 0, FilterMode.Bilinear, RenderTextureFormat.RGHalf);
                cb.GetTemporaryRT(AoBId, hw, hh, 0, FilterMode.Bilinear, RenderTextureFormat.RGHalf);
                cb.Blit(SourceId, AoAId, post, PassAo);
                cb.Blit(AoAId, AoBId, post, PassAoBlurX);
                cb.Blit(AoBId, AoAId, post, PassAoBlurY);
                cb.SetGlobalTexture(AoTexId, AoAId);
            }
            else
            {
                cb.SetGlobalTexture(AoTexId, Texture2D.whiteTexture);
            }
            if (t.Vol)
            {
                cb.GetTemporaryRT(VolAId, hw, hh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                cb.GetTemporaryRT(VolBId, hw, hh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                cb.Blit(SourceId, VolAId, post, PassVol);
                cb.Blit(VolAId, VolBId, post, PassVolBlurX);
                cb.Blit(VolBId, VolAId, post, PassVolBlurY);
                cb.SetGlobalTexture(VolTexId, VolAId);
            }
            else
            {
                cb.SetGlobalTexture(VolTexId, Texture2D.blackTexture);
            }

            if (t.CloudSteps > 0)
            {
                cb.GetTemporaryRT(CloudRtId, hw, hh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                cb.Blit(SourceId, CloudRtId, post, PassClouds);
                cb.SetGlobalTexture(CloudTexId, CloudRtId);
            }
            int qw = Mathf.Max(1, w / 4), qh = Mathf.Max(1, h / 4);
            cb.GetTemporaryRT(RaysAId, qw, qh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cb.GetTemporaryRT(RaysBId, qw, qh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cb.Blit(SourceId, RaysAId, post, PassRaysMask);
            cb.Blit(RaysAId, RaysBId, post, PassRays);
            cb.SetGlobalTexture(RaysTexId, RaysBId);

            int n = 0;
            int mw = w / 2, mh = h / 2;
            while (n < mipIds.Length && mw >= 2 && mh >= 2)
            {
                cb.GetTemporaryRT(mipIds[n], mw, mh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                n++;
                mw /= 2;
                mh /= 2;
            }
            cb.Blit(SourceId, mipIds[0], post, PassPrefilter);
            for (int i = 1; i < n; i++)
            {
                cb.Blit(mipIds[i - 1], mipIds[i], post, PassDown);
            }
            for (int i = n - 2; i >= 0; i--)
            {
                cb.Blit(mipIds[i + 1], mipIds[i], post, PassUp);
            }
            cb.SetGlobalTexture(BloomTexId, mipIds[0]);

            cb.Blit(SourceId, main, post, PassComposite);
            for (int i = 0; i < n; i++)
            {
                cb.ReleaseTemporaryRT(mipIds[i]);
            }
            cb.ReleaseTemporaryRT(RaysAId);
            cb.ReleaseTemporaryRT(RaysBId);
            if (t.Ao)
            {
                cb.ReleaseTemporaryRT(AoAId);
                cb.ReleaseTemporaryRT(AoBId);
            }
            if (t.Vol)
            {
                cb.ReleaseTemporaryRT(VolAId);
                cb.ReleaseTemporaryRT(VolBId);
            }
            if (t.CloudSteps > 0)
            {
                cb.ReleaseTemporaryRT(CloudRtId);
            }
            cb.ReleaseTemporaryRT(SourceId);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
            if (t.Water && water != null)
            {
                sceneColor = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Killcraft scene for water", filterMode = FilterMode.Bilinear };
                sceneDepth = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat) { name = "Killcraft scene depth for water", filterMode = FilterMode.Point };
                sceneColor.Create();
                sceneDepth.Create();
                cbWater = new CommandBuffer { name = "Killcraft water: the scene so far" };
                cbWater.Blit(main, sceneColor);
                cbWater.SetGlobalTexture(DepthId, new RenderTargetIdentifier(depth), RenderTextureSubElement.Depth);
                cbWater.Blit(main, sceneDepth, post, PassCopyDepth);
                cbWater.SetRenderTarget(new RenderTargetIdentifier(main), new RenderTargetIdentifier(depth));
                cbWater.SetGlobalTexture(SceneColorId, sceneColor);
                cbWater.SetGlobalTexture(SceneDepthId, sceneDepth);
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cbWater);
                WorldRender.UseWater(water);
            }
            else
            {
                WorldRender.UseWater(null);
            }
            builtCam = cam;
            builtFor = main;
            builtDepth = depth;
            builtTier = tier;
            Plugin.Log.LogInfo($"shaders: on ULTRAKILL's picture ({w}x{h}, {n} bloom levels, {t.Name})");
        }

        // ---- dark levels (6-2, ...): their picture's brightness, measured ----

        private static RenderTexture lumRt;
        private static bool lumPending;
        private static float lumAt, lumAverage = 0.3f, darkShown;

        // 0 in a lit level, up to 1 in a very dark one: the shaders' darkening there eased off
        // (occlusion, contrast, vignette, filmic tones, fog), the exposure up a little.
        private static float Darkness()
        {
            if (lumRt != null && cb != null && !lumPending && Time.unscaledTime > lumAt && SystemInfo.supportsAsyncGPUReadback)
            {
                lumPending = true;
                lumAt = Time.unscaledTime + 0.5f;
                try
                {
                    AsyncGPUReadback.Request(lumRt, 0, TextureFormat.RGBA32, req =>
                    {
                        lumPending = false;
                        if (req.hasError)
                        {
                            return;
                        }
                        var data = req.GetData<Color32>();
                        if (data.Length == 0)
                        {
                            return;
                        }
                        float sum = 0f;
                        for (int i = 0; i < data.Length; i++)
                        {
                            Color32 c = data[i];
                            sum += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        }
                        lumAverage = sum / (data.Length * 255f);
                    });
                }
                catch
                {
                    lumPending = false;
                }
            }
            float want = Mathf.Clamp01((0.24f - lumAverage) / 0.16f);
            darkShown = Mathf.MoveTowards(darkShown, want, Time.unscaledDeltaTime * 0.6f);
            return darkShown;
        }

        // ---- not in the sun's shadows: effects (V1's dash and slide lines, particles, trails) ----

        private static readonly List<Renderer> effects = new List<Renderer>(), hiddenEffects = new List<Renderer>();
        private static float effectsAt = -100f;

        private static void HideEffects()
        {
            try
            {
                if (Time.unscaledTime - effectsAt > 2f)
                {
                    effectsAt = Time.unscaledTime;
                    effects.Clear();
                    effects.AddRange(UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>());
                    effects.AddRange(UnityEngine.Object.FindObjectsOfType<TrailRenderer>());
                    effects.AddRange(UnityEngine.Object.FindObjectsOfType<LineRenderer>());
                }
                foreach (Renderer r in effects)
                {
                    Hide(r);
                }
                // (V1's own, each time: what's see-through on it and its camera, the lines among them.)
                var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
                if (nm != null)
                {
                    foreach (Renderer r in nm.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer
                            || (r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 2500))
                        {
                            Hide(r);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void Hide(Renderer r)
        {
            if (r != null && r.enabled && !r.forceRenderingOff)
            {
                r.forceRenderingOff = true;
                hiddenEffects.Add(r);
            }
        }

        private static void ShowEffects()
        {
            foreach (Renderer r in hiddenEffects)
            {
                if (r != null)
                {
                    r.forceRenderingOff = false;
                }
            }
            hiddenEffects.Clear();
        }

        private static void Detach()
        {
            if (cb != null && builtCam != null)
            {
                builtCam.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
            }
            cb?.Release();
            cb = null;
            if (cbWater != null && builtCam != null)
            {
                builtCam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, cbWater);
            }
            cbWater?.Release();
            cbWater = null;
            if (sceneColor != null)
            {
                sceneColor.Release();
                UnityEngine.Object.Destroy(sceneColor);
                sceneColor = null;
            }
            if (sceneDepth != null)
            {
                sceneDepth.Release();
                UnityEngine.Object.Destroy(sceneDepth);
                sceneDepth = null;
            }
            WorldRender.UseWater(null);
            builtCam = null;
            builtFor = null;
            builtDepth = null;
            builtTier = -1;
            shadowsDrawn = false;
            colourDrawn = false;
        }

        // ---- for Killcraft's page in ULTRAKILL's options (OptionsTab) ----

        internal static bool On
        {
            get => on;
            set
            {
                if (on != value)
                {
                    on = value;
                    dirty = true;
                    Plugin.Log.LogInfo($"shaders: {(on ? "on" : "off")}");
                }
            }
        }

        internal static string[] TierNames
        {
            get
            {
                var names = new string[tiers.Length];
                for (int i = 0; i < tiers.Length; i++)
                {
                    names[i] = tiers[i].Name;
                }
                return names;
            }
        }

        // Picking a preset also puts the sliders to its look.
        internal static int TierIndex
        {
            get => tier;
            set
            {
                if (value >= 0 && value < tiers.Length && value != tier)
                {
                    tier = value;
                    UseLook();
                    Plugin.Log.LogInfo($"shaders: {T.Name}");
                }
            }
        }

        internal static int SettingCount => settings != null ? settings.Length : 0;
        internal static string SettingName(int i) => settings[i].Name;
        internal static float SettingMin(int i) => settings[i].Min;
        internal static float SettingMax(int i) => settings[i].Max;
        internal static float GetSetting(int i) => settings[i].Value;

        internal static void SetSetting(int i, float v)
        {
            v = Mathf.Clamp(Mathf.Round(v * 100f) / 100f, settings[i].Min, settings[i].Max);
            if (Mathf.Abs(v - settings[i].Value) > 0.0001f)
            {
                settings[i].Value = v;
                dirty = true;
            }
        }

        internal static void ResetLook() => UseLook();

        internal static void SaveNow() => Save();

        // ---- the panel, with the game paused ----

        private static GUIStyle title, label, check;
        private static Texture2D back;

        // The game's paused in a level: the pause menu's panels are up (this one, Killcraft's options).
        internal static bool PanelsShown => inGame && paused && settings != null;

        public static void Gui()
        {
            if (!inGame || !paused || settings == null)
            {
                return;
            }
            title ??= new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            label ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            check ??= new GUIStyle(GUI.skin.toggle) { fontSize = 14 };
            if (back == null)
            {
                back = new Texture2D(1, 1);
                back.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
                back.Apply();
            }
            const float rowH = 40f;
            float w = 320f, x = Screen.width - w - 20f, y = 60f;
            float height = 36f + 32f + 24f + 2f * 32f + settings.Length * rowH + 40f;
            GUI.DrawTexture(new Rect(x - 10f, y - 10f, w + 20f, height + 10f), back);
            GUI.Label(new Rect(x, y, w, 30f), "Killcraft shaders", title);
            y += 36f;
            bool now = GUI.Toggle(new Rect(x, y, w, 24f), on, $" On  ({Key.Value} in the game)", check);
            if (now != on)
            {
                on = now;
                dirty = true;
                Plugin.Log.LogInfo($"shaders: {(on ? "on" : "off")}");
            }
            y += 32f;
            GUI.Label(new Rect(x, y, w, 20f), $"Quality: {T.Name}", label);
            y += 24f;
            float bw = (w - 8f) / 3f;
            for (int i = 0; i < tiers.Length; i++)
            {
                var r = new Rect(x + (i % 3) * (bw + 4f), y + (i / 3) * 32f, bw, 28f);
                string text = i == tier ? $"> {tiers[i].Name} <" : tiers[i].Name;
                if (GUI.Button(r, text) && i != tier)
                {
                    tier = i;
                    UseLook();
                    Plugin.Log.LogInfo($"shaders: {T.Name}");
                }
            }
            y += 2f * 32f;
            foreach (Setting s in settings)
            {
                GUI.Label(new Rect(x, y, w, 20f), $"{s.Name}: {s.Value:0.00}", label);
                float v = GUI.HorizontalSlider(new Rect(x, y + 20f, w, 16f), s.Value, s.Min, s.Max);
                if (Mathf.Abs(v - s.Value) > 0.0001f)
                {
                    s.Value = Mathf.Round(v * 100f) / 100f;
                    dirty = true;
                }
                y += rowH;
            }
            if (GUI.Button(new Rect(x, y + 4f, 180f, 28f), $"Reset to {T.Name}'s look"))
            {
                UseLook();
            }
        }

        // The sliders as the preset has them.
        private static void UseLook()
        {
            float[] look = T.Look;
            for (int i = 0; i < settings.Length && i < look.Length; i++)
            {
                settings[i].Value = Mathf.Clamp(look[i], settings[i].Min, settings[i].Max);
            }
            dirty = true;
        }
    }
}
