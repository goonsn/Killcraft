using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KillNetLauncher
{
    // The launcher's one window: ULTRAKILL's main menu, as KILLNET (design/mockup.py). This half: the
    // state, what the buttons do, the mouse and keys, the frame loop. The drawing (and the intro, and
    // the closing) is in LauncherForm.Draw.cs.
    internal sealed partial class LauncherForm : Form
    {
        private const int W = 1920, H = 1080;
        private static readonly string LauncherVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private readonly Settings settings = Settings.Load();
        private readonly bool forRender;
        private readonly Bitmap bg;
        // The figure on the right: KILLNET's globe (0), KILLCRAFT's exploded block (1).
        private readonly Bitmap[] figures = new Bitmap[2];
        // KILLNET's logo (0) and KILLCRAFT's (1): which by MINECRAFT ON/OFF, as the mod names itself.
        private readonly Bitmap[] logos = new Bitmap[2];
        private readonly Rectangle[] logoBoxes = new Rectangle[2];
        private int BrandIndex => settings.Minecraft ? 1 : 0;
        private string BrandName => settings.Minecraft ? "KILLCRAFT" : "KILLNET";
        private Bitmap logo => logos[BrandIndex];
        private Rectangle logoBox => logoBoxes[BrandIndex];
        private float brandAt = -100f;
        private int brandFrom;
        private readonly Wires wires;
        private readonly PrivateFontCollection fonts = new PrivateFontCollection();
        private readonly Font big, small;
        private IntPtr fontMemory;

        private sealed class Btn
        {
            public Rectangle R;
            public Func<string> Label;
            public Action Click;
            public Func<bool> Enabled = () => true;
            public byte[] Sound = Sfx.Click;
            public float Hv;  // (how far its hover fill has wiped across: 0..1)
        }

        private List<Btn> buttons, mainButtons, optionButtons, leaving;
        private float shownAt = float.NaN, leftAt = -100f;
        private bool options;
        private int selected, pressed = -1;
        private Rectangle githubR, discordR;
        private int linkHover = -1;

        // What's there and what's going on.
        private string bepinex, mod;
        private bool hasMcParts, steamUp;
        private Installer.Release latest;
        private bool checking, busy;
        private string status = "STANDBY - WAIT FOR LAUNCH";
        private float statusAt = -100f, linesAt = float.NaN;
        private int progress = -1;
        private float repairAskedAt = -100f;
        // (The mod in r2modman / Thunderstore's app / Gale and not here: PLAY says so first, see Play.)
        private string modManager;
        private float managerAskedAt = -100f;
        // (The update check, as the boot screen shows it: when it came back, what it said.)
        private float updateKnownAt = float.PositiveInfinity;
        private string updateResult;
        private Color updateColor = Color.White;

        // The clock: seconds since the window opened (a fixed one for --render).
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private float fixedTime = -1f, skipAt = -1f;
        private float T => fixedTime >= 0f ? fixedTime : (float)clock.Elapsed.TotalSeconds;

        // Closing: the picture switching off like an old screen first.
        private float closingAt = -1f;
        private bool reallyClose;

        public LauncherForm(bool forRender = false)
        {
            this.forRender = forRender;
            base.Text = "KillNet";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            using (Stream s = Res("icon.ico"))
            {
                Icon = new Icon(s);
            }
            bg = LoadArt("bg.png");
            logos[0] = LoadArt("logo.png");
            logos[1] = LoadArt("logo-craft.png");
            figures[0] = LoadArt("figure-net.png");
            figures[1] = LoadArt("figure-small.png");
            logoBoxes[0] = Opaque(logos[0]);
            logoBoxes[1] = Opaque(logos[1]);
            wires = new Wires(Res("wires.txt"), bg.Height);
            byte[] ttf;
            using (Stream s = Res("vcr.ttf"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                ttf = ms.ToArray();
            }
            fontMemory = Marshal.AllocCoTaskMem(ttf.Length);
            Marshal.Copy(ttf, 0, fontMemory, ttf.Length);
            fonts.AddMemoryFont(fontMemory, ttf.Length);
            big = new Font(fonts.Families[0], 48f, FontStyle.Regular, GraphicsUnit.Pixel);
            small = new Font(fonts.Families[0], 27f, FontStyle.Regular, GraphicsUnit.Pixel);

            Rectangle area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int w = (int)Math.Min(Math.Min(area.Width * 0.75, area.Height * 0.75 * W / H), W);
            ClientSize = new Size(w, w * H / W);

            Sfx.On = settings.Sound && !forRender;
            BuildButtons();
            if (!Installer.IsGame(settings.GamePath))
            {
                settings.GamePath = Installer.FindGame();
                settings.Save();
            }
            Refresh(false);
            try
            {
                steamUp = Process.GetProcessesByName("steam").Length > 0;
            }
            catch (Exception)
            {
            }
            if (forRender)
            {
                updateKnownAt = 0f;
                updateResult = "UP TO DATE";
                return;
            }
            Application.Idle += OnIdle;
            Shown += async (s, e) => await Check();
        }

        private static Stream Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

        private static Bitmap LoadArt(string name)
        {
            using (Stream s = Res(name))
            using (var b = new Bitmap(s))
            {
                return b.Clone(new Rectangle(0, 0, b.Width, b.Height), PixelFormat.Format32bppPArgb);
            }
        }

        // Where a page-sized layer has anything (its alpha), for the logo's slam.
        private static Rectangle Opaque(Bitmap b)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var px = new byte[d.Stride * b.Height];
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    if (px[y * d.Stride + x * 4 + 3] > 0)
                    {
                        x0 = Math.Min(x0, x);
                        y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x);
                        y1 = Math.Max(y1, y);
                    }
                }
            }
            return x1 < 0 ? new Rectangle(0, 0, b.Width, b.Height) : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        protected override void Dispose(bool disposing)
        {
            Application.Idle -= OnIdle;
            base.Dispose(disposing);
            if (fontMemory != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fontMemory);
                fontMemory = IntPtr.Zero;
            }
        }

        // ---- the window: rounded corners (Windows 11), a frame each time the screen shows one ----

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Hwnd;
            public uint Msg;
            public IntPtr WParam, LParam;
            public uint Time;
            public Point P;
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMessage m, IntPtr hwnd, uint min, uint max, uint remove);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;  // (DWMWA_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUND)
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch (Exception)
            {
            }
        }

        // While there's nothing else to do: draw, then wait for the screen's next refresh (DwmFlush), so
        // it moves evenly at the monitor's rate (at most ~80 a second on faster ones).
        private long lastFrame;

        private void OnIdle(object sender, EventArgs e)
        {
            while (!IsDisposed && !PeekMessage(out NativeMessage _, IntPtr.Zero, 0, 0, 0))
            {
                if (WindowState == FormWindowState.Minimized || !Visible)
                {
                    System.Threading.Thread.Sleep(20);
                    continue;
                }
                long now = clock.ElapsedTicks;
                if ((now - lastFrame) * 1000.0 / Stopwatch.Frequency >= 12.0)
                {
                    lastFrame = now;
                    Invalidate();
                    Update();
                }
                if (DwmFlush() != 0)
                {
                    System.Threading.Thread.Sleep(8);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!forRender && !reallyClose && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
            {
                e.Cancel = true;
                if (closingAt < 0f)
                {
                    closingAt = T;
                    Sfx.Play(Sfx.PowerOff);
                    var done = new Timer { Interval = (int)(CloseTime * 1000) + 40 };
                    done.Tick += (s, a) =>
                    {
                        done.Stop();
                        reallyClose = true;
                        Close();
                    };
                    done.Start();
                }
                return;
            }
            base.OnFormClosing(e);
        }

        // ---- the buttons (as ULTRAKILL's menu lays them out) ----

        private const int BX = 128, BW = 628, BH = 100, Gap = 13, BY = 436;
        private static readonly int Half = (BW - Gap) / 2;

        private static Rectangle Row(int row) => new Rectangle(BX, BY + row * (BH + Gap), BW, BH);
        private static Rectangle LeftHalf(int row) => new Rectangle(BX, BY + row * (BH + Gap), Half, BH);
        private static Rectangle RightHalf(int row) => new Rectangle(BX + Half + Gap, BY + row * (BH + Gap), Half, BH);

        private void BuildButtons()
        {
            Func<bool> idle = () => !busy;
            mainButtons = new List<Btn>
            {
                new Btn { R = Row(0), Label = () => "PLAY", Click = Play, Enabled = idle },
                new Btn { R = Row(1), Label = () => "MINECRAFT: " + (settings.Minecraft ? "ON" : "OFF"), Click = ToggleMinecraft, Enabled = idle, Sound = Sfx.Glitch },
                new Btn { R = LeftHalf(2), Label = () => "UPDATE", Click = () => _ = Update(false), Enabled = () => !busy && Installer.IsGame(settings.GamePath) },
                new Btn { R = RightHalf(2), Label = () => "OPTIONS", Click = () => ShowScreen(true), Enabled = idle },
                new Btn { R = Row(3), Label = () => "QUIT", Click = Close, Sound = null },
            };
            optionButtons = new List<Btn>
            {
                new Btn { R = LeftHalf(0), Label = () => "FOLDER", Click = PickFolder, Enabled = idle },
                new Btn { R = RightHalf(0), Label = () => "REPAIR", Click = () => _ = Update(true), Enabled = () => !busy && Installer.IsGame(settings.GamePath) },
                new Btn { R = Row(1), Label = () => "AUTO UPDATE: " + (settings.AutoUpdate ? "ON" : "OFF"), Click = () => { settings.AutoUpdate = !settings.AutoUpdate; settings.Save(); }, Enabled = idle },
                new Btn { R = Row(2), Label = () => "AUTO CLOSE: " + (settings.CloseOnPlay ? "ON" : "OFF"), Click = () => { settings.CloseOnPlay = !settings.CloseOnPlay; settings.Save(); }, Enabled = idle },
                new Btn { R = LeftHalf(3), Label = () => "SFX: " + (settings.Sound ? "ON" : "OFF"), Click = ToggleSound },
                new Btn { R = RightHalf(3), Label = () => "BACK", Click = () => ShowScreen(false), Sound = Sfx.Back },
            };
            buttons = mainButtons;
        }

        // The other menu: these slide out to the left, those slide in after them.
        private void ShowScreen(bool toOptions)
        {
            if (options == toOptions)
            {
                return;
            }
            leaving = buttons;
            leftAt = T;
            options = toOptions;
            buttons = options ? optionButtons : mainButtons;
            foreach (Btn b in buttons)
            {
                b.Hv = 0f;
            }
            shownAt = T + 0.14f;
            linesAt = T + 0.1f;
            selected = options ? buttons.Count - 1 : 0;
            pressed = -1;
        }

        private void Press(Btn b)
        {
            if (b.Sound != null)
            {
                Sfx.Play(b.Sound);
            }
            b.Click();
        }

        // ---- the state, the status lines ----

        private void Refresh(bool redraw = true)
        {
            string game = settings.GamePath;
            bepinex = Installer.IsGame(game) ? Installer.BepInExVersion(game) : null;
            mod = Installer.IsGame(game) ? Installer.ModVersion(game) : null;
            hasMcParts = Installer.IsGame(game) && Installer.HasMinecraftParts(game);
            modManager = mod == null ? Installer.ModManagerCopy() : null;
        }

        private void Say(string text, int pct = -1)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Say(text, pct)));
                return;
            }
            text = text.ToUpperInvariant();
            if (text != status)
            {
                statusAt = T;
                if (text.StartsWith("ERROR"))
                {
                    Sfx.Play(Sfx.Error);
                }
            }
            status = text;
            progress = pct;
        }

        // (An install of unknown version, put there by hand, isn't taken for an old one: only REPAIR replaces it.)
        private bool UpdateWaiting => latest != null && !string.IsNullOrEmpty(mod) && Installer.Newer(latest.Version, mod);
        private bool LauncherUpdate => latest != null && latest.LauncherUrl != null && Installer.Newer(latest.LauncherVersion, LauncherVersion);

        private string Header()
        {
            if (options)
            {
                return "LAUNCHER_" + LauncherVersion;
            }
            if (mod == null)
            {
                return Installer.IsGame(settings.GamePath) ? BrandName + " NOT INSTALLED" : BrandName + "_" + LauncherVersion;
            }
            return mod.Length == 0 ? BrandName + " TEST BUILD" : BrandName + "_" + mod + (UpdateWaiting ? " UPDATE " + latest.Version : " READY");
        }

        private string[] Lines()
        {
            if (options)
            {
                string path = string.IsNullOrEmpty(settings.GamePath) ? "NOT SET" : settings.GamePath.ToUpperInvariant();
                return new[] { "GAME FOLDER:", Tail(path, 30), Fit(status, 30) };
            }
            string game = Installer.IsGame(settings.GamePath) ? "ULTRAKILL FOUND...OK" : "ULTRAKILL...NOT FOUND";
            string bep = bepinex != null ? $"BEPINEX {Short(bepinex)}...OK" : "BEPINEX...NOT INSTALLED";
            string st = status + (progress >= 0 ? $" {progress}%" : "");
            return new[] { game, bep, Fit(st, 37) };
        }

        private static string Short(string v)
        {
            string[] p = v.Split('.');
            return p.Length >= 3 ? $"{p[0]}.{p[1]}.{p[2]}" : v;
        }

        private static string Fit(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 3) + "...";
        private static string Tail(string s, int n) => s.Length <= n ? s : "..." + s.Substring(s.Length - (n - 3));

        // ---- what the buttons do ----

        private void SetUpdate(string text, Color color)
        {
            updateResult = text;
            updateColor = color;
            updateKnownAt = T;
        }

        private async Task Check()
        {
            try
            {
                if (!Installer.IsGame(settings.GamePath))
                {
                    Say("ULTRAKILL NOT FOUND - SET IT IN OPTIONS");
                    SetUpdate("SKIPPED", Dim);
                    return;
                }
                checking = true;
                try
                {
                    latest = await Installer.LatestRelease();
                }
                catch (Exception)
                {
                    Say(mod == null ? "OFFLINE - CAN'T DOWNLOAD " + BrandName : "OFFLINE - STANDBY");
                    SetUpdate("OFFLINE", Red);
                    notesOffline = true;
                    return;
                }
                SetNotes(latest.Notes);
                if (LauncherUpdate && settings.AutoUpdate)
                {
                    if (await SelfUpdate())
                    {
                        return;
                    }
                }
                if (mod == null && modManager != null)
                {
                    Say($"{BrandName} FOUND IN {modManager}");
                    SetUpdate(latest.Version + " OUT", Color.White);
                }
                else if (mod == null)
                {
                    Say("PRESS PLAY TO INSTALL " + latest.Version);
                    SetUpdate(latest.Version + " TO INSTALL", Color.White);
                }
                else if (LauncherUpdate)
                {
                    Say($"LAUNCHER {latest.LauncherVersion} - PRESS UPDATE");
                    SetUpdate($"LAUNCHER {latest.LauncherVersion} OUT", Color.White);
                }
                else if (UpdateWaiting)
                {
                    Say(settings.AutoUpdate ? $"UPDATE {latest.Version} - INSTALLS ON PLAY" : $"UPDATE {latest.Version} - PRESS UPDATE");
                    SetUpdate(latest.Version + " OUT", Color.White);
                }
                else if (mod.Length == 0)
                {
                    Say($"TEST BUILD - UPDATE FOR {latest.Version}");
                    SetUpdate(latest.Version + " OUT", Color.White);
                }
                else
                {
                    Say("STANDBY - WAIT FOR LAUNCH");
                    SetUpdate("UP TO DATE", Color.White);
                }
            }
            finally
            {
                checking = false;
                if (float.IsPositiveInfinity(updateKnownAt))
                {
                    SetUpdate(updateResult ?? "-", Dim);
                }
            }
        }

        // The new launcher in place of this one, started; this one closes. False if it couldn't.
        private async Task<bool> SelfUpdate()
        {
            busy = true;
            try
            {
                Say("UPDATING LAUNCHER TO " + latest.LauncherVersion);
                await Installer.UpdateLauncher(latest, Progress);
                Say("RESTARTING");
                await Task.Delay(300);
                Close();
                return true;
            }
            catch (Exception e)
            {
                Say("LAUNCHER UPDATE FAILED: " + e.Message);
                await Task.Delay(1500);
                return false;
            }
            finally
            {
                busy = false;
            }
        }

        private void Progress(string what, int pct) => Say(what, pct);

        private bool Ready()
        {
            if (!Installer.IsGame(settings.GamePath))
            {
                Say("ULTRAKILL NOT FOUND - SET IT IN OPTIONS");
                return false;
            }
            if (Installer.GameRunning())
            {
                Say("CLOSE ULTRAKILL FIRST");
                return false;
            }
            return true;
        }

        // Installs what's missing (and the update, with auto update on), sets the mode, starts ULTRAKILL.
        private async void Play()
        {
            if (busy || !Ready())
            {
                return;
            }
            // (Installed by a mod manager: that starts the game with its own profile, so a second copy here
            // is only wanted when asked for: PLAY again.)
            if (mod == null && modManager != null && T - managerAskedAt > 8f)
            {
                managerAskedAt = T;
                string first = "START IT FROM " + modManager;
                Say(first);
                await Task.Delay(2500);
                if (status == first)
                {
                    Say("OR PRESS PLAY AGAIN TO INSTALL HERE");
                }
                return;
            }
            busy = true;
            try
            {
                string game = settings.GamePath;
                if (bepinex == null)
                {
                    await Installer.InstallBepInEx(game, Progress);
                    Refresh();
                }
                bool needMod = mod == null || (settings.Minecraft && !hasMcParts && mod != "");
                if (needMod || (settings.AutoUpdate && (latest == null || UpdateWaiting)))
                {
                    if (latest == null)
                    {
                        Say("CHECKING FOR UPDATES");
                        try
                        {
                            latest = await Installer.LatestRelease();
                        }
                        catch (Exception) when (!needMod)
                        {
                            // (Offline, already installed: play what's there.)
                        }
                    }
                    if (latest != null && (needMod || UpdateWaiting))
                    {
                        await Installer.InstallMod(game, latest, settings.Minecraft || hasMcParts, Progress);
                        Refresh();
                    }
                }
                Installer.SetMinecraft(game, settings.Minecraft);
                // (Killcraft's first start: Prism asks for a Microsoft account. Said here, and the launcher
                // stays open with it.)
                bool signIn = settings.Minecraft && !Installer.MinecraftSignedIn();
                Say(settings.Minecraft ? "LAUNCHING - WITH MINECRAFT" : "LAUNCHING - MULTIPLAYER ONLY");
                Installer.MarkLaunch(game);
                Installer.StartGame();
                if (signIn)
                {
                    await Task.Delay(1500);
                    Say("SIGN IN TO MINECRAFT WHEN PRISM OPENS");
                    return;
                }
                if (settings.CloseOnPlay)
                {
                    await Task.Delay(2500);
                    Close();
                    return;
                }
                await Task.Delay(1500);
                Say("STANDBY - WAIT FOR LAUNCH");
            }
            catch (Exception e)
            {
                Say("ERROR: " + e.Message);
            }
            finally
            {
                busy = false;
            }
        }

        // UPDATE: the launcher first if there's a new one, then the mod. REPAIR (force): the mod again.
        private async Task Update(bool force)
        {
            if (busy || !Ready())
            {
                return;
            }
            busy = true;
            try
            {
                Say("CHECKING FOR UPDATES");
                latest = await Installer.LatestRelease();
                if (!force && LauncherUpdate)
                {
                    busy = false;
                    if (await SelfUpdate())
                    {
                        return;
                    }
                    busy = true;
                }
                SetNotes(latest.Notes);
                // (A build of unknown version - a test build, put there by hand - may be newer than the
                // release: UPDATE or REPAIR replaces it only when pressed twice.)
                if (mod == "" && T - repairAskedAt > 6f)
                {
                    repairAskedAt = T;
                    Say($"PRESS AGAIN TO INSTALL {latest.Version}");
                    return;
                }
                if (force || string.IsNullOrEmpty(mod) || UpdateWaiting || (settings.Minecraft && !hasMcParts))
                {
                    if (Installer.BepInExVersion(settings.GamePath) == null)
                    {
                        await Installer.InstallBepInEx(settings.GamePath, Progress);
                    }
                    await Installer.InstallMod(settings.GamePath, latest, settings.Minecraft || hasMcParts, Progress);
                    Refresh();
                    Say($"{BrandName} {latest.Version} INSTALLED");
                    Sfx.Play(Sfx.Done);
                }
                else
                {
                    Say($"UP TO DATE ({mod})");
                }
            }
            catch (Exception e)
            {
                Say("ERROR: " + e.Message);
            }
            finally
            {
                busy = false;
            }
        }

        private void ToggleMinecraft()
        {
            // (The logo glitches from one name to the other.)
            brandFrom = BrandIndex;
            brandAt = T;
            settings.Minecraft = !settings.Minecraft;
            settings.Save();
            Say(settings.Minecraft
                ? (hasMcParts || mod == null ? "MINECRAFT ON - KILLCRAFT MODE" : "MINECRAFT ON - PLAY DOWNLOADS IT")
                : "MULTIPLAYER ONLY - KILLNET MODE");
        }

        private void ToggleSound()
        {
            settings.Sound = !settings.Sound;
            settings.Save();
            Sfx.On = settings.Sound;
            Sfx.Play(Sfx.Click);
        }

        private void PickFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "ULTRAKILL's folder (with ULTRAKILL.exe in it)", SelectedPath = settings.GamePath })
            {
                if (d.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                if (!Installer.IsGame(d.SelectedPath))
                {
                    Say("NO ULTRAKILL.EXE THERE");
                    return;
                }
                settings.GamePath = d.SelectedPath;
                settings.Save();
                Refresh();
                Say("GAME FOLDER SET");
                _ = Check();
            }
        }

        // ---- the mouse and the keys ----

        // (Any key or click in the intro: straight to the menu.)
        private bool SkipIntro()
        {
            if (T >= MenuAt)
            {
                return false;
            }
            skipAt = T;
            Sfx.Play(Sfx.Click);
            return true;
        }

        private Point Page(Point p) => new Point(p.X * W / Math.Max(1, ClientSize.Width), p.Y * H / Math.Max(1, ClientSize.Height));

        private int At(Point page)
        {
            if (!Settled)
            {
                return -1;
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].R.Contains(page) && buttons[i].Enabled())
                {
                    return i;
                }
            }
            return -1;
        }

        private int LinkAt(Point page) => T < MenuAt + 0.6f ? -1 : githubR.Contains(page) ? 0 : discordR.Contains(page) ? 1 : notesLinkR.Contains(page) ? 2 : -1;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (NewsR.Contains(Page(e.Location)))
            {
                notesScroll = Math.Max(0f, Math.Min(notesMax, notesScroll - e.Delta / 120f * 3f));
            }
        }

        private void Select(int i)
        {
            if (i != selected)
            {
                selected = i;
                Sfx.Play(Sfx.Hover);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = Page(e.Location);
            int at = At(p);
            if (at >= 0)
            {
                Select(at);
            }
            int link = LinkAt(p);
            if (link >= 0 && link != linkHover)
            {
                Sfx.Play(Sfx.Hover);
            }
            linkHover = link;
            Cursor = at >= 0 || linkHover >= 0 ? Cursors.Hand : Cursors.Default;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || closingAt >= 0f)
            {
                return;
            }
            if (SkipIntro())
            {
                return;
            }
            Point p = Page(e.Location);
            pressed = At(p);
            if (pressed < 0 && LinkAt(p) < 0)
            {
                // (Anywhere else: the window's moved by it, as by a title bar.)
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point p = Page(e.Location);
            int was = pressed;
            pressed = -1;
            if (was >= 0 && At(p) == was)
            {
                Press(buttons[was]);
            }
            else if (was < 0)
            {
                int link = LinkAt(p);
                if (link >= 0)
                {
                    Sfx.Play(Sfx.Click);
                    Installer.Open(link == 0 ? Installer.RepoUrl : link == 1 ? Installer.DiscordUrl
                        : latest != null && latest.Page.Length > 0 ? latest.Page : Installer.RepoUrl + "/releases");
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (closingAt >= 0f || SkipIntro())
            {
                return;
            }
            if (!Settled && e.KeyCode != Keys.Escape)
            {
                return;
            }
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.Left:
                    Step(-1);
                    break;
                case Keys.Down:
                case Keys.Right:
                case Keys.Tab:
                    Step(1);
                    break;
                case Keys.Enter:
                case Keys.Space:
                    if (selected >= 0 && selected < buttons.Count && buttons[selected].Enabled())
                    {
                        Press(buttons[selected]);
                    }
                    break;
                case Keys.Escape:
                    if (options)
                    {
                        Sfx.Play(Sfx.Back);
                        ShowScreen(false);
                    }
                    else
                    {
                        Close();
                    }
                    break;
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Tab || keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

        private void Step(int d)
        {
            int i = selected;
            for (int n = 0; n < buttons.Count; n++)
            {
                i = (i + d + buttons.Count) % buttons.Count;
                if (buttons[i].Enabled())
                {
                    break;
                }
            }
            Select(i);
        }
    }
}
