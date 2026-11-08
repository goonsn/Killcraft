using UnityEngine;

namespace Killcraft
{
    // PvP: a parried hit coming back, parried again (Pvp.ParryBack): an ULTRAPARRY. A big blast (ULTRAKILL's
    // own super explosion, bigger each time round, harmless: the hit itself goes back), a screen shake
    // and a heap of style for whoever did it; the other player sees the blast where it happened.
    internal static class UltraParry
    {
        // (A little longer than a parry's: the hit comes back over the network.)
        public const float Window = 0.45f;
        private const string StyleId = "killcraft.ultraparry";
        private static StyleHUD registeredOn;

        public static void Boom(Vector3 at, int chain, bool mine)
        {
            int deep = Mathf.Max(1, chain - 1);
            try
            {
                GameObject prefab = MonoSingleton<DefaultReferenceManager>.Instance.superExplosion;
                if (prefab != null)
                {
                    GameObject go = Object.Instantiate(prefab, at, Quaternion.identity);
                    go.transform.localScale *= Mathf.Min(1.5f + 0.5f * deep, 4f);
                    foreach (Explosion e in go.GetComponentsInChildren<Explosion>(true))
                    {
                        e.harmless = true;
                        e.maxSize *= Mathf.Min(1.5f + 0.5f * deep, 4f);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"ultraparry: no blast ({e.Message})");
            }
            if (!mine)
            {
                return;
            }
            try
            {
                MonoSingleton<CameraController>.Instance.CameraShake(1.5f + 0.5f * deep);
            }
            catch
            {
            }
            try
            {
                StyleHUD style = MonoSingleton<StyleHUD>.Instance;
                // (Each level has its own style meter.)
                if (registeredOn != style)
                {
                    registeredOn = style;
                    try
                    {
                        style.RegisterStyleItem(StyleId, "<color=#ff2020>ULTRA</color>PARRY");
                    }
                    catch (System.ArgumentException)
                    {
                    }
                }
                // (Insane: more than a whole level's worth of most kills, more each time round.)
                style.AddPoints(2500 * deep, StyleId, null, null, -1, "", deep > 1 ? $" x{deep}" : "");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"ultraparry: no style ({e.Message})");
            }
        }
    }
}
