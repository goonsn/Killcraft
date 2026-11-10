using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Killcraft
{
    // PvP between players in the same level (with the host's PvP setting on), fought with ULTRAKILL's
    // own weapons as they fight its enemies. Each other player here is, to this ULTRAKILL, an enemy
    // where they're drawn: an unseen one (their V1 body, or their Minecraft player, is what's seen),
    // a body and a head (headshots), so everything that finds or hits ULTRAKILL's enemies finds and
    // hits them: beams and the railcannon, shotgun pellets, the core and rockets going off on them,
    // nails, saws and magnets, coins snapping to their head, the chainsaw, punches, the whiplash
    // (pulling V1 to them). A hit on one isn't taken there: it goes to that player (KillNet), whose
    // ULTRAKILL takes it as V1 (or, in Minecraft mode, as its Minecraft player), about as ULTRAKILL's
    // own weapons hurt V1 (ten times an enemy's damage, so a revolver shot is a tenth of V1's
    // health), by the host's PvP damage; with caps, so nothing but the railcannon takes most of it
    // at once.
    //
    // Parries: V1 punching just as another player's punch, shot, saw, rocket or blast reaches it (or
    // a Minecraft player's sword or arrow, or a mob's: Combat) parries it, as ULTRAKILL's parries:
    // no damage, health back, the parry flash, and the hit goes back to whoever sent it. Beams and
    // coins can't be parried (as in ULTRAKILL).
    internal static class Pvp
    {
        public const byte HitMessage = 22, ParriedMessage = 23, BledMessage = 24, ShotParriedMessage = 25;
        private const byte KindBeam = 0, KindMelee = 1, KindShot = 2, KindBlast = 3, KindRail = 4;

        // ULTRAKILL's own: its weapons hurt V1 ten times what they do to an enemy (a reflected beam,
        // explosions: 35 to V1, 3.5 to an enemy).
        private const float ToV1 = 10f;
        private const float RailHit = 70f, BlastCap = 45f, HitCap = 55f, FrameCap = 80f;
        private const float ParryWindow = 0.3f;

        private sealed class Target
        {
            public int Player;
            public GameObject Root;
            public EnemyIdentifier Eid;
            public Transform Body, Head, Grab;
            public CapsuleCollider BodyShape;
            public SphereCollider HeadShape;
            public CapsuleCollider GrabShape;
            public SphereCollider Middle;
            public bool Seen;
        }

        // What this player's hits on one other player came to this frame (sent once, at its end).
        private sealed class Pending
        {
            public float Hp;     // shots, beams, punches: each counts
            public float Once;   // blasts, the railcannon: the biggest counts (one reaching head and body is one hit)
            public byte Kind;
            public float Biggest;
            public Vector3 Push, At;
        }

        // What ULTRAKILL calls its player's hits (EnemyIdentifier.hitter).
        private static readonly HashSet<string> Weapons = new HashSet<string>
        {
            "revolver", "coin", "lightningbolt", "railcannon", "shotgun", "shotgunzone", "nail", "sawblade",
            "chainsaw", "chainsawbounce", "chainsawzone", "chainsawprojectile", "explosion", "ffexplosion",
            "punch", "heavypunch", "hammer", "hammerzone", "drill", "drillpunch", "harpoon", "cannonball",
            "projectile", "ground slam", "zapper", "zap", "aftershock",
        };

        public static bool IsWeapon(string hitter) => Weapons.Contains(hitter);

        private static readonly HashSet<string> NotWeapons = new HashSet<string>
        {
            "enemy", "hook", "blocked", "fire", "deathzone", "environment", "acid", "secret", "maurice",
        };

        private static readonly Dictionary<int, Target> targets = new Dictionary<int, Target>();
        private static readonly HashSet<int> eids = new HashSet<int>();
        private static readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        private static readonly List<Vector3> places = new List<Vector3>();
        private static float punchedAt = -100f;
        private static bool loggedMade, loggedTaken;
        private static readonly HashSet<string> loggedHits = new HashSet<string>();

        public static bool On => Room.Shared && Room.Pvp && !MinosMode.Active;

        // One of the other players' targets (not one of the level's enemies).
        public static bool Is(EnemyIdentifier eid) => eid != null && eids.Count > 0 && eids.Contains(eid.GetInstanceID());

        // Where the players with targets stand (MobHits leaves their Minecraft boxes out: a hit there would count twice).
        public static List<Vector3> Places() => places;

        // ---- placed each frame (RemotePlayers) ----

        public static void Begin()
        {
            places.Clear();
            foreach (Target t in targets.Values)
            {
                t.Seen = false;
            }
        }

        // feet: where they're drawn; ultrakill: as V1 (else their Minecraft player); alive: in it now.
        public static void Place(int player, Vector3 feet, float yaw, bool ultrakill, bool sliding, bool alive)
        {
            if (!On || !alive)
            {
                return;
            }
            if (targets.TryGetValue(player, out Target dead) && dead.Root != null && (dead.Eid == null || dead.Eid.dead))
            {
                Remove(dead);  // (something killed it anyway: a pit's death zone)
                targets.Remove(player);
            }
            if (!targets.TryGetValue(player, out Target t) || t.Root == null)
            {
                t = Make(player);
                if (t == null)
                {
                    return;
                }
                targets[player] = t;
            }
            t.Seen = true;
            places.Add(feet);
            float u = Coords.U;
            // V1's drawn size (V1Body: a little over a Minecraft player), or a Minecraft player's.
            float tall = (ultrakill ? V1Body.TallBlocks : 1.8f) * u;
            float radius = (ultrakill ? 0.5f : 0.35f) * u;
            if (sliding)
            {
                tall *= 0.5f;
            }
            float head = Mathf.Min(0.3f * u, tall * 0.2f);
            t.BodyShape.radius = radius;
            t.BodyShape.height = Mathf.Max(tall - head * 1.2f, radius * 2f);
            t.Body.localPosition = new Vector3(0f, t.BodyShape.height * 0.5f, 0f);
            t.HeadShape.radius = head;
            t.Head.localPosition = new Vector3(0f, tall - head, 0f);
            t.GrabShape.radius = radius * 1.1f;
            t.GrabShape.height = tall;
            t.Grab.localPosition = new Vector3(0f, tall * 0.5f, 0f);
            if (t.Middle != null)
            {
                t.Middle.center = new Vector3(0f, tall * 0.5f, 0f);
            }
            t.Root.transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw, 0f));
            if (!t.Root.activeSelf)
            {
                t.Root.SetActive(true);
            }
        }

        public static void End()
        {
            List<int> gone = null;
            foreach (var kv in targets)
            {
                Target t = kv.Value;
                if (t.Root == null)
                {
                    (gone ??= new List<int>()).Add(kv.Key);
                    continue;
                }
                if (!t.Seen && t.Root.activeSelf)
                {
                    t.Root.SetActive(false);
                }
                if (!On)
                {
                    (gone ??= new List<int>()).Add(kv.Key);
                }
            }
            if (gone != null)
            {
                foreach (int id in gone)
                {
                    Remove(targets[id]);
                    targets.Remove(id);
                }
            }
        }

        public static void LevelChanged()
        {
            foreach (Target t in targets.Values)
            {
                Remove(t);
            }
            targets.Clear();
            eids.Clear();
            pending.Clear();
            places.Clear();
        }

        private static void Remove(Target t)
        {
            if (t.Eid != null)
            {
                eids.Remove(t.Eid.GetInstanceID());
                try
                {
                    (MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker)?.RemoveEnemy(t.Eid);
                }
                catch
                {
                }
            }
            if (t.Root != null)
            {
                UnityEngine.Object.Destroy(t.Root);
            }
        }

        // An enemy ULTRAKILL takes as one of its own (in its list of enemies, as coins, nails and
        // homing look there), a V2 to its weapons (heavy: the whiplash pulls V1 to it), never dying,
        // never in the bestiary or the kill count, harmless.
        private static Target Make(int player)
        {
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            if (nm == null)
            {
                return null;
            }
            var root = new GameObject($"Killcraft player {player} (PvP target)");
            root.SetActive(false);
            var t = new Target { Player = player, Root = root };
            try
            {
                EnemyIdentifier eid = root.AddComponent<EnemyIdentifier>();
                eid.enemyType = EnemyType.V2;
                eid.enemyClass = EnemyClass.Machine;
                eid.dontUnlockBestiary = true;
                eid.dontCountAsKills = true;
                eid.ignorePlayer = true;
                eid.flammables = new List<Flammable>();
                eid.burners = new List<Flammable>();
                eid.weaknesses = new string[0];
                eid.weaknessMultipliers = new float[0];
                eid.activateOnDeath = new GameObject[0];
                eid.health = 999f;
                t.Eid = eid;
                t.Body = Part(root.transform, eid, "Body", out GameObject body);
                t.BodyShape = body.AddComponent<CapsuleCollider>();
                t.Head = Part(root.transform, eid, "Head", out GameObject head);
                t.HeadShape = head.AddComponent<SphereCollider>();
                eid.weakPoint = head;
                // (ULTRAKILL's explosions, the core's and rockets', the overpumped shotgun's, the
                // malicious railcannon's, look for a collider on the enemy itself, to check nothing
                // stands between: a point in its middle, on a layer nothing shoots at. Without one
                // they passed the other players by.)
                root.layer = 2;
                t.Middle = root.AddComponent<SphereCollider>();
                t.Middle.isTrigger = true;
                t.Middle.radius = 0.05f;
                // What the whiplash catches (it looks for enemies on their own layer, as each of
                // ULTRAKILL's has a body for it: not what bullets hit).
                var grab = new GameObject("Grab") { layer = 12 };
                grab.transform.SetParent(root.transform, false);
                Rigidbody grabBody = grab.AddComponent<Rigidbody>();
                grabBody.isKinematic = true;
                grabBody.useGravity = false;
                t.GrabShape = grab.AddComponent<CapsuleCollider>();
                t.GrabShape.isTrigger = true;
                t.Grab = grab.transform;
                // (And in ULTRAKILL's targeting, where coins look.)
                PvpVisionTarget seen = root.AddComponent<PvpVisionTarget>();
                seen.Eid = eid;
                seen.Body = t.Body;
                seen.Head = t.Head;
                // (Not something V1 bumps into or stands on.)
                if (nm.playerCollider != null)
                {
                    Physics.IgnoreCollision(t.BodyShape, nm.playerCollider, true);
                    Physics.IgnoreCollision(t.HeadShape, nm.playerCollider, true);
                    Physics.IgnoreCollision(t.GrabShape, nm.playerCollider, true);
                }
                eids.Add(eid.GetInstanceID());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"PvP: couldn't make player {player}'s target ({e.Message}); ULTRAKILL's weapons won't hit them");
                UnityEngine.Object.Destroy(root);
                return null;
            }
            if (!loggedMade)
            {
                loggedMade = true;
                Plugin.Log.LogInfo($"PvP: other players are ULTRAKILL targets (player {player}'s made)");
            }
            return t;
        }

        private static Transform Part(Transform root, EnemyIdentifier eid, string tag, out GameObject go)
        {
            go = new GameObject(tag);
            go.layer = 10;  // ULTRAKILL's enemy limbs (what its weapons hit)
            try
            {
                go.tag = tag;
            }
            catch
            {
            }
            go.transform.SetParent(root, false);
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            go.AddComponent<EnemyIdentifierIdentifier>().eid = eid;
            return go.transform;
        }

        private static int PlayerOf(EnemyIdentifier eid)
        {
            foreach (Target t in targets.Values)
            {
                if (t.Eid == eid)
                {
                    return t.Player;
                }
            }
            return 0;
        }

        // ---- this player's hits on them (EnemyIdentifier.DeliverDamage, before ULTRAKILL takes it) ----

        public static void Hit(EnemyIdentifier eid, GameObject target, Vector3 force, Vector3 point, float multiplier, float crit, GameObject sourceWeapon, bool fromExplosion)
        {
            int player = PlayerOf(eid);
            string hitter = eid.hitter ?? "";
            eid.hitter = "";
            // Only this player's weapons (not the level's enemies' shots and blasts, its lava and pits,
            // fire: theirs hurt them in their own ULTRAKILL); the whiplash catching them only pulls.
            if (player == 0 || multiplier <= 0f || !(Weapons.Contains(hitter) || (sourceWeapon != null && !NotWeapons.Contains(hitter))))
            {
                return;
            }
            bool head = target != null && target.CompareTag("Head");
            float hp = multiplier * (1f + (head ? Mathf.Max(crit, 0f) : 0f)) * ToV1;
            byte kind;
            switch (hitter)
            {
                case "railcannon":
                    kind = KindRail;
                    hp = RailHit;
                    break;
                case "revolver":
                case "coin":
                case "lightningbolt":
                    kind = KindBeam;
                    hp = Mathf.Min(hp, HitCap);
                    break;
                case "punch":
                case "heavypunch":
                case "hammer":
                case "hammerzone":
                case "drillpunch":
                case "chainsaw":
                case "ground slam":
                    kind = fromExplosion ? KindBlast : KindMelee;
                    hp = Mathf.Min(hp, fromExplosion ? BlastCap : HitCap);
                    break;
                default:
                    kind = fromExplosion ? KindBlast : KindShot;
                    hp = Mathf.Min(hp, fromExplosion ? BlastCap : HitCap);
                    break;
            }
            if (!pending.TryGetValue(player, out Pending p))
            {
                pending[player] = p = new Pending();
            }
            if (kind == KindBlast || kind == KindRail)
            {
                p.Once = Mathf.Max(p.Once, hp);
            }
            else
            {
                p.Hp += hp;
            }
            if (hp > p.Biggest)
            {
                p.Biggest = hp;
                p.Kind = kind;
                p.At = kind == KindBlast ? point : MyEyes();
            }
            p.Push += force.sqrMagnitude > 1e-6f ? force.normalized * hp : Vector3.zero;
            DamageNumbers.Add(eid, point != Vector3.zero ? point : (target != null ? target.transform.position : eid.transform.position),
                hp * Room.PvpDamage, head, false);
            try
            {
                EnemySync.Bleed(eid, target, multiplier, head ? crit : 0f, fromExplosion);
            }
            catch
            {
            }
            if (loggedHits.Add(hitter + (fromExplosion ? "!" : "")))
            {
                Plugin.Log.LogInfo($"PvP: hit player {player} ({hitter}{(fromExplosion ? ", a blast" : "")}, {hp:0} damage)");
            }
        }

        private static Vector3 MyEyes()
        {
            var cc = MonoSingleton.GetInstance(typeof(CameraController)) as CameraController;
            if (cc != null)
            {
                return cc.transform.position;
            }
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            return nm != null ? nm.transform.position : Vector3.zero;
        }

        // The frame's hits, each player's in one (shotgun pellets, an explosion's limbs, at once).
        public static void Frame()
        {
            SendBlood();
            if (pending.Count == 0)
            {
                return;
            }
            if (On)
            {
                foreach (var kv in pending)
                {
                    Pending p = kv.Value;
                    float hp = Mathf.Min(p.Hp + p.Once, Mathf.Max(FrameCap, p.Once));
                    byte kind = p.Kind;
                    Vector3 at = p.At;
                    // A shove: blasts and punches the most.
                    float shove = kind == KindBlast ? 28f : kind == KindMelee ? 14f : kind == KindRail ? 10f : 3f;
                    Vector3 push = p.Push.sqrMagnitude > 1e-6f ? p.Push.normalized * shove : Vector3.zero;
                    KillNet.Send(HitMessage, KillNet.ToPlayer, w =>
                    {
                        w.Write(hp);
                        w.Write(kind);
                        Write(w, at);
                        Write(w, push);
                    }, kv.Key);
                }
            }
            pending.Clear();
        }

        // ---- the others' hits on this player ----

        public static void Handle(int from, byte type, BinaryReader r)
        {
            float hp = r.ReadSingle();
            byte kind = r.ReadByte();
            Vector3 at = Read(r), push = Read(r);
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            if (nm == null || nm.dead || !Room.Pvp || hp <= 0f)
            {
                return;
            }
            // (Their shots this player just punched out of the air: any that hit before they heard are void.)
            if ((kind == KindShot || kind == KindBlast) && type == HitMessage && immuneFrom.TryGetValue(from, out float until) && Time.unscaledTime < until)
            {
                return;
            }
            if (type == ParriedMessage)
            {
                // (How many parries deep: 1 a parry, 2 a parry of a parry, ...; older Killcraft sends none.)
                int chain = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : 1;
                if (chain >= 2)
                {
                    UltraParry.Boom(at, chain, false);
                }
                // This player's own hit, parried: parried back as it comes (an ultraparry), or it lands.
                if (Parrying(at, UltraParry.Window))
                {
                    ParryBack(nm, from, hp, push, chain + 1);
                    return;
                }
                Take(nm, hp, KindBeam, push, chain >= 2 ? $"player {from}'s parry of a parry" : "parried");
                return;
            }
            hp *= Room.PvpDamage;
            if (kind != KindBeam && kind != KindRail && Parrying(at))
            {
                nm.Parry(null, "");
                Plugin.Log.LogInfo($"PvP: parried player {from}'s hit ({hp:0} damage, sent back)");
                Vector3 back = -push;
                KillNet.Send(ParriedMessage, KillNet.ToPlayer, w =>
                {
                    w.Write(hp);
                    w.Write(KindBeam);
                    Write(w, MyEyes());
                    Write(w, back);
                    w.Write((byte)1);
                }, from);
                return;
            }
            Take(nm, hp, kind, push, $"player {from}", from);
        }

        private static void Take(NewMovement nm, float hp, byte kind, Vector3 push, string by, int hitBy = 0)
        {
            int damage = Mathf.Max(1, Mathf.RoundToInt(hp));
            Patches.PvpHit = true;
            HitBy = hitBy;
            try
            {
                nm.GetHurt(damage, false, 1f, kind == KindBlast);
            }
            finally
            {
                Patches.PvpHit = false;
                HitBy = 0;
            }
            if (!Patches.OwnsPlayer && nm.rb != null && push.sqrMagnitude > 0.01f)
            {
                // (Blasts lift too, as ULTRAKILL's own throw V1.)
                Vector3 v = push + (kind == KindBlast ? Vector3.up * push.magnitude * 0.5f : Vector3.zero);
                nm.rb.velocity += v;
            }
            if (!loggedTaken)
            {
                loggedTaken = true;
                Plugin.Log.LogInfo($"PvP: hit by {by} ({damage} damage)");
            }
        }

        // ---- blood ----
        //
        // A player hurt (as V1, by anything; as their Minecraft player, by anything in Minecraft) bleeds
        // as ULTRAKILL's enemies do: the others' ULTRAKILLs spill ULTRAKILL's own blood where they
        // are, which heals whoever stands in it there (V1, or, in Minecraft mode, Minecraft's player:
        // Patches.GetHealth). Not in their own ULTRAKILL: their own blood would heal them. The player
        // who hit them already spilled it, hitting (Hit).

        public static int HpBefore;
        public static int HitBy;
        private static float bledAt = -100f, bledAmount;
        private static Vector3 bledWhere;
        private static int bledBy;

        public static void Bleed(float amount, Vector3 at, int by)
        {
            if (!Room.Shared || amount <= 0f)
            {
                return;
            }
            bledAmount += amount;
            bledWhere = at;
            if (by != 0)
            {
                bledBy = by;
            }
        }

        // (Once a tenth of a second at most, what bled meanwhile: lava, a nailgun.)
        private static void SendBlood()
        {
            if (bledAmount <= 0f || Time.unscaledTime - bledAt < 0.1f)
            {
                return;
            }
            bledAt = Time.unscaledTime;
            float amount = bledAmount;
            Vector3 at = bledWhere;
            int by = bledBy;
            bledAmount = 0f;
            bledBy = 0;
            if (!Room.Shared)
            {
                return;
            }
            KillNet.Send(BledMessage, KillNet.ToRoom, w =>
            {
                w.Write(amount);
                Write(w, at);
                w.Write(by);
            });
        }

        public static void HandleBled(int from, BinaryReader r)
        {
            float amount = r.ReadSingle();
            Vector3 at = Read(r);
            int by = r.ReadInt32();
            if (by != 0 && by == Room.MyId)
            {
                return;  // (this player's hit: already spilled here)
            }
            EnemyIdentifier eid = null;
            if (targets.TryGetValue(from, out Target t) && t.Root != null && t.Root.activeInHierarchy)
            {
                at = t.Body.position;  // (where they're drawn here, now)
                eid = t.Eid;
            }
            try
            {
                Spill(at, amount, eid);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"PvP: couldn't spill player {from}'s blood ({e.Message})");
            }
        }

        private static void Spill(Vector3 at, float amount, EnemyIdentifier eid)
        {
            var bsm = MonoSingleton.GetInstance(typeof(BloodsplatterManager)) as BloodsplatterManager;
            if (bsm == null)
            {
                return;
            }
            int splashes = amount >= 30f ? 3 : amount >= 12f ? 2 : 1;
            for (int i = 0; i < splashes; i++)
            {
                GameObject gore = bsm.GetGore(amount >= 10f ? GoreType.Limb : GoreType.Small, false, false, false, eid, false);
                if (gore == null)
                {
                    continue;
                }
                gore.transform.position = at + UnityEngine.Random.insideUnitSphere * 0.4f;
                if (eid != null)
                {
                    GoreZone zone = eid.GetGoreZone();
                    if (zone != null && zone.goreZone != null)
                    {
                        gore.transform.SetParent(zone.goreZone, true);
                    }
                }
                Bloodsplatter splatter = gore.GetComponent<Bloodsplatter>();
                if (splatter != null)
                {
                    splatter.GetReady();
                }
            }
            if (!loggedBlood)
            {
                loggedBlood = true;
                Plugin.Log.LogInfo($"PvP: another player bled here ({amount:0} damage)");
            }
        }

        private static bool loggedBlood;

        // ---- parries ----

        private static readonly Dictionary<int, float> immuneFrom = new Dictionary<int, float>();
        private static readonly List<(int Owner, uint Id, string Name)> punchedOut = new List<(int, uint, string)>();

        // V1 just punched (its punch's hitbox on: Punch.ActiveStart): another player's shots within
        // its reach in front (as this player sees them coming) are parried, as ULTRAKILL's punch
        // parries its enemies' projectiles: gone, health back, and their damage back at the shooter.
        public static void Punched()
        {
            punchedAt = Time.unscaledTime;
            if (!On || Patches.OwnsPlayer)
            {
                return;
            }
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            var cc = MonoSingleton.GetInstance(typeof(CameraController)) as CameraController;
            if (nm == null || nm.dead || cc == null)
            {
                return;
            }
            punchedOut.Clear();
            // (A little further than the punch reaches: what's seen of their shots is a moment behind.)
            Shots.TakeNear(cc.transform.position, cc.transform.forward, 4f * Coords.U, punchedOut);
            if (punchedOut.Count == 0)
            {
                return;
            }
            nm.Parry(null, "");
            var byOwner = new Dictionary<int, (List<uint> Ids, float Back)>();
            foreach (var (owner, id, name) in punchedOut)
            {
                if (!byOwner.TryGetValue(owner, out var o))
                {
                    o = (new List<uint>(), 0f);
                }
                o.Ids.Add(id);
                o.Back += BackDamage(name);
                byOwner[owner] = o;
            }
            foreach (var kv in byOwner)
            {
                int owner = kv.Key;
                List<uint> ids = kv.Value.Ids;
                float back = Mathf.Min(kv.Value.Back, 60f) * Room.PvpDamage;
                Vector3 eyes = cc.transform.position, dir = cc.transform.forward * 12f;
                immuneFrom[owner] = Time.unscaledTime + 0.6f;
                KillNet.Send(ShotParriedMessage, KillNet.ToPlayer, w =>
                {
                    w.Write(back);
                    Write(w, eyes);
                    Write(w, dir);
                    w.Write(ids.Count);
                    foreach (uint id in ids)
                    {
                        w.Write(id);
                    }
                }, owner);
                Plugin.Log.LogInfo($"PvP: punched {ids.Count} of player {owner}'s shots out of the air ({back:0} damage back)");
            }
        }

        // What a parried shot does to the one who fired it (as ULTRAKILL's parried projectiles hit hard).
        private static float BackDamage(string name)
        {
            if (name.IndexOf("Rocket", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 35f;
            }
            if (name.IndexOf("Cannonball", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 30f;
            }
            if (name.IndexOf("Harpoon", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Drill", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 20f;
            }
            if (name.StartsWith("NailAlt", StringComparison.Ordinal))
            {
                return 12f;
            }
            if (name.StartsWith("Nail", StringComparison.Ordinal))
            {
                return 3f;
            }
            if (name.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 4f;
            }
            return 10f;
        }

        // Another player punched this player's shots out of the air: they're gone here too, and
        // their damage comes back.
        public static void HandleShotParried(int from, BinaryReader r)
        {
            float back = r.ReadSingle();
            Vector3 at = Read(r), push = Read(r);
            int n = r.ReadInt32();
            int gone = 0;
            for (int i = 0; i < n && i < 400; i++)
            {
                if (Shots.Cancel(r.ReadUInt32()))
                {
                    gone++;
                }
            }
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            Plugin.Log.LogInfo($"PvP: player {from} parried {n} of this player's shots ({gone} still flying here, {back:0} damage back)");
            // (Punched back as it comes: a parry of their parry.)
            if (nm != null && !nm.dead && Room.Pvp && back > 0f && Parrying(at, UltraParry.Window))
            {
                ParryBack(nm, from, back, push, 2);
                return;
            }
            if (nm != null && !nm.dead && Room.Pvp && back > 0f)
            {
                Take(nm, back, KindBeam, push, $"player {from}'s parry", from);
            }
        }

        // A parried hit coming back, punched again: an ultraparry. A big blast where V1 punched it,
        // a heap of style, and the hit back at the one who parried it, harder each time round.
        private static void ParryBack(NewMovement nm, int from, float hp, Vector3 push, int chain)
        {
            nm.Parry(null, "");
            Vector3 eyes = MyEyes();
            var cc = MonoSingleton.GetInstance(typeof(CameraController)) as CameraController;
            Vector3 blastAt = cc != null ? cc.transform.position + cc.transform.forward * (2.5f * Coords.U) : eyes;
            UltraParry.Boom(blastAt, chain, true);
            float harder = Mathf.Min(hp * 1.5f, 250f);
            Vector3 back = -push * 1.25f;
            byte depth = (byte)Mathf.Min(chain, 255);
            KillNet.Send(ParriedMessage, KillNet.ToPlayer, w =>
            {
                w.Write(harder);
                w.Write(KindBeam);
                Write(w, blastAt);
                Write(w, back);
                w.Write(depth);
            }, from);
            Plugin.Log.LogInfo($"PvP: ULTRAPARRY x{chain - 1}: parried player {from}'s parry ({harder:0} damage back)");
            ChatLines.Add($"ULTRAPARRY x{chain - 1}! {Party.NameOf(from)}'s parry, parried back");
        }

        // A hit from there (where it came from) lands as V1 punches towards it: parried.
        public static bool Parrying(Vector3 from) => Parrying(from, ParryWindow);

        public static bool Parrying(Vector3 from, float window)
        {
            if (Time.unscaledTime - punchedAt > window || Patches.OwnsPlayer)
            {
                return false;
            }
            var cc = MonoSingleton.GetInstance(typeof(CameraController)) as CameraController;
            if (cc == null)
            {
                return false;
            }
            Vector3 to = from - cc.transform.position;
            return to.sqrMagnitude < 0.01f || Vector3.Dot(cc.transform.forward, to.normalized) > 0.25f;
        }

        // A Minecraft player's or mob's hit (amount: Minecraft's), parried: back at the one who sent it.
        public static void ParriedMinecraft(NewMovement nm, uint attacker, float amount, Vector3 from)
        {
            nm.Parry(null, "");
            Destruction.Damage(attacker, amount * 2f, from - nm.transform.position);
            Plugin.Log.LogInfo($"PvP: parried a Minecraft hit ({amount:0.0}, sent back to {attacker})");
        }

        private static void Write(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 Read(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
