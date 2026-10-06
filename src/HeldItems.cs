using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's skulls in Minecraft's inventory (Killcraft's mod, mcmod's HeldItems). V1's hand
    // holds one item; while Minecraft has the player, picking up another skull puts the one in hand
    // away instead (into a hidden stash), so V1 carries any number. What V1 carries is published
    // (Combat: a dead actor, which SkyCraft makes no stand-in for: its level is the ItemType in hand,
    // X/Y/Z how many blue/red/green skulls in all), and the mod keeps those in the player's inventory.
    // The skull selected in Minecraft's hotbar goes into V1's hand (out of the stash) and shows in
    // front of the camera; any other selection leaves it in the hidden arm. Shown, it is held the way
    // ULTRAKILL holds it, mirrored to the right (where Minecraft's hand is): V1's arm stays active but
    // invisible (Lockout), its holding animation playing, and the skull follows its hand mirrored.
    internal static class HeldItems
    {
        public const uint StateId = 0x3FFFFFF0;
        public const string StateName = "Killcraft held";

        private static int selected;
        private static float selectedAt = -100f;
        private static ItemIdentifier shown;
        private static Transform view;
        private static GameObject stashRoot;
        // The arm left active (invisible) for its holding animation, and what was hidden of it.
        public static GameObject GhostArm { get; private set; }
        private static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private static Animator ghostAnimator;
        private static AnimatorCullingMode ghostCulling;
        private static readonly List<ItemIdentifier> stash = new List<ItemIdentifier>();

        public static bool IsSkull(ItemIdentifier item) => item != null
            && (item.itemType == ItemType.SkullBlue || item.itemType == ItemType.SkullRed || item.itemType == ItemType.SkullGreen);

        private static FistControl Fists => MonoSingleton.GetInstance(typeof(FistControl)) as FistControl;

        public static int HeldType
        {
            get
            {
                FistControl fists = Fists;
                ItemIdentifier held = fists != null ? fists.heldObject : null;
                return held != null ? (int)held.itemType : 0;
            }
        }

        // How many of a skull V1 carries: in hand and stashed.
        public static int Carried(ItemType type)
        {
            int n = HeldType == (int)type ? 1 : 0;
            foreach (ItemIdentifier item in stash)
            {
                n += item != null && item.itemType == type ? 1 : 0;
            }
            return n;
        }

        public static void Selected(int itemType)
        {
            selected = itemType;
            selectedAt = Time.unscaledTime;
        }

        public static void LevelChanged()
        {
            stash.Clear();
            shown = null;
            EndGhost();
        }

        private static readonly AccessTools.FieldRef<Punch, bool> hasHeldItem = AccessTools.FieldRefAccess<Punch, bool>("hasHeldItem");

        // Just before a punch picks up an item (Patches): a skull in V1's hand goes into the stash
        // first, so the punch's hand is free for the new one.
        public static void BeforePickUp(Punch punch, Transform target)
        {
            FistControl fists = Fists;
            if (fists == null || punch == null || target == null || target.gameObject.layer != 22)
            {
                return;
            }
            ItemIdentifier item = target.GetComponent<ItemIdentifier>();
            ItemIdentifier held = fists.heldObject;
            if (item == null || item == held || target.GetComponent<ItemPlaceZone>() != null || !IsSkull(held))
            {
                return;
            }
            Stash(punch, fists, held);
            hasHeldItem(punch) = false;
        }

        private static void Stash(Punch punch, FistControl fists, ItemIdentifier held)
        {
            if (shown == held)
            {
                shown = null;
            }
            if (stashRoot == null)
            {
                stashRoot = new GameObject("Killcraft skull stash");
                stashRoot.SetActive(false);
            }
            held.transform.SetParent(stashRoot.transform, false);
            stash.Add(held);
            punch.heldItem = null;
            fists.heldObject = null;
            punch.ResetHeldState();
        }

        public static void Frame(bool minecraftHasPlayer, CameraController cc)
        {
            stash.RemoveAll(item => item == null);
            FistControl fists = Fists;
            Punch punch = fists != null ? fists.currentPunch : null;
            // (Minecraft says every tenth of a second; a lagging Minecraft can take a while.)
            bool fresh = Time.unscaledTime - selectedAt < 3f;
            // The selected skull into V1's hand, out of the stash (the one in hand into it).
            if (minecraftHasPlayer && fresh && selected != 0 && punch != null && (fists.heldObject == null || IsSkull(fists.heldObject))
                && HeldType != selected)
            {
                ItemIdentifier wanted = stash.Find(item => item != null && (int)item.itemType == selected);
                if (wanted != null)
                {
                    if (fists.heldObject != null)
                    {
                        Stash(punch, fists, fists.heldObject);
                    }
                    stash.Remove(wanted);
                    punch.ForceHold(wanted);
                }
            }

            ItemIdentifier held = fists != null ? fists.heldObject : null;
            bool show = minecraftHasPlayer && IsSkull(held) && cc != null && cc.cam != null && fresh && selected == (int)held.itemType;
            if (shown != null && (!show || shown != held))
            {
                // Back into the (hidden) arm, the way ULTRAKILL holds it. Not one that has left the
                // hand meanwhile (placed in an altar, stashed): that's where it was put.
                if (fists != null && shown == fists.heldObject && punch != null && punch.holder != null)
                {
                    shown.transform.SetParent(punch.holder, true);
                    punch.ResetHeldItemPosition();
                }
                shown = null;
                EndGhost();
            }
            if (show && shown == null)
            {
                if (view == null)
                {
                    view = new GameObject("Killcraft held item").transform;
                }
                if (view.parent != cc.cam.transform)
                {
                    view.SetParent(cc.cam.transform, false);
                }
                view.localPosition = Vector3.zero;
                view.localRotation = Quaternion.identity;
                view.localScale = Vector3.one;
                // How ULTRAKILL has it in the hand (its place, turn and size there), kept for Pose.
                // (As Punch.ResetHeldItemPosition puts it.)
                shown = held;
                shownAt = Time.unscaledTime;
                heldPosition = held.reverseTransformSettings ? held.putDownPosition : Vector3.zero;
                heldRotation = held.reverseTransformSettings ? Quaternion.Euler(held.putDownRotation) : Quaternion.identity;
                heldScale = held.reverseTransformSettings ? held.putDownScale : Vector3.one;
                // (The arm first: switched on, ULTRAKILL puts what it holds back into its hand.)
                StartGhost(fists, punch, held);
                shown.transform.SetParent(view, false);
            }
        }

        // The skull follows V1's (invisible) hand, mirrored to the right of the camera. After the
        // arm's animation has moved the hand this frame (Host.LateUpdate).
        public static void Pose(CameraController cc)
        {
            var fists = Fists;
            Punch punch = fists != null ? fists.currentPunch : null;
            if (shown == null || view == null || punch == null || punch.holder == null || cc == null || cc.cam == null)
            {
                return;
            }
            // Where the skull is in the hand, seen from the camera, then mirrored left to right: taken
            // once the arm has settled into holding it (its pick-up, a punch or sway would move it
            // about), and kept like that from then on.
            // (The arm switched on again, Punch.OnEnable, takes it back into its hand.)
            if (shown.transform.parent != view)
            {
                shown.transform.SetParent(view, false);
            }
            if (!poses.TryGetValue(shown.itemType, out HeldPose pose))
            {
                AnimatorStateInfo state = ghostAnimator != null ? ghostAnimator.GetCurrentAnimatorStateInfo(0) : default;
                bool settled = ghostAnimator != null && state.IsName("Holding") && !ghostAnimator.IsInTransition(0) && state.normalizedTime >= 1f;
                if (!settled && Time.unscaledTime - shownAt < 2f)
                {
                    shown.transform.localScale = Vector3.zero;  // (not yet)
                    return;
                }
                Transform cam = cc.cam.transform;
                Transform hand = punch.holder;
                Vector3 p = cam.InverseTransformPoint(hand.TransformPoint(heldPosition));
                Quaternion r = Quaternion.Inverse(cam.rotation) * hand.rotation * heldRotation;
                Vector3 s = Vector3.Scale(hand.lossyScale, heldScale);
                Vector3 c = cam.lossyScale;
                pose = new HeldPose
                {
                    Position = new Vector3(-p.x, p.y, p.z),
                    Rotation = new Quaternion(r.x, -r.y, -r.z, r.w),
                    Scale = new Vector3(s.x / c.x, s.y / c.y, s.z / c.z),
                };
                poses[shown.itemType] = pose;
                Plugin.Log.LogInfo($"held skull {shown.itemType}: in front of the camera at {pose.Position}, turned {pose.Rotation.eulerAngles}, " +
                    $"size {pose.Scale}{(settled ? "" : " (the arm never settled)")}");
            }
            shown.transform.localPosition = pose.Position;
            shown.transform.localRotation = pose.Rotation;
            shown.transform.localScale = pose.Scale;
        }

        private struct HeldPose
        {
            public Vector3 Position, Scale;
            public Quaternion Rotation;
        }

        private static readonly Dictionary<ItemType, HeldPose> poses = new Dictionary<ItemType, HeldPose>();
        private static float shownAt;

        private static Vector3 heldPosition, heldScale;
        private static Quaternion heldRotation;

        private static void StartGhost(FistControl fists, Punch punch, ItemIdentifier held)
        {
            EndGhost();
            // The spawned arm that has this punch (Lockout leaves it active).
            Transform arm = punch.transform;
            foreach (GameObject spawned in spawnedArms(fists))
            {
                if (spawned != null && punch.transform.IsChildOf(spawned.transform))
                {
                    arm = spawned.transform;
                    break;
                }
            }
            GhostArm = arm.gameObject;
            foreach (Renderer r in GhostArm.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled)
                {
                    r.enabled = false;
                    hiddenRenderers.Add(r);
                }
            }
            ghostAnimator = punch.GetComponent<Animator>();
            if (ghostAnimator != null)
            {
                ghostCulling = ghostAnimator.cullingMode;
                ghostAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            GhostArm.SetActive(true);
            // Switched back on, its animator starts over: into the holding pose, as ULTRAKILL does when
            // it picks something up.
            if (ghostAnimator != null)
            {
                bool full = !held.noHoldingAnimation && fists.forceNoHold <= 0;
                ghostAnimator.SetBool("SemiHolding", !full);
                ghostAnimator.SetBool("Holding", full);
                if (full)
                {
                    ghostAnimator.Play("Holding", -1, 0f);
                }
            }
        }

        private static void EndGhost()
        {
            foreach (Renderer r in hiddenRenderers)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            hiddenRenderers.Clear();
            if (ghostAnimator != null)
            {
                ghostAnimator.cullingMode = ghostCulling;
                ghostAnimator = null;
            }
            GhostArm = null;
        }

        private static readonly AccessTools.FieldRef<FistControl, List<GameObject>> spawnedArms =
            AccessTools.FieldRefAccess<FistControl, List<GameObject>>("spawnedArms");
    }
}
