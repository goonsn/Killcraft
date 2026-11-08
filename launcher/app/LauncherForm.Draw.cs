using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace KillNetLauncher
{
    // The drawing. Bottom up: the block diagram scrolling up (bg.png, it loops) with packets running along
    // its wires, the figure (floating), the logo, the status lines and the buttons; over all of it the
    // screen's frame and faint scanlines. Laid out on a 1920x1080 page, scaled to the window.
    //
    // The intro, as an old machine's screen coming on: the picture switching on (a line, then the screen),
    // a boot log of what it really finds (ULTRAKILL, BepInEx, the mod, Steam, the update check) with a
    // loading bar; then a hard cut: the logo slams in (the screen shaking), a scan line draws the picture
    // in from the top, the menu slides in. Any key or click skips it. Closing switches the screen off.
    internal sealed partial class LauncherForm
    {
        private const float ScrollSpeed = 26f;  // (page pixels a second)
        private static readonly Color Ink = Color.FromArgb(205, 205, 205);
        private static readonly Color Dim = Color.FromArgb(110, 110, 110);
        private static readonly Color Faint = Color.FromArgb(55, 55, 55);
        private static readonly Color Red = Color.FromArgb(255, 40, 40);

        // ---- the intro's clock (seconds since the window opened) ----

        private const float PowerOnEnd = 0.38f, LineStart = 0.45f, LineEvery = 0.15f, CloseTime = 0.34f;
        private const int BootRows = 6;

        private static float LineAt(int i) => LineStart + i * LineEvery;
        private static float ResultAt(int i) => LineAt(i) + 0.07f;
        private const float UpdateTimeout = 2.5f;  // (the menu doesn't wait longer than that for the update check)
        private float UpdateShownAt => Math.Max(ResultAt(BootRows - 1), Math.Min(updateKnownAt, LineAt(BootRows - 1) + UpdateTimeout));
        private float EnterAt => UpdateShownAt + 0.12f;
        // The cut from the boot log to the logo (sooner when skipped: everything's done by then).
        private float Cut => skipAt >= 0f ? Math.Min(EnterAt + 0.4f, skipAt - 1.3f) : EnterAt + 0.4f;
        private float MenuAt => Cut + 0.6f;
        private float ShownAt => float.IsNaN(shownAt) ? MenuAt : shownAt;
        private float LinesAt => float.IsNaN(linesAt) ? MenuAt : linesAt;

        private static float Ease(float p)
        {
            p = Math.Max(0f, Math.Min(1f, p));
            return 1f - (1f - p) * (1f - p) * (1f - p);
        }

        private static float Clamp01(float p) => Math.Max(0f, Math.Min(1f, p));

        // How far a button is slid off to the left now (0: in place).
        private float SlideIn(int i) => -(BX + BW + 60) * (1f - Ease((T - ShownAt - i * 0.055f) / 0.38f));
        private float SlideOut(int i) => -(BX + BW + 60) * Ease((T - leftAt - i * 0.035f) / 0.24f);
        private bool Settled => T - ShownAt > 0.38f + buttons.Count * 0.055f;

        // ---- the layers, scaled to the window (made again when its size changes) ----

        private Size scaledFor;
        private Bitmap bgS, overS, back;
        private readonly Bitmap[] figuresS = new Bitmap[2];
        private readonly Bitmap[] logosS = new Bitmap[2];
        private Bitmap logoS => logosS[BrandIndex];

        private void Scale(int cw, int ch)
        {
            if (scaledFor == new Size(cw, ch) && bgS != null)
            {
                return;
            }
            scaledFor = new Size(cw, ch);
            bgS?.Dispose();
            logosS[0]?.Dispose();
            logosS[1]?.Dispose();
            figuresS[0]?.Dispose();
            figuresS[1]?.Dispose();
            overS?.Dispose();
            float s = cw / (float)W;
            bgS = Scaled(bg, cw, Math.Max(1, (int)Math.Round(bg.Height * s)));
            logosS[0] = Scaled(logos[0], cw, ch);
            logosS[1] = Scaled(logos[1], cw, ch);
            figuresS[0] = Scaled(figures[0], cw, ch);
            figuresS[1] = Scaled(figures[1], cw, ch);
            // Over everything: scanlines (every third row a little darker), the frame (black outside its
            // rounded corners, a white line).
            overS = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(overS))
            {
                using (var line = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
                {
                    for (int y = 0; y < ch; y += 3)
                    {
                        g.FillRectangle(line, 0, y, cw, 1);
                    }
                }
                g.ScaleTransform(s, s);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath edge = Rounded(new RectangleF(6, 6, W - 13, H - 13), 22))
                using (var outside = new Region(new RectangleF(-10, -10, W + 20, H + 20)))
                using (var black = new SolidBrush(Color.Black))
                using (var pen = new Pen(Color.White, 5))
                {
                    outside.Exclude(edge);
                    g.FillRegion(black, outside);
                    g.DrawPath(pen, edge);
                }
            }
        }

        private static Bitmap Scaled(Bitmap b, int w, int h)
        {
            var o = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(o))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(b, new Rectangle(0, 0, w, h));
            }
            return o;
        }

        private static GraphicsPath Chamfer(RectangleF r, float c)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(r.X + c, r.Y), new PointF(r.Right - c, r.Y), new PointF(r.Right, r.Y + c), new PointF(r.Right, r.Bottom - c),
                new PointF(r.Right - c, r.Bottom), new PointF(r.X + c, r.Bottom), new PointF(r.X, r.Bottom - c), new PointF(r.X, r.Y + c),
            });
            return p;
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private SizeF Measure(Graphics g, string s, Font f) => g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);

        private new void Text(Graphics g, string s, float x, float y, Color c, Font f = null)
        {
            using (var b = new SolidBrush(c))
            {
                g.DrawString(s, f ?? small, b, x, y, StringFormat.GenericTypographic);
            }
        }

        // A terminal line (black behind it), typed out from `from` (seconds), the cursor after it while typing.
        private void Term(Graphics g, string line, float x, float y, Color color, float from = -100f, float cps = 70f, bool cursor = false)
        {
            if (T < from)
            {
                return;
            }
            int n = line.Length;
            if (T < from + n / cps)
            {
                n = Math.Max(0, (int)((T - from) * cps));
                cursor = true;
            }
            string shown = line.Substring(0, n) + (cursor && ((int)(T * 2f) % 2 == 0 || n < line.Length) ? "_" : "");
            if (shown.Length == 0)
            {
                return;
            }
            SizeF size = Measure(g, shown, small);
            using (var b = new SolidBrush(Color.Black))
            {
                g.FillRectangle(b, x - 2, y - 2, size.Width + 4, size.Height + 2);
            }
            Text(g, shown, x, y, color);
        }

        // ---- a frame ----

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                Compose(e.Graphics, ClientSize.Width, ClientSize.Height);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        // The frame, or (closing) the frame squeezed into a line, then a dot, as an old screen switching off.
        private void Compose(Graphics g, int cw, int ch)
        {
            if (closingAt < 0f)
            {
                Draw(g, cw, ch);
                return;
            }
            if (back == null || back.Size != new Size(cw, ch))
            {
                back?.Dispose();
                back = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            }
            using (Graphics bg2 = Graphics.FromImage(back))
            {
                Draw(bg2, cw, ch);
            }
            float p = Clamp01((T - closingAt) / CloseTime);
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            if (p < 0.5f)
            {
                float q = p / 0.5f;
                int h = Math.Max(3, (int)(ch * (1f - q * q)));
                var dest = new Rectangle(0, (ch - h) / 2, cw, h);
                g.DrawImage(back, dest);
                using (var w = new SolidBrush(Color.FromArgb((int)(q * 230), 255, 255, 255)))
                {
                    g.FillRectangle(w, dest);
                }
            }
            else if (p < 0.85f)
            {
                float q = (p - 0.5f) / 0.35f;
                int w = Math.Max(4, (int)(cw * (1f - Ease(q))));
                g.FillRectangle(Brushes.White, (cw - w) / 2, ch / 2 - 1, w, 3);
            }
            else
            {
                int a = (int)(255 * (1f - (p - 0.85f) / 0.15f));
                using (var w = new SolidBrush(Color.FromArgb(Math.Max(0, a), 255, 255, 255)))
                {
                    g.FillRectangle(w, cw / 2 - 3, ch / 2 - 3, 6, 6);
                }
            }
        }

        private float prevT = -1f;

        private bool Crossed(float at) => prevT < at && at <= T;

        // The intro's sounds, as its moments go by (none when skipped).
        private void Sounds()
        {
            if (forRender)
            {
                return;
            }
            if (skipAt < 0f)
            {
                if (Crossed(0.02f))
                {
                    Sfx.Play(Sfx.PowerOn);
                }
                for (int i = 0; i < BootRows - 1; i++)
                {
                    if (Crossed(ResultAt(i)))
                    {
                        Sfx.Play(Sfx.Tick);
                    }
                }
                if (Crossed(UpdateShownAt))
                {
                    Sfx.Play(Sfx.Tick);
                }
                if (Crossed(EnterAt))
                {
                    Sfx.Play(Sfx.Done);
                }
                if (Crossed(Cut + 0.24f))
                {
                    Sfx.Play(Sfx.Slam);
                }
            }
            prevT = T;
        }

        private void Draw(Graphics g, int cw, int ch)
        {
            Scale(cw, ch);
            float s = cw / (float)W, t = T;
            Sounds();
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = s >= 0.99f ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAlias;
            g.Clear(Color.Black);

            if (t < Cut)
            {
                if (t >= PowerOnEnd * 0.6f)
                {
                    g.ScaleTransform(s, s);
                    DrawBoot(g, t);
                    g.ResetTransform();
                }
                g.DrawImageUnscaled(overS, 0, 0);
                DrawPowerOn(g, t, cw, ch);
                return;
            }

            float c = t - Cut;
            if (c < 0.06f)
            {
                // (The cut: the screen white for a moment.)
                g.Clear(Color.White);
                g.DrawImageUnscaled(overS, 0, 0);
                return;
            }
            // The logo's landing shakes the picture.
            int dx = 0, dy = 0;
            if (c >= 0.24f && c < 0.8f)
            {
                float amp = 18f * s * (float)Math.Exp(-(c - 0.24f) * 8f);
                var shake = new Random((int)(c * 60f) * 31 + 7);
                dx = (int)Math.Round((shake.NextDouble() * 2 - 1) * amp);
                dy = (int)Math.Round((shake.NextDouble() * 2 - 1) * amp);
            }

            // The picture, drawn in by a scan line coming down.
            float wipe = c < 0.3f ? 0f : Ease((c - 0.3f) / 0.5f);
            if (wipe > 0f)
            {
                int wy = (int)(wipe * ch);
                g.SetClip(new Rectangle(0, 0, cw, wy));
                g.TranslateTransform(dx, dy);
                int off = (int)(t * ScrollSpeed * s) % bgS.Height;
                g.DrawImageUnscaled(bgS, 0, -off);
                if (bgS.Height - off < ch + 40)
                {
                    g.DrawImageUnscaled(bgS, 0, bgS.Height - off);
                }
                g.ScaleTransform(s, s);
                DrawPackets(g, t, off / s);
                g.ResetTransform();
                int bob = (int)Math.Round(Math.Sin(t * 1.3) * 7f * s);
                DrawFigure(g, cw, s, dx, dy + bob);
                g.ResetClip();
                if (wipe < 1f)
                {
                    g.FillRectangle(Brushes.White, 0, wy - Math.Max(2, (int)(3 * s)), cw, Math.Max(2, (int)(3 * s)));
                    using (var f = new SolidBrush(Dim))
                    {
                        g.FillRectangle(f, 0, wy - (int)(14 * s), cw, Math.Max(1, (int)(2 * s)));
                    }
                }
            }

            // The logo: slamming down (big, growing smaller fast), then still after a glitch.
            if (c < 0.24f)
            {
                float p = Clamp01((c - 0.06f) / 0.18f);
                float k = 1.45f - 0.45f * p * p;
                float cx = logoBox.X + logoBox.Width / 2f, cy = logoBox.Y + logoBox.Height / 2f;
                g.ScaleTransform(s, s);
                g.DrawImage(logo, new RectangleF(cx - logoBox.Width * k / 2f, cy - logoBox.Height * k / 2f, logoBox.Width * k, logoBox.Height * k),
                    logoBox, GraphicsUnit.Pixel);
                g.ResetTransform();
            }
            else if (c < 0.42f)
            {
                float left = 1f - (c - 0.24f) / 0.18f;
                var rnd = new Random((int)((c - 0.24f) / 0.035f) * 7919 + 13);
                int top = (int)((logoBox.Y - 8) * s), bottom = (int)((logoBox.Bottom + 8) * s), bands = 7;
                for (int i = 0; i < bands; i++)
                {
                    int y0 = top + (bottom - top) * i / bands, y1 = top + (bottom - top) * (i + 1) / bands;
                    int ox = (int)((rnd.NextDouble() * 2 - 1) * 46 * s * left);
                    g.DrawImage(logoS, new Rectangle(dx + ox, dy + y0, cw, y1 - y0), new Rectangle(0, y0, cw, y1 - y0), GraphicsUnit.Pixel);
                }
            }
            else
            {
                DrawLogo(g, cw, s, dx, dy);
            }
            if (c >= 0.24f && c < 0.36f)
            {
                using (var flash = new SolidBrush(Color.FromArgb((int)((1f - (c - 0.24f) / 0.12f) * 90), 255, 255, 255)))
                {
                    g.FillRectangle(flash, 0, 0, cw, ch);
                }
            }

            g.TranslateTransform(dx, dy);
            g.ScaleTransform(s, s);
            DrawMenu(g, t);
            g.ResetTransform();
            g.DrawImageUnscaled(overS, 0, 0);
        }

        // ---- the intro's parts ----

        // The screen switching on: a line across the middle, opening into a white screen that fades.
        private static void DrawPowerOn(Graphics g, float t, int cw, int ch)
        {
            if (t >= PowerOnEnd)
            {
                return;
            }
            float p = t / PowerOnEnd;
            if (p < 0.3f)
            {
                float q = Ease(p / 0.3f);
                int w = (int)(cw * 0.92f * q);
                g.FillRectangle(Brushes.White, (cw - w) / 2, ch / 2 - 1, Math.Max(2, w), 3);
            }
            else if (p < 0.55f)
            {
                float q = Ease((p - 0.3f) / 0.25f);
                int h = Math.Max(3, (int)(ch * q));
                g.FillRectangle(Brushes.White, 0, (ch - h) / 2, cw, h);
            }
            else
            {
                int a = (int)(255 * (1f - (p - 0.55f) / 0.45f));
                using (var w = new SolidBrush(Color.FromArgb(Math.Max(0, a), 255, 255, 255)))
                {
                    g.FillRectangle(w, 0, 0, cw, ch);
                }
            }
        }

        private (string label, string value, Color color) BootRow(int i)
        {
            bool found = Installer.IsGame(settings.GamePath);
            switch (i)
            {
                case 0:
                    return ("ULTRAKILL", found ? "FOUND" : "NOT FOUND", found ? Color.White : Red);
                case 1:
                    return ("BEPINEX", bepinex != null ? Short(bepinex) : found ? "NOT INSTALLED" : "-", bepinex != null ? Color.White : Dim);
                case 2:
                    return (BrandName, mod == null ? "NOT INSTALLED" : mod.Length == 0 ? "TEST BUILD" : mod, mod == null ? Dim : Color.White);
                case 3:
                    return ("MINECRAFT", settings.Minecraft ? (hasMcParts || mod == null ? "ON" : "ON - NOT DOWNLOADED") : "OFF", Color.White);
                case 4:
                    return ("STEAM", steamUp ? "RUNNING" : "NOT RUNNING", steamUp ? Color.White : Dim);
                default:
                    if (T >= updateKnownAt)
                    {
                        return ("UPDATES", updateResult ?? "-", updateColor);
                    }
                    if (T >= LineAt(BootRows - 1) + UpdateTimeout)
                    {
                        return ("UPDATES", "STILL CHECKING", Dim);
                    }
                    return ("UPDATES", "CHECKING " + "|/-\\"[(int)(T * 12f) % 4], Dim);
            }
        }

        // The boot log: what it finds, row by row, and a loading bar.
        private void DrawBoot(Graphics g, float t)
        {
            const float X = 130, RuleW = 1660, ValueX = 720;
            Text(g, "KILLNET LAUNCHER " + LauncherVersion, X, 104, Color.White);
            SizeF tw = Measure(g, "KILLNET LAUNCHER " + LauncherVersion, small);
            if ((int)(t * 2.5f) % 2 == 0 && t < LineAt(0))
            {
                Text(g, "_", X + tw.Width + 4, 104, Color.White);
            }
            using (var faint = new SolidBrush(Faint))
            {
                g.FillRectangle(faint, X, 148, RuleW * Ease((t - 0.3f) / 0.3f), 3);
            }
            int shown = 0;
            for (int i = 0; i < BootRows; i++)
            {
                if (t < LineAt(i))
                {
                    continue;
                }
                var (label, value, color) = BootRow(i);
                float y = 190 + i * 46;
                int n = Math.Min(label.Length, (int)((t - LineAt(i)) * 260f) + 1);
                Text(g, label.Substring(0, n), X, y, Ink);
                // (Dots leading to the value.)
                float from = X + Measure(g, label, small).Width + 16;
                int dots = (int)((ValueX - 20 - from) / 14);
                int dn = Math.Min(dots, (int)((t - LineAt(i) - 0.02f) * 900f));
                using (var dim = new SolidBrush(Faint))
                {
                    for (int d = 0; d < dn; d++)
                    {
                        g.FillRectangle(dim, from + d * 14, y + 19, 4, 4);
                    }
                }
                bool done = i < BootRows - 1 ? t >= ResultAt(i) : true;
                if (done)
                {
                    Text(g, value, ValueX, y, color);
                    if (i < BootRows - 1 || t >= updateKnownAt)
                    {
                        shown++;
                    }
                }
            }
            float barY = 960;
            if (t >= EnterAt)
            {
                Text(g, "> ENTERING KILLNET" + ((int)(t * 6f) % 2 == 0 ? "_" : ""), X, 190 + BootRows * 46 + 30, Color.White);
            }
            // The loading bar: what's been found so far.
            float pct = t >= EnterAt ? 1f : shown / (float)(BootRows + 1);
            if (t >= LineAt(0))
            {
                Text(g, "LOADING", X, barY - 40, Dim);
                string ps = (int)(pct * 100) + "%";
                Text(g, ps, X + RuleW - Measure(g, ps, small).Width, barY - 40, Dim);
                const int Segs = 40;
                float sw = RuleW / Segs;
                int on = (int)Math.Round(pct * Segs);
                using (var lit = new SolidBrush(Color.White))
                using (var off = new SolidBrush(Faint))
                {
                    for (int k = 0; k < Segs; k++)
                    {
                        g.FillRectangle(k < on ? lit : off, X + k * sw, barY, sw - 7, 18);
                    }
                }
            }
        }

        // Packets running along the diagram's wires (a bright head, its wire lit behind it), the block it
        // reaches flashing. Worked out from the time alone (the same picture for the same moment).
        private void DrawPackets(Graphics g, float t, float offPage)
        {
            if (wires.All.Count == 0)
            {
                return;
            }
            int tall = bg.Height;
            const int Slots = 7;
            const float Period = 2.7f;
            using (var lit = new Pen(Color.FromArgb(150, 150, 150), 2))
            using (var head = new SolidBrush(Color.White))
            using (var trail = new SolidBrush(Color.FromArgb(170, 170, 170)))
            {
                for (int k = 0; k < Slots; k++)
                {
                    float tk = t + k * 0.91f;
                    int j = (int)Math.Floor(tk / Period);
                    float u = tk - j * Period;
                    uint h = (uint)(k * 73856093) ^ (uint)(j * 19349663);
                    h ^= h >> 13;
                    h *= 0x5bd1e995;
                    h ^= h >> 15;
                    Wires.Wire w = wires.All[(int)(h % (uint)wires.All.Count)];
                    float speed = Math.Max(520f, w.Len / 1.9f);
                    float d = u * speed;
                    PointF at = Wires.At(w, Math.Min(d, w.Len));
                    // (Where the wire is on the screen now: the picture scrolls, and loops.)
                    float yy = ((at.Y - offPage) % tall + tall) % tall;
                    if (yy > tall - 200)
                    {
                        yy -= tall;
                    }
                    float shift = yy - at.Y;
                    if (yy < -250 || yy > H + 250)
                    {
                        continue;
                    }
                    if (d < w.Len)
                    {
                        PointF[] part = Wires.Part(w, d - 220f, d);
                        for (int i = 0; i < part.Length; i++)
                        {
                            part[i].Y += shift;
                        }
                        if (part.Length >= 2)
                        {
                            g.DrawLines(lit, part);
                        }
                        for (int b = 2; b >= 1; b--)
                        {
                            PointF p = Wires.At(w, d - b * 13f);
                            float sz = 8 - b * 2;
                            g.FillRectangle(trail, p.X - sz / 2, p.Y + shift - sz / 2, sz, sz);
                        }
                        g.FillRectangle(head, at.X - 5, at.Y + shift - 5, 10, 10);
                    }
                    else if (w.End.HasValue)
                    {
                        float a = 1f - (d - w.Len) / speed / 0.45f;
                        if (a > 0f)
                        {
                            RectangleF r = w.End.Value;
                            using (var pen = new Pen(Color.FromArgb((int)(a * 255), 255, 255, 255), 3))
                            {
                                g.DrawRectangle(pen, r.X, r.Y + shift, r.Width, r.Height);
                            }
                        }
                    }
                }
            }
        }

        // ---- the menu ----

        private void DrawMenu(Graphics g, float t)
        {
            // The status lines, as a terminal under the logo, typed out as the menu comes in.
            Term(g, Header(), 130, 261, Ink, LinesAt, 80f);
            string[] lines = Lines();
            for (int i = 0; i < lines.Length; i++)
            {
                bool last = i == lines.Length - 1;
                float from = LinesAt + 0.12f * (i + 1);
                if (last && !options && statusAt > from)
                {
                    from = statusAt;  // (a new status: typed out anew)
                }
                bool error = last && status.StartsWith("ERROR");
                bool cursor = last && !options && (busy || checking || !lines[i].EndsWith("..."));
                Term(g, lines[i], 130, 322 + i * 30, error ? Red : Ink, from, last ? 110f : 80f, cursor);
            }

            // The buttons (and those leaving).
            float dt = prevDraw < 0f ? 0f : Math.Max(0f, Math.Min(0.1f, t - prevDraw));
            prevDraw = t;
            if (leaving != null && t - leftAt < 0.24f + leaving.Count * 0.035f + 0.05f)
            {
                for (int i = 0; i < leaving.Count; i++)
                {
                    DrawButton(g, leaving[i], SlideOut(i), false);
                }
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                Btn b = buttons[i];
                bool on = b.Enabled();
                float target = on && i == selected && Settled && closingAt < 0f ? 1f : 0f;
                b.Hv = target;  // (filled at once under the mouse, as ULTRAKILL's)
                DrawButton(g, b, SlideIn(i), on && i == pressed);
            }

            // The links, the version.
            int ly = BY + 4 * (BH + Gap) - Gap + 22;
            float linkFrom = MenuAt + 0.4f;
            Term(g, "INIT SOCIALS...OK", 130, ly, Ink, linkFrom, 80f);
            SizeF gs = Measure(g, "GITHUB", small), ds = Measure(g, "DISCORD", small);
            githubR = new Rectangle(130, ly + 30, (int)gs.Width, (int)gs.Height);
            discordR = new Rectangle(130 + (int)gs.Width + 34, ly + 30, (int)ds.Width, (int)ds.Height);
            Term(g, "GITHUB", githubR.X, githubR.Y, linkHover == 0 ? Color.White : Ink, linkFrom + 0.25f, 80f);
            Term(g, "DISCORD", discordR.X, discordR.Y, linkHover == 1 ? Color.White : Ink, linkFrom + 0.35f, 80f);
            if (linkHover >= 0)
            {
                Rectangle r = linkHover == 0 ? githubR : discordR;
                g.FillRectangle(Brushes.White, r.X, r.Bottom + 1, r.Width, 3);
            }
            DrawNews(g, t);
            string ver = "LAUNCHER " + LauncherVersion;
            Term(g, ver, W - 130 - Measure(g, ver, small).Width, H - 82, Dim, linkFrom + 0.45f, 80f);
        }

        private float prevDraw = -1f;

        // The logo, still; or (MINECRAFT just switched) glitching from one name to the other: bands of the
        // old one jumping sideways more and more, then the new one's settling.
        private void DrawLogo(Graphics g, int cw, float s, int dx, int dy)
        {
            float b = T - brandAt;
            if (b >= 0.34f || (fixedTime >= 0f && !fixedSwitch))
            {
                g.DrawImageUnscaled(logoS, dx, dy);
                return;
            }
            int from = b < 0.17f ? brandFrom : BrandIndex;
            float amt = b < 0.17f ? b / 0.17f : 1f - (b - 0.17f) / 0.17f;
            Rectangle a = logoBoxes[0], c = logoBoxes[1];
            int top = (int)((Math.Min(a.Y, c.Y) - 8) * s), bottom = (int)((Math.Max(a.Bottom, c.Bottom) + 8) * s), bands = 9;
            var rnd = new Random((int)(b / 0.03f) * 7919 + 101);
            for (int i = 0; i < bands; i++)
            {
                int y0 = top + (bottom - top) * i / bands, y1 = top + (bottom - top) * (i + 1) / bands;
                if (rnd.NextDouble() < 0.18 * amt)
                {
                    continue;  // (a band gone for a moment)
                }
                int ox = (int)((rnd.NextDouble() * 2 - 1) * 70 * s * amt);
                g.DrawImage(logosS[from], new Rectangle(dx + ox, dy + y0, cw, y1 - y0), new Rectangle(0, y0, cw, y1 - y0), GraphicsUnit.Pixel);
            }
        }

        // The figure, still; or glitching from one to the other with the logo (MINECRAFT just switched).
        private void DrawFigure(Graphics g, int cw, float s, int dx, int dy)
        {
            float b = T - brandAt;
            if (b >= 0.34f || (fixedTime >= 0f && !fixedSwitch))
            {
                g.DrawImageUnscaled(figuresS[BrandIndex], dx, dy);
                return;
            }
            int from = b < 0.17f ? brandFrom : BrandIndex;
            float amt = b < 0.17f ? b / 0.17f : 1f - (b - 0.17f) / 0.17f;
            int top = (int)(50 * s), bottom = (int)(620 * s), bands = 12;
            var rnd = new Random((int)(b / 0.03f) * 6007 + 7);
            for (int i = 0; i < bands; i++)
            {
                int y0 = top + (bottom - top) * i / bands, y1 = top + (bottom - top) * (i + 1) / bands;
                if (rnd.NextDouble() < 0.2 * amt)
                {
                    continue;
                }
                int ox = (int)((rnd.NextDouble() * 2 - 1) * 60 * s * amt);
                g.DrawImage(figuresS[from], new Rectangle(dx + ox, dy + y0, cw, y1 - y0), new Rectangle(0, y0, cw, y1 - y0), GraphicsUnit.Pixel);
            }
        }

        // ---- the patch notes (the newest release's notes from GitHub), on the right under the figure ----

        private static readonly Rectangle NewsR = new Rectangle(1160, 628, 632, 352);
        private Font tiny;
        private List<(string Text, int Kind)> notes;  // (0 heading, 1 bullet, 2 text, 3 blank)
        private List<(string Text, int Kind, bool More)> notesWrapped;
        private bool notesOffline;
        private float notesAt = -100f, notesScroll, notesMax;
        private Rectangle notesLinkR;

        // GitHub's markdown, as lines: headings, bullets, text (bold, code and links plain; Downloads left out).
        private void SetNotes(string md)
        {
            var list = new List<(string, int)>();
            bool skip = false;
            foreach (string raw in (md ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
                line = line.Replace("**", "").Replace("`", "").Replace("__", "");
                if (line.StartsWith("#"))
                {
                    string head = line.TrimStart('#').Trim();
                    skip = head.StartsWith("Download", StringComparison.OrdinalIgnoreCase);
                    if (skip || head.Length == 0)
                    {
                        continue;
                    }
                    if (list.Count > 0 && list[list.Count - 1].Item2 != 3)
                    {
                        list.Add(("", 3));
                    }
                    list.Add((head.ToUpperInvariant(), 0));
                }
                else if (skip)
                {
                    continue;
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    list.Add((line.Substring(2).Trim().ToUpperInvariant(), 1));
                }
                else if (line.Length > 0)
                {
                    list.Add((line.ToUpperInvariant(), 2));
                }
                else if (list.Count > 0 && list[list.Count - 1].Item2 != 3)
                {
                    list.Add(("", 3));
                }
            }
            while (list.Count > 0 && list[list.Count - 1].Item2 == 3)
            {
                list.RemoveAt(list.Count - 1);
            }
            notes = list;
            notesWrapped = null;
            notesScroll = 0f;
            if (notesAt < 0f)
            {
                notesAt = T;
            }
        }

        private List<(string, int, bool)> Wrap(Graphics g, float width)
        {
            var o = new List<(string, int, bool)>();
            foreach (var (text, kind) in notes)
            {
                if (kind == 3)
                {
                    o.Add(("", 3, false));
                    continue;
                }
                string lead = kind == 1 ? "- " : "";
                string line = "";
                bool more = false;
                foreach (string word in text.Split(' '))
                {
                    string next = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Measure(g, lead + next, tiny).Width > width)
                    {
                        o.Add((lead + line, kind, more));
                        lead = kind == 1 ? "  " : "";
                        more = true;
                        line = word;
                    }
                    else
                    {
                        line = next;
                    }
                }
                o.Add((lead + line, kind, more));
            }
            return o;
        }

        private void DrawNews(Graphics g, float t)
        {
            if (tiny == null)
            {
                tiny = new Font(fonts.Families[0], 21f, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            float appear = Ease((t - MenuAt - 0.15f) / 0.45f);
            if (appear <= 0f)
            {
                return;
            }
            // (Sliding in from the right as the buttons come from the left.)
            var r = new RectangleF(NewsR.X + (W - NewsR.X + 60) * (1f - appear), NewsR.Y, NewsR.Width, NewsR.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath box = Chamfer(r, 14))
            using (var fill = new SolidBrush(Color.FromArgb(225, 0, 0, 0)))
            using (var pen = new Pen(Color.White, 3))
            {
                g.FillPath(fill, box);
                g.DrawPath(pen, box);
            }
            string title = latest != null ? "-- PATCH NOTES " + latest.Version + " --" : "-- PATCH NOTES --";
            SizeF ts = Measure(g, title, small);
            Text(g, title, r.X + (r.Width - ts.Width) / 2f, r.Y + 14, Color.White);
            g.FillRectangle(Brushes.White, r.X + 18, r.Y + 52, r.Width - 36, 2);

            var body = new RectangleF(r.X + 22, r.Y + 66, r.Width - 56, r.Height - 66 - 46);
            const float LineH = 27f;
            if (notes == null || notes.Count == 0)
            {
                string msg = notesOffline ? "NO SIGNAL - OFFLINE" : notes != null ? "NO NOTES FOR THIS ONE" : "RECEIVING" + new string('.', 1 + (int)(t * 3f) % 3);
                Text(g, msg, body.X, body.Y, Dim, tiny);
            }
            else
            {
                if (notesWrapped == null)
                {
                    notesWrapped = Wrap(g, body.Width);
                }
                int visible = (int)(body.Height / LineH);
                notesMax = Math.Max(0, notesWrapped.Count - visible);
                notesScroll = Math.Min(notesScroll, notesMax);
                int first = (int)notesScroll;
                // (Each line appearing a moment after the last, the first time.)
                float from = Math.Max(MenuAt + 0.5f, notesAt + 0.1f);
                GraphicsState st = g.Save();
                g.SetClip(body, CombineMode.Intersect);
                for (int i = first; i < notesWrapped.Count && i < first + visible + 1; i++)
                {
                    if (t < from + i * 0.03f)
                    {
                        break;
                    }
                    var (text, kind, _) = notesWrapped[i];
                    if (kind == 3)
                    {
                        continue;
                    }
                    Text(g, text, body.X, body.Y + (i - first) * LineH, kind == 0 ? Color.White : Ink, tiny);
                }
                g.Restore(st);
                // (A bar when there's more than shows: the mouse wheel scrolls.)
                if (notesMax > 0)
                {
                    float trackX = r.Right - 20, trackH = body.Height;
                    using (var faint = new SolidBrush(Faint))
                    {
                        g.FillRectangle(faint, trackX, body.Y, 4, trackH);
                    }
                    float thumbH = Math.Max(24f, trackH * visible / notesWrapped.Count);
                    float thumbY = body.Y + (trackH - thumbH) * (notesScroll / notesMax);
                    g.FillRectangle(Brushes.White, trackX, thumbY, 4, thumbH);
                }
            }
            string link = "OPEN ON GITHUB";
            SizeF ls = Measure(g, link, tiny);
            notesLinkR = appear >= 1f ? new Rectangle((int)(r.X + 22), (int)(r.Bottom - 36), (int)ls.Width, (int)ls.Height) : Rectangle.Empty;
            Text(g, link, r.X + 22, r.Bottom - 36, linkHover == 2 ? Color.White : Dim, tiny);
            if (linkHover == 2)
            {
                g.FillRectangle(Brushes.White, r.X + 22, r.Bottom - 36 + ls.Height + 1, ls.Width, 2);
            }
        }

        // A button: white outline, black inside; hovered, filled white (the label black); pressed, red.
        private void DrawButton(Graphics g, Btn b, float dx, bool down)
        {
            if (dx <= -(BX + BW + 50))
            {
                return;
            }
            bool on = b.Enabled();
            var r = new RectangleF(b.R.X + dx, b.R.Y, b.R.Width, b.R.Height);
            string label = b.Label();
            SizeF size = Measure(g, label, big);
            float tx = r.X + (r.Width - size.Width) / 2f, ty = r.Y + (r.Height - size.Height) / 2f + 3 + (down ? 3 : 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath outer = Chamfer(r, 12))
            using (GraphicsPath inner = Chamfer(RectangleF.Inflate(r, -5, -5), 9))
            {
                if (down)
                {
                    using (var red = new SolidBrush(Red))
                    {
                        g.FillPath(red, outer);
                    }
                    Text(g, label, tx, ty, Color.Black, big);
                    return;
                }
                using (var edge = new SolidBrush(on ? Color.White : Dim))
                {
                    g.FillPath(edge, outer);
                }
                g.FillPath(Brushes.Black, inner);
                Text(g, label, tx, ty, on ? Color.White : Dim, big);
                if (b.Hv > 0f)
                {
                    GraphicsState st = g.Save();
                    g.SetClip(new RectangleF(r.X, r.Y, r.Width * b.Hv, r.Height), CombineMode.Intersect);
                    g.FillPath(Brushes.White, outer);
                    Text(g, label, tx, ty, Color.Black, big);
                    g.Restore(st);
                }
            }
        }

        // ---- stills, for checking the look ----

        // A still of the window (KillNet.exe --render file.png [main|options|busy|close] [seconds]).
        public void RenderTo(string file, string screen, float at)
        {
            fixedTime = at;
            if (screen == "options")
            {
                options = true;
                buttons = optionButtons;
                selected = buttons.Count - 1;
            }
            else if (screen == "busy")
            {
                busy = true;
                status = "DOWNLOADING 2.0.0";
                progress = 43;
            }
            else if (screen == "updating")
            {
                updateKnownAt = float.PositiveInfinity;
            }
            else if (screen == "close")
            {
                closingAt = at - 0.12f;
            }
            else if (screen == "craft")
            {
                settings.Minecraft = true;
            }
            else if (screen == "switch")
            {
                brandFrom = 0;
                settings.Minecraft = true;
                brandAt = at - 0.1f;
                fixedSwitch = true;
            }
            if (screen != "nonotes")
            {
                latest = new Installer.Release { Version = "2.0.0", Notes = SampleNotes };
                SetNotes(SampleNotes);
                notesAt = 0f;
            }
            using (var page = new Bitmap(W, H, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(page))
                {
                    Compose(g, W, H);
                }
                page.Save(file);
            }
        }

        private bool fixedSwitch;
        private const string SampleNotes = "# Killcraft 2.0.0: multiplayer\n\n**Play ULTRAKILL together over Steam**, with or without Minecraft.\n\n## New\n- **Parties:** PARTY in the main menu. HOST PARTY (name, size, public / friends / private, cheats on or off), JOIN BY CODE, PUBLIC LOBBIES, REJOIN, or a Steam invite.\n- **One level for everyone:** shared enemies and bosses (one health bar), doors, arenas, breakables, Cybergrind waves.\n- **Voice chat** (push to talk: V) heard from where each V1 is, **text chat** (Enter).\n- **Replays:** every run is kept, with a CASE FILE.\n\n## Downloads\n- `Killcraft-2.0.0.zip`\n";

        // How long a frame takes to draw at that size (milliseconds, on average), for --bench.
        public double Bench(int cw, int ch, int frames)
        {
            using (var page = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(page))
            {
                fixedTime = 8f;
                Compose(g, cw, ch);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    fixedTime = 8f + i / 60f;
                    Compose(g, cw, ch);
                }
                return sw.Elapsed.TotalMilliseconds / frames;
            }
        }
    }
}
