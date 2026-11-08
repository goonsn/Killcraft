using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KillNetLauncher
{
    // What the launcher does to ULTRAKILL's folder: finds it (Steam's libraries), installs BepInEx 5 and
    // the mod from their GitHub releases, says what's installed, sets the mod's mode, starts the game.
    internal static class Installer
    {
        public const string SteamAppId = "1229490";
        public const string Repo = "goonsn/Killcraft";
        public const string RepoUrl = "https://github.com/" + Repo;
        private const string UserAgent = "KillNet-Launcher";

        public sealed class Release
        {
            public string Version = "";
            public string Tag = "";
            public string FullUrl, FullName;      // Killcraft-<v>.zip: the mod with Minecraft's parts
            public string NetUrl, NetName;        // KillNet-<v>.zip: multiplayer only (smaller)
            public long FullSize, NetSize;
            public string LauncherVersion, LauncherUrl;  // KillNet-Launcher-<v>.exe, the newest in any release
        }

        public const string DiscordUrl = "https://discord.gg/UZsPDRvnGJ";

        static Installer()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // ---- ULTRAKILL ----

        public static bool IsGame(string path) => !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "ULTRAKILL.exe"));

        // ULTRAKILL's folder in one of Steam's libraries, or "".
        public static string FindGame()
        {
            var libraries = new List<string>();
            foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            {
                foreach (string name in new[] { "SteamPath", "InstallPath" })
                {
                    try
                    {
                        if (Registry.GetValue(key, name, null) is string p && p.Length > 0)
                        {
                            libraries.Add(p.Replace('/', '\\'));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            foreach (string steam in libraries.ToArray())
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string game = Path.Combine(lib, "steamapps", "common", "ULTRAKILL");
                if (IsGame(game))
                {
                    return game;
                }
            }
            try
            {
                if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId, "InstallLocation", null) is string loc && IsGame(loc))
                {
                    return loc;
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        public static bool GameRunning() => Process.GetProcessesByName("ULTRAKILL").Length > 0;

        // ---- what's installed ----

        public static string BepInExVersion(string game)
        {
            string dll = Path.Combine(game, "BepInEx", "core", "BepInEx.dll");
            if (!File.Exists(dll) || !File.Exists(Path.Combine(game, "winhttp.dll")))
            {
                return null;
            }
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(dll);
                return string.IsNullOrEmpty(v.FileVersion) ? "5" : v.FileVersion;
            }
            catch (Exception)
            {
                return "5";
            }
        }

        private static string ModFolder(string game) => Path.Combine(game, "BepInEx", "plugins", "Killcraft");

        // The installed mod's version ("" if installed some other way, null if not installed).
        public static string ModVersion(string game)
        {
            string folder = ModFolder(game);
            if (!File.Exists(Path.Combine(folder, "Killcraft.dll")))
            {
                return null;
            }
            try
            {
                string f = Path.Combine(folder, "version.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Minecraft's parts there (installed from the full Killcraft download).
        public static bool HasMinecraftParts(string game) => File.Exists(Path.Combine(ModFolder(game), "SkyCraft-Minecraft.zip"));

        public static string OpenableModFolder(string game) => Directory.Exists(ModFolder(game)) ? ModFolder(game) : game;

        // ---- GitHub ----

        private static object GetJson(string url)
        {
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                string text = web.DownloadString(url);
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
            }
        }

        // The newest release with the mod in it, and the newest launcher in any of the recent ones.
        public static Task<Release> LatestRelease() => Task.Run(() =>
        {
            Release rel = null;
            string bestLauncher = null, bestLauncherUrl = null;
            var launcher = new Regex(@"^KillNet-Launcher-(\d+(?:\.\d+)*)\.exe$", RegexOptions.IgnoreCase);
            foreach (Dictionary<string, object> r in (IEnumerable)GetJson($"https://api.github.com/repos/{Repo}/releases?per_page=20"))
            {
                if (r["draft"] is bool draft && draft || r["prerelease"] is bool pre && pre || !(r["assets"] is IEnumerable assets))
                {
                    continue;
                }
                var here = new Release { Tag = r["tag_name"] as string ?? "" };
                here.Version = here.Tag.TrimStart('v', 'V');
                string lv = null, lu = null;
                foreach (Dictionary<string, object> a in assets)
                {
                    string name = a["name"] as string ?? "";
                    string url = a["browser_download_url"] as string;
                    long size = Convert.ToInt64(a["size"]);
                    Match m = launcher.Match(name);
                    if (m.Success)
                    {
                        lv = m.Groups[1].Value;
                        lu = url;
                    }
                    else if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    else if (name.StartsWith("KillNet-", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("KillNet-Launcher", StringComparison.OrdinalIgnoreCase))
                    {
                        here.NetUrl = url; here.NetName = name; here.NetSize = size;
                    }
                    else if (name.StartsWith("Killcraft-", StringComparison.OrdinalIgnoreCase))
                    {
                        here.FullUrl = url; here.FullName = name; here.FullSize = size;
                    }
                }
                if (rel == null && here.FullUrl != null)
                {
                    rel = here;
                }
                if (lv != null && Newer(lv, bestLauncher))
                {
                    bestLauncher = lv;
                    bestLauncherUrl = lu;
                }
            }
            if (rel == null)
            {
                throw new Exception("no release with KillNet in it");
            }
            rel.LauncherVersion = bestLauncher;
            rel.LauncherUrl = bestLauncherUrl;
            // (No multiplayer-only download: the full one does both.)
            if (rel.NetUrl == null)
            {
                rel.NetUrl = rel.FullUrl; rel.NetName = rel.FullName; rel.NetSize = rel.FullSize;
            }
            return rel;
        });

        // ---- the launcher itself ----

        public static string Exe => System.Reflection.Assembly.GetEntryAssembly().Location;

        // The last version's file, left by an update (it was still running then).
        public static void CleanUp()
        {
            try
            {
                if (File.Exists(Exe + ".old"))
                {
                    File.Delete(Exe + ".old");
                }
            }
            catch (Exception)
            {
            }
        }

        // The new launcher downloaded next to this one, swapped in (a running exe can be renamed, not
        // overwritten), and started; this one then closes.
        public static async Task UpdateLauncher(Release rel, Action<string, int> progress)
        {
            string exe = Exe, fresh = exe + ".new", old = exe + ".old";
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                web.DownloadProgressChanged += (s, e) => progress("UPDATING LAUNCHER", e.ProgressPercentage);
                await web.DownloadFileTaskAsync(new Uri(rel.LauncherUrl), fresh);
            }
            if (new FileInfo(fresh).Length < 100000)
            {
                File.Delete(fresh);
                throw new Exception("the launcher download is broken");
            }
            if (File.Exists(old))
            {
                File.Delete(old);
            }
            File.Move(exe, old);
            try
            {
                File.Move(fresh, exe);
            }
            catch (Exception)
            {
                File.Move(old, exe);
                throw;
            }
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        // a newer than b ("2.0.1" > "2.0.0"; anything > "" or null).
        public static bool Newer(string a, string b)
        {
            if (string.IsNullOrEmpty(a))
            {
                return false;
            }
            if (string.IsNullOrEmpty(b))
            {
                return true;
            }
            int[] pa = Parts(a), pb = Parts(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y)
                {
                    return x > y;
                }
            }
            return false;
        }

        private static int[] Parts(string v) => Regex.Matches(v, "\\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();

        private static async Task<string> Download(string url, string name, Action<string, int> progress, string what)
        {
            string folder = Path.Combine(Path.GetTempPath(), "KillNet");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name);
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                web.DownloadProgressChanged += (s, e) => progress(what, e.ProgressPercentage);
                await web.DownloadFileTaskAsync(new Uri(url), file);
            }
            return file;
        }

        // ---- installing ----

        public static async Task InstallBepInEx(string game, Action<string, int> progress)
        {
            progress("FINDING BEPINEX", -1);
            string url = null, name = null;
            await Task.Run(() =>
            {
                foreach (Dictionary<string, object> r in (IEnumerable)GetJson("https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=40"))
                {
                    string tag = r["tag_name"] as string ?? "";
                    if (r["prerelease"] is bool pre && pre || !tag.StartsWith("v5."))
                    {
                        continue;
                    }
                    foreach (Dictionary<string, object> a in (IEnumerable)r["assets"])
                    {
                        string n = a["name"] as string ?? "";
                        if ((n.StartsWith("BepInEx_win_x64_5") || n.StartsWith("BepInEx_x64_5")) && n.EndsWith(".zip"))
                        {
                            url = a["browser_download_url"] as string;
                            name = n;
                            return;
                        }
                    }
                }
            });
            if (url == null)
            {
                throw new Exception("no BepInEx 5 download on GitHub");
            }
            string zip = await Download(url, name, progress, "DOWNLOADING BEPINEX");
            progress("INSTALLING BEPINEX", -1);
            await Task.Run(() => Extract(zip, game, null));
        }

        public static async Task InstallMod(string game, Release rel, bool minecraft, Action<string, int> progress)
        {
            string url = minecraft ? rel.FullUrl : rel.NetUrl;
            string name = minecraft ? rel.FullName : rel.NetName;
            if (url == null)
            {
                throw new Exception($"no download in release {rel.Tag}");
            }
            string zip = await Download(url, name, progress, $"DOWNLOADING {rel.Version}");
            progress("INSTALLING " + rel.Version, -1);
            await Task.Run(() =>
            {
                string plugins = Path.Combine(game, "BepInEx", "plugins");
                Directory.CreateDirectory(plugins);
                Extract(zip, plugins, "Killcraft/");
                File.WriteAllText(Path.Combine(ModFolder(game), "version.txt"), rel.Version);
            });
        }

        // Each file of the zip into the folder (only those under `only`, when given); nothing outside it.
        private static void Extract(string zip, string into, string only)
        {
            string root = Path.GetFullPath(into);
            using (ZipArchive z = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name.EndsWith("/") || (only != null && !name.StartsWith(only, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
            }
        }

        // ---- the mod's mode, and starting the game ----

        // [Minecraft] StartWithUltrakill in the mod's settings (made if it isn't there yet).
        public static void SetMinecraft(string game, bool on)
        {
            string folder = Path.Combine(game, "BepInEx", "config");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "dev.killcraft.cfg");
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
            string value = "StartWithUltrakill = " + (on ? "true" : "false");
            int section = lines.FindIndex(l => l.Trim().Equals("[Minecraft]", StringComparison.OrdinalIgnoreCase));
            if (section < 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add("");
                }
                lines.Add("[Minecraft]");
                lines.Add("");
                lines.Add(value);
            }
            else
            {
                int i = section + 1;
                bool set = false;
                for (; i < lines.Count && !lines[i].TrimStart().StartsWith("["); i++)
                {
                    if (Regex.IsMatch(lines[i], "^\\s*StartWithUltrakill\\s*="))
                    {
                        lines[i] = value;
                        set = true;
                        break;
                    }
                }
                if (!set)
                {
                    lines.Insert(section + 1, value);
                }
            }
            File.WriteAllLines(file, lines);
        }

        public static void StartGame() => Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });

        public static void Open(string what)
        {
            try
            {
                Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }
    }
}
