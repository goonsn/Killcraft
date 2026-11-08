using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // Damage numbers (with Minecraft off, V1 fighting with ULTRAKILL's weapons): what each of V1's
    // hits does, floating up from where it landed and fading: on ULTRAKILL's enemies, on other
    // players (PvP), on Minecraft's mobs. As V1's health counts it: ten for each of an enemy's
    // (a revolver shot is 10, as it is to V1). Hits close together on the same one add up (a
    // shotgun's pellets are one number); a headshot's is yellow, a killing blow's red and bigger.
    internal static class DamageNumbers
    {
        private const float Life = 0.9f, Merge = 0.2f, ToShown = 10f;

        private sealed class Number
        {
            public object Target;
            public Vector3 At;
            public float Amount, Born, Last;
            public bool Head, Kill;
        }

        private static readonly List<Number> numbers = new List<Number>();
        private static GUIStyle style;
        private static bool show;

        // An amount of ULTRAKILL's damage (an enemy's health), at that place, on that one.
        public static void Enemy(object target, Vector3 at, float damage, bool head, bool kill) => Add(target, at, damage * ToShown, head, kill);

        // An amount of V1's health (another player's: Pvp).
        public static void Add(object target, Vector3 at, float amount, bool head, bool kill)
        {
            if (!show || amount <= 0.05f)
            {
                return;
            }
            float now = Time.unscaledTime;
            foreach (Number n in numbers)
            {
                if (n.Target != null && Equals(n.Target, target) && now - n.Last < Merge)
                {
                    n.Amount += amount;
                    n.Last = now;
                    n.Head |= head;
                    n.Kill |= kill;
                    return;
                }
            }
            if (numbers.Count > 40)
            {
                numbers.RemoveAt(0);
            }
            numbers.Add(new Number { Target = target, At = at, Amount = amount, Born = now, Last = now, Head = head, Kill = kill });
        }

        // Each frame: shown while V1 is ULTRAKILL's, in a level, not paused.
        public static void Frame(bool on)
        {
            show = on;
            if (!on)
            {
                numbers.Clear();
            }
        }

        public static void Gui()
        {
            if (!show || numbers.Count == 0 || Event.current.type != EventType.Repaint)
            {
                return;
            }
            var cc = MonoSingleton.GetInstance(typeof(CameraController)) as CameraController;
            Camera cam = cc != null ? cc.cam : Camera.main;
            if (cam == null)
            {
                return;
            }
            style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            float now = Time.unscaledTime;
            for (int i = numbers.Count - 1; i >= 0; i--)
            {
                Number n = numbers[i];
                float age = now - n.Last;
                if (age > Life)
                {
                    numbers.RemoveAt(i);
                    continue;
                }
                Vector3 p = cam.WorldToScreenPoint(n.At);
                if (p.z <= 0f)
                {
                    continue;
                }
                float sinceBorn = now - n.Born;
                float fade = 1f - Mathf.Clamp01((age - 0.45f) / (Life - 0.45f));
                // (A pop as it lands, then up it floats.)
                float pop = 1f + Mathf.Max(0f, 0.12f - age) * 3f;
                float size = Screen.height * (0.022f + Mathf.Clamp01(n.Amount / 60f) * 0.014f) * (n.Kill ? 1.3f : 1f) * pop;
                style.fontSize = Mathf.Max(8, Mathf.RoundToInt(size));
                float x = p.x, y = Screen.height - p.y - sinceBorn * Screen.height * 0.06f;
                string text = Mathf.Max(1, Mathf.RoundToInt(n.Amount)).ToString();
                var r = new Rect(x - 120f, y - 30f, 240f, 60f);
                style.normal.textColor = new Color(0f, 0f, 0f, 0.85f * fade);
                GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, style);
                Color c = n.Kill ? new Color(1f, 0.2f, 0.15f) : n.Head ? new Color(1f, 0.85f, 0.2f) : Color.white;
                c.a = fade;
                style.normal.textColor = c;
                GUI.Label(r, text, style);
            }
        }

        // ---- ULTRAKILL's enemies (this ULTRAKILL's own: their health goes down here) ----

        private struct Before
        {
            public float Health;
            public bool Dead;
        }

        [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage)), HarmonyPrefix]
        private static void DamageBefore(EnemyIdentifier __instance, out Before __state)
        {
            __state = new Before { Health = __instance != null ? __instance.health : 0f, Dead = __instance == null || __instance.dead };
        }

        [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage)), HarmonyPostfix]
        private static void DamageAfter(EnemyIdentifier __instance, GameObject target, Vector3 hitPoint, GameObject sourceWeapon, Before __state)
        {
            if (!show || __instance == null || __state.Dead || Pvp.Is(__instance) || !ByPlayer(__instance.hitter, sourceWeapon))
            {
                return;
            }
            try
            {
                __instance.ForceGetHealth();
            }
            catch
            {
            }
            float lost = __state.Health - __instance.health;
            if (lost > 0.001f)
            {
                Enemy(__instance, Where(__instance, target, hitPoint), lost, target != null && target.CompareTag("Head"), __instance.dead || __instance.health <= 0f);
            }
        }

        // (Someone else's enemy, this player's hit sent to them: EnemySync. About what it does there.)
        public static void Sent(EnemyIdentifier eid, GameObject target, Vector3 hitPoint, float multiplier, float crit)
        {
            if (!show || eid == null || !ByPlayer(eid.hitter, null))
            {
                return;
            }
            bool head = target != null && target.CompareTag("Head");
            Enemy(eid, Where(eid, target, hitPoint), multiplier * (1f + (head ? Mathf.Max(crit, 0f) : 0f)) * eid.totalDamageTakenMultiplier, head, false);
        }

        private static bool ByPlayer(string hitter, GameObject sourceWeapon) =>
            Pvp.IsWeapon(hitter ?? "") || (sourceWeapon != null && hitter != "enemy");

        private static Vector3 Where(EnemyIdentifier eid, GameObject target, Vector3 hitPoint)
        {
            if (hitPoint != Vector3.zero)
            {
                return hitPoint;
            }
            return target != null ? target.transform.position : eid.transform.position;
        }
    }
}
