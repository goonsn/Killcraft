using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Killcraft
{
    // F5 in ULTRAKILL mode (Minecraft off), as Minecraft's: first person, then from behind, then from
    // the front, then first person again.
    //
    // What's seen is V1's own third-person body, the one every level's player carries, animated by
    // ULTRAKILL's PlayerAnimations as it plays (running, sliding, dodging, shooting and its kick,
    // punches, the weapon out), only never seen from its own eyes: shown, while the first-person gun
    // and arm aren't. The camera is moved behind V1 (or in front, looking back) after ULTRAKILL has
    // placed it each frame, kept out of walls, as Minecraft's F5 does in Minecraft mode (Host.ThirdPerson).
    // (Drawing it from behind by swapping the camera's view matrix instead froze the picture.)
    [HarmonyLib.HarmonyPatch]
    internal static class UkThirdPerson
    {
        // (Units: two to a Minecraft block. V1 is a little under two blocks tall.)
        // The view narrower than ULTRAKILL's own (105 by default: from close behind, as through a wide
        // lens, V1 was stretched thin and what he holds stuck out), from further back to match: V1 the
        // same size on screen.
        private const float Narrow = 0.6f, Distance = 3.5f / Narrow, Raise = 0.5f;
        private static float fovBefore = -1f, virtualFovBefore = -1f;
        private static Camera virtualCam;
        private const int CameraBlockers = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24) | (1 << 26);

        private static int mode;  // 0 first person, 1 behind, 2 front
        private static bool on;
        private static UnityEngine.InputSystem.Key key = UnityEngine.InputSystem.Key.None;
        private static CameraController cc;
        private static readonly List<(Transform T, int Layer)> bodyLayers = new List<(Transform, int)>();
        private static readonly List<(Renderer R, ShadowCastingMode Shadows, bool Enabled)> bodyRenderers = new List<(Renderer, ShadowCastingMode, bool)>();
        private static readonly HashSet<Renderer> hiddenViewmodel = new HashSet<Renderer>();
        private static readonly HashSet<string> extraLogged = new HashSet<string>();
        private static readonly HashSet<Canvas> hiddenCanvases = new HashSet<Canvas>();
        private static Transform body;
        private static float viewmodelAt;
        private static readonly HarmonyLib.AccessTools.FieldRef<FistControl, List<GameObject>> spawnedArms =
            HarmonyLib.AccessTools.FieldRefAccess<FistControl, List<GameObject>>("spawnedArms");

        // Each frame (Update): the key, and whether third person can be on now (a level, ULTRAKILL mode).
        public static void Frame(bool ultrakillMode, bool inGame, bool paused, CameraController camera)
        {
            if (ultrakillMode && inGame && !paused && !Spectate.Active && !ReplayPlayer.Active && !Typing.Active && !MinosMode.IAmMinos && Pressed())
            {
                mode = (mode + 1) % 3;
                Plugin.Log.LogInfo($"third person: {(mode == 0 ? "off" : mode == 1 ? "from behind" : "from the front")}");
            }
            // (Not as Minos Prime: his own camera, MinosControl.)
            bool want = mode != 0 && ultrakillMode && inGame && camera != null && camera.cam != null && !MinosMode.IAmMinos;
            if (want && !on)
            {
                Start(camera);
            }
            else if (!want && on)
            {
                Stop();
            }
            if (on)
            {
                cc = camera;
                if (body == null)
                {
                    ShowBody();
                }
                if ((viewmodelAt -= Time.unscaledDeltaTime) <= 0f)
                {
                    viewmodelAt = 1f;
                    HideViewmodel();
                    if (classicShown.Count == 0)
                    {
                        ShowClassicHud();
                    }
                }
                ClassicCharge();
                OwnGuns();
            }
        }

        // V1's own gun in third person drawn as its variant's own model (as the others' are).
        private static VariantGuns ownGuns;
        private static PlayerAnimations ownFor;

        private static void OwnGuns()
        {
            try
            {
                var anims = MonoSingleton.GetInstance(typeof(PlayerAnimations)) as PlayerAnimations;
                if (anims == null)
                {
                    return;
                }
                GameObject[] w = V1Body.weaponsOf(anims) ?? new GameObject[0];
                GameObject[] a = V1Body.altWeaponsOf(anims) ?? new GameObject[0];
                if (anims != ownFor || ownGuns == null)
                {
                    ownGuns?.Destroy();
                    ownFor = anims;
                    ownGuns = new VariantGuns(w, a);
                }
                byte weapon = 255;
                bool alt = false;
                for (int i = 0; i < 5; i++)
                {
                    if (i < w.Length && w[i] != null && w[i].activeSelf)
                    {
                        weapon = (byte)i;
                    }
                    if (i < a.Length && a[i] != null && a[i].activeSelf)
                    {
                        weapon = (byte)i;
                        alt = true;
                    }
                }
                int variant = Skins.CurrentVariant();
                if (ownGuns.Show(weapon, alt, variant))
                {
                    Skins.Paint(ownGuns.Made, Profiles.Mine, weapon, alt, variant);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"third person: couldn't show the gun's variant ({e.Message})");
                ownGuns = null;
            }
        }

        // (Also spectating's first / third person.)
        public static bool KeyPressed() => !Typing.Active && Pressed();

        public static string KeyName()
        {
            Pressed();
            return key.ToString().ToUpperInvariant();
        }

        internal static bool Pressed()
        {
            if (key == UnityEngine.InputSystem.Key.None
                && (!Enum.TryParse(Plugin.ThirdPersonKey.Value, true, out key) || key == UnityEngine.InputSystem.Key.None))
            {
                key = UnityEngine.InputSystem.Key.F5;
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

        private static void Start(CameraController camera)
        {
            on = true;
            cc = camera;
            ShowBody();
            HideViewmodel();
            ShowClassicHud();
        }

        private static void Stop()
        {
            on = false;
            ownGuns?.Show(255, false, -1);
            RestoreFov();
            if (cc != null)
            {
                cc.transform.localPosition = cc.defaultPos;
                cc.ApplyRotations();
            }
            HideBody();
            foreach (Renderer r in hiddenViewmodel)
            {
                if (r != null)
                {
                    r.forceRenderingOff = false;
                }
            }
            hiddenViewmodel.Clear();
            foreach (Canvas c in hiddenCanvases)
            {
                if (c != null)
                {
                    c.enabled = true;
                }
            }
            hiddenCanvases.Clear();
            RestoreHud();
        }

        public static void LevelChanged()
        {
            if (on)
            {
                Stop();
            }
            body = null;
            virtualCam = null;
            fovBefore = -1f;
            virtualFovBefore = -1f;
            bodyLayers.Clear();
            bodyRenderers.Clear();
            hiddenViewmodel.Clear();
            hiddenCanvases.Clear();
        }

        // ---- the picture, from behind (or the front) ----

        // After ULTRAKILL's camera has placed itself (Patches.CameraAfter, and Host's LateUpdate after
        // Minecraft mode's own): from V1's eyes to behind them (or in front), short of any wall.
        public static void Place()
        {
            if (!on || cc == null || cc.transform.parent == null)
            {
                return;
            }
            Transform t = cc.transform;
            Vector3 eye = t.parent.localToWorldMatrix.MultiplyPoint3x4(cc.defaultPos);
            Quaternion look = Look();
            Vector3 forward = look * Vector3.forward;
            Vector3 dir = mode == 2 ? forward : -forward;
            Vector3 start = eye + Vector3.up * Raise;
            float dist = Distance;
            if (Physics.SphereCast(eye, 0.3f, (start + dir * dist - eye).normalized, out RaycastHit hit, (start + dir * dist - eye).magnitude, CameraBlockers, QueryTriggerInteraction.Ignore))
            {
                t.position = eye + (start + dir * dist - eye).normalized * Mathf.Max(0f, hit.distance - 0.1f);
            }
            else
            {
                t.position = start + dir * dist;
            }
            t.rotation = mode == 2 ? Quaternion.LookRotation(-forward, look * Vector3.up) : look;
            // (Once a frame: this runs twice.)
            if (fovBefore < 0f && cc.cam != null)
            {
                fovBefore = cc.cam.fieldOfView;
                float narrow = 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fovBefore * 0.5f * Mathf.Deg2Rad) * Narrow);
                cc.cam.fieldOfView = narrow;
                if (virtualCam == null)
                {
                    Transform v = cc.cam.transform.Find("Virtual Camera");
                    virtualCam = v != null ? v.GetComponent<Camera>() : null;
                }
                // (ULTRAKILL's Virtual Camera goes with the main one when it sees as it does.)
                if (virtualCam != null && Mathf.Abs(virtualCam.fieldOfView - fovBefore) < 0.01f)
                {
                    virtualFovBefore = virtualCam.fieldOfView;
                    virtualCam.fieldOfView = narrow;
                }
            }
        }

        // ULTRAKILL's own view back before its camera works out the next frame's (its zoom, its dash
        // widening, the mouse's speed when zoomed all go from it).
        [HarmonyLib.HarmonyPatch(typeof(CameraController), "LateUpdate"), HarmonyLib.HarmonyPrefix]
        private static void CameraBefore() => RestoreFov();

        private static void RestoreFov()
        {
            if (fovBefore >= 0f && cc != null && cc.cam != null)
            {
                cc.cam.fieldOfView = fovBefore;
            }
            if (virtualFovBefore >= 0f && virtualCam != null)
            {
                virtualCam.fieldOfView = virtualFovBefore;
            }
            fovBefore = -1f;
            virtualFovBefore = -1f;
        }

        // Where V1 looks, from ULTRAKILL's look angles, not the camera's own facing: this turns it round
        // for the front view (and runs twice a frame).
        private static Quaternion Look() => cc.transform.parent.rotation * Quaternion.AngleAxis(-cc.rotationX, Vector3.right);

        // V1's body aims its head and gun the way the camera faces: from the front that's backwards,
        // so while it poses the camera faces where V1 looks.
        [HarmonyLib.HarmonyPatch(typeof(PlayerAnimations), "LateUpdate"), HarmonyLib.HarmonyPrefix]
        private static void BodyBefore(out Quaternion? __state)
        {
            __state = null;
            if (on && mode == 2 && cc != null && cc.transform.parent != null)
            {
                __state = cc.transform.rotation;
                cc.transform.rotation = Look();
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(PlayerAnimations), "LateUpdate"), HarmonyLib.HarmonyPostfix]
        private static void BodyAfter(PlayerAnimations __instance, Quaternion? __state)
        {
            if (__state.HasValue && cc != null)
            {
                cc.transform.rotation = __state.Value;
            }
            if (on)
            {
                V1Motion.Apply(__instance);
            }
        }

        // ---- V1's body shown, the first-person gun and arm not ----

        private static void ShowBody()
        {
            var anims = MonoSingleton.GetInstance(typeof(PlayerAnimations)) as PlayerAnimations;
            if (anims == null)
            {
                return;
            }
            body = anims.transform;
            bodyLayers.Clear();
            bodyRenderers.Clear();
            // (Only what ULTRAKILL draws of it now, as a shadow, made seen: what it keeps off (other
            // weapons, effects) stays off. And moved to a layer the camera draws only if it's on one it
            // doesn't.)
            int drawn = cc != null && cc.cam != null ? cc.cam.cullingMask : ~0;
            var log = new System.Text.StringBuilder();
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true))
            {
                log.Append($" {r.name}[{(r.enabled ? "" : "off,")}{r.shadowCastingMode},{r.gameObject.layer}{(r.gameObject.activeInHierarchy ? "" : ",inactive")}]");
                bodyRenderers.Add((r, r.shadowCastingMode, r.enabled));
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
                GameObject g = r.gameObject;
                if ((drawn & (1 << g.layer)) == 0)
                {
                    bodyLayers.Add((g.transform, g.layer));
                    g.layer = 0;
                }
            }
            // (Kept off altogether rather than drawn as a shadow: its body mesh itself shown, still not
            // whatever else is off.)
            bool seen = false;
            foreach (var (r, _, _) in bodyRenderers)
            {
                seen |= r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly;
            }
            if (!seen)
            {
                foreach (SkinnedMeshRenderer r in body.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    r.enabled = true;
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }
            Plugin.Log.LogInfo($"third person: V1's body{(seen ? "" : " (kept off: its skinned meshes shown)")}:{log}");
        }

        private static void HideBody()
        {
            foreach (var (t, layer) in bodyLayers)
            {
                if (t != null)
                {
                    t.gameObject.layer = layer;
                }
            }
            foreach (var (r, shadows, enabled) in bodyRenderers)
            {
                if (r != null)
                {
                    r.shadowCastingMode = shadows;
                    r.enabled = enabled;
                }
            }
            bodyLayers.Clear();
            bodyRenderers.Clear();
            body = null;
        }

        // ULTRAKILL's classic HUD (its other kind: flat on the screen, the guns' charges with it) while
        // in third person: the standard one is fixed in front of the eye, by the guns, and was hidden
        // with them (it floated about once the camera moved back).
        private static readonly List<HudController> classicShown = new List<HudController>();

        private static void ShowClassicHud()
        {
            int type = MonoSingleton.GetInstance(typeof(PrefsManager)) is PrefsManager prefs ? prefs.GetInt("hudType") : 1;
            bool colorless = type == 3;
            foreach (HudController hc in UnityEngine.Object.FindObjectsOfType<HudController>(true))
            {
                if (hc == null || !hc.altHud || hc.colorless != colorless || hc.transform.childCount == 0 || classicShown.Contains(hc))
                {
                    continue;
                }
                GameObject panel = hc.transform.GetChild(0).gameObject;
                classicShown.Add(hc);
                if (!panel.activeSelf)
                {
                    panel.SetActive(true);
                }
                if (extraLogged.Add("classic " + hc.name))
                {
                    Canvas canvas = hc.GetComponentInParent<Canvas>() ?? panel.GetComponentInChildren<Canvas>(true);
                    Plugin.Log.LogInfo($"third person: ULTRAKILL's classic HUD shown ({hc.name}, {(canvas != null ? canvas.renderMode.ToString() : "no canvas")})");
                }
            }
        }

        private static void RestoreHud()
        {
            foreach (HudController hc in classicShown)
            {
                if (hc != null)
                {
                    hc.CheckSituation();
                }
            }
            classicShown.Clear();
            classicCharge.Clear();
            chargeLooked = false;
        }

        // The classic HUD's CHARGE (the railcannon's): ULTRAKILL fills it from the standard HUD's
        // meter, which didn't while that was hidden (it stayed full after a shot). Filled here as
        // ULTRAKILL fills its own.
        private static readonly List<UnityEngine.UI.Image> classicCharge = new List<UnityEngine.UI.Image>();
        private static bool chargeLooked;

        private static void ClassicCharge()
        {
            if (classicShown.Count == 0
                || !(MonoSingleton.GetInstance(typeof(RailcannonMeter)) is RailcannonMeter rm)
                || !(MonoSingleton.GetInstance(typeof(WeaponCharges)) is WeaponCharges wc))
            {
                return;
            }
            if (!chargeLooked)
            {
                chargeLooked = true;
                classicCharge.Clear();
                var panels = rm.altHudPanels ?? new GameObject[0];
                var known = new HashSet<UnityEngine.UI.Image>(rm.meters ?? new UnityEngine.UI.Image[0]);
                var log = new System.Text.StringBuilder();
                foreach (HudController hc in classicShown)
                {
                    if (hc == null)
                    {
                        continue;
                    }
                    foreach (UnityEngine.UI.Image img in hc.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    {
                        if (img.type != UnityEngine.UI.Image.Type.Filled)
                        {
                            continue;
                        }
                        bool inPanel = false;
                        foreach (GameObject panel in panels)
                        {
                            inPanel |= panel != null && img.transform.IsChildOf(panel.transform);
                        }
                        if (inPanel || known.Contains(img) || img == rm.colorlessMeter)
                        {
                            classicCharge.Add(img);
                            log.Append($" {img.name}{(known.Contains(img) || img == rm.colorlessMeter ? " (ULTRAKILL's own)" : "")}");
                        }
                    }
                }
                Plugin.Log.LogInfo($"third person: the classic HUD's charge meter:{(log.Length > 0 ? log.ToString() : " none found")}");
            }
            float charge = wc.raicharge;
            var colours = MonoSingleton.GetInstance(typeof(ColorBlindSettings)) as ColorBlindSettings;
            foreach (UnityEngine.UI.Image img in classicCharge)
            {
                if (img == null)
                {
                    continue;
                }
                img.fillAmount = Mathf.Clamp01(charge / 4f);
                if (colours != null && img != rm.colorlessMeter)
                {
                    img.color = colours.GetHudColor(charge > 4f ? HudColorType.railcannonFull : HudColorType.railcannonCharging);
                }
            }
        }

        private static bool IsClassicHud(Component c)
        {
            foreach (HudController hc in c.GetComponentsInParent<HudController>(true))
            {
                if (hc.altHud)
                {
                    return true;
                }
            }
            return false;
        }

        private static void HideCanvas(Canvas c)
        {
            if (c != null && c.enabled && (body == null || !c.transform.IsChildOf(body)) && !IsClassicHud(c) && hiddenCanvases.Add(c))
            {
                c.enabled = false;
                if (extraLogged.Add("canvas " + c.name))
                {
                    Plugin.Log.LogInfo($"third person: hid the gun's {c.name} (canvas, layer {c.gameObject.layer})");
                }
            }
        }

        private static void HideViewmodel()
        {
            var roots = new List<GameObject>();
            if (MonoSingleton.GetInstance(typeof(GunControl)) is GunControl guns && guns.slots != null)
            {
                foreach (List<GameObject> slot in guns.slots)
                {
                    if (slot != null)
                    {
                        roots.AddRange(slot);
                    }
                }
            }
            if (MonoSingleton.GetInstance(typeof(FistControl)) is FistControl fists && spawnedArms(fists) is List<GameObject> arms)
            {
                roots.AddRange(arms);
            }
            foreach (DualWield dual in UnityEngine.Object.FindObjectsOfType<DualWield>())
            {
                roots.Add(dual.gameObject);
            }
            // (And whatever else hangs on the camera on the guns' own layers: their extra parts. Only
            // those: ULTRAKILL's picture itself is put on screen by things on the camera, and hiding
            // those froze it.)
            var gunLayers = new HashSet<int>();
            foreach (GameObject root in roots)
            {
                if (root != null)
                {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        gunLayers.Add(r.gameObject.layer);
                    }
                }
            }
            // The guns' own meters (the chainsaw shotgun's fuel, the rocket launcher's dial, ...) are
            // canvases on them, not renderers.
            foreach (GameObject root in roots)
            {
                if (root != null)
                {
                    foreach (Canvas c in root.GetComponentsInChildren<Canvas>(true))
                    {
                        HideCanvas(c);
                    }
                }
            }
            gunLayers.Remove(0);
            gunLayers.Remove(5);
            if (cc != null && gunLayers.Count > 0)
            {
                foreach (Renderer r in cc.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && gunLayers.Contains(r.gameObject.layer) && r.GetComponentInParent<Canvas>() == null
                        && (body == null || !r.transform.IsChildOf(body)) && hiddenViewmodel.Add(r))
                    {
                        r.forceRenderingOff = true;
                        if (extraLogged.Add(r.name))
                        {
                            Plugin.Log.LogInfo($"third person: hid {r.name} (layer {r.gameObject.layer}) from the camera too");
                        }
                    }
                }
                foreach (Canvas c in cc.GetComponentsInChildren<Canvas>(true))
                {
                    if (c != null && gunLayers.Contains(c.gameObject.layer))
                    {
                        HideCanvas(c);
                    }
                }
            }
            foreach (GameObject root in roots)
            {
                if (root == null)
                {
                    continue;
                }
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && (body == null || !r.transform.IsChildOf(body)) && hiddenViewmodel.Add(r))
                    {
                        r.forceRenderingOff = true;
                    }
                }
            }
        }
    }
}
