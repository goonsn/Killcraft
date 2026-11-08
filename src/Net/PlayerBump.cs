using UnityEngine;

namespace Killcraft
{
    // Players bumping into each other (the party host's setting, for everyone): another player's V1 is
    // solid to walk into. Each game pushes only its own V1 out of the others (so each player's push
    // is half), and only as far as nothing of the level is in the way: swept against everything V1
    // collides with, so a push never puts anyone into or through a wall, a floor, or out of the map.
    internal static class PlayerCollision
    {
        // V1 as one upright cylinder for this: how far apart two stand at least, and how tall.
        private const float Apart = 1.25f, Tall = 2.8f;

        // Whether this player's setting is the one that counts: alone, or the party's host.
        public static bool Mine => !SteamNet.InLobby || SteamNet.IAmOwner;

        public static bool On => Mine ? Plugin.PlayerCollision.Value : Room.Collision;

        public static void Set(bool on)
        {
            if (Mine)
            {
                Plugin.PlayerCollision.Value = on;
                return;
            }
            PartyChat.System("Only the party host can turn player collision on or off.");
        }

        public static void Frame(NewMovement nm, bool inLevel, bool ultrakillDrives)
        {
            if (!On || !inLevel || !ultrakillDrives || nm == null || nm.dead || nm.rb == null || nm.rb.isKinematic
                || !Room.Shared || Replay.Playing || Time.deltaTime <= 0f)
            {
                return;
            }
            Vector3 feet = nm.transform.position - Vector3.up * Coords.FeetBelowRoot;
            Vector3 push = Vector3.zero;
            foreach (Vector3 other in RemotePlayers.Bodies())
            {
                if (Mathf.Abs(feet.y - other.y) > Tall)
                {
                    continue;
                }
                Vector3 d = feet - other;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist >= Apart)
                {
                    continue;
                }
                // (Right on top of each other: apart sideways, each its own way.)
                Vector3 dir = dist > 0.01f ? d / dist : (Room.MyId % 2 == 0 ? Vector3.right : Vector3.left);
                push += dir * (Apart - dist);
            }
            if (push.sqrMagnitude < 0.0001f)
            {
                return;
            }
            Vector3 away = push.normalized;
            // (No more running on into them.)
            Vector3 v = nm.rb.velocity;
            float into = Vector3.Dot(new Vector3(v.x, 0f, v.z), -away);
            if (into > 0f)
            {
                nm.rb.velocity = v + away * into;
            }
            float step = Mathf.Min(push.magnitude * 0.5f * Mathf.Min(1f, 15f * Time.deltaTime), 0.5f);
            if (nm.rb.SweepTest(away, out RaycastHit hit, step + 0.05f, QueryTriggerInteraction.Ignore))
            {
                step = Mathf.Max(0f, hit.distance - 0.05f);
            }
            if (step > 0.0001f)
            {
                nm.rb.position += away * step;
                nm.transform.position += away * step;
            }
        }
    }

    // Out of the map (in a party): V1 falling with nothing at all of the level below it for a while (it
    // went through a wall or a floor, or came back somewhere outside) is put back where it last stood
    // on solid ground. Falls into the level's own pits are ULTRAKILL's as ever (there's a floor or a
    // death zone below those).
    internal static class OutOfMap
    {
        private static Vector3 safe;
        private static bool haveSafe;
        private static float lostAt = -1f, checkAt;
        private static string scene = "";

        public static void Frame(NewMovement nm, bool inLevel, bool ultrakillDrives)
        {
            string now = SceneHelper.CurrentScene ?? "";
            if (now != scene)
            {
                scene = now;
                haveSafe = false;
                lostAt = -1f;
            }
            if (!Plugin.OutOfMapRescue.Value || !SteamNet.InLobby || !inLevel || !ultrakillDrives || nm == null || nm.dead
                || nm.rb == null || nm.rb.isKinematic || Replay.Playing || Profiles.Mine.State == Profiles.StateDone)
            {
                lostAt = -1f;
                return;
            }
            if (Time.unscaledTime - checkAt < 0.25f)
            {
                return;
            }
            checkAt = Time.unscaledTime;
            Vector3 root = nm.transform.position;
            int mask = LayerMaskDefaults.Get(LMD.Environment);
            if (nm.gc != null && nm.gc.onGround)
            {
                if (Physics.Raycast(root, Vector3.down, 4f, mask, QueryTriggerInteraction.Ignore))
                {
                    safe = root + Vector3.up * 0.1f;
                    haveSafe = true;
                }
                lostAt = -1f;
                return;
            }
            if (nm.rb.velocity.y > -5f || Physics.Raycast(root, Vector3.down, 5000f, mask, QueryTriggerInteraction.Ignore))
            {
                lostAt = -1f;
                return;
            }
            if (lostAt < 0f)
            {
                lostAt = Time.unscaledTime;
            }
            if (Time.unscaledTime - lostAt < 4f)
            {
                return;
            }
            lostAt = -1f;
            if (haveSafe)
            {
                Plugin.Log.LogWarning($"KillNet: V1 fell out of the level at {root}; back on solid ground at {safe}");
                nm.rb.velocity = Vector3.zero;
                nm.rb.position = safe;
                nm.transform.position = safe;
                PartyChat.System("You fell out of the map: put back on solid ground.");
                return;
            }
            CheckPoint cp = MonoSingleton<StatsManager>.Instance != null ? MonoSingleton<StatsManager>.Instance.currentCheckPoint : null;
            if (cp != null)
            {
                Plugin.Log.LogWarning($"KillNet: V1 fell out of the level at {root}; back to checkpoint {cp.name}");
                cp.OnRespawn();
                PartyChat.System("You fell out of the map: back to your checkpoint.");
            }
        }
    }
}
