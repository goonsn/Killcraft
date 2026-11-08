using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Killcraft
{
    // Who else plays this ULTRAKILL level (or its Nether) right now, in the same Minecraft world:
    // the server says (KillNet's Room), from each player's presence. One of them, the authority, runs
    // the level's enemies for everyone.
    internal static class Room
    {
        public static int MyId { get; private set; }
        private static int authorityId;
        public static int AuthorityId
        {
            get => ReplayAuthority != int.MinValue ? ReplayAuthority : authorityId;
            private set => authorityId = value;
        }

        // A replay playing: the one who ran its enemies (its id moved out of the way: ReplayPlayer.Fake).
        public static int ReplayAuthority = int.MinValue;
        public static readonly List<int> Peers = new List<int>();
        public static readonly Dictionary<int, Guid> Uuids = new Dictionary<int, Guid>();
        // The world's PvP setting (its host's): players hurt each other, and how hard.
        public static bool Pvp { get; private set; }
        public static float PvpDamage { get; private set; } = 1f;
        // The party's played with Minecraft (its host plays with it), as its host's game says.
        public static bool HostMinecraft { get; private set; } = true;
        // Players bump into each other (the party host's setting).
        public static bool Collision { get; private set; } = true;
        private static float heardAt = -100f, presenceAt = -100f;
        private static uint lastLevel;
        private static byte lastFlags = 255;

        // Others here (the server said so lately).
        public static bool Shared => ReplayAuthority != int.MinValue || (Peers.Count > 0 && Time.unscaledTime - heardAt < 5f);
        public static bool IAmAuthority => ReplayAuthority == int.MinValue && (!Shared || MyId == AuthorityId);

        // The level's goings-on worth saying: to others here, or kept for this run's replay.
        public static bool Sharing => Shared || Replay.Recording;

        public static void Frame(uint level, bool inLevel, bool ultrakillMode, bool nether)
        {
            byte flags = (byte)((ultrakillMode ? 1 : 0) | (nether ? 2 : 0) | (inLevel ? 4 : 0));
            if (level != lastLevel || flags != lastFlags || Time.unscaledTime - presenceAt > 1f)
            {
                lastLevel = level;
                lastFlags = flags;
                presenceAt = Time.unscaledTime;
                string scene = SceneHelper.CurrentScene ?? "";
                KillNet.Send(KillNet.Presence, KillNet.ToServer, w =>
                {
                    w.Write(level);
                    w.Write(flags);
                    // (Its name, for the others' party screens.)
                    w.Write(scene);
                    // (This player's Minecraft UUID, if they play with it.)
                    bool uuid = Plugin.WithMinecraft && (Party.McUuidMsb != 0 || Party.McUuidLsb != 0);
                    w.Write((byte)(uuid ? 1 : 0));
                    if (uuid)
                    {
                        w.Write(Party.McUuidMsb);
                        w.Write(Party.McUuidLsb);
                    }
                });
            }
            if (Time.unscaledTime - heardAt > 5f && Peers.Count > 0)
            {
                Peers.Clear();
            }
        }

        public static void Handle(BinaryReader r)
        {
            int me = r.ReadInt32();
            int authority = r.ReadInt32();
            int n = r.ReadInt32();
            var now = new List<int>();
            Uuids.Clear();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                long msb = r.ReadInt64(), lsb = r.ReadInt64();
                Uuids[id] = ToGuid(msb, lsb);
                if (id != me)
                {
                    now.Add(id);
                }
            }
            bool pvp = false;
            float pvpDamage = 1f;
            if (r.BaseStream.Length - r.BaseStream.Position >= 5)
            {
                pvp = r.ReadByte() != 0;
                pvpDamage = Mathf.Clamp(r.ReadSingle(), 0.1f, 3f);
            }
            bool mc = true;
            if (r.BaseStream.Length - r.BaseStream.Position >= 1)
            {
                mc = r.ReadByte() != 0;
            }
            if (mc != HostMinecraft)
            {
                Plugin.Log.LogInfo($"KillNet: the party is played {(mc ? "with" : "without")} Minecraft");
            }
            HostMinecraft = mc;
            bool collide = true;
            if (r.BaseStream.Length - r.BaseStream.Position >= 1)
            {
                collide = r.ReadByte() != 0;
            }
            if (collide != Collision)
            {
                Plugin.Log.LogInfo($"KillNet: players {(collide ? "bump into" : "go through")} each other (the party host's setting)");
            }
            Collision = collide;
            if (pvp != Pvp || Mathf.Abs(pvpDamage - PvpDamage) > 0.01f)
            {
                Plugin.Log.LogInfo($"KillNet: PvP {(pvp ? $"on (damage {pvpDamage:0.00})" : "off")} in this world");
            }
            Pvp = pvp;
            PvpDamage = pvpDamage;
            bool changed = me != MyId || authority != authorityId || now.Count != Peers.Count || !now.TrueForAll(Peers.Contains);
            MyId = me;
            AuthorityId = authority;
            Peers.Clear();
            Peers.AddRange(now);
            heardAt = Time.unscaledTime;
            if (changed)
            {
                string who = Peers.Count == 0 ? "nobody" : string.Join(", ", Peers);
                string runs = Peers.Count == 0 ? "alone" : MyId == AuthorityId ? "running the enemies" : $"{AuthorityId} runs the enemies";
                Plugin.Log.LogInfo($"KillNet: in this level with {who} (I'm {MyId}, {runs})");
            }
        }

        // Java's UUID (most/least significant halves) as a Guid of the same 16 bytes, in order.
        private static Guid ToGuid(long msb, long lsb)
        {
            var b = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                b[i] = (byte)(msb >> (56 - 8 * i));
                b[8 + i] = (byte)(lsb >> (56 - 8 * i));
            }
            return new Guid(b);
        }

        public static void WriteUuid(BinaryWriter w, Guid g)
        {
            byte[] b = g.ToByteArray();
            long msb = 0, lsb = 0;
            for (int i = 0; i < 8; i++)
            {
                msb = msb << 8 | b[i];
                lsb = lsb << 8 | b[8 + i];
            }
            w.Write(msb);
            w.Write(lsb);
        }
    }
}
