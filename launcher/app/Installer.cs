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
            // (Each download's URLs: GitHub's API one first, then the plain one; see Fetch.)
            public string[] FullUrl; public string FullName;  // Killcraft-<v>.zip: the mod with Minecraft's parts
            public string[] NetUrl; public string NetName;    // KillNet-<v>.zip: multiplayer only (smaller)
            public long FullSize, NetSize;
            public string LauncherVersion; public string[] LauncherUrl;  // KillNet-Launcher-<v>.exe, the newest in any release
            public string Notes = "", Page = "";          // its notes (GitHub's markdown) and its page
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

        // The installed mod's version: what the launcher installed (version.txt), else the dll's own (from
        // 2.0.1 on it carries it); "" if neither says (a test build), null if not installed.
        public static string ModVersion(string game)
        {
            string folder = ModFolder(game);
            string dll = Path.Combine(folder, "Killcraft.dll");
            if (!File.Exists(dll))
            {
                return null;
            }
            try
            {
                string f = Path.Combine(folder, "version.txt");
                if (File.Exists(f))
                {
                    return File.ReadAllText(f).Trim();
                }
                string v = FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "";
                v = Regex.Match(v, @"^\d+\.\d+\.\d+").Value;
                return v.Length == 0 || v == "1.0.0" || v == "0.0.0" ? "" : v;
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Minecraft's parts there (installed from the full Killcraft download).
        public static bool HasMinecraftParts(string game) => File.Exists(Path.Combine(ModFolder(game), "SkyCraft-Minecraft.zip"));

        // Signed in to Minecraft in SkyCraft's Prism (the mod unpacks it to %LOCALAPPDATA%\SkyCraft; the
        // first Killcraft start asks for a Microsoft account there).
        public static bool MinecraftSignedIn() => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyCraft", "Prism", "accounts.json"));

        // The mod in a mod manager's profile (r2modman, Thunderstore's app, Gale: Thunderstore installs go
        // there, not into the game's folder; the manager starts the game with that profile). Its name, or null.
        public static string ModManagerCopy()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var managers = new[]
            {
                ("R2MODMAN", Path.Combine(appData, "r2modmanPlus-local", "ULTRAKILL", "profiles")),
                ("THUNDERSTORE APP", Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder", "ULTRAKILL", "profiles")),
                ("GALE", Path.Combine(appData, "com.kesomannen.gale", "ultrakill", "profiles")),
            };
            foreach (var (name, profiles) in managers)
            {
                try
                {
                    if (!Directory.Exists(profiles))
                    {
                        continue;
                    }
                    foreach (string profile in Directory.GetDirectories(profiles))
                    {
                        string plugins = Path.Combine(profile, "BepInEx", "plugins");
                        if (!Directory.Exists(plugins))
                        {
                            continue;
                        }
                        foreach (string package in Directory.GetDirectories(plugins))
                        {
                            if (File.Exists(Path.Combine(package, "Killcraft.dll")) || Directory.GetDirectories(package).Any(d => File.Exists(Path.Combine(d, "Killcraft.dll"))))
                            {
                                return name;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

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
            string bestLauncher = null;
            string[] bestLauncherUrl = null;
            var launcher = new Regex(@"^KillNet-Launcher-(\d+(?:\.\d+)*)\.exe$", RegexOptions.IgnoreCase);
            foreach (Dictionary<string, object> r in (IEnumerable)GetJson($"https://api.github.com/repos/{Repo}/releases?per_page=20"))
            {
                if (r["draft"] is bool draft && draft || r["prerelease"] is bool pre && pre || !(r["assets"] is IEnumerable assets))
                {
                    continue;
                }
                var here = new Release { Tag = r["tag_name"] as string ?? "", Notes = r["body"] as string ?? "", Page = r["html_url"] as string ?? "" };
                here.Version = here.Tag.TrimStart('v', 'V');
                string lv = null;
                string[] lu = null;
                foreach (Dictionary<string, object> a in assets)
                {
                    string name = a["name"] as string ?? "";
                    string[] url = Urls(a);
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
            await Fetch(rel.LauncherUrl, fresh, p => progress("UPDATING LAUNCHER", p));
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

        // A release asset's URLs: GitHub's API one (asks for the file itself), then the plain download link.
        // (The plain links have failed on their own: 504 for every .exe on GitHub while zips still came.)
        private static string[] Urls(Dictionary<string, object> asset)
        {
            var urls = new List<string>();
            foreach (string key in new[] { "url", "browser_download_url" })
            {
                if (asset.TryGetValue(key, out object v) && v is string u && u.Length > 0)
                {
                    urls.Add(u);
                }
            }
            return urls.ToArray();
        }

        // The file from the first URL that gives it; the last one's error if none does.
        public static async Task Fetch(string[] urls, string file, Action<int> progress)
        {
            Exception last = new Exception("no download link");
            foreach (string url in urls ?? new string[0])
            {
                try
                {
                    using (var web = new WebClient())
                    {
                        web.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                        if (url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            web.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                        }
                        web.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                        await web.DownloadFileTaskAsync(new Uri(url), file);
                    }
                    if (new FileInfo(file).Length > 0)
                    {
                        return;
                    }
                    last = new Exception("the download came empty");
                }
                catch (Exception e)
                {
                    last = e;
                }
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
            throw last;
        }

        private static async Task<string> Download(string[] urls, string name, Action<string, int> progress, string what)
        {
            string folder = Path.Combine(Path.GetTempPath(), "KillNet");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name);
            await Fetch(urls, file, p => progress(what, p));
            return file;
        }

        // ---- installing ----

        public static async Task InstallBepInEx(string game, Action<string, int> progress)
        {
            progress("FINDING BEPINEX", -1);
            string[] url = null;
            string name = null;
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
                            url = Urls(a);
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
            await Task.Run(() => Extract(zip, game, null, p => progress("INSTALLING BEPINEX", p)));
        }

        public static async Task InstallMod(string game, Release rel, bool minecraft, Action<string, int> progress)
        {
            string[] url = minecraft ? rel.FullUrl : rel.NetUrl;
            string name = minecraft ? rel.FullName : rel.NetName;
            if (url == null)
            {
                throw new Exception($"no download in release {rel.Tag}");
            }
            string zip = await Download(url, name, progress, $"DOWNLOADING {rel.Version}");
            progress("INSTALLING " + rel.Version, 0);
            await Task.Run(() =>
            {
                string plugins = Path.Combine(game, "BepInEx", "plugins");
                Directory.CreateDirectory(plugins);
                Extract(zip, plugins, "Killcraft/", p => progress("INSTALLING " + rel.Version, p));
                File.WriteAllText(Path.Combine(ModFolder(game), "version.txt"), rel.Version);
            });
        }

        // Each file of the zip into the folder (only those under `only`, when given); nothing outside it.
        // (How far, by bytes written, as they're written: the full download is mostly one big file,
        // SkyCraft's Minecraft.)
        private static void Extract(string zip, string into, string only, Action<int> progress)
        {
            string root = Path.GetFullPath(into);
            using (ZipArchive z = ZipFile.OpenRead(zip))
            {
                var files = new List<(ZipArchiveEntry entry, string target)>();
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name.EndsWith("/") || (only != null && !name.StartsWith(only, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add((e, target));
                    }
                }
                long total = Math.Max(1, files.Sum(f => f.entry.Length)), done = 0;
                int shown = -1;
                var buffer = new byte[1 << 20];
                foreach (var (entry, target) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream from = entry.Open())
                    using (var to = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        int n;
                        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            to.Write(buffer, 0, n);
                            done += n;
                            int pct = (int)Math.Min(100, done * 100 / total);
                            if (pct != shown)
                            {
                                shown = pct;
                                progress(pct);
                            }
                        }
                    }
                    File.SetLastWriteTime(target, entry.LastWriteTime.DateTime);
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

        // (Read by the mod as it starts: the launcher chose KILLNET or KILLCRAFT already, so it doesn't ask.)
        public static void MarkLaunch(string game)
        {
            try
            {
                if (Directory.Exists(ModFolder(game)))
                {
                    File.WriteAllText(Path.Combine(ModFolder(game), "launcher-start.txt"), DateTime.UtcNow.Ticks.ToString());
                }
            }
            catch (Exception)
            {
            }
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
