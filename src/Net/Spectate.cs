using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Killcraft
{
    // Dying in a party: while someone else in the party is still alive in the level, V1 stays down
    // and the camera watches them (left/right click: the next one) until one of them reaches a
    // checkpoint: then back there, alive, without the level restarting for anyone. With reviving on,
    // a teammate standing by where V1 went down for a few seconds brings V1 back there. Nobody left
    // alive: ULTRAKILL's own death screen, and its checkpoint restart, as alone.
    // Watching from the menu (SPECTATE on a teammate's row of the party menu): this player loads
    // their level and stays down from the start, watching, until the one playing lets them in (J).
    [HarmonyPatch]
    internal static class Spectate
    {
        public const byte CheckpointMessage = 36, ReviveMessage = 37, LetInMessage = 38;
        private const float Orbit = 7f, ReviveRange = 5f;
        private const int CameraBlockers = (1 << 8) | (1 << 24) | (1 << 26);

        private static bool active;
        private static int target = int.MinValue;
        private static float yaw, pitch = 15f, noOneSince = -1f, startedAt;
        private static bool behind;
        private static Vector3 downAt;
        private static bool suppress;

        // Watching from the menu: who, in which level (once it's loaded), since when; the last
        // checkpoint the others reached (taken once let in).
        private static bool watching;
        private static int pendingWatch = int.MinValue;
        private static string pendingScene = "", lastScene = "";
        private static float pendingAt;
        private static bool heardCheckpoint;
        private static Vector3 lastCheckpoint;

        private static bool menuRestart;

        // Being revived: by whom, how far along, when last heard.
        private static int reviver = int.MinValue;
        private static float revived, revivedAt = -100f;

        // Reviving someone: their id, how long so far.
        private static readonly Dictionary<int, float> reviving = new Dictionary<int, float>();
        private static float reviveSentAt;
        private static int shownReviving = int.MinValue;
        private static float shownRevivingProgress;

        private static Canvas canvas;
        private static TextMeshProUGUI title, hint, center, prompt;

        public static bool Active => active;

        public static bool IsWatching => active && watching;

        public static bool Revivable => active && Plugin.Revive.Value && !MinosMode.Round;

        // A Minos Prime round: out, watching the V1s still in it.
        internal static void BeginFor(NewMovement nm)
        {
            if (!active && Alive().Count > 0)
            {
                Begin(nm);
            }
        }

        // A Minos Prime round: back in after dying, there.
        internal static void ReviveAt(Vector3 at, float facing) => Revive(at, facing, false);

        // The sandbox has no checkpoints: there R respawns, as ULTRAKILL's own would (a friend can
        // still revive).
        private static bool Sandbox => SceneHelper.CurrentScene == "uk_construct";

        private static bool RPressed()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.rKey.wasPressedThisFrame && !Typing.Active;
        }

        // ---- dying ----

        [HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.RestartCheckpoint)), HarmonyPrefix]
        private static void MenuRestartBefore() => menuRestart = true;

        [HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.RestartCheckpoint)), HarmonyFinalizer]
        private static void MenuRestartAfter() => menuRestart = false;

        // (Dead, ULTRAKILL restarts from the checkpoint on R or a left click: spectating, a left click
        // is the next player. Its pause menu's RESPAWN AT CHECKPOINT still does.)
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.Restart)), HarmonyPrefix]
        private static bool RestartBefore()
        {
            bool sandboxR = active && !menuRestart && Sandbox && !watching && RPressed();
            if (active && !menuRestart && !sandboxR)
            {
                return false;
            }
            if (active)
            {
                Plugin.Log.LogInfo(sandboxR ? "spectate: respawning (the sandbox, R)" : "spectate: respawning at the checkpoint (pause menu)");
                Stop();
            }
            return true;
        }

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt)), HarmonyPrefix]
        private static void HurtBefore(NewMovement __instance, out bool __state) => __state = __instance != null && __instance.dead;

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt)), HarmonyPostfix]
        private static void HurtAfter(NewMovement __instance, bool __state)
        {
            try
            {
                if (__state || __instance == null || !__instance.dead || ReplayPlayer.Active || ReplayPlayer.Pending)
                {
                    return;
                }
                // (A Minos Prime round: the round says whether this V1's back or out.)
                if (MinosMode.Round)
                {
                    MinosMode.LocalDied(__instance);
                    return;
                }
                if (__instance.endlessMode)
                {
                    if (CyberWaiting())
                    {
                        Begin(__instance);
                    }
                    return;
                }
                if (watching)
                {
                    Begin(__instance);
                    return;
                }
                if (!Plugin.SpectateOnDeath.Value || !Room.Shared || Alive().Count == 0)
                {
                    return;
                }
                Begin(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't start ({e.Message})");
            }
        }

        private static void Begin(NewMovement nm)
        {
            active = true;
            startedAt = Time.unscaledTime;
            downAt = nm.transform.position;
            noOneSince = -1f;
            reviver = int.MinValue;
            revived = 0f;
            // (No death screen: ULTRAKILL's red-out and its "press to restart" put away.)
            if (nm.deathSequence != null)
            {
                nm.deathSequence.gameObject.SetActive(false);
            }
            List<int> alive = Alive();
            target = alive.Count > 0 ? alive[0] : int.MinValue;
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (cc != null)
            {
                yaw = cc.transform.eulerAngles.y;
            }
            if (watching)
            {
                target = pendingWatch;
                pendingWatch = int.MinValue;
                Plugin.Log.LogInfo($"spectate: watching {Profiles.NameOf(target)} from the start (Esc, JOIN, to play)");
                PartyChat.System($"Watching {Typing.Escape(Profiles.NameOf(target))}: Esc, then JOIN, to play.");
                return;
            }
            if (Sandbox)
            {
                Plugin.Log.LogInfo($"spectate: V1 down in the sandbox, watching {Profiles.NameOf(target)} (R respawns)");
                PartyChat.System("You died: press R to respawn" + (Plugin.Revive.Value ? ", or wait for a revive." : "."));
                return;
            }
            Plugin.Log.LogInfo($"spectate: V1 down, watching {Profiles.NameOf(target)} until a checkpoint" + (Plugin.Revive.Value ? " (or a revive)" : ""));
            PartyChat.System("You died: spectating until someone reaches a checkpoint" + (Plugin.Revive.Value ? " (or revives you)." : "."));
        }

        // (Spectating: none of this V1's guns or fists on screen, nor to fire.)
        private static void NoGuns()
        {
            try
            {
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    if (gc.currentWeapon != null && gc.currentWeapon.activeSelf)
                    {
                        gc.currentWeapon.SetActive(false);
                    }
                    gc.activated = false;
                }
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null)
                {
                    if (fc.activated)
                    {
                        fc.NoFist();
                    }
                }
            }
            catch
            {
            }
        }

        private static void GunsBack()
        {
            try
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm == null || nm.dead)
                {
                    return;
                }
                GunControl gc = MonoSingleton<GunControl>.Instance;
                if (gc != null)
                {
                    gc.activated = true;
                    if (gc.currentWeapon != null && !gc.currentWeapon.activeSelf)
                    {
                        gc.currentWeapon.SetActive(true);
                    }
                }
                MonoSingleton<FistControl>.Instance?.YesFist();
            }
            catch
            {
            }
        }

        // The Cybergrind: dead with others alive there (and spectating on).
        public static bool CyberWaiting()
        {
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            return nm != null && nm.endlessMode && Plugin.SpectateOnDeath.Value && Room.Shared && Alive().Count > 0;
        }

        // A Cybergrind wave cleared: back in, by whoever's being watched.
        public static void WaveCleared()
        {
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (!active || watching || nm == null || !nm.endlessMode)
            {
                return;
            }
            Vector3 at = downAt + Vector3.up * 0.5f;
            if (target != int.MinValue && RemotePlayers.HeadOf(target, out Vector3 head))
            {
                at = head - Vector3.up * (1.6f * Coords.U) + Vector3.up * 0.5f;
            }
            Plugin.Log.LogInfo("spectate: the wave's cleared: back in");
            PartyChat.System("Wave cleared: you're back in.");
            Revive(at, yaw, false);
        }

        private static void Stop()
        {
            if (active)
            {
                GunsBack();
            }
            active = false;
            watching = false;
            RemotePlayers.HiddenId = int.MinValue;
            target = int.MinValue;
            reviver = int.MinValue;
            revived = 0f;
        }

        // The others in this level still alive (playing, not dead, not finished).
        private static List<int> Alive()
        {
            var list = new List<int>();
            foreach (int id in Room.Peers)
            {
                Profiles.Info i = Profiles.Of(id);
                if (i.Fresh && i.State == Profiles.StatePlaying && i.Scene == SceneHelper.CurrentScene && !MinosMode.IsMinos(id))
                {
                    list.Add(id);
                }
            }
            return list;
        }

        // ---- each frame ----

        public static void Frame(bool inLevel, bool paused)
        {
            NewMovement nm = inLevel ? MonoSingleton<NewMovement>.Instance : null;
            if (active && (nm == null || !nm.dead || !inLevel))
            {
                // (ULTRAKILL brought V1 back itself: a restart, the level changed.)
                Stop();
            }
            if (!inLevel || (SceneHelper.CurrentScene ?? "") != lastScene)
            {
                lastScene = inLevel ? SceneHelper.CurrentScene ?? "" : "";
                heardCheckpoint = false;
            }
            Arriving(nm, inLevel);
            Menu(inLevel);
            if (active)
            {
                Watching(nm, paused);
            }
            else if (nm != null && !nm.dead && inLevel && !paused)
            {
                Reviving(nm);
            }
            else
            {
                reviving.Clear();
            }
            Draw(inLevel, paused);
        }

        // SPECTATE in the party menu: to their level, watching.
        public static void WatchFromMenu(int id, string scene)
        {
            if (!Party.IsLevel(scene))
            {
                return;
            }
            pendingWatch = id;
            pendingScene = scene;
            pendingAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"spectate: going to {scene} to watch {Profiles.NameOf(id)}");
            Party.LoadQuiet(scene);
        }

        // There: V1 down from the start (once the level has handed it over, or after a while).
        private static void Arriving(NewMovement nm, bool inLevel)
        {
            if (pendingWatch == int.MinValue)
            {
                return;
            }
            if (Time.unscaledTime - pendingAt > 90f)
            {
                pendingWatch = int.MinValue;
                return;
            }
            if (!inLevel || nm == null || SceneHelper.CurrentScene != pendingScene || active)
            {
                return;
            }
            watching = true;
            if (nm.dead)
            {
                Begin(nm);
                return;
            }
            if (!nm.activated && Time.unscaledTime - pendingAt < 25f)
            {
                watching = false;
                return;
            }
            try
            {
                nm.GetHurt(100000, false, 0f, false, false, 0.35f, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't put V1 down to watch ({e.Message})");
            }
            if (!active)
            {
                watching = false;
                pendingWatch = int.MinValue;
            }
        }

        // ESC while spectating: ULTRAKILL's pause menu with JOIN (watching from the menu: in, where
        // they are, as UltraLink's spectators join) and STOP SPECTATING (watching: back to the menu;
        // dead: back at the checkpoint).
        private static void Menu(bool inLevel)
        {
            PauseButtons.Spectating(active && watching && inLevel, Join, StopSpectating);
        }

        private static void Join()
        {
            if (!active || !watching)
            {
                return;
            }
            if (target == int.MinValue || !RemotePlayers.HeadOf(target, out Vector3 head))
            {
                PartyChat.System("Nobody to join by right now.");
                return;
            }
            Vector3 feet = head - Vector3.up * (1.6f * Coords.U);
            float facing = 0f;
            if (RemotePlayers.LookOf(target, out Vector3 look))
            {
                var flat = new Vector3(look.x, 0f, look.z);
                facing = Quaternion.LookRotation(flat.sqrMagnitude > 0.001f ? flat : Vector3.forward, Vector3.up).eulerAngles.y;
            }
            Plugin.Log.LogInfo($"spectate: joining, by {Profiles.NameOf(target)}");
            PartyChat.System($"You joined, by {Typing.Escape(Profiles.NameOf(target))}.");
            bool cp = heardCheckpoint;
            Vector3 cpAt = lastCheckpoint;
            Revive(feet + Vector3.up * 0.5f, facing, false);
            if (cp)
            {
                TakeCheckpoint(cpAt);
            }
            try
            {
                MonoSingleton<OptionsManager>.Instance?.UnPause();
            }
            catch
            {
            }
        }

        private static void StopSpectating()
        {
            if (!active)
            {
                return;
            }
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            try
            {
                if (watching)
                {
                    Plugin.Log.LogInfo("spectate: stopped watching (back to the menu)");
                    Stop();
                    om?.QuitMission();
                    return;
                }
                Plugin.Log.LogInfo("spectate: stopped (back at the checkpoint)");
                om?.RestartCheckpoint();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't stop ({e.Message})");
            }
        }

        private static void Watching(NewMovement nm, bool paused)
        {
            List<int> alive = Alive();
            if (alive.Count == 0)
            {
                if (noOneSince < 0f)
                {
                    noOneSince = Time.unscaledTime;
                }
                // (Watching: the others' presence takes a moment to arrive.)
                if (watching && Time.unscaledTime - startedAt < 8f)
                {
                    return;
                }
                // Nobody left: ULTRAKILL's death screen, its restart from the checkpoint.
                if (Time.unscaledTime - noOneSince > 1.5f)
                {
                    Plugin.Log.LogInfo("spectate: nobody left alive here: ULTRAKILL's death screen");
                    if (nm.endlessMode)
                    {
                        Stop();
                        PartyChat.System("Nobody's left: the run's over.");
                        Cybergrind.GameOver();
                        return;
                    }
                    PartyChat.System(watching ? "Nobody's playing here any more: the level's yours." : "Nobody's left alive: back to your checkpoint.");
                    Stop();
                    if (nm.deathSequence != null)
                    {
                        nm.deathSequence.gameObject.SetActive(true);
                        nm.deathSequence.EndSequence();
                    }
                }
                return;
            }
            noOneSince = -1f;
            if (!alive.Contains(target))
            {
                target = alive[0];
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && !paused && !Typing.Active)
            {
                int step = mouse.leftButton.wasPressedThisFrame ? 1 : mouse.rightButton.wasPressedThisFrame ? -1 : 0;
                if (step != 0 && alive.Count > 1)
                {
                    int at = alive.IndexOf(target);
                    target = alive[((at + step) % alive.Count + alive.Count) % alive.Count];
                }
                Vector2 d = mouse.delta.ReadValue();
                yaw += d.x * 0.12f;
                pitch = Mathf.Clamp(pitch - d.y * 0.12f, -60f, 80f);
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && !paused && !Typing.Active && kb.fKey.wasPressedThisFrame && Plugin.SpectateThirdPerson.Value)
            {
                behind = !behind;
            }
            if (!Plugin.SpectateThirdPerson.Value)
            {
                behind = false;
            }
            NoGuns();
            if (reviver != int.MinValue && Time.unscaledTime - revivedAt > 0.8f)
            {
                reviver = int.MinValue;
                revived = 0f;
            }
        }

        // After ULTRAKILL's camera has placed itself: round the player being watched.
        public static void Place()
        {
            if (!active || target == int.MinValue)
            {
                return;
            }
            CameraController cc = MonoSingleton<CameraController>.Instance;
            if (cc == null || !RemotePlayers.HeadOf(target, out Vector3 head))
            {
                RemotePlayers.HiddenId = int.MinValue;
                return;
            }
            // Through their eyes (their V1 not drawn: it'd be in the way).
            if (!behind && RemotePlayers.StateOf(target, out _, out _, out Vector3 eyes, out _, out _, out _, out _))
            {
                RemotePlayers.HiddenId = target;
                cc.transform.position = head;
                cc.transform.rotation = Quaternion.LookRotation(eyes, Vector3.up);
                return;
            }
            RemotePlayers.HiddenId = int.MinValue;
            Quaternion look = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = look * Vector3.back;
            Vector3 at = head + back * Orbit;
            if (Physics.SphereCast(head, 0.3f, back, out RaycastHit hit, Orbit, CameraBlockers, QueryTriggerInteraction.Ignore))
            {
                at = head + back * Mathf.Max(0.5f, hit.distance - 0.2f);
            }
            cc.transform.position = at;
            cc.transform.rotation = look;
        }

        // ---- reviving a teammate ----

        private static void Reviving(NewMovement nm)
        {
            if (MinosMode.Round)
            {
                return;
            }
            float need = Mathf.Clamp(Plugin.ReviveSeconds.Value, 1f, 10f);
            shownReviving = int.MinValue;
            bool send = Time.unscaledTime - reviveSentAt > 0.2f;
            foreach (int id in Room.Peers)
            {
                Profiles.Info i = Profiles.Of(id);
                bool down = i.Fresh && i.State == Profiles.StateDead && (i.Flags & Profiles.FlagRevivable) != 0 && i.Scene == SceneHelper.CurrentScene;
                bool near = down && RemotePlayers.HeadOf(id, out Vector3 head) && (head - nm.transform.position).magnitude < ReviveRange + 1.6f * Coords.U;
                if (!near)
                {
                    reviving.Remove(id);
                    continue;
                }
                reviving.TryGetValue(id, out float t);
                t += Time.unscaledDeltaTime;
                reviving[id] = t;
                float progress = Mathf.Clamp01(t / need);
                shownReviving = id;
                shownRevivingProgress = progress;
                if (send || progress >= 1f)
                {
                    KillNet.Send(ReviveMessage, KillNet.ToPlayer, w => w.Write(progress), id);
                }
                if (progress >= 1f)
                {
                    reviving.Remove(id);
                    Plugin.Log.LogInfo($"spectate: revived {Profiles.NameOf(id)}");
                    PartyChat.System($"You revived {Typing.Escape(Profiles.NameOf(id))}.");
                }
            }
            if (send)
            {
                reviveSentAt = Time.unscaledTime;
            }
        }

        // ---- a checkpoint reached ----

        [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.ActivateCheckPoint)), HarmonyPostfix]
        private static void Reached(CheckPoint __instance)
        {
            try
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (suppress || __instance == null || !Room.Shared || nm == null || nm.dead)
                {
                    return;
                }
                Transform t = __instance.transform;
                Vector3 at = t.position + t.up * 1.25f;
                float facing = t.rotation.eulerAngles.y + 0.01f;
                string scene = SceneHelper.CurrentScene ?? "";
                KillNet.Send(CheckpointMessage, KillNet.ToEveryone, w =>
                {
                    w.Write(scene);
                    w.Write(at.x);
                    w.Write(at.y);
                    w.Write(at.z);
                    w.Write(facing);
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't tell the party about a checkpoint ({e.Message})");
            }
        }

        public static void Handle(int from, byte type, BinaryReader r)
        {
            if (type == CheckpointMessage)
            {
                string scene = r.ReadString();
                var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float facing = r.ReadSingle();
                if (scene == SceneHelper.CurrentScene)
                {
                    heardCheckpoint = true;
                    lastCheckpoint = at;
                }
                if (active && !watching && scene == SceneHelper.CurrentScene)
                {
                    Plugin.Log.LogInfo($"spectate: {Profiles.NameOf(from)} reached a checkpoint: back there");
                    PartyChat.System($"{Typing.Escape(Profiles.NameOf(from))} reached a checkpoint: you're back.");
                    Revive(at, facing, true);
                }
                return;
            }
            if (type == LetInMessage)
            {
                string scene = r.ReadString();
                var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float facing = r.ReadSingle();
                if (active && watching && scene == SceneHelper.CurrentScene)
                {
                    Plugin.Log.LogInfo($"spectate: {Profiles.NameOf(from)} let this player in");
                    PartyChat.System($"{Typing.Escape(Profiles.NameOf(from))} let you join.");
                    bool cp = heardCheckpoint;
                    Vector3 cpAt = lastCheckpoint;
                    Revive(at + Vector3.up * 0.5f, facing, false);
                    if (cp)
                    {
                        TakeCheckpoint(cpAt);
                    }
                }
                return;
            }
            if (type == ReviveMessage)
            {
                float progress = r.ReadSingle();
                if (!active || watching || !Plugin.Revive.Value || MinosMode.Round)
                {
                    return;
                }
                reviver = from;
                revived = progress;
                revivedAt = Time.unscaledTime;
                if (progress >= 1f)
                {
                    Plugin.Log.LogInfo($"spectate: revived by {Profiles.NameOf(from)}");
                    PartyChat.System($"{Typing.Escape(Profiles.NameOf(from))} revived you.");
                    CameraController cc = MonoSingleton<CameraController>.Instance;
                    Revive(downAt + Vector3.up * 0.5f, cc != null ? yaw : 0f, false);
                }
            }
        }

        // V1 up again at a place, facing a way (and that checkpoint made this player's own too).
        private static void Revive(Vector3 at, float facing, bool checkpoint)
        {
            Stop();
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null)
            {
                return;
            }
            try
            {
                CameraController cc = nm.cc != null ? nm.cc : MonoSingleton<CameraController>.Instance;
                nm.transform.position = at;
                if (nm.rb != null)
                {
                    nm.rb.velocity = Vector3.zero;
                }
                if (cc != null)
                {
                    cc.ResetCamera(facing);
                    cc.ApplyRotations();
                    cc.activated = true;
                }
                if (!nm.enabled)
                {
                    nm.enabled = true;
                }
                nm.Respawn();
                nm.GetHealth(0, silent: true);
                if (cc != null)
                {
                    cc.StopShake();
                }
                nm.ActivatePlayer();
                if (checkpoint)
                {
                    TakeCheckpoint(at);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't bring V1 back ({e.Message}); ULTRAKILL's restart instead");
                try
                {
                    MonoSingleton<StatsManager>.Instance.Restart();
                }
                catch
                {
                }
            }
        }

        // The checkpoint there (this level's own copy of it) taken, so a later death comes back to it.
        private static void TakeCheckpoint(Vector3 at)
        {
            CheckPoint best = null;
            float bestD = 6f;
            foreach (CheckPoint cp in UnityEngine.Object.FindObjectsOfType<CheckPoint>())
            {
                float d = (cp.transform.position + cp.transform.up * 1.25f - at).magnitude;
                if (d < bestD)
                {
                    best = cp;
                    bestD = d;
                }
            }
            if (best == null)
            {
                return;
            }
            StatsManager sm = MonoSingleton<StatsManager>.Instance;
            if (sm != null && sm.currentCheckPoint == best)
            {
                return;
            }
            suppress = true;
            try
            {
                best.ActivateCheckPoint();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spectate: couldn't take the checkpoint ({e.Message})");
            }
            finally
            {
                suppress = false;
            }
        }

        // ---- shown ----

        private static void Draw(bool inLevel, bool paused)
        {
            bool show = inLevel && !paused && (active || shownReviving != int.MinValue);
            if (!show)
            {
                if (canvas != null && canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(false);
                }
                return;
            }
            if (canvas == null)
            {
                canvas = Ui.Canvas("Killcraft spectate", 15, true);
                canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>().enabled = false;
                title = Ui.Text(canvas.transform, "", 34f, Color.white, TextAlignmentOptions.Center, 0f, 860f, 1920f, 50f);
                hint = Ui.Text(canvas.transform, "", 18f, new Color(0.8f, 0.8f, 0.8f), TextAlignmentOptions.Center, 0f, 912f, 1920f, 30f);
                center = Ui.Text(canvas.transform, "", 26f, Color.white, TextAlignmentOptions.Center, 0f, 640f, 1920f, 40f);
                prompt = Ui.Text(canvas.transform, "", 24f, Color.white, TextAlignmentOptions.Center, 0f, 150f, 1920f, 36f);
            }
            prompt.text = "";
            canvas.gameObject.SetActive(true);
            if (active)
            {
                string who = target != int.MinValue ? Typing.Escape(Profiles.NameOf(target).ToUpperInvariant()) : "...";
                title.text = $"<color=#FFA030>SPECTATING</color>  <color=#{Ui.Hex(Profiles.Of(target).Color)}>{who}</color>";
                string view = Plugin.SpectateThirdPerson.Value ? (behind ? "  ·  F: THEIR EYES" : "  ·  F: BEHIND THEM") : "";
                bool cyber = MonoSingleton<NewMovement>.Instance != null && MonoSingleton<NewMovement>.Instance.endlessMode;
                hint.text = MinosMode.Round
                    ? "LEFT / RIGHT CLICK: SWITCH  ·  OUT UNTIL THE ROUND'S OVER" + view
                    : cyber
                    ? "LEFT / RIGHT CLICK: SWITCH  ·  BACK WHEN THE WAVE'S CLEARED" + view
                    : watching
                    ? "LEFT / RIGHT CLICK: SWITCH  ·  ESC: JOIN" + view
                    : Sandbox
                    ? "LEFT / RIGHT CLICK: SWITCH  ·  R: RESPAWN" + (Plugin.Revive.Value ? " (OR WAIT FOR A REVIVE)" : "")
                    : "LEFT / RIGHT CLICK: SWITCH  ·  BACK AT THE NEXT CHECKPOINT" + (Plugin.Revive.Value ? " OR WHEN REVIVED" : "") + "  ·  ESC > CHECKPOINT: RESPAWN";
                center.text = watching
                    ? (Time.unscaledTime - startedAt < 6f ? "ESC, THEN JOIN, TO PLAY" : "")
                    : reviver != int.MinValue
                    ? $"BEING REVIVED BY {Typing.Escape(Profiles.NameOf(reviver).ToUpperInvariant())}  {Bar(revived)}"
                    : Time.unscaledTime - startedAt < 2.5f ? "<color=#E05040>YOU DIED</color>" : "";
            }
            else if (shownReviving == int.MinValue)
            {
                title.text = "";
                hint.text = "";
                center.text = "";
            }
            else
            {
                title.text = "";
                hint.text = "";
                center.text = $"REVIVING {Typing.Escape(Profiles.NameOf(shownReviving).ToUpperInvariant())}  {Bar(shownRevivingProgress)}";
            }
        }

        private static string Bar(float p)
        {
            int n = Mathf.RoundToInt(Mathf.Clamp01(p) * 10f);
            return $"[{new string('|', n)}<color=#444444>{new string('|', 10 - n)}</color>] {Mathf.RoundToInt(p * 100f)}%";
        }
    }
}
