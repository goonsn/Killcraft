using HarmonyLib;

namespace Killcraft
{
    // Bosses are tougher with more players in the level: each other player there adds
    // BossHealthPerPlayer of their health (ULTRAKILL's own health modifier, as its radiance does).
    [HarmonyPatch(typeof(EnemyIdentifier), "UpdateModifiers")]
    internal static class BossScaling
    {
        private static void Postfix(EnemyIdentifier __instance)
        {
            if (__instance == null || !__instance.isBoss || !Room.Shared)
            {
                return;
            }
            float per = UnityEngine.Mathf.Clamp(Plugin.BossHealthPerPlayer.Value, 0f, 2f);
            if (per > 0f)
            {
                __instance.totalHealthModifier *= 1f + per * Room.Peers.Count;
            }
        }
    }
}
