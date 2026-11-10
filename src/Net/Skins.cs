using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // How a player's V1 looks to the others (and to themselves, third person): its colour (DEFAULT,
    // RED, WHITE, BLACK, GOLD, GREEN, PURPLE: V1's own texture recoloured), and its guns as theirs are:
    // the variant (ULTRAKILL colours a gun's cylinder, barrels, hammer, ... blue, green or red, as
    // WeaponIcon does in first person) and their paint (the guns' colour presets, as GunColorGetter
    // paints them).
    internal static class Skins
    {
        // The parts ULTRAKILL colours by variant (WeaponIcon's variationColoredRenderers; the
        // railcannon's body, as Railcannon does).
        private static readonly HashSet<string> VariantParts = new HashSet<string>
        {
            "Revolver_Cylinder", "MinosRevolver", "MinosRevolver_Cyllinder", "Barrel_L", "Barrel_R", "Nailgun New", "ImpactHammer", "Railcannon",
        };

        private static readonly Color[] DefaultVariants =
        {
            new Color(0.25f, 0.65f, 1f), new Color(0.3f, 1f, 0.3f), new Color(1f, 0.2f, 0.2f), new Color(1f, 0.85f, 0.2f),
        };

        public static Color VariantColor(int v)
        {
            try
            {
                ColorBlindSettings cbs = MonoSingleton<ColorBlindSettings>.Instance;
                if (cbs != null && cbs.variationColors != null && v >= 0 && v < cbs.variationColors.Length)
                {
                    return cbs.variationColors[v];
                }
            }
            catch
            {
            }
            return DefaultVariants[Mathf.Clamp(v, 0, DefaultVariants.Length - 1)];
        }

        // ---- guns ----

        public sealed class Part
        {
            public Renderer R;
            public Material[] Default, Colored;
            public int Weapon;
            public bool Alt;
            public bool Variant;
        }

        // A copied V1's guns' parts, read from their GunColorGetters before those go.
        public static List<Part> Collect(GameObject root)
        {
            var parts = new List<Part>();
            foreach (GunColorGetter g in root.GetComponentsInChildren<GunColorGetter>(true))
            {
                Renderer r = g.GetComponent<Renderer>();
                if (r == null)
                {
                    continue;
                }
                parts.Add(new Part
                {
                    R = r,
                    Default = g.defaultMaterials != null && g.defaultMaterials.Length > 0 ? (Material[])g.defaultMaterials.Clone() : r.sharedMaterials,
                    Colored = g.coloredMaterials != null ? (Material[])g.coloredMaterials.Clone() : new Material[0],
                    Weapon = g.weaponNumber,
                    Alt = g.altVersion,
                    Variant = VariantParts.Contains(r.name),
                });
            }
            return parts;
        }

        // The gun they have out (weapon 0-4, alternate or not), painted as theirs, its variant's colour.
        public static void Paint(List<Part> parts, Profiles.Info who, int weapon, bool alt, int variant)
        {
            if (parts == null || weapon < 0 || weapon > 4)
            {
                return;
            }
            int slot = weapon + (alt ? 5 : 0);
            bool painted = who != null && who.Painted[slot];
            var block = new MaterialPropertyBlock();
            foreach (Part p in parts)
            {
                if (p.R == null || p.Weapon != weapon + 1 || p.Alt != alt)
                {
                    continue;
                }
                try
                {
                    Material[] mats = painted && p.Colored.Length > 0 ? p.Colored : p.Default;
                    if (mats != null && mats.Length > 0)
                    {
                        p.R.sharedMaterials = mats;
                    }
                    block.Clear();
                    if (painted && p.Colored.Length > 0)
                    {
                        block.SetColor("_CustomColor1", who.Paint[slot * 3]);
                        block.SetColor("_CustomColor2", who.Paint[slot * 3 + 1]);
                        block.SetColor("_CustomColor3", who.Paint[slot * 3 + 2]);
                    }
                    if (p.Variant && variant >= 0)
                    {
                        Color c = VariantColor(variant);
                        Material m = p.R.sharedMaterial;
                        block.SetColor(m != null && m.HasProperty("_EmissiveColor") ? "_EmissiveColor" : "_Color", c);
                    }
                    p.R.SetPropertyBlock(block);
                }
                catch
                {
                }
            }
        }

        // This player's own third-person V1 (F5): its gun in its variant's colour (ULTRAKILL paints it itself).
        private static float ownAt = -100f;

        public static void OwnFrame(bool inLevel)
        {
            if (!inLevel || Time.unscaledTime - ownAt < 0.5f)
            {
                return;
            }
            ownAt = Time.unscaledTime;
            try
            {
                var pa = MonoSingleton.GetInstance(typeof(PlayerAnimations)) as PlayerAnimations;
                if (pa == null)
                {
                    return;
                }
                int variant = CurrentVariant();
                var block = new MaterialPropertyBlock();
                foreach (Renderer r in pa.GetComponentsInChildren<Renderer>(false))
                {
                    if (!VariantParts.Contains(r.name) || variant < 0)
                    {
                        continue;
                    }
                    r.GetPropertyBlock(block);
                    Material m = r.sharedMaterial;
                    block.SetColor(m != null && m.HasProperty("_EmissiveColor") ? "_EmissiveColor" : "_Color", VariantColor(variant));
                    r.SetPropertyBlock(block);
                }
                SkinBody(pa.gameObject, Profiles.Mine, ownBody);
            }
            catch
            {
            }
        }

        // The variant of the gun this player has out (-1: none).
        public static int CurrentVariant()
        {
            try
            {
                var gc = MonoSingleton.GetInstance(typeof(GunControl)) as GunControl;
                GameObject w = gc != null ? gc.currentWeapon : null;
                if (w != null && w.TryGetComponent(out WeaponIcon icon) && icon.weaponDescriptor != null)
                {
                    return (int)icon.weaponDescriptor.variationColor;
                }
            }
            catch
            {
            }
            return -1;
        }

        // ---- V1's colours ----

        // V1's own textures, recoloured: its blue (the body, the wings' frames) in the body colour,
        // its yellow lights in the lights colour; the wings' glow is the wing material's emissive
        // colour.
        public sealed class Body
        {
            public Renderer R;
            public Material[] Original;
            public string Shown = "";
        }

        private static readonly Body ownBody = new Body();
        private static readonly Dictionary<string, Material> recoloured = new Dictionary<string, Material>();
        private static readonly Dictionary<string, Texture2D> retextured = new Dictionary<string, Texture2D>();

        public static string LookKey(Profiles.Info who) => who == null ? "" : $"{K(who.Body)}|{K(who.Lights)}|{K(who.Wings)}";

        private static string K(Color32 c) => c.a == 0 ? "-" : ColorUtility.ToHtmlStringRGB(c);

        public static bool Plain(Profiles.Info who) => who == null || (who.Body.a == 0 && who.Lights.a == 0 && who.Wings.a == 0);

        // V1's body (v1_mdl) in a player's colours (none set: as it is).
        public static void SkinBody(GameObject root, Profiles.Info who, Body body)
        {
            if (body.R == null)
            {
                foreach (SkinnedMeshRenderer r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r.name == "v1_mdl")
                    {
                        body.R = r;
                        body.Original = r.sharedMaterials;
                        body.Shown = "";
                        break;
                    }
                }
                if (body.R == null)
                {
                    return;
                }
            }
            string key = LookKey(who);
            if (body.Shown == key)
            {
                return;
            }
            body.Shown = key;
            if (Plain(who))
            {
                body.R.sharedMaterials = body.Original;
                return;
            }
            var mats = new Material[body.Original.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = Recolour(body.Original[i], who);
            }
            body.R.sharedMaterials = mats;
        }

        // Copies of looks nobody wears now (a colour tried on the wheel, a player gone): freed a few
        // seconds later, when nothing shows them any more.
        private static readonly List<(UnityEngine.Object Obj, float At)> stale = new List<(UnityEngine.Object, float)>();

        private static void Prune(Profiles.Info also)
        {
            if (recoloured.Count + retextured.Count < 24)
            {
                return;
            }
            var looks = new HashSet<string>();
            var paints = new HashSet<string>();
            void Worn(Profiles.Info i)
            {
                looks.Add(LookKey(i));
                paints.Add(K(i.Body) + "|" + K(i.Lights));
                paints.Add(K(i.Body) + "|-");  // (the wings': their lights left)
            }
            Worn(also);
            Worn(ownLook);
            foreach (Profiles.Info i in Profiles.All())
            {
                Worn(i);
            }
            var drop = new List<string>();
            foreach (var kv in recoloured)
            {
                if (!looks.Contains(kv.Key.Substring(kv.Key.IndexOf('|') + 1)))
                {
                    drop.Add(kv.Key);
                }
            }
            foreach (string k in drop)
            {
                if (recoloured[k] != null)
                {
                    stale.Add((recoloured[k], Time.unscaledTime));
                }
                recoloured.Remove(k);
            }
            drop.Clear();
            foreach (var kv in retextured)
            {
                if (!paints.Contains(kv.Key.Substring(kv.Key.IndexOf('|') + 1)))
                {
                    drop.Add(kv.Key);
                }
            }
            foreach (string k in drop)
            {
                if (retextured[k] != null)
                {
                    stale.Add((retextured[k], Time.unscaledTime));
                }
                retextured.Remove(k);
            }
        }

        private static void Bury()
        {
            for (int i = stale.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime - stale[i].At > 3f)
                {
                    if (stale[i].Obj != null)
                    {
                        UnityEngine.Object.Destroy(stale[i].Obj);
                    }
                    stale.RemoveAt(i);
                }
            }
        }

        private static Material Recolour(Material m, Profiles.Info who)
        {
            if (m == null)
            {
                return null;
            }
            bool wing = m.name.IndexOf("Wing", StringComparison.OrdinalIgnoreCase) >= 0;
            string key = m.GetInstanceID() + "|" + LookKey(who);
            if (recoloured.TryGetValue(key, out Material done) && done != null)
            {
                return done;
            }
            Prune(who);
            var copy = new Material(m) { name = m.name + " (Killcraft look)" };
            try
            {
                Texture tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                Color32 lights = wing ? new Color32(0, 0, 0, 0) : who.Lights;
                if (tex != null && (who.Body.a != 0 || lights.a != 0))
                {
                    copy.SetTexture("_MainTex", RecolourTexture(tex, who.Body, lights));
                }
                if (wing && who.Wings.a != 0 && copy.HasProperty("_EmissiveColor"))
                {
                    copy.SetColor("_EmissiveColor", who.Wings);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"skins: couldn't recolour {m.name} ({e.Message})");
            }
            recoloured[key] = copy;
            return copy;
        }

        private static Texture2D RecolourTexture(Texture tex, Color32 body, Color32 lights)
        {
            string key = tex.GetInstanceID() + "|" + K(body) + "|" + K(lights);
            if (retextured.TryGetValue(key, out Texture2D had) && had != null)
            {
                return had;
            }
            int w = Mathf.Min(tex.width, 1024), h = Mathf.Min(tex.height, 1024);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture was = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { filterMode = tex.filterMode, wrapMode = tex.wrapMode };
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = read.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                Color.RGBToHSV(c, out float hue, out float sat, out float val);
                Color o;
                if (body.a != 0 && sat > 0.18f && hue > 0.5f && hue < 0.78f)
                {
                    o = Replace(sat, val, body);
                }
                else if (lights.a != 0 && sat > 0.3f && val > 0.3f && hue > 0.04f && hue < 0.2f)
                {
                    o = Replace(sat, val, lights);
                }
                else
                {
                    continue;
                }
                px[i] = new Color32((byte)(o.r * 255f), (byte)(o.g * 255f), (byte)(o.b * 255f), px[i].a);
            }
            read.SetPixels32(px);
            read.Apply(true);
            read.name = tex.name + " (Killcraft look)";
            retextured[key] = read;
            return read;
        }

        // ---- this player's own arms in first person, in their V1's colour ----
        // The arm holding the guns (T_MainArm, in each gun's model), the Feedbacker (T_Feedbacker) and
        // the blue arm (Blue Arm): their blue plates as V1's body's are. The whiplash's green arm, the
        // Knuckleblaster and the guns themselves keep their own colours.
        private static readonly HashSet<string> ArmTextures = new HashSet<string> { "T_MainArm", "T_Feedbacker", "Blue Arm" };
        private static readonly Dictionary<Renderer, (Material[] Original, Material[] Shown)> arms = new Dictionary<Renderer, (Material[], Material[])>();
        private static readonly Profiles.Info ownLook = new Profiles.Info();
        private static readonly List<Renderer> found = new List<Renderer>();
        private static string armsKey;
        private static float armsAt = -100f;

        public static void OwnArms()
        {
            Bury();
            ownLook.Body = Profiles.ParseColor(Plugin.BodyColour.Value);
            ownLook.Lights = Profiles.ParseColor(Plugin.LightsColour.Value);
            ownLook.Wings = Profiles.ParseColor(Plugin.WingsColour.Value);
            string key = LookKey(ownLook);
            float now = Time.unscaledTime;
            // (Each half second: a gun picked up or an arm switched to is a new model.)
            if (key == armsKey && now - armsAt < 0.5f)
            {
                return;
            }
            armsKey = key;
            armsAt = now;
            try
            {
                found.Clear();
                GunControl gc = MonoSingleton<GunControl>.Instance;
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (gc != null)
                {
                    found.AddRange(gc.GetComponentsInChildren<Renderer>(true));
                }
                if (fc != null)
                {
                    found.AddRange(fc.GetComponentsInChildren<Renderer>(true));
                }
                var gone = new List<Renderer>();
                foreach (Renderer r in arms.Keys)
                {
                    if (r == null)
                    {
                        gone.Add(r);
                    }
                }
                foreach (Renderer r in gone)
                {
                    arms.Remove(r);
                }
                bool plain = Plain(ownLook);
                foreach (Renderer r in found)
                {
                    if (r == null || !(r is SkinnedMeshRenderer || r is MeshRenderer))
                    {
                        continue;
                    }
                    Material[] has = r.sharedMaterials;
                    arms.TryGetValue(r, out var was);
                    // (Its materials as ours left them, or new ones ULTRAKILL put on since.)
                    Material[] original = was.Shown != null && Same(has, was.Shown) ? was.Original : has;
                    if (!AnyArm(original))
                    {
                        arms.Remove(r);
                        continue;
                    }
                    Material[] want = original;
                    if (!plain)
                    {
                        want = new Material[original.Length];
                        for (int i = 0; i < original.Length; i++)
                        {
                            want[i] = IsArm(original[i]) ? Recolour(original[i], ownLook) : original[i];
                        }
                    }
                    if (!Same(has, want))
                    {
                        r.sharedMaterials = want;
                    }
                    arms[r] = (original, want);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"skins: couldn't colour the arms ({e.Message})");
            }
        }

        private static bool IsArm(Material m)
        {
            if (m == null || !m.HasProperty("_MainTex"))
            {
                return false;
            }
            Texture t = m.GetTexture("_MainTex");
            return t != null && ArmTextures.Contains(t.name);
        }

        private static bool AnyArm(Material[] ms)
        {
            foreach (Material m in ms)
            {
                if (IsArm(m))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Same(Material[] a, Material[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        // A pixel's shade kept, in the colour asked for.
        private static Color Replace(float sat, float val, Color32 target)
        {
            Color.RGBToHSV(target, out float th, out float ts, out float tv);
            return Color.HSVToRGB(th, Mathf.Lerp(sat, ts, 0.8f), Mathf.Clamp01(val * Mathf.Lerp(0.25f, 1.5f, tv)));
        }
    }
}
