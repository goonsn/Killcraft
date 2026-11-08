using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Killcraft
{
    // The mod's own LOADING, over everything while a level picked through the party screen loads (the
    // end of a WAKE): ULTRAKILL's main menu fall behind it, still falling, in big dark pixels; LOADING,
    // and the mod's logo and name.
    // (While a level loads ULTRAKILL stops the main menu dead, so the fall is filmed during the WAKE's
    // 3 seconds and played round and round here, its end faded into its start.)
    // And plain black (nothing on it) from the main menu's first frame back from QUIT MISSION in a
    // party until the party screen's up: the main menu not seen on the way.
    internal static class LoadingScreen
    {
        private static Canvas canvas;
        private static CanvasGroup group;
        private static RawImage fall, fallOver;
        private static RectTransform middle;
        private static Image logo;
        private static TextMeshProUGUI brand;
        private static RenderTexture snapshot;
        private static bool up, sawLoad, forParty;
        private static float shownAt, loadedAt = -1f, fadeFrom = -1f;

        public static bool Up => up;

        // (Back from QUIT MISSION in a party: black at once as the main menu's loaded, before it's drawn.)
        public static void Hook()
        {
            SceneManager.sceneLoaded += (s, m) =>
            {
                try
                {
                    if (PartyMenu.OpenOnMenu && SteamNet.InLobby && IsMainMenu(s))
                    {
                        ShowBlack();
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"loading screen: {e.Message}");
                }
            };
        }

        // (The main menu's scene: SceneHelper's name for it, or its menus in it - scenes are named by ids.)
        internal static bool IsMainMenu(Scene s)
        {
            if (SceneHelper.CurrentScene == "Main Menu")
            {
                return true;
            }
            foreach (GameObject root in s.GetRootGameObjects())
            {
                if (root.transform.Find("Main Menu (1)") != null)
                {
                    return true;
                }
            }
            return false;
        }

        // A level from the party screen: the fall, LOADING, the logo.
        public static void Show()
        {
            try
            {
                Build();
                if (filmed < Overlap * 2)
                {
                    Snap();
                }
                logo.sprite = Brand.Logo;
                brand.text = Brand.Name;
                Ui.Refont(canvas.transform);
                fall.gameObject.SetActive(filmed >= Overlap * 2 || fall.texture != null);
                middle.gameObject.SetActive(true);
                Raise(false);
                Play(Time.unscaledTime);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"loading screen: couldn't show it ({e.Message})");
            }
        }

        private static void ShowBlack()
        {
            Build();
            fall.gameObject.SetActive(false);
            fallOver.gameObject.SetActive(false);
            middle.gameObject.SetActive(false);
            Raise(true);
            // (Already loaded: waiting for the party screen only.)
            sawLoad = true;
        }

        private static void Raise(bool untilPartyScreen)
        {
            canvas.gameObject.SetActive(true);
            group.alpha = 1f;
            up = true;
            sawLoad = SceneHelper.PendingScene != null;
            forParty = untilPartyScreen;
            shownAt = Time.unscaledTime;
            loadedAt = -1f;
            fadeFrom = -1f;
        }

        // ---- the fall, filmed ----

        private const int FilmRate = 30, FilmMax = 100, Overlap = 10;
        private static readonly RenderTexture[] film = new RenderTexture[FilmMax];
        private static int filmed;
        private static float filmedAt = -1f;

        // (A WAKE begun: filmed from now.)
        public static void StartFilming()
        {
            filmed = 0;
            filmedAt = -1f;
        }

        // Each frame of the WAKE: the main menu's fall as ULTRAKILL drew it (under the menus), 30 a second.
        public static void Film()
        {
            if (filmed >= FilmMax || (filmedAt >= 0f && Time.unscaledTime - filmedAt < 1f / FilmRate))
            {
                return;
            }
            try
            {
                PostProcessV2_Handler pp = MonoSingleton<PostProcessV2_Handler>.Instance;
                RenderTexture from = pp != null ? pp.mainTex : null;
                if (from == null)
                {
                    return;
                }
                if (film[filmed] == null)
                {
                    film[filmed] = new RenderTexture(240, 135, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Killcraft loading fall " + filmed };
                    film[filmed].Create();
                }
                Graphics.Blit(from, film[filmed]);
                filmed++;
                filmedAt = Time.unscaledTime;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"loading screen: couldn't film the fall ({e.Message})");
                filmed = FilmMax;
            }
        }

        // Round and round: frames Overlap..filmed-1, the last Overlap of them fading into the first.
        private static void Play(float now)
        {
            if (filmed < Overlap * 2)
            {
                fallOver.gameObject.SetActive(false);
                return;
            }
            int loop = filmed - Overlap;
            int j = Overlap + (int)((now - shownAt) * FilmRate) % loop;
            fall.texture = film[j];
            int m = j - (filmed - Overlap);
            if (m >= 0)
            {
                fallOver.gameObject.SetActive(true);
                fallOver.texture = film[m];
                fallOver.color = new Color(fall.color.r, fall.color.g, fall.color.b, (m + 1f) / Overlap);
            }
            else
            {
                fallOver.gameObject.SetActive(false);
            }
        }

        // The main menu's fall as ULTRAKILL last drew it (its picture before the menus go over it),
        // shrunk to big pixels.
        private static void Snap()
        {
            RenderTexture from = null;
            try
            {
                PostProcessV2_Handler pp = MonoSingleton<PostProcessV2_Handler>.Instance;
                from = pp != null ? pp.mainTex : null;
            }
            catch
            {
            }
            if (from == null)
            {
                fall.texture = null;
                return;
            }
            if (snapshot == null)
            {
                snapshot = new RenderTexture(192, 108, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Killcraft loading fall" };
                snapshot.Create();
            }
            Graphics.Blit(from, snapshot);
            fall.texture = snapshot;
        }

        public static void Hide()
        {
            if (up && fadeFrom < 0f)
            {
                fadeFrom = Time.unscaledTime;
            }
        }

        public static void Frame()
        {
            if (!up || canvas == null)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (fall.gameObject.activeSelf)
            {
                Play(now);
            }
            if (fadeFrom >= 0f)
            {
                float a = 1f - (now - fadeFrom) / 0.25f;
                group.alpha = Mathf.Clamp01(a);
                if (a <= 0f)
                {
                    up = false;
                    canvas.gameObject.SetActive(false);
                }
                return;
            }
            if (SceneHelper.PendingScene != null)
            {
                sawLoad = true;
                loadedAt = -1f;
                return;
            }
            if (!sawLoad && now - shownAt < 2f)
            {
                return;
            }
            if (loadedAt < 0f)
            {
                loadedAt = now;
            }
            if (forParty)
            {
                if (PartyMenu.Open || now - loadedAt > 4f || !(SceneHelper.CurrentScene ?? "").Contains("Main Menu"))
                {
                    Hide();
                }
                return;
            }
            if (now - loadedAt > 0.15f)
            {
                Hide();
            }
        }

        private static void Build()
        {
            if (canvas != null)
            {
                return;
            }
            canvas = Ui.Canvas("Killcraft loading", 32000, true);
            group = canvas.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = true;
            Transform root = canvas.transform;
            Ui.Picture(Ui.Fill(root, "Black"), null, Color.black).raycastTarget = true;
            RectTransform f = Ui.Fill(root, "Fall");
            fall = f.gameObject.AddComponent<RawImage>();
            fall.raycastTarget = false;
            fall.color = new Color(0.56f, 0.56f, 0.56f, 1f);
            RectTransform fo = Ui.Fill(root, "Fall over");
            fallOver = fo.gameObject.AddComponent<RawImage>();
            fallOver.raycastTarget = false;
            fallOver.gameObject.SetActive(false);
            middle = Ui.Rect(root, "Middle", 0f, 0f, 700f, 200f);
            middle.anchorMin = middle.anchorMax = new Vector2(0.5f, 0.5f);
            middle.pivot = new Vector2(0.5f, 0.5f);
            middle.anchoredPosition = new Vector2(0f, 20f);
            TextMeshProUGUI word = Ui.Text(middle, "LOADING", 110f, Color.white, TextAlignmentOptions.Center, 0f, 0f, 700f, 120f);
            word.overflowMode = TextOverflowModes.Overflow;
            // The logo and the name under it, side by side, centred together.
            RectTransform under = Ui.Rect(middle, "Brand", 0f, 112f, 700f, 64f);
            var row = under.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter;
            row.spacing = 10f;
            row.childControlWidth = row.childControlHeight = false;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            logo = Ui.Image(under, Brand.Logo, Ui.Red, 0f, 0f, 52f, 52f);
            logo.preserveAspect = true;
            brand = Ui.Text(under, Brand.Name, 52f, Ui.Red, TextAlignmentOptions.MidlineLeft, 0f, 0f, 10f, 60f);
            brand.overflowMode = TextOverflowModes.Overflow;
            var fit = brand.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            canvas.gameObject.SetActive(false);
        }
    }
}
