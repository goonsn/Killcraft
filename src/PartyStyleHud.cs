using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // The party style meter (PartyStyle) on screen: ULTRAKILL's own style meter, as it is - its box, its
    // bar, its list of style - showing the party's instead of this player's own: the party's rank and how
    // full, shown while the party's combo lasts, and everyone's style in its list (the others' in their
    // colour). ULTRAKILL still counts this player's own underneath (their rank). Its rank pictures are
    // the party's: ULTRAKILL's own coloured letter (cut from its picture as the game runs), the party's
    // word after it in white as ULTRAKILL's are (design/ranks: Bebas Neue); the last, ULTRAPARTY, bright
    // yellow edged orange as its ULTRAKILL.
    [HarmonyPatch]
    internal static class PartyStyleHud
    {
        private static readonly AccessTools.FieldRef<StyleHUD, GameObject> hudOf = AccessTools.FieldRefAccess<StyleHUD, GameObject>("styleHud");
        private static readonly AccessTools.FieldRef<StyleHUD, Slider> sliderOf = AccessTools.FieldRefAccess<StyleHUD, Slider>("styleSlider");
        private static readonly AccessTools.FieldRef<StyleHUD, float> scaleOf = AccessTools.FieldRefAccess<StyleHUD, float>("rankScale");
        private static readonly AccessTools.FieldRef<StyleHUD, Queue<string>> itemsOf = AccessTools.FieldRefAccess<StyleHUD, Queue<string>>("hudItemsQueue");

        private static bool showing;
        private static int shownRank = -1;
        private static bool loggedFail;
        private static readonly string[] Letters = { "D", "C", "B", "A", "S", "SS", "SSS", "U" };
        private static Sprite[] party;
        private static List<StyleRank> partyFor;

        // The party's rank pictures, made once from ULTRAKILL's (its letters, sizes and pivots).
        private static Sprite PartySprite(List<StyleRank> ranks, int i)
        {
            if (party == null || partyFor != ranks)
            {
                partyFor = ranks;
                party = new Sprite[ranks.Count];
                for (int k = 0; k < ranks.Count && k < Letters.Length; k++)
                {
                    try
                    {
                        party[k] = Make(ranks[k].sprite, Letters[k]);
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"party style: couldn't make the {Letters[k]} rank's picture ({e.Message}); ULTRAKILL's shown");
                    }
                }
                Plugin.Log.LogInfo("party style: the party's rank pictures made");
            }
            return i < party.Length && party[i] != null ? party[i] : ranks[i].sprite;
        }

        private static Texture2D Resource(string name)
        {
            using (System.IO.Stream s = typeof(PartyStyleHud).Assembly.GetManifestResourceStream("Killcraft.ranks." + name + ".png"))
            {
                if (s == null)
                {
                    return null;
                }
                var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                return ImageConversion.LoadImage(t, ms.ToArray()) ? t : null;
            }
        }

        // ULTRAKILL's picture's pixels (its texture isn't readable: drawn into one that is).
        private static Color32[] Pixels(Sprite s, out int w, out int h)
        {
            Rect r = s.textureRect;
            w = Mathf.RoundToInt(r.width);
            h = Mathf.RoundToInt(r.height);
            Texture tex = s.texture;
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture was = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
            read.Apply();
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = read.GetPixels32();
            Object.Destroy(read);
            return px;
        }

        // Made this many times as big as ULTRAKILL's picture (its letter smoothly enlarged, the word drawn
        // that big: design/ranks), with smaller copies for the screen's size: sharp on a big screen.
        private const int Big = 3;

        private static Sprite Make(Sprite uk, string letter)
        {
            if (uk == null)
            {
                return null;
            }
            Texture2D made;
            if (letter == "U")
            {
                made = Resource("rank-U");
            }
            else
            {
                Texture2D word = Resource("word-" + letter);
                if (word == null)
                {
                    return null;
                }
                Color32[] px = Pixels(uk, out int w, out int h);
                // Its coloured letter(s) kept, its white word gone.
                int right = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color32 c = px[y * w + x];
                        bool white = c.r > 200 && c.g > 200 && c.b > 200;
                        bool grey = System.Math.Abs(c.r - c.g) < 30 && System.Math.Abs(c.g - c.b) < 30 && c.r > 90;
                        if (c.a == 0 || white || grey)
                        {
                            px[y * w + x] = new Color32(0, 0, 0, 0);
                        }
                        else
                        {
                            right = Mathf.Max(right, x);
                        }
                    }
                }
                var small = new Texture2D(w, h, TextureFormat.RGBA32, false);
                small.SetPixels32(px);
                small.Apply();
                int W = w * Big, H = h * Big;
                var outPx = new Color32[W * H];
                // The letter, enlarged (only as far as it goes).
                int letterW = Mathf.Min(W, (right + 2) * Big);
                for (int y = 0; y < H; y++)
                {
                    float v = (y + 0.5f) / H;
                    for (int x = 0; x < letterW; x++)
                    {
                        outPx[y * W + x] = small.GetPixelBilinear((x + 0.5f) / W, v);
                    }
                }
                Object.Destroy(small);
                // The word after it, its capitals where ULTRAKILL's are (36 px down a 100 px picture;
                // the word's picture is for a 300 px one).
                float unit = H / 100f;
                float k = H / 300f;
                int x0 = Mathf.RoundToInt((right + 1) * Big + 5f * unit);
                int ww = Mathf.Min(Mathf.RoundToInt(word.width * k), W - x0 - Mathf.RoundToInt(2f * unit));
                int wh = Mathf.RoundToInt(word.height * k);
                int top = Mathf.RoundToInt(36f * unit);
                bool exact = ww == word.width && wh == word.height;
                Color32[] wpx = exact ? word.GetPixels32() : null;
                for (int y = 0; y < wh; y++)
                {
                    int ty = H - 1 - (top + y);
                    if (ty < 0 || ty >= H)
                    {
                        continue;
                    }
                    for (int x = 0; x < ww; x++)
                    {
                        int tx = x0 + x;
                        if (tx < 0 || tx >= W)
                        {
                            continue;
                        }
                        float a = exact ? wpx[(wh - 1 - y) * word.width + x].a / 255f
                            : word.GetPixelBilinear((x + 0.5f) / ww, 1f - (y + 0.5f) / wh).a;
                        if (a > 0f)
                        {
                            Color32 had = outPx[ty * W + tx];
                            outPx[ty * W + tx] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.Max(had.a, a * 255f), 0f, 255f));
                        }
                    }
                }
                Object.Destroy(word);
                made = new Texture2D(W, H, TextureFormat.RGBA32, true);
                made.SetPixels32(outPx);
                made.Apply(true);
            }
            if (made == null)
            {
                return null;
            }
            made.name = "Killcraft party rank " + letter;
            made.filterMode = FilterMode.Trilinear;
            made.anisoLevel = 4;
            made.wrapMode = TextureWrapMode.Clamp;
            Object.DontDestroyOnLoad(made);
            var pivot = new Vector2(uk.pivot.x / uk.rect.width, uk.pivot.y / uk.rect.height);
            // (As big on screen as ULTRAKILL's: as many more pixels to a unit.)
            float ppu = uk.pixelsPerUnit * made.width / Mathf.Max(1f, uk.rect.width);
            Sprite s = Sprite.Create(made, new Rect(0f, 0f, made.width, made.height), pivot, ppu);
            s.name = made.name;
            Object.DontDestroyOnLoad(s);
            return s;
        }

        // (PartyStyle: another player's style, into ULTRAKILL's list.)
        public static void Item(int id, string label)
        {
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                Queue<string> q = sh != null ? itemsOf(sh) : null;
                if (q == null || string.IsNullOrEmpty(label))
                {
                    return;
                }
                // ("+ NAME": the name in their colour; ULTRAKILL takes each line off at its "+".)
                string rest = label.StartsWith("+ ") ? label.Substring(2) : label;
                q.Enqueue($"+ <color=#{Ui.Hex(Profiles.Of(id).Color)}>{rest}</color>");
            }
            catch
            {
            }
        }

        // (PartyStyle: points from anyone: the rank picture's little jump, as ULTRAKILL gives it.)
        public static void Bump()
        {
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null)
                {
                    scaleOf(sh) = 0.2f;
                }
            }
            catch
            {
            }
        }

        // After StyleHUD's own Update: what it shows, the party's.
        [HarmonyPatch(typeof(StyleHUD), "Update"), HarmonyPostfix]
        private static void AfterUpdate(StyleHUD __instance)
        {
            try
            {
                // (Minos Prime's player: no style meter at all - V1's.)
                if (V1HudOff.Off)
                {
                    GameObject meter = hudOf(__instance);
                    if (meter != null && meter.activeSelf)
                    {
                        meter.SetActive(false);
                    }
                    return;
                }
                bool party = PartyStyle.Active;
                if (!party)
                {
                    if (showing)
                    {
                        // (Back to this player's own.)
                        showing = false;
                        shownRank = -1;
                        __instance.rankImage.sprite = __instance.currentRank.sprite;
                    }
                    return;
                }
                showing = true;
                GameObject hud = hudOf(__instance);
                bool on = PartyStyle.Combo || __instance.forceMeterOn;
                if (hud != null && hud.activeSelf != on)
                {
                    hud.SetActive(on);
                }
                List<StyleRank> ranks = __instance.ranks;
                if (ranks == null || ranks.Count == 0)
                {
                    return;
                }
                int rank = Mathf.Clamp(PartyStyle.Rank, 0, ranks.Count - 1);
                Sprite want = PartySprite(ranks, rank);
                if (rank != shownRank || __instance.rankImage.sprite != want)
                {
                    shownRank = rank;
                    __instance.rankImage.sprite = want;
                }
                Slider s = sliderOf(__instance);
                if (s != null)
                {
                    s.value = PartyStyle.Fill;
                }
            }
            catch (System.Exception e)
            {
                if (!loggedFail)
                {
                    loggedFail = true;
                    Plugin.Log.LogWarning($"party style: couldn't show it in ULTRAKILL's style meter ({e.Message})");
                }
            }
        }
    }
}
