using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KillNetLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // (KillNet.exe --render out.png [main|options|busy] [seconds]: the window's picture at that moment
            // to a file, for checking the look.)
            if (args.Length >= 2 && args[0] == "--render")
            {
                float at = 6f;
                if (args.Length > 3)
                {
                    float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at);
                }
                using (var f = new LauncherForm(true))
                {
                    f.RenderTo(args[1], args.Length > 2 ? args[2] : "main", at);
                }
                return 0;
            }
            // (KillNet.exe --bench out.txt: how long a frame takes to draw.)
            if (args.Length >= 2 && args[0] == "--bench")
            {
                using (var f = new LauncherForm(true))
                {
                    System.IO.File.WriteAllText(args[1], $"1376x774: {f.Bench(1376, 774, 120):0.00} ms, 1920x1080: {f.Bench(1920, 1080, 120):0.00} ms");
                }
                return 0;
            }
            // (KillNet.exe --check out.txt: what it finds - the game, BepInEx, the mod, the latest release.)
            if (args.Length >= 2 && args[0] == "--check")
            {
                var lines = new System.Collections.Generic.List<string>();
                string game = Installer.FindGame();
                lines.Add("game: " + game);
                lines.Add("bepinex: " + (Installer.IsGame(game) ? Installer.BepInExVersion(game) ?? "none" : "-"));
                lines.Add("mod: " + (Installer.IsGame(game) ? Installer.ModVersion(game) ?? "none" : "-"));
                lines.Add("minecraft parts: " + (Installer.IsGame(game) && Installer.HasMinecraftParts(game)));
                try
                {
                    Installer.Release r = Installer.LatestRelease().Result;
                    lines.Add($"latest: {r.Tag} ({r.Version}) full={r.FullName} ({r.FullSize}) net={r.NetName}");
                    lines.Add($"launcher: {r.LauncherVersion} via {string.Join(" | ", r.LauncherUrl ?? new string[0])}");
                    // (KillNet.exe --check out.txt fetch: also downloads the launcher and the multiplayer zip.)
                    if (Array.IndexOf(args, "fetch") >= 0)
                    {
                        foreach (var (what, urls) in new[] { ("launcher", r.LauncherUrl), ("net zip", r.NetUrl) })
                        {
                            string f = System.IO.Path.GetTempFileName();
                            try
                            {
                                Installer.Fetch(urls, f, p => { }).Wait();
                                lines.Add($"fetch {what}: {new System.IO.FileInfo(f).Length} B");
                            }
                            catch (Exception e)
                            {
                                lines.Add($"fetch {what}: failed " + e.GetBaseException().Message);
                            }
                            System.IO.File.Delete(f);
                        }
                    }
                    // (... install: installs the full download into a scratch folder, saying how far it got.)
                    if (Array.IndexOf(args, "install") >= 0)
                    {
                        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KillNet-installtest");
                        var said = new System.Collections.Generic.List<string>();
                        try
                        {
                            Installer.InstallMod(dir, r, true, (what, p) => { lock (said) { said.Add($"{what} {p}"); } }).Wait();
                            lines.Add($"install: ok, {said.Count} steps: {string.Join(", ", said.Where((x, i) => i % 15 == 0 || i == said.Count - 1))}");
                            lines.Add("install: version.txt " + Installer.ModVersion(dir) + ", minecraft parts " + Installer.HasMinecraftParts(dir));
                        }
                        catch (Exception e)
                        {
                            lines.Add("install: failed " + e.GetBaseException().Message);
                        }
                        try
                        {
                            System.IO.Directory.Delete(dir, true);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    lines.Add("mod manager copy: " + (Installer.ModManagerCopy() ?? "none"));
                    lines.Add("minecraft signed in: " + Installer.MinecraftSignedIn());
                }
                catch (Exception e)
                {
                    lines.Add("latest: failed " + e.GetBaseException().Message);
                }
                System.IO.File.WriteAllLines(args[1], lines);
                return 0;
            }
            // One launcher at a time. (Started by the old one after an update: it waits for that to close.)
            using (var one = new Mutex(false, "KillNet.Launcher"))
            {
                bool mine;
                try
                {
                    mine = one.WaitOne(Array.IndexOf(args, "--updated") >= 0 ? 10000 : 0);
                }
                catch (AbandonedMutexException)
                {
                    mine = true;
                }
                if (!mine)
                {
                    return 0;
                }
                // (What a self-update left behind: the old exe, its process gone now.)
                Installer.CleanUp();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LauncherForm());
                one.ReleaseMutex();
            }
            return 0;
        }
    }
}
