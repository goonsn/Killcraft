using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Killcraft
{
    // Pretend lag and loss on what the other players say of their V1 (Debug/SimLatencyMs and
    // Debug/SimLossPercent), to try two Killcrafts on one PC as if they were far apart. Only their
    // PlayerState and Moves (what their body goes by); a held message is delivered when its lag is
    // up, each a little sooner or later than the next (so some come out of order, as they can).
    internal static class NetSim
    {
        private struct Held
        {
            public float Due;
            public int From;
            public byte Type;
            public byte[] Data;
        }

        private static readonly List<Held> held = new List<Held>();

        public static bool Active => Plugin.SimLatencyMs != null && (Plugin.SimLatencyMs.Value > 0 || Plugin.SimLossPercent.Value > 0f);

        public static int Waiting => held.Count;

        // True: taken (dropped, or kept for later); the reader is then spent.
        public static bool Hold(int from, byte type, BinaryReader r)
        {
            if (!Active)
            {
                return false;
            }
            if (Plugin.SimLossPercent.Value > 0f && Random.value * 100f < Plugin.SimLossPercent.Value)
            {
                return true;
            }
            int n = (int)(r.BaseStream.Length - r.BaseStream.Position);
            byte[] data = r.ReadBytes(n);
            float ms = Plugin.SimLatencyMs.Value * Random.Range(0.85f, 1.15f);
            held.Add(new Held { Due = Time.unscaledTime + ms / 1000f, From = from, Type = type, Data = data });
            return true;
        }

        public static void Frame()
        {
            if (held.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            held.Sort((a, b) => a.Due.CompareTo(b.Due));
            int i = 0;
            for (; i < held.Count && held[i].Due <= now; i++)
            {
                Held h = held[i];
                using (var rd = new BinaryReader(new MemoryStream(h.Data)))
                {
                    RemotePlayers.Deliver(h.From, h.Type, rd);
                }
            }
            held.RemoveRange(0, i);
        }
    }
}
