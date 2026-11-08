using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Killcraft
{
    // Killcraft's own menus (the party menu, the KILLCRAFT panel, the party's HUD, the chat), made
    // here piece by piece in ULTRAKILL's look: its VCR font, white outlined boxes that fill white
    // (black writing) under the mouse, red for Killcraft's own. Sizes are a 1920x1080 screen's.
    internal static class Ui
    {
        public static readonly Color White = Color.white;
        public static readonly Color Black = new Color(0f, 0f, 0f, 1f);
        public static readonly Color Red = new Color(0.86f, 0.33f, 0.28f);
        public static readonly Color Orange = new Color(0.92f, 0.34f, 0.22f);
        public static readonly Color Grey = new Color(0.55f, 0.55f, 0.55f);
        public static readonly Color DarkGrey = new Color(0.25f, 0.25f, 0.25f);
        public static readonly Color Yellow = new Color(1f, 0.82f, 0.18f);
        public static readonly Color Cyan = new Color(0.3f, 0.9f, 1f);
        public static readonly Color Green = new Color(0.3f, 0.95f, 0.4f);
        public static readonly Color Panel = new Color(0.03f, 0.03f, 0.035f, 0.94f);

        private static TMP_FontAsset font;
        private static float fontLookedAt = -100f;

        // ULTRAKILL's own menu font: VCR_OSD_MONO_1 (its buttons, options, HUD), else its UI copy.
        // (Not "vcr-osd-replayed": a dynamic atlas of another VCR font, scaled to 0.79, which mixed
        // letters of two fonts and sizes into one line.)
        public static TMP_FontAsset Font
        {
            get
            {
                bool best = font != null && font.name == "VCR_OSD_MONO_1";
                if (!best && Time.realtimeSinceStartup - fontLookedAt > 2f)
                {
                    fontLookedAt = Time.realtimeSinceStartup;
                    int rank = font == null ? 0 : Rank(font.name);
                    foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        if (f != null && Rank(f.name) > rank)
                        {
                            font = f;
                            rank = Rank(f.name);
                        }
                    }
                }
                return font;
            }
        }

        private static int Rank(string name)
        {
            if (name == "VCR_OSD_MONO_1")
            {
                return 4;
            }
            if (name == "VCR_OSD_MONO_UI")
            {
                return 3;
            }
            if (name.IndexOf("VCR_OSD_MONO", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }
            return name.IndexOf("replayed", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }

        // ---- shapes ----
        // ULTRAKILL's own boxes (its menus' buttons, sliders, panels: corners cut at 45°, sliced; found
        // by name once a menu of ULTRAKILL's has loaded them), else the same shapes drawn here. Their
        // outlines are 12 pixels of the sprite: an image's pixelsPerUnitMultiplier of 12/t draws them t
        // pixels thick (of a 1920x1080 screen), corners in proportion.

        private static readonly Dictionary<string, Sprite> uk = new Dictionary<string, Sprite>();
        private static float ukLookedAt = -100f;
        private static readonly string[] UkNames = { "Round_BorderLarge", "Round_BorderLargeBlack", "Round_FillLarge", "Round_BorderSmall", "Round_FillSmall", "Round_HorizHandle_Invert" };
        private static Sprite fbLine, fbLineBlack, fbFill, fbLineSmall, fbFillSmall, fbHandle;
        private static Sprite roundLine, roundFill, chamLine, chamFill, bigChamLine, bigChamFill, frameLine, crown, skull, eye;

        private static Sprite Uk(string name)
        {
            if (uk.TryGetValue(name, out Sprite s) && s != null)
            {
                return s;
            }
            if (Time.unscaledTime - ukLookedAt > 3f)
            {
                ukLookedAt = Time.unscaledTime;
                try
                {
                    foreach (Sprite f in Resources.FindObjectsOfTypeAll<Sprite>())
                    {
                        if (f != null && Array.IndexOf(UkNames, f.name) >= 0 && f.texture != null && (!uk.TryGetValue(f.name, out Sprite had) || had == null))
                        {
                            uk[f.name] = f;
                        }
                    }
                }
                catch
                {
                }
                if (uk.TryGetValue(name, out s) && s != null)
                {
                    return s;
                }
            }
            return null;
        }

        // A button's outline (black inside, as ULTRAKILL's main menu has them), outline only, filled.
        public static Sprite BoxLineBlack => Uk("Round_BorderLargeBlack") ?? (fbLineBlack != null ? fbLineBlack : fbLineBlack = Octagon(83, 35f, 12f, 41, 2));
        public static Sprite BoxLine => Uk("Round_BorderLarge") ?? (fbLine != null ? fbLine : fbLine = Octagon(83, 35f, 12f, 41, 0));
        public static Sprite BoxFill => Uk("Round_FillLarge") ?? (fbFill != null ? fbFill : fbFill = Octagon(83, 35f, 12f, 37, 1));
        // The small ones (corners cut less).
        public static Sprite SmallLine => Uk("Round_BorderSmall") ?? (fbLineSmall != null ? fbLineSmall : fbLineSmall = Octagon(59, 23f, 12f, 29, 0));
        public static Sprite SmallFill => Uk("Round_FillSmall") ?? (fbFillSmall != null ? fbFillSmall : fbFillSmall = Octagon(50, 23f, 12f, 25, 1));
        // A slider's handle (stripes on its left).
        public static Sprite Handle => Uk("Round_HorizHandle_Invert") ?? (fbHandle != null ? fbHandle : fbHandle = HandleShape());

        // (The older names.)
        public static Sprite RoundLine => SmallLine;
        public static Sprite RoundFill => SmallFill;

        // A box with round corners (sliced: any size).
        private static Sprite roundBox;
        public static Sprite RoundBox
        {
            get
            {
                if (roundBox != null)
                {
                    return roundBox;
                }
                const int r = 14, size = r * 2 + 4;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                float h = size / 2f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float qx = Mathf.Abs(x + 0.5f - h) - (h - r), qy = Mathf.Abs(y + 0.5f - h) - (h - r);
                        float d = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f)) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(0.5f - d)));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                roundBox = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r + 1, r + 1, r + 1, r + 1));
                roundBox.name = "Killcraft Round Box";
                return roundBox;
            }
        }

        // A bar with round ends (sliced: any length).
        private static Sprite pill;
        public static Sprite Pill
        {
            get
            {
                if (pill != null)
                {
                    return pill;
                }
                const int w = 32, h = 16;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[w * h];
                float r = h / 2f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, r, w - r), cy = r;
                        float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                        px[y * w + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(r - d + 0.5f)));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                pill = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, 0, r, 0));
                pill.name = "Killcraft Pill";
                return pill;
            }
        }
        public static Sprite ChamLine => chamLine != null ? chamLine : chamLine = Shape(40, 8f, 2f, false, false);
        public static Sprite ChamFill => chamFill != null ? chamFill : chamFill = Shape(40, 8f, 0f, false, true);
        // A panel's frame: corners cut wide, a thin line (ULTRAKILL's HUD panels').
        public static Sprite BigChamLine => bigChamLine != null ? bigChamLine : bigChamLine = Shape(80, 24f, 2.5f, false, false);
        public static Sprite BigChamFill => bigChamFill != null ? bigChamFill : bigChamFill = Shape(80, 24f, 0f, false, true);
        // The whole screen's frame (the main menu's): a thick white line, corners cut.
        private static Sprite bandLine;
        public static Sprite BandLine => bandLine != null ? bandLine : bandLine = Shape(80, 24f, 8f, false, false);
        public static Sprite FrameLine => frameLine != null ? frameLine : frameLine = Shape(80, 22f, 7f, false, false);

        // A ring and a disc (the panel's X).
        private static Sprite circleLine, circleFill;
        public static Sprite CircleLine => circleLine != null ? circleLine : circleLine = Circle(3f);
        public static Sprite CircleFill => circleFill != null ? circleFill : circleFill = Circle(0f);

        private static Sprite Circle(float b)
        {
            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float d = s / 2f - Mathf.Sqrt((x + 0.5f - s / 2f) * (x + 0.5f - s / 2f) + (y + 0.5f - s / 2f) * (y + 0.5f - s / 2f));
                    float a = Mathf.Clamp01(d + 0.5f);
                    if (b > 0f)
                    {
                        a *= Mathf.Clamp01(b * 1.8f - d + 0.5f);
                    }
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
        }

        // An image sliced with ULTRAKILL's sprite, its outline t pixels thick.
        public static Image Sliced(Image img, float thick)
        {
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 12f / Mathf.Max(0.5f, thick);
            return img;
        }

        // ULTRAKILL's shape drawn: an octagon s pixels square, corners cut by c, its outline b wide;
        // inside: 0 nothing, 1 filled, 2 black.
        private static Sprite Octagon(int s, float c, float b, int border, int inside)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float d = Inside(x + 0.5f, y + 0.5f, s, c, false);
                    float a = Mathf.Clamp01(d + 0.5f);
                    float line = Mathf.Clamp01(b - d + 0.5f);
                    if (inside == 0)
                    {
                        px[y * s + x] = new Color32(255, 255, 255, (byte)(a * line * 255f));
                    }
                    else if (inside == 1)
                    {
                        px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }
                    else
                    {
                        byte v = (byte)(line * 255f);
                        px[y * s + x] = new Color32(v, v, v, (byte)(a * 255f));
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            Sprite sp = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sp.name = "Round_Killcraft";
            return sp;
        }

        private static Sprite HandleShape()
        {
            const int w = 143, h = 108;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // (Corners cut by 23 on the right, a straight left end from x 6.)
                    float cx = Mathf.Max(0f, x + 0.5f - (w - 23f)), cyTop = Mathf.Max(0f, 23f - (y + 0.5f)), cyBottom = Mathf.Max(0f, y + 0.5f - (h - 23f));
                    bool on = x >= 6 && cx + cyTop <= 23f && cx + cyBottom <= 23f;
                    // (Two stripes across its left end.)
                    float diag = x + (h - y);
                    if (x < 117 && ((diag > 30 && diag < 47) || (diag > 64 && diag < 81)))
                    {
                        on = false;
                    }
                    px[y * w + x] = new Color32(255, 255, 255, on ? (byte)255 : (byte)0);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            Sprite sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(117, 0, 25, 0));
            sp.name = "Round_KillcraftHandle";
            return sp;
        }

        // A box s pixels square: corners rounded (radius r) or cut (by r); its outline b wide, or filled.
        private static Sprite Shape(int s, float r, float b, bool round, bool fill)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float d = Inside(x + 0.5f, y + 0.5f, s, r, round);
                    float a = Mathf.Clamp01(d + 0.5f);
                    if (!fill)
                    {
                        a *= Mathf.Clamp01(b - d + 0.5f);
                    }
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            float m = Mathf.Ceil(r + b + 1f);
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(m, m, m, m));
        }

        // How far inside the box's edge a point is.
        private static float Inside(float x, float y, float s, float r, bool round)
        {
            float edge = Mathf.Min(Mathf.Min(x, s - x), Mathf.Min(y, s - y));
            if (round)
            {
                float cx = Mathf.Clamp(x, r, s - r), cy = Mathf.Clamp(y, r, s - r);
                if ((x < r || x > s - r) && (y < r || y > s - r))
                {
                    return r - Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                }
                return edge;
            }
            float k = 0.70710678f;
            float c1 = (x + y - r) * k, c2 = (s - x + y - r) * k, c3 = (x + s - y - r) * k, c4 = (s - x + s - y - r) * k;
            return Mathf.Min(edge, Mathf.Min(Mathf.Min(c1, c2), Mathf.Min(c3, c4)));
        }

        // Pixel art, rows top down ('#' on).
        private static Sprite Pixels(params string[] rows)
        {
            int w = rows[0].Length, h = rows.Length;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    tex.SetPixel(x, h - 1 - y, rows[y][x] == '#' ? Color.white : new Color(1f, 1f, 1f, 0f));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        // The party host's crown (as UltraLink's): flat gold, three sharp points on a band, nothing
        // else; its edges smooth (4x4 samples a pixel), in its own colour (shown white).
        public static Sprite Crown => crown != null ? crown : crown = CrownShape();

        private static Sprite CrownShape()
        {
            const int W = 64, H = 48, N = 4;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var gold = new Color(1f, 0.78f, 0.1f);
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < N; sy++)
                    {
                        for (int sx = 0; sx < N; sx++)
                        {
                            if (InCrown((x + (sx + 0.5f) / N) / W, (y + (sy + 0.5f) / N) / H))
                            {
                                hits++;
                            }
                        }
                    }
                    px[y * W + x] = new Color(gold.r, gold.g, gold.b, hits / (float)(N * N));
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);

            // (u across, v up: a band below, three points up to sharp tips, two dips between.)
            bool InCrown(float u, float v)
            {
                if (u < 0.06f || u > 0.94f || v < 0.08f)
                {
                    return false;
                }
                if (v < 0.34f)
                {
                    return true;
                }
                float top;
                if (u < 0.28f)
                {
                    top = Mathf.Lerp(0.94f, 0.5f, (u - 0.06f) / 0.22f);
                }
                else if (u < 0.5f)
                {
                    top = Mathf.Lerp(0.5f, 0.94f, (u - 0.28f) / 0.22f);
                }
                else if (u < 0.72f)
                {
                    top = Mathf.Lerp(0.94f, 0.5f, (u - 0.5f) / 0.22f);
                }
                else
                {
                    top = Mathf.Lerp(0.5f, 0.94f, (u - 0.72f) / 0.22f);
                }
                return v <= top;
            }
        }

        // A little skull (the skulls a player carries).
        public static Sprite Skull => skull != null ? skull : skull = Pixels(
            "..######..",
            ".########.",
            "##########",
            "##..##..##",
            "##..##..##",
            "##########",
            ".###..###.",
            "..######..",
            "..#.##.#..");

        // An eye (spectating).
        public static Sprite Eye => eye != null ? eye : eye = Pixels(
            "....#####....",
            "..##.....##..",
            ".#...###...#.",
            "#...#####...#",
            ".#...###...#.",
            "..##.....##..",
            "....#####....");

        // ---- pieces ----

        // A screen-wide canvas over ULTRAKILL's (order: which goes on top).
        public static Canvas Canvas(string name, int order, bool keep)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (keep)
            {
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        // A rectangle at x, y from its parent's top left (w x h; 0 for its parent's whole width/height).
        public static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            Place(rt, x, y, w, h);
            return rt;
        }

        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            bool sw = w <= 0f, sh = h <= 0f;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchorMin = new Vector2(0f, sh ? 0f : 1f);
            rt.anchorMax = new Vector2(sw ? 1f : 0f, 1f);
            rt.offsetMin = new Vector2(x, sh ? y : -y - h);
            rt.offsetMax = new Vector2(sw ? -x : x + w, -y);
        }

        // Anchored to the parent's whole area, inset.
        public static RectTransform Fill(Transform parent, string name, float inset = 0f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
            return rt;
        }

        public static void Refont(Transform root)
        {
            TMP_FontAsset f = Font;
            if (f == null || root == null)
            {
                return;
            }
            foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t.font != f)
                {
                    t.font = f;
                }
            }
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align, float x, float y, float w, float h)
        {
            RectTransform rt = Rect(parent, "Text", x, y, w, h);
            return Label(rt, text, size, color, align);
        }

        public static TextMeshProUGUI Label(RectTransform rt, string text, float size, Color color, TextAlignmentOptions align)
        {
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null)
            {
                t.font = Font;
            }
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        public static Image Image(Transform parent, Sprite sprite, Color color, float x, float y, float w, float h)
        {
            RectTransform rt = Rect(parent, "Image", x, y, w, h);
            return Picture(rt, sprite, color);
        }

        public static Image Picture(RectTransform rt, Sprite sprite, Color color)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sprite != null && sprite.border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            // (ULTRAKILL's boxes: outlines 3 pixels thick unless made otherwise.)
            if (sprite != null && sprite.name.StartsWith("Round_", StringComparison.Ordinal))
            {
                img.pixelsPerUnitMultiplier = 4f;
            }
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // A plain line (a header's underline).
        public static Image Line(Transform parent, Color color, float x, float y, float w, float h = 2f) => Image(parent, null, color, x, y, w, h);

        // A button as ULTRAKILL's: its outlined box (black inside), grey under the mouse, red pressed;
        // small: the smaller box (thinner, corners cut less). thick: the outline's width (0: by size).
        public static UiButton Button(Transform parent, string label, float size, Action click, float x, float y, float w, float h,
            TextAlignmentOptions align = TextAlignmentOptions.Center, bool small = false, float thick = 0f)
        {
            RectTransform rt = Rect(parent, "Button " + label, x, y, w, h);
            if (thick <= 0f)
            {
                thick = small ? (h >= 40f ? 3f : 2.5f) : (h >= 56f ? 5f : h >= 36f ? 3.5f : 2.8f);
            }
            Sprite line = small ? SmallLine : BoxLineBlack;
            Image back = Picture(rt, line, White);
            Sliced(back, thick);
            back.raycastTarget = true;
            RectTransform lr = Fill(rt, "Label");
            bool left = align == TextAlignmentOptions.Left || align == TextAlignmentOptions.MidlineLeft;
            lr.offsetMin = new Vector2(left ? Mathf.Max(14f, h * 0.42f) : 6f, 0f);
            lr.offsetMax = new Vector2(-6f, 0f);
            TextMeshProUGUI t = Label(lr, label, size, White, align);
            var b = rt.gameObject.AddComponent<UiButton>();
            b.Back = back;
            b.Text = t;
            b.Click = click;
            b.Line = line;
            b.Filled = small ? SmallFill : BoxFill;
            b.Refresh();
            return b;
        }

        // A switch (UltraLink's): a pill, its knob left (off) or right (on, the pill filled white).
        public static UiSwitch Switch(Transform parent, float x, float y, bool on, Action<bool> changed)
        {
            RectTransform rt = Rect(parent, "Switch", x, y, 46f, 24f);
            UnityEngine.UI.Image pill = Picture(rt, SmallLine, White);
            Sliced(pill, 2.2f);
            pill.raycastTarget = true;
            RectTransform kr = Rect(rt, "Knob", 4f, 4f, 16f, 16f);
            UnityEngine.UI.Image knob = Picture(kr, CircleFill, White);
            knob.type = UnityEngine.UI.Image.Type.Simple;
            knob.raycastTarget = false;
            var sw = rt.gameObject.AddComponent<UiSwitch>();
            sw.Pill = pill;
            sw.Knob = kr;
            sw.KnobImage = knob;
            sw.Changed = changed;
            sw.Set(on);
            return sw;
        }

        // A dropdown (UltraLink's): the choice and a small triangle after it; clicked, the choices in a
        // list under it (clicking anywhere else closes it).
        private static Sprite triangle;
        public static Sprite Triangle => triangle != null ? triangle : triangle = Pixels(
            "#########",
            " ####### ",
            "  #####  ",
            "   ###   ",
            "    #    ");

        public static UiButton Dropdown(Transform parent, float size, float x, float y, float w, float h, string[] choices, Func<int> get, Action<int> set)
        {
            UiButton b = null;
            UiDropdown dd = null;
            b = Button(parent, "", size, () => dd.Toggle(), x, y, w, h, TextAlignmentOptions.MidlineLeft, true, 2.5f);
            dd = b.gameObject.AddComponent<UiDropdown>();
            dd.Button = b;
            dd.Choices = choices;
            dd.Get = get;
            dd.SetChoice = set;
            dd.Size = size;
            RectTransform tr = Rect(b.transform, "Triangle", 0f, 0f, 9f, 5f);
            tr.anchorMin = tr.anchorMax = new Vector2(0f, 0.5f);
            tr.pivot = new Vector2(0f, 0.5f);
            Image ti = Picture(tr, Triangle, White);
            ti.preserveAspect = true;
            dd.Arrow = tr;
            dd.Show();
            return b;
        }

        // Opened: a quick grow and fade in (what's shown unchanged otherwise).
        public static void Open(RectTransform rt)
        {
            if (rt == null)
            {
                return;
            }
            UiOpenEffect e = rt.GetComponent<UiOpenEffect>();
            if (e == null)
            {
                e = rt.gameObject.AddComponent<UiOpenEffect>();
            }
            e.Play();
        }

        // Rounded boxes (UltraLink's "..." and its text fields): an outline, or filled.
        private static Sprite roundedLine, roundedFill;
        public static Sprite RoundedLine => roundedLine != null ? roundedLine : roundedLine = Shape(40, 9f, 2.2f, true, false);
        public static Sprite RoundedFill => roundedFill != null ? roundedFill : roundedFill = Shape(40, 9f, 0f, true, true);

        // The "..." button's box (UltraLink's, ULTRAKILL's look): corners cut at 45 degrees, a thin line.
        private static Sprite moreLine, moreFill;
        public static Sprite MoreLine => moreLine != null ? moreLine : moreLine = Shape(40, 8f, 2.2f, false, false);
        public static Sprite MoreFill => moreFill != null ? moreFill : moreFill = Shape(40, 8f, 0f, false, true);

        // The "..." button by a player (UltraLink's): that box, three round dots across its middle.
        public static UiButton MoreButton(Transform parent, Action click, float x, float y, float w, float h)
        {
            UiButton b = Button(parent, "", 10f, click, x, y, w, h, TextAlignmentOptions.Center, true, 2.2f);
            b.Line = MoreLine;
            b.Filled = MoreFill;
            b.Back.sprite = MoreLine;
            b.Back.pixelsPerUnitMultiplier = 1f;
            float dot = Mathf.Max(4f, Mathf.Round(h * 0.15f)), gap = Mathf.Max(3f, Mathf.Round(dot * 0.8f));
            float total = 3f * dot + 2f * gap;
            b.Dots = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                RectTransform d = Rect(b.transform, "Dot", 0f, 0f, dot, dot);
                d.anchorMin = d.anchorMax = new Vector2(0.5f, 0.5f);
                d.pivot = new Vector2(0.5f, 0.5f);
                d.anchoredPosition = new Vector2(-total / 2f + dot / 2f + i * (dot + gap), 0f);
                b.Dots[i] = Picture(d, CircleFill, White);
            }
            b.Refresh();
            return b;
        }

        // A text field (UltraLink's): a rounded box, what's typed in it from the left.
        public static UiButton Field(Transform parent, float size, Action click, float x, float y, float w, float h)
        {
            UiButton b = Button(parent, "", size, click, x, y, w, h, TextAlignmentOptions.MidlineLeft, true, 2.2f);
            b.Line = RoundedLine;
            b.Filled = RoundedFill;
            b.Back.sprite = RoundedLine;
            b.Back.pixelsPerUnitMultiplier = 1f;
            ((RectTransform)b.Text.transform).offsetMin = new Vector2(14f, 0f);
            b.Refresh();
            return b;
        }

        // Killcraft's logo: a Minecraft block, ULTRAKILL's shading, torn by three claw marks (design/logo.py).
        private static Sprite logo;
        public static Sprite Logo => logo != null ? logo : logo = Pixels(
            "                  ###                  ",
            "                #######                ",
            "              ###########              ",
            "            ##############             ",
            "          ###############   #          ",
            "        ################   ####        ",
            "      #################   ####         ",
            "    ##################   ####   ###    ",
            "  ###################   ####   ######  ",
            "####################   ####   ####   ##",
            "###################   ####   ####   ###",
            "## ###############   ####   ####   # ##",
            "# # #############   ####   ####    # ##",
            "## # # #########    ###   ####   # # ##",
            "# # # # #######   ####   ####    # # ##",
            "## # # # # ###   ####   ####   # # # ##",
            "# # # # # # #   ####   ###     # # # ##",
            "## # # # # #   ####   ## #   # # # # ##",
            "# # # # # #   # ##   # #     # # # # ##",
            "## # # # #   # #     # #     # # # # ##",
            "# # # # #   # #    # #     # # # # # ##",
            "## # # #   # #     # #   # # # # # # ##",
            "# # # #   # #     ##     # # # # # # ##",
            "## # #   # #     # #   # # # # # # # ##",
            "# # #   # #     # #    # # # # # # # ##",
            "## #   # #     # #   # # # # # # # # ##",
            "# #   # #     # #    # # # # # # # # ##",
            "## # # #     # #   # # # # # # # # # ##",
            "# # # #     # #   ## # # # # # # # # ##",
            " ### #     # #   # # # # # # # # # ### ",
            "   ## # # # #   # ## # # # # # # ###   ",
            "     ### # #   # # # # # # # # ###     ",
            "       ## #   # # ## # # # # ###       ",
            "         ### # # # # # # # ###         ",
            "           ## # # ## # # ###           ",
            "             ### # # # ###             ",
            "               ## ## ###               ",
            "                 #####                 ",
            "                   #                   ");

        // KillNet's logo (played without Minecraft): a globe, the internet's sign (design/netlogo.py).
        private static Sprite netLogo;
        public static Sprite NetLogo => netLogo != null ? netLogo : netLogo = Pixels(
            "                  ###                  ",
            "             #############             ",
            "           #################           ",
            "         ##### ##  #  ## #####         ",
            "        #####  #   #   #  #####        ",
            "       ####   ##   #   ##   ####       ",
            "      ####    #    #    #    ####      ",
            "     #####   ##    #    ##   #####     ",
            "    ###############################    ",
            "   #################################   ",
            "   ## ##    ##     #     ##    ## ##   ",
            "  ### #     ##     #     ##     # ###  ",
            "  ## ##     ##     #     ##     ## ##  ",
            " ### ##     #      #      #     ## ### ",
            " ##  ##     #      #      #     ##  ## ",
            " ##  #      #      #      #      #  ## ",
            " ## ##      #      #      #      ## ## ",
            " ## ##     ##      #      ##     ## ## ",
            "### ##     ##      ##     ##     ## ###",
            "#######################################",
            "### ##     ##     ###     ##     ## ###",
            " ## ##     ##      #      ##     ## ## ",
            " ## ##      #      #      #      ## ## ",
            " ##  #      #      #      #      #  ## ",
            " ##  ##     #      #      #     ##  ## ",
            " ### ##     #      #      #     ## ### ",
            "  ## ##     ##     #     ##     ## ##  ",
            "  ### #     ##     #     ##     # ###  ",
            "   ## ##    ##     #     ##    ## ##   ",
            "   #################################   ",
            "    ###############################    ",
            "     #####   ##    #    ##   #####     ",
            "      ####    #    #    #    ####      ",
            "       ####   ##   #   ##   ####       ",
            "        #####  #   #   #  #####        ",
            "         ##### ##  #  ## #####         ",
            "           #################           ",
            "             #############             ",
            "                  ###                  ");

        // Opened: its parts appear row by row, top down (as ULTRAKILL's menus' buttons do).
        public static void Reveal(RectTransform root, params Transform[] containers)
        {
            if (root == null)
            {
                return;
            }
            UiReveal r = root.GetComponent<UiReveal>();
            if (r == null)
            {
                r = root.gameObject.AddComponent<UiReveal>();
            }
            r.Play(containers);
        }

        // A slider as ULTRAKILL's: an outlined box, filled up to its striped handle.
        public static UiSlider Slider(Transform parent, float x, float y, float w, float h, float value, Action<float> changed)
        {
            RectTransform rt = Rect(parent, "Slider", x, y, w, h);
            var hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
            Image back = Picture(Fill(rt, "Back"), BoxLine, White);
            Sliced(back, 3f);
            float inset = 6f;
            RectTransform area = Fill(rt, "Area", inset);
            RectTransform fill = Rect(area, "Fill", 0f, 0f, 10f, 0f);
            Image fi = Picture(fill, SmallFill, new Color(1f, 1f, 1f, 0.28f));
            Sliced(fi, 3f);
            float ih = Mathf.Max(4f, h - 2f * inset);
            RectTransform handle = Rect(area, "Handle", 0f, 0f, Mathf.Round(ih * 1.7f), 0f);
            Image hi = Picture(handle, Handle, White);
            hi.type = UnityEngine.UI.Image.Type.Sliced;
            hi.pixelsPerUnitMultiplier = 108f / ih;
            var s = rt.gameObject.AddComponent<UiSlider>();
            s.Area = area;
            s.FillRect = fill;
            s.HandleRect = handle;
            s.Changed = changed;
            s.Set(value);
            return s;
        }

        // A list that scrolls (with the mouse wheel): rows go in its Content, top down.
        public static RectTransform Scroll(Transform parent, float x, float y, float w, float h, float spacing, out ScrollRect scroll)
        {
            RectTransform view = Rect(parent, "Scroll", x, y, w, h);
            view.gameObject.AddComponent<RectMask2D>();
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            RectTransform content = Rect(view, "Content", 0f, 0f, 0f, 10f);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.offsetMin = new Vector2(0f, content.offsetMin.y);
            content.offsetMax = new Vector2(0f, content.offsetMax.y);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = view;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = false;
            return content;
        }

        // A row in a scrolling list (h tall, its parent's width).
        public static RectTransform Row(Transform content, float h)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(content, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(0f, h);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = h;
            le.minHeight = h;
            return rt;
        }

        public static void Clear(Transform t)
        {
            var list = new List<GameObject>();
            foreach (Transform c in t)
            {
                list.Add(c.gameObject);
            }
            foreach (GameObject g in list)
            {
                g.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(g);
            }
        }

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // A Steam avatar (RGBA, top row first) as a texture.
        public static Texture2D Texture(int w, int h, byte[] rgba)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var flipped = new byte[rgba.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(rgba, y * row, flipped, (h - 1 - y) * row, row);
            }
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            return tex;
        }

        // ULTRAKILL's menu click (its own sound, if it can be found).
        private static AudioClip click;
        private static bool clickLooked;
        private static AudioSource clickSource;

        public static void ClickSound()
        {
            try
            {
                if (!clickLooked)
                {
                    clickLooked = true;
                    foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    {
                        if (c != null && (c.name == "Click1" || c.name == "click" || c.name == "UI_Click"))
                        {
                            click = c;
                            break;
                        }
                    }
                }
                if (click == null)
                {
                    return;
                }
                if (clickSource == null)
                {
                    var go = new GameObject("Killcraft click");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    clickSource = go.AddComponent<AudioSource>();
                    clickSource.ignoreListenerPause = true;
                }
                clickSource.volume = 0.5f * MonoSingleton<PrefsManager>.Instance.GetFloat("allVolume", 1f);
                clickSource.PlayOneShot(click);
            }
            catch
            {
            }
        }
    }

    // A button, as ULTRAKILL's behave: its box in its colour, grey (51%) under the mouse, red while
    // pressed, faded between in a tenth of a second; filled while On (black writing); or held down
    // for a while (Hold seconds: HOLD TO LEAVE), filling as it's held.
    internal sealed class UiButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler
    {
        public Image Back;
        public TextMeshProUGUI Text;
        public Image[] Dots;
        public Action Click;
        public Sprite Line, Filled;
        public Color Accent = Color.white;
        public Color TextOn = Color.black;
        public bool On;
        public bool Interactable = true;
        public float Hold;
        // (Only its text: the box there, for the mouse, but not drawn.)
        public bool Bare;
        private bool hover, down;
        private float downAt;
        private Image holdFill;
        private Color shown = Color.white;
        private bool shownSet;

        public void Set(string label)
        {
            if (Text != null && Text.text != label)
            {
                Text.text = label;
            }
        }

        private Color Wanted()
        {
            if (!Interactable)
            {
                return new Color(0.78f * Accent.r, 0.78f * Accent.g, 0.78f * Accent.b, 0.5f);
            }
            if (down && Hold <= 0f)
            {
                return new Color(1f, 0f, 0f, 1f);
            }
            if (hover && !On)
            {
                return new Color(0.51f * Accent.r, 0.51f * Accent.g, 0.51f * Accent.b, Accent.a);
            }
            return Accent;
        }

        public void Refresh()
        {
            if (Back == null)
            {
                return;
            }
            Back.sprite = On && Interactable ? Filled : Line;
            if (!shownSet)
            {
                shown = Wanted();
                shownSet = true;
            }
            Paint();
        }

        private void Paint()
        {
            Back.color = Bare ? new Color(0f, 0f, 0f, 0f) : shown;
            Color text = !Interactable ? new Color(0.6f, 0.6f, 0.6f, 0.7f) : On ? TextOn : Accent;
            if (Text != null)
            {
                Text.color = text;
            }
            if (Dots != null)
            {
                foreach (Image d in Dots)
                {
                    if (d != null)
                    {
                        d.color = text;
                    }
                }
            }
        }

        // (The mouse onto it: for screens that show the one under the mouse their own way.)
        public Action Hovered;

        public void OnPointerEnter(PointerEventData e)
        {
            hover = true;
            Hovered?.Invoke();
        }

        public void OnPointerExit(PointerEventData e)
        {
            hover = false;
            down = false;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left)
            {
                return;
            }
            down = true;
            downAt = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData e)
        {
            down = false;
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (!Interactable || Hold > 0f || e.button != PointerEventData.InputButton.Left)
            {
                return;
            }
            Ui.ClickSound();
            try
            {
                Click?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"menu: a button failed ({ex.Message})");
            }
        }

        private void OnDisable()
        {
            hover = false;
            down = false;
            shownSet = false;
        }

        private void Update()
        {
            if (Back != null)
            {
                Color want = Wanted();
                // (ULTRAKILL's buttons fade in 0.1 s.)
                shown = Color.Lerp(shown, want, Mathf.Clamp01(Time.unscaledDeltaTime / 0.1f) * 1.5f);
                if (((Vector4)(shown - want)).sqrMagnitude < 0.0001f)
                {
                    shown = want;
                }
                Paint();
            }
            if (Hold <= 0f)
            {
                return;
            }
            if (holdFill == null)
            {
                RectTransform r = Ui.Fill(transform, "Held", 4f);
                r.SetAsFirstSibling();
                holdFill = Ui.Picture(r, Ui.SmallFill, Accent);
                holdFill.type = UnityEngine.UI.Image.Type.Filled;
                holdFill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
                holdFill.fillAmount = 0f;
                Back.transform.SetAsFirstSibling();
            }
            float held = down && Interactable ? (Time.unscaledTime - downAt) / Hold : 0f;
            holdFill.fillAmount = Mathf.Clamp01(held);
            holdFill.color = new Color(Accent.r, Accent.g, Accent.b, 0.55f);
            if (held >= 1f)
            {
                down = false;
                Ui.ClickSound();
                try
                {
                    Click?.Invoke();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"menu: a button failed ({ex.Message})");
                }
            }
        }
    }

    // A slider: dragged or clicked along (0 to 1); its fill up to the handle.
    internal sealed class UiSwitch : MonoBehaviour, IPointerClickHandler
    {
        public Image Pill, KnobImage;
        public RectTransform Knob;
        public Action<bool> Changed;
        public bool On;

        public void Set(bool on)
        {
            On = on;
            // (UltraLink's: an outlined pill, its white knob right when on; left, grey, when off.)
            if (Pill != null)
            {
                Pill.sprite = Ui.SmallLine;
                Pill.color = Color.white;
            }
            if (KnobImage != null)
            {
                KnobImage.color = on ? Color.white : new Color(0.45f, 0.45f, 0.45f);
            }
            if (Knob != null)
            {
                Ui.Place(Knob, on ? 26f : 4f, 4f, 16f, 16f);
            }
        }

        public void OnPointerClick(PointerEventData e)
        {
            Ui.ClickSound();
            Set(!On);
            Changed?.Invoke(On);
        }
    }

    internal sealed class UiDropdown : MonoBehaviour
    {
        public UiButton Button;
        public string[] Choices;
        public Func<int> Get;
        public Action<int> SetChoice;
        public float Size;
        public RectTransform Arrow;
        private GameObject open;
        private int shown = -1;

        public void Show()
        {
            int i = Mathf.Clamp(Get(), 0, Choices.Length - 1);
            if (i == shown)
            {
                return;
            }
            shown = i;
            Button.Set(Choices[i]);
            float tw = Button.Text.GetPreferredValues(Choices[i]).x;
            var lr = (RectTransform)Button.Text.transform;
            Arrow.anchoredPosition = new Vector2(lr.offsetMin.x + tw + 10f, -1f);
        }

        private void Update()
        {
            Show();
        }

        public void Toggle()
        {
            if (open != null)
            {
                Close();
                return;
            }
            Canvas c = GetComponentInParent<Canvas>();
            Transform top = c != null ? c.rootCanvas.transform : transform.root;
            open = new GameObject("Killcraft dropdown", typeof(RectTransform));
            var ort = (RectTransform)open.transform;
            ort.SetParent(top, false);
            ort.anchorMin = Vector2.zero;
            ort.anchorMax = Vector2.one;
            ort.offsetMin = ort.offsetMax = Vector2.zero;
            // (Anywhere else: closed.)
            Image block = open.AddComponent<Image>();
            block.color = new Color(0f, 0f, 0f, 0f);
            block.raycastTarget = true;
            var closer = open.AddComponent<UnityEngine.UI.Button>();
            closer.transition = Selectable.Transition.None;
            closer.onClick.AddListener(Close);
            var me = (RectTransform)transform;
            float w = me.rect.width, ih = me.rect.height - 4f;
            RectTransform box = Ui.Rect(ort, "List", 0f, 0f, w, 8f + Choices.Length * ih);
            var corners = new Vector3[4];
            me.GetWorldCorners(corners);
            box.position = corners[0];
            box.anchoredPosition += new Vector2(0f, -4f);
            Image back = Ui.Picture(box, Ui.SmallLine, Color.white);
            Ui.Sliced(back, 2.5f);
            back.raycastTarget = true;
            Ui.Picture(Ui.Fill(box, "Black", 3f), null, new Color(0.02f, 0.02f, 0.02f, 0.97f)).transform.SetAsFirstSibling();
            for (int n = 0; n < Choices.Length; n++)
            {
                int k = n;
                UiButton item = Ui.Button(box, Choices[n], Size, () =>
                {
                    SetChoice(k);
                    shown = -1;
                    Show();
                    Close();
                }, 4f, 4f + n * ih, w - 8f, ih, TextAlignmentOptions.MidlineLeft, true, 0.01f);
                item.On = n == Mathf.Clamp(Get(), 0, Choices.Length - 1);
                item.Refresh();
            }
            Ui.Refont(ort);
        }

        public void Close()
        {
            if (open != null)
            {
                Destroy(open);
            }
            open = null;
        }

        private void OnDisable()
        {
            Close();
        }
    }

    // The parts of a screen (the children of its containers) appearing row by row, top down: those
    // whose tops are level together, a row every 35 ms, each popping in (a quick fade, a few pixels' drop).
    internal sealed class UiReveal : MonoBehaviour
    {
        private const float Step = 0.035f, Fade = 0.08f;
        private readonly List<(CanvasGroup Group, RectTransform Rt, float At, Vector2 Pos)> items = new List<(CanvasGroup, RectTransform, float, Vector2)>();
        private float started = -1f;

        public void Play(Transform[] containers)
        {
            Finish();
            var found = new List<(RectTransform Rt, float Top, float Left)>();
            var corners = new Vector3[4];
            foreach (Transform c in containers)
            {
                if (c == null)
                {
                    continue;
                }
                foreach (Transform t in c)
                {
                    if (t is RectTransform rt && t.GetComponent<UiRevealSkip>() == null)
                    {
                        rt.GetWorldCorners(corners);
                        Vector3 local = transform.InverseTransformPoint(corners[1]);
                        found.Add((rt, local.y, local.x));
                    }
                }
            }
            // (Top down; within ~12 units counts as one row.)
            found.Sort((a, b) => b.Top.CompareTo(a.Top));
            int row = -1;
            float rowTop = float.MaxValue;
            foreach (var f in found)
            {
                if (rowTop - f.Top > 12f)
                {
                    row++;
                    rowTop = f.Top;
                }
                CanvasGroup g = f.Rt.GetComponent<CanvasGroup>();
                if (g == null)
                {
                    g = f.Rt.gameObject.AddComponent<CanvasGroup>();
                }
                g.alpha = 0f;
                // (Laid out by a list: faded in where it is, not moved.)
                bool laidOut = f.Rt.parent != null && f.Rt.parent.GetComponent<LayoutGroup>() != null;
                items.Add((g, laidOut ? null : f.Rt, row * Step, f.Rt.anchoredPosition));
            }
            started = Time.unscaledTime;
            Update();
        }

        private void Update()
        {
            if (started < 0f)
            {
                return;
            }
            float t = Time.unscaledTime - started;
            bool done = true;
            foreach (var it in items)
            {
                if (it.Group == null)
                {
                    continue;
                }
                float p = Mathf.Clamp01((t - it.At) / Fade);
                it.Group.alpha = p;
                if (it.Rt != null)
                {
                    it.Rt.anchoredPosition = it.Pos + new Vector2(0f, 6f * (1f - p));
                }
                done &= p >= 1f;
            }
            if (done)
            {
                Finish();
            }
        }

        // (Everything in place at once.)
        public void Finish()
        {
            foreach (var it in items)
            {
                if (it.Group != null)
                {
                    it.Group.alpha = 1f;
                }
                if (it.Rt != null)
                {
                    it.Rt.anchoredPosition = it.Pos;
                }
            }
            items.Clear();
            started = -1f;
        }

        private void OnDisable() => Finish();
    }

    // (Not revealed: there from the start, like the screen's background.)
    internal sealed class UiRevealSkip : MonoBehaviour
    {
    }

    internal sealed class UiOpenEffect : MonoBehaviour
    {
        private CanvasGroup group;
        private float at = -1f;
        private const float Length = 0.16f;

        public void Play()
        {
            if (group == null)
            {
                group = GetComponent<CanvasGroup>();
                if (group == null)
                {
                    group = gameObject.AddComponent<CanvasGroup>();
                }
            }
            at = Time.unscaledTime;
            Apply(0f);
        }

        private void Update()
        {
            if (at < 0f)
            {
                return;
            }
            float t = Mathf.Clamp01((Time.unscaledTime - at) / Length);
            Apply(t);
            if (t >= 1f)
            {
                at = -1f;
            }
        }

        private void Apply(float t)
        {
            float e = 1f - Mathf.Pow(1f - t, 3f);
            group.alpha = e;
            // (A canvas's own root isn't scaled: the canvas keeps that.)
            if (GetComponent<Canvas>() == null)
            {
                transform.localScale = Vector3.one * Mathf.Lerp(0.94f, 1f, e);
            }
        }

        private void OnDisable()
        {
            if (group != null)
            {
                group.alpha = 1f;
            }
            transform.localScale = Vector3.one;
            at = -1f;
        }
    }

    internal sealed class UiSlider : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public RectTransform Area, FillRect, HandleRect;
        public Action<float> Changed;
        public float Value { get; private set; }

        public void Set(float v)
        {
            Value = Mathf.Clamp01(v);
            if (Area == null)
            {
                return;
            }
            float w = Area.rect.width;
            if (w <= 1f)
            {
                // (Not laid out yet: next frame.)
                pending = true;
                return;
            }
            float hw = HandleRect != null ? HandleRect.rect.width : 0f;
            float x = Value * Mathf.Max(0f, w - hw);
            if (HandleRect != null)
            {
                HandleRect.anchoredPosition = new Vector2(x, HandleRect.anchoredPosition.y);
            }
            if (FillRect != null)
            {
                FillRect.sizeDelta = new Vector2(Mathf.Max(0f, x + hw * 0.5f), FillRect.sizeDelta.y);
                FillRect.gameObject.SetActive(x + hw * 0.5f > 2f);
            }
        }

        private bool pending = true;

        private void LateUpdate()
        {
            if (pending)
            {
                pending = false;
                Set(Value);
            }
        }

        public void OnPointerDown(PointerEventData e) => Drag(e);

        public void OnDrag(PointerEventData e) => Drag(e);

        private void Drag(PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Area, e.position, e.pressEventCamera, out Vector2 local))
            {
                return;
            }
            Rect r = Area.rect;
            float hw = HandleRect != null ? HandleRect.rect.width : 0f;
            Set((local.x - r.xMin - hw * 0.5f) / Mathf.Max(1f, r.width - hw));
            try
            {
                Changed?.Invoke(Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"menu: a slider failed ({ex.Message})");
            }
        }
    }

    // Typing (the chat, a party code): what the keyboard types, read straight from it, Enter sends,
    // Esc lets go. Only one at a time.
    [HarmonyLib.HarmonyPatch]
    internal static class Typing
    {
        public static object Owner { get; private set; }
        public static string Text { get; private set; } = "";
        private static int max;
        private static Action<string> sent;
        private static bool hooked;
        private static readonly System.Text.StringBuilder typed = new System.Text.StringBuilder();
        private static int startFrame;

        public static bool Active => Owner != null;

        public static void Begin(object owner, int maxLength, Action<string> send, string start = "")
        {
            Owner = owner;
            max = maxLength;
            sent = send;
            Text = start ?? "";
            typed.Clear();
            startFrame = Time.frameCount;
            GameInput(true);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && !hooked)
            {
                kb.onTextInput += OnChar;
                hooked = true;
            }
        }

        public static int EndedFrame { get; private set; } = -1;

        public static void End()
        {
            EndedFrame = Time.frameCount;
            Owner = null;
            sent = null;
            Text = "";
            GameInput(false);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && hooked)
            {
                kb.onTextInput -= OnChar;
            }
            hooked = false;
        }

        // ULTRAKILL's own controls off while typing (W was walking V1, number keys switching guns, ...):
        // its actions disabled, as when it rebinds a key. (Its chat state's input lock alone didn't.)
        private static bool gameInputOff;

        private static void GameInput(bool off)
        {
            if (off == gameInputOff)
            {
                return;
            }
            gameInputOff = off;
            try
            {
                var im = MonoSingleton.GetInstance(typeof(InputManager)) as InputManager;
                if (im != null && im.InputSource != null)
                {
                    if (off)
                    {
                        im.InputSource.Disable();
                    }
                    else
                    {
                        im.InputSource.Enable();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"chat: couldn't {(off ? "hold" : "give back")} ULTRAKILL's controls ({e.Message})");
            }
        }

        // ULTRAKILL's cheat keys (B flight, V noclip, ...) are their own actions, apart from its
        // controls: what they'd toggle, not toggled while typing (nor on the Enter that sends).
        [HarmonyLib.HarmonyPatch(typeof(CheatsManager), nameof(CheatsManager.HandleCheatBind)), HarmonyLib.HarmonyPrefix]
        private static bool CheatKey() => !Active && EndedFrame != Time.frameCount;

        private static void OnChar(char c)
        {
            if (Owner != null && Time.frameCount != startFrame && !char.IsControl(c))
            {
                typed.Append(c);
            }
        }

        // Each frame (Host): the keys typed, Backspace, Enter, Esc.
        public static void Frame()
        {
            if (Owner == null)
            {
                GameInput(false);
                return;
            }
            GameInput(true);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null)
            {
                End();
                return;
            }
            if (typed.Length > 0)
            {
                string t = Text + typed.ToString();
                typed.Clear();
                Text = t.Length > max ? t.Substring(0, max) : t;
            }
            if (kb.backspaceKey.wasPressedThisFrame && Text.Length > 0)
            {
                Text = Text.Substring(0, Text.Length - 1);
            }
            if (kb.ctrlKey.isPressed && kb.vKey.wasPressedThisFrame)
            {
                string paste = (GUIUtility.systemCopyBuffer ?? "").Replace("\n", " ").Replace("\r", "");
                Text = (Text + paste).Length > max ? (Text + paste).Substring(0, max) : Text + paste;
            }
            if (Time.frameCount == startFrame)
            {
                return;
            }
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                Action<string> send = sent;
                string text = Text;
                End();
                send?.Invoke(text);
            }
            else if (kb.escapeKey.wasPressedThisFrame)
            {
                End();
            }
        }

        // (TMP's rich text: what someone typed shown as typed, no tags.)
        public static string Escape(string s) => (s ?? "").Replace("<", "<​");

        // What's typed so far, with a blinking caret.
        public static string Shown(string placeholder)
        {
            bool caret = (int)(Time.unscaledTime * 2f) % 2 == 0;
            return Text.Length == 0 && !caret ? $"<color=#777777>{placeholder}</color>" : Escape(Text) + (caret ? "_" : " ");
        }

        // The same on one line of a box: when it's longer than the box, its end (where the caret
        // is), "..." before it, as a text field scrolls.
        private static string fitFor;
        private static float fitRoom;
        private static int fitFrom;

        public static string ShownIn(TMP_Text box, string prefix, string placeholder)
        {
            if (box == null || Text.Length == 0)
            {
                return prefix + Shown(placeholder);
            }
            box.enableWordWrapping = false;
            box.overflowMode = TextOverflowModes.Overflow;
            float room = box.rectTransform.rect.width - 12f;
            if (Text != fitFor || Mathf.Abs(room - fitRoom) > 0.5f)
            {
                fitFor = Text;
                fitRoom = room;
                fitFrom = 0;
                if (room > 20f)
                {
                    // (Measured with the caret, so it doesn't move as it blinks.)
                    while (fitFrom < Text.Length - 1
                        && box.GetPreferredValues(prefix + (fitFrom > 0 ? "..." : "") + Escape(Text.Substring(fitFrom)) + "_").x > room)
                    {
                        fitFrom += Mathf.Max(1, (Text.Length - fitFrom) / 16);
                    }
                }
            }
            bool caret = (int)(Time.unscaledTime * 2f) % 2 == 0;
            int from = Mathf.Clamp(fitFrom, 0, Text.Length);
            return prefix + (from > 0 ? "..." : "") + Escape(Text.Substring(from)) + (caret ? "_" : " ");
        }
    }

}
