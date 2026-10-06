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

        internal static Host Current;

        private void Update()
        {
            Current = this;
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
                    mcFrozen = false;
                    mcMuted = false;
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
            pausedNow = paused;

            // The toggle key: Minecraft off (plain ULTRAKILL, Minecraft's player waits where it is) or
            // back on (Minecraft's player comes to V1 and takes over again).
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && inGame && !paused && !(Patches.OwnsPlayer && Patches.McScreenOpen) && ToggleKey(keyboard))
            {
                toggleNotice = 3f;
                // (In the Nether only over ground: there it's Killcraft's colliders from Minecraft's blocks
                // around V1, kept loaded by Minecraft's player following V1 about.)
                toggleRefused = Coords.InNether && !mcDisabled
                    && !Physics.Raycast(nm.transform.position, Vector3.down, 24f * Coords.U, 1 << 8, QueryTriggerInteraction.Ignore);
                if (!toggleRefused)
                {
                    mcDisabled = !mcDisabled;
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
                Nether.LevelChanged();
                SmileOs.LevelChanged();
                HeldItems.LevelChanged();
                Plugin.Log.LogInfo($"level {scene} (world {worldId:X8}, Minecraft area offset {Coords.OffsetX}, {Coords.OffsetZ})");
                // A new level with Minecraft's player still in the Nether: it comes out (and then to V1).
                if (haveMc && mcInWorld && (Coords.InNether || (Time.unscaledTime - Combat.DimensionAt < 2f && Combat.DimensionNether)))
                {
                    Plugin.Log.LogInfo("a new level with Minecraft's player in the Nether: sending it back");
                    Destruction.LeaveNether();
                    leaveNetherTimer = 5f;
                }
                epoch++;
                Collision.ForgetMeshes();
                Collision.Reset(epoch);
                Combat.Reset();
                teleportPending = true;
                haveLastSet = false;
                settle = 0.5f;
            }

            // Minecraft's player went through a Nether portal, or came back: ULTRAKILL shows that world.
            // (V1 goes along below, as for an ender pearl.)
            bool crossed = false;
            bool mcDead = haveMc && (mc.Flags & Proto.McDead) != 0;
            // Dying in the Nether respawns Minecraft's player in the level (its spawn point is kept
            // there), so that's where it is from now. Until the respawned player shows up, its reported
            // position is still the dead one's in the Nether: no switching back on that, and SkyCraft
            // isn't asked to hold the player (it would pin the respawned one to the Nether's
            // coordinates, out in the level's void).
            if (Coords.InNether && mcDead)
            {
                diedInNether = true;
                respawnedFor = 0f;
                Coords.SetNether(false);
                WorldRender.Reposition();
                Plugin.Log.LogInfo("Minecraft's player died in the Nether: it respawns in the level");
            }
            if (diedInNether)
            {
                bool back = haveMc && mcInWorld && !mcDead && !Coords.IsNetherX(mc.X);
                respawnedFor = back ? respawnedFor + Time.unscaledDeltaTime : 0f;
                if (respawnedFor > 0.5f || !inGame)
                {
                    diedInNether = false;
                }
            }
            // (Only when Minecraft's player really is in that world, Killcraft's mod says: put in the
            // Nether at the level's coordinates, it's sent back to its landing, not a trip.)
            bool dimensionKnown = Time.unscaledTime - Combat.DimensionAt < 2f;
            bool atNetherX = haveMc && Coords.IsNetherX(mc.X);
            if (haveMc && mcInWorld && !diedInNether && atNetherX != Coords.InNether && (!dimensionKnown || Combat.DimensionNether == atNetherX))
            {
                Coords.SetNether(!Coords.InNether);
                WorldRender.Reposition();
                crossed = true;
                if (Coords.InNether)
                {
                    Nether.Landed(Coords.ToUnity(mc.X, mc.Y, mc.Z));
                }
                Plugin.Log.LogInfo(Coords.InNether ? $"Minecraft's player went to the Nether ({mc.X:0}, {mc.Y:0}, {mc.Z:0})"
                    : $"Minecraft's player is back from the Nether ({mc.X:0}, {mc.Y:0}, {mc.Z:0})");
                // ULTRAKILL has V1 (F9): it goes where Minecraft's player came out. (Only if V1 was in the
                // world Minecraft's player left: sent out of the Nether for a new level, it's V1 that
                // Minecraft's player comes to, not the other way round.)
                if (mcDisabled && inGame && !nm.dead && Coords.IsNetherUnity(nm.transform.position) != Coords.InNether)
                {
                    Vector3 root = Coords.ToUnity(mc.X, mc.Y, mc.Z) + Vector3.up * Coords.FeetBelowRoot;
                    nm.transform.position = root;
                    nm.rb.position = root;
                    nm.rb.velocity = Vector3.zero;
                    lastSetRoot = root;
                }
            }
            // Where Minecraft's player last was in the level (alive): where it comes back to.
            if (haveMc && mcInWorld && !mcDead && !Coords.InNether && !diedInNether && !Coords.IsNetherX(mc.X)
                && !(dimensionKnown && Combat.DimensionNether))
            {
                levelX = mc.X;
                levelY = mc.Y;
                levelZ = mc.Z;
                haveLevelPos = true;
            }
            // V1 and Minecraft's player in different worlds without Minecraft having V1: V1 back in the
            // level (restarted from a checkpoint, a new level) leaves Minecraft's player in the Nether,
            // which is sent back through its portal (walking it to V1 inside the Nether would drop it
            // out of the world); V1 still up in the Nether goes to Minecraft's player in the level.
            bool v1InNether = inGame && Coords.IsNetherUnity(nm.transform.position);
            bool mcLeftInNether = inGame && haveMc && mcInWorld && Coords.InNether && !v1InNether && !puppet && !crossed;
            if (inGame && haveMc && mcInWorld && !Coords.InNether && v1InNether && !puppet && !crossed && !nm.dead)
            {
                Vector3 root = Coords.ToUnity(mc.X, mc.Y, mc.Z) + Vector3.up * Coords.FeetBelowRoot;
                Plugin.Log.LogInfo("V1 was still in the Nether: it goes to Minecraft's player");
                nm.transform.position = root;
                nm.rb.position = root;
                lastSetRoot = root;
                haveLastSet = true;
            }
            leaveNetherTimer -= Time.unscaledDeltaTime;
            leftInNetherFor = mcLeftInNether ? leftInNetherFor + Time.unscaledDeltaTime : 0f;
            if (leftInNetherFor > 1.5f && !loading && nm.activated && !nm.dead && leaveNetherTimer <= 0f)
            {
                leaveNetherTimer = 5f;
                Plugin.Log.LogInfo("V1 is in the level but Minecraft's player is in the Nether: sending it back through its portal");
                Destruction.LeaveNether();
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
            // (Never in the Nether: there V1 only ever follows Minecraft's player. A teleport left pending
            // goes when it's back in the level.)
            if (teleportPending && inGame && !loading && (!alive || Collision.ClearConsumed) && !Coords.InNether)
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
            // Minecraft's player jumped far by itself while it had V1 (an ender pearl, chorus fruit,
            // /tp): V1 goes there, instead of Minecraft's player being walked back. (ULTRAKILL moving
            // V1 is caught above and leaves a teleport pending.)
            // (Through a Nether portal too: Minecraft may have let go of V1 for a moment on the way.)
            bool follow = Coords.InNether
                ? !mcLeftInNether && !ultrakillTakes
                : (puppet || (crossed && !ultrakillTakes)) && !teleportPending && mc.TeleportAck == teleportSeq;
            if (away && follow && !nm.dead && !mcDead && !ultrakillDrives)
            {
                Plugin.Log.LogInfo($"Minecraft's player moved itself to ({mc.X:0.0}, {mc.Y:0.0}, {mc.Z:0.0}): V1 follows");
                away = false;
            }
            // A Potion of ULTRAKILL: ULTRAKILL's movement moves V1, and Minecraft's player (still the
            // one with the items, health and attacks) is put where V1 is, 20 times a second.
            // (Not from the moment Minecraft's player steps into a Nether portal: Killcraft's mod says so.)
            // In the Nether V1 only has ground where Killcraft has built it from Minecraft's blocks
            // (NetherWorld): not just after arriving, nor over nothing (V1 would fall out of the Nether).
            if (crossed)
            {
                crossedAt = Time.unscaledTime;
            }
            bool netherGround = !Coords.InNether || (Time.unscaledTime - crossedAt > 2f
                && Physics.Raycast(nm.transform.position, Vector3.down, 24f * Coords.U, 1 << 8, QueryTriggerInteraction.Ignore));
            bool ukWanted = inGame && !loading && !crossed && !mcDisabled && !nm.dead && !mcDead && netherGround
                && Time.unscaledTime - Combat.LastSignal < 2f && Time.unscaledTime - Combat.LastStop > 3f;
            // Minecraft off (F9): ULTRAKILL has V1, and Minecraft's player follows it the same way (what
            // would hurt it hurts V1: Killcraft's mod), so it keeps the world (the Nether) loaded around
            // V1 and goes through the Nether portals V1 walks into. (Not while it's in one, nor just
            // after a trip.) In a portal it waits there for Minecraft's portal to take it, while V1 stays
            // in it too: V1 walking out (as off the portal it came out of) takes it along again.
            bool inPortal = Time.unscaledTime - Combat.LastStop < 0.5f;
            bool v1AtMc = haveMc && Math.Abs(mc.X - tx) < 1.5 && Math.Abs(mc.Y - ty) < 2.5 && Math.Abs(mc.Z - tz) < 1.5;
            netherFree = mcDisabled && Coords.InNether == v1InNether && inGame && !loading && haveMc && mcInWorld && !nm.dead && !mcDead
                && !(inPortal && v1AtMc) && Time.unscaledTime - crossedAt > 2f;
            if (netherFree)
            {
                teleportPending = false;  // (its own moves are this)
            }
            if (ultrakillDrives || netherFree)
            {
                // V1's real feet (its collider shrinks while sliding, so not a fixed offset): Minecraft's
                // player put even a little into the floor crawls, its eye at the ground.
                if (nm.playerCollider != null && nm.playerCollider.enabled)
                {
                    Vector3 pos = nm.transform.position;
                    Coords.ToMc(new Vector3(pos.x, nm.playerCollider.bounds.min.y + 0.02f * Coords.U, pos.z), out tx, out ty, out tz);
                }
                // Somewhere else than it was put (an ender pearl, a portal, respawning): V1 goes there.
                bool jumped = ultrakillDrives && ukTargetSet && teleportSeq == ukSeq && mc.TeleportAck == teleportSeq
                    && (Math.Abs(mc.X - ukX) > 4 || Math.Abs(mc.Y - ukY) > 4 || Math.Abs(mc.Z - ukZ) > 4);
                if (jumped)
                {
                    Plugin.Log.LogInfo($"Minecraft's player moved itself to ({mc.X:0.0}, {mc.Y:0.0}, {mc.Z:0.0}): V1 follows");
                    Vector3 root = Coords.ToUnity(mc.X, mc.Y, mc.Z) + Vector3.up * Coords.FeetBelowRoot;
                    nm.transform.position = root;
                    nm.rb.position = root;
                    nm.rb.velocity = Vector3.zero;
                    lastSetRoot = root;
                    tx = mc.X;
                    ty = mc.Y;
                    tz = mc.Z;
                    ukTargetSet = false;
                }
                // (Never to the other world's coordinates, nor in the Nether a sudden jump far away: V1
                // flung or moved by ULTRAKILL. Minecraft's player waits; V1 out of the Nether brings it back.)
                else if (Coords.IsNetherX(tx) != Coords.InNether
                    || (Coords.InNether && (Math.Abs(tx - mc.X) > 200 || Math.Abs(ty - mc.Y) > 200 || Math.Abs(tz - mc.Z) > 200)))
                {
                    if (Time.unscaledTime - farLogAt > 5f)
                    {
                        farLogAt = Time.unscaledTime;
                        Plugin.Log.LogInfo($"V1 is far from Minecraft's player ({tx:0}, {ty:0}, {tz:0}): it isn't moved there");
                    }
                }
                else if (!teleportPending && mc.TeleportAck == teleportSeq && Time.unscaledTime - ukSentAt >= 0.05f
                    && (!ukTargetSet || Math.Abs(tx - ukX) > 0.01 || Math.Abs(ty - ukY) > 0.01 || Math.Abs(tz - ukZ) > 0.01))
                {
                    teleportSeq++;
                    ukX = tx;
                    ukY = ty;
                    ukZ = tz;
                    ukTargetSet = true;
                    ukSeq = teleportSeq;
                    ukSentAt = Time.unscaledTime;
                }
                away = false;
            }
            else if (puppet && !away && !teleportPending && mc.TeleportAck == teleportSeq && !mcDead && !Coords.InNether
                && RisingFloor(Coords.ToUnity(mc.X, mc.Y, mc.Z), out Vector3 top))
            {
                Coords.ToMc(top, out tx, out ty, out tz);
                teleportSeq++;
            }
            StepTowards(away, inGame && !loading && !ultrakillTakes && !nm.dead && !Coords.InNether, tx, ty, tz);

            arriving = haveMc && mcInWorld && inGame && !loading && (mc.TeleportAck != teleportSeq || away) && !ultrakillTakes;
            // (While ULTRAKILL moves V1 its teleports are only just sent: Minecraft still has the player.)
            puppet = haveMc && mcInWorld && inGame && !loading && (mc.TeleportAck == teleportSeq || (ultrakillDrives && ukTargetSet)) && !away && !nm.dead && !ultrakillTakes;
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
            // A jump pad's launch lasts until V1 is down again.
            if (launching && (nm.dead || !inGame || loading || !netherGround || Time.unscaledTime - launchSince > 10f
                || (Time.unscaledTime - launchSince > 0.3f && nm.gc != null && nm.gc.onGround)))
            {
                launching = false;
            }
            SetUltrakillDrives(controlled != null && puppet && (ukWanted || launching));
            // Restarting from a checkpoint in the first moments of dying can leave ULTRAKILL's death
            // sequence (red screen, slowed sound) running on a living V1: end it the way a respawn does.
            if (puppet && !nm.dead && nm.deathSequence != null && nm.deathSequence.gameObject.activeSelf)
            {
                Plugin.Log.LogInfo("ULTRAKILL's death screen was still up on a living V1; closing it");
                nm.deathSequence.gameObject.SetActive(false);
            }
            Patches.OwnsPlayer = controlled != null;
            Lockout.Frame(controlled != null, ultrakillDrives);
            HeldItems.Frame(controlled != null, nm != null ? nm.cc : null);
            // (Also when ULTRAKILL has V1 — toggled off, dead: Minecraft's mobs and TNT carry on otherwise.)
            FreezeMinecraft(paused && inGame);
            // The chat McCommand opens isn't a Minecraft screen the player has open: Esc still belongs to
            // ULTRAKILL's menu.
            Patches.McScreenOpen = screenOpen && !McCommand.Busy;

            // (Respawning puts Minecraft's player at its spawn point: Killcraft's data pack keeps that
            // where the player last stood, so a death is a short move. See McSave.)
            InputForward.UltrakillMoves = ultrakillDrives;
            InputForward.Frame(controlled != null && puppet && !paused && !McCommand.Busy, screenOpen, Screen.width, Screen.height);
            // (Also while Minecraft is on its way to V1, as through a Nether portal: no flash of ULTRAKILL's HUD.)
            HudHider.Frame(controlled != null, nm);
            // (Before Combat publishes the damage requests MobHits makes.)
            MobHits.Frame(mcDisabled && inGame && !loading && haveMc && mcInWorld && !nm.dead, haveMc ? Coords.ToUnity(mc.X, mc.Y, mc.Z) : Vector3.zero);
            Combat.Frame(alive && inGame && mcInWorld && !mcDisabled, inGame && haveMc && mcInWorld, netherFree, nm);
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
                    // (Not with Minecraft off (F9): its player follows V1, and in between, as in a Nether
                    // portal, just stands. Held, SkyCraft would keep holding it at that spot after the
                    // portal took it, the level's coordinates in the Nether.)
                    | (loading || (!diedInNether && ((ultrakillTakes && !mcDisabled) || (nm != null && nm.dead) || (inGame && !(haveMc && mcInWorld)))) ? Proto.SkyLoading : 0),
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
                // SkyCraft also puts a new Minecraft player (one that respawned or changed dimension)
                // here, straight away: before Killcraft has seen where it is, so this has to be where it
                // will be. (Wrong, it's a jump to the other world's coordinates, which Minecraft's server
                // checks block by block, frozen for a minute.) Unless Killcraft is moving Minecraft's
                // player itself:
                //  - in the level, Minecraft's player having V1: where the portal it stands in would take
                //    it (Killcraft's data pack lands it at NetherX + x/8, 70, z/8);
                //  - in the Nether (any way out of it ends in the level): where it last was in the level,
                //    the portal it came through (or near its spawn point);
                //  - V1 still in the Nether (dead, Minecraft's player respawned): the same.
                bool ownTeleport = teleportPending || haveWaypoint || mc.TeleportAck != teleportSeq || away;
                // (Except a Potion of ULTRAKILL's own move, in the Nether too.)
                bool ukMove = (ultrakillDrives || netherFree) && mc.TeleportAck != teleportSeq;
                if ((Coords.InNether || diedInNether || v1InNether) && haveLevelPos && !ukMove)
                {
                    sky.X = levelX;
                    sky.Y = levelY;
                    sky.Z = levelZ;
                }
                // (Following V1 with Minecraft off, too: waiting in a portal, it's about to go through.)
                else if (haveMc && mcInWorld && !mcDead && (puppet || (mcDisabled && inPortal)) && !ownTeleport && !ukMove)
                {
                    sky.X = Coords.NetherX + Math.Floor(mc.X / 8) + 0.5;
                    sky.Y = Coords.NetherLandingY;
                    sky.Z = Math.Floor(mc.Z / 8) + 0.5;
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
        private float stepTimer, stepLog, inWorldFor, leaveNetherTimer, leftInNetherFor, respawnedFor;
        private bool diedInNether, haveLevelPos;
        private double levelX, levelY, levelZ;
        private bool haveWaypoint, everPuppet;
        private const float TeleportUnits = 8f;
        // ULTRAKILL's pause freezes Minecraft's world too (its mobs, TNT, ...): Killcraft's mod freezes
        // its ticking while Killcraft says so (Combat publishes it; nothing published, as on ULTRAKILL's
        // main menu, is never frozen). (It used to be /tick freeze typed into Minecraft's chat, which a
        // trip out of the Nether at the same moment could swallow, leaving Minecraft frozen.)
        private bool mcFrozen;
        internal static bool FreezeWanted;

        private void FreezeMinecraft(bool freeze)
        {
            if (freeze != mcFrozen && haveMc && mcInWorld)
            {
                mcFrozen = freeze;
                Plugin.Log.LogInfo(freeze ? "Minecraft frozen (paused)" : "Minecraft running again");
            }
            FreezeWanted = mcFrozen;
            // Its sound is off while paused, and while ULTRAKILL has V1 (F9): then Minecraft's player
            // only follows V1 about, and its steps, swings and burning would be noise.
            bool mute = mcFrozen || (mcDisabled && inGame);
            if (mute != mcMuted && haveMc)
            {
                mcMuted = mute;
                McAudio.SetMuted(Link.McPid(), mute);
            }
            McCommand.Frame(haveMc && mcInWorld, screenOpen);
        }
        private bool mcMuted;
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

        // Minecraft's third person (F5): ULTRAKILL's camera goes where Minecraft's is, behind the player
        // (or in front, looking back), pulled in where ULTRAKILL's level is in the way. Minecraft draws
        // its own player then, and that comes with its scene (WorldRender). Also run after ULTRAKILL's
        // camera has placed itself (Patches), so this always has the last word.
        private Vector3 lastEye, lastFeet;
        private bool thirdPerson, pausedNow;
        private const int CameraBlockers = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24) | (1 << 26);

        internal void ThirdPerson()
        {
            CameraController cc = controlled != null ? controlled.cc : null;
            bool on = cc != null && haveMc && mcInWorld && inGame && !pausedNow && mc.CameraMode != 0;
            Vector3 feet = controlled != null ? (puppet && !ultrakillDrives ? lastFeet : controlled.transform.position - Vector3.up * Coords.FeetBelowRoot) : Vector3.zero;
            WorldRender.PlaceAvatar(on, feet);
            if (!on)
            {
                if (thirdPerson && cc != null)
                {
                    cc.transform.localPosition = cc.defaultPos;
                }
                thirdPerson = false;
                return;
            }
            thirdPerson = true;
            Vector3 eye = puppet && !ultrakillDrives ? lastEye : cc.transform.parent.localToWorldMatrix.MultiplyPoint3x4(cc.defaultPos);
            float dist = (mc.CameraDistance > 0.1f ? mc.CameraDistance : 4f) * Coords.U;
            // Where V1 looks, from ULTRAKILL's look angles: not the camera's own facing, which this
            // turns round for the front view (and this runs twice a frame).
            Quaternion look = cc.transform.parent.rotation * Quaternion.AngleAxis(-cc.rotationX, Vector3.right);
            Vector3 forward = look * Vector3.forward;
            Vector3 dir = mc.CameraMode == 2 ? forward : -forward;
            if (Physics.SphereCast(eye, 0.2f * Coords.U, dir, out RaycastHit hit, dist, CameraBlockers, QueryTriggerInteraction.Ignore))
            {
                dist = Mathf.Max(0f, hit.distance - 0.05f * Coords.U);
            }
            cc.transform.position = eye + dir * dist;
            cc.transform.rotation = mc.CameraMode == 2 ? Quaternion.LookRotation(-forward, look * Vector3.up) : look;
        }

        // Floors that rise (the Cybergrind's pillars between waves, lifts) come up through Minecraft's
        // player before Minecraft has their new collision, and Minecraft never pushes a player out of
        // a shape: it falls through. When the surface under its feet is rising and already above them,
        // the player is put back on top.
        private Collider floorCollider;
        private float floorTop, floorLift;

        private bool RisingFloor(Vector3 feet, out Vector3 top)
        {
            top = feet;
            float u = Coords.U;
            Collider below = null;
            RaycastHit hit = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(feet + Vector3.up * (1.6f * u), Vector3.down, 1.65f * u, CameraBlockers, QueryTriggerInteraction.Ignore))
            {
                if (h.distance < nearest && !WorldRender.IsOurs(h.collider))
                {
                    nearest = h.distance;
                    below = h.collider;
                    hit = h;
                }
            }
            if (below == null)
            {
                floorCollider = null;
                return false;
            }
            float colliderTop = below.bounds.max.y;
            bool rising = below == floorCollider && colliderTop > floorTop + 0.001f;
            floorCollider = below;
            floorTop = colliderTop;
            floorLift -= Time.unscaledDeltaTime;
            if (!rising || hit.point.y < feet.y + 0.03f * u || floorLift > 0f)
            {
                return false;
            }
            floorLift = 0.1f;
            top = new Vector3(feet.x, hit.point.y + 0.01f * u, feet.z);
            return true;
        }

        // ULTRAKILL's jump pads and launchers set V1's velocity, which a puppet (kinematic) V1 doesn't
        // have: for the flight ULTRAKILL's physics move V1 (as for a Potion of ULTRAKILL), and
        // Minecraft takes over again when it lands. Called just before the launch (Patches).
        private bool launching;
        private float launchSince, crossedAt = -100f;

        internal void StartLaunch()
        {
            if (controlled == null || !puppet)
            {
                return;
            }
            launching = true;
            launchSince = Time.unscaledTime;
            if (!ultrakillDrives)
            {
                Plugin.Log.LogInfo("launched: ULTRAKILL's physics move V1 until it lands");
                SetUltrakillDrives(true);
            }
        }

        // A Potion of ULTRAKILL working (see Update): V1 is ULTRAKILL's physics again, not Minecraft's
        // puppet.
        private bool ultrakillDrives, ukTargetSet, netherFree;
        private float farLogAt = -100f;
        private double ukX, ukY, ukZ;
        private float ukSentAt;
        private uint ukSeq;

        private void SetUltrakillDrives(bool on)
        {
            if (on == ultrakillDrives)
            {
                return;
            }
            ultrakillDrives = on;
            ukTargetSet = false;
            Patches.UltrakillMoves = on;
            // Minecraft's movement keys held now would stay held.
            Link.PushInput(Proto.InReleaseAll);
            if (controlled != null && controlled.rb != null)
            {
                controlled.rb.velocity = Vector3.zero;
                controlled.rb.isKinematic = !on;
                controlled.rb.interpolation = on ? savedInterpolation : RigidbodyInterpolation.None;
            }
            Plugin.Log.LogInfo(on ? "ULTRAKILL's movement moves V1 (Potion of ULTRAKILL or a launch)" : "Minecraft moves V1 again");
        }

        private void SetControl(NewMovement nm)
        {
            if (ultrakillDrives)
            {
                ultrakillDrives = false;
                ukTargetSet = false;
                Patches.UltrakillMoves = false;
            }
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
            Overlay.Frame(haveMc && mcInWorld && controlled != null && !mcFrozen && !McCommand.Busy);
            WorldRender.Frame(haveMc && mcInWorld && inGame);
            MoveV1();
            NewMovement v1 = Find<NewMovement>();
            bool v1InNether = inGame && Coords.InNether && v1 != null && Coords.IsNetherUnity(v1.transform.position);
            Nether.Frame(v1InNether, v1 != null ? v1.cc : null);
            NetherWorld.Frame(v1InNether && (puppet || netherFree) && !mcFrozen && !v1.dead, v1 != null ? v1.transform.position : Vector3.zero);
            ThirdPerson();
            HeldItems.Pose(v1 != null ? v1.cc : null);
            SmileOs.Frame(inGame && haveMc && mcInWorld);
        }

        private void MoveV1()
        {
            if (controlled == null || controlled.cc == null)
            {
                return;
            }
            if (ultrakillDrives)
            {
                lastSetRoot = controlled.transform.position;  // ULTRAKILL moves it; Minecraft follows (Update)
            }
            else if (puppet)
            {
                Interpolate(out double fx, out double fy, out double fz, out double eye);
                lastFeet = Coords.ToUnity(fx, fy, fz);
                Vector3 root = lastFeet + Vector3.up * Coords.FeetBelowRoot;
                controlled.transform.position = root;
                controlled.rb.position = root;
                controlled.cc.transform.position = lastEye = Coords.ToUnity(fx, fy + eye, fz);
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
            // (Not while the tick values are far from where Minecraft's player is drawn: changing
            // dimension, Minecraft stops ticking for a moment while the new place loads, and they still
            // hold the old one.)
            bool ticksStale = Math.Abs(mc.CurX - mc.X) > 4 || Math.Abs(mc.CurY - mc.Y) > 4 || Math.Abs(mc.CurZ - mc.Z) > 4;
            if (mc.TickQpc != 0 && mc.TickMs > 0f && !ticksStale)
            {
                if (qpcFrequency == 0)
                {
                    Native.QueryPerformanceFrequency(out qpcFrequency);
                }
                Native.QueryPerformanceCounter(out long now);
                double period = mc.TickMs * qpcFrequency / 1000.0;
                double t = Math.Max(0.0, Math.Min(1.0, (now - mc.TickQpc) / period));
                // A jump (ender pearl, teleport) isn't movement: no sliding V1 through the walls between.
                if (Math.Abs(mc.CurX - mc.PrevX) > 4 || Math.Abs(mc.CurY - mc.PrevY) > 4 || Math.Abs(mc.CurZ - mc.PrevZ) > 4)
                {
                    t = 1.0;
                }
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

        private bool mcDisabled, toggleRefused;
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
            if (toggleNotice > 0f && toggleRefused)
            {
                return "Killcraft: Minecraft can only be turned off in the Nether while standing on ground";
            }
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
            if (arriving && !ultrakillDrives)
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
