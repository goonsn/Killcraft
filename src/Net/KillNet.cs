using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Killcraft
{
    // Killcraft's multiplayer messages between the players' ULTRAKILLs, carried by Steam (SteamNet: the
    // party's Steam lobby, peer to peer through its owner), with Minecraft or without it. This
    // ULTRAKILL's own Minecraft is still talked to over localhost (Killcraft's mod connects to the port
    // given to it, -Dkillcraft.netPort): what's for it (route ToMinecraft), and what it says itself
    // (from -2); what its world's server passes on from other players isn't used any more (Steam
    // carries that). A message is its type, its route and what it says, all little-endian; one from
    // another player arrives after their id.
    internal static class KillNet
    {
        // Routes (the mod's KillNet).
        public const byte ToRoom = 0, ToAuthority = 1, ToPlayer = 2, ToServer = 3, ToEveryone = 4, ToMinecraft = 255;
        // Types.
        public const byte Presence = 1, PlayerState = 2, Room = 100, HidePlayers = 200;

        private const int MaxFrame = 30000;
        private static TcpListener listener;
        private static volatile NetworkStream stream;
        private static readonly object writeLock = new object();
        private static readonly ConcurrentQueue<byte[]> inbox = new ConcurrentQueue<byte[]>();
        private static bool started;

        // In a party (its Steam lobby).
        public static bool Connected => SteamNet.InLobby;
        // This ULTRAKILL's own Minecraft connected.
        public static bool McConnected => stream != null;

        public static void Start()
        {
            if (started)
            {
                return;
            }
            started = true;
            int port = Plugin.NetPort;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: can't listen on port {port} ({e.Message}); no multiplayer between ULTRAKILLs");
                return;
            }
            new Thread(Run) { IsBackground = true, Name = "Killcraft KillNet" }.Start();
        }

        private static void Run()
        {
            while (true)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    client.NoDelay = true;
                    NetworkStream s = client.GetStream();
                    stream = s;
                    Plugin.Log.LogInfo("KillNet: Minecraft connected");
                    var header = new byte[2];
                    while (true)
                    {
                        ReadFully(s, header, 2);
                        int length = header[0] << 8 | header[1];
                        var data = new byte[length];
                        ReadFully(s, data, length);
                        inbox.Enqueue(data);
                    }
                }
                catch (Exception e)
                {
                    stream = null;
                    Plugin.Log.LogInfo($"KillNet: Minecraft disconnected ({e.Message})");
                    try
                    {
                        client?.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ReadFully(Stream s, byte[] buffer, int count)
        {
            int at = 0;
            while (at < count)
            {
                int n = s.Read(buffer, at, count - at);
                if (n <= 0)
                {
                    throw new IOException("closed");
                }
                at += n;
            }
        }

        // A message: type, route (ToPlayer: then the player's id), then what write puts.
        public static void Send(byte type, byte route, Action<BinaryWriter> write, int toPlayer = 0)
        {
            NetworkStream s = stream;
            // (A replay playing: its level isn't this player's to say anything of.)
            if (Replay.Playing && Replay.LevelType(type))
            {
                return;
            }
            bool record = Replay.Recording && Replay.Keeps(type) && route != ToPlayer && route != ToServer && route != ToMinecraft;
            // (Alone in the level: the level's goings-on are only kept for the replay, not sent.)
            bool solo = !Killcraft.Room.Shared && Replay.LevelType(type) && route != ToServer && route != ToMinecraft;
            bool can = route == ToMinecraft ? s != null : SteamNet.InLobby;
            if ((!can || solo) && !record)
            {
                return;
            }
            byte[] data;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(type);
                w.Write(route);
                if (route == ToPlayer)
                {
                    w.Write(toPlayer);
                }
                write?.Invoke(w);
                w.Flush();
                data = ms.ToArray();
            }
            if (record)
            {
                Replay.Sent(type, data, 2);
            }
            if (!can || solo)
            {
                return;
            }
            if (data.Length > MaxFrame)
            {
                Plugin.Log.LogWarning($"KillNet: message {type} too big ({data.Length} bytes); not sent");
                return;
            }
            if (route != ToMinecraft)
            {
                SteamNet.Send(data);
                return;
            }
            try
            {
                lock (writeLock)
                {
                    var header = new[] { (byte)(data.Length >> 8), (byte)data.Length };
                    s.Write(header, 0, 2);
                    s.Write(data, 0, data.Length);
                }
            }
            catch (Exception)
            {
                stream = null;
            }
        }

        // Main thread, each frame: what has arrived, to handle (from: the sender's id, -1 the server).
        // A message for this player from the party (the sender's id in front).
        public static void Deliver(byte[] withFrom)
        {
            steamInbox.Enqueue(withFrom);
        }

        private static readonly Queue<byte[]> steamInbox = new Queue<byte[]>();

        public static void Receive(Action<int, byte, BinaryReader> handle)
        {
            try
            {
                SteamNet.Frame();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: Steam failed ({e.Message})");
            }
            while (inbox.TryDequeue(out byte[] data))
            {
                // (From this PC's Minecraft: only what it says itself, -2. Its world's server's passing
                // on of other players' messages is Steam's job now.)
                if (data.Length >= 6 && BitConverter.ToInt32(data, 0) == -2)
                {
                    steamInbox.Enqueue(data);
                }
            }
            while (steamInbox.Count > 0)
            {
                byte[] data = steamInbox.Dequeue();
                if (data.Length < 6)
                {
                    continue;
                }
                try
                {
                    using (var r = new BinaryReader(new MemoryStream(data)))
                    {
                        int from = r.ReadInt32();
                        byte type = r.ReadByte();
                        byte route = r.ReadByte();
                        if (route == ToPlayer)
                        {
                            r.ReadInt32();
                        }
                        if (Replay.Recording)
                        {
                            Replay.Heard(from, type, data, (int)r.BaseStream.Position);
                        }
                        handle(from, type, r);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"KillNet: bad message ({e.Message})");
                }
            }
        }
    }
}
