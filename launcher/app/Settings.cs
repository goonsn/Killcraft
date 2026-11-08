using System;
using System.Collections.Generic;
using System.IO;

namespace KillNetLauncher
{
    // The launcher's own settings: %APPDATA%\KillNet\launcher.cfg, one "key = value" a line.
    internal sealed class Settings
    {
        public string GamePath = "";
        public bool Minecraft;
        public bool AutoUpdate = true;
        public bool CloseOnPlay = true;
        public bool Sound = true;

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillNet");
        private static string FilePath => Path.Combine(Folder, "launcher.cfg");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return s;
                }
                var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("#"))
                    {
                        v[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
                if (v.TryGetValue("GamePath", out string g)) s.GamePath = g;
                if (v.TryGetValue("Minecraft", out string m)) s.Minecraft = m == "true";
                if (v.TryGetValue("AutoUpdate", out string a)) s.AutoUpdate = a != "false";
                if (v.TryGetValue("CloseOnPlay", out string c)) s.CloseOnPlay = c != "false";
                if (v.TryGetValue("Sound", out string so)) s.Sound = so != "false";
            }
            catch (Exception)
            {
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(FilePath, new[]
                {
                    "# KillNet launcher settings",
                    "GamePath = " + GamePath,
                    "Minecraft = " + (Minecraft ? "true" : "false"),
                    "AutoUpdate = " + (AutoUpdate ? "true" : "false"),
                    "CloseOnPlay = " + (CloseOnPlay ? "true" : "false"),
                    "Sound = " + (Sound ? "true" : "false"),
                });
            }
            catch (Exception)
            {
            }
        }
    }
}
