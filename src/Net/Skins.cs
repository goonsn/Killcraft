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

        // A pixel's shade kept, in the colour asked for.
        private static Color Replace(float sat, float val, Color32 target)
        {
            Color.RGBToHSV(target, out float th, out float ts, out float tv);
            return Color.HSVToRGB(th, Mathf.Lerp(sat, ts, 0.8f), Mathf.Clamp01(val * Mathf.Lerp(0.25f, 1.5f, tv)));
        }
    }
}
