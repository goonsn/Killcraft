using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // Minecraft renders its hand, hotbar, hearts and every open screen offscreen at ULTRAKILL's
    // resolution and publishes the pixels (premultiplied RGBA, bottom-up rows) through the overlay
    // triple buffer. They're shown over ULTRAKILL's frame on a top-most canvas.
    internal static class Overlay
    {
        private static GameObject root;
        private static RawImage image;
        private static Texture2D texture;

        private static void Ensure()
        {
            if (root != null)
            {
                return;
            }
            root = new GameObject("Killcraft overlay");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var child = new GameObject("Minecraft");
            child.transform.SetParent(root.transform, false);
            image = child.AddComponent<RawImage>();
            image.raycastTarget = false;
            RectTransform rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            root.SetActive(false);
        }

        public static void Frame(bool show)
        {
            Ensure();
            if (Link.AcquireOverlayFrame())
            {
                Link.FrontHeader(out int w, out int h, out bool bottomUp);
                if (w > 0 && h > 0 && w <= Proto.MaxOverlayW && h <= Proto.MaxOverlayH)
                {
                    if (texture == null || texture.width != w || texture.height != h)
                    {
                        if (texture != null)
                        {
                            Object.Destroy(texture);
                        }
                        texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                        image.texture = texture;
                    }
                    texture.LoadRawTextureData(Link.FrontPixels, w * h * 4);
                    texture.Apply(false, false);
                    // Unity textures start at the bottom row, so bottom-up pixels need no flip.
                    image.uvRect = bottomUp ? new Rect(0, 0, 1, 1) : new Rect(0, 1, 1, -1);
                }
            }
            bool visible = show && texture != null;
            if (root.activeSelf != visible)
            {
                root.SetActive(visible);
            }
        }
    }
}
