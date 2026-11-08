using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's light on Minecraft's blocks and items, as its own walls get it.
    //
    // A level's walls are drawn by ULTRAKILL's stationary shader (Hidden/ULTRAKILL/ULTRAKILL-Stationary,
    // fed by its StaticSceneOptimizer), which lights each vertex itself: the level's ambient, plus every
    // one of the level's lights near it (StaticSceneOptimizer.globalLights), each
    //   colour x intensity x 1 / (1 + 25 d^2 / range^2) x spot cone x fade to nothing at twice its range
    //   x the surface facing it (N.L),
    // and the wall's texture times that (above 1 is brighter than the texture). Blocks aren't walls of
    // the level (Unity gives them only its few nearest lights, dimmer), so the same sum is worked out
    // here for each of their vertices and put in their vertex colours, from the same lights with the
    // same numbers; outdoors (in one of the level's OutdoorsZones) with the lights the outdoors gets.
    // The level's lights change (switched on, off, flickering): blocks are lit again then.
    internal static class BlockLight
    {
        private struct L
        {
            public Vector3 Pos, Dir;      // (directional: Pos is towards the light)
            public Vector3 Color;
            public float SpotCos, SpotScale, Range;
            public bool Directional, Indoors, Outdoors;
        }

        private static readonly List<L> lights = new List<L>();
        private static readonly List<Light> source = new List<Light>();
        private static readonly List<Collider> outdoorZones = new List<Collider>();
        private static Vector3 ambient;
        private static float checkAt, collectAt;
        private static int stateHash;
        private static string scene = "";
        private static bool logged, loggedLights;

        // Lit this way (an ULTRAKILL level with lights); otherwise Minecraft's own light levels light them.
        public static bool Active { get; private set; }

        // Goes up whenever the level's lights change: what was lit before is lit again.
        public static int Version { get; private set; }

        // Each frame: the level's lights, as they are now (checked four times a second).
        public static void Frame()
        {
            // (ULTRAKILL's own name for the level: Unity's for its scene isn't it.)
            string now = SceneHelper.CurrentScene ?? "";
            if (now != scene)
            {
                scene = now;
                logged = false;
                loggedLights = false;
                darkLogged = false;
                source.Clear();
                outdoorZones.Clear();
                checkAt = 0f;
                Collect();
            }
            if (Time.unscaledTime < checkAt)
            {
                return;
            }
            checkAt = Time.unscaledTime + 0.25f;
            // (None yet: the level's still starting, or it has none (the sandbox): looked for again now and then.)
            if ((source.Count == 0 && Time.unscaledTime >= collectAt) || (source.Count > 0 && source[0] == null))
            {
                collectAt = Time.unscaledTime + 2f;
                Collect();
            }
            Snapshot();
        }

        private static void Collect()
        {
            source.Clear();
            var opt = MonoSingleton.GetInstance(typeof(StaticSceneOptimizer)) as StaticSceneOptimizer;
            // (The sandbox has the set-up and its lights, but no ambient, and its walls show as their
            // textures: blocks lit from its lights were black away from them. Minecraft's own light there.)
            bool sandbox = scene == "uk_construct";
            if (!sandbox && opt != null && opt.globalLights != null && opt.globalLights.Length > 0)
            {
                foreach (Light l in opt.globalLights)
                {
                    if (l != null)
                    {
                        source.Add(l);
                    }
                }
            }
            // (A level without ULTRAKILL's baked set-up, the sandbox for one: its walls aren't lit this
            // way (the sandbox's are as their textures), so neither are blocks: Minecraft's own light.)
            outdoorZones.Clear();
            foreach (OutdoorsZone z in Object.FindObjectsOfType<OutdoorsZone>())
            {
                foreach (Collider c in z.GetComponentsInChildren<Collider>())
                {
                    outdoorZones.Add(c);
                }
            }
            if (!logged || (source.Count > 0 && !loggedLights) || Plugin.Diagnostics.Value)
            {
                logged = true;
                loggedLights = source.Count > 0;
                int dir = 0, outd = 0, ind = 0;
                foreach (Light l in source)
                {
                    dir += l.type == LightType.Directional ? 1 : 0;
                    outd += (l.cullingMask & (1 << 24 | 1 << 6)) != 0 ? 1 : 0;
                    ind += (l.cullingMask & (1 << 8 | 1 << 7)) != 0 ? 1 : 0;
                }
                if (source.Count > 0)
                {
                    Plugin.Log.LogInfo($"render: level lights: {dir} directional, {outd} for outdoors, {ind} for indoors, {source.Count - outd - ind} neither (sun: {(RenderSettings.sun != null ? RenderSettings.sun.name + " mask " + RenderSettings.sun.cullingMask.ToString("X") : "none")})");
                }
                Plugin.Log.LogInfo($"render: blocks lit as ULTRAKILL lights its walls ({source.Count} level lights{(opt != null ? "" : ", no StaticSceneOptimizer")}{(sandbox ? ", the sandbox: Minecraft's light" : "")}, "
                    + $"{outdoorZones.Count} outdoor zones, ambient {RenderSettings.ambientLight})");
            }
        }

        private static void Snapshot()
        {
            lights.Clear();
            int hash = 17;
            foreach (Light l in source)
            {
                if (l == null || !l.isActiveAndEnabled)
                {
                    hash = hash * 31 + 1;
                    continue;
                }
                Transform t = l.transform;
                var x = new L
                {
                    Directional = l.type == LightType.Directional,
                    Color = new Vector3(l.color.r, l.color.g, l.color.b) * l.intensity,
                    Indoors = (l.cullingMask & (1 << 8 | 1 << 7)) != 0,
                    Outdoors = (l.cullingMask & (1 << 24 | 1 << 6)) != 0,
                };
                bool spot = l.type == LightType.Spot;
                float angle = l.spotAngle * Mathf.Deg2Rad;
                x.SpotCos = spot ? Mathf.Cos(angle / 2f) : -1f;
                x.SpotScale = spot ? 1f / (Mathf.Cos(angle / 4f) - Mathf.Cos(angle / 2f)) : 1f;
                x.Range = x.Directional ? 0f : l.range;
                x.Dir = -t.forward;
                x.Pos = x.Directional ? -t.forward : t.position;
                lights.Add(x);
                hash = hash * 31 + Mathf.RoundToInt(x.Pos.x * 4f);
                hash = hash * 31 + Mathf.RoundToInt(x.Pos.y * 4f);
                hash = hash * 31 + Mathf.RoundToInt(x.Pos.z * 4f);
                hash = hash * 31 + Mathf.RoundToInt(x.Color.x * 64f) + 7 * Mathf.RoundToInt(x.Color.y * 64f) + 13 * Mathf.RoundToInt(x.Color.z * 64f);
                hash = hash * 31 + Mathf.RoundToInt(x.Range * 8f);
            }
            Color a = RenderSettings.ambientLight;
            ambient = new Vector3(a.r, a.g, a.b);
            hash = hash * 31 + Mathf.RoundToInt(a.r * 255f) + 7 * Mathf.RoundToInt(a.g * 255f) + 13 * Mathf.RoundToInt(a.b * 255f);
            Active = lights.Count > 0;
            if (hash != stateHash)
            {
                stateHash = hash;
                Version++;
            }
        }

        // Whether a place is in one of the level's outdoor areas.
        public static bool Outdoors(Vector3 p)
        {
            foreach (Collider c in outdoorZones)
            {
                if (c != null && c.enabled && c.bounds.Contains(p) && (c.ClosestPoint(p) - p).sqrMagnitude < 0.0001f)
                {
                    return true;
                }
            }
            return false;
        }

        // The level's lights that can reach anything within a sphere (a section, a frame's things).
        public static void Near(Vector3 centre, float radius, bool outdoors, List<int> into) => Near(centre, radius, outdoors ? Where.Outdoors : Where.Indoors, into);

        public enum Where { Indoors, Outdoors, Both }

        // Where a place is, for which of the level's lights reach it: out in one of its outdoor areas;
        // else under open sky, or on its outdoor ground, both its indoor and outdoor lights (not every
        // open courtyard is one of its outdoor areas: blocks there were black, out of the sun); else in.
        public static Where Place(Vector3 p)
        {
            if (Outdoors(p))
            {
                return Where.Outdoors;
            }
            if (Ground(p, Vector3.up, 300f) < 0)
            {
                return Where.Both;
            }
            // (ULTRAKILL moves its outdoor walls and floors from layer 24 to 6 as a level starts, its
            // indoor ones from 8 to 7: on either outdoor layer, or over nothing, both.)
            int below = Ground(p, Vector3.down, 40f);
            return below == 24 || below == 6 || below < 0 ? Where.Both : Where.Indoors;
        }

        private const int LevelGeometry = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24);
        private static readonly RaycastHit[] hits = new RaycastHit[16];

        // The layer of the level's nearest surface that way (not Killcraft's blocks), or -1: none.
        private static int Ground(Vector3 p, Vector3 dir, float range)
        {
            int n = Physics.RaycastNonAlloc(p, dir, hits, range, LevelGeometry, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            int layer = -1;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || hits[i].distance >= best || WorldRender.IsOurs(c))
                {
                    continue;
                }
                best = hits[i].distance;
                layer = c.gameObject.layer;
            }
            return layer;
        }

        private static bool darkLogged;

        // Once a level: blocks lit nearly black (to see why in the log).
        public static void Dark(Vector3 centre, Where where, List<int> near, Vector3 light)
        {
            if (darkLogged || lights.Count == 0)
            {
                return;
            }
            darkLogged = true;
            var names = new System.Text.StringBuilder();
            foreach (int i in near)
            {
                L l = lights[i];
                names.Append(l.Directional ? "dir" : "point").Append(l.Outdoors ? "/out" : "").Append(l.Indoors ? "/in" : "").Append(' ');
            }
            Plugin.Log.LogInfo($"render: blocks at {centre} lit dark ({light}): {where}, {near.Count} of {lights.Count} lights near ({names}), ambient {ambient}, ground below layer {Ground(centre, Vector3.down, 40f)}, above layer {Ground(centre, Vector3.up, 300f)}");
        }

        public static void Near(Vector3 centre, float radius, Where where, List<int> into)
        {
            into.Clear();
            for (int i = 0; i < lights.Count; i++)
            {
                L l = lights[i];
                if (where == Where.Outdoors ? !l.Outdoors : where == Where.Indoors ? !l.Indoors : !(l.Outdoors || l.Indoors))
                {
                    continue;
                }
                if (!l.Directional)
                {
                    float reach = 2f * l.Range + radius;
                    if ((l.Pos - centre).sqrMagnitude > reach * reach)
                    {
                        continue;
                    }
                }
                into.Add(i);
            }
        }

        // How much of a light reaches a face turned away from it. ULTRAKILL's walls are lit by lights
        // all round them (rarely wholly turned away); a block standing out in a room isn't, and by N.L
        // alone its far faces went black: a part of the light around reaches every face.
        private const float Around = 0.25f;

        // ULTRAKILL's light at a point on a surface (ambient included), from the lights Near found.
        // Two-sided (plants, items' flat quads): lit from whichever side the light is.
        public static Vector3 At(Vector3 p, Vector3 n, bool twoSided, List<int> near) => At(p, n, twoSided, near, false);

        // The light around a point, every way at once (the first-person hand's).
        public static Vector3 Omni(Vector3 p, List<int> near) => At(p, Vector3.up, true, near, true);

        private static Vector3 At(Vector3 p, Vector3 n, bool twoSided, List<int> near, bool omni)
        {
            Vector3 sum = ambient;
            for (int k = 0; k < near.Count; k++)
            {
                L l = lights[near[k]];
                Vector3 toLight;
                float atten = 1f;
                if (l.Directional)
                {
                    toLight = l.Pos;
                }
                else
                {
                    Vector3 d = l.Pos - p;
                    float d2 = Mathf.Max(d.sqrMagnitude, 0.000001f);
                    float range = Mathf.Max(l.Range, 0.000001f);
                    float dist = Mathf.Sqrt(d2);
                    if (dist > 2f * range)
                    {
                        continue;
                    }
                    toLight = d / dist;
                    atten = 1f / (1f + d2 * 25f / (range * range));
                    float cone = Mathf.Clamp01((Mathf.Max(Vector3.Dot(toLight, l.Dir), 0f) - l.SpotCos) * l.SpotScale);
                    atten *= cone * Mathf.Clamp01((2f * range - dist) / range);
                }
                float facing = omni ? 1f : Vector3.Dot(n, toLight);
                facing = omni ? 1f : twoSided ? Mathf.Abs(facing) : Mathf.Max(facing, 0f);
                sum += l.Color * (atten * (Around + (1f - Around) * facing));
            }
            return sum;
        }

        private const float Cap = 1.15f, Knee = 0.7f, Desaturate = 0.3f, Floor = 0.08f;

        // Its colour a little toward grey; past the knee, brighter only ever less so, toward the cap
        // (clipped there instead, a brightly lit block's faces were all the same).
        private static Vector3 Soften(Vector3 l)
        {
            float grey = 0.3f * l.x + 0.59f * l.y + 0.11f * l.z;
            l = Vector3.Lerp(l, new Vector3(grey, grey, grey), Desaturate);
            float top = Mathf.Max(l.x, Mathf.Max(l.y, l.z));
            if (top <= Knee)
            {
                return l;
            }
            float room = Cap - Knee;
            float eased = Knee + room * (1f - Mathf.Exp(-(top - Knee) / room));
            return l * (eased / top);
        }

        // The first-person hand's light, as Minecraft's packed light (a block light level, no sky):
        // Minecraft draws it, over ULTRAKILL's picture. Its brightness only (Minecraft's light has no
        // colour), from ULTRAKILL's light around the camera.
        public static int HandLight(Vector3 camera, List<int> scratch)
        {
            Near(camera, 1f, Place(camera), scratch);
            Vector3 l = Soften(Omni(camera, scratch));
            float b = Mathf.Clamp(0.3f * l.x + 0.59f * l.y + 0.11f * l.z, 0.12f, 1f);
            // (Minecraft's light curve, f = L / (4 - 3L) for L in 0..1, the other way round.)
            int level = Mathf.Clamp(Mathf.RoundToInt(15f * 4f * b / (1f + 3f * b)), 0, 15);
            return level << 4;
        }

        // Minecraft's face normals by its vertex face number (0: none), in ULTRAKILL's axes (Z mirrored).
        public static readonly Vector3[] FaceNormal =
        {
            Vector3.zero, Vector3.down, Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.zero,
        };

        // A vertex's light: ULTRAKILL's there, plus Minecraft's own block light (torches, glowing blocks:
        // those glow, and light what's near them as they do in Minecraft), as a colour multiplier.
        // Shade: Minecraft's own for the face (up 1, sides 0.8 and 0.6, down 0.5): a block reads as a
        // block in even light, as in Minecraft.
        public static Color Colour(uint c, float blockLight, Vector3 ukLight, float shade = 1f, float tintAlpha = -1f)
        {
            // (Its colour a little toward grey, and no brighter than a little over the texture:
            // strong coloured light on Minecraft's colourful textures was too much.)
            // (Never wholly black: a little light everywhere, as Minecraft's darkest isn't either.)
            Vector3 light = Vector3.Max(Soften(ukLight), new Vector3(Floor, Floor, Floor));
            float mc = blockLight;
            float r = Mathf.Min(light.x + mc, Cap) * shade, g = Mathf.Min(light.y + mc, Cap) * shade, b = Mathf.Min(light.z + mc, Cap) * shade;
            return new Color((c & 0xFF) / 255f * r, ((c >> 8) & 0xFF) / 255f * g, ((c >> 16) & 0xFF) / 255f * b, tintAlpha >= 0f ? tintAlpha : (c >> 24) / 255f);
        }
    }
}
