using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // V1's body alive above the legs: this player's in F5, and the other players' V1s (V1Body).
    // ULTRAKILL's own animations only move its legs (the body's for being seen from its own eyes: the
    // chest turned with the view, the head and gun aimed where it looks), so seen from outside it ran
    // stiff from the hips up. On top of them, after they've posed it each frame:
    //  - the chest leaning into a run (and away from a strafe), rolling into turns, twisting against
    //    the stride, the free arm swinging with it; a lean back or forward in the air;
    //  - breathing, standing;
    //  - a kick back with each shot, a turn into each punch (Kick);
    //  - (another player's) the legs' cycle sped up or slowed so the foot on the ground stays put:
    //    their body is drawn bigger than ULTRAKILL made it, and its feet slid.
    // The head and the gun keep their aim.
    internal sealed class V1Motion
    {
        private static readonly AccessTools.FieldRef<PlayerAnimations, Transform> ChestOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("chest"),
            HeadOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("head"),
            RightArmOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("rightArm"),
            LeftArmOf = AccessTools.FieldRefAccess<PlayerAnimations, Transform>("leftArm");
        private static readonly AccessTools.FieldRef<PlayerAnimations, float> LeftArmWeightOf = AccessTools.FieldRefAccess<PlayerAnimations, float>("leftArmWeight");

        private static readonly V1Motion mine = new V1Motion();

        private Transform found;
        private Transform leftFoot, rightFoot;
        private float lean, roll, twist, swing, legSpan = 1f, lastYaw, turn, kickPitch, kickYaw, breath;
        private bool haveYaw;
        // (The legs' speed, and the free arm raised to the whiplash: another player's.)
        private float legSpeed = 1f, hookArm;

        // This player's own V1 in F5 (after its PlayerAnimations).
        public static void Apply(PlayerAnimations pa)
        {
            var nm = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
            if (pa == null || nm == null || nm.rb == null)
            {
                return;
            }
            bool ground = nm.gc != null && nm.gc.onGround;
            bool still = nm.sliding || (nm.boost && !nm.sliding);
            // (As the others see it: the slam's tuck, the arm raised to the whiplash.)
            bool slam = nm.gc != null && nm.gc.heavyFall && !ground;
            bool hooked = false;
            try
            {
                HookArm arm = MonoSingleton<HookArm>.Instance;
                hooked = arm != null && arm.state != HookState.Ready;
            }
            catch
            {
            }
            Vector3 look = nm.cc != null ? nm.cc.transform.forward : nm.transform.forward;
            mine.Run(pa.transform, nm.transform, nm.rb.velocity, ground, still, ChestOf(pa), HeadOf(pa), RightArmOf(pa), LeftArmOf(pa),
                LeftArmWeightOf(pa) <= 0.01f, null, -1f, slam, hooked, look);
        }

        // A shot's kick back, a punch's turn (degrees).
        public void Kick(float pitch, float yaw)
        {
            kickPitch = Mathf.Max(kickPitch, pitch);
            kickYaw += yaw;
        }

        // body: what the animations pose (its bones under it); basis: turned the way the player looks
        // (flat); velocity: theirs; anim: to set the legs' speed by (another player's), or null.
        // Another player's too: dt (their time, unscaled), slam (falling in a ground slam), hooked (the
        // whiplash out), look (where they look), legScale (how much bigger than V1 the body is drawn).
        public void Run(Transform body, Transform basis, Vector3 velocity, bool ground, bool still, Transform chest, Transform head,
            Transform rightArm, Transform leftArm, bool leftFree, Animator anim,
            float dt = -1f, bool slam = false, bool hooked = false, Vector3 look = default, float legScale = 1f)
        {
            if (body == null || chest == null || head == null || rightArm == null)
            {
                return;
            }
            if (body != found)
            {
                Find(body);
            }
            dt = Mathf.Max(dt > 0f ? dt : Time.deltaTime, 1e-4f);
            Vector3 local = basis.InverseTransformDirection(velocity);
            float speed = new Vector2(local.x, local.z).magnitude;
            float run = ground && !still ? Mathf.Clamp01(speed / 16f) : 0f;

            // How fast it's turning (rolling into it, running).
            float yaw = basis.eulerAngles.y;
            float yawRate = haveYaw ? Mathf.DeltaAngle(lastYaw, yaw) / dt : 0f;
            lastYaw = yaw;
            haveYaw = true;
            turn = Mathf.Lerp(turn, Mathf.Clamp(yawRate / 240f, -1f, 1f), 1f - Mathf.Exp(-dt * 8f));

            // The stride: from where the feet are (left one ahead: +1), else the animation's own cycle.
            float stride = 0f;
            if (leftFoot != null && rightFoot != null)
            {
                float ahead = Vector3.Dot(leftFoot.position - rightFoot.position, basis.forward);
                legSpan = Mathf.Max(legSpan * (1f - dt * 0.5f), Mathf.Abs(ahead), 0.3f);
                stride = Mathf.Clamp(ahead / legSpan, -1f, 1f);
            }

            float k = 1f - Mathf.Exp(-dt * 12f);
            // (Slamming down: tucked forward, straight.)
            float leanTo = slam ? RemoteTuning.SlamLean
                : ground ? Mathf.Clamp(local.z / 16f, -1f, 1f) * 15f * (still ? 0f : 1f) : Mathf.Clamp(-local.y * 0.5f, -14f, 12f);
            float rollTo = slam ? 0f : Mathf.Clamp(-local.x / 16f, -1f, 1f) * (ground ? 10f : 6f) - turn * 10f * Mathf.Max(run, 0.3f);
            lean = Mathf.Lerp(lean, leanTo, k);
            roll = Mathf.Lerp(roll, rollTo, k);
            twist = Mathf.Lerp(twist, -stride * 14f * run, 1f - Mathf.Exp(-dt * 20f));
            swing = Mathf.Lerp(swing, stride * 38f * run, 1f - Mathf.Exp(-dt * 20f));
            kickPitch = Mathf.MoveTowards(kickPitch, 0f, dt * Mathf.Max(40f, kickPitch * 9f));
            kickYaw = Mathf.MoveTowards(kickYaw, 0f, dt * Mathf.Max(60f, Mathf.Abs(kickYaw) * 8f));
            // Breathing, standing (and a little running).
            breath += dt * Mathf.Lerp(1.6f, 4f, run);
            float breathe = Mathf.Sin(breath) * Mathf.Lerp(1.8f, 0.6f, run) * (ground ? 1f : 0.3f);

            // (What ULTRAKILL aimed stays aimed: the head, the gun arm.)
            Quaternion headAim = head.rotation, armAim = rightArm.rotation;
            Transform aimer = rightArm.childCount > 0 ? rightArm.GetChild(0) : null;
            Quaternion aimerAim = aimer != null ? aimer.rotation : Quaternion.identity;

            chest.rotation = Quaternion.AngleAxis(twist + kickYaw, basis.up)
                * Quaternion.AngleAxis(lean + breathe - kickPitch, basis.right)
                * Quaternion.AngleAxis(roll, basis.forward) * chest.rotation;
            head.rotation = headAim;
            rightArm.rotation = armAim;
            if (aimer != null)
            {
                aimer.rotation = aimerAim;
            }
            // The free arm (not while it punches or throws); raised to the whiplash while it's out.
            if (leftArm != null && leftFree)
            {
                float air = ground ? 0f : -25f;
                leftArm.rotation = Quaternion.AngleAxis(swing + air + breathe * 2f, basis.right) * leftArm.rotation;
                hookArm = Mathf.Lerp(hookArm, hooked ? 1f : 0f, 1f - Mathf.Exp(-dt * RemoteTuning.HookArmEase));
                if (hookArm > 0.01f && look.sqrMagnitude > 0.01f)
                {
                    // (Along the bone as the gun arm is aimed: PlayerAnimations.AimRightArm's turns.)
                    Quaternion reach = Quaternion.LookRotation(look, Vector3.up) * Quaternion.AngleAxis(90f, Vector3.right) * Quaternion.AngleAxis(180f, Vector3.up);
                    leftArm.rotation = Quaternion.Slerp(leftArm.rotation, reach, hookArm);
                }
            }
            else
            {
                hookArm = 0f;
            }

            if (anim != null)
            {
                Legs(anim, speed, run, dt, legScale);
            }
        }

        // The legs' cycle at the body's ground speed, so the foot on the ground stays put: the run
        // clip's full stride is V1 at RunClipSpeed (faster above it), and a body drawn legScale times
        // bigger strides legScale times further (slower). (The whole animator's speed: there's no
        // other; eased, and only while running.)
        private void Legs(Animator anim, float speed, float run, float dt, float legScale)
        {
            float want = run < 0.2f ? 1f
                : Mathf.Clamp(Mathf.Max(1f, speed / RemoteTuning.RunClipSpeed) / Mathf.Max(legScale, 0.1f), RemoteTuning.LegSpeedMin, RemoteTuning.LegSpeedMax);
            legSpeed = Mathf.Lerp(legSpeed, want, 1f - Mathf.Exp(-dt * RemoteTuning.LegSpeedEase));
            anim.speed = legSpeed;
        }

        // The feet, by name (left or right; a foot, else the lower leg, else the leg).
        private void Find(Transform body)
        {
            found = body;
            leftFoot = rightFoot = null;
            int bestL = 0, bestR = 0;
            var names = new StringBuilder();
            foreach (Transform t in body.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                int rank = n.Contains("foot") || n.Contains("ankle") ? 3 : n.Contains("calf") || n.Contains("shin") || n.Contains("knee") || n.Contains("lowerleg") ? 2 : n.Contains("leg") || n.Contains("thigh") ? 1 : 0;
                if (rank == 0)
                {
                    continue;
                }
                names.Append(' ').Append(t.name);
                int side = Side(n);
                if (side < 0 && rank > bestL)
                {
                    bestL = rank;
                    leftFoot = t;
                }
                else if (side > 0 && rank > bestR)
                {
                    bestR = rank;
                    rightFoot = t;
                }
            }
            if (this == mine)
            {
                Plugin.Log.LogInfo($"third person: V1's body moves above the legs too (feet: {(leftFoot != null ? leftFoot.name : "?")}, {(rightFoot != null ? rightFoot.name : "?")}; legs:{names})");
            }
        }

        private static int Side(string n)
        {
            if (n.Contains("left") || n.EndsWith(".l") || n.EndsWith("_l") || n.EndsWith(" l") || n.StartsWith("l_") || n.StartsWith("l.") || n.Contains("_l_") || n.Contains(".l."))
            {
                return -1;
            }
            if (n.Contains("right") || n.EndsWith(".r") || n.EndsWith("_r") || n.EndsWith(" r") || n.StartsWith("r_") || n.StartsWith("r.") || n.Contains("_r_") || n.Contains(".r."))
            {
                return 1;
            }
            return 0;
        }
    }
}
