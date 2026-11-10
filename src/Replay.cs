using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // Replays (UltraLink's): each run kept from level start, saved at the level's end or on SAVE NOW.
    // A replay is what the players' ULTRAKILLs said to each other through the run (KillNet: where
    // each V1 was and what it did, its shots, its voice; where the enemies were, which woke and which
    // died, what broke), this player's own as it would have been sent (also alone), each with its
    // time; played back, the level is loaded and those messages are handled again as they came, so
    // every V1 and enemy is shown as multiplayer shows them (ReplayPlayer). Beside each replay, what
    // the REPLAYS screen and its CASE FILE show: the level, the players, the ranks, health through the
    // run, the checkpoints and arenas, the kills and what made them, each gun's time in hand, the style.
    [HarmonyPatch]
    internal static class Replay
    {
        // Not messages: things in the run itself, kept among them.
        // Which arm is out (0 the Feedbacker, 1 the Knuckleblaster), as ULTRAKILL keeps it.
        private static readonly HarmonyLib.AccessTools.FieldRef<FistControl, int> armNum = HarmonyLib.AccessTools.FieldRefAccess<FistControl, int>("currentVarNum");

        public static int ArmOf(FistControl fc)
        {
            try
            {
                return Mathf.Clamp(armNum(fc), 0, 254);
            }
            catch
            {
                return 255;
            }
        }

        public const byte AuthorityEvent = 250, RestartEvent = 251, CheckpointEvent = 252, MarkEvent = 253, HitEvent = 249, InputEvent = 248;

        // A hit on an enemy (as EnemySync's Damage message has it): played back as dealt.
        public static void EnemyHit(uint id, string part, Vector3 force, Vector3 point, float multiplier, float crit, byte flags, string hitter)
        {
            if (!Recording)
            {
                return;
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(id);
                w.Write(part ?? "");
                w.Write(force.x); w.Write(force.y); w.Write(force.z);
                w.Write(point.x); w.Write(point.y); w.Write(point.z);
                w.Write(multiplier);
                w.Write(crit);
                w.Write(flags);
                w.Write(hitter ?? "");
                w.Flush();
                byte[] b = ms.ToArray();
                Write(-1, HitEvent, b, 0, b.Length);
            }
        }
        private const string Magic = "KCR1";
        private const int Version = 1;

        [Serializable]
        public sealed class PlayerMeta
        {
            public int Id;
            public string Name = "", SteamId = "", Body = "", Lights = "", Wings = "";
            public int ColorIndex = -1;
        }

        [Serializable]
        public sealed class Header
        {
            public string Scene = "";
            public int Me, Authority;
            public float Length;
            // (Unity's JSON leaves an array of these out: kept as plain arrays, made into them on load.)
            [NonSerialized] public PlayerMeta[] Players = new PlayerMeta[0];
            public int[] PlayerIds = new int[0], PlayerColors = new int[0];
            public string[] PlayerNames = new string[0], PlayerSteam = new string[0], PlayerBody = new string[0], PlayerLights = new string[0], PlayerWings = new string[0];

            public void Pack()
            {
                int n = Players.Length;
                PlayerIds = new int[n]; PlayerColors = new int[n];
                PlayerNames = new string[n]; PlayerSteam = new string[n]; PlayerBody = new string[n]; PlayerLights = new string[n]; PlayerWings = new string[n];
                for (int k = 0; k < n; k++)
                {
                    PlayerMeta p = Players[k];
                    PlayerIds[k] = p.Id; PlayerColors[k] = p.ColorIndex;
                    PlayerNames[k] = p.Name ?? ""; PlayerSteam[k] = p.SteamId ?? ""; PlayerBody[k] = p.Body ?? ""; PlayerLights[k] = p.Lights ?? ""; PlayerWings[k] = p.Wings ?? "";
                }
            }

            public void Unpack()
            {
                int n = PlayerIds != null ? PlayerIds.Length : 0;
                Players = new PlayerMeta[n];
                for (int k = 0; k < n; k++)
                {
                    Players[k] = new PlayerMeta
                    {
                        Id = PlayerIds[k],
                        ColorIndex = At(PlayerColors, k, -1),
                        Name = At(PlayerNames, k), SteamId = At(PlayerSteam, k), Body = At(PlayerBody, k), Lights = At(PlayerLights, k), Wings = At(PlayerWings, k),
                    };
                }
            }

            private static string At(string[] a, int k) => a != null && k < a.Length && a[k] != null ? a[k] : "";
            private static int At(int[] a, int k, int or) => a != null && k < a.Length ? a[k] : or;
        }

        [Serializable]
        public sealed class Info
        {
            public string File = "", Scene = "", Level = "", Difficulty = "";
            public string[] Players = new string[0];
            public float Length, Seconds, Style;
            public int Kills;
            public string Rank = "", TimeRank = "", KillsRank = "", StyleRank = "";
            public long Date;
            public bool Finished;
            public int Restarts, SecretsFound, SecretsTotal, LowestHp = 100;
            public bool TookDamage, MajorUsed;
            public int[] TimeRanks = new int[0], KillRanks = new int[0], StyleRanks = new int[0];
            // Health, 4 a second.
            public int[] Hp = new int[0];
            // Checkpoints and arenas, with when.
            public float[] MarkTimes = new float[0];
            public string[] MarkLabels = new string[0];
            // MARK THIS MOMENT.
            public float[] Bookmarks = new float[0];
            public float[] KillTimes = new float[0];
            public string[] KillTypes = new string[0], KillWeapons = new string[0];
            public string[] Weapons = new string[0];
            public float[] WeaponSeconds = new float[0];
            public int[] WeaponColors = new int[0];
            public string[] StyleNames = new string[0];
            public int[] StyleCounts = new int[0], StylePoints = new int[0];
            // This player's own tags (the CASE FILE's TAGS; found by SEARCH).
            public string UserTags = "";

            public DateTime When => new DateTime(Date, DateTimeKind.Local);
            public string Key => Path.GetFileNameWithoutExtension(File);
            public string Picture => Path.ChangeExtension(File, ".png");
            public string Meta => Path.ChangeExtension(File, ".json");
        }

        public struct Rec
        {
            public float T;
            public int From;
            public byte Type;
            public byte[] Data;
        }

        private sealed class Run
        {
            public string Scene = "", Name = "", TmpPath = "";
            public DateTime Start;
            public StatsManager Stats;
            public FileStream Tmp;
            public BinaryWriter W;
            public float Clock, HpAt, PictureAt = 6f;
            public bool Pictured, SavedOnce, Done;
            public float FinishAt = -1f;
            public int InputBits = -1, InputArm = -1;
            public int LastAuthority = int.MinValue, Checkpoints, LowestHp = 100;
            public readonly List<int> Ids = new List<int>();
            public readonly List<int> Hp = new List<int>();
            public readonly List<float> MarkTimes = new List<float>(), Bookmarks = new List<float>(), KillTimes = new List<float>();
            public readonly List<string> MarkLabels = new List<string>(), KillTypes = new List<string>(), KillWeapons = new List<string>();
            public readonly Dictionary<string, float> WeaponSeconds = new Dictionary<string, float>();
            public readonly Dictionary<string, int> WeaponColors = new Dictionary<string, int>();
            public readonly Dictionary<string, int> StyleCounts = new Dictionary<string, int>(), StylePoints = new Dictionary<string, int>();
            public string Rank = "";
            public bool Finished;
        }

        private static Run run;
        private static string lastSaved;
        private static readonly object fileLock = new object();
        // File work off the main thread, one after another (a save, then the run's own file let go).
        private static System.Threading.Tasks.Task chain = System.Threading.Tasks.Task.FromResult(0);

        private static void Later(Action a)
        {
            lock (fileLock)
            {
                chain = chain.ContinueWith(_ =>
                {
                    try
                    {
                        a();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"replay: file work went wrong ({e.Message})");
                    }
                });
            }
        }

        public static string Folder => Path.Combine(BepInEx.Paths.PluginPath, "Killcraft", "replays");

        public static bool Recording => run != null && !run.Done && !ReplayPlayer.Active && !ReplayPlayer.Pending;

        public static bool Playing => ReplayPlayer.Active;

        public static string RunClock => run != null ? Clock(run.Clock) : "--:--";

        public static string LastSavedLabel => string.IsNullOrEmpty(lastSaved) ? "NO REPLAY SAVED YET" : lastSaved;

        public static string MarksLabel
        {
            get
            {
                if (run == null || run.Bookmarks.Count == 0)
                {
                    return "No marks yet.";
                }
                var lines = new List<string>();
                for (int i = 0; i < run.Bookmarks.Count; i++)
                {
                    lines.Add($"MARK {i + 1}  ·  {Clock(run.Bookmarks[i])}");
                }
                return string.Join("\n", lines);
            }
        }

        // What a replay keeps of what's said (the level's goings-on), and what's the level's alone
        // (not passed on while a replay plays: it's the replay's level now).
        public static bool Keeps(byte type)
        {
            switch (type)
            {
                case KillNet.PlayerState:
                case V1Body.Moves:
                case Shots.Fired:
                case WorldSync.Broke:
                case RemotePlayers.HeldMessage:
                case Voice.VoiceMessage:
                case Profiles.ProfileMessage:
                case Profiles.StatusMessage:
                case EnemySync.Snapshot:
                case EnemySync.Died:
                case EnemySync.Woken:
                case LevelEvents.Message:
                    return true;
                default:
                    return false;
            }
        }

        public static bool LevelType(byte type)
        {
            switch (type)
            {
                case KillNet.PlayerState:
                case V1Body.Moves:
                case Shots.Fired:
                case WorldSync.Broke:
                case RemotePlayers.HeldMessage:
                case EnemySync.Snapshot:
                case EnemySync.Damage:
                case EnemySync.Died:
                case EnemySync.Woken:
                case EnemySync.Reset:
                case Pvp.HitMessage:
                case Pvp.ParriedMessage:
                case Pvp.BledMessage:
                case Pvp.ShotParriedMessage:
                case Spectate.CheckpointMessage:
                case Spectate.ReviveMessage:
                case Spectate.LetInMessage:
                case SandboxSync.Placed:
                case SandboxSync.Place:
                case SandboxSync.Removed:
                case SandboxSync.Moved:
                case SandboxSync.Want:
                case SandboxSync.Changed:
                case LevelEvents.Message:
                    return true;
                default:
                    return false;
            }
        }

        // ---- recording ----

        public static void Frame(bool inGame, bool loading, bool paused, NewMovement nm)
        {
            try
            {
                if (ReplayPlayer.Active || ReplayPlayer.Pending)
                {
                    if (run != null)
                    {
                        End();
                    }
                    return;
                }
                string scene = SceneHelper.CurrentScene ?? "";
                StatsManager sm = inGame ? MonoSingleton<StatsManager>.Instance : null;
                if (run != null && !loading && (scene != run.Scene || !inGame || sm != run.Stats || !Plugin.ReplaysOn.Value))
                {
                    End();
                }
                if (run == null && inGame && !loading && nm != null && sm != null && Plugin.ReplaysOn.Value && Party.IsLevel(scene))
                {
                    Begin(scene, sm);
                }
                if (run == null || run.Done || paused || loading)
                {
                    return;
                }
                run.Clock += Mathf.Min(Time.unscaledDeltaTime, Time.maximumDeltaTime);
                // The fire buttons, the punch and the whiplash, and which arm is out (each change): played back
                // to the guns and the arms themselves.
                try
                {
                    PlayerInput input = MonoSingleton<InputManager>.Instance != null ? MonoSingleton<InputManager>.Instance.InputSource : null;
                    if (input != null)
                    {
                        int bits = (input.Fire1.IsPressed ? 1 : 0) | (input.Fire2.IsPressed ? 2 : 0) | (input.Punch.IsPressed ? 4 : 0) | (input.Hook.IsPressed ? 8 : 0);
                        FistControl fc = MonoSingleton<FistControl>.Instance;
                        int arm = fc != null ? ArmOf(fc) : 255;
                        if (bits != run.InputBits || arm != run.InputArm)
                        {
                            run.InputBits = bits;
                            run.InputArm = arm;
                            Write(-1, InputEvent, new[] { (byte)bits, (byte)arm }, 0, 2);
                        }
                    }
                }
                catch
                {
                }
                // (The level's end: on a few seconds more, falling down its pit, then saved.)
                if (run.FinishAt > 0f && run.Clock >= run.FinishAt && !run.Done)
                {
                    Save(true);
                    PartyChat.Event($"REPLAY SAVED ({Clock(run.Clock)})");
                    return;
                }
                int authority = Room.Shared ? Room.AuthorityId : Room.MyId;
                if (authority != run.LastAuthority)
                {
                    run.LastAuthority = authority;
                    Write(-1, AuthorityEvent, BitConverter.GetBytes(authority), 0, 4);
                }
                if (nm != null && run.Clock >= run.HpAt)
                {
                    run.HpAt = run.Clock + 0.25f;
                    int hp = nm.dead ? 0 : Mathf.Clamp(nm.hp, 0, 200);
                    run.Hp.Add(hp);
                    run.LowestHp = Mathf.Min(run.LowestHp, hp);
                }
                if (nm != null && !nm.dead)
                {
                    string weapon = WeaponName(out int color);
                    if (weapon.Length > 0)
                    {
                        run.WeaponSeconds.TryGetValue(weapon, out float s);
                        run.WeaponSeconds[weapon] = s + Time.unscaledDeltaTime;
                        run.WeaponColors[weapon] = color;
                    }
                }
                if (!run.Pictured && run.Clock >= run.PictureAt && Host.Current != null)
                {
                    run.Pictured = true;
                    Host.Current.StartCoroutine(TakePicture(Path.Combine(Folder, run.Name + ".png")));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: recording went wrong ({e.Message}); this run isn't kept");
                Discard();
            }
        }

        private static void Begin(string scene, StatsManager sm)
        {
            Directory.CreateDirectory(Folder);
            DateTime now = DateTime.Now;
            var r = new Run
            {
                Scene = scene,
                Stats = sm,
                Start = now,
                Name = $"{now:yyyyMMdd-HHmmss} {Safe(scene)}",
            };
            r.TmpPath = Path.Combine(Folder, $".recording-{now.Ticks}.tmp");
            r.Tmp = new FileStream(r.TmpPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            r.W = new BinaryWriter(r.Tmp);
            run = r;
            Note(Room.MyId);
            Plugin.Log.LogInfo($"replay: recording {scene}");
        }

        // The run over: kept if it was saved (to where it got), else let go.
        private static void End()
        {
            Run r = run;
            if (r == null)
            {
                return;
            }
            if (r.FinishAt > 0f && !r.Done)
            {
                Save(true);
            }
            else if (r.SavedOnce && !r.Done)
            {
                Save(false);
            }
            Plugin.Log.LogInfo($"replay: stopped recording ({Clock(r.Clock)}{(r.SavedOnce ? ", saved" : ", not saved")})");
            Close(r, true);
            run = null;
        }

        private static void Discard()
        {
            if (run != null)
            {
                Close(run, true);
            }
            run = null;
        }

        private static void Close(Run r, bool delete)
        {
            try
            {
                lock (fileLock)
                {
                    r.W?.Flush();
                    r.Tmp?.Dispose();
                }
                string tmp = r.TmpPath;
                if (delete && File.Exists(tmp))
                {
                    // (After any save still copying it.)
                    Later(() => File.Delete(tmp));
                }
            }
            catch
            {
            }
        }

        private static void Note(int id)
        {
            if (run != null && !run.Ids.Contains(id))
            {
                run.Ids.Add(id);
            }
        }

        private static void Write(int from, byte type, byte[] data, int offset, int length)
        {
            Run r = run;
            if (r == null || r.Done || length < 0 || length > 65535)
            {
                return;
            }
            lock (fileLock)
            {
                r.W.Write(r.Clock);
                r.W.Write(from);
                r.W.Write(type);
                r.W.Write((ushort)length);
                r.W.Write(data, offset, length);
            }
        }

        // Another player's message, as it came.
        public static void Heard(int from, byte type, byte[] data, int offset)
        {
            if (!Recording || from < 0 || !Keeps(type))
            {
                return;
            }
            Note(from);
            Write(from, type, data, offset, data.Length - offset);
        }

        // This player's own, as it goes (or would: alone, nothing goes).
        public static void Sent(byte type, byte[] data, int offset)
        {
            if (!Recording || !Keeps(type))
            {
                return;
            }
            Note(Room.MyId);
            Write(Room.MyId, type, data, offset, data.Length - offset);
        }

        public static void SaveNow()
        {
            if (!Recording && (run == null || !run.Done))
            {
                PartyChat.System("Nothing to save: replays record in a level.");
                return;
            }
            Save(false);
            PartyChat.Event($"REPLAY SAVED ({Clock(run.Clock)})");
        }

        // A moment in this run marked (a bookmark on the replay's timeline).
        public static void Mark()
        {
            if (!Recording)
            {
                PartyChat.System("Marks are for a run being recorded (in a level).");
                return;
            }
            run.Bookmarks.Add(run.Clock);
            Write(-1, MarkEvent, new byte[0], 0, 0);
            Plugin.Log.LogInfo($"replay: marked {Clock(run.Clock)}");
            PartyChat.Event($"MARKED {Clock(run.Clock)}");
        }

        // Written out: the messages (copied from the run's own file as it is), what's needed to play
        // them, and what the REPLAYS screen shows.
        private static void Save(bool final)
        {
            Run r = run;
            if (r == null)
            {
                return;
            }
            try
            {
                Info info = MakeInfo(r, final);
                var header = new Header { Scene = r.Scene, Me = Room.MyId, Authority = r.LastAuthority == int.MinValue ? Room.MyId : r.LastAuthority, Length = r.Clock };
                var players = new List<PlayerMeta>();
                foreach (int id in r.Ids)
                {
                    Profiles.Info p = id == Room.MyId ? Profiles.Mine : Profiles.Of(id);
                    players.Add(new PlayerMeta
                    {
                        Id = id,
                        Name = Profiles.NameOf(id),
                        SteamId = p.SteamId.ToString(),
                        ColorIndex = p.ColorIndex,
                        Body = Hex(p.Body),
                        Lights = Hex(p.Lights),
                        Wings = Hex(p.Wings),
                    });
                }
                if (players.Count == 0)
                {
                    players.Add(new PlayerMeta { Id = Room.MyId, Name = Profiles.NameOf(Room.MyId) });
                }
                header.Players = players.ToArray();
                header.Pack();
                string headerJson = JsonUtility.ToJson(header);
                string metaJson = JsonUtility.ToJson(info, true);
                string kcr = info.File, meta = info.Meta, tmp = r.TmpPath;
                long length;
                lock (fileLock)
                {
                    r.W.Flush();
                    length = r.Tmp.Length;
                }
                r.SavedOnce = true;
                if (final)
                {
                    r.Done = true;
                }
                lastSaved = $"{Party.LevelName(r.Scene).ToUpperInvariant()}  ·  {Clock(r.Clock)}";
                Later(() =>
                {
                    try
                    {
                        {
                            string part = kcr + ".part";
                            using (var src = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var dst = new FileStream(part, FileMode.Create))
                            using (var gz = new GZipStream(dst, System.IO.Compression.CompressionLevel.Fastest))
                            using (var w = new BinaryWriter(gz))
                            {
                                w.Write(Magic);
                                w.Write(Version);
                                w.Write(headerJson);
                                w.Flush();
                                var buffer = new byte[65536];
                                long left = length;
                                while (left > 0)
                                {
                                    int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                                    if (n <= 0)
                                    {
                                        break;
                                    }
                                    gz.Write(buffer, 0, n);
                                    left -= n;
                                }
                            }
                            if (File.Exists(kcr))
                            {
                                File.Delete(kcr);
                            }
                            File.Move(part, kcr);
                            File.WriteAllText(meta, metaJson);
                        }
                        Plugin.Log.LogInfo($"replay: saved {Path.GetFileName(kcr)} ({new FileInfo(kcr).Length / 1024} KB{(final ? ", the level's end" : "")})");
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"replay: couldn't save {Path.GetFileName(kcr)} ({e.Message})");
                    }
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't save ({e.Message})");
            }
        }

        private static Info MakeInfo(Run r, bool final)
        {
            var i = new Info
            {
                File = Path.Combine(Folder, r.Name + ".kcr"),
                Scene = r.Scene,
                Level = Party.LevelName(r.Scene).ToUpperInvariant(),
                Length = r.Clock,
                Date = r.Start.Ticks,
                Finished = r.Finished,
                Rank = r.Rank,
                LowestHp = r.LowestHp,
                Hp = r.Hp.ToArray(),
                MarkTimes = r.MarkTimes.ToArray(),
                MarkLabels = r.MarkLabels.ToArray(),
                Bookmarks = r.Bookmarks.ToArray(),
                KillTimes = r.KillTimes.ToArray(),
                KillTypes = r.KillTypes.ToArray(),
                KillWeapons = r.KillWeapons.ToArray(),
            };
            var names = new List<string>();
            foreach (int id in r.Ids)
            {
                names.Add(Profiles.NameOf(id));
            }
            i.Players = names.ToArray();
            try
            {
                int d = MonoSingleton<PrefsManager>.Instance.GetInt("difficulty");
                string[] difficulties = { "HARMLESS", "LENIENT", "STANDARD", "VIOLENT", "BRUTAL", "UKMD" };
                i.Difficulty = d >= 0 && d < difficulties.Length ? difficulties[d] : "";
            }
            catch
            {
            }
            StatsManager sm = r.Stats;
            if (sm != null)
            {
                i.Seconds = sm.seconds;
                i.Kills = sm.kills;
                i.Style = sm.stylePoints;
                i.Restarts = sm.restarts;
                i.TookDamage = sm.tookDamage;
                i.MajorUsed = sm.majorUsed;
                i.SecretsFound = sm.secrets;
                i.SecretsTotal = sm.secretObjects != null ? sm.secretObjects.Length : 0;
                i.TimeRanks = sm.timeRanks ?? new int[0];
                i.KillRanks = sm.killRanks ?? new int[0];
                i.StyleRanks = sm.styleRanks ?? new int[0];
                try
                {
                    i.TimeRank = Plain(sm.GetRanks(sm.timeRanks, sm.seconds, true));
                    i.KillsRank = Plain(sm.GetRanks(sm.killRanks, sm.kills, false));
                    i.StyleRank = Plain(sm.GetRanks(sm.styleRanks, sm.stylePoints, false));
                }
                catch
                {
                }
            }
            var weapons = new List<KeyValuePair<string, float>>(r.WeaponSeconds);
            weapons.Sort((a, b) => b.Value.CompareTo(a.Value));
            i.Weapons = new string[weapons.Count];
            i.WeaponSeconds = new float[weapons.Count];
            i.WeaponColors = new int[weapons.Count];
            for (int k = 0; k < weapons.Count; k++)
            {
                i.Weapons[k] = weapons[k].Key;
                i.WeaponSeconds[k] = weapons[k].Value;
                r.WeaponColors.TryGetValue(weapons[k].Key, out i.WeaponColors[k]);
            }
            var style = new List<KeyValuePair<string, int>>(r.StylePoints);
            style.Sort((a, b) => b.Value.CompareTo(a.Value));
            i.StyleNames = new string[style.Count];
            i.StyleCounts = new int[style.Count];
            i.StylePoints = new int[style.Count];
            for (int k = 0; k < style.Count; k++)
            {
                i.StyleNames[k] = style[k].Key;
                i.StylePoints[k] = style[k].Value;
                r.StyleCounts.TryGetValue(style[k].Key, out i.StyleCounts[k]);
            }
            return i;
        }

        // A picture of the run for its row (a band across the middle of the screen, a few seconds in).
        private static System.Collections.IEnumerator TakePicture(string path)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = null;
            RenderTexture rt = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                const int W = 640, H = 124;
                float band = (float)shot.width / W * H / shot.height;
                rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(shot, rt, new Vector2(1f, band), new Vector2(0f, 0.5f - band / 2f));
                RenderTexture was = RenderTexture.active;
                RenderTexture.active = rt;
                var pic = new Texture2D(W, H, TextureFormat.RGB24, false);
                pic.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                pic.Apply();
                RenderTexture.active = was;
                byte[] png = ImageConversion.EncodeToPNG(pic);
                UnityEngine.Object.Destroy(pic);
                File.WriteAllBytes(path, png);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't take the run's picture ({e.Message})");
            }
            finally
            {
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (shot != null)
                {
                    UnityEngine.Object.Destroy(shot);
                }
            }
        }

        // ---- what the run had: kills, style, checkpoints, arenas, restarts, its end ----

        [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.Death), new[] { typeof(bool) }), HarmonyPrefix]
        private static void DeathBefore(EnemyIdentifier __instance, out int __state)
        {
            __state = -1;
            try
            {
                if (Recording && __instance != null && !__instance.dead && run.Stats != null)
                {
                    __state = run.Stats.kills;
                }
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.Death), new[] { typeof(bool) }), HarmonyPostfix]
        private static void DeathAfter(EnemyIdentifier __instance, int __state)
        {
            try
            {
                if (__state < 0 || !Recording || run.Stats == null || run.Stats.kills <= __state || __instance == null)
                {
                    return;
                }
                run.KillTimes.Add(run.Clock);
                run.KillTypes.Add(__instance.enemyType.ToString());
                run.KillWeapons.Add(WeaponName(out _));
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(StyleHUD), nameof(StyleHUD.AddPoints)), HarmonyPrefix]
        private static void StyleBefore(out int __state)
        {
            __state = -1;
            try
            {
                if (Recording && run.Stats != null)
                {
                    __state = run.Stats.stylePoints;
                }
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(StyleHUD), nameof(StyleHUD.AddPoints)), HarmonyPostfix]
        private static void StyleAfter(StyleHUD __instance, string pointID, int __state)
        {
            try
            {
                if (__state < 0 || !Recording || run.Stats == null || string.IsNullOrEmpty(pointID))
                {
                    return;
                }
                int gained = run.Stats.stylePoints - __state;
                if (gained <= 0)
                {
                    return;
                }
                string name = Plain(__instance.GetLocalizedName(pointID)).Trim().ToUpperInvariant();
                if (name.Length == 0)
                {
                    return;
                }
                run.StyleCounts.TryGetValue(name, out int c);
                run.StyleCounts[name] = c + 1;
                run.StylePoints.TryGetValue(name, out int p);
                run.StylePoints[name] = p + gained;
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.ActivateCheckPoint)), HarmonyPostfix]
        private static void CheckpointReached(CheckPoint __instance)
        {
            try
            {
                if (!Recording || __instance == null)
                {
                    return;
                }
                run.Checkpoints++;
                run.MarkTimes.Add(run.Clock);
                run.MarkLabels.Add($"CP {run.Checkpoints}");
                Write(-1, CheckpointEvent, BitConverter.GetBytes(EnemySync.PathId(__instance.transform)), 0, 4);
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.OnRespawn)), HarmonyPostfix]
        private static void Restarted()
        {
            try
            {
                if (Recording)
                {
                    Write(-1, RestartEvent, new byte[0], 0, 0);
                }
            }
            catch
            {
            }
        }

        // A boss's health bar up: the boss marked (its name) on the timeline.
        [HarmonyPatch(typeof(BossHealthBar), "Start"), HarmonyPostfix]
        private static void BossAppeared(BossHealthBar __instance)
        {
            try
            {
                if (!Recording || __instance == null)
                {
                    return;
                }
                string name = (__instance.bossName ?? "BOSS").Trim().ToUpperInvariant();
                if (name.Length == 0)
                {
                    name = "BOSS";
                }
                if (run.MarkLabels.Contains(name))
                {
                    return;
                }
                run.MarkTimes.Add(run.Clock);
                run.MarkLabels.Add(name);
            }
            catch
            {
            }
        }

        [HarmonyPatch(typeof(ActivateArena), nameof(ActivateArena.Activate)), HarmonyPrefix]
        private static void ArenaBefore(ActivateArena __instance, out bool __state) => __state = __instance != null && __instance.activated;

        [HarmonyPatch(typeof(ActivateArena), nameof(ActivateArena.Activate)), HarmonyPostfix]
        private static void ArenaAfter(ActivateArena __instance, bool __state)
        {
            try
            {
                if (Recording && __instance != null && !__state && __instance.activated
                    && (run.MarkTimes.Count == 0 || run.Clock - run.MarkTimes[run.MarkTimes.Count - 1] > 3f))
                {
                    run.MarkTimes.Add(run.Clock);
                    run.MarkLabels.Add("ARENA");
                }
            }
            catch
            {
            }
        }

        // The level's end (its ranks shown): the run saved, with them.
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SendInfo)), HarmonyPostfix]
        private static void LevelEnd()
        {
            try
            {
                if (!Recording)
                {
                    return;
                }
                FinalRank fr = MonoSingleton<FinalRank>.Instance;
                run.Rank = fr != null && fr.totalRank != null ? Plain(fr.totalRank.text).Trim() : "";
                run.Finished = true;
                if (run.FinishAt < 0f)
                {
                    run.FinishAt = run.Clock + 8f;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't save at the level's end ({e.Message})");
            }
        }

        // ---- names ----

        private static readonly string[] Kinds = { "REVOLVER", "SHOTGUN", "NAILGUN", "RAILCANNON", "ROCKET LAUNCHER" };
        private static readonly string[] AltKinds = { "ALT REVOLVER", "JACKHAMMER", "SAWBLADE LAUNCHER", "RAILCANNON", "ROCKET LAUNCHER" };

        // "REVOLVER · MARKSMAN": the gun in hand (none: "").
        private static string WeaponName(out int color)
        {
            color = 0;
            var gc = MonoSingleton.GetInstance(typeof(GunControl)) as GunControl;
            GameObject w = gc != null ? gc.currentWeapon : null;
            if (w == null || !w.activeInHierarchy)
            {
                return "";
            }
            int slot = gc.currentSlotIndex - 1;
            bool alt = w.TryGetComponent(out WeaponIdentifier wid) && wid.alternateVersion;
            string kind = slot >= 0 && slot < Kinds.Length ? (alt ? AltKinds[slot] : Kinds[slot]) : w.name.ToUpperInvariant();
            string variant = "";
            if (w.TryGetComponent(out WeaponIcon icon) && icon.weaponDescriptor != null)
            {
                variant = (icon.weaponDescriptor.weaponName ?? "").ToUpperInvariant();
                color = (int)icon.weaponDescriptor.variationColor;
            }
            return variant.Length > 0 && variant != kind ? $"{kind} · {variant}" : kind;
        }

        private static string Plain(string s) => Regex.Replace(s ?? "", "<.*?>", "");

        private static string Hex(Color32 c) => c.a == 0 ? "" : ColorUtility.ToHtmlStringRGB(c);

        private static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return s;
        }

        // ---- the saved ones ----

        public static string FolderFor(Info i) => Path.GetDirectoryName(i.File) ?? Folder;

        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Application.OpenURL("file:///" + Folder.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't open the folder ({e.Message})");
            }
        }

        // The saved replays, newest first (each a .kcr with a .json beside it).
        public static List<Info> List()
        {
            var list = new List<Info>();
            try
            {
                if (!Directory.Exists(Folder))
                {
                    return list;
                }
                foreach (string f in Directory.GetFiles(Folder, "*.json"))
                {
                    try
                    {
                        string kcr = Path.ChangeExtension(f, ".kcr");
                        if (!File.Exists(kcr))
                        {
                            continue;
                        }
                        Info i = JsonUtility.FromJson<Info>(File.ReadAllText(f));
                        if (i != null)
                        {
                            i.File = kcr;
                            list.Add(i);
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"replay: couldn't read {Path.GetFileName(f)} ({e.Message})");
                    }
                }
                list.Sort((a, b) => b.Date.CompareTo(a.Date));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't list the replays ({e.Message})");
            }
            return list;
        }

        public static Info Find(string key)
        {
            foreach (Info i in List())
            {
                if (i.Key == key)
                {
                    return i;
                }
            }
            return null;
        }

        public static void Delete(Info i)
        {
            try
            {
                foreach (string f in new[] { i.File, i.Meta, i.Picture })
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                    }
                }
                Plugin.Log.LogInfo($"replay: deleted {Path.GetFileName(i.File)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't delete {Path.GetFileName(i.File)} ({e.Message})");
            }
        }

        // A replay's messages, read back.
        public static bool Load(Info i, out Header header, out List<Rec> recs)
        {
            header = null;
            recs = new List<Rec>();
            try
            {
                using (var fs = new FileStream(i.File, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(new BufferedStream(gz, 65536)))
                {
                    if (r.ReadString() != Magic)
                    {
                        throw new Exception("not a Killcraft replay");
                    }
                    int version = r.ReadInt32();
                    if (version > Version)
                    {
                        throw new Exception($"made by a newer Killcraft (version {version})");
                    }
                    header = JsonUtility.FromJson<Header>(r.ReadString());
                    header?.Unpack();
                    while (true)
                    {
                        float t;
                        try
                        {
                            t = r.ReadSingle();
                        }
                        catch (EndOfStreamException)
                        {
                            break;
                        }
                        int from = r.ReadInt32();
                        byte type = r.ReadByte();
                        int n = r.ReadUInt16();
                        byte[] data = r.ReadBytes(n);
                        if (data.Length < n)
                        {
                            break;
                        }
                        recs.Add(new Rec { T = t, From = from, Type = type, Data = data });
                    }
                }
                return header != null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"replay: couldn't read {Path.GetFileName(i.File)} ({e.Message})");
                return false;
            }
        }

        public static string Clock(float seconds)
        {
            int m = (int)(seconds / 60f);
            int s = (int)(seconds - m * 60f);
            return $"{m}:{s:00}";
        }
    }
}
