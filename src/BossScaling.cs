using HarmonyLib;

namespace Killcraft
{
    // Enemies are tougher with more players in the level: each other player there adds the party host's
    // BossHealthPerPlayer of a boss's health, EnemyHealthPerPlayer of any other's (ULTRAKILL's own health
    // modifier, as its radiance does: hits on it count for less). Not in VERSUS (each plays alone).
    [HarmonyPatch(typeof(EnemyIdentifier), "UpdateModifiers")]
    internal static class BossScaling
    {
        private static void Postfix(EnemyIdentifier __instance)
        {
            if (__instance == null)
            {
                return;
            }
            // (A Minos Prime round: Minos's health times the V1s.)
            if (MinosMode.HealthFor(__instance, out float times))
            {
                __instance.totalHealthModifier *= times;
                return;
            }
            if (!Room.Shared || Pvp.Is(__instance))
            {
                return;
            }
            float per = __instance.isBoss ? PartyRules.BossHealth : PartyRules.EnemyHealth;
            if (per > 0f)
            {
                __instance.totalHealthModifier *= 1f + per * Room.Peers.Count;
            }
        }
    }
}
