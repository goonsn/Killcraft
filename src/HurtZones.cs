using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's solid hurt zones (Act 2's sand, ...) count the player in them from collisions. While
    // Minecraft drives V1 its body is kinematic, and Unity reports no collisions between that and the
    // level, so they never noticed it: V1 walked on the sand unhurt. Here what V1's capsule (a little
    // bigger: standing on it is touching it) is in contact with is checked instead, and the zone told,
    // as its own OnCollisionEnter/Exit would. (Trigger zones still see a kinematic body by themselves.)
    internal static class HurtZones
    {
        private static readonly AccessTools.FieldRef<HurtZone, int> hurtingPlayer = AccessTools.FieldRefAccess<HurtZone, int>("hurtingPlayer");
        private static HashSet<HurtZone> touching = new HashSet<HurtZone>(), now = new HashSet<HurtZone>();
        private static readonly Collider[] hits = new Collider[32];
        private static bool broken;

        // active: Minecraft drives V1 (its body kinematic).
        public static void Frame(NewMovement nm, bool active)
        {
            if (broken)
            {
                return;
            }
            try
            {
                now.Clear();
                CapsuleCollider cap = nm != null ? nm.playerCollider : null;
                if (active && cap != null && cap.enabled && nm.gameObject.activeInHierarchy && !nm.dead)
                {
                    Transform t = cap.transform;
                    Vector3 scale = t.lossyScale;
                    float radius = cap.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) + 0.15f;
                    float half = Mathf.Max(cap.height * Mathf.Abs(scale.y) * 0.5f - cap.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)), 0f);
                    Vector3 center = t.TransformPoint(cap.center);
                    int n = Physics.OverlapCapsuleNonAlloc(center - t.up * half, center + t.up * half, radius, hits, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        Collider c = hits[i];
                        if (c == null || c == cap)
                        {
                            continue;
                        }
                        HurtZone zone = c.GetComponentInParent<HurtZone>();
                        if (zone != null && !zone.trigger && zone.affected != AffectedSubjects.EnemiesOnly)
                        {
                            now.Add(zone);
                        }
                    }
                }
                foreach (HurtZone zone in now)
                {
                    if (!touching.Contains(zone))
                    {
                        hurtingPlayer(zone)++;
                    }
                }
                foreach (HurtZone zone in touching)
                {
                    if (zone != null && !now.Contains(zone) && hurtingPlayer(zone) > 0)
                    {
                        hurtingPlayer(zone)--;
                    }
                }
                (touching, now) = (now, touching);
            }
            catch (System.Exception e)
            {
                broken = true;
                Plugin.Log.LogWarning($"hurt zones: {e.Message}; sand and other hurting floors won't hurt while Minecraft has V1");
            }
        }
    }
}
