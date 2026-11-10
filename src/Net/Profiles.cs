using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Killcraft
{
    // Who each player in the world is and how they're doing, for the party menu, the KILLCRAFT panel
    // and the party's HUD: their Steam account (name, picture), their colour and V1's, their guns'
    // paint (each player's own GunColorController), and, a few times a second, their status (health,
    // dead, a checkpoint just reached, a skull held, the level finished: its rank and time). And how
    // far away they are (a ping, there and back through the Minecraft server).
    internal static class Profiles
    {
        public const byte ProfileMessage = 31, StatusMessage = 32, PingMessage = 34, PongMessage = 35;
        public const byte StateMenu = 0, StatePlaying = 1, StateDead = 2, StateDone = 3;
        public const byte FlagCheckpoint = 1, FlagBlueSkull = 2, FlagRedSkull = 4, FlagUnderwater = 8, FlagPaused = 16, FlagSpectating = 32, FlagRevivable = 64, FlagWatching = 128;

        public static readonly (string Name, Color Color)[] Colors =
        {
            ("RED", new Color(1f, 0.22f, 0.2f)),
            ("ORANGE", new Color(1f, 0.55f, 0.15f)),
            ("YELLOW", new Color(1f, 0.88f, 0.2f)),
            ("GREEN", new Color(0.3f, 0.95f, 0.35f)),
            ("CYAN", new Color(0.25f, 0.8f, 1f)),
            ("BLUE", new Color(0.35f, 0.5f, 1f)),
            ("PURPLE", new Color(0.62f, 0.35f, 1f)),
            ("PINK", new Color(1f, 0.45f, 0.8f)),
            ("WHITE", new Color(0.95f, 0.95f, 0.95f)),
        };


        public sealed class Info
        {
            public int Id;
            public ulong SteamId;
            public string SteamName = "";
            public int ColorIndex = -1;
            public Color32 Body, Lights, Wings;  // (alpha 0: V1's own)
            // Each gun's paint (revolver .. rocket launcher, then the alternate ones): painted or
            // ULTRAKILL's own look, and its three colours.
            public readonly bool[] Painted = new bool[10];
            public readonly Color[] Paint = new Color[30];
            public int PaintVersion;
            public float ProfileAt = -100f;
            // Their party's code (the world's owner's, its world open).
            public string Code = "";

            public byte State;
            public int Hp = 100;
            public byte Flags;
            public string Scene = "";
            public float Seconds;
            public string Rank = "";
            public int Difficulty = -1;
            public float StatusAt = -100f;

            public float Ping = -1f;
            public Texture2D Avatar;
            public bool AvatarAsked;
            public bool Muted;
            public float Volume = 1f;

            public Color Color => ColorIndex >= 0 && ColorIndex < Colors.Length ? Colors[ColorIndex].Color
                : Colors[(int)((SteamId != 0 ? SteamId : (ulong)(uint)Id) % (ulong)Colors.Length)].Color;

            public bool Fresh => Time.unscaledTime - StatusAt < 3f;
        }

        private static readonly Dictionary<int, Info> others = new Dictionary<int, Info>();
        public static readonly Info Mine = new Info();
        private static float statusAt = -100f, profileAt = -100f, pingAt = -100f, paintAt = -100f;
        private static string sentProfile = "";
        private static float checkpointAt = -100f;

        // A player's (this one's own for its own id).
        public static Info Of(int id)
        {
            if (id == Room.MyId && id != 0)
            {
                Mine.Id = id;
                return Mine;
            }
            if (!others.TryGetValue(id, out Info i))
            {
                others[id] = i = new Info { Id = id };
            }
            return i;
        }

        public static bool Has(int id) => id == Room.MyId || others.ContainsKey(id);

        // Everyone known: this player, then the others (Skins: the looks still worn).
        public static IEnumerable<Info> All()
        {
            yield return Mine;
            foreach (Info i in others.Values)
            {
                yield return i;
            }
        }

        // A player's name: their Steam name, else their Minecraft one.
        public static string NameOf(int id)
        {
            Info i = id == Room.MyId ? Mine : others.TryGetValue(id, out Info o) ? o : null;
            if (i != null && !string.IsNullOrEmpty(i.SteamName))
            {
                return i.SteamName;
            }
            return Party.NameOf(id);
        }

        public static string Colored(int id) => $"<color=#{Ui.Hex(Of(id).Color)}>{Typing.Escape(NameOf(id))}</color>";

        public static string DifficultyName(int d)
        {
            switch (d)
            {
                case 0: return "HARMLESS";
                case 1: return "LENIENT";
                case 2: return "STANDARD";
                case 3: return "VIOLENT";
                case 4: return "BRUTAL";
                case 5: return "UKMD";
                default: return "";
            }
        }

        public static int MyDifficulty()
        {
            try
            {
                return MonoSingleton<PrefsManager>.Instance.GetInt("difficulty");
            }
            catch
            {
                return -1;
            }
        }

        // ---- this player ----

        public static void Frame(NewMovement nm, bool inLevel, bool paused)
        {
            try
            {
                Me(nm, inLevel, paused);
                if (!KillNet.Connected)
                {
                    return;
                }
                if (Time.unscaledTime - statusAt > 0.25f)
                {
                    statusAt = Time.unscaledTime;
                    SendStatus();
                }
                string p = ProfileKey();
                if (p != sentProfile || Time.unscaledTime - profileAt > 3f)
                {
                    sentProfile = p;
                    profileAt = Time.unscaledTime;
                    SendProfile();
                }
                if (Time.unscaledTime - pingAt > 2f && Party.Members.Count > 1)
                {
                    pingAt = Time.unscaledTime;
                    float now = Time.unscaledTime;
                    KillNet.Send(PingMessage, KillNet.ToEveryone, w => w.Write(now));
                }
                Avatars();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - profileAt > 30f)
                {
                    profileAt = Time.unscaledTime;
                    Plugin.Log.LogWarning($"profiles: {e.Message}");
                }
            }
        }

        private static void Me(NewMovement nm, bool inLevel, bool paused)
        {
            Info m = Mine;
            m.Id = Room.MyId;
            if (m.SteamId == 0 && ServerList.SteamUp)
            {
                try
                {
                    m.SteamId = SteamClient.SteamId.Value;
                    m.SteamName = SteamClient.Name ?? "";
                }
                catch
                {
                }
            }
            if (string.IsNullOrEmpty(m.SteamName))
            {
                m.SteamName = Party.MyName;
            }
            m.ColorIndex = Plugin.MyColor.Value;
            m.Code = ServerList.Code;
            m.Body = ParseColor(Plugin.BodyColour.Value);
            m.Lights = ParseColor(Plugin.LightsColour.Value);
            m.Wings = ParseColor(Plugin.WingsColour.Value);
            if (Time.unscaledTime - paintAt > 2f)
            {
                paintAt = Time.unscaledTime;
                ReadPaint(m);
            }
            m.Scene = SceneHelper.CurrentScene ?? "";
            m.Difficulty = MyDifficulty();
            m.Flags = 0;
            if (!inLevel || nm == null)
            {
                m.State = StateMenu;
                m.Hp = 100;
                return;
            }
            m.Hp = nm.hp;
            m.State = nm.dead ? StateDead : StatePlaying;
            StatsManager sm = MonoSingleton<StatsManager>.Instance;
            if (sm != null)
            {
                m.Seconds = sm.seconds;
                if (sm.infoSent && !sm.endlessMode)
                {
                    m.State = StateDone;
                    if (string.IsNullOrEmpty(m.Rank))
                    {
                        m.Rank = FinalRankLetter();
                    }
                }
                else
                {
                    m.Rank = "";
                }
            }
            if (Time.unscaledTime - checkpointAt < 4f)
            {
                m.Flags |= FlagCheckpoint;
            }
            try
            {
                FistControl fc = MonoSingleton<FistControl>.Instance;
                if (fc != null && fc.heldObject != null)
                {
                    if (fc.heldObject.itemType == ItemType.SkullBlue)
                    {
                        m.Flags |= FlagBlueSkull;
                    }
                    else if (fc.heldObject.itemType == ItemType.SkullRed)
                    {
                        m.Flags |= FlagRedSkull;
                    }
                }
            }
            catch
            {
            }
            try
            {
                UnderwaterController uw = MonoSingleton<UnderwaterController>.Instance;
                if (uw != null && uw.inWater)
                {
                    m.Flags |= FlagUnderwater;
                }
            }
            catch
            {
            }
            if (paused)
            {
                m.Flags |= FlagPaused;
            }
            if (Spectate.Active)
            {
                m.Flags |= FlagSpectating;
            }
            if (Spectate.Revivable)
            {
                m.Flags |= FlagRevivable;
            }
            if (Spectate.IsWatching)
            {
                m.Flags |= FlagWatching;
            }
        }

        private static string FinalRankLetter()
        {
            try
            {
                FinalRank fr = MonoSingleton<FinalRank>.Instance;
                if (fr != null && fr.totalRank != null)
                {
                    string t = System.Text.RegularExpressions.Regex.Replace(fr.totalRank.text ?? "", "<.*?>", "").Trim();
                    return t.Length > 3 ? t.Substring(0, 3) : t;
                }
            }
            catch
            {
            }
            return "";
        }

        // This player's guns' paint, as ULTRAKILL's GunColorGetter picks it.
        // A hex colour from the settings ("7A3FD1"), or V1's own (alpha 0).
        public static Color32 ParseColor(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 6 && ColorUtility.TryParseHtmlString("#" + hex, out Color c))
            {
                return new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), 255);
            }
            return new Color32(0, 0, 0, 0);
        }

        private static void WriteColor(BinaryWriter w, Color32 c)
        {
            w.Write(c.r);
            w.Write(c.g);
            w.Write(c.b);
            w.Write(c.a);
        }

        private static Color32 ReadColor(BinaryReader r)
        {
            byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte(), ca = r.ReadByte();
            return new Color32(cr, cg, cb, ca);
        }

        private static void ReadPaint(Info m)
        {
            GunColorController gcc = MonoSingleton<GunColorController>.Instance;
            if (gcc == null)
            {
                return;
            }
            int version = 17;
            for (int i = 0; i < 10; i++)
            {
                int weapon = i % 5 + 1;
                bool alt = i >= 5;
                bool painted = false;
                GunColorPreset colors = null;
                try
                {
                    int[] presets = alt ? gcc.altPresets : gcc.presets;
                    GunColorPreset[] current = alt ? gcc.currentAltColors : gcc.currentColors;
                    bool custom = gcc.hasUnlockedColors != null && weapon <= gcc.hasUnlockedColors.Length && gcc.hasUnlockedColors[weapon - 1]
                        && MonoSingleton<PrefsManager>.Instance.GetBool("gunColorType." + weapon + (alt ? ".a" : ""));
                    painted = custom || (presets != null && weapon <= presets.Length && presets[weapon - 1] != 0);
                    colors = current != null && weapon <= current.Length ? current[weapon - 1] : null;
                }
                catch
                {
                }
                m.Painted[i] = painted && colors != null;
                if (colors != null)
                {
                    m.Paint[i * 3] = colors.color1;
                    m.Paint[i * 3 + 1] = colors.color2;
                    m.Paint[i * 3 + 2] = colors.color3;
                }
                version = version * 31 + (m.Painted[i] ? 1 : 0);
                if (m.Painted[i])
                {
                    for (int k = 0; k < 3; k++)
                    {
                        Color c = m.Paint[i * 3 + k];
                        version = version * 31 + (int)(c.r * 255f) * 65536 + (int)(c.g * 255f) * 256 + (int)(c.b * 255f);
                    }
                }
            }
            m.PaintVersion = version;
        }

        private static string ProfileKey() => $"{Mine.SteamId}|{Mine.SteamName}|{Mine.ColorIndex}|{Skins.LookKey(Mine)}|{Mine.PaintVersion}|{Mine.Code}";

        private static void SendProfile()
        {
            Info m = Mine;
            KillNet.Send(ProfileMessage, KillNet.ToEveryone, w =>
            {
                w.Write(m.SteamId);
                w.Write(m.SteamName ?? "");
                w.Write((sbyte)m.ColorIndex);
                WriteColor(w, m.Body);
                WriteColor(w, m.Lights);
                WriteColor(w, m.Wings);
                for (int i = 0; i < 10; i++)
                {
                    w.Write(m.Painted[i]);
                    if (m.Painted[i])
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            Color c = m.Paint[i * 3 + k];
                            w.Write((byte)(Mathf.Clamp01(c.r) * 255f));
                            w.Write((byte)(Mathf.Clamp01(c.g) * 255f));
                            w.Write((byte)(Mathf.Clamp01(c.b) * 255f));
                        }
                    }
                }
                w.Write(m.Code ?? "");
            });
        }

        private static void SendStatus()
        {
            Info m = Mine;
            KillNet.Send(StatusMessage, KillNet.ToEveryone, w =>
            {
                w.Write(m.State);
                w.Write((short)Mathf.Clamp(m.Hp, -999, 9999));
                w.Write(m.Flags);
                w.Write(m.Scene ?? "");
                w.Write(m.Seconds);
                w.Write(m.Rank ?? "");
                w.Write((sbyte)m.Difficulty);
            });
        }

        // ---- the others ----

        public static void Handle(int from, byte type, BinaryReader r)
        {
            switch (type)
            {
                case ProfileMessage:
                {
                    Info i = Of(from);
                    ulong steam = r.ReadUInt64();
                    if (steam != i.SteamId)
                    {
                        i.Avatar = null;
                        i.AvatarAsked = false;
                    }
                    i.SteamId = steam;
                    string name = r.ReadString();
                    i.SteamName = name.Length > 40 ? name.Substring(0, 40) : name;
                    i.ColorIndex = r.ReadSByte();
                    i.Body = ReadColor(r);
                    i.Lights = ReadColor(r);
                    i.Wings = ReadColor(r);
                    int version = 17;
                    for (int n = 0; n < 10; n++)
                    {
                        i.Painted[n] = r.ReadBoolean();
                        version = version * 31 + (i.Painted[n] ? 1 : 0);
                        if (i.Painted[n])
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte();
                                i.Paint[n * 3 + k] = new Color32(cr, cg, cb, 255);
                                version = version * 31 + cr * 65536 + cg * 256 + cb;
                            }
                        }
                    }
                    i.PaintVersion = version;
                    if (r.BaseStream.Length - r.BaseStream.Position > 0)
                    {
                        string code = r.ReadString();
                        i.Code = code.Length > 16 ? "" : code;
                    }
                    i.ProfileAt = Time.unscaledTime;
                    break;
                }
                case StatusMessage:
                {
                    Info i = Of(from);
                    i.State = r.ReadByte();
                    i.Hp = r.ReadInt16();
                    i.Flags = r.ReadByte();
                    i.Scene = r.ReadString();
                    i.Seconds = r.ReadSingle();
                    i.Rank = r.ReadString();
                    i.Difficulty = r.ReadSByte();
                    i.StatusAt = Time.unscaledTime;
                    break;
                }
                case PingMessage:
                {
                    float t = r.ReadSingle();
                    KillNet.Send(PongMessage, KillNet.ToPlayer, w => w.Write(t), from);
                    break;
                }
                case PongMessage:
                {
                    float t = r.ReadSingle();
                    float ms = (Time.unscaledTime - t) * 1000f;
                    if (ms >= 0f && ms < 10000f)
                    {
                        Info i = Of(from);
                        i.Ping = i.Ping < 0f ? ms : Mathf.Lerp(i.Ping, ms, 0.5f);
                    }
                    break;
                }
            }
        }

        // ---- Steam pictures ----

        private static readonly List<(Info Who, Task<Steamworks.Data.Image?> Task)> asking = new List<(Info, Task<Steamworks.Data.Image?>)>();

        private static void Avatars()
        {
            if (!ServerList.SteamUp)
            {
                return;
            }
            Ask(Mine);
            foreach (Info i in others.Values)
            {
                Ask(i);
            }
            for (int n = asking.Count - 1; n >= 0; n--)
            {
                var (who, task) = asking[n];
                if (!task.IsCompleted)
                {
                    continue;
                }
                asking.RemoveAt(n);
                if (task.Status == TaskStatus.RanToCompletion && task.Result.HasValue)
                {
                    Steamworks.Data.Image img = task.Result.Value;
                    if (img.Width > 0 && img.Height > 0 && img.Data != null && img.Data.Length >= img.Width * img.Height * 4)
                    {
                        who.Avatar = Ui.Texture((int)img.Width, (int)img.Height, img.Data);
                    }
                }
            }
        }

        private static void Ask(Info i)
        {
            if (i.AvatarAsked || i.SteamId == 0)
            {
                return;
            }
            i.AvatarAsked = true;
            try
            {
                asking.Add((i, SteamFriends.GetMediumAvatarAsync(new SteamId { Value = i.SteamId })));
            }
            catch
            {
            }
        }

        // A player's Steam profile, in Steam's overlay.
        public static void OpenSteamProfile(int id)
        {
            Info i = Of(id);
            if (i.SteamId != 0 && ServerList.SteamUp)
            {
                try
                {
                    SteamFriends.OpenUserOverlay(new SteamId { Value = i.SteamId }, "steamid");
                }
                catch
                {
                }
            }
        }

        // ---- a checkpoint reached ----

        [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.ActivateCheckPoint))]
        private static class CheckpointHook
        {
            private static void Postfix() => checkpointAt = Time.unscaledTime;
        }

        // The code of the party this player's in (theirs, or the world owner's).
        public static string PartyCode()
        {
            if (!string.IsNullOrEmpty(ServerList.Code))
            {
                return ServerList.Code;
            }
            foreach (Party.Member m in Party.Members)
            {
                if (m.WorldHost && m.Id != Room.MyId && others.TryGetValue(m.Id, out Info i) && !string.IsNullOrEmpty(i.Code))
                {
                    return i.Code;
                }
            }
            return "";
        }

        public static void Forget()
        {
            others.Clear();
        }
    }
}
