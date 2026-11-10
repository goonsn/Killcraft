using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Killcraft
{
    // The other players in this level, as V1. Each ULTRAKILL tells the others where its V1 is, 20
    // times a second (KillNet's PlayerState): where, how fast, where it looks, what it's doing
    // (running, sliding, dashing, in the air) and which weapon it has out. One in Minecraft mode is
    // already there as their Minecraft player; one in ULTRAKILL mode is drawn as a V1 body instead,
    // and their Minecraft body isn't drawn meanwhile (the mod's HidePlayerMixin, told here which).
    //
    // The body is V2's (ULTRAKILL's only whole V1-shaped one), made V1: its enemy scripts, colliders
    // and wings taken off, its red turned V1's blue. Its own animations do the rest, as V2 does
    // them: running (forward, back, sideways), sliding, jumping, dashing; its arms point where the
    // player looks, holding V2's revolver, shotgun or nailgun, or (V2 has none) a copy of this
    // player's own railcannon or rocket launcher; shooting flashes its muzzle (Shots says when).
    internal static class RemotePlayers
    {
        private const float SendEvery = 0.05f;
        private const byte FlagUltrakill = 1, FlagGround = 2, FlagSliding = 4, FlagDead = 8, FlagDash = 16, FlagSlam = 32, FlagHook = 64;
        public const byte WeaponRevolver = 0, WeaponShotgun = 1, WeaponNailgun = 2, WeaponRailcannon = 3, WeaponRocket = 4, WeaponNone = 255;

        private sealed class Remote
        {
            public Vector3 Feet, Velocity, Look = Vector3.forward;
            public float Yaw, At;
            // What they said lately, by their own clock (shown a moment behind, between two of them:
            // smooth however unevenly they come), and that clock's offset from this one's, and how
            // unevenly they come. Everything of theirs is read off this one timeline at one moment
            // (PlayT): where they are, how fast, which way, what they're doing, their gun; and their
            // moves (Moves) wait for it, so a jump or a landing plays as the body gets there.
            public readonly List<Snap> Snaps = new List<Snap>();
            public readonly List<Move> Moves = new List<Move>();
            public bool Timed, Extrapolated;
            public float Offset = float.NaN, Jitter = 0.03f, PlayT, Delay;
            // When their last jump and landing played, by their clock (an event, or one made up from
            // their state when its event was lost: not both).
            public float JumpedAt = -100f, LandedAt = -100f, JumpMadeUpAt = -100f, LandMadeUpAt = -100f;
            // Debug markers: where they last said they were (red), where they're drawn (green).
            public GameObject RawMarker, ShownMarker;
            // Each frame's debug readout.
            public string Debug = "";
            public int Variant = -1;
            public byte Flags, Weapon = WeaponNone;
            public string WeaponName = "";
            public bool Alt;
            public GameObject Body;
            // ULTRAKILL's own third-person V1 (V1Body); without one here, V2's body (the rest below).
            public V1Body V1;
            public Animator Anim;
            public float Recoil;
            public float FeetToRoot;
            public Vector3 Shown;
            public bool Placed;
            // (Eased towards what they last said, 20 times a second: turning, looking, moving.)
            public float ShownYaw;
            public Vector3 ShownLook = Vector3.forward, ShownVelocity;
            // What its V2 body had (read before its scripts went).
            public GameObject[] Weapons = new GameObject[0];
            public Transform[] Aim = new Transform[0];
            public GameObject SlideEffect, DodgeEffect, JumpSound, DashSound;
            public GameObject[] MuzzleFlash = new GameObject[0];
            public Transform[] ShootPoint = new Transform[0];
            public Animator ShotgunAnim;
            public int Layer;
            public byte ShownWeapon = 254;
            public GameObject Held;
            public string HeldName;
            public bool WasGround = true, WasSliding, WasDash;
            public float FlashedAt = -100f;
            // Where the level's enemies (the authority's) go after them: a marker at their middle.
            public GameObject Marker;
            public bool HiddenNow;
            public float HideCheckAt;
            public EnemyTarget Target;
            // What they hold in their hand (ULTRAKILL's: a torch, a skull...), and its light here.
            public byte Item;
            public Light ItemLight;
        }

        // What they said at one moment of their clock.
        public struct Snap
        {
            public float T;
            public Vector3 Feet, Velocity, Look;
            public float Yaw;
            public byte Flags, Weapon;
            public bool Alt;
        }

        // A move of theirs (V1Body.Moves), waiting for its moment of their timeline.
        public struct Move
        {
            public float T, Value;
            public byte Kind;
        }

        // Their state as read off the timeline at the moment shown.
        private struct Sampled
        {
            public Vector3 Feet, Velocity, Look;
            public float Yaw;
            public byte Flags, Weapon;
            public bool Alt, Extrapolated;

            public void Take(in Snap s)
            {
                Feet = s.Feet;
                Velocity = s.Velocity;
                Look = s.Look;
                Yaw = s.Yaw;
                Flags = s.Flags;
                Weapon = s.Weapon;
                Alt = s.Alt;
            }
        }

        private static readonly List<EnemyTarget> targets = new List<EnemyTarget>();

        // The other players here, as targets for this ULTRAKILL's enemies (EnemySync).
        public static List<EnemyTarget> Targets() => targets;

        // Where the other players here are (a little above their feet: WorldSync's doors).
        private static readonly List<Vector3> positions = new List<Vector3>();

        public static List<Vector3> Positions() => positions;

        // Where the other players' bodies stand here now (their feet, as drawn): PlayerCollision.
        private static readonly List<Vector3> bodies = new List<Vector3>();

        public static List<Vector3> Bodies() => bodies;

        private static readonly Dictionary<int, Remote> remotes = new Dictionary<int, Remote>();
        private static float sendTimer, hideTimer;
        private static Vector3 lastFeet;
        private static readonly Queue<(float T, Vector3 P)> trail = new Queue<(float T, Vector3 P)>();
        private static int lastHidden;
        private static readonly List<int> gone = new List<int>();
        private static readonly List<Guid> hide = new List<Guid>();
        private static bool noBody, loggedParts, loggedHeld;
        private static Material blueBody;

        public static void Frame(NewMovement nm, CameraController cc, bool inLevel, bool ultrakillMode)
        {
            if ((sendTimer -= Time.unscaledDeltaTime) <= 0f && Room.Sharing && inLevel && nm != null)
            {
                float dt = SendEvery - sendTimer;
                sendTimer = SendEvery;
                Send(nm, cc, dt, ultrakillMode);
                SendItem();
            }
            NetSim.Frame();
            DebugKeys();

            // Gone (left the level or the world: quiet for a while). (Lists kept, not made each frame:
            // what's made each frame is the garbage collector's work, a stutter now and then.)
            gone.Clear();
            foreach (var kv in remotes)
            {
                if (Time.unscaledTime - kv.Value.At > 3f)
                {
                    gone.Add(kv.Key);
                }
            }
            foreach (int id in gone)
            {
                Destroy(remotes[id]);
                remotes.Remove(id);
            }

            targets.Clear();
            positions.Clear();
            bodies.Clear();
            Pvp.Begin();
            foreach (var each in remotes)
            {
                Remote r = each.Value;
                Pose(each.Key, r);
                bool alive = (r.Flags & FlagDead) == 0 && Time.unscaledTime - r.At < 1f && !MinosMode.IsMinos(each.Key);
                // (Where they're drawn: their V1 body, or their Minecraft player, where Minecraft has it.)
                bool asV1 = (r.Flags & FlagUltrakill) != 0;
                Vector3 drawn = r.Body != null && r.Body.activeSelf ? r.Shown : r.Feet + r.Velocity * Mathf.Min(Time.unscaledTime - r.At, 0.1f);
                Pvp.Place(each.Key, drawn, r.Yaw, asV1, (r.Flags & FlagSliding) != 0, alive && inLevel);
                if (alive)
                {
                    positions.Add(r.Feet + Vector3.up * 1f);
                    if (inLevel)
                    {
                        bodies.Add(drawn);
                    }
                }
                if (r.Marker == null)
                {
                    r.Marker = new GameObject("Killcraft other player (enemies' target)");
                    r.Target = new EnemyTarget(r.Marker.transform);
                }
                r.Marker.transform.position = r.Feet + r.Velocity * Mathf.Min(Time.unscaledTime - r.At, 0.15f) + Vector3.up * Coords.FeetBelowRoot;
                if (r.Marker.activeSelf != alive)
                {
                    r.Marker.SetActive(alive);
                }
                if (alive)
                {
                    targets.Add(r.Target);
                }
            }

            Pvp.End();

            // Which players' Minecraft bodies Killcraft's mod hides: those drawn as V1 here.
            hideTimer -= Time.unscaledDeltaTime;
            hide.Clear();
            int key = 17;
            foreach (var kv in remotes)
            {
                if (kv.Value.Body != null && kv.Value.Body.activeSelf && Room.Uuids.TryGetValue(kv.Key, out Guid uuid))
                {
                    hide.Add(uuid);
                    key = key * 31 + uuid.GetHashCode();
                }
            }
            key = key * 31 + hide.Count;
            if (key != lastHidden || hideTimer <= 0f)
            {
                lastHidden = key;
                hideTimer = 2f;
                KillNet.Send(KillNet.HidePlayers, KillNet.ToMinecraft, w =>
                {
                    w.Write(hide.Count);
                    foreach (Guid g in hide)
                    {
                        Room.WriteUuid(w, g);
                    }
                });
            }
        }

        // ---- this player ----

        private static void Send(NewMovement nm, CameraController cc, float dt, bool ultrakillMode)
        {
            Vector3 feet = nm.transform.position - Vector3.up * Coords.FeetBelowRoot;
            // (ULTRAKILL's own, moving V1: what its animations go by. Else, from where it was.)
            // (Moved by Minecraft, its 20 steps a second: from a quarter second of where it's been, not
            // frame to frame, most of which it hadn't moved; then the others' V1 ran in fits, mostly
            // standing still.)
            float now = Time.unscaledTime;
            if ((feet - lastFeet).sqrMagnitude > 20f * 20f)
            {
                trail.Clear();  // (a teleport)
            }
            trail.Enqueue((now, feet));
            while (trail.Count > 2 && now - trail.Peek().T > 0.25f)
            {
                trail.Dequeue();
            }
            var oldest = trail.Peek();
            Vector3 moved = now - oldest.T > 0.03f ? (feet - oldest.P) / (now - oldest.T) : Vector3.zero;
            Vector3 velocity = !Patches.OwnsPlayer && nm.rb != null && !nm.rb.isKinematic ? nm.rb.velocity : moved;
            if (velocity.sqrMagnitude > 200f * 200f)
            {
                velocity = Vector3.zero;  // (a teleport)
            }
            lastFeet = feet;
            bool ground = false;
            try
            {
                ground = nm.gc != null && nm.gc.onGround;
            }
            catch
            {
            }
            bool hook = false;
            try
            {
                HookArm arm = MonoSingleton<HookArm>.Instance;
                hook = arm != null && arm.state != HookState.Ready;
            }
            catch
            {
            }
            byte flags = (byte)((ultrakillMode ? FlagUltrakill : 0) | (ground ? FlagGround : 0) | (nm.sliding ? FlagSliding : 0)
                | (nm.dead ? FlagDead : 0) | (nm.boost && !nm.sliding ? FlagDash : 0) | (nm.gc != null && nm.gc.heavyFall ? FlagSlam : 0)
                | (hook ? FlagHook : 0));
            float yaw = cc != null ? cc.rotationY : nm.transform.eulerAngles.y;
            Vector3 look = cc != null ? cc.transform.forward : nm.transform.forward;
            GameObject weapon = CurrentWeapon(out byte kind, out bool alt);
            string weaponName = weapon != null ? Shots.PrefabName(weapon) : "";
            KillNet.Send(KillNet.PlayerState, KillNet.ToRoom, w =>
            {
                w.Write(feet.x);
                w.Write(feet.y);
                w.Write(feet.z);
                w.Write(velocity.x);
                w.Write(velocity.y);
                w.Write(velocity.z);
                w.Write(yaw);
                w.Write(flags);
                w.Write(look.x);
                w.Write(look.y);
                w.Write(look.z);
                w.Write(kind);
                w.Write(alt);
                w.Write(weaponName);
                // (Newer: the gun's variant, and when, by this player's clock.)
                w.Write((sbyte)Skins.CurrentVariant());
                w.Write(Time.unscaledTime);
            });
        }

        // (Which of the five, as PlayerAnimations counts them: its slot; and whether it's the
        // alternate one of that slot.)
        private static GameObject CurrentWeapon(out byte kind, out bool alt)
        {
            kind = WeaponNone;
            alt = false;
            var gc = MonoSingleton.GetInstance(typeof(GunControl)) as GunControl;
            GameObject w = gc != null ? gc.currentWeapon : null;
            if (w == null || !w.activeInHierarchy)
            {
                return null;
            }
            int slot = gc.currentSlotIndex - 1;
            kind = slot >= 0 && slot <= WeaponRocket ? (byte)slot : KindOf(w);
            alt = w.TryGetComponent(out WeaponIdentifier wid) && wid.alternateVersion;
            return w;
        }

        private static byte KindOf(GameObject w) =>
            w.GetComponent<Revolver>() != null ? WeaponRevolver
            : w.GetComponent<Shotgun>() != null || w.GetComponent<ShotgunHammer>() != null ? WeaponShotgun
            : w.GetComponent<Nailgun>() != null ? WeaponNailgun
            : w.GetComponent<Railcannon>() != null ? WeaponRailcannon
            : w.GetComponent<RocketLauncher>() != null ? WeaponRocket
            : WeaponNone;

        // ---- the others ----

        public static void Handle(int from, BinaryReader r)
        {
            if (NetSim.Hold(from, KillNet.PlayerState, r))
            {
                return;
            }
            HandleNow(from, r);
        }

        // (From NetSim, when its pretend lag is up.)
        internal static void Deliver(int from, byte type, BinaryReader r)
        {
            if (type == KillNet.PlayerState)
            {
                HandleNow(from, r);
            }
            else if (type == V1Body.Moves)
            {
                HandleMoveNow(from, r);
            }
        }

        private static void HandleNow(int from, BinaryReader r)
        {
            if (!remotes.TryGetValue(from, out Remote remote))
            {
                remotes[from] = remote = new Remote();
                Plugin.Log.LogInfo($"KillNet: player {from} is in this level");
            }
            remote.Feet = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            remote.Velocity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            remote.Yaw = r.ReadSingle();
            remote.Flags = r.ReadByte();
            Vector3 look = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (look.sqrMagnitude > 0.01f)
            {
                remote.Look = look.normalized;
            }
            remote.Weapon = r.ReadByte();
            remote.Alt = r.ReadBoolean();
            remote.WeaponName = r.ReadString();
            remote.At = Time.unscaledTime;
            if (r.BaseStream.Length - r.BaseStream.Position >= 5)
            {
                remote.Variant = r.ReadSByte();
                Heard(remote, r.ReadSingle());
            }
        }

        // When they said it, by their clock: kept in order, the clocks' offset and the unevenness followed.
        private static void Heard(Remote remote, float at)
        {
            float now = Time.unscaledTime;
            float sample = now - at;
            if (!remote.Timed || float.IsNaN(remote.Offset) || Mathf.Abs(sample - remote.Offset) > 5f)
            {
                remote.Offset = sample;
                remote.Snaps.Clear();
                remote.Timed = true;
            }
            else if (sample < remote.Offset)
            {
                remote.Offset = sample;
            }
            else
            {
                // (Drifting up slowly: the quickest lately is the true offset.)
                remote.Offset += (sample - remote.Offset) * 0.01f;
            }
            remote.Jitter = Mathf.Lerp(remote.Jitter, Mathf.Clamp(sample - remote.Offset, 0f, 0.5f), 0.08f);
            var snap = new Snap
            {
                T = at, Feet = remote.Feet, Velocity = remote.Velocity, Look = remote.Look, Yaw = remote.Yaw,
                Flags = remote.Flags, Weapon = remote.Weapon, Alt = remote.Alt,
            };
            List<Snap> list = remote.Snaps;
            if (list.Count > 0 && at <= list[list.Count - 1].T)
            {
                return;  // (late, out of order)
            }
            list.Add(snap);
            while (list.Count > 40 || (list.Count > 2 && list[0].T < at - 2f))
            {
                list.RemoveAt(0);
            }
        }

        // The moment of their timeline shown now: behind the newest they said by the delay (RemoteTuning).
        private static float PlayTime(Remote r)
        {
            r.Delay = Mathf.Clamp(RemoteTuning.DelayBase + RemoteTuning.DelayPerJitter * r.Jitter, RemoteTuning.DelayMin, RemoteTuning.DelayMax);
            return Time.unscaledTime - r.Offset - r.Delay;
        }

        // Their state at the moment shown, all of it from the one time: between the two they said either
        // side of it (the place along the curve their speeds make; speed, facing and look between the
        // two; what they were doing and holding as of the earlier one, so a jump or a slide begins as
        // the body gets to where it began); past the newest, carried on by its speed a little, then held.
        private static Sampled Sample(Remote r)
        {
            var s = new Sampled { Feet = r.Feet, Velocity = r.Velocity, Look = r.Look, Yaw = r.Yaw, Flags = r.Flags, Weapon = r.Weapon, Alt = r.Alt };
            List<Snap> list = r.Snaps;
            if (!r.Timed || list.Count == 0)
            {
                // (An older Killcraft, without its clock: the newest, carried on a little.)
                s.Feet = r.Feet + r.Velocity * Mathf.Min(Time.unscaledTime - r.At, 0.1f);
                r.PlayT = Time.unscaledTime;
                return s;
            }
            float t = r.PlayT = PlayTime(r);
            if (t <= list[0].T)
            {
                s.Take(list[0]);
                return s;
            }
            for (int i = list.Count - 1; i >= 1; i--)
            {
                Snap a = list[i - 1], b = list[i];
                if (a.T > t)
                {
                    continue;
                }
                if (t > b.T)
                {
                    break;
                }
                float dt = b.T - a.T;
                if (dt <= 0.0001f || (b.Feet - a.Feet).sqrMagnitude > 60f * 60f)
                {
                    s.Take(b);  // (a teleport: there at once)
                    return s;
                }
                s.Take(a);
                float u = (t - a.T) / dt, u2 = u * u, u3 = u2 * u;
                float h00 = 2f * u3 - 3f * u2 + 1f, h10 = u3 - 2f * u2 + u, h01 = -2f * u3 + 3f * u2, h11 = u3 - u2;
                // (Speeds that don't fit the distance, as a dash's start: straight between them instead.)
                Vector3 straight = Vector3.Lerp(a.Feet, b.Feet, u);
                Vector3 curve = h00 * a.Feet + h10 * dt * a.Velocity + h01 * b.Feet + h11 * dt * b.Velocity;
                s.Feet = (curve - straight).sqrMagnitude > 4f ? straight : curve;
                s.Velocity = Vector3.Lerp(a.Velocity, b.Velocity, u);
                s.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, u);
                s.Look = Vector3.Slerp(a.Look, b.Look, u);
                return s;
            }
            Snap last = list[list.Count - 1];
            s.Take(last);
            s.Feet = last.Feet + last.Velocity * Mathf.Min(t - last.T, RemoteTuning.ExtrapolateMax);
            s.Extrapolated = t - last.T > 0.001f;
            return s;
        }

        // Their moves whose moment has come, in order.
        private static void PlayMoves(Remote r)
        {
            List<Move> moves = r.Moves;
            while (moves.Count > 0 && moves[0].T <= r.PlayT)
            {
                Move m = moves[0];
                moves.RemoveAt(0);
                Play(r, m.Kind, m.Value, m.T);
            }
        }

        // A move of theirs played (an event of theirs; not a jump or landing made up from their state
        // just before, when the event seemed lost).
        private static void Play(Remote r, byte move, float value, float at)
        {
            if (move == V1Body.MoveJump)
            {
                if (at - r.JumpMadeUpAt < RemoteTuning.EventFallbackWindow)
                {
                    return;
                }
                r.JumpedAt = at;
            }
            else if (move == V1Body.MoveLand)
            {
                if (at - r.LandMadeUpAt < RemoteTuning.EventFallbackWindow)
                {
                    return;
                }
                r.LandedAt = at;
            }
            if (r.V1 != null && r.Body != null && r.Body.activeSelf)
            {
                r.V1.Move(move, value);
            }
        }

        // A jump or a landing seen in their state (off the ground going up; back on it) with no event
        // of it come (lost: they're sent unreliably): made up.
        private static void MakeUpMoves(Remote r, in Sampled s, bool ground, bool sliding, bool dash, bool wasGround)
        {
            if (!r.Timed || !r.Placed || r.V1 == null)
            {
                return;
            }
            float t = r.PlayT;
            if (!ground && wasGround && !sliding && !dash && s.Velocity.y > 3f && t - r.JumpedAt > RemoteTuning.EventFallbackWindow)
            {
                r.JumpedAt = r.JumpMadeUpAt = t;
                r.V1.Move(V1Body.MoveJump, 0f, false);
            }
            else if (ground && !wasGround && t - r.LandedAt > RemoteTuning.EventFallbackWindow)
            {
                r.LandedAt = r.LandMadeUpAt = t;
                r.V1.Move(V1Body.MoveLand, Mathf.Clamp01(Mathf.Max(0f, -r.ShownVelocity.y) / 100f), false);
            }
        }

        // A shot of theirs (Shots): their muzzle flashes, and a shotgun pumps.
        public static void Fired(int from)
        {
            if (!remotes.TryGetValue(from, out Remote r) || r.Body == null || !r.Body.activeSelf || Time.unscaledTime - r.FlashedAt < 0.08f)
            {
                return;
            }
            r.FlashedAt = Time.unscaledTime;
            ReplayPlayer.Shot(from);
            try
            {
                GunSound(from, r);
            }
            catch
            {
            }
            if (r.V1 != null)
            {
                if (!r.V1.HeardMoves)
                {
                    r.V1.Shoot(1f);
                }
                return;
            }
            r.Recoil = Mathf.Max(r.Recoil, RecoilOf(r.Weapon));
            int i = r.Weapon;
            if (i < r.MuzzleFlash.Length && r.MuzzleFlash[i] != null)
            {
                Transform at = i < r.ShootPoint.Length && r.ShootPoint[i] != null ? r.ShootPoint[i] : r.Weapons[i].transform;
                try
                {
                    UnityEngine.Object.Instantiate(r.MuzzleFlash[i], at.position, Quaternion.LookRotation(r.Look));
                }
                catch
                {
                }
            }
            if (i == WeaponShotgun && r.ShotgunAnim != null)
            {
                r.ShotgunAnim.SetTrigger("Shoot");
            }
        }

        // How far a V2 body's arm kicks up with each weapon's shot (V2 has no shooting animation of its own).
        private static float RecoilOf(byte weapon)
        {
            switch (weapon)
            {
                case WeaponRevolver: return 18f;
                case WeaponShotgun: return 28f;
                case WeaponNailgun: return 5f;
                case WeaponRailcannon: return 40f;
                case WeaponRocket: return 22f;
                default: return 0f;
            }
        }

        // What ULTRAKILL told their own PlayerAnimations (a shot, punch, coin toss, jump, landing): kept
        // for its moment of their timeline, when the body's where they were then (an older Killcraft's,
        // without its time, or one off their timeline by more than EventMaxWait: played now).
        public static void HandleMove(int from, BinaryReader r)
        {
            if (NetSim.Hold(from, V1Body.Moves, r))
            {
                return;
            }
            HandleMoveNow(from, r);
        }

        private static void HandleMoveNow(int from, BinaryReader r)
        {
            V1Body.Read(r, out byte move, out float value, out float at);
            if (!remotes.TryGetValue(from, out Remote remote))
            {
                return;
            }
            if (float.IsNaN(at) || !remote.Timed || float.IsNaN(remote.Offset) || Mathf.Abs(at - remote.PlayT) > RemoteTuning.EventMaxWait)
            {
                Play(remote, move, value, remote.PlayT);
                return;
            }
            List<Move> list = remote.Moves;
            int i = list.Count;
            while (i > 0 && list[i - 1].T > at)
            {
                i--;
            }
            list.Insert(i, new Move { T = at, Kind = move, Value = value });
            if (list.Count > 64)
            {
                list.RemoveAt(0);
            }
        }

        public static void LevelChanged()
        {
            foreach (Remote r in remotes.Values)
            {
                Destroy(r);
            }
            remotes.Clear();
            V1Body.LevelChanged();
            gunClips.Clear();
            torchTemplate = null;
            torchLookedAt = -100f;
            itemSent = 255;
        }

        private static void Pose(int id, Remote r)
        {
            // (A Minos Prime round's Minos player: their V1's put away, Minos is what's seen of them.)
            bool show = (r.Flags & FlagUltrakill) != 0 && (r.Flags & FlagDead) == 0 && Time.unscaledTime - r.At < 1.5f && !MinosMode.IsMinos(id);
            if (show && r.Body == null)
            {
                r.V1 = V1Body.Make();
                if (r.V1 != null)
                {
                    r.Body = r.V1.Root;
                }
                else if (!noBody)
                {
                    MakeBody(r);
                }
            }
            if (r.Body == null)
            {
                return;
            }
            if (r.Body.activeSelf != show)
            {
                r.Body.SetActive(show);
                r.Placed = false;
            }
            if (!show)
            {
                r.Moves.Clear();  // (not drawn: its moves not saved up for when it is)
                return;
            }
            bool hide = id == HiddenId;
            // (Hidden: again now and then, for what's made on it later, as its gun's own model.)
            if (hide != r.HiddenNow || (hide && Time.unscaledTime > r.HideCheckAt))
            {
                r.HiddenNow = hide;
                r.HideCheckAt = Time.unscaledTime + 0.03f;
                Hide(r.Body, hide);
                Hide(r.Held, hide);
            }
            ItemLight(r);
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
            // All of their state as of the one moment of their timeline that's shown now.
            Sampled s = Sample(r);
            r.Extrapolated = s.Extrapolated;
            bool ground = (s.Flags & FlagGround) != 0, sliding = (s.Flags & FlagSliding) != 0, dash = (s.Flags & FlagDash) != 0,
                slam = (s.Flags & FlagSlam) != 0, hook = (s.Flags & FlagHook) != 0;
            // On the ground: the feet on the floor found under them, if it's near (not down a step or
            // ledge they were only half over).
            Vector3 target = s.Feet;
            if (ground && Physics.Raycast(target + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 1.5f + RemoteTuning.GroundSnapMax, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)
                && Mathf.Abs(hit.point.y - target.y) <= RemoteTuning.GroundSnapMax)
            {
                target.y = hit.point.y;
            }
            // (Eased into, a little, against the odd step in the timeline; far off, there at once.)
            r.Shown = r.Placed && (target - r.Shown).sqrMagnitude < 60f * 60f
                ? Vector3.Lerp(r.Shown, target, 1f - Mathf.Exp(-(r.Timed ? RemoteTuning.PositionEase : 15f) * dt))
                : target;
            if (!r.Placed)
            {
                r.ShownYaw = s.Yaw;
                r.ShownLook = s.Look;
                r.ShownVelocity = s.Velocity;
            }
            float ease = 1f - Mathf.Exp(-RemoteTuning.YawEase * dt);
            r.ShownYaw = Mathf.LerpAngle(r.ShownYaw, s.Yaw, ease);
            r.ShownLook = Vector3.Slerp(r.ShownLook, s.Look, ease);
            bool wasGround = r.WasGround, wasSliding = r.WasSliding, wasDash = r.WasDash;
            // Their moves whose moment has come; a jump or landing whose event was lost, from their state.
            PlayMoves(r);
            MakeUpMoves(r, s, ground, sliding, dash, wasGround);
            r.ShownVelocity = Vector3.Lerp(r.ShownVelocity, s.Velocity, 1f - Mathf.Exp(-RemoteTuning.VelocityEase * dt));
            r.WasGround = ground;
            r.WasSliding = sliding;
            r.WasDash = dash;
            r.Placed = true;
            Transform t = r.Body.transform;
            t.rotation = Quaternion.Euler(0f, r.ShownYaw, 0f);
            if (r.V1 != null)
            {
                // (Its root is V1's middle, as this player's is.)
                t.position = r.Shown + Vector3.up * Coords.FeetBelowRoot;
                r.V1.Pose(r.ShownVelocity, ground, sliding, dash, slam, hook, s.Weapon, s.Alt, dt);
                r.V1.Look(Profiles.Of(id), s.Weapon, s.Alt, r.Variant);
                if (debugOn)
                {
                    DebugMark(r);
                    r.Debug = $"delay {r.Delay * 1000f:0}ms jitter {r.Jitter * 1000f:0}ms snaps {r.Snaps.Count} moves {r.Moves.Count}"
                        + $"{(s.Extrapolated ? " EXTRAPOLATING" : "")} raw-drawn {(r.Feet - r.Shown).magnitude:0.00}\n  {r.V1.Describe()}";
                }
                return;
            }
            t.position = r.Shown + Vector3.up * r.FeetToRoot;

            ShowWeapon(r);

            if (r.Anim == null)
            {
                return;
            }
            Vector3 flat = new Vector3(s.Velocity.x, 0f, s.Velocity.z);
            // What they just started doing: as V2 does it.
            if (sliding && !wasSliding)
            {
                r.Anim.Play("Slide", 0, 0f);
            }
            if (r.SlideEffect != null && r.SlideEffect.activeSelf != sliding)
            {
                r.SlideEffect.SetActive(sliding);
            }
            if (dash && !wasDash)
            {
                r.Anim.SetTrigger("Jump");
                Effect(r.DodgeEffect, r.Shown + Vector3.up * 2f, flat.sqrMagnitude > 1f ? Quaternion.LookRotation(flat) : t.rotation);
                Effect(r.DashSound, r.Shown, t.rotation);
            }
            else if (!ground && wasGround && s.Velocity.y > 3f && !sliding)
            {
                r.Anim.SetTrigger("Jump");
                Effect(r.JumpSound, r.Shown, t.rotation);
            }

            bool running = ground && !sliding && !dash && flat.magnitude > 1.5f;
            // (As V2 does: its body faces where it goes, its root where it looks.)
            Transform model = r.Anim.transform;
            model.rotation = flat.sqrMagnitude > 1f ? Quaternion.LookRotation(flat.normalized) : t.rotation;
            r.Anim.SetBool("InAir", !ground || dash);
            r.Anim.SetBool("Sliding", sliding);
            if (!running)
            {
                r.Anim.SetBool("RunningBack", false);
                if (r.Anim.layerCount > 1)
                {
                    r.Anim.SetLayerWeight(1, 0f);
                }
                if (r.Anim.layerCount > 2)
                {
                    r.Anim.SetLayerWeight(2, 0f);
                }
                return;
            }
            float angle = Quaternion.Angle(model.rotation, t.rotation);
            r.Anim.SetBool("RunningLeft", model.rotation.eulerAngles.y > t.rotation.eulerAngles.y);
            r.Anim.SetBool("RunningBack", angle > 90f);
            if (r.Anim.layerCount > 1)
            {
                r.Anim.SetLayerWeight(1, 1f);
            }
            if (r.Anim.layerCount > 2)
            {
                r.Anim.SetLayerWeight(2, angle <= 90f ? angle / 90f : Mathf.Abs(-180f + angle) / 90f);
            }
        }

        // After the animations this frame: arms (and what they hold) where the player looks.
        public static void LateFrame()
        {
            foreach (Remote r in remotes.Values)
            {
                if (r.Body == null || !r.Body.activeSelf)
                {
                    continue;
                }
                Vector3 look = r.ShownLook;
                if (r.V1 != null)
                {
                    r.V1.Late(look);
                    continue;
                }
                // (As V2 aims: LookAt, then its arm's own 10 degrees; kicked up by a shot, settling back.)
                r.Recoil = Mathf.MoveTowards(r.Recoil, 0f, Time.deltaTime * Mathf.Max(60f, r.Recoil * 8f));
                if (r.Aim.Length > 0 && r.Aim[0] != null)
                {
                    r.Aim[0].rotation = Quaternion.LookRotation(look);
                    r.Aim[0].Rotate(Vector3.right, 10f - r.Recoil, Space.Self);
                }
                if (r.Aim.Length > 1 && r.Aim[1] != null)
                {
                    r.Aim[1].rotation = Quaternion.LookRotation(look, Vector3.up);
                    r.Aim[1].Rotate(Vector3.up, 180f, Space.Self);
                }
                if (r.Held != null && r.Held.activeSelf)
                {
                    Transform hand = r.Weapons.Length > 0 && r.Weapons[0] != null ? r.Weapons[0].transform : r.Body.transform;
                    r.Held.transform.SetPositionAndRotation(hand.position + look * 0.3f, Quaternion.LookRotation(look));
                }
            }
        }

        private static void Effect(GameObject prefab, Vector3 at, Quaternion rotation)
        {
            if (prefab == null)
            {
                return;
            }
            try
            {
                UnityEngine.Object.Instantiate(prefab, at, rotation);
            }
            catch
            {
            }
        }

        // V2's revolver, shotgun or nailgun, as theirs is; their railcannon or rocket launcher (V2 has
        // neither) copied from this player's own, if this player has one like it.
        private static void ShowWeapon(Remote r)
        {
            byte kind = r.Weapon;
            bool v2Has = kind < r.Weapons.Length && r.Weapons[kind] != null;
            if (kind != r.ShownWeapon || (!v2Has && kind != WeaponNone && r.HeldName != r.WeaponName))
            {
                r.ShownWeapon = kind;
                for (int i = 0; i < r.Weapons.Length; i++)
                {
                    if (r.Weapons[i] != null)
                    {
                        r.Weapons[i].SetActive(i == kind);
                    }
                }
                if (r.Held != null)
                {
                    UnityEngine.Object.Destroy(r.Held);
                    r.Held = null;
                    r.HeldName = null;
                }
                if (!v2Has && kind != WeaponNone)
                {
                    r.HeldName = r.WeaponName;
                    r.Held = CopyWeapon(r, kind, r.WeaponName);
                }
            }
        }

        private static GameObject CopyWeapon(Remote r, byte kind, string name)
        {
            var gc = MonoSingleton.GetInstance(typeof(GunControl)) as GunControl;
            if (gc == null || gc.slots == null)
            {
                return null;
            }
            GameObject source = null;
            foreach (List<GameObject> slot in gc.slots)
            {
                if (slot == null)
                {
                    continue;
                }
                foreach (GameObject w in slot)
                {
                    if (w == null)
                    {
                        continue;
                    }
                    if (Shots.PrefabName(w) == name)
                    {
                        source = w;
                        break;
                    }
                    if (source == null && KindOf(w) == kind)
                    {
                        source = w;
                    }
                }
            }
            if (source == null)
            {
                return null;
            }
            GameObject go = Shots.MakeOff(source, Vector3.zero, Quaternion.identity);
            go.name = "Killcraft other player's " + name;
            try
            {
                StripAll(go);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't copy a {name} for another player to hold ({e.Message})");
                UnityEngine.Object.Destroy(go);
                return null;
            }
            var parts = new List<string>();
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = r.Layer;
            }
            foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true))
            {
                // (V1's own arm, holding it in first person: theirs is the body's.)
                bool arm = rd.name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0 || rd.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0
                    || rd.GetType().Name == "ParticleSystemRenderer" || rd is TrailRenderer || rd is LineRenderer;
                if (arm)
                {
                    rd.enabled = false;
                }
                parts.Add(rd.name + (arm ? " (off)" : ""));
            }
            go.transform.SetParent(null, true);
            go.SetActive(true);
            // About the size of a gun in V1's hand.
            Bounds b = default;
            bool any = false;
            foreach (Renderer rd in go.GetComponentsInChildren<Renderer>())
            {
                if (!rd.enabled)
                {
                    continue;
                }
                if (!any)
                {
                    b = rd.bounds;
                    any = true;
                }
                else
                {
                    b.Encapsulate(rd.bounds);
                }
            }
            float size = any ? Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) : 0f;
            if (size > 0.01f)
            {
                go.transform.localScale *= (kind == WeaponRailcannon ? 2.2f : 1.8f) / size;
            }
            if (!loggedHeld)
            {
                loggedHeld = true;
                Plugin.Log.LogInfo($"KillNet: other player's {name} copied from this player's ({size:0.00} big): {string.Join(", ", parts)}");
            }
            return go;
        }

        private static void MakeBody(Remote r)
        {
            GameObject prefab = NetherWorld.PrefabFor(EnemyType.V2);
            if (prefab == null)
            {
                noBody = true;
                Plugin.Log.LogWarning("KillNet: no V2 in ULTRAKILL's list of enemies, so other players in ULTRAKILL mode show as their Minecraft body");
                return;
            }
            // Made switched off (its scripts mustn't start: it would be an enemy), stripped, then on.
            GameObject go = Shots.MakeOff(prefab, Vector3.zero, Quaternion.identity);
            go.name = "Killcraft other player";
            SkinnedMeshRenderer smr = null;
            try
            {
                smr = ReadV2(r, go);
                StripAll(go);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't make a V1 body ({e.Message})");
                UnityEngine.Object.Destroy(go);
                noBody = true;
                return;
            }

            var parts = new List<string>();
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                // (Its holiday hats and spawn flash: a script chose those, and it's gone.)
                if (n == "Pumpkin" || n == "Christmas" || n == "Easter" || n.StartsWith("SpawnEffect", StringComparison.Ordinal))
                {
                    t.gameObject.SetActive(false);
                }
            }
            // V2's wings (V1 has none): their trails, and the wings themselves (the second of its body's materials).
            foreach (TrailRenderer tr in go.GetComponentsInChildren<TrailRenderer>(true))
            {
                tr.enabled = false;
            }
            if (smr != null)
            {
                Material[] mats = smr.sharedMaterials;
                var names = new List<string>();
                foreach (Material m in mats)
                {
                    names.Add(m != null ? m.name : "none");
                }
                parts.Add($"{smr.name} [{string.Join(", ", names)}]");
                if (mats.Length > 0 && mats[0] != null)
                {
                    smr.sharedMaterials = new[] { Blue(mats[0]) };
                }
                r.Layer = smr.gameObject.layer;
            }
            if (r.SlideEffect != null)
            {
                r.SlideEffect.SetActive(false);
            }
            foreach (GameObject w in r.Weapons)
            {
                if (w != null)
                {
                    w.SetActive(false);
                }
            }
            go.transform.SetParent(null, true);
            go.SetActive(true);
            r.Body = go;
            r.Anim = smr != null ? smr.GetComponentInParent<Animator>() : null;
            if (r.Anim == null)
            {
                r.Anim = go.GetComponentInChildren<Animator>(true);
            }
            if (r.Anim != null)
            {
                r.Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                r.Anim.applyRootMotion = false;
            }
            if (r.FeetToRoot < 0f)
            {
                r.FeetToRoot = smr != null ? go.transform.position.y - smr.bounds.min.y : 0f;
            }
            if (!loggedParts)
            {
                loggedParts = true;
                foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true))
                {
                    parts.Add(rd.name + (rd.enabled && rd.gameObject.activeInHierarchy ? "" : " (off)"));
                }
                Plugin.Log.LogInfo($"KillNet: V1 body's parts: {string.Join(", ", parts)}");
            }
            Plugin.Log.LogInfo($"KillNet: V1 body made from V2 ({(r.Anim != null ? "animated" : "no animator")}, root {r.FeetToRoot:0.00} above its feet, "
                + $"{r.Weapons.Length} weapons, {r.Aim.Length} aiming arms)");
        }

        // What its V2 script had (its weapons, arms, effects) and where its feet are (the bottom of its
        // collider, which it stands on), before the scripts and colliders go.
        private static SkinnedMeshRenderer ReadV2(Remote r, GameObject go)
        {
            V2 v2 = go.GetComponentInChildren<V2>(true);
            SkinnedMeshRenderer smr = null;
            if (v2 != null)
            {
                smr = v2.smr;
                r.Weapons = v2.weapons ?? new GameObject[0];
                r.Aim = v2.aimAtTarget ?? new Transform[0];
                r.SlideEffect = v2.slideEffect;
                r.DodgeEffect = v2.dodgeEffect;
                r.JumpSound = v2.jumpSound;
                r.DashSound = v2.dashJumpSound;
                r.MuzzleFlash = new GameObject[r.Weapons.Length];
                r.ShootPoint = new Transform[r.Weapons.Length];
                for (int i = 0; i < r.Weapons.Length; i++)
                {
                    GameObject w = r.Weapons[i];
                    if (w == null || w.transform.childCount == 0)
                    {
                        continue;
                    }
                    Transform gun = w.transform.GetChild(0);
                    if (gun.TryGetComponent(out EnemyRevolver rev))
                    {
                        r.MuzzleFlash[i] = rev.muzzleFlash;
                        r.ShootPoint[i] = rev.shootPoint;
                    }
                    else if (gun.TryGetComponent(out EnemyShotgun sg))
                    {
                        r.MuzzleFlash[i] = sg.muzzleFlash;
                        r.ShootPoint[i] = sg.shootPoint;
                        r.ShotgunAnim = gun.GetComponentInChildren<Animator>(true);
                    }
                    else if (gun.TryGetComponent(out EnemyNailgun ng))
                    {
                        r.MuzzleFlash[i] = ng.muzzleFlash;
                        r.ShootPoint[i] = ng.shootPoint;
                    }
                }
            }
            if (smr == null)
            {
                smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            }
            CapsuleCollider capsule = go.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                float bottom = capsule.center.y - Mathf.Max(capsule.height * 0.5f, capsule.radius);
                r.FeetToRoot = -bottom * go.transform.lossyScale.y;
            }
            else
            {
                r.FeetToRoot = -1f;  // (from what's drawn, once it's on: MakeBody)
            }
            return smr;
        }

        // V2's body texture, its red made V1's blue (once: every other player's body shares it).
        private static Material Blue(Material red)
        {
            if (blueBody != null)
            {
                return blueBody;
            }
            blueBody = new Material(red) { name = "Killcraft V1 body" };
            try
            {
                Texture src = red.mainTexture;
                if (src != null)
                {
                    blueBody.mainTexture = Recolor(src);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't make V2's body blue ({e.Message}); other players are red");
            }
            return blueBody;
        }

        private static Texture2D Recolor(Texture src)
        {
            int w = src.width, h = src.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture was = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                Color.RGBToHSV(c, out float hue, out float sat, out float val);
                if (sat > 0.25f && (hue < 0.09f || hue > 0.9f))
                {
                    Color b = Color.HSVToRGB(0.58f, sat, val);
                    px[i] = new Color32((byte)(b.r * 255f), (byte)(b.g * 255f), (byte)(b.b * 255f), c.a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            tex.filterMode = src.filterMode;
            tex.wrapMode = src.wrapMode;
            tex.name = "Killcraft V1 body";
            return tex;
        }

        // Everything but its looks and animations.
        private static void StripAll(GameObject go)
        {
            foreach (Joint j in go.GetComponentsInChildren<Joint>(true))
            {
                UnityEngine.Object.DestroyImmediate(j);
            }
            // (Scripts, sounds, lights, its navmesh agent: every Behaviour but its Animators. Over again,
            // for any one that needed another first gone.)
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Behaviour c in go.GetComponentsInChildren<Behaviour>(true))
                {
                    if (c != null && !(c is Animator))
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                }
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                UnityEngine.Object.DestroyImmediate(c);
            }
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                UnityEngine.Object.DestroyImmediate(rb);
            }
        }

        // Where another player's V1 is shown (its head), while it's in this level: for their voice.
        // Spectating through their eyes: their V1 not drawn (its shadow still).
        public static int HiddenId = int.MinValue;

        public static bool LookOf(int id, out Vector3 look)
        {
            if (remotes.TryGetValue(id, out Remote r) && r.Placed && r.Body != null && Time.unscaledTime - r.At < 3f)
            {
                look = r.ShownLook.sqrMagnitude > 0.01f ? r.ShownLook.normalized : Vector3.forward;
                return true;
            }
            look = Vector3.forward;
            return false;
        }

        // A player as this game's enemies go after them (their marker), if they're here and alive.
        public static EnemyTarget TargetOf(int id)
        {
            return remotes.TryGetValue(id, out Remote r) && r.Target != null && r.Marker != null && r.Marker.activeSelf ? r.Target : null;
        }

        public static bool HeadOf(int id, out Vector3 at)
        {
            // (A Minos Prime round's Minos player: his head.)
            if (MinosMode.IsMinos(id) && MinosControl.HeadOf(out at))
            {
                return true;
            }
            if (remotes.TryGetValue(id, out Remote r) && r.Placed && r.Body != null && Time.unscaledTime - r.At < 3f)
            {
                at = r.Shown + Vector3.up * (1.6f * Coords.U);
                return true;
            }
            at = Vector3.zero;
            return false;
        }

        // Where a player is shown now (their feet), facing, looking, how fast, sliding or dashing,
        // their gun (its prefab's name): what a replay's view through their eyes goes by.
        public static bool StateOf(int id, out Vector3 feet, out float yaw, out Vector3 look, out Vector3 velocity, out bool sliding, out bool dashing, out string weapon)
        {
            if (remotes.TryGetValue(id, out Remote r) && r.Placed && r.Body != null && Time.unscaledTime - r.At < 3f)
            {
                feet = r.Shown;
                yaw = r.ShownYaw;
                look = r.ShownLook;
                velocity = r.ShownVelocity;
                sliding = (r.Flags & FlagSliding) != 0;
                dashing = (r.Flags & FlagDash) != 0;
                weapon = r.WeaponName ?? "";
                return true;
            }
            feet = velocity = Vector3.zero;
            look = Vector3.forward;
            yaw = 0f;
            sliding = dashing = false;
            weapon = "";
            return false;
        }

        private static void Hide(GameObject go, bool hide)
        {
            if (go == null)
            {
                return;
            }
            foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true))
            {
                if (rd is MeshRenderer || rd is SkinnedMeshRenderer)
                {
                    rd.shadowCastingMode = hide ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
                }
                else if (rd is LineRenderer || rd is TrailRenderer || rd is SpriteRenderer || rd is ParticleSystemRenderer)
                {
                    rd.enabled = !hide;
                }
            }
        }

        // ---- what they hold ----

        public const byte HeldMessage = 44;
        private static byte itemSent = 255;
        private static float itemSentAt = -100f;
        private static Light torchTemplate;
        private static float torchLookedAt = -100f;

        // This player's held thing (ULTRAKILL's item type; 0 nothing), said when it changes and now and then.
        private static void SendItem()
        {
            byte item = 0;
            try
            {
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null && fc.heldObject != null)
                {
                    item = (byte)fc.heldObject.itemType;
                }
            }
            catch
            {
            }
            if (item == itemSent && Time.unscaledTime - itemSentAt < 2f)
            {
                return;
            }
            itemSent = item;
            itemSentAt = Time.unscaledTime;
            KillNet.Send(HeldMessage, KillNet.ToRoom, w => w.Write(item));
        }

        public static void HandleItem(int from, BinaryReader r)
        {
            byte item = r.ReadByte();
            if (remotes.TryGetValue(from, out Remote remote))
            {
                remote.Item = item;
            }
        }

        // A torch in their hand lights the level round them here, as the torch does for the one holding it.
        private static void ItemLight(Remote r)
        {
            bool torch = r.Item == (byte)ItemType.Torch;
            if (!torch)
            {
                if (r.ItemLight != null)
                {
                    r.ItemLight.enabled = false;
                }
                return;
            }
            if (r.ItemLight == null)
            {
                var go = new GameObject("Killcraft torch light");
                go.transform.SetParent(r.Body.transform, false);
                go.transform.localPosition = new Vector3(0.6f, 1.6f, 0.6f);
                r.ItemLight = go.AddComponent<Light>();
                r.ItemLight.type = LightType.Point;
                r.ItemLight.color = new Color(1f, 0.6f, 0.3f);
                r.ItemLight.range = 22f;
                r.ItemLight.intensity = 2.5f;
                r.ItemLight.shadows = LightShadows.None;
                Light t = TorchLight();
                if (t != null)
                {
                    r.ItemLight.color = t.color;
                    r.ItemLight.range = t.range;
                    r.ItemLight.intensity = t.intensity;
                    r.ItemLight.renderMode = t.renderMode;
                }
            }
            r.ItemLight.enabled = true;
        }

        // The level's own torch's light (to light alike).
        private static Light TorchLight()
        {
            if (torchTemplate != null || Time.unscaledTime - torchLookedAt < 5f)
            {
                return torchTemplate;
            }
            torchLookedAt = Time.unscaledTime;
            foreach (ItemIdentifier it in Resources.FindObjectsOfTypeAll<ItemIdentifier>())
            {
                if (it != null && it.itemType == ItemType.Torch)
                {
                    Light l = it.GetComponentInChildren<Light>(true);
                    if (l != null)
                    {
                        torchTemplate = l;
                        break;
                    }
                }
            }
            return torchTemplate;
        }

        // ---- their guns heard ----

        private sealed class GunClips
        {
            public AudioClip[] Clips = new AudioClip[0];
            public UnityEngine.Audio.AudioMixerGroup Group;
        }
        private static readonly Dictionary<string, GunClips> gunClips = new Dictionary<string, GunClips>();

        // Their shot, heard where they are (their gun's own sound: this ULTRAKILL's same gun's).
        private static void GunSound(int id, Remote r)
        {
            string name = r.WeaponName;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            if (!gunClips.TryGetValue(name, out GunClips g) || g == null)
            {
                g = FindClips(name);
                if (g == null)
                {
                    return;
                }
                gunClips[name] = g;
            }
            if (g.Clips.Length == 0)
            {
                return;
            }
            AudioClip clip = g.Clips[UnityEngine.Random.Range(0, g.Clips.Length)];
            if (clip == null)
            {
                return;
            }
            var go = new GameObject("Killcraft gunshot");
            go.transform.position = r.Shown + Vector3.up * (1.4f * Coords.U);
            AudioSource a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.outputAudioMixerGroup = g.Group;
            a.spatialBlend = id == HiddenId ? 0f : 1f;
            a.minDistance = 8f;
            a.maxDistance = 200f;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.volume = 0.85f;
            a.pitch = Mathf.Max(0.05f, Time.timeScale);
            a.Play();
            UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.05f, a.pitch) + 0.2f);
        }

        // Its shot sounds: the fields called shot / shoot (not charging, not clicking).
        private static GunClips FindClips(string name)
        {
            GunControl gc = MonoSingleton<GunControl>.Instance;
            if (gc == null)
            {
                return null;
            }
            foreach (List<GameObject> slot in gc.slots)
            {
                foreach (GameObject go in slot)
                {
                    if (go == null || Shots.PrefabName(go) != name)
                    {
                        continue;
                    }
                    var g = new GunClips();
                    var clips = new List<AudioClip>();
                    AudioSource own = go.GetComponent<AudioSource>();
                    g.Group = own != null ? own.outputAudioMixerGroup : null;
                    foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null)
                        {
                            continue;
                        }
                        foreach (System.Reflection.FieldInfo f in mb.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        {
                            string fn = f.Name.ToLowerInvariant();
                            if (!(fn.Contains("shot") || fn.Contains("shoot") || fn.Contains("fire")) || fn.Contains("charg") || fn.Contains("click") || fn.Contains("fail"))
                            {
                                continue;
                            }
                            if (f.FieldType == typeof(AudioClip) && f.GetValue(mb) is AudioClip c && c != null)
                            {
                                clips.Add(c);
                            }
                            else if (f.FieldType == typeof(AudioClip[]) && f.GetValue(mb) is AudioClip[] cs)
                            {
                                foreach (AudioClip c2 in cs)
                                {
                                    if (c2 != null)
                                    {
                                        clips.Add(c2);
                                    }
                                }
                            }
                        }
                        if (clips.Count > 0)
                        {
                            break;
                        }
                    }
                    g.Clips = clips.ToArray();
                    return g;
                }
            }
            return new GunClips();
        }

        // ---- debug: raw against drawn, the animator's state; pretend lag (NetSim) ----

        private static bool debugOn;
        private static GUIStyle debugStyle;

        // F8 with Debug/Diagnostics on: the readout and the markers.
        private static void DebugKeys()
        {
            if (!Plugin.Diagnostics.Value)
            {
                return;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame)
            {
                debugOn = !debugOn;
                Plugin.Log.LogInfo($"KillNet: remote player debug {(debugOn ? "on" : "off")} (lag {Plugin.SimLatencyMs.Value} ms, loss {Plugin.SimLossPercent.Value}%)");
                if (!debugOn)
                {
                    foreach (Remote r in remotes.Values)
                    {
                        if (r.RawMarker != null)
                        {
                            r.RawMarker.SetActive(false);
                        }
                        if (r.ShownMarker != null)
                        {
                            r.ShownMarker.SetActive(false);
                        }
                    }
                }
            }
        }

        private static void DebugMark(Remote r)
        {
            if (r.RawMarker == null)
            {
                r.RawMarker = Marker(new Color(1f, 0.2f, 0.2f, 0.8f), 0.35f);
                r.ShownMarker = Marker(new Color(0.2f, 1f, 0.2f, 0.8f), 0.25f);
            }
            r.RawMarker.SetActive(true);
            r.ShownMarker.SetActive(true);
            r.RawMarker.transform.position = r.Feet + Vector3.up * 0.2f;
            r.ShownMarker.transform.position = r.Shown + Vector3.up * 0.2f;
        }

        private static GameObject Marker(Color c, float size)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Killcraft remote player debug marker";
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                UnityEngine.Object.Destroy(col);
            }
            go.transform.localScale = Vector3.one * size;
            Renderer rd = go.GetComponent<Renderer>();
            Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Diffuse");
            if (sh != null)
            {
                rd.sharedMaterial = new Material(sh);
            }
            rd.material.color = c;
            rd.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        // (Host.OnGUI.)
        public static void Gui()
        {
            if (!debugOn || remotes.Count == 0)
            {
                return;
            }
            debugStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false, alignment = TextAnchor.UpperLeft };
            var sb = new System.Text.StringBuilder();
            sb.Append($"remote players (F8)  sim lag {Plugin.SimLatencyMs.Value} ms  loss {Plugin.SimLossPercent.Value}%  held {NetSim.Waiting}\n");
            foreach (var kv in remotes)
            {
                Remote r = kv.Value;
                sb.Append($"#{kv.Key} ").Append(r.Body != null && r.Body.activeSelf ? r.Debug : "not drawn").Append('\n');
            }
            string text = sb.ToString();
            var rect = new Rect(12f, 90f, Screen.width - 24f, 400f);
            GUI.color = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, debugStyle);
            GUI.color = Color.white;
            GUI.Label(rect, text, debugStyle);
        }

        private static void Destroy(Remote r)
        {
            if (r.RawMarker != null)
            {
                UnityEngine.Object.Destroy(r.RawMarker);
                UnityEngine.Object.Destroy(r.ShownMarker);
                r.RawMarker = r.ShownMarker = null;
            }
            if (r.Body != null)
            {
                UnityEngine.Object.Destroy(r.Body);
            }
            if (r.Held != null)
            {
                UnityEngine.Object.Destroy(r.Held);
            }
            if (r.Marker != null)
            {
                UnityEngine.Object.Destroy(r.Marker);
            }
            r.Body = null;
            r.V1 = null;
            r.Held = null;
            r.Marker = null;
            r.Target = null;
            targets.Clear();
        }
    }
}
