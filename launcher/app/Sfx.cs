using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KillNetLauncher
{
    // The launcher's sounds, made here (square waves, noise, thumps: nothing taken from any game) and played
    // with winmm's PlaySound (one at a time: a new one cuts the last, which suits menu blips).
    internal static class Sfx
    {
        private const int Rate = 44100;

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

        private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

        public static bool On = true;

        // (Before the sounds: they're made with it, in this order.)
        private static readonly Random rnd = new Random(5);

        // (Kept alive: PlaySound reads them while they play.)
        public static readonly byte[] Hover = Make(0.035, (t, p) => Square(1760, t) * Env(p, 0.002, 6) * 0.16);
        public static readonly byte[] Click = Make(0.11, (t, p) => Square(p < 0.4 ? 880 : 1320, t) * Env(p, 0.002, 3) * 0.2);
        public static readonly byte[] Back = Make(0.11, (t, p) => Square(p < 0.4 ? 1320 : 660, t) * Env(p, 0.002, 3) * 0.2);
        public static readonly byte[] Tick = Make(0.018, (t, p) => Square(2400, t) * Env(p, 0.001, 8) * 0.1);
        public static readonly byte[] Done = Make(0.16, (t, p) => Square(p < 0.33 ? 660 : p < 0.66 ? 990 : 1320, t) * Env(p, 0.002, 2) * 0.16);
        public static readonly byte[] PowerOn = Make(0.32, (t, p) => (Noise() * 0.5 * Env(p, 0.001, 14) + Math.Sin(2 * Math.PI * (60 + 3000 * p * p) * t) * 0.3 * Env(p, 0.01, 3)) * 0.7);
        public static readonly byte[] PowerOff = Make(0.3, (t, p) => Math.Sin(2 * Math.PI * (2400 - 2300 * Math.Sqrt(p)) * t) * 0.22 * (1 - p) + Noise() * 0.12 * Env(p, 0.001, 10));
        public static readonly byte[] Slam = Make(0.55, (t, p) => (Math.Sin(2 * Math.PI * (90 - 50 * p) * t) * 0.9 + Noise() * Env(p, 0.001, 18) * 0.8) * Env(p, 0.002, 5) * 0.75);
        public static readonly byte[] Glitch = Make(0.24, (t, p) => (Noise() * (((int)(p * 16)) % 2 == 0 ? 0.55 : 0.12) + Square(p < 0.5 ? 330 : 990, t) * 0.25) * Env(p, 0.001, 2.5) * 0.55);
        public static readonly byte[] Error = Make(0.2, (t, p) => Square(p < 0.5 ? 220 : 165, t) * Env(p, 0.002, 2) * 0.2);

        public static void Play(byte[] wav)
        {
            if (!On)
            {
                return;
            }
            try
            {
                PlaySound(wav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
            }
            catch (Exception)
            {
            }
        }

        private static double Noise() => rnd.NextDouble() * 2 - 1;

        private static double Square(double hz, double t) => (t * hz) % 1.0 < 0.5 ? 1 : -1;

        // An attack (seconds) then an exponential fall (over the sound's length p: 0..1).
        private static double Env(double p, double attack, double fall) => Math.Min(1, p / Math.Max(1e-6, attack * 10)) * Math.Exp(-fall * p);

        // A 16-bit mono WAV of f(time, 0..1) over that long.
        private static byte[] Make(double seconds, Func<double, double, double> f)
        {
            int n = (int)(seconds * Rate);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + n * 2);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Rate);
                w.Write(Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(n * 2);
                for (int i = 0; i < n; i++)
                {
                    double p = i / (double)n;
                    // (A few milliseconds faded at the end: no click.)
                    double tail = Math.Min(1, (n - i) / (Rate * 0.004));
                    double v = Math.Max(-1, Math.Min(1, f(i / (double)Rate, p) * tail));
                    w.Write((short)(v * 32000));
                }
                return ms.ToArray();
            }
        }
    }
}
