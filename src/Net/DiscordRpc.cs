using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Killcraft
{
    // Discord Rich Presence from ULTRAKILL itself (with Minecraft or without it): the player's Discord
    // status shows Killcraft, the level, and the party; while in a party it has a Join button, and a
    // friend with Killcraft (or KillNet) running who clicks it joins the same party (its Steam lobby id
    // is the join secret). Talks to the Discord app on this PC over its local pipe
    // (\\.\pipe\discord-ipc-N) from one background thread, as Killcraft's Minecraft mod used to
    // (DiscordPresence.java, now off: McSave). Without Discord running it quietly retries.
    internal static class DiscordRpc
    {
        private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
        private const string SecretPrefix = "kcparty:";
        private static readonly long Start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static string appId;
        private static bool started;
        private static volatile string wanted;
        private static float describedAt = -100f;
        private static readonly ConcurrentQueue<ulong> joins = new ConcurrentQueue<ulong>();

        // The Discord application whose status this is: Killcraft's, another one's, or none.
        private static string AppId()
        {
            string v = (Plugin.DiscordAppId?.Value ?? "").Trim();
            if (v.Length == 0)
            {
                return Plugin.KillcraftDiscordApp;
            }
            // ("skycraft": SkyCraft's own status, from its Minecraft; "0": none.)
            return v == "0" || v.Equals("skycraft", StringComparison.OrdinalIgnoreCase) ? "" : v;
        }

        // Each frame (main thread): what the status should say; joins clicked on Discord.
        public static void Frame(bool inGame)
        {
            if (!started)
            {
                started = true;
                appId = AppId();
                if (appId.Length == 0)
                {
                    return;
                }
                new Thread(Run) { IsBackground = true, Name = "Killcraft Discord" }.Start();
                Plugin.Log.LogInfo($"Discord: Rich Presence on (application {appId})");
            }
            if (appId.Length == 0)
            {
                return;
            }
            while (joins.TryDequeue(out ulong lobby))
            {
                Plugin.Log.LogInfo($"Discord: joining a party from Discord (Steam lobby {lobby})");
                ServerList.JoinParty(lobby, "your Discord friend");
            }
            if (Time.unscaledTime - describedAt < 1f)
            {
                return;
            }
            describedAt = Time.unscaledTime;
            wanted = Describe(inGame);
        }

        private static string Describe(bool inGame)
        {
            string scene = SceneHelper.CurrentScene ?? "";
            string where = inGame && Party.IsLevel(scene) ? Party.LevelName(scene) : "In the menu";
            bool mc = Plugin.WithMinecraft && ServerList.MinecraftAllowed;
            var a = new StringBuilder();
            a.Append("{\"details\":").Append(Json(mc ? "Playing ULTRAKILL with Minecraft" : "Playing ULTRAKILL")).Append(',');
            a.Append("\"state\":").Append(Json(ServerList.InParty ? (ServerList.PartyOwner ? "Hosting a party" : "In a party") + " · " + where : where)).Append(',');
            if (ServerList.InParty)
            {
                ulong id = ServerList.Party.Value.Id.Value;
                int size = 1;
                try
                {
                    size = Mathf.Max(1, ServerList.Party.Value.MemberCount);
                }
                catch
                {
                }
                a.Append("\"party\":{\"id\":").Append(Json("kc-" + id.ToString("x"))).Append(",\"size\":[").Append(size).Append(",10]},");
                if (size < 10)
                {
                    a.Append("\"secrets\":{\"join\":").Append(Json(SecretPrefix + id)).Append("},");
                }
            }
            a.Append("\"timestamps\":{\"start\":").Append(Start).Append("},");
            a.Append("\"assets\":{\"large_image\":\"killcraft\",\"large_text\":\"" + Brand.Title + "\"}}");
            return "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + ",\"activity\":" + a + "}";
        }

        private static string Json(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    default:
                        if (c < 0x20)
                        {
                            b.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            b.Append(c);
                        }
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        // ---- the pipe (background thread) ----

        private static IntPtr pipe = IntPtr.Zero;
        private static string sent;
        private static DateTime sentAt = DateTime.MinValue;
        private static bool toldNoDiscord;
        private static int nonce;

        private static void Run()
        {
            while (true)
            {
                try
                {
                    if (pipe == IntPtr.Zero && !Connect())
                    {
                        Thread.Sleep(15000);
                        continue;
                    }
                    string w = wanted;
                    // (Discord takes 5 updates in 20 s: well under.)
                    if (w != null && w != sent && (DateTime.UtcNow - sentAt).TotalSeconds > 5)
                    {
                        Write(OpFrame, "{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"" + (++nonce) + "\",\"args\":" + w + "}");
                        sent = w;
                        sentAt = DateTime.UtcNow;
                    }
                    ReadAvailable();
                    Thread.Sleep(250);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogInfo($"Discord: connection closed ({e.Message}); trying again later");
                    Close();
                    Thread.Sleep(15000);
                }
            }
        }

        private static bool Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                IntPtr h = CreateFileW(@"\\.\pipe\discord-ipc-" + i, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h == InvalidHandle)
                {
                    continue;
                }
                pipe = h;
                Write(OpHandshake, "{\"v\":1,\"client_id\":\"" + appId + "\"}");
                DateTime until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until)
                {
                    string msg = ReadFrame();
                    if (msg != null && msg.Contains("\"READY\""))
                    {
                        // (Friends clicking Join.)
                        Write(OpFrame, "{\"cmd\":\"SUBSCRIBE\",\"nonce\":\"" + (++nonce) + "\",\"evt\":\"ACTIVITY_JOIN\"}");
                        Plugin.Log.LogInfo("Discord: connected (Rich Presence)");
                        toldNoDiscord = false;
                        sent = null;
                        return true;
                    }
                    if (msg == null)
                    {
                        Thread.Sleep(100);
                    }
                }
                Close();
                return false;
            }
            if (!toldNoDiscord)
            {
                toldNoDiscord = true;
                Plugin.Log.LogInfo("Discord: not running; Rich Presence connects when it is");
            }
            return false;
        }

        private static readonly Regex JoinSecret = new Regex("\"secret\"\\s*:\\s*\"" + SecretPrefix + "(\\d+)\"");

        private static void ReadAvailable()
        {
            string msg;
            while ((msg = ReadFrame()) != null)
            {
                if (msg.Contains("\"ACTIVITY_JOIN\"") && msg.Contains("\"DISPATCH\""))
                {
                    Match m = JoinSecret.Match(msg);
                    if (m.Success && ulong.TryParse(m.Groups[1].Value, out ulong lobby))
                    {
                        joins.Enqueue(lobby);
                    }
                }
                else if (msg.Contains("\"ERROR\""))
                {
                    Plugin.Log.LogWarning($"Discord says {msg}");
                }
            }
        }

        private static void Write(int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            if (!WriteFile(pipe, frame, (uint)frame.Length, out uint _, IntPtr.Zero))
            {
                throw new Exception("couldn't write to Discord");
            }
        }

        // The next frame if one has fully arrived (never waits for one that hasn't started).
        private static string ReadFrame()
        {
            while (Available() >= 8)
            {
                byte[] head = ReadExactly(8);
                int op = BitConverter.ToInt32(head, 0), length = BitConverter.ToInt32(head, 4);
                if (length < 0 || length > 1 << 20)
                {
                    throw new Exception("bad frame from Discord");
                }
                byte[] body = ReadExactly(length);
                string text = Encoding.UTF8.GetString(body);
                switch (op)
                {
                    case OpPing:
                        var pong = new byte[8 + length];
                        BitConverter.GetBytes(OpPong).CopyTo(pong, 0);
                        BitConverter.GetBytes(length).CopyTo(pong, 4);
                        body.CopyTo(pong, 8);
                        WriteFile(pipe, pong, (uint)pong.Length, out uint _, IntPtr.Zero);
                        break;
                    case OpClose:
                        throw new Exception("Discord closed the connection: " + text);
                    case OpFrame:
                        return text;
                }
            }
            return null;
        }

        private static uint Available()
        {
            if (!PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out uint avail, IntPtr.Zero))
            {
                throw new Exception("Discord's pipe closed");
            }
            return avail;
        }

        private static byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            int at = 0;
            while (at < n)
            {
                var part = new byte[n - at];
                if (!ReadFile(pipe, part, (uint)part.Length, out uint got, IntPtr.Zero) || got == 0)
                {
                    throw new Exception("Discord's pipe closed");
                }
                Buffer.BlockCopy(part, 0, b, at, (int)got);
                at += (int)got;
            }
            return b;
        }

        private static void Close()
        {
            if (pipe != IntPtr.Zero)
            {
                CloseHandle(pipe);
            }
            pipe = IntPtr.Zero;
            sent = null;
        }

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool WriteFile(IntPtr h, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool PeekNamedPipe(IntPtr h, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);

        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr h);
    }
}
