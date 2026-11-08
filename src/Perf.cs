using System;
using System.Diagnostics;
using UnityEngine;

namespace Killcraft
{
    // How smooth the game runs, in the log every 30 seconds in a level: frames a second, how many
    // frames took long (a stutter), how long Killcraft's own work takes each frame and at worst, and
    // how much memory it all makes for the garbage collector (whose pauses are stutters too).
    internal static class Perf
    {
        private static readonly Stopwatch watch = new Stopwatch();
        private static long startTicks;
        private static double ourMs, ourWorst, frameWorst, ourInWorst;
        private static int frames, over33, over50, gcAt = -1;
        private static long memAt;
        private static float since;
        private static double ourThisFrame;

        // Each part of Killcraft's work: all its time in the 30 seconds, and its longest once.
        private sealed class Part
        {
            public double Total, Worst;
        }

        private static readonly System.Collections.Generic.Dictionary<string, Part> parts = new System.Collections.Generic.Dictionary<string, Part>();
        private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Part>> sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Part>>();

        public static long Now()
        {
            if (!watch.IsRunning)
            {
                watch.Start();
            }
            return watch.ElapsedTicks;
        }

        public static void Add(string name, long from)
        {
            double ms = (watch.ElapsedTicks - from) * 1000.0 / Stopwatch.Frequency;
            if (!parts.TryGetValue(name, out Part p))
            {
                parts[name] = p = new Part();
            }
            p.Total += ms;
            if (ms > p.Worst)
            {
                p.Worst = ms;
            }
        }

        public static void Begin()
        {
            if (!watch.IsRunning)
            {
                watch.Start();
            }
            startTicks = watch.ElapsedTicks;
        }

        // late: LateUpdate's (the frame's last of Killcraft's own work).
        public static void End(bool late)
        {
            double ms = (watch.ElapsedTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
            ourThisFrame += ms;
            if (!late)
            {
                return;
            }
            float dt = Time.unscaledDeltaTime;
            double frameMs = dt * 1000.0;
            frames++;
            ourMs += ourThisFrame;
            ourWorst = Math.Max(ourWorst, ourThisFrame);
            if (frameMs > frameWorst)
            {
                frameWorst = frameMs;
                ourInWorst = ourThisFrame;
            }
            if (frameMs > 33.3)
            {
                over33++;
            }
            if (frameMs > 50)
            {
                over50++;
            }
            ourThisFrame = 0;
            since += dt;
            if (since < 30f)
            {
                return;
            }
            int gc = GC.CollectionCount(0);
            long mem = GC.GetTotalMemory(false);
            if (gcAt >= 0 && SceneHelper.CurrentScene != null && SceneHelper.CurrentScene != "Main Menu")
            {
                Plugin.Log.LogInfo($"perf: {frames / since:0} fps; {over33} frames over 33 ms, {over50} over 50 ms, the slowest {frameWorst:0} ms "
                    + $"(Killcraft's part of it {ourInWorst:0.0} ms); Killcraft's work {ourMs / Math.Max(frames, 1):0.00} ms a frame, at most {ourWorst:0.0} ms; "
                    + $"{gc - gcAt} garbage collections, memory {mem / 1048576.0:0} MB ({(mem - memAt) / 1048576.0 / since:+0.0;-0.0} MB/s)");
                sorted.Clear();
                sorted.AddRange(parts);
                sorted.Sort((a, b) => b.Value.Total.CompareTo(a.Value.Total));
                var line = new System.Text.StringBuilder("perf: Killcraft's slowest parts (a frame, at most):");
                for (int i = 0; i < sorted.Count && i < 6; i++)
                {
                    line.Append($" {sorted[i].Key} {sorted[i].Value.Total / Math.Max(frames, 1):0.00} ms ({sorted[i].Value.Worst:0.0}),");
                }
                sorted.Sort((a, b) => b.Value.Worst.CompareTo(a.Value.Worst));
                line.Append(" longest once:");
                for (int i = 0; i < sorted.Count && i < 3; i++)
                {
                    line.Append($" {sorted[i].Key} {sorted[i].Value.Worst:0.0} ms,");
                }
                Plugin.Log.LogInfo(line.ToString().TrimEnd(','));
            }
            parts.Clear();
            gcAt = gc;
            memAt = mem;
            since = 0f;
            frames = over33 = over50 = 0;
            ourMs = ourWorst = frameWorst = ourInWorst = 0;
        }
    }
}
