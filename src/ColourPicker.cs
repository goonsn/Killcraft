using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Killcraft
{
    // A colour wheel (the KILLNET panel's YOUR LOOK): round it the hue, out from its middle how strong,
    // and a bar beside it for how bright. What it shows follows the mouse while dragging; the colour is
    // put on (saved, V1 recoloured, the others told) when the mouse lets go.
    internal static class ColourPicker
    {
        private static Sprite wheel;

        // The wheel's picture: hue round it (red at the right, then yellow, green, ... anticlockwise),
        // white in the middle, full colour at the rim; its edge smooth.
        public static Sprite Wheel
        {
            get
            {
                if (wheel != null)
                {
                    return wheel;
                }
                const int s = 256;
                var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[s * s];
                float r = s / 2f;
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        float dx = x + 0.5f - r, dy = y + 0.5f - r;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(r - d);
                        float hue = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                        Color c = Color.HSVToRGB(hue, Mathf.Clamp01(d / r), 1f);
                        px[y * s + x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(a * 255f));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                return wheel = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            }
        }

        // A point on the wheel (from its middle, its radius 1) for a hue and strength, and back.
        public static Vector2 At(float hue, float sat) => new Vector2(Mathf.Cos(hue * 2f * Mathf.PI), Mathf.Sin(hue * 2f * Mathf.PI)) * sat;

        public static void From(Vector2 p, out float hue, out float sat)
        {
            hue = Mathf.Repeat(Mathf.Atan2(p.y, p.x) / (2f * Mathf.PI), 1f);
            sat = Mathf.Clamp01(p.magnitude);
        }
    }

    // The wheel's and the bar's mouse: where in it (0-1 across, 0-1 up), while held and when let go.
    internal sealed class UiPickArea : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public Action<Vector2> Moved;
        public Action Released;
        private bool held;

        public void OnPointerDown(PointerEventData e)
        {
            held = true;
            Move(e);
        }

        public void OnDrag(PointerEventData e) => Move(e);

        private void OnDisable()
        {
            if (held)
            {
                OnPointerUp(null);
            }
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (!held)
            {
                return;
            }
            held = false;
            try
            {
                Released?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"menu: the colour wheel failed ({ex.Message})");
            }
        }

        private void Move(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out Vector2 local))
            {
                return;
            }
            Rect r = rt.rect;
            try
            {
                Moved?.Invoke(new Vector2((local.x - r.xMin) / Mathf.Max(1f, r.width), (local.y - r.yMin) / Mathf.Max(1f, r.height)));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"menu: the colour wheel failed ({ex.Message})");
            }
        }
    }
}
