using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace KillNetLauncher
{
    // The background's wires and blocks (Art/wires.txt, written by design/art.py with bg.png), for the
    // packets the launcher sends along them: each wire runs from the block it leaves to the one it goes into.
    internal sealed class Wires
    {
        internal sealed class Wire
        {
            public PointF[] P;
            public float[] Cum;   // (the length up to each point)
            public float Len;
            public RectangleF? End;  // (the block it goes into, if any)
        }

        public readonly List<Wire> All = new List<Wire>();

        public Wires(Stream s, int tall)
        {
            var boxes = new List<RectangleF>();
            var raw = new List<PointF[]>();
            using (var r = new StreamReader(s))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    string[] p = line.Trim().Split(' ');
                    if (p[0] == "W" && p.Length >= 3)
                    {
                        var pts = new PointF[p.Length - 1];
                        for (int i = 1; i < p.Length; i++)
                        {
                            string[] xy = p[i].Split(',');
                            pts[i - 1] = new PointF(F(xy[0]), F(xy[1]));
                        }
                        raw.Add(pts);
                    }
                    else if (p[0] == "B" && p.Length == 5)
                    {
                        boxes.Add(RectangleF.FromLTRB(F(p[1]), F(p[2]), F(p[3]), F(p[4])));
                    }
                }
            }
            foreach (PointF[] pts in raw)
            {
                var w = new Wire { P = pts, Cum = new float[pts.Length] };
                for (int i = 1; i < pts.Length; i++)
                {
                    w.Cum[i] = w.Cum[i - 1] + Dist(pts[i - 1], pts[i]);
                }
                w.Len = w.Cum[pts.Length - 1];
                PointF e = pts[pts.Length - 1];
                foreach (RectangleF b in boxes)
                {
                    foreach (int dy in new[] { -tall, 0, tall })
                    {
                        var rb = new RectangleF(b.X - 4, b.Y + dy - 4, b.Width + 8, b.Height + 8);
                        if (rb.Contains(e))
                        {
                            w.End = new RectangleF(b.X, b.Y + dy, b.Width, b.Height);
                        }
                    }
                }
                if (w.Len > 1f)
                {
                    All.Add(w);
                }
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // The point that far along the wire.
        public static PointF At(Wire w, float d)
        {
            d = Math.Max(0f, Math.Min(w.Len, d));
            for (int i = 1; i < w.P.Length; i++)
            {
                if (d <= w.Cum[i])
                {
                    float seg = w.Cum[i] - w.Cum[i - 1];
                    float f = seg <= 0f ? 0f : (d - w.Cum[i - 1]) / seg;
                    return new PointF(w.P[i - 1].X + (w.P[i].X - w.P[i - 1].X) * f, w.P[i - 1].Y + (w.P[i].Y - w.P[i - 1].Y) * f);
                }
            }
            return w.P[w.P.Length - 1];
        }

        // The stretch of the wire from d0 to d1, as points.
        public static PointF[] Part(Wire w, float d0, float d1)
        {
            d0 = Math.Max(0f, d0);
            d1 = Math.Min(w.Len, d1);
            var pts = new List<PointF> { At(w, d0) };
            for (int i = 1; i < w.P.Length - 1; i++)
            {
                if (w.Cum[i] > d0 && w.Cum[i] < d1)
                {
                    pts.Add(w.P[i]);
                }
            }
            pts.Add(At(w, d1));
            return pts.ToArray();
        }
    }
}
