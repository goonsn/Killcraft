using System;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's half of SkyCraft's link, run every frame (after ULTRAKILL's own scripts, so its
    // camera has already turned for this frame):
    //  - tells Minecraft where V1 is, where the camera looks, and whether a level is running;
    //  - once Minecraft has the player, moves V1 and the camera to Minecraft's player every frame
    //    (Minecraft is authoritative for movement, collision and health);
    //  - streams level collision, enemies and input to Minecraft and applies its hits back.
    [DefaultExecutionOrder(30000)]
    internal sealed class Host : MonoBehaviour
    {
        private McState mc;
        private bool haveMc;
        private bool mcWasAlive;
        private uint lastMcPid;
        private int epoch;
        private uint teleportSeq = ((uint)Environment.TickCount & 0x7FFFFFF) | 1;
        private uint worldId;
        private string lastScene;
        private bool teleportPending = true;
        private bool needResync;
        private Vector3 lastSetRoot;
        private bool haveLastSet;
        private float settle;
        private long qpcFrequency;

        private bool inGame;
        private bool mcInWorld;
        private bool screenOpen;
        private bool puppet;
        private bool arriving;
        private bool ultrakillTakes;
        private float mcYaw;
        private float lastUnityYaw;
        private bool yawInitialized;
        private NewMovement controlled;
        private bool savedKinematic;
        private RigidbodyInterpolation savedInterpolation;

        private GUIStyle style;
        private static Texture2D white;

        private static T Find<T>() where T : MonoSingleton<T>
        {
            T found = MonoSingleton.GetInstance(typeof(T)) as T;
            return found != null ? found : null;
        }

        private static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "")
            {
                h = (h ^ c) * 16777619;
            }
            return h == 0 ? 1 : h;
        }

        private void Update()
        {
            if (!Link.Ready)
            {
                return;
            }
            Link.Heartbeat();

            bool alive = Link.McAlive();
            // On a torn read (Minecraft mid-write) keep last frame's state, as SkyCraft does.
            McState fresh = default;
            if (alive && Link.ReadMcState(ref fresh))
            {
                mc = fresh;
                haveMc = true;
            }
            else if (!alive)
            {
                haveMc = false;
            }
            uint pid = Link.McPid();
            bool newProcess = alive && pid != 0 && pid != lastMcPid;
            if (alive)
            {
                lastMcPid = pid;
            }
            if (alive && (!mcWasAlive || newProcess))
            {
                // Minecraft (re)connected: resend all collision from a fresh epoch.
                Plugin.Log.LogInfo($"Minecraft connected (pid {pid})");
                Launcher.State = Launcher.Status.Running;
                // Only a new Minecraft process starts its overlay rotation over; resetting ours for the
                // same process (it just stalled) puts both sides on the same slot and old frames flash.
                if (newProcess)
                {
                    Link.ResetOverlay();
                }
                epoch++;
                Collision.Reset(epoch);
                Combat.Reset();
                teleportPending = true;
                settle = 0.5f;
                if (newProcess)
                {
                    spawnPointWorld = 0;
                    McCommand.Clear();
                }
            }
            else if (!alive && mcWasAlive)
            {
                Plugin.Log.LogWarning("Minecraft stopped answering");
            }
            mcWasAlive = alive;
            bool inWorldNow = haveMc && (mc.Flags & Proto.McInWorld) != 0;
            inWorldFor = inWorldNow ? inWorldFor + Time.unscaledDeltaTime : 0f;
            // Its first frames in the world can still carry a zero position: trust it after a moment.
            mcInWorld = inWorldNow && inWorldFor > 0.5f;
            screenOpen = haveMc && (mc.Flags & Proto.McScreenOpen) != 0;

            NewMovement nm = Find<NewMovement>();
            OptionsManager options = Find<OptionsManager>();
            CameraController cc = nm != null ? nm.cc : null;
            bool mainMenu = options != null && options.mainMenu;
            bool loading = SceneHelper.PendingScene != null;
            inGame = nm != null && cc != null && !mainMenu && nm.gameObject.activeInHierarchy;
            bool paused = options != null && options.paused;

            // The toggle key: Minecraft off (plain ULTRAKILL, Minecraft's player waits where it is) or
            // back on (Minecraft's player comes to V1 and takes over again).
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && inGame && !paused && !(Patches.OwnsPlayer && Patches.McScreenOpen) && ToggleKey(keyboard))
            {
                mcDisabled = !mcDisabled;
                toggleNotice = 3f;
                Plugin.Log.LogInfo(mcDisabled ? "Minecraft turned off: ULTRAKILL has V1" : "Minecraft turned back on");
                if (mcDisabled)
                {
                    everPuppet = false;  // when it comes back, V1 lands first, as at a level start
                }
                else
                {
                    needResync = true;
                }
            }
            toggleNotice -= Time.unscaledDeltaTime;

            // A new level is a new world: Minecraft drops all collision and the player is placed again.
            string scene = SceneHelper.CurrentScene;
            if (inGame && scene != lastScene)
            {
                lastScene = scene;
                everPuppet = false;
                worldId = Fnv(scene);
                Coords.SetLevel(worldId);
                WorldRender.Reposition();
                WorldRender.LevelLoaded();
                Plugin.Log.LogInfo($"level {scene} (world {worldId:X8}, Minecraft area offset {Coords.OffsetX}, {Coords.OffsetZ})");
                epoch++;
                Collision.ForgetMeshes();
                Collision.Reset(epoch);
                Combat.Reset();
                teleportPending = true;
                haveLastSet = false;
                settle = 0.5f;
            }

            // ULTRAKILL moved V1 itself (checkpoint, respawn, level script): Minecraft follows. Those
            // are big jumps; a frame's worth of fast movement (flying) is just timing, not a move.
            if (inGame && haveLastSet && (nm.transform.position - lastSetRoot).sqrMagnitude > TeleportUnits * TeleportUnits)
            {
                Plugin.Log.LogInfo($"ULTRAKILL moved the player {(nm.transform.position - lastSetRoot).magnitude:0.0} units; resyncing Minecraft");
                teleportPending = true;
                haveLastSet = false;
            }
            if (!inGame || loading)
            {
                haveLastSet = false;
            }
            if (needResync && inGame && !loading && !nm.dead)
            {
                needResync = false;
                teleportPending = true;
            }
            if (teleportPending && inGame && !loading && (!alive || Collision.ClearConsumed))
            {
                teleportSeq++;
                teleportPending = false;
            }

            // Intros and cutscenes deactivate V1 (and wait for it to land, walk through a trigger, ...):
            // ULTRAKILL keeps the player for those, like SkyCraft hands Skyrim the player for furniture.
            // While Minecraft drives, NewMovement.Update doesn't run, so only ULTRAKILL's own scripts
            // change this flag.
            // Minecraft also only takes V1 standing on the ground: levels start with V1 dropping in from
            // high up, and Minecraft placing its player there would fall to its death.
            bool grounded = nm != null && nm.gc != null && nm.gc.onGround;
            // Until Minecraft has the player for real, a V1 that isn't standing on something is left to
            // ULTRAKILL to land (intro drops, checkpoints a bit above the floor): frozen in the air,
            // Minecraft's player would be put there and fall away from it.
            // (Only before Minecraft has had the player since the level started, V1 died or a
            // cutscene: flying or jumping with it is Minecraft's business.)
            if (nm == null || !nm.activated || nm.dead)
            {
                everPuppet = false;
            }
            ultrakillTakes = inGame && (mcDisabled || !nm.activated || (!everPuppet && !grounded));
            // Where Minecraft's player has to be (V1's feet), and whether it's still far from there.
            double tx = 0, ty = 0, tz = 0;
            if (inGame)
            {
                Coords.ToMc(nm.transform.position - Vector3.up * Coords.FeetBelowRoot, out tx, out ty, out tz);
            }
            bool away = haveMc && mcInWorld && inGame && (Math.Abs(mc.X - tx) > 8 || Math.Abs(mc.Y - ty) > 8 || Math.Abs(mc.Z - tz) > 8);
            StepTowards(away, !loading && !ultrakillTakes && !nm.dead, tx, ty, tz);

            arriving = haveMc && mcInWorld && inGame && !loading && (mc.TeleportAck != teleportSeq || away) && !ultrakillTakes;
            puppet = haveMc && mcInWorld && inGame && !loading && mc.TeleportAck == teleportSeq && !away && !nm.dead && !ultrakillTakes;
            bool owns = (puppet || arriving) && inGame && !nm.dead;
            if (owns != (controlled != null) || (owns && controlled != nm))
            {
                if (!owns && controlled != null)
                {
                    Plugin.Log.LogInfo($"handing V1 back: inGame {inGame}, loading {loading}, Minecraft {(haveMc ? "up" : "down")}, in world {mcInWorld}, " +
                        $"dead {(nm != null && nm.dead)}, ULTRAKILL cutscene {ultrakillTakes}");
                }
                SetControl(owns ? nm : null);
            }
            everPuppet |= puppet;
            // Restarting from a checkpoint in the first moments of dying can leave ULTRAKILL's death
            // sequence (red screen, slowed sound) running on a living V1: end it the way a respawn does.
            if (puppet && !nm.dead && nm.deathSequence != null && nm.deathSequence.gameObject.activeSelf)
            {
                Plugin.Log.LogInfo("ULTRAKILL's death screen was still up on a living V1; closing it");
                nm.deathSequence.gameObject.SetActive(false);
            }
            Patches.OwnsPlayer = controlled != null;
            Patches.McScreenOpen = screenOpen;

            // Respawning puts Minecraft's player at its spawn point, and SkyCraft holds the respawned
            // player where it died meanwhile: from the world spawn (tens of thousands of blocks away)
            // that is a move that freezes Minecraft's server for most of a minute. So each level
            // makes where Minecraft's player arrived its spawn point: a death is a short move.
            if (puppet && (mc.Flags & Proto.McOnGround) != 0 && spawnPointWorld != worldId)
            {
                spawnPointWorld = worldId;
                McCommand.Run($"spawnpoint @s {Math.Floor(mc.X)} {Math.Floor(mc.Y)} {Math.Floor(mc.Z)}");
            }
            McCommand.Frame(controlled != null && puppet && !paused, screenOpen);
            InputForward.Frame(controlled != null && puppet && !paused && !McCommand.Busy, screenOpen, Screen.width, Screen.height);
            HudHider.Frame(controlled != null && puppet, nm);
            Combat.Frame(alive && inGame && mcInWorld && !mcDisabled, nm);
            while (Link.PopEvent(out McEvent e))
            {
                Combat.Handle(e, nm);
            }
            WorldRender.Drain();

            var sky = new SkyState
            {
                // While ULTRAKILL has V1 (intro drop, cutscene, death) Minecraft is told the world is
                // loading: it freezes its player, and afterwards places it where V1 actually is.
                // Otherwise it would get V1's mid-air intro position and fall to its death.
                // Also until Minecraft's player is in its world and we know where: it waits where it
                // is (a saved spot, the spawn after dying), and only then is walked here (StepTowards).
                Flags = (inGame ? Proto.SkyInGame : 0) | (paused ? Proto.SkyMenuOpen : 0)
                    | (loading || ultrakillTakes || (nm != null && nm.dead) || (inGame && !(haveMc && mcInWorld)) ? Proto.SkyLoading : 0),
                WorldId = worldId,
                Epoch = (uint)epoch,
                TeleportSeq = teleportSeq,
                ViewportW = (uint)Mathf.Min(Screen.width, Proto.MaxOverlayW),
                ViewportH = (uint)Mathf.Min(Screen.height, Proto.MaxOverlayH),
                GameHour = 12f,
            };
            if (inGame)
            {
                sky.X = tx;
                sky.Y = ty;
                sky.Z = tz;
                if (haveWaypoint)
                {
                    sky.X = wpX;
                    sky.Y = wpY;
                    sky.Z = wpZ;
                }
                // Minecraft's yaw never wraps while you turn (its hand sway follows the change), so
                // send an unwrapped angle: a jump from 180 to -180 would whip the held item around.
                if (!yawInitialized)
                {
                    mcYaw = Coords.YawToMc(cc.rotationY);
                    yawInitialized = true;
                }
                else
                {
                    mcYaw += Mathf.DeltaAngle(lastUnityYaw, cc.rotationY);
                }
                lastUnityYaw = cc.rotationY;
                sky.Yaw = mcYaw;
                sky.Pitch = Coords.PitchToMc(cc.rotationX);
            }
            Link.WriteSkyState(sky);

            settle -= Time.unscaledDeltaTime;
            if (haveMc && inGame && !loading && settle <= 0f)
            {
                if (puppet)
                {
                    Collision.Update(mc.X, mc.Y, mc.Z);
                }
                else
                {
                    Collision.Update(tx, ty, tz);
                }
            }
        }

        // Moving Minecraft's player far in one go freezes Minecraft's server: its client reports the
        // jump as one move, and the server checks collision with every block in the box between the
        // two places (tens of thousands of blocks across: most of a minute). Levels are far apart in
        // Minecraft and respawning puts the player at the world spawn, so long trips are made one
        // axis at a time (a thin box, checked at once): X, then Y, then Z.

        private void NextStep(double tx, double ty, double tz, out double x, out double y, out double z)
        {
            bool dx = Math.Abs(mc.X - tx) > 0.5, dy = Math.Abs(mc.Y - ty) > 0.5, dz = Math.Abs(mc.Z - tz) > 0.5;
            int axes = (dx ? 1 : 0) + (dy ? 1 : 0) + (dz ? 1 : 0);
            x = tx;
            y = ty;
            z = tz;
            // What the server checks is the box between the two places: a small one is no trouble.
            double box = (Math.Abs(mc.X - tx) + 1) * (Math.Abs(mc.Y - ty) + 2) * (Math.Abs(mc.Z - tz) + 1);
            if (axes <= 1 || box < 200000)
            {
                return;  // the last leg (or already there)
            }
            if (dx)
            {
                y = mc.Y;  // along X first
            }
            z = mc.Z;      // then Y; Z last
        }

        // While Minecraft's player is still far from V1 it is sent from waypoint to waypoint, each
        // a moment after it got to the last (so its server has handled that move before the next).
        // Minecraft holds its player at a teleport target until it knows the collision there and
        // only then acknowledges it: the waypoints have none, so being there is what counts.
        private float stepTimer, stepLog, inWorldFor;
        private bool haveWaypoint, everPuppet;
        private const float TeleportUnits = 8f;
        private uint spawnPointWorld;
        private double wpX, wpY, wpZ, wpFromX, wpFromY, wpFromZ;

        private void StepTowards(bool away, bool canStep, double tx, double ty, double tz)
        {
            if (!away)
            {
                haveWaypoint = false;
                stepTimer = 0f;
                stepLog = 0f;
                return;
            }
            bool there = Math.Abs(mc.X - wpX) < 1 && Math.Abs(mc.Y - wpY) < 1 && Math.Abs(mc.Z - wpZ) < 1;
            bool moved = Math.Abs(mc.X - wpFromX) > 4 || Math.Abs(mc.Y - wpFromY) > 4 || Math.Abs(mc.Z - wpFromZ) > 4;
            // The waypoint is planned from where Minecraft's player is. While it can't go yet (V1 dead,
            // a load) that keeps changing (respawning puts it at the world spawn), and when it turns
            // up somewhere else than planned, the trip starts over from there.
            if (!haveWaypoint || !canStep || (moved && !there))
            {
                NextStep(tx, ty, tz, out wpX, out wpY, out wpZ);
                wpFromX = mc.X;
                wpFromY = mc.Y;
                wpFromZ = mc.Z;
                haveWaypoint = true;
                stepTimer = 0f;
                there = Math.Abs(mc.X - wpX) < 1 && Math.Abs(mc.Y - wpY) < 1 && Math.Abs(mc.Z - wpZ) < 1;
            }
            bool settled = mc.TeleportAck == teleportSeq || mc.TeleportAck == teleportSeq - 1;
            stepLog += Time.unscaledDeltaTime;
            if (stepLog > 3f)
            {
                stepLog = 0f;
                Plugin.Log.LogInfo($"Minecraft's player is still far from V1: at ({mc.X:0.0}, {mc.Y:0.0}, {mc.Z:0.0}), waypoint ({wpX:0.0}, {wpY:0.0}, {wpZ:0.0}); " +
                    $"can step {canStep}, teleport pending {teleportPending}, ack {mc.TeleportAck}/{teleportSeq}");
            }
            if (!canStep || teleportPending)
            {
                stepTimer = 0f;
                return;
            }
            stepTimer += Time.unscaledDeltaTime;
            // At the waypoint: on to the next. Somewhere else for a while (it moved, or a teleport
            // got lost): a new waypoint from where it is now.
            if ((there && settled && stepTimer >= 0.4f) || stepTimer >= 3f)
            {
                NextStep(tx, ty, tz, out wpX, out wpY, out wpZ);
                wpFromX = mc.X;
                wpFromY = mc.Y;
                wpFromZ = mc.Z;
                stepTimer = 0f;
                teleportSeq++;
                Plugin.Log.LogInfo($"Minecraft's player is far from V1 (at {mc.X:0}, {mc.Y:0}, {mc.Z:0}): next waypoint ({wpX:0}, {wpY:0}, {wpZ:0})");
            }
        }

        private void SetControl(NewMovement nm)
        {
            var guns = Find<GunControl>();
            var fists = Find<FistControl>();
            if (controlled != null)
            {
                Plugin.Log.LogInfo("ULTRAKILL has the player back");
                if (controlled.rb != null)
                {
                    controlled.rb.isKinematic = savedKinematic;
                    controlled.rb.interpolation = savedInterpolation;
                }
                try
                {
                    guns?.YesWeapon();
                    fists?.YesFist();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"restoring weapons: {e.Message}");
                }
                needResync = true;
                haveLastSet = false;
            }
            controlled = nm;
            if (nm != null)
            {
                Plugin.Log.LogInfo("Minecraft drives the player");
                nm.rb.velocity = Vector3.zero;
                savedKinematic = nm.rb.isKinematic;
                savedInterpolation = nm.rb.interpolation;
                nm.rb.interpolation = RigidbodyInterpolation.None;
                nm.rb.isKinematic = true;
                nm.sliding = false;
                try
                {
                    guns?.NoWeapon();
                    fists?.NoFist();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"hiding weapons: {e.Message}");
                }
            }
        }

        private void LateUpdate()
        {
            if (!Link.Ready)
            {
                return;
            }
            if (Plugin.Diagnostics.Value && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame)
            {
                WorldRender.DebugCycle();
            }
            Overlay.Frame(haveMc && mcInWorld && controlled != null);
            WorldRender.Frame(haveMc && mcInWorld && inGame);
            if (controlled == null || controlled.cc == null)
            {
                return;
            }
            if (puppet)
            {
                Interpolate(out double fx, out double fy, out double fz, out double eye);
                Vector3 root = Coords.ToUnity(fx, fy, fz) + Vector3.up * Coords.FeetBelowRoot;
                controlled.transform.position = root;
                controlled.rb.position = root;
                controlled.cc.transform.position = Coords.ToUnity(fx, fy + eye, fz);
                lastSetRoot = root;
            }
            else
            {
                lastSetRoot = controlled.transform.position;  // arriving: V1 waits where it is
            }
            haveLastSet = true;
        }

        // Minecraft's 20 Hz physics ticks interpolated on this frame's clock, the way Minecraft's
        // own renderer uses partial ticks (sampling its per-frame position would judder).
        private void Interpolate(out double x, out double y, out double z, out double eye)
        {
            if (mc.TickQpc != 0 && mc.TickMs > 0f)
            {
                if (qpcFrequency == 0)
                {
                    Native.QueryPerformanceFrequency(out qpcFrequency);
                }
                Native.QueryPerformanceCounter(out long now);
                double period = mc.TickMs * qpcFrequency / 1000.0;
                double t = Math.Max(0.0, Math.Min(1.0, (now - mc.TickQpc) / period));
                x = mc.PrevX + (mc.CurX - mc.PrevX) * t;
                y = mc.PrevY + (mc.CurY - mc.PrevY) * t;
                z = mc.PrevZ + (mc.CurZ - mc.PrevZ) * t;
                eye = mc.TickEyeO + (mc.TickEye - mc.TickEyeO) * t;
                return;
            }
            x = mc.X;
            y = mc.Y;
            z = mc.Z;
            eye = mc.EyeHeight > 0f ? mc.EyeHeight : 1.62f;
        }

        private bool mcDisabled;
        private float toggleNotice;
        private UnityEngine.InputSystem.Key toggleKey = UnityEngine.InputSystem.Key.None;

        private bool ToggleKey(UnityEngine.InputSystem.Keyboard keyboard)
        {
            if (toggleKey == UnityEngine.InputSystem.Key.None)
            {
                if (!Enum.TryParse(Plugin.ToggleKey.Value, true, out toggleKey) || toggleKey == UnityEngine.InputSystem.Key.None)
                {
                    toggleKey = UnityEngine.InputSystem.Key.F9;
                }
            }
            var control = keyboard[toggleKey];
            return control != null && control.wasPressedThisFrame;
        }

        private string StatusText()
        {
            if (toggleNotice > 0f)
            {
                return mcDisabled
                    ? $"Killcraft: Minecraft off, plain ULTRAKILL ({Plugin.ToggleKey.Value} turns it back on)"
                    : "Killcraft: Minecraft back on";
            }
            if (mcDisabled)
            {
                return null;
            }
            if (!Link.Ready)
            {
                return "Killcraft: couldn't create the shared memory (see BepInEx/LogOutput.log)";
            }
            if (!mcWasAlive)
            {
                switch (Launcher.State)
                {
                    case Launcher.Status.SignIn:
                        return "Killcraft: sign in to your Microsoft account in the Prism Launcher window (first time only), then Minecraft starts by itself";
                    case Launcher.Status.Starting:
                        return "Killcraft: starting Minecraft... (the first launch downloads Minecraft and Java and takes a few minutes)";
                    case Launcher.Status.NoLauncher:
                        return "Killcraft: no Minecraft to start: put SkyCraft-Minecraft.zip in BepInEx/plugins/Killcraft";
                    case Launcher.Status.Failed:
                        return "Killcraft: starting Minecraft failed (see BepInEx/LogOutput.log)";
                    default:
                        return "Killcraft: waiting for Minecraft (the SkyCraft instance in Prism Launcher)";
                }
            }
            if (!inGame)
            {
                return null;
            }
            if (!mcInWorld)
            {
                return "Killcraft: Minecraft is opening its world...";
            }
            if (arriving)
            {
                return $"Killcraft: sending the level to Minecraft ({Collision.SentRegions} regions)...";
            }
            return null;
        }

        private void OnGUI()
        {
            string text = StatusText();
            if (text != null)
            {
                style ??= new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
                var r = new Rect(12, 12, Screen.width - 24, 60);
                GUI.color = Color.black;
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
                GUI.color = Color.white;
                GUI.Label(r, text, style);
            }
            if (controlled != null && screenOpen)
            {
                // Minecraft draws no cursor of its own into the overlay.
                white ??= Texture2D.whiteTexture;
                Vector2 c = InputForward.Cursor;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(c.x - 1, c.y - 1, 10, 10), white);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(c.x, c.y, 8, 8), white);
            }
        }
    }
}
