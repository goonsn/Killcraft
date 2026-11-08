using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Killcraft
{
    // Starts SkyCraft's Minecraft (its portable Prism Launcher with the "SkyCraft" instance). That
    // instance runs with -Dskycraft.startHidden=true: it hides its window, waits on its title screen
    // until the host game is in a level, opens its world by itself and quits when the host closes.
    internal static class Launcher
    {
        public enum Status { Off, Starting, SignIn, Running, NoLauncher, Failed }

        public static volatile Status State = Status.Off;

        private static string PluginDir => Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        private static string Bundle => Path.Combine(PluginDir, "SkyCraft-Minecraft.zip");
        private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyCraft");

        public static bool MinecraftRunning()
        {
            IntPtr mutex = Native.OpenMutexW(Native.Synchronize, false, Proto.MappingName + "_minecraft");
            if (mutex == IntPtr.Zero)
            {
                return false;
            }
            Native.CloseHandle(mutex);
            return true;
        }

        public static void StartMinecraft()
        {
            if (MinecraftRunning())
            {
                Plugin.Log.LogInfo("Minecraft: already running");
                State = Status.Running;
                return;
            }
            string chosen = Environment.ExpandEnvironmentVariables(Plugin.LauncherPath.Value ?? "");
            bool bundled = chosen.Length == 0 && File.Exists(Bundle);
            if (chosen.Length == 0 && !bundled)
            {
                Plugin.Log.LogWarning($"Minecraft: not started: no SkyCraft-Minecraft.zip in {PluginDir} and no Launcher set in the config");
                State = Status.NoLauncher;
                return;
            }
            if (chosen.Length > 0 && !File.Exists(chosen))
            {
                Plugin.Log.LogWarning($"Minecraft: not started: {chosen} doesn't exist");
                State = Status.NoLauncher;
                return;
            }
            State = Status.Starting;
            string args = Plugin.LauncherArgs.Value;
            new Thread(() =>
            {
                try
                {
                    string program = bundled ? EnsureBundle() : chosen;
                    if (program == null)
                    {
                        State = Status.Failed;
                        return;
                    }
                    if (bundled && !File.Exists(Path.Combine(Path.GetDirectoryName(program), "accounts.json")))
                    {
                        State = Status.SignIn;
                    }
                    if (bundled && Plugin.PlayerSlot != 1 && !EnsureInstanceCopy(InstallDir))
                    {
                        State = Status.Failed;
                        return;
                    }
                    if (bundled)
                    {
                        McSave.Prepare(InstallDir);
                    }
                    Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program) });
                    Plugin.Log.LogInfo($"Minecraft: started {program} {args}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Minecraft: couldn't start: {e}");
                    State = Status.Failed;
                }
            }) { IsBackground = true, Name = "Killcraft launcher" }.Start();
        }

        // Player 2 (and up) on this PC: its own copy of SkyCraft's Prism instance (a Minecraft can only
        // run once per instance), made on its first start and given SkyCraft's mods again on each.
        // Its world, logs and screenshots are its own.
        private static bool EnsureInstanceCopy(string dir)
        {
            string instances = Path.Combine(dir, "Prism", "instances");
            string from = Path.Combine(instances, "SkyCraft");
            string to = Path.Combine(instances, Plugin.InstanceName);
            try
            {
                if (!Directory.Exists(from))
                {
                    Plugin.Log.LogError($"Minecraft: no {from} to copy for player {Plugin.PlayerSlot}; start Killcraft once normally first");
                    return false;
                }
                if (!File.Exists(Path.Combine(to, "instance.cfg")))
                {
                    // Made outside Prism's instances and moved in finished: a running Prism takes in a
                    // new instance as it appears (and saved the half-copied one's settings over it).
                    Plugin.Log.LogInfo($"Minecraft: making {to} for player {Plugin.PlayerSlot}");
                    string temp = Path.Combine(dir, "killcraft-instance-copy");
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, true);
                    }
                    CopyDir(from, temp, new[] { "saves", "logs", "crash-reports", "screenshots", "killcraft" });
                    string cfg = Path.Combine(temp, "instance.cfg");
                    var lines = new System.Collections.Generic.List<string>();
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        if (line.StartsWith("uuid=") || line.StartsWith("lastLaunchTime=") || line.StartsWith("totalTimePlayed=") || line.StartsWith("lastTimePlayed="))
                        {
                            continue;
                        }
                        lines.Add(line.StartsWith("name=") ? "name=" + Plugin.InstanceName
                            : line.StartsWith("notes=") ? $"notes=\"Killcraft's player {Plugin.PlayerSlot} on this PC (second-player.bat). A copy of SkyCraft's instance.\""
                            : line);
                    }
                    File.WriteAllLines(cfg, lines);
                    if (Directory.Exists(to))
                    {
                        Directory.Delete(to, true);
                    }
                    Directory.Move(temp, to);
                }
                string fromMods = Path.Combine(from, ".minecraft", "mods");
                string toMods = Path.Combine(to, ".minecraft", "mods");
                Directory.CreateDirectory(toMods);
                foreach (string jar in Directory.GetFiles(toMods, "*.jar"))
                {
                    if (!File.Exists(Path.Combine(fromMods, Path.GetFileName(jar))))
                    {
                        File.Delete(jar);
                    }
                }
                foreach (string jar in Directory.GetFiles(fromMods, "*.jar"))
                {
                    string target = Path.Combine(toMods, Path.GetFileName(jar));
                    var a = new FileInfo(jar);
                    if (!File.Exists(target) || new FileInfo(target).Length != a.Length || new FileInfo(target).LastWriteTimeUtc != a.LastWriteTimeUtc)
                    {
                        File.Copy(jar, target, true);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Minecraft: couldn't make {to}: {e}");
                return false;
            }
        }

        private static void CopyDir(string from, string to, string[] skip)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            }
            foreach (string sub in Directory.GetDirectories(from))
            {
                if (Array.IndexOf(skip, Path.GetFileName(sub)) < 0)
                {
                    CopyDir(sub, Path.Combine(to, Path.GetFileName(sub)), skip);
                }
            }
        }

        // Unpacks the bundle into %LOCALAPPDATA%\SkyCraft (shared with SkyCraft itself, so the
        // signed-in account and the downloaded Minecraft and Java are reused) when it is new or changed.
        private static string EnsureBundle()
        {
            string dir = InstallDir;
            string prism = Path.Combine(dir, "Prism", "prismlauncher.exe");
            var info = new FileInfo(Bundle);
            string stamp = $"{info.Length} {info.LastWriteTimeUtc.Ticks}";
            string stampFile = Path.Combine(dir, "killcraft-bundle.stamp");
            if (File.Exists(prism) && File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp)
            {
                return prism;
            }
            Plugin.Log.LogInfo($"Minecraft: unpacking SkyCraft's Minecraft to {dir}");
            Directory.CreateDirectory(dir);
            string mods = Path.Combine(dir, "Prism", "instances", "SkyCraft", ".minecraft", "mods");
            if (Directory.Exists(mods))
            {
                foreach (string jar in Directory.GetFiles(mods))
                {
                    string name = Path.GetFileName(jar);
                    if (name.StartsWith("skycraft-") || name.StartsWith("fabric-api-") || name.StartsWith("e4mc-"))
                    {
                        File.Delete(jar);
                    }
                }
            }
            string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            var unpack = Process.Start(new ProcessStartInfo(tar, $"-xf \"{Bundle}\" -C \"{dir}\"") { UseShellExecute = false, CreateNoWindow = true });
            unpack.WaitForExit(5 * 60 * 1000);
            if (unpack.ExitCode != 0 || !File.Exists(prism))
            {
                Plugin.Log.LogError($"Minecraft: unpacking failed (tar exit code {unpack.ExitCode})");
                return null;
            }
            string cfg = Path.Combine(dir, "Prism", "prismlauncher.cfg");
            if (!File.Exists(cfg))
            {
                File.Copy(Path.Combine(dir, "defaults", "prismlauncher.cfg"), cfg);
            }
            File.WriteAllText(stampFile, stamp);
            return prism;
        }
    }
}
