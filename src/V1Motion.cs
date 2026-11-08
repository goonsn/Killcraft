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
        // (The legs' speed: another player's.)
        private Transform planted;
        private Vector3 plantedAt;
        private float legSpeed = 1f;

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
            mine.Run(pa.transform, nm.transform, nm.rb.velocity, ground, still, ChestOf(pa), HeadOf(pa), RightArmOf(pa), LeftArmOf(pa),
                LeftArmWeightOf(pa) <= 0.01f, null);
        }

        // A shot's kick back, a punch's turn (degrees).
        public void Kick(float pitch, float yaw)
        {
            kickPitch = Mathf.Max(kickPitch, pitch);
            kickYaw += yaw;
        }

        // body: what the animations pose (its bones under it); basis: turned the way the player looks
        // (flat); velocity: theirs; anim: to set the legs' speed by (another player's), or null.
        public void Run(Transform body, Transform basis, Vector3 velocity, bool ground, bool still, Transform chest, Transform head,
            Transform rightArm, Transform leftArm, bool leftFree, Animator anim)
        {
            if (body == null || chest == null || head == null || rightArm == null)
            {
                return;
            }
            if (body != found)
            {
                Find(body);
            }
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
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
            float leanTo = ground ? Mathf.Clamp(local.z / 16f, -1f, 1f) * 15f * (still ? 0f : 1f) : Mathf.Clamp(-local.y * 0.5f, -14f, 12f);
            float rollTo = Mathf.Clamp(-local.x / 16f, -1f, 1f) * (ground ? 10f : 6f) - turn * 10f * Mathf.Max(run, 0.3f);
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
            // The free arm (not while it punches or throws).
            if (leftArm != null && leftFree)
            {
                float air = ground ? 0f : -25f;
                leftArm.rotation = Quaternion.AngleAxis(swing + air + breathe * 2f, basis.right) * leftArm.rotation;
            }

            if (anim != null)
            {
                Legs(anim, velocity, run, dt);
            }
        }

        // The foot on the ground (the lower) should stay where it is while the body goes on: moving
        // with the body, the legs are behind (faster); going back, ahead (slower).
        private void Legs(Animator anim, Vector3 velocity, float run, float dt)
        {
            if (run < 0.25f || leftFoot == null || rightFoot == null)
            {
                planted = null;
                legSpeed = Mathf.MoveTowards(legSpeed, 1f, dt * 2f);
                anim.speed = legSpeed;
                return;
            }
            Transform low = leftFoot.position.y < rightFoot.position.y ? leftFoot : rightFoot;
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            float v = flat.magnitude;
            if (low == planted && v > 1f)
            {
                Vector3 moved = low.position - plantedAt;
                float slip = Vector3.Dot(new Vector3(moved.x, 0f, moved.z) / dt, flat / v) / v;
                legSpeed = Mathf.Clamp(legSpeed * Mathf.Exp(Mathf.Clamp(slip, -1f, 1f) * dt * 2.5f), 0.5f, 2.2f);
            }
            planted = low;
            plantedAt = low.position;
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
