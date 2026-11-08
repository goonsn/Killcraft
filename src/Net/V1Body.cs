using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace Killcraft
{
    // Another player's V1, as ULTRAKILL itself would draw one: ULTRAKILL's own third-person V1 (the
    // v1_combined body every level's player carries, animated by its PlayerAnimations, unseen from
    // V1's own eyes), copied with its looks, its animations and its own models of the five weapons,
    // and moved as PlayerAnimations moves this player's, from what the other player sends: running
    // (its walk blend by speed and direction), sliding (turned to the slide), dodging, in the air,
    // landing, chest and head turned where they look, gun arm aimed there, the weapon they have out
    // (alternate ones too); and what ULTRAKILL tells PlayerAnimations when it happens (KillNet's
    // Moves, sent from this player's own): each weapon's shot and its kick back (the Shoot
    // animation, at that weapon's speed), punches, coin tosses, jumps.
    internal sealed class V1Body
    {
        public const byte Moves = 3;
        internal const byte MoveShoot = 0, MovePunch = 1, MoveCoin = 2, MoveJump = 3, MoveLand = 4;

        private static readonly AccessTools.FieldRef<PlayerAnimations, Transform> chestOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("chest");
        private static readonly AccessTools.FieldRef<PlayerAnimations, Transform> headOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("head");
        private static readonly AccessTools.FieldRef<PlayerAnimations, Transform> rightArmOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("rightArm");
        private static readonly AccessTools.FieldRef<PlayerAnimations, Transform> leftArmOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("leftArm");
        internal static readonly AccessTools.FieldRef<PlayerAnimations, GameObject[]> weaponsOf = AccessTools.FieldRefAccess<PlayerAnimations, GameObject[]>("weapons");
        internal static readonly AccessTools.FieldRef<PlayerAnimations, GameObject[]> altWeaponsOf = AccessTools.FieldRefAccess<PlayerAnimations, GameObject[]>("altweapons");

        private static bool loggedParts, noneHere;

        public GameObject Root;
        private Transform model, chest, head, rightArm, leftArm;
        private Quaternion restRotation;
        private Animator anim;
        private GameObject[] weapons, altWeapons;
        private float directionLerp, aimerWeight = 1f, aimerSpeed = 1f, leftArmWeight;
        private int shownWeapon = -2;
        private bool shownAlt;
        // Whether they send their moves (a Killcraft whose player has a PlayerAnimations): if not, a
        // shot (Shots) is their Shoot.
        public bool HeardMoves;
        // Alive above the legs (V1Motion), from what they're doing.
        private readonly V1Motion motion = new V1Motion();
        private Vector3 velocity;
        private bool onGround, still, slamming, hooked;
        private float dt = 1f / 60f;
        // Its guns' parts (painted, coloured by variant: Skins) and its body's colour.
        private List<Skins.Part> parts = new List<Skins.Part>();
        private readonly Skins.Body skin = new Skins.Body();
        private string looked = "";

        // ---- made from this player's ----

        public static V1Body Make()
        {
            if (noneHere)
            {
                return null;
            }
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            var mine = MonoSingleton.GetInstance(typeof(PlayerAnimations)) as PlayerAnimations;
            if (nm == null || mine == null)
            {
                noneHere = mine == null && nm != null;
                if (noneHere)
                {
                    Plugin.Log.LogInfo("KillNet: this level's player has no third-person V1 body; other players show as V2's");
                }
                return null;
            }
            var b = new V1Body();
            try
            {
                b.Build(nm, mine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't copy V1's body ({e.Message}); other players show as V2's");
                if (b.Root != null)
                {
                    UnityEngine.Object.Destroy(b.Root);
                }
                noneHere = true;
                return null;
            }
            return b;
        }

        private float sizeUp = 1f;

        // How tall another player's V1 is drawn, in blocks (a Minecraft player is 1.8: V1 a head
        // taller, as big as it is beside ULTRAKILL's enemies). Its PvP target is as tall (Pvp).
        public const float TallBlocks = 2.3f;

        private void Build(NewMovement nm, PlayerAnimations mine)
        {
            Transform source = mine.transform;
            // Where it is on V1 (it's under V1's ground check), kept on the copy.
            Vector3 offset = nm.transform.InverseTransformPoint(source.position);
            restRotation = Quaternion.Inverse(nm.transform.rotation) * source.rotation;
            Vector3 scale = source.lossyScale;

            GameObject copy = Shots.MakeOff(source.gameObject, Vector3.zero, Quaternion.identity);
            copy.name = "v1_combined (Killcraft other player)";
            PlayerAnimations theirs = copy.GetComponent<PlayerAnimations>();
            chest = chestOf(theirs);
            head = headOf(theirs);
            rightArm = rightArmOf(theirs);
            leftArm = leftArmOf(theirs);
            weapons = weaponsOf(theirs) ?? new GameObject[0];
            altWeapons = altWeaponsOf(theirs) ?? new GameObject[0];
            if (chest == null || head == null || rightArm == null || leftArm == null || rightArm.childCount == 0)
            {
                UnityEngine.Object.Destroy(copy);
                throw new Exception("its PlayerAnimations isn't as expected");
            }
            this.parts = Skins.Collect(copy);
            Strip(copy);
            guns = new VariantGuns(weapons, altWeapons);
            // Seen (this player's own is drawn only as its shadow, or not at all), by every camera.
            var parts = new List<string>();
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = 0;
            }
            foreach (Renderer rd in copy.GetComponentsInChildren<Renderer>(true))
            {
                rd.enabled = true;
                rd.shadowCastingMode = ShadowCastingMode.On;
                rd.forceRenderingOff = false;
                if (rd is SkinnedMeshRenderer smr)
                {
                    smr.updateWhenOffscreen = true;
                    smr.gameObject.SetActive(true);
                }
                parts.Add(rd.name);
            }
            foreach (GameObject w in weapons)
            {
                if (w != null)
                {
                    w.SetActive(false);
                }
            }
            foreach (GameObject w in altWeapons)
            {
                if (w != null)
                {
                    w.SetActive(false);
                }
            }

            Root = new GameObject("Killcraft other player");
            model = copy.transform;
            model.SetParent(Root.transform, false);
            model.localPosition = offset;
            model.localRotation = restRotation;
            model.localScale = scale;
            copy.SetActive(true);
            // As tall as a Minecraft player (beside them, and Minecraft's mobs and blocks, V1's own size
            // looked tiny): scaled up from its feet (the model's middle is at them).
            float top = float.MinValue;
            foreach (Renderer rd in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (rd is MeshRenderer || rd is SkinnedMeshRenderer)
                {
                    top = Mathf.Max(top, rd.bounds.max.y);
                }
            }
            float tall = top - model.position.y;
            if (tall > 0.5f && tall < 20f)
            {
                sizeUp = Mathf.Clamp(TallBlocks * Coords.U / tall, 1f, 3.5f);
                model.localScale = scale * sizeUp;
            }
            anim = copy.GetComponent<Animator>();
            if (anim != null)
            {
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.applyRootMotion = false;
                // (Their time, not this player's: they go on at full speed through this V1's slow
                // motion, as their position does.)
                anim.updateMode = AnimatorUpdateMode.UnscaledTime;
            }
            if (!loggedParts)
            {
                loggedParts = true;
                Plugin.Log.LogInfo($"KillNet: other players are V1's own third-person body ({weapons.Length} weapons, {altWeapons.Length} alternate; "
                    + $"{(anim != null ? "animated" : "no animator")}, {offset.y:0.00} from V1's middle, {sizeUp:0.00}x V1's size: a Minecraft player's height): {string.Join(", ", parts)}");
            }
        }

        // Everything but its looks and animations (its PlayerAnimations would follow this player).
        internal static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Behaviour c in go.GetComponentsInChildren<Behaviour>(true))
                {
                    // (A canvas only once what needs it, its scaler and raycaster, has gone.)
                    if (c != null && !(c is Animator) && !(pass == 0 && c is Canvas))
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

        // ---- each frame (as PlayerAnimations.LateUpdate, from what they sent) ----

        // Before the animations: what it's doing (as of the moment of their timeline that's shown:
        // RemotePlayers samples it all at one time, so the body's pose and its place agree). Root is
        // at their V1's middle, turned their way. dt: this frame, unscaled (the animator runs unscaled).
        public void Pose(Vector3 velocity, bool ground, bool sliding, bool dodging, bool slam, bool hook, byte weapon, bool alt, float dt)
        {
            if (anim == null)
            {
                return;
            }
            this.dt = dt = Mathf.Clamp(dt, 1e-4f, 0.1f);
            ShowWeapon(weapon, alt);
            this.velocity = velocity;
            onGround = ground;
            still = sliding || dodging;
            slamming = slam && !ground;
            hooked = hook;
            anim.SetBool("Dodging", dodging);
            anim.SetBool("InAir", !ground);
            anim.SetBool("Sliding", sliding);
            float land = anim.layerCount > 1 ? anim.GetLayerWeight(1) : 0f;
            if (land > 0f)
            {
                anim.SetLayerWeight(1, Mathf.MoveTowards(land, 0f, dt * 3f));
            }
            if (leftArmWeight > 0f)
            {
                leftArmWeight = Mathf.MoveTowards(leftArmWeight, 0f, dt * 3f);
                if (anim.layerCount > 3)
                {
                    anim.SetLayerWeight(3, Mathf.Min(leftArmWeight, 1f));
                }
            }
            directionLerp = Mathf.MoveTowards(directionLerp, sliding ? 1 : 0, dt * 10f);
            Quaternion rest = Root.transform.rotation * restRotation;
            model.rotation = directionLerp > 0f && velocity.sqrMagnitude > 0.01f
                ? Quaternion.Lerp(rest, Quaternion.LookRotation(velocity.normalized), directionLerp)
                : rest;
            // The run blend as PlayerAnimations sets it (its full stride at 15 a second), damped a
            // little against the 20-a-second steps in what they say; a dash's direction at once.
            Vector3 local = Quaternion.Inverse(Root.transform.rotation) * (velocity / RemoteTuning.RunClipSpeed);
            if (dodging)
            {
                Vector3 dodge = new Vector3(local.x, 0f, local.z).normalized;
                anim.SetFloat("VelX", dodge.x);
                anim.SetFloat("VelY", dodge.z);
            }
            else
            {
                anim.SetFloat("VelX", local.x, RemoteTuning.VelocityDamp, dt);
                anim.SetFloat("VelY", local.z, RemoteTuning.VelocityDamp, dt);
            }
        }

        // After the animations: chest, head and gun arm where they look.
        public void Late(Vector3 look)
        {
            if (anim == null)
            {
                return;
            }
            Quaternion camera = Quaternion.LookRotation(look, Vector3.up);
            Quaternion cameraLocal = Quaternion.Inverse(Root.transform.rotation) * camera;
            Quaternion chestWas = chest.rotation, headWas = head.rotation;
            if (directionLerp > 0f)
            {
                Quaternion leftWas = leftArm.rotation;
                chest.localRotation *= cameraLocal;
                chest.rotation = Quaternion.RotateTowards(chest.rotation, Quaternion.Inverse(model.localRotation), 45f);
                leftArm.rotation = leftWas;
            }
            else
            {
                chest.localRotation *= cameraLocal;
            }
            head.rotation = camera;
            if (aimerWeight < 1f)
            {
                aimerWeight = Mathf.MoveTowards(aimerWeight, 1f, dt * (5f * aimerSpeed));
            }
            if (shownWeapon < 2)
            {
                Aim(rightArm, camera);
            }
            Aim(rightArm.GetChild(0), camera);
            if (leftArmWeight > 0f)
            {
                chest.rotation = Quaternion.Lerp(chest.rotation, chestWas, leftArmWeight);
                head.rotation = Quaternion.Lerp(head.rotation, headWas, leftArmWeight);
            }
            motion.Run(model, Root.transform, velocity, onGround, still, chest, head, rightArm, leftArm, leftArmWeight <= 0.01f, anim,
                dt, slamming, hooked, look, sizeUp);
        }

        // For the debug readout (RemotePlayers): the animator's state and what drives it.
        public string Describe()
        {
            if (anim == null)
            {
                return "no animator";
            }
            string state = "?";
            AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
            foreach (string name in new[] { "Grounded", "Slide", "Jump", "InAir", "Dodging", "Intro", "KnockedDown", "Flailing" })
            {
                if (info.IsName(name))
                {
                    state = name;
                    break;
                }
            }
            string arm = "?";
            if (anim.layerCount > 2)
            {
                AnimatorStateInfo a = anim.GetCurrentAnimatorStateInfo(2);
                foreach (string name in new[] { "WeaponHoldSmall", "WeaponHoldBig", "WeaponShootBig", "WeaponShootSmall", "WeaponChangeBig", "WeaponChangeSmall" })
                {
                    if (a.IsName(name))
                    {
                        arm = name;
                        break;
                    }
                }
            }
            return $"{state}{(anim.IsInTransition(0) ? "~" : "")} vel {anim.GetFloat("VelX"):0.00},{anim.GetFloat("VelY"):0.00} "
                + $"air {(anim.GetBool("InAir") ? 1 : 0)} slide {(anim.GetBool("Sliding") ? 1 : 0)} dodge {(anim.GetBool("Dodging") ? 1 : 0)} "
                + $"slam {(slamming ? 1 : 0)} hook {(hooked ? 1 : 0)} land {(anim.layerCount > 1 ? anim.GetLayerWeight(1) : 0f):0.00} "
                + $"legs x{anim.speed:0.00} arm {arm} weapon {shownWeapon}{(shownAlt ? "alt" : "")}";
        }

        private void Aim(Transform aimer, Quaternion camera)
        {
            Quaternion was = aimer.rotation;
            aimer.rotation = camera;
            aimer.Rotate(Vector3.right, 90f, Space.Self);
            aimer.Rotate(Vector3.up, 180f, Space.Self);
            if (aimerWeight < 1f)
            {
                aimer.rotation = Quaternion.Lerp(was, aimer.rotation, aimerWeight);
            }
        }

        private void ShowWeapon(byte weapon, bool alt)
        {
            int num = weapon == RemotePlayers.WeaponNone ? -1 : weapon;
            if (num == shownWeapon && alt == shownAlt)
            {
                return;
            }
            bool first = shownWeapon == -2;
            shownWeapon = num;
            shownAlt = alt;
            for (int i = 0; i < weapons.Length; i++)
            {
                if (weapons[i] != null)
                {
                    weapons[i].SetActive(i == num && !alt);
                }
                if (i < altWeapons.Length && altWeapons[i] != null)
                {
                    altWeapons[i].SetActive(i == num && alt);
                }
            }
            anim.SetBool("BigWeapon", num >= 2);
            if (!first)
            {
                anim.SetTrigger("ChangeWeapon");
                aimerWeight = 0f;
            }
        }

        // As they look: their V1's colour, their gun's paint and variant.
        public void Look(Profiles.Info who, byte weapon, bool alt, int variant)
        {
            if (who == null || looked == "failed")
            {
                return;
            }
            try
            {
                if (guns != null && guns.Show(weapon, alt, variant))
                {
                    parts.AddRange(guns.Made);
                    looked = "";
                }
                Skins.SkinBody(Root, who, skin);
                string key = $"{weapon}|{alt}|{variant}|{who.PaintVersion}";
                if (key != looked && weapon != RemotePlayers.WeaponNone)
                {
                    looked = key;
                    Skins.Paint(parts, who, weapon, alt, variant);
                }
            }
            catch (Exception e)
            {
                looked = "failed";
                Plugin.Log.LogWarning($"KillNet: couldn't colour another player's V1 ({e.Message})");
            }
        }

        private VariantGuns guns;

        // What ULTRAKILL told their PlayerAnimations (heard: from them; else made up from their state).
        public void Move(byte move, float value, bool heard = true)
        {
            HeardMoves |= heard;
            if (anim == null)
            {
                return;
            }
            switch (move)
            {
                case MoveShoot:
                    Shoot(value);
                    break;
                case MovePunch:
                    anim.SetTrigger("Punch");
                    anim.SetFloat("PunchSpeed", value);
                    leftArmWeight = 2f / Mathf.Max(value, 0.1f);
                    motion.Kick(4f, 16f);
                    break;
                case MoveCoin:
                    anim.SetTrigger("CoinToss");
                    leftArmWeight = 1f;
                    break;
                case MoveJump:
                    anim.SetTrigger("Jump");
                    break;
                case MoveLand:
                    if (anim.layerCount > 1)
                    {
                        anim.SetLayerWeight(1, Mathf.Clamp01(value));
                    }
                    break;
            }
        }

        public void Shoot(float speed)
        {
            anim.SetTrigger("Shoot");
            anim.SetFloat("ShootSpeed", speed);
            aimerWeight = 0f;
            aimerSpeed = speed;
            motion.Kick(shownWeapon >= 3 ? 12f : shownWeapon == 1 ? 9f : 5f, 0f);
        }

        public static void LevelChanged() => noneHere = false;

        // ---- this player's moves, sent ----

        public static void Install(Harmony harmony)
        {
            Hook(harmony, nameof(PlayerAnimations.Shoot), nameof(AfterShoot));
            Hook(harmony, nameof(PlayerAnimations.Punch), nameof(AfterPunch));
            Hook(harmony, nameof(PlayerAnimations.CoinToss), nameof(AfterCoin));
            Hook(harmony, nameof(PlayerAnimations.Jump), nameof(AfterJump));
            Hook(harmony, nameof(PlayerAnimations.Land), nameof(AfterLand));
        }

        private static void Hook(Harmony harmony, string method, string postfix)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(PlayerAnimations), method), postfix: new HarmonyMethod(typeof(V1Body), postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's PlayerAnimations.{method} ({e.Message}); other players won't see it");
            }
        }

        private static void AfterShoot(float speed) => Send(MoveShoot, speed);

        private static void AfterPunch(float speed) => Send(MovePunch, speed);

        private static void AfterCoin() => Send(MoveCoin, 0f);

        private static void AfterJump() => Send(MoveJump, 0f);

        private static void AfterLand(float force) => Send(MoveLand, force);

        private static void Send(byte move, float value)
        {
            if (!Room.Sharing)
            {
                return;
            }
            KillNet.Send(Moves, KillNet.ToRoom, w =>
            {
                w.Write(move);
                w.Write(value);
                // (Newer: when, by this player's clock, as PlayerState says it: played on the others'
                // screens at that moment of this player's timeline, with the body where it was then.)
                w.Write(Time.unscaledTime);
            });
        }

        // (at: when, by their clock; NaN from an older Killcraft.)
        public static void Read(BinaryReader r, out byte move, out float value, out float at)
        {
            move = r.ReadByte();
            value = r.ReadSingle();
            at = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadSingle() : float.NaN;
        }
    }

    // Each variant's own gun on a third-person V1 (another player's, or this player's own in third
    // person). The third-person V1 has one model a weapon (variants only coloured): the gun out is
    // drawn as its variant's own first-person model instead (the Sharpshooter's, the Sawed-On's saw,
    // the Screwdriver's harpoon, ...): ULTRAKILL's prefab (GunSetter's), the gun's parts only (not V1's
    // arm), still, put on the third-person gun by a bone both rigs share.
    internal sealed class VariantGuns
    {
        private readonly GameObject[] weapons, altWeapons;
        private readonly Renderer[][] baseParts = new Renderer[10][];
        private readonly Dictionary<string, GameObject> models = new Dictionary<string, GameObject>();
        private string key = "";
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static readonly HashSet<string> logged = new HashSet<string>();

        // A model just made, checked once it's had a moment in the hand: still there, and the size of a gun.
        private GameObject checking;
        private Transform checkBone;
        private float checkAt;
        private static readonly HashSet<string> ExtraParts = new HashSet<string> { "Chainsaw", "Chainsaw_Blade", "Spear", "Blade (1)" };

        // The parts of the model made last (painted by the owner).
        public List<Skins.Part> Made = new List<Skins.Part>();

        public VariantGuns(GameObject[] weapons, GameObject[] altWeapons)
        {
            this.weapons = weapons ?? new GameObject[0];
            this.altWeapons = altWeapons ?? new GameObject[0];
            for (int i = 0; i < 5; i++)
            {
                baseParts[i] = i < this.weapons.Length && this.weapons[i] != null ? this.weapons[i].GetComponentsInChildren<Renderer>(true) : new Renderer[0];
                baseParts[i + 5] = i < this.altWeapons.Length && this.altWeapons[i] != null ? this.altWeapons[i].GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            }
        }

        // The gun out (weapon 0-4, 255 none), its variant (-1 none): true when a model was just made.
        public bool Show(byte weapon, bool alt, int variant)
        {
            string want = weapon > 4 || variant < 0 || variant > 2 || !Plugin.VariantGuns.Value ? "" : $"{weapon}|{alt}|{variant}";
            if (want == key)
            {
                if (checking != null && Time.unscaledTime > checkAt)
                {
                    Check();
                }
                return false;
            }
            checking = null;
            key = want;
            foreach (GameObject m in models.Values)
            {
                if (m != null)
                {
                    m.SetActive(false);
                }
            }
            foreach (Renderer[] list in baseParts)
            {
                foreach (Renderer r in list)
                {
                    if (r != null)
                    {
                        r.forceRenderingOff = false;
                    }
                }
            }
            if (want.Length == 0)
            {
                return false;
            }
            GameObject[] set = alt ? altWeapons : weapons;
            GameObject holder = weapon < set.Length ? set[weapon] : null;
            if (holder == null)
            {
                return false;
            }
            bool made = false;
            if (!models.TryGetValue(want, out GameObject model))
            {
                model = failed.Contains(want) ? null : Make(weapon, alt, variant, holder, want);
                models[want] = model;
                made = model != null;
                if (made)
                {
                    checking = model;
                    checkBone = model.transform.parent;
                    checkAt = Time.unscaledTime + 0.4f;
                }
            }
            if (model == null)
            {
                return false;
            }
            model.SetActive(true);
            foreach (Renderer r in baseParts[weapon + (alt ? 5 : 0)])
            {
                if (r != null)
                {
                    r.forceRenderingOff = true;
                }
            }
            return made;
        }

        // Off in the distance, out of size, or switched off while the hand's gun is out: the plain
        // third-person gun instead (the warning says which).
        private void Check()
        {
            GameObject model = checking;
            checking = null;
            if (model == null || checkBone == null || !checkBone.gameObject.activeInHierarchy)
            {
                return;
            }
            string wrong = null;
            if (!model.activeInHierarchy)
            {
                wrong = "switched off";
            }
            else
            {
                bool any = false;
                Bounds b = default;
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled)
                    {
                        continue;
                    }
                    if (!any)
                    {
                        b = r.bounds;
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(r.bounds);
                    }
                }
                float far = any ? (b.center - checkBone.position).magnitude : 0f;
                float size = any ? b.size.magnitude : 0f;
                if (!any)
                {
                    wrong = "nothing of it drawn";
                }
                else if (far > 4f || size > 12f || size < 0.05f)
                {
                    wrong = $"{far:0.0} from the hand, {size:0.00} across";
                }
                else
                {
                    Plugin.Log.LogInfo($"variant guns: {model.name} in the hand ({far:0.00} from it, {size:0.00} across): {Detail(model)}");
                }
            }
            if (wrong == null)
            {
                return;
            }
            Plugin.Log.LogWarning($"variant guns: {model.name} came out wrong ({wrong}); the plain gun instead");
            string was = key;
            failed.Add(was);
            models[was] = null;
            UnityEngine.Object.Destroy(model);
            key = "";
            string[] k = was.Split('|');
            Show(byte.Parse(k[0]), bool.Parse(k[1]), int.Parse(k[2]));
        }

        // Each part of a model as it's drawn (for the log: what's unseen or black, and why).
        private static string Detail(GameObject model)
        {
            var sb = new System.Text.StringBuilder();
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled)
                {
                    continue;
                }
                r.GetPropertyBlock(block);
                sb.Append(r.name).Append('[')
                    .Append(r.gameObject.activeInHierarchy ? "" : "inactive,")
                    .Append(r.isVisible ? "seen" : "unseen").Append(",L").Append(r.gameObject.layer)
                    .Append(',').Append(r.bounds.size.magnitude.ToString("0.00"));
                if (r is SkinnedMeshRenderer smr)
                {
                    sb.Append(",bones ").Append(smr.bones != null ? smr.bones.Length : 0).Append(smr.sharedMesh != null ? "" : ",no mesh");
                }
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null)
                    {
                        sb.Append(",null");
                        continue;
                    }
                    sb.Append(',').Append(m.name.Replace(" (Killcraft, lit as the level)", "~"))
                        .Append('<').Append(string.Join("+", m.shaderKeywords)).Append(" q").Append(m.renderQueue).Append('>');
                }
                if (block.HasColor("_Color"))
                {
                    sb.Append(" _Color=").Append(block.GetColor("_Color"));
                }
                if (block.HasColor("_EmissiveColor"))
                {
                    sb.Append(" _Em=").Append(block.GetColor("_EmissiveColor"));
                }
                if (block.HasColor("_CustomColor1"))
                {
                    sb.Append(" _C1=").Append(block.GetColor("_CustomColor1"));
                }
                sb.Append("] ");
            }
            return sb.ToString();
        }

        // ULTRAKILL's first-person gun materials are lit only for the first-person gun's camera
        // (VERTEX_LIGHTING: black anywhere else); its third-person guns' "Unlit" ones are the same
        // without it, lit as the level is. So copies of them without it.
        private static readonly Dictionary<Material, Material> worldLit = new Dictionary<Material, Material>();

        internal static Material[] WorldLit(Material[] mats)
        {
            if (mats == null)
            {
                return null;
            }
            var lit = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                lit[i] = WorldLit(mats[i]);
            }
            return lit;
        }

        private static Material WorldLit(Material m)
        {
            if (m == null || !m.IsKeywordEnabled("VERTEX_LIGHTING"))
            {
                return m;
            }
            if (worldLit.TryGetValue(m, out Material done) && done != null)
            {
                return done;
            }
            var copy = new Material(m) { name = m.name + " (Killcraft, lit as the level)" };
            copy.DisableKeyword("VERTEX_LIGHTING");
            if (copy.HasProperty("_VertexLighting"))
            {
                copy.SetFloat("_VertexLighting", 0f);
            }
            worldLit[m] = copy;
            return copy;
        }

        public void Destroy()
        {
            Show(255, false, -1);
            foreach (GameObject m in models.Values)
            {
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                }
            }
            models.Clear();
        }

        private static GameObject Prefab(int weapon, bool alt, int variant)
        {
            var gs = MonoSingleton.GetInstance(typeof(GunSetter)) as GunSetter;
            if (gs == null)
            {
                return null;
            }
            AssetReference[][] byVariant;
            switch (weapon)
            {
                case 0: byVariant = new[] { gs.revolverPierce, gs.revolverRicochet, gs.revolverTwirl }; break;
                case 1: byVariant = new[] { gs.shotgunGrenade, gs.shotgunPump, gs.shotgunRed }; break;
                case 2: byVariant = new[] { gs.nailMagnet, gs.nailOverheat, gs.nailRed }; break;
                case 3: byVariant = new[] { gs.railCannon, gs.railHarpoon, gs.railMalicious }; break;
                case 4: byVariant = new[] { gs.rocketBlue, gs.rocketGreen, gs.rocketRed }; break;
                default: return null;
            }
            AssetReference[] refs = byVariant[variant];
            int at = alt ? 1 : 0;
            if (refs == null || refs.Length <= at || refs[at] == null || !refs[at].RuntimeKeyIsValid())
            {
                return null;
            }
            return AssetHelper.LoadPrefab(refs[at]);
        }

        private GameObject Make(int weapon, bool alt, int variant, GameObject holder, string want)
        {
            var box = new GameObject("Killcraft variant gun (being made)");
            box.SetActive(false);
            try
            {
                GameObject prefab = Prefab(weapon, alt, variant);
                if (prefab == null)
                {
                    throw new Exception("ULTRAKILL has no prefab for it here");
                }
                // (Made inside something switched off: none of its scripts wake.)
                GameObject inst = UnityEngine.Object.Instantiate(prefab, box.transform, false);
                inst.name = $"{prefab.name} (Killcraft third person)";
                // (Its screens, the rocket launcher's clock and the like, aren't the gun.)
                foreach (Canvas c in inst.GetComponentsInChildren<Canvas>(true))
                {
                    if (c != null)
                    {
                        UnityEngine.Object.DestroyImmediate(c.gameObject);
                    }
                }
                var keep = new HashSet<Renderer>();
                foreach (GunColorGetter g in inst.GetComponentsInChildren<GunColorGetter>(true))
                {
                    Renderer r = g.GetComponent<Renderer>();
                    if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    {
                        keep.Add(r);
                    }
                }
                foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if ((r is MeshRenderer || r is SkinnedMeshRenderer) && ExtraParts.Contains(r.name))
                    {
                        keep.Add(r);
                    }
                }
                // Where it goes: the same skinned part's bone in the third-person gun (same index, same
                // name: the same rig), else by the part's own name.
                Transform mine = null, theirs = null;
                var holderParts = new List<SkinnedMeshRenderer>();
                foreach (SkinnedMeshRenderer hp in holder.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!Ours(hp.transform))
                    {
                        holderParts.Add(hp);
                    }
                }
                foreach (Renderer r in keep)
                {
                    if (!(r is SkinnedMeshRenderer smr))
                    {
                        continue;
                    }
                    foreach (SkinnedMeshRenderer other in holderParts)
                    {
                        if (other.name != smr.name || other.bones == null || smr.bones == null || other.bones.Length != smr.bones.Length)
                        {
                            continue;
                        }
                        for (int b = 0; b < smr.bones.Length && mine == null; b++)
                        {
                            if (smr.bones[b] != null && other.bones[b] != null && smr.bones[b].name == other.bones[b].name)
                            {
                                mine = smr.bones[b];
                                theirs = other.bones[b];
                            }
                        }
                        if (mine != null)
                        {
                            break;
                        }
                    }
                    if (mine != null)
                    {
                        break;
                    }
                }
                if (mine == null)
                {
                    foreach (Renderer r in keep)
                    {
                        if (r is SkinnedMeshRenderer smr)
                        {
                            foreach (Transform cand in new[] { smr.rootBone, smr.transform })
                            {
                                Transform t = cand != null ? FindIn(holder.transform, cand.name) : null;
                                if (t != null && mine == null)
                                {
                                    mine = cand;
                                    theirs = t;
                                }
                            }
                        }
                    }
                }
                if (mine == null)
                {
                    throw new Exception("no bone of it matches the third-person gun's");
                }
                // (Where the prefab's root is from that bone: the same from the third-person one.)
                Matrix4x4 local = mine.worldToLocalMatrix * inst.transform.localToWorldMatrix;
                List<Skins.Part> parts = Skins.Collect(inst);
                parts.RemoveAll(part => !keep.Contains(part.R));
                foreach (Skins.Part part in parts)
                {
                    part.Default = WorldLit(part.Default);
                    part.Colored = WorldLit(part.Colored);
                }
                foreach (ParticleSystem ps in inst.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.EmissionModule em = ps.emission;
                    em.enabled = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                V1Body.Strip(inst);
                foreach (Animator a in inst.GetComponentsInChildren<Animator>(true))
                {
                    UnityEngine.Object.DestroyImmediate(a);
                }
                var names = new List<string>();
                foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (!keep.Contains(r))
                    {
                        r.enabled = false;
                        continue;
                    }
                    for (Transform up = r.transform; up != null && up != inst.transform; up = up.parent)
                    {
                        up.gameObject.SetActive(true);
                    }
                    r.enabled = true;
                    r.forceRenderingOff = false;
                    r.sharedMaterials = WorldLit(r.sharedMaterials);
                    r.shadowCastingMode = ShadowCastingMode.On;
                    if (r is SkinnedMeshRenderer s2)
                    {
                        s2.updateWhenOffscreen = true;
                    }
                    names.Add(r.name);
                }
                foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = 0;
                }
                Transform it = inst.transform;
                it.SetParent(theirs, false);
                Vector3 x = local.GetColumn(0), y = local.GetColumn(1), z = local.GetColumn(2);
                it.localPosition = local.GetColumn(3);
                it.localRotation = Quaternion.LookRotation(z, y);
                // (Mirrored: one of its axes the other way, not a turn.)
                float sx = Vector3.Dot(Vector3.Cross(y, z), x) < 0f ? -x.magnitude : x.magnitude;
                it.localScale = new Vector3(sx, y.magnitude, z.magnitude);
                Made = parts;
                if (logged.Add(prefab.name))
                {
                    var mats = new List<string>();
                    foreach (Renderer r in keep)
                    {
                        foreach (Material m in r.sharedMaterials)
                        {
                            if (m != null)
                            {
                                mats.Add($"{m.name} <{m.shader.name}>");
                            }
                        }
                    }
                    Plugin.Log.LogInfo($"variant guns: their own model ({prefab.name}: {string.Join(", ", names)}, on {theirs.name}{(sx < 0f ? ", mirrored" : "")}; {string.Join(", ", mats)})");
                }
                return inst;
            }
            catch (Exception e)
            {
                failed.Add(want);
                Plugin.Log.LogWarning($"variant guns: none of its own for weapon {weapon}{(alt ? " (alternate)" : "")}, variant {variant} ({e.Message}); coloured instead");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(box);
            }
        }

        // Inside a variant model made before (its root's name says so).
        private static bool Ours(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (t.name.EndsWith("(Killcraft third person)", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static Transform FindIn(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name && !Ours(t))
                {
                    return t;
                }
            }
            return null;
        }
    }
}
