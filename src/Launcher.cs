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
