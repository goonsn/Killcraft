using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Killcraft
{
    // A replay played: its level loaded, and the replay's messages handled again at their times as
    // if the players were here (each one's id moved out of the way of real ones: Fake + theirs), so
    // their V1s, guns, shots and voices, and the level's enemies (puppets of the replay's
    // authority), are shown as multiplayer shows them. Seen through one player's eyes: this V1
    // (its controls off, unhurt) carried along where they went, looking where they looked, holding
    // their gun, sliding and dashing when they did; so their arms, gun and ULTRAKILL's HUD are seen
    // as they saw them (and the level's doors and arenas go as they went).
    // The bar along the bottom: the run's health as a pulse, its checkpoints, arenas, kills and
    // marks; play, pause, 5 seconds back and on, speed, whose view, exit. Back in time: the level
    // loaded again and the replay run through quickly to there.
    // Watched together (the party): the one who started it says where it is, twice a second; the
    // others follow (their own view each), the replay sent to those who haven't got it first.
    [HarmonyPatch]
    internal static class ReplayPlayer
    {
        public const byte ControlMessage = 40, OfferMessage = 41, ChunkMessage = 42, WantMessage = 43;
        public const int Fake = 1000000;
        private static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };

        private static Replay.Info info;
        private static Replay.Header header;
        private static List<Replay.Rec> recs;
        private static bool active, pending, allowRestart, exiting;
        private static string scene = "";
        private static float pendingAt, clock, length, seekTo = -1f, refreshAt, freshAt;
        private static int speedAt = 2, cursor;
        private static bool paused, ended;
        private static readonly Dictionary<long, Replay.Rec> last = new Dictionary<long, Replay.Rec>();

        // Each player's states through the replay (read once): the view through their eyes goes by
        // these at the replay's own clock (exact, smooth, still when paused), not by their V1 as shown.
        private struct Pose
        {
            public float T, Yaw;
            public Vector3 Feet, Vel, Look;
            public byte Flags, Kind;
            public sbyte Variant;
            public string Weapon;
        }
        private static readonly Dictionary<int, List<Pose>> tracks = new Dictionary<int, List<Pose>>();
        // The one who saved it: their fire buttons, each change (newer replays): their guns here are
        // fired with them, so they charge, overheat, spin, throw coins and saws as they did.
        private static readonly List<KeyValuePair<float, byte>> inputs = new List<KeyValuePair<float, byte>>();
        // (Which arm the recorder had out, from when: newer replays only.)
        private static readonly List<KeyValuePair<float, byte>> arms = new List<KeyValuePair<float, byte>>();
        private static int viewerArm = -1;
        private static GameObject wantedGun, fallFx;
        private static float switchedAt, slideTriedAt;
        private static bool slammingWas;
        private static readonly AccessTools.FieldRef<NewMovement, float> slideSafety = AccessTools.FieldRefAccess<NewMovement, float>("slideSafety");
        private static readonly AccessTools.FieldRef<NewMovement, GameObject> slideParticle = AccessTools.FieldRefAccess<NewMovement, GameObject>("currentSlideParticle");
        private static readonly AccessTools.FieldRef<NewMovement, GameObject> slideScrape = AccessTools.FieldRefAccess<NewMovement, GameObject>("slideScrape");
        private static readonly System.Reflection.MethodInfo DetachSlideScrapeMethod = AccessTools.Method(typeof(NewMovement), "DetachSlideScrape");
        private static GameObject keptGlow, keptScrape;
        private static readonly AccessTools.FieldRef<NewMovement, AudioSource> moveAudio = AccessTools.FieldRefAccess<NewMovement, AudioSource>("aud");
        private static int toldSlide, toldDash, toldSlam;
        // The watched player on the ground (as they said): ULTRAKILL told so too (a slide's sound, its
        // glow, go by it; carried, V1 never quite touches the floor itself). Their sandbox arm in hand:
        // its buttons not pressed here (its menu would open on this screen).
        private static bool grounded, armHeld;
        private static float slideBegan = -1f;
        private static GroundCheck[] groundChecks = new GroundCheck[0];
        private static Vector3 heading = Vector3.forward;
        private static readonly System.Reflection.MethodInfo TryDashMethod = AccessTools.Method(typeof(NewMovement), "TryDash");
        private static bool listenerPaused;
        private static float recoilAt;
        private static readonly List<int> povs = new List<int>();
        private static int pov = int.MinValue;
        private static float appliedScale = -1f, readyAt = -1f, coverUntil, savedVolume = 1f;
        private static bool slidingWas, dashingWas, covered;
        private static string weaponWas = "";
        private static readonly System.Reflection.MethodInfo StartSlideMethod = AccessTools.Method(typeof(NewMovement), "StartSlide");
        private static readonly System.Reflection.MethodInfo DodgeMethod = AccessTools.Method(typeof(NewMovement), "Dodge");

        // Together: who says where it is (int.MinValue: nobody else; this player decides).
        private static int controller = int.MinValue;
        private static bool together;
        private static float controlSentAt;

        public static bool Active => active;
        public static bool Pending => pending;
        private static bool Leading => controller == int.MinValue;
        private static float Speed => Speeds[speedAt];

        // ---- starting, stopping ----

        public static void Play(Replay.Info i, bool withParty)
        {
            if (active || pending)
            {
                Stop(false);
            }
            if (!Replay.Load(i, out Replay.Header h, out List<Replay.Rec> list))
            {
                PartyChat.System("Couldn't read that replay (the log says why).");
                return;
            }
            info = i;
            header = h;
            recs = list;
            Tracks();
            length = Mathf.Max(h.Length, list.Count > 0 ? list[list.Count - 1].T : 0f);
            scene = h.Scene;
            povs.Clear();
            foreach (Replay.PlayerMeta p in h.Players)
            {
                povs.Add(Fake + p.Id);
            }
            // (A replay saved before the players were kept with it: its players from its messages.)
            if (h.Players.Length == 0)
            {
                var ids = new List<int>();
                foreach (Replay.Rec r in list)
                {
                    if (r.From >= 0 && !ids.Contains(r.From))
                    {
                        ids.Add(r.From);
                    }
                }
                var metas = new List<Replay.PlayerMeta>();
                for (int k = 0; k < ids.Count; k++)
                {
                    metas.Add(new Replay.PlayerMeta { Id = ids[k], Name = k < i.Players.Length ? i.Players[k] : ids[k] == h.Me && i.Players.Length > 0 ? i.Players[0] : "" });
                }
                h.Players = metas.ToArray();
                povs.Clear();
                foreach (Replay.PlayerMeta p in h.Players)
                {
                    povs.Add(Fake + p.Id);
                }
            }
            pov = Fake + h.Me;
            if (!povs.Contains(pov) && povs.Count > 0)
            {
                pov = povs[0];
            }
            speedAt = 2;
            paused = false;
            controller = int.MinValue;
            together = false;
            if (withParty && OthersInParty())
            {
                together = true;
                Offer(i);
            }
            Load(0f);
            Plugin.Log.LogInfo($"replay: playing {Path.GetFileName(i.File)} ({list.Count} messages, {Replay.Clock(length)}, {h.Players.Length} players{(together ? ", with the party" : "")})");
        }

        private static void Tracks()
        {
            tracks.Clear();
            inputs.Clear();
            arms.Clear();
            foreach (Replay.Rec r in recs)
            {
                if (r.Type == Replay.InputEvent && r.Data.Length >= 1)
                {
                    inputs.Add(new KeyValuePair<float, byte>(r.T, r.Data[0]));
                    if (r.Data.Length >= 2 && r.Data[1] != 255)
                    {
                        arms.Add(new KeyValuePair<float, byte>(r.T, r.Data[1]));
                    }
                    continue;
                }
                if (r.Type != KillNet.PlayerState)
                {
                    continue;
                }
                try
                {
                    using (var br = new BinaryReader(new MemoryStream(r.Data)))
                    {
                        var p = new Pose { T = r.T };
                        p.Feet = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        p.Vel = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        p.Yaw = br.ReadSingle();
                        p.Flags = br.ReadByte();
                        p.Look = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        p.Kind = br.ReadByte();
                        br.ReadBoolean();
                        p.Weapon = br.ReadString();
                        p.Variant = br.BaseStream.Length - br.BaseStream.Position >= 1 ? br.ReadSByte() : (sbyte)-1;
                        if (p.Look.sqrMagnitude < 0.01f)
                        {
                            p.Look = Quaternion.Euler(0f, p.Yaw, 0f) * Vector3.forward;
                        }
                        if (!tracks.TryGetValue(r.From, out List<Pose> l))
                        {
                            tracks[r.From] = l = new List<Pose>();
                        }
                        l.Add(p);
                    }
                }
                catch
                {
                }
            }
        }

        // Going to `t` with the gun being loaded then (the alt fire pressed since the last shot: the
        // shotgun pumped, a charge held): from just after that shot, so the loading plays out (a gun
        // can't be put halfway through it).
        private static float LoadingStart(float t)
        {
            if (inputs.Count == 0 || pov != Fake + header.Me)
            {
                return t;
            }
            int found = -1;
            for (int k = inputs.Count - 1; k >= 0; k--)
            {
                if (inputs[k].Key <= t)
                {
                    found = k;
                    break;
                }
            }
            if (found < 0)
            {
                return t;
            }
            bool loaded = false;
            for (int k = found; k >= 0; k--)
            {
                byte now = inputs[k].Value, before = k > 0 ? inputs[k - 1].Value : (byte)0;
                if (t - inputs[k].Key > 10f)
                {
                    break;
                }
                if ((now & 1) != (before & 1))
                {
                    // (The last shot: from just after it, if something was loaded since.)
                    return loaded ? inputs[k].Key + 0.02f : HoldStart(t);
                }
                if ((now & 2) != 0 && (before & 2) == 0)
                {
                    loaded = true;
                }
            }
            return HoldStart(t);
        }

        // A fire button held at `t` (for a few seconds at most): when it was pressed (a little before).
        private static float HoldStart(float t)
        {
            if (inputs.Count == 0 || pov != Fake + header.Me)
            {
                return t;
            }
            int found = -1;
            for (int k = inputs.Count - 1; k >= 0; k--)
            {
                if (inputs[k].Key <= t)
                {
                    found = k;
                    break;
                }
            }
            if (found < 0 || inputs[found].Value == 0)
            {
                return t;
            }
            int start = found;
            while (start > 0 && inputs[start - 1].Value != 0 && t - inputs[start - 1].Key < 6f)
            {
                start--;
            }
            float pressed = inputs[start].Key;
            return t - pressed < 6f ? Mathf.Max(0f, pressed - 0.1f) : t;
        }

        // The recorder's arm out now (if this player has it too).
        private static void Arm(FistControl fc)
        {
            if (arms.Count == 0)
            {
                return;
            }
            int found = -1;
            for (int k = arms.Count - 1; k >= 0; k--)
            {
                if (arms[k].Key <= clock)
                {
                    found = k;
                    break;
                }
            }
            int want = arms[found >= 0 ? found : 0].Value;
            int now = Replay.ArmOf(fc);
            if (viewerArm < 0)
            {
                viewerArm = now;
            }
            if (want != now)
            {
                try
                {
                    fc.ForceArm(want);
                }
                catch
                {
                }
            }
        }

        private static byte InputsAt(float t)
        {
            int lo = 0, hi = inputs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (inputs[mid].Key <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found >= 0 ? inputs[found].Value : (byte)0;
        }

        // The recorded player's guns fired here by their own buttons (watching them, a newer replay).
        public static bool Injecting => active && !ended && !armHeld && inputs.Count > 0 && pov == Fake + header.Me;

        public static bool RealGuns(int from) => Injecting && from == pov;

        // (Fire1, Fire2, Punch, Hook.)
        private static readonly bool[] inNow = new bool[4];
        private static readonly int[] inPerformed = { -1, -1, -1, -1 }, inCanceled = { -1, -1, -1, -1 };
        private static readonly float[] inPerformedTime = new float[4];
        private static int inFrame = -1;

        private static void EvalInputs()
        {
            if (Time.frameCount == inFrame)
            {
                return;
            }
            inFrame = Time.frameCount;
            byte bits = InputsAt(clock);
            for (int k = 0; k < 4; k++)
            {
                bool p = (bits >> k & 1) != 0;
                if (p && !inNow[k])
                {
                    inPerformed[k] = Time.frameCount;
                    inPerformedTime[k] = Time.time;
                }
                else if (!p && inNow[k])
                {
                    inCanceled[k] = Time.frameCount;
                }
                inNow[k] = p;
            }
        }

        private static int Which(InputActionState s)
        {
            if (!Injecting || s == null)
            {
                return -1;
            }
            InputManager im = MonoSingleton<InputManager>.Instance;
            PlayerInput src = im != null ? im.InputSource : null;
            if (src == null)
            {
                return -1;
            }
            return s == src.Fire1 ? 0 : s == src.Fire2 ? 1 : s == src.Punch ? 2 : s == src.Hook ? 3 : -1;
        }

        // Paused: no shaking. (V1 held mid-slide has ULTRAKILL shake the camera again each frame, and
        // with time stopped it never dies down.)
        [HarmonyPatch(typeof(CameraController), nameof(CameraController.CameraShake)), HarmonyPrefix]
        private static bool ShakeWhilePaused() => !(active && paused);

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.IsPressed), MethodType.Getter), HarmonyPrefix]
        private static bool PressedGet(InputActionState __instance, ref bool __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inNow[k];
            return false;
        }

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.WasPerformedThisFrame), MethodType.Getter), HarmonyPrefix]
        private static bool PerformedGet(InputActionState __instance, ref bool __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inPerformed[k] == Time.frameCount;
            return false;
        }

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.WasCanceledThisFrame), MethodType.Getter), HarmonyPrefix]
        private static bool CanceledGet(InputActionState __instance, ref bool __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inCanceled[k] == Time.frameCount;
            return false;
        }

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.HoldTime), MethodType.Getter), HarmonyPrefix]
        private static bool HoldGet(InputActionState __instance, ref float __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inNow[k] || inCanceled[k] == Time.frameCount ? Time.time - inPerformedTime[k] : 0f;
            return false;
        }

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.PerformedTime), MethodType.Getter), HarmonyPrefix]
        private static bool PerformedTimeGet(InputActionState __instance, ref float __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inPerformedTime[k];
            return false;
        }

        [HarmonyPatch(typeof(InputActionState), nameof(InputActionState.PerformedFrame), MethodType.Getter), HarmonyPrefix]
        private static bool PerformedFrameGet(InputActionState __instance, ref int __result)
        {
            int k = Which(__instance);
            if (k < 0) return true;
            EvalInputs();
            __result = inPerformed[k];
            return false;
        }

        // (The Knuckleblaster's held punch - its blast - asks the punch button itself, not ULTRAKILL's
        // input state: that button answered from the recording too.)
        private static bool IsPunchAction(InputAction a)
        {
            if (!Injecting || a == null)
            {
                return false;
            }
            InputManager im = MonoSingleton<InputManager>.Instance;
            PlayerInput src = im != null ? im.InputSource : null;
            return src != null && src.Punch != null && a == src.Punch.Action;
        }

        [HarmonyPatch(typeof(InputAction), nameof(InputAction.IsPressed)), HarmonyPrefix]
        private static bool ActionPressed(InputAction __instance, ref bool __result)
        {
            if (!IsPunchAction(__instance)) return true;
            EvalInputs();
            __result = inNow[2];
            return false;
        }

        [HarmonyPatch(typeof(InputAction), nameof(InputAction.WasReleasedThisFrame)), HarmonyPrefix]
        private static bool ActionReleased(InputAction __instance, ref bool __result)
        {
            if (!IsPunchAction(__instance)) return true;
            EvalInputs();
            __result = inCanceled[2] == Time.frameCount;
            return false;
        }

        // (The watched player on the ground: so is V1, as ULTRAKILL reckons it.)
        [HarmonyPatch(typeof(NewMovement), "Update"), HarmonyPrefix]
        private static void GroundBefore(NewMovement __instance)
        {
            Ground(__instance);
            // (V1's controls off, ULTRAKILL's Update throws away a slide's glow and its scrape (the
            // sound) every frame: kept aside while it runs, and put back after.)
            keptGlow = keptScrape = null;
            if (active && !ended && __instance.sliding)
            {
                keptGlow = slideParticle(__instance);
                keptScrape = slideScrape(__instance);
                slideParticle(__instance) = null;
                slideScrape(__instance) = null;
            }
        }

        // (V1's controls off, ULTRAKILL's Update puts the arm away every frame (NoFist), and each time it
        // comes out again its animation starts over - no punch was ever seen. While the recorder's punches
        // are played, it isn't put away at all.)
        [HarmonyPatch(typeof(FistControl), nameof(FistControl.NoFist)), HarmonyPrefix]
        private static bool KeepArmOut() => !Injecting;

        // (And out, the one they had, if it was away when the replay began.)
        [HarmonyPatch(typeof(NewMovement), "Update"), HarmonyPostfix]
        private static void ArmAfter()
        {
            if (!Injecting)
            {
                return;
            }
            try
            {
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc == null)
                {
                    return;
                }
                GameObject arm = fc.currentArmObject;
                if (arm != null && !arm.activeSelf || !fc.activated)
                {
                    fc.YesFist();
                }
                Arm(fc);
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(NewMovement), "Update"), HarmonyPostfix]
        private static void SlideAfter(NewMovement __instance)
        {
            if (keptGlow == null && keptScrape == null)
            {
                return;
            }
            NewMovement nm = __instance;
            if (slideParticle(nm) == null)
            {
                slideParticle(nm) = keptGlow;
            }
            if (slideScrape(nm) == null)
            {
                slideScrape(nm) = keptScrape;
            }
            keptGlow = keptScrape = null;
            if (!nm.sliding)
            {
                // (The slide ended in there: what it had goes as ULTRAKILL lets it go.)
                if (slideParticle(nm) != null)
                {
                    UnityEngine.Object.Destroy(slideParticle(nm));
                    slideParticle(nm) = null;
                }
                if (slideScrape(nm) != null)
                {
                    try
                    {
                        DetachSlideScrapeMethod?.Invoke(nm, null);
                    }
                    catch
                    {
                        UnityEngine.Object.Destroy(slideScrape(nm));
                        slideScrape(nm) = null;
                    }
                }
                return;
            }
            GameObject scrape = slideScrape(nm);
            if (scrape != null)
            {
                if (grounded)
                {
                    scrape.transform.position = nm.transform.position + heading;
                    scrape.transform.forward = -heading;
                    nm.cc?.CameraShake(0.1f);
                }
                else
                {
                    scrape.transform.position = Vector3.one * 5000f;
                }
            }
        }

        [HarmonyPatch(typeof(GroundCheck), nameof(GroundCheck.UpdateState)), HarmonyPostfix]
        private static void GroundAfter() => Ground(MonoSingleton<NewMovement>.Instance);

        private static void Ground(NewMovement nm)
        {
            if (!active || ended || !grounded || nm == null)
            {
                return;
            }
            if (groundChecks.Length == 0 || groundChecks[0] == null)
            {
                groundChecks = nm.GetComponentsInChildren<GroundCheck>(true);
            }
            foreach (GroundCheck g in groundChecks)
            {
                if (g != null)
                {
                    g.onGround = true;
                }
            }
        }

        // (Watching a replay: this player's keys don't change the gun in hand.)
        [HarmonyPatch(typeof(GunControl), "Update"), HarmonyPrefix]
        private static bool GunKeys() => !active;

        // Where a player was at `t`: between the two states either side of it.
        private static bool Sample(int id, float t, out Pose p)
        {
            p = default;
            if (!tracks.TryGetValue(id - Fake, out List<Pose> l) || l.Count == 0)
            {
                return false;
            }
            int lo = 0, hi = l.Count - 1;
            if (t <= l[0].T)
            {
                p = l[0];
                return t > l[0].T - 2f;
            }
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (l[mid].T <= t)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            Pose a = l[lo];
            p = a;
            if (lo + 1 < l.Count)
            {
                Pose b = l[lo + 1];
                float dt = b.T - a.T;
                if (dt > 0.0001f && dt < 1f && (b.Feet - a.Feet).sqrMagnitude < 20f * 20f)
                {
                    float u = Mathf.Clamp01((t - a.T) / dt);
                    p.Feet = Vector3.Lerp(a.Feet, b.Feet, u);
                    p.Vel = Vector3.Lerp(a.Vel, b.Vel, u);
                    p.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, u);
                    p.Look = Vector3.Slerp(a.Look, b.Look, u);
                }
                return true;
            }
            return t - a.T < 2f;
        }

        // The level (again), the replay from the start, on to `to` once there.
        private static void Load(float to)
        {
            active = false;
            pending = true;
            ended = false;
            pendingAt = Time.unscaledTime;
            readyAt = -1f;
            weaponWas = "";
            wantedGun = null;
            toldSlide = toldDash = toldSlam = 0;
            groundChecks = new GroundCheck[0];
            grounded = armHeld = false;
            slidingWas = dashingWas = slammingWas = false;
            seekTo = to;
            clock = 0f;
            cursor = 0;
            last.Clear();
            Room.ReplayAuthority = int.MinValue;
            Party.LoadQuiet(scene);
        }

        public static void Stop(bool toMenu)
        {
            bool was = active || pending;
            if (was)
            {
                Release();
            }
            active = false;
            pending = false;
            Cover(false);
            ListenerPause(false);
            StyleStill(false);
            Room.ReplayAuthority = int.MinValue;
            RemotePlayers.HiddenId = int.MinValue;
            EnemySync.ReplayResets = false;
            SetTimeScale(1f);
            appliedScale = -1f;
            if (bar != null)
            {
                bar.gameObject.SetActive(false);
            }
            if (together && Leading)
            {
                SendControl(true);
            }
            together = false;
            controller = int.MinValue;
            if (was)
            {
                Plugin.Log.LogInfo("replay: stopped playing");
            }
            if (toMenu && was)
            {
                exiting = true;
                try
                {
                    MonoSingleton<OptionsManager>.Instance?.QuitMission();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"replay: couldn't go back to the menu ({e.Message})");
                }
                exiting = false;
            }
        }

        // ---- each frame ----

        public static void Frame(bool inGame, bool loading, bool gamePaused)
        {
            try
            {
                Sending();
                if (pending)
                {
                    Arriving(inGame, loading);
                }
                // (Black, quiet, while the level loads and the replay runs on to where it's going:
                // none of ULTRAKILL's start, nor everything that happened on the way at once.)
                Cover(pending || (active && Time.unscaledTime < coverUntil));
                if (!active)
                {
                    return;
                }
                NewMovement nm = inGame ? MonoSingleton<NewMovement>.Instance : null;
                if (!loading && (!inGame || nm == null || (SceneHelper.CurrentScene ?? "") != scene))
                {
                    Stop(false);
                    return;
                }
                if (loading)
                {
                    return;
                }
                Hold(nm);
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                Unselect();
                Keys(gamePaused);
                // (Not while the black's up after a jump on the timeline: it starts where it was put.)
                if (!gamePaused && !paused && !ended && !covered)
                {
                    clock = Mathf.Min(length, clock + Mathf.Min(Time.unscaledDeltaTime, 0.1f) * Speed);
                    Feed(clock, true);
                    // (The end: the level's results come up as they did; nothing's frozen.)
                    if (clock >= length && Leading)
                    {
                        ended = true;
                        // (V1 still: not walking, not moving - its gun's walking bob stops.)
                        if (nm.rb != null && !nm.rb.isKinematic)
                        {
                            nm.rb.velocity = Vector3.zero;
                        }
                    }
                }
                if (ended)
                {
                    nm.walking = false;
                }
                // (Paused: the last of each player and of the enemies said again now and then, or
                // they'd be taken for gone.)
                if ((paused || gamePaused) && Time.unscaledTime > refreshAt)
                {
                    refreshAt = Time.unscaledTime + 0.5f;
                    // (Not their shots again: the gun would kick each time, over and over.)
                    resaying = true;
                    try
                    {
                        foreach (Replay.Rec r in last.Values)
                        {
                            Dispatch(r, true);
                        }
                    }
                    finally
                    {
                        resaying = false;
                    }
                }
                if (Time.unscaledTime > freshAt)
                {
                    freshAt = Time.unscaledTime + 1f;
                    Fresh();
                }
                // (Over: V1 left as it was - not made to slide, dash or change guns again and again.)
                if (!ended)
                {
                    Mimic(nm);
                    endedAt = -1f;
                    StillGun(false);
                }
                else
                {
                    Watch(nm);
                    StillGun(true);
                }
                if (!gamePaused)
                {
                    SetTimeScale(paused ? 0f : Speed);
                }
                ListenerPause(paused && !gamePaused);
                StyleStill(paused);
                // (Paused mid-shake: still at once.)
                if (paused)
                {
                    CameraController shaker = MonoSingleton<CameraController>.Instance;
                    if (shaker != null && shaker.cameraShaking > 0f)
                    {
                        shaker.StopShake();
                    }
                }
                if (together && Leading && Time.unscaledTime - controlSentAt > 0.5f)
                {
                    SendControl(false);
                }
                Bar();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: playback went wrong ({e})");
                Stop(false);
            }
        }

        private static void Arriving(bool inGame, bool loading)
        {
            if (Time.unscaledTime - pendingAt > 90f)
            {
                Plugin.Log.LogWarning("replay: the level didn't load; not playing");
                pending = false;
                return;
            }
            NewMovement nm = inGame ? MonoSingleton<NewMovement>.Instance : null;
            if (loading || nm == null || (SceneHelper.CurrentScene ?? "") != scene)
            {
                return;
            }
            // (A moment for the level to set itself up: then V1 is taken, out of ULTRAKILL's start.)
            if (readyAt < 0f)
            {
                readyAt = Time.unscaledTime;
            }
            if (Time.unscaledTime - readyAt < 0.6f)
            {
                return;
            }
            pending = false;
            coverUntil = Time.unscaledTime + 0.3f;
            // (The same level loaded again, going back: ULTRAKILL's level is new, so is what's kept of
            // it here: the enemies killed later on aren't dead yet.)
            EnemySync.LevelChanged();
            WorldSync.LevelChanged();
            Shots.LevelChanged();
            active = true;
            Room.ReplayAuthority = Fake + header.Authority;
            EnemySync.ReplayResets = header.Players.Length <= 1;
            Fresh();
            Hold(nm);
            clock = 0f;
            cursor = 0;
            if (seekTo > 0f)
            {
                RunTo(seekTo);
            }
            seekTo = -1f;
            Plugin.Log.LogInfo($"replay: in {scene}, playing from {Replay.Clock(clock)}");
        }

        // The replay's players, as they were (their names, colours, look), seen lately.
        private static void Fresh()
        {
            foreach (Replay.PlayerMeta p in header.Players)
            {
                Profiles.Info i = Profiles.Of(Fake + p.Id);
                i.SteamName = p.Name;
                i.ColorIndex = p.ColorIndex;
                ulong.TryParse(p.SteamId, out i.SteamId);
                i.Body = Profiles.ParseColor(p.Body);
                i.Lights = Profiles.ParseColor(p.Lights);
                i.Wings = Profiles.ParseColor(p.Wings);
                i.Scene = scene;
                i.StatusAt = Time.unscaledTime;
            }
        }

        // V1 taken from the player: its controls (moving, looking, guns, fist) off, no falling, full
        // stamina (each frame: ULTRAKILL's start gives them back).
        private static void Hold(NewMovement nm)
        {
            try
            {
                nm.activated = false;
                CameraController cc = MonoSingleton<CameraController>.Instance;
                if (cc != null)
                {
                    cc.activated = false;
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    gc.activated = Injecting;
                }
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null)
                {
                    // (On while the recorder's punches are played: the arm shows them, the one they had out.)
                    fc.activated = Injecting;
                    if (Injecting)
                    {
                        Arm(fc);
                    }
                }
                if (nm.rb != null)
                {
                    // (The replay over: V1 falls on as it was, down the level's last pit.)
                    nm.rb.useGravity = ended;
                }
                nm.boostCharge = 300f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't take V1 ({e.Message})");
            }
        }

        private static void Release()
        {
            StillGun(false);
            try
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm != null && nm.rb != null)
                {
                    nm.rb.useGravity = true;
                }
                CameraController cc = MonoSingleton<CameraController>.Instance;
                if (cc != null)
                {
                    cc.activated = true;
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    gc.activated = true;
                }
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null)
                {
                    fc.activated = true;
                    // (The viewer's own arm back.)
                    if (viewerArm >= 0 && Replay.ArmOf(fc) != viewerArm)
                    {
                        fc.ForceArm(viewerArm);
                    }
                }
                viewerArm = -1;
            }
            catch
            {
            }
        }

        // What the watched player did that V1 shows from the inside: their gun out, a slide begun or
        // ended, a dash; their health (the one who saved the replay: theirs was kept).
        private static void Mimic(NewMovement nm)
        {
            if (!Sample(pov, clock, out Pose now))
            {
                return;
            }
            bool sliding = (now.Flags & 4) != 0, dashing = (now.Flags & 16) != 0;
            grounded = (now.Flags & 2) != 0 || sliding;
            try
            {
                GunControl held = MonoSingleton<GunControl>.Instance;
                armHeld = held != null && held.currentWeapon != null && held.currentWeapon.GetComponent<Sandbox.Arm.SandboxArm>() != null;
            }
            catch
            {
                armHeld = false;
            }
            string weapon = now.Weapon ?? "";
            try
            {
                string key = weapon + "|" + now.Kind + "|" + now.Variant;
                if (key != weaponWas)
                {
                    weaponWas = key;
                    Equip(weapon, now.Kind, now.Variant);
                }
                if (sliding && !nm.sliding && (!slidingWas || Time.unscaledTime - slideTriedAt > 0.5f) && StartSlideMethod != null)
                {
                    slideTriedAt = Time.unscaledTime;
                    StartSlideMethod.Invoke(nm, null);
                    if (toldSlide++ < 3)
                    {
                        Plugin.Log.LogInfo($"replay: a slide at {Replay.Clock(clock)} (sliding {nm.sliding}, its glow {(slideParticle(nm) != null ? "made" : "missing")})");
                    }
                }
                else if (!sliding && nm.sliding)
                {
                    nm.StopSlide();
                }
                if (sliding && nm.sliding)
                {
                    slideSafety(nm) = 1f;  // (ULTRAKILL stops a slide that's too slow by its own reckoning)
                }
                if (dashing && !dashingWas && !sliding)
                {
                    Dash(nm);
                }
                bool slamming = (now.Flags & 32) != 0;
                if (slamming && !slammingWas && nm.gc != null && !nm.gc.heavyFall)
                {
                    if (toldSlam++ < 3)
                    {
                        Plugin.Log.LogInfo($"replay: a slam at {Replay.Clock(clock)}");
                    }
                    nm.windState = 0.5f;
                    nm.gc.heavyFall = true;
                    nm.slamForce = 1f;
                    if (nm.fallParticle != null)
                    {
                        fallFx = UnityEngine.Object.Instantiate(nm.fallParticle, nm.transform);
                    }
                }
                if (!slamming && slammingWas)
                {
                    // (Their slam hit the ground: ULTRAKILL's impact, its dust, its thud, its debris.)
                    if (fallFx != null)
                    {
                        UnityEngine.Object.Destroy(fallFx);
                        fallFx = null;
                    }
                    if (nm.gc != null)
                    {
                        nm.gc.heavyFall = false;
                    }
                    nm.LandingImpact();
                    nm.cc?.CameraShake(0.5f);
                }
                if (sliding && nm.sliding)
                {
                    if (slideBegan < 0f)
                    {
                        slideBegan = Time.unscaledTime;
                    }
                    else if (toldSlide <= 3 && slideBegan > 1f && Time.unscaledTime - slideBegan > 0.3f)
                    {
                        toldSlide++;
                        GameObject g = slideParticle(nm);
                        CameraController cam = MonoSingleton<CameraController>.Instance;
                        Plugin.Log.LogInfo($"replay: sliding for a moment: its glow {(g == null ? "gone" : g.activeInHierarchy ? $"shown {(cam != null ? Vector3.Distance(cam.transform.position, g.transform.position) : -1f):0.0} away" : "switched off")}, on the ground {nm.gc != null && nm.gc.onGround}");
                        slideBegan = 0.0001f;
                    }
                }
                else
                {
                    slideBegan = -1f;
                }
                slammingWas = slamming;
                // (Their gun kept in hand, whatever this player presses.)
                GunControl gun = MonoSingleton<GunControl>.Instance;
                if (wantedGun != null && gun != null && gun.currentWeapon != wantedGun && Time.unscaledTime - switchedAt > 0.25f)
                {
                    SwitchTo(gun, wantedGun);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: V1 couldn't do as they did ({e.InnerException?.Message ?? e.Message})");
            }
            slidingWas = sliding;
            dashingWas = dashing;
            if (pov == Fake + header.Me && info.Hp.Length > 0)
            {
                nm.hp = Mathf.Clamp(info.Hp[Mathf.Clamp((int)(clock * 4f), 0, info.Hp.Length - 1)], 1, 200);
            }
        }

        // A dash: ULTRAKILL's (its burst ahead, its sound, the wind lines), the way they were going.
        private static void Dash(NewMovement nm)
        {
            try
            {
                Vector3 dir = heading;
                if (nm.dodgeParticle != null)
                {
                    UnityEngine.Object.Instantiate(nm.dodgeParticle, nm.transform.position + dir * 10f, Quaternion.LookRotation(-dir));
                }
                AudioSource a = moveAudio(nm);
                if (a != null && nm.dodgeSound != null)
                {
                    a.clip = nm.dodgeSound;
                    a.volume = 1f;
                    a.pitch = 1f;
                    a.Play();
                }
                nm.windState = 0.5f;
                if (toldDash++ < 3)
                {
                    Plugin.Log.LogInfo($"replay: a dash at {Replay.Clock(clock)} (its burst {(nm.dodgeParticle != null ? "made" : "missing")})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't show a dash ({e.Message})");
            }
        }

        // Their gun, if this player has it too.
        private static void Equip(string weapon, byte kind, sbyte variant)
        {
            GunControl gc = MonoSingleton<GunControl>.Instance;
            if (gc == null || string.IsNullOrEmpty(weapon))
            {
                return;
            }
            if (!EquipNamed(gc, weapon) && kind < gc.slots.Count)
            {
                // (This player's guns are set up otherwise: the same slot's gun of that colour.)
                foreach (GameObject go in gc.slots[kind])
                {
                    if (go != null && go.TryGetComponent(out WeaponIcon icon) && icon.weaponDescriptor != null && (int)icon.weaponDescriptor.variationColor == variant)
                    {
                        wantedGun = go;
                        if (gc.currentWeapon != go)
                        {
                            SwitchTo(gc, go);
                        }
                        return;
                    }
                }
                Plugin.Log.LogInfo($"replay: this player hasn't got {weapon} (slot {kind + 1}, variant {variant})");
            }
        }

        // ULTRAKILL's own switch to it (its slot, its variant), as the player's key would.
        private static void SwitchTo(GunControl gc, GameObject go)
        {
            switchedAt = Time.unscaledTime;
            for (int s = 0; s < gc.slots.Count; s++)
            {
                int v = gc.slots[s].IndexOf(go);
                if (v >= 0)
                {
                    gc.SwitchWeapon(s + 1, v);
                    return;
                }
            }
            gc.ForceWeapon(go);
        }

        private static bool EquipNamed(GunControl gc, string weapon)
        {
            foreach (List<GameObject> slot in gc.slots)
            {
                foreach (GameObject go in slot)
                {
                    if (go != null && Shots.PrefabName(go) == weapon)
                    {
                        wantedGun = go;
                        if (gc.currentWeapon != go)
                        {
                            SwitchTo(gc, go);
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        // A shot by a player (RemotePlayers: a beam, or something sent flying): the watched one's
        // kicks the gun in V1's hand.
        public static void Shot(int from)
        {
            if (!active || ended || resaying || from != pov || Injecting || Time.unscaledTime - recoilAt < 0.06f)
            {
                return;
            }
            recoilAt = Time.unscaledTime;
            Recoil();
        }

        private static bool resaying;

        // (Over: the gun in V1's hand held still - its idle animation, ULTRAKILL's own, stopped where
        // it is - until the replay's played on again or left.)
        private static readonly List<Animator> stilled = new List<Animator>();

        private static void StillGun(bool on)
        {
            try
            {
                if (!on)
                {
                    if (stilled.Count > 0)
                    {
                        foreach (Animator a in stilled)
                        {
                            if (a != null)
                            {
                                a.speed = 1f;
                            }
                        }
                        stilled.Clear();
                    }
                    return;
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                GameObject w = gc != null ? gc.currentWeapon : null;
                if (w == null)
                {
                    return;
                }
                foreach (Animator a in w.GetComponentsInChildren<Animator>())
                {
                    if (a.speed != 0f && !stilled.Contains(a))
                    {
                        a.speed = 0f;
                        stilled.Add(a);
                    }
                }
            }
            catch
            {
            }
        }

        // (For a few seconds after the end: what V1's gun and the camera are doing, in the log.)
        private static float endedAt = -1f, watchedAt;

        private static void Watch(NewMovement nm)
        {
            if (!Plugin.Diagnostics.Value)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (endedAt < 0f)
            {
                endedAt = now;
                watchedAt = 0f;
            }
            if (now - endedAt > 4f || now - watchedAt < 0.25f)
            {
                return;
            }
            watchedAt = now;
            try
            {
                GunControl gc = MonoSingleton<GunControl>.Instance;
                CameraController cc = MonoSingleton<CameraController>.Instance;
                GameObject w = gc != null ? gc.currentWeapon : null;
                var sb = new System.Text.StringBuilder();
                sb.Append($"replay end +{now - endedAt:0.00}s: gun {(w != null ? w.name : "none")}");
                if (w != null)
                {
                    sb.Append($" pos {w.transform.localPosition.ToString("F3")}");
                    Transform up = w.transform.parent;
                    if (up != null)
                    {
                        sb.Append($" holder {up.name} {up.localPosition.ToString("F3")}");
                    }
                    foreach (Animator a in w.GetComponentsInChildren<Animator>())
                    {
                        AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(0);
                        sb.Append($" anim {a.name} state {st.shortNameHash} t {st.normalizedTime:0.00} speed {a.speed:0.00}");
                        if (a.layerCount > 1)
                        {
                            sb.Append($" layer1 {a.GetLayerWeight(1):0.00}");
                        }
                        break;
                    }
                }
                if (cc != null)
                {
                    sb.Append($" cam {cc.transform.localPosition.ToString("F3")}");
                }
                sb.Append($" walking {nm.walking} sliding {nm.sliding} vel {(nm.rb != null ? nm.rb.velocity.magnitude : 0f):0.0} gcOn {gc != null && gc.activated} timescale {Time.timeScale:0.00} switched {now - switchedAt:0.0}s ago");
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"replay end: {e.Message}");
            }
        }

        // Their shot: the gun in V1's hand kicks as it did.
        private static void Recoil()
        {
            try
            {
                GunControl gc = MonoSingleton<GunControl>.Instance;
                GameObject w = gc != null ? gc.currentWeapon : null;
                if (w == null)
                {
                    return;
                }
                foreach (Animator a in w.GetComponentsInChildren<Animator>())
                {
                    foreach (AnimatorControllerParameter p in a.parameters)
                    {
                        if (p.type == AnimatorControllerParameterType.Trigger && (p.name == "Shoot" || p.name == "Fire"))
                        {
                            a.SetTrigger(p.name);
                            return;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        // The level's end reached in a replay: its results are the run's (not this playback's nothing).
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SendInfo)), HarmonyPrefix]
        private static void ResultsBefore(StatsManager __instance)
        {
            if (!active || info == null || __instance == null)
            {
                return;
            }
            try
            {
                __instance.seconds = info.Seconds;
                __instance.kills = info.Kills;
                __instance.stylePoints = Mathf.RoundToInt(info.Style);
                __instance.restarts = info.Restarts;
                __instance.tookDamage = info.TookDamage;
                __instance.majorUsed = info.MajorUsed;
                __instance.secrets = info.SecretsFound;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't show the run's results ({e.Message})");
            }
        }

        // (V1 isn't hurt while it's being carried through a replay.)
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt)), HarmonyPrefix]
        private static bool NoHurt() => !(active || pending);

        // (Paused: the music, the level's machines, everything, stop where they are.)
        private static void ListenerPause(bool on)
        {
            if (on != listenerPaused)
            {
                listenerPaused = on;
                AudioListener.pause = on;
            }
        }

        private static bool styleStill;

        // (Paused: ULTRAKILL's style meter doesn't go on shaking.)
        private static void StyleStill(bool on)
        {
            if (on == styleStill)
            {
                return;
            }
            styleStill = on;
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null)
                {
                    sh.enabled = !on;
                }
                foreach (BossHealthBar b in UnityEngine.Object.FindObjectsOfType<BossHealthBar>())
                {
                    b.enabled = !on;
                }
            }
            catch
            {
            }
        }

        private static Canvas cover;

        private static void Cover(bool on)
        {
            if (on == covered)
            {
                return;
            }
            covered = on;
            try
            {
                if (cover == null)
                {
                    cover = Ui.Canvas("Killcraft replay cover", 30000, true);
                    Ui.Picture(Ui.Fill(cover.transform, "Black"), null, Color.black).raycastTarget = false;
                    TextMeshProUGUI t = Ui.Label(Ui.Fill(cover.transform, "Text"), "LOADING REPLAY", 22f, Ui.Grey, TextAlignmentOptions.Center);
                    Ui.Refont(cover.transform);
                }
                cover.gameObject.SetActive(on);
                if (on)
                {
                    savedVolume = AudioListener.volume > 0.01f ? AudioListener.volume : savedVolume;
                    AudioListener.volume = 0f;
                }
                else
                {
                    AudioListener.volume = savedVolume;
                }
            }
            catch
            {
            }
        }

        // (Dead V1 starts again on a click: not while watching a replay.)
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.Restart)), HarmonyPrefix]
        private static bool RestartBefore() => !active || allowRestart || exiting;

        // ---- the messages ----

        private static void Feed(float to, bool live)
        {
            while (cursor < recs.Count && recs[cursor].T <= to)
            {
                Apply(recs[cursor], live);
                cursor++;
            }
        }

        private static bool Continuous(byte type) => type == KillNet.PlayerState || type == EnemySync.Snapshot;

        private static void Apply(Replay.Rec r, bool live)
        {
            switch (r.Type)
            {
                case Replay.AuthorityEvent:
                    if (r.Data.Length >= 4)
                    {
                        Room.ReplayAuthority = Fake + BitConverter.ToInt32(r.Data, 0);
                    }
                    return;
                case Replay.RestartEvent:
                    Restart();
                    return;
                case Replay.CheckpointEvent:
                    if (r.Data.Length >= 4)
                    {
                        Checkpoint(BitConverter.ToUInt32(r.Data, 0));
                    }
                    return;
                case Replay.MarkEvent:
                    return;
                case Replay.HitEvent:
                    if (live)
                    {
                        try
                        {
                            using (var br = new BinaryReader(new MemoryStream(r.Data)))
                            {
                                EnemySync.ReplayHit(br);
                            }
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"replay: a hit at {r.T:0.0} went wrong ({e.Message})");
                        }
                    }
                    return;
                case Voice.VoiceMessage:
                    if (!live || Mathf.Abs(Speed - 1f) > 0.01f)
                    {
                        return;
                    }
                    break;
                case Shots.Fired:
                case V1Body.Moves:
                    if (!live)
                    {
                        return;
                    }
                    break;
            }
            if (Continuous(r.Type))
            {
                last[(long)r.From << 8 | r.Type] = r;
                if (!live)
                {
                    return;
                }
            }
            Dispatch(r);
        }

        private static void Dispatch(Replay.Rec r, bool still = false)
        {
            byte[] data = r.Data;
            if (r.Type == KillNet.PlayerState && data.Length >= 4)
            {
                // (Said now, by their clock: their V1 shown as it comes, not held back.)
                data = (byte[])data.Clone();
                byte[] now = BitConverter.GetBytes(Time.unscaledTime);
                Buffer.BlockCopy(now, 0, data, data.Length - 4, 4);
                // (Paused: not moving, or they'd be carried on and back again by their speed.)
                if (still && data.Length >= 24)
                {
                    Array.Clear(data, 12, 12);
                }
            }
            else if (r.Type == V1Body.Moves && data.Length >= 9)
            {
                // (A move's time, by the same clock as the states' above: played at its moment of the
                // timeline, with the body where it was then.)
                data = (byte[])data.Clone();
                byte[] now = BitConverter.GetBytes(Time.unscaledTime);
                Buffer.BlockCopy(now, 0, data, data.Length - 4, 4);
            }
            else if (still && r.Type == EnemySync.Snapshot && data.Length >= 4)
            {
                // (Each enemy: its number, where, which way, how fast: how fast made nothing.)
                data = (byte[])data.Clone();
                int count = BitConverter.ToInt32(data, 0);
                for (int k = 0; k < count && 4 + k * 44 + 44 <= data.Length; k++)
                {
                    Array.Clear(data, 4 + k * 44 + 32, 12);
                }
            }

            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    Host.Dispatch(Fake + r.From, r.Type, reader);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: message {r.Type} from {r.From} at {r.T:0.0} went wrong ({e.Message})");
            }
        }

        // Quickly on to `to`: what happened, in order; where everyone was, only the last.
        private static void RunTo(float to)
        {
            Walk(clock, to);
            Feed(to, false);
            clock = to;
            foreach (Replay.Rec r in last.Values)
            {
                Dispatch(r);
            }
        }

        // The watched player's way from `from` to `to`, a step at a time: the level's triggers they went
        // through told V1 went in (and out), as if it had walked it: the rooms, doors and lights
        // the level switches on and off as one goes switch as they did (jumped over, they didn't).
        private static readonly HashSet<Collider> inside = new HashSet<Collider>();
        private static readonly Collider[] hits = new Collider[64];

        private static void Walk(float from, float to)
        {
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null || nm.playerCollider == null || to - from < 0.5f || pov == int.MinValue)
            {
                return;
            }
            Collider me = nm.playerCollider;
            int told = 0;
            inside.Clear();
            for (float t = from; t <= to + 0.001f; t += 0.15f)
            {
                if (!Sample(pov, Mathf.Min(t, to), out Pose p))
                {
                    continue;
                }
                Vector3 a = p.Feet + Vector3.up * 0.6f, b = p.Feet + Vector3.up * (Coords.FeetBelowRoot * 2f - 0.6f);
                int n = Physics.OverlapCapsuleNonAlloc(a, b, 0.5f, hits, ~0, QueryTriggerInteraction.Collide);
                var now = new HashSet<Collider>();
                for (int k = 0; k < n; k++)
                {
                    Collider c = hits[k];
                    if (c == null || !c.isTrigger || c == me || c.transform.IsChildOf(nm.transform))
                    {
                        continue;
                    }
                    now.Add(c);
                    if (!inside.Contains(c))
                    {
                        c.gameObject.SendMessage("OnTriggerEnter", me, SendMessageOptions.DontRequireReceiver);
                        told++;
                    }
                }
                foreach (Collider c in inside)
                {
                    if (c != null && !now.Contains(c))
                    {
                        c.gameObject.SendMessage("OnTriggerExit", me, SendMessageOptions.DontRequireReceiver);
                    }
                }
                inside.Clear();
                inside.UnionWith(now);
            }
            inside.Clear();
            if (told > 0)
            {
                Plugin.Log.LogInfo($"replay: went through {told} of the level's triggers on the way to {Replay.Clock(to)}");
            }
        }

        private static void Seek(float to)
        {
            to = Mathf.Clamp(to, 0f, length);
            to = LoadingStart(to);
            if (to < length - 0.05f)
            {
                ended = false;
            }
            if (to >= clock)
            {
                if (to - clock > 1f)
                {
                    coverUntil = Time.unscaledTime + 0.3f;
                    Cover(true);
                }
                RunTo(to);
                return;
            }
            Plugin.Log.LogInfo($"replay: back to {Replay.Clock(to)} (the level again)");
            Load(to);
        }

        // The recorded player started again from a checkpoint: so does the level here.
        private static void Restart()
        {
            allowRestart = true;
            try
            {
                MonoSingleton<StatsManager>.Instance?.Restart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't start the level again from its checkpoint ({e.Message})");
            }
            finally
            {
                allowRestart = false;
            }
            EnemySync.ReplayRestarted();
        }

        private static void Checkpoint(uint id)
        {
            try
            {
                foreach (CheckPoint cp in Resources.FindObjectsOfTypeAll<CheckPoint>())
                {
                    if (cp != null && cp.gameObject.scene.IsValid() && EnemySync.PathId(cp.transform) == id)
                    {
                        cp.ActivateCheckPoint();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't take the checkpoint ({e.Message})");
            }
        }

        private static void SetTimeScale(float s)
        {
            if (Mathf.Abs(s - appliedScale) < 0.001f)
            {
                return;
            }
            appliedScale = s;
            try
            {
                TimeController tc = MonoSingleton<TimeController>.Instance;
                if (tc != null)
                {
                    tc.timeScaleModifier = s;
                    tc.RestoreTime();
                }
            }
            catch
            {
            }
        }

        // ---- the camera ----

        private static void Keys(bool gamePaused)
        {
            if (gamePaused || Typing.Active)
            {
                return;
            }
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null)
            {
                if (Leading && kb.spaceKey.wasPressedThisFrame)
                {
                    TogglePause();
                }
                if (Leading && kb.leftArrowKey.wasPressedThisFrame)
                {
                    Seek(clock - 5f);
                }
                if (Leading && kb.rightArrowKey.wasPressedThisFrame)
                {
                    Seek(clock + 5f);
                }
                if (Leading && kb.upArrowKey.wasPressedThisFrame)
                {
                    speedAt = Mathf.Min(Speeds.Length - 1, speedAt + 1);
                }
                if (Leading && kb.downArrowKey.wasPressedThisFrame)
                {
                    speedAt = Mathf.Max(0, speedAt - 1);
                }
                if (kb.qKey.wasPressedThisFrame)
                {
                    NextPov(-1);
                }
                if (kb.eKey.wasPressedThisFrame)
                {
                    NextPov(1);
                }
                if (kb.hKey.wasPressedThisFrame && bar != null)
                {
                    barHidden = !barHidden;
                }
            }
        }

        // A bar button clicked stays Unity's selected one: Space presses it too ("submit"), on top of
        // Space's own pause (paused and unpaused at once). Nothing of the bar kept selected.
        private static void Unselect()
        {
            try
            {
                EventSystem es = EventSystem.current;
                GameObject sel = es != null ? es.currentSelectedGameObject : null;
                if (sel != null && bar != null && sel.transform.IsChildOf(bar.transform))
                {
                    es.SetSelectedGameObject(null);
                }
            }
            catch
            {
            }
        }

        private static void TogglePause()
        {
            if (!Leading)
            {
                return;
            }
            if (ended || (paused && clock >= length - 0.05f))
            {
                ended = false;
                paused = false;
                Seek(0f);
                return;
            }
            paused = !paused;
        }

        private static void NextPov(int step)
        {
            if (povs.Count == 0)
            {
                return;
            }
            int at = Mathf.Max(0, povs.IndexOf(pov));
            for (int n = 1; n <= povs.Count; n++)
            {
                int next = povs[((at + step * n) % povs.Count + povs.Count) % povs.Count];
                if (RemotePlayers.HeadOf(next, out _) || n == povs.Count)
                {
                    pov = next;
                    weaponWas = "";
                    slidingWas = dashingWas = false;
                    return;
                }
            }
        }

        // V1 where the watched player is (as their V1 is shown: moving smoothly), facing and looking
        // as they did, moving as fast (ULTRAKILL's speed lines). After ULTRAKILL's camera has placed
        // itself, and again late in the frame.
        public static void Place()
        {
            if (!active || pov == int.MinValue || ended)
            {
                return;
            }
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (nm == null || cc == null || nm.dead)
            {
                return;
            }
            if (!Sample(pov, clock, out Pose now))
            {
                return;
            }
            Vector3 feet = now.Feet, look = now.Look, velocity = now.Vel;
            float yaw = now.Yaw;
            RemotePlayers.HiddenId = pov;
            Vector3 root = feet + Vector3.up * Coords.FeetBelowRoot;
            nm.transform.position = root;
            if (nm.rb != null)
            {
                nm.rb.position = root;
                if (!nm.rb.isKinematic)
                {
                    nm.rb.velocity = paused ? Vector3.zero : velocity;
                }
            }
            cc.rotationY = yaw;
            cc.rotationX = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
            cc.ApplyRotations();
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            heading = flat.sqrMagnitude > 1f ? flat.normalized : new Vector3(look.x, 0f, look.z).sqrMagnitude > 0.01f ? new Vector3(look.x, 0f, look.z).normalized : heading;
            // (ULTRAKILL puts a slide's glow ahead by V1's own speed: carried here, it has none.)
            GameObject glow = nm.sliding ? slideParticle(nm) : null;
            if (glow != null)
            {
                glow.transform.position = root + heading * 10f;
                glow.transform.forward = -heading;
            }
        }

        // ---- the bar ----

        // UltraLink's: a thin strip along the bottom: play / pause, the speed, the timeline (pressed
        // or dragged: there), the time, and ▲ for the rest (5 seconds back and on, whose view, first or
        // third person, exit) in a row above it. Whose view it is: their name over it.
        private static Canvas bar;
        private static bool barHidden, more;
        private static RectTransform strip, track, fill, handle, morePanel, pauseIcon, playIcon, nameRect;
        private static UiTimeline timeline;
        private static TextMeshProUGUI timeText, povName, togetherText;
        private static UiButton playButton, speedButton, moreButton, back5, on5;
        private const float StripH = 50f, MoreH = 46f, TrackLeft = 132f, RightW = 236f, TrackH = 14f;

        private static void Bar()
        {
            if (bar == null)
            {
                BuildBar();
            }
            bar.gameObject.SetActive(!barHidden);
            if (barHidden)
            {
                return;
            }
            float f = length > 0f ? Mathf.Clamp01(clock / length) : 0f;
            if (timeline.Held >= 0f)
            {
                f = timeline.Held;
            }
            float tw = Mathf.Max(1f, track.rect.width);
            fill.sizeDelta = new Vector2(f * tw, 0f);
            handle.anchoredPosition = new Vector2(f * tw, 0f);
            timeText.text = $"{Replay.Clock(timeline.Held >= 0f ? f * length : clock)} / {Replay.Clock(length)}";
            pauseIcon.gameObject.SetActive(!paused && !ended);
            playIcon.gameObject.SetActive(paused || ended);
            speedButton.Set($"{Speed:0.##}X");
            moreButton.Set(more ? "▼" : "▲");
            morePanel.gameObject.SetActive(more);
            nameRect.anchoredPosition = new Vector2(0f, Below + StripH + (more ? MoreH + 6f : 0f) + 6f);
            povName.text = pov != int.MinValue ? Typing.Escape(Profiles.NameOf(pov)) : "";
            povName.color = pov != int.MinValue ? Profiles.Of(pov).Color : Color.white;
            foreach (UiButton b in new[] { playButton, speedButton, back5, on5 })
            {
                if (b.Interactable != Leading)
                {
                    b.Interactable = Leading;
                    b.Refresh();
                }
            }
            togetherText.text = !together ? "" : Leading ? "WATCHING TOGETHER  ·  YOU LEAD" : $"WATCHING TOGETHER  ·  {Typing.Escape(Profiles.NameOf(controller).ToUpperInvariant())} LEADS";
        }

        private const float Inset = 18f, Below = 10f;

        // A rounded black bar along the bottom, in from the edges (UltraLink's).
        private static RectTransform Bottom(Transform parent, string name, float y, float h, Color back)
        {
            RectTransform rt = Ui.Rect(parent, name, 0f, 0f, 10f, h);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(Inset, Below + y);
            rt.offsetMax = new Vector2(-Inset, Below + y + h);
            Image img = Ui.Picture(rt, Ui.RoundBox, back);
            img.raycastTarget = true;
            return rt;
        }

        private static void BuildBar()
        {
            bar = Ui.Canvas("Killcraft replay bar", 70, true);
            RectTransform root = (RectTransform)bar.transform;
            strip = Bottom(root, "Strip", 0f, StripH, new Color(0f, 0f, 0f, 0.78f));

            // Play / pause: drawn (two bars, a triangle), not typed.
            playButton = Ui.Button(strip, "", 14f, TogglePause, 16f, 9f, 34f, 34f, TextAlignmentOptions.Center, true, 2f);
            playButton.Bare = true;
            pauseIcon = Ui.Rect(playButton.transform, "Pause", 11f, 10f, 12f, 14f);
            Ui.Image(pauseIcon, null, Color.white, 0f, 0f, 4f, 14f).raycastTarget = false;
            Ui.Image(pauseIcon, null, Color.white, 8f, 0f, 4f, 14f).raycastTarget = false;
            playIcon = Ui.Rect(playButton.transform, "Play", 10f, 10f, 14f, 14f);
            RectTransform tri = Ui.Fill(playIcon, "Triangle");
            tri.pivot = new Vector2(0.5f, 0.5f);
            tri.localEulerAngles = new Vector3(0f, 0f, 90f);
            Image ti = Ui.Picture(tri, Ui.Triangle, Color.white);
            ti.raycastTarget = false;
            ti.preserveAspect = true;
            speedButton = Ui.Button(strip, "1X", 16f, () => speedAt = (speedAt + 1) % Speeds.Length, 60f, 9f, 56f, 34f, TextAlignmentOptions.Center, true, 2f);
            speedButton.Bare = true;

            // The timeline.
            track = Ui.Rect(strip, "Track", 0f, 0f, 10f, 6f);
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(0f, 0.5f);
            track.offsetMin = new Vector2(TrackLeft, -TrackH / 2f);
            track.offsetMax = new Vector2(-RightW, TrackH / 2f);
            Ui.Picture(track, null, new Color(0.2f, 0.2f, 0.2f, 0.95f));
            fill = Ui.Rect(track, "Fill", 0f, 0f, 10f, TrackH);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = Vector2.zero;
            Ui.Picture(fill, null, new Color(1f, 0.33f, 0.2f)).raycastTarget = false;
            handle = Ui.Rect(track, "Handle", 0f, 0f, 7f, 26f);
            handle.anchorMin = handle.anchorMax = new Vector2(0f, 0.5f);
            handle.pivot = new Vector2(0.5f, 0.5f);
            Ui.Picture(handle, null, Color.white).raycastTarget = false;
            RectTransform hit = Ui.Fill(track, "Seek");
            hit.offsetMin = new Vector2(0f, -14f);
            hit.offsetMax = new Vector2(0f, 14f);
            hit.SetAsFirstSibling();
            Ui.Picture(hit, null, new Color(0f, 0f, 0f, 0f)).raycastTarget = true;
            timeline = hit.gameObject.AddComponent<UiTimeline>();
            timeline.Seek = f => { if (Leading) Seek(f * length); };

            // The time, ▲.
            RectTransform right = Ui.Rect(strip, "Right", 0f, 0f, RightW, StripH);
            right.anchorMin = right.anchorMax = new Vector2(1f, 1f);
            right.pivot = new Vector2(1f, 1f);
            right.anchoredPosition = Vector2.zero;
            timeText = Ui.Text(right, "", 16f, Color.white, TextAlignmentOptions.MidlineRight, 0f, 0f, 176f, StripH);
            moreButton = Ui.Button(right, "▲", 15f, () => more = !more, 190f, 9f, 34f, 34f, TextAlignmentOptions.Center, true, 2f);
            moreButton.Bare = true;

            // Whose view, over the strip.
            nameRect = Ui.Rect(root, "Name", 0f, 0f, 600f, 24f);
            nameRect.anchorMin = nameRect.anchorMax = new Vector2(0.5f, 0f);
            nameRect.pivot = new Vector2(0.5f, 0f);
            povName = Ui.Label(nameRect, "", 15f, Color.white, TextAlignmentOptions.Center);

            // ▲: the rest.
            morePanel = Bottom(root, "More", StripH + 6f, MoreH, new Color(0f, 0f, 0f, 0.65f));
            Ui.Text(morePanel, "SPACE: PAUSE  ·  ← →: 5 S  ·  ↑ ↓: SPEED  ·  Q / E: PLAYER  ·  H: HIDE",
                11f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 18f, 0f, 640f, MoreH);
            RectTransform mid = Ui.Rect(morePanel, "Buttons", 0f, 0f, 362f, MoreH);
            mid.anchorMin = mid.anchorMax = new Vector2(0.5f, 0.5f);
            mid.pivot = new Vector2(0.5f, 0.5f);
            mid.anchoredPosition = Vector2.zero;
            float y = 8f, h = 30f;
            back5 = Ui.Button(mid, "-5 S", 14f, () => Seek(clock - 5f), 0f, y, 70f, h, TextAlignmentOptions.Center, true, 2.5f);
            on5 = Ui.Button(mid, "+5 S", 14f, () => Seek(clock + 5f), 78f, y, 70f, h, TextAlignmentOptions.Center, true, 2.5f);
            Ui.Button(mid, "<", 15f, () => NextPov(-1), 168f, y, 34f, h, TextAlignmentOptions.Center, true, 2.5f);
            Ui.Button(mid, ">", 15f, () => NextPov(1), 208f, y, 34f, h, TextAlignmentOptions.Center, true, 2.5f);
            UiButton exit = Ui.Button(mid, "EXIT", 14f, () => Stop(true), 262f, y, 100f, h, TextAlignmentOptions.Center, true, 2.5f);
            exit.Accent = KillcraftPanel.KillRed;
            exit.Refresh();
            RectTransform tr = Ui.Rect(morePanel, "Together", 0f, 0f, 420f, MoreH);
            tr.anchorMin = tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(1f, 1f);
            tr.anchoredPosition = new Vector2(-18f, 0f);
            togetherText = Ui.Label(tr, "", 12f, Ui.Grey, TextAlignmentOptions.MidlineRight);
            Ui.Refont(bar.transform);
            bar.gameObject.SetActive(false);
        }

        // The run's health through it (UltraLink's RUN PULSE): a line going straight from each reading
        // to the next (sloping, not in steps), black above it, dark red under it; checkpoints and arenas
        // as lines down it (bosses gold), kills as white diamonds along its foot; a band along the top
        // (`strip` high) for the marks' names; the corners rounded.
        public static Texture2D Pulse(Replay.Info i, int w, int h, int strip = 0)
        {
            w = Mathf.Max(8, w);
            h = Mathf.Max(8, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            int gh = Mathf.Max(4, h - strip);
            float len = Mathf.Max(1f, i.Length);
            var above = new Color32(14, 3, 2, 245);
            var under = new Color32(70, 18, 11, 245);
            var band = new Color32(98, 21, 13, 250);
            var ys = new float[w];
            int n = i.Hp.Length;
            // (A point every 12 pixels, the health over that stretch: joined by straight lines.)
            const float step = 12f;
            int count = Mathf.CeilToInt(w / step) + 2;
            var pts = new float[count];
            for (int k = 0; k < count; k++)
            {
                float a = Mathf.Max(0f, k * step - step / 2f), b = Mathf.Min(w, k * step + step / 2f), sum = 0f;
                int c = 0;
                for (float xx = a; xx < b; xx += 1f, c++)
                {
                    sum += HpAt(i, xx / w * len);
                }
                pts[k] = c > 0 ? sum / c : HpAt(i, Mathf.Min(k * step, w) / w * len);
            }
            for (int x = 0; x < w; x++)
            {
                int k = Mathf.Min((int)(x / step), count - 2);
                float hp = Mathf.Lerp(pts[k], pts[k + 1], (x + 0.5f - k * step) / step);
                ys[x] = 9f + Mathf.Clamp01(hp / 100f) * (gh - 15);
                for (int y = 0; y < h; y++)
                {
                    px[y * w + x] = y >= gh ? band : y < ys[x] ? under : above;
                }
            }
            foreach (KeyValuePair<float, string> m in Replays.Marks(i))
            {
                int x = Mathf.Clamp((int)(m.Key / len * w), 0, w - 1);
                bool boss = !m.Value.StartsWith("CP", StringComparison.Ordinal) && m.Value != "ARENA";
                for (int y = 0; y < h; y++)
                {
                    px[y * w + x] = y >= gh ? new Color32(40, 8, 5, 255) : boss ? new Color32(255, 200, 60, 255) : new Color32(120, 92, 86, 255);
                }
            }
            // The line: smooth (each pixel by how near it is to the line), 2 pixels thick.
            var lineColor = new Color32(255, 88, 58, 255);
            for (int x = 0; x < w; x++)
            {
                float y0 = x > 0 ? ys[x - 1] : ys[0], y1 = ys[x];
                int lo = Mathf.Max(0, (int)Mathf.Floor(Mathf.Min(y0, y1) - 2f)), hi = Mathf.Min(gh - 1, (int)Mathf.Ceil(Mathf.Max(y0, y1) + 2f));
                for (int y = lo; y <= hi; y++)
                {
                    float d = DistanceToSegment(new Vector2(x + 0.5f, y + 0.5f), new Vector2(x - 0.5f, y0), new Vector2(x + 0.5f, y1));
                    float a = Mathf.Clamp01(1.6f - d);
                    if (a <= 0f)
                    {
                        continue;
                    }
                    px[y * w + x] = Color32.Lerp(px[y * w + x], lineColor, a);
                }
            }
            foreach (float t in i.KillTimes)
            {
                int cx = Mathf.Clamp((int)(t / len * w), 3, w - 4);
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        if (Mathf.Abs(dx) + Mathf.Abs(dy) <= 2)
                        {
                            px[(4 + dy) * w + cx + dx] = new Color32(255, 255, 255, 255);
                        }
                    }
                }
            }
            // Rounded corners.
            const float r = 14f;
            float hx = w / 2f, hy = h / 2f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - hx) - (hx - r), qy = Mathf.Abs(y + 0.5f - hy) - (hy - r);
                    if (qx <= 0f || qy <= 0f)
                    {
                        continue;
                    }
                    float d = Mathf.Sqrt(qx * qx + qy * qy) - r;
                    Color32 c = px[y * w + x];
                    c.a = (byte)(c.a * Mathf.Clamp01(0.5f - d));
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private static float HpAt(Replay.Info i, float t)
        {
            int n = i.Hp.Length;
            if (n == 0)
            {
                return 100f;
            }
            float at = Mathf.Clamp(t * 4f, 0f, n - 1);
            int k = Mathf.Min((int)at, n - 1);
            return Mathf.Lerp(i.Hp[k], i.Hp[Mathf.Min(k + 1, n - 1)], at - k);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float u = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return (p - (a + ab * u)).magnitude;
        }

        // ---- watching together ----

        private static bool OthersInParty()
        {
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id != Room.MyId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool InParty(int id)
        {
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        // What's sent of a replay: its CASE FILE's data, the replay, its picture.
        private static byte[] offered = new byte[0];
        private static string offeredKey = "";
        private static int offeredMeta, offeredKcr, offeredPng;
        private sealed class Sending_ { public int To; public int At; }
        private static readonly List<Sending_> sends = new List<Sending_>();
        private static float sendAt;

        private static void Offer(Replay.Info i)
        {
            try
            {
                byte[] meta = File.ReadAllBytes(i.Meta);
                byte[] kcr = File.ReadAllBytes(i.File);
                byte[] png = File.Exists(i.Picture) ? File.ReadAllBytes(i.Picture) : new byte[0];
                offered = new byte[meta.Length + kcr.Length + png.Length];
                Buffer.BlockCopy(meta, 0, offered, 0, meta.Length);
                Buffer.BlockCopy(kcr, 0, offered, meta.Length, kcr.Length);
                Buffer.BlockCopy(png, 0, offered, meta.Length + kcr.Length, png.Length);
                offeredKey = i.Key;
                offeredMeta = meta.Length;
                offeredKcr = kcr.Length;
                offeredPng = png.Length;
                sends.Clear();
                KillNet.Send(OfferMessage, KillNet.ToEveryone, w =>
                {
                    w.Write(offeredKey);
                    w.Write(i.Level ?? "");
                    w.Write(offeredMeta);
                    w.Write(offeredKcr);
                    w.Write(offeredPng);
                });
                PartyChat.Event("WATCHING A REPLAY WITH THE PARTY");
                Plugin.Log.LogInfo($"replay: offered {offeredKey} to the party ({offered.Length / 1024} KB)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't offer it to the party ({e.Message})");
            }
        }

        // Those who haven't got it: sent it, a piece at a time (a dozen a second each).
        private static void Sending()
        {
            if (sends.Count == 0 || Time.unscaledTime < sendAt)
            {
                return;
            }
            sendAt = Time.unscaledTime + 1f / 12f;
            const int Piece = 24000;
            for (int k = sends.Count - 1; k >= 0; k--)
            {
                Sending_ s = sends[k];
                int n = Math.Min(Piece, offered.Length - s.At);
                if (n <= 0)
                {
                    sends.RemoveAt(k);
                    continue;
                }
                int at = s.At;
                KillNet.Send(ChunkMessage, KillNet.ToPlayer, w =>
                {
                    w.Write(offeredKey);
                    w.Write(at);
                    w.Write((ushort)n);
                    w.Write(offered, at, n);
                }, s.To);
                s.At += n;
            }
        }

        private static void SendControl(bool ended)
        {
            controlSentAt = Time.unscaledTime;
            string key = info != null ? info.Key : "";
            float c = clock;
            KillNet.Send(ControlMessage, KillNet.ToEveryone, w =>
            {
                w.Write(key);
                w.Write(c);
                w.Write((byte)speedAt);
                w.Write(paused);
                w.Write(ended);
            });
        }

        // Receiving one.
        private static byte[] incoming;
        private static string incomingKey = "", incomingLevel = "";
        private static int incomingFrom, incomingMeta, incomingKcr, incomingPng, incomingGot;

        public static void Handle(int from, byte type, BinaryReader r)
        {
            switch (type)
            {
                case OfferMessage:
                {
                    string key = r.ReadString();
                    string level = r.ReadString();
                    int meta = r.ReadInt32(), kcr = r.ReadInt32(), png = r.ReadInt32();
                    if (from == Room.MyId || !InParty(from))
                    {
                        return;
                    }
                    if (!InMenu())
                    {
                        PartyChat.System($"{Typing.Escape(Profiles.NameOf(from))} is watching a replay with the party ({Typing.Escape(level)}): back in the menu to join.");
                        return;
                    }
                    Replay.Info have = Replay.Find(key);
                    if (have != null)
                    {
                        Follow(have, from);
                        return;
                    }
                    incoming = new byte[meta + kcr + png];
                    incomingKey = key;
                    incomingLevel = level;
                    incomingFrom = from;
                    incomingMeta = meta;
                    incomingKcr = kcr;
                    incomingPng = png;
                    incomingGot = 0;
                    KillNet.Send(WantMessage, KillNet.ToPlayer, w => w.Write(key), from);
                    PartyChat.Event($"GETTING {Typing.Escape(Profiles.NameOf(from).ToUpperInvariant())}'S REPLAY...");
                    break;
                }
                case WantMessage:
                {
                    string key = r.ReadString();
                    if (key == offeredKey && offered.Length > 0 && sends.Find(s => s.To == from) == null)
                    {
                        sends.Add(new Sending_ { To = from });
                        Plugin.Log.LogInfo($"replay: sending it to {Profiles.NameOf(from)}");
                    }
                    break;
                }
                case ChunkMessage:
                {
                    string key = r.ReadString();
                    int at = r.ReadInt32();
                    int n = r.ReadUInt16();
                    byte[] data = r.ReadBytes(n);
                    if (incoming == null || key != incomingKey || from != incomingFrom || at + n > incoming.Length)
                    {
                        return;
                    }
                    Buffer.BlockCopy(data, 0, incoming, at, n);
                    incomingGot += n;
                    if (incomingGot >= incoming.Length)
                    {
                        Received();
                    }
                    break;
                }
                case ControlMessage:
                {
                    string key = r.ReadString();
                    float c = r.ReadSingle();
                    int s = r.ReadByte();
                    bool p = r.ReadBoolean();
                    bool ended = r.ReadBoolean();
                    if (from != controller || info == null || key != info.Key || !(active || pending))
                    {
                        return;
                    }
                    if (ended)
                    {
                        controller = int.MinValue;
                        together = false;
                        PartyChat.System($"{Typing.Escape(Profiles.NameOf(from))} stopped watching: the replay's yours now.");
                        return;
                    }
                    speedAt = Mathf.Clamp(s, 0, Speeds.Length - 1);
                    paused = p;
                    if (active && Mathf.Abs(c - clock) > 1f)
                    {
                        Seek(c);
                    }
                    else if (pending)
                    {
                        seekTo = c;
                    }
                    break;
                }
            }
        }

        private static bool InMenu()
        {
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            return om != null && om.mainMenu;
        }

        private static void Received()
        {
            try
            {
                Directory.CreateDirectory(Replay.Folder);
                string b = Path.Combine(Replay.Folder, incomingKey);
                File.WriteAllBytes(b + ".kcr", Slice(incoming, incomingMeta, incomingKcr));
                if (incomingPng > 0)
                {
                    File.WriteAllBytes(b + ".png", Slice(incoming, incomingMeta + incomingKcr, incomingPng));
                }
                File.WriteAllBytes(b + ".json", Slice(incoming, 0, incomingMeta));
                Plugin.Log.LogInfo($"replay: got {incomingKey} from {Profiles.NameOf(incomingFrom)}");
                Replay.Info i = Replay.Find(incomingKey);
                incoming = null;
                if (i != null && InMenu())
                {
                    Follow(i, incomingFrom);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't keep the replay sent ({e.Message})");
            }
        }

        private static byte[] Slice(byte[] a, int at, int n)
        {
            var b = new byte[n];
            Buffer.BlockCopy(a, at, b, 0, n);
            return b;
        }

        private static void Follow(Replay.Info i, int lead)
        {
            Replays.CloseQuietly();
            Play(i, false);
            if (pending)
            {
                controller = lead;
                together = true;
                PartyChat.Event($"WATCHING {Typing.Escape(Profiles.NameOf(lead).ToUpperInvariant())}'S REPLAY TOGETHER");
            }
        }
    }

    // The bar's timeline: pressed or dragged along, the replay goes there (once let go).
    internal sealed class UiTimeline : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public Action<float> Seek;
        private float at = -1f;

        // Where it's held (pressed or dragged, not let go yet), or -1.
        public float Held => at;

        private float Fraction(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out Vector2 p);
            return Mathf.Clamp01((p.x - rt.rect.xMin) / Mathf.Max(1f, rt.rect.width));
        }

        public void OnPointerDown(PointerEventData e) => at = Fraction(e);

        public void OnDrag(PointerEventData e) => at = Fraction(e);

        public void OnPointerUp(PointerEventData e)
        {
            if (at >= 0f)
            {
                Seek?.Invoke(at);
            }
            at = -1f;
        }
    }
}
