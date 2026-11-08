using System;
using Steamworks;
using UnityEngine;

namespace Killcraft
{
    // Back into the party by itself (AutoRejoin): after a dropped connection (Steam says this player
    // isn't in the lobby any more, Steam couldn't reach its owner, or nothing came from the owner for
    // a long while), and after ULTRAKILL closed in one without leaving it (a crash: InPartyNow still set
    // at the start). A few tries, then REJOIN on the party screen is left for later.
    internal static class Rejoin
    {
        // (Long enough for the owner's slowest level load: its game says nothing while one loads.)
        private const float SilentFor = 45f;
        private const float OutOfLobbyFor = 4f;
        private const int Tries = 4;
        private const float BetweenTries = 6f;

        private static bool startChecked, hooked;
        private static ulong target;
        private static string targetName = "";
        private static int tries;
        private static float nextTryAt;
        private static float outSince = -1f, checkedAt = -100f, lastFrame = -100f, quietFrom = -100f;
        private static ulong failedFor;

        public static bool Busy => target != 0;

        public static void Cancel()
        {
            if (target != 0)
            {
                Plugin.Log.LogInfo("rejoin: stopped");
            }
            target = 0;
            failedFor = 0;
        }

        // Each frame (from ServerList's).
        public static void Frame()
        {
            if (LocalLink.Wanted || !ServerList.SteamUp)
            {
                return;
            }
            float now = Time.unscaledTime;
            // (A long frame here - a level loading - isn't the owner going quiet: counted from after it.)
            if (now - lastFrame > 2f)
            {
                quietFrom = now;
            }
            lastFrame = now;
            if (!hooked)
            {
                hooked = true;
                SteamNet.ConnectionFailed += id => failedFor = id;
            }
            if (!startChecked)
            {
                startChecked = true;
                // (Started from a Steam invite: that party, not the last one.)
                if (Plugin.AutoRejoin.Value && Plugin.PartyOpen.Value && !ServerList.InParty && !ServerList.JoinPending
                    && ServerList.LastParty(out ulong id, out string name))
                {
                    Plugin.Log.LogInfo($"rejoin: ULTRAKILL closed in party {name} last time; joining it again");
                    ChatLines.Add($"Rejoining {name} (ULTRAKILL closed while you were in it)...");
                    Begin(id, name);
                }
                else
                {
                    Plugin.PartyOpen.Value = ServerList.InParty;
                }
            }
            if (target != 0)
            {
                Retry(now);
            }
            else if (Plugin.AutoRejoin.Value && ServerList.InParty)
            {
                Watch(now);
            }
        }

        private static void Begin(ulong id, string name)
        {
            target = id;
            targetName = string.IsNullOrEmpty(name) ? "the party" : name;
            tries = 0;
            nextTryAt = Time.unscaledTime + 1f;
            outSince = -1f;
            failedFor = 0;
        }

        // In a party: still really in it?
        private static void Watch(float now)
        {
            Steamworks.Data.Lobby lobby = ServerList.Party.Value;
            ulong me, owner;
            try
            {
                me = SteamClient.SteamId.Value;
                owner = lobby.Owner.Id.Value;
            }
            catch (Exception)
            {
                return;
            }
            string why = null;
            if (now - checkedAt >= 1f)
            {
                checkedAt = now;
                bool listed = false;
                try
                {
                    foreach (Friend f in lobby.Members)
                    {
                        if (f.Id.Value == me)
                        {
                            listed = true;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    listed = true;
                }
                if (listed)
                {
                    outSince = -1f;
                }
                else if (outSince < 0f)
                {
                    outSince = now;
                }
                else if (now - outSince > OutOfLobbyFor)
                {
                    why = "Steam dropped you from the lobby";
                }
            }
            if (why == null && owner != me && owner != 0)
            {
                if (failedFor == owner)
                {
                    why = "Steam couldn't reach the host";
                }
                else if (now - Mathf.Max(SteamNet.OwnerHeardAt, quietFrom) > SilentFor)
                {
                    why = $"nothing from the host for {SilentFor:0} s";
                }
            }
            failedFor = 0;
            if (why == null)
            {
                return;
            }
            ServerList.LastParty(out ulong _, out string name);
            Plugin.Log.LogWarning($"rejoin: lost the party ({why}); joining it again");
            ChatLines.Add($"Lost the connection to the party ({why}) - rejoining...");
            ulong id = lobby.Id.Value;
            ServerList.LeaveParty(rejoining: true);
            Begin(id, name);
        }

        private static void Retry(float now)
        {
            if (ServerList.InParty)
            {
                if (ServerList.Party.Value.Id.Value == target)
                {
                    Plugin.Log.LogInfo("rejoin: back in the party");
                    ChatLines.Add("Back in the party.");
                }
                // (In another one now - an invite taken meanwhile: that one.)
                target = 0;
                return;
            }
            if (ServerList.JoinPending)
            {
                return;
            }
            // (Nobody's in it any more: Steam deleted it, no use trying again.)
            if (tries > 0 && ServerList.LastJoinGone)
            {
                Plugin.Log.LogInfo($"rejoin: {targetName} is gone (nobody's in it)");
                ChatLines.Add($"{targetName} is gone - nobody's in it any more.");
                Plugin.PartyOpen.Value = false;
                target = 0;
                return;
            }
            if (now < nextTryAt)
            {
                return;
            }
            if (tries >= Tries)
            {
                Plugin.Log.LogWarning($"rejoin: couldn't get back into {targetName} in {Tries} tries");
                ChatLines.Add($"Couldn't get back into {targetName} - it may have closed. REJOIN on the party screen tries again.");
                Plugin.PartyOpen.Value = false;
                target = 0;
                return;
            }
            tries++;
            nextTryAt = now + BetweenTries;
            Plugin.Log.LogInfo($"rejoin: try {tries} of {Tries}");
            ServerList.JoinParty(target, targetName, quiet: true);
        }
    }
}
