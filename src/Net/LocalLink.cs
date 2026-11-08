using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Killcraft
{
    // Two (or more) ULTRAKILLs on this PC trying multiplayer alone (second-player.bat): one Steam
    // account can't reach itself through Steam, so the party's messages go over localhost instead.
    // Player 1 (HostForSecondPlayer) is the party's owner and listens; the others connect. Each player
    // is known by their slot (1, 2, ...) where a Steam party uses Steam ids. Frames: 4-byte length,
    // then the bytes; the first a client sends is its slot.
    internal static class LocalLink
    {
        private const int Port = 25599;

        public static bool Wanted => Plugin.PlayerSlot > 1 || (Plugin.HostForSecondPlayer != null && Plugin.HostForSecondPlayer.Value);
        public static bool IAmOwner => Plugin.PlayerSlot == 1;
        public static ulong Me => (ulong)Plugin.PlayerSlot;

        private static readonly ConcurrentQueue<(ulong From, byte[] Data)> inbox = new ConcurrentQueue<(ulong, byte[])>();
        private static readonly ConcurrentDictionary<ulong, NetworkStream> peers = new ConcurrentDictionary<ulong, NetworkStream>();
        private static readonly object writeLock = new object();
        private static bool started;

        // In the party: the owner always (alone or not); the others once connected to them.
        public static bool Up => started && (IAmOwner || peers.ContainsKey(1));

        public static IEnumerable<ulong> Members
        {
            get
            {
                yield return Me;
                foreach (ulong k in peers.Keys)
                {
                    yield return k;
                }
            }
        }

        public static void Start()
        {
            if (started || !Wanted)
            {
                return;
            }
            started = true;
            new Thread(IAmOwner ? (ThreadStart)Listen : Connect) { IsBackground = true, Name = "Killcraft local party" }.Start();
            Plugin.Log.LogInfo($"KillNet: trying multiplayer on this PC (player {Plugin.PlayerSlot}, {(IAmOwner ? "running the party" : "joining player 1")}, port {Port})");
        }

        private static void Listen()
        {
            TcpListener l;
            try
            {
                l = new TcpListener(IPAddress.Loopback, Port);
                l.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: can't listen for the second player on port {Port} ({e.Message})");
                return;
            }
            while (true)
            {
                try
                {
                    TcpClient c = l.AcceptTcpClient();
                    c.NoDelay = true;
                    new Thread(() => Serve(c, 0)) { IsBackground = true }.Start();
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        }

        private static void Connect()
        {
            while (true)
            {
                try
                {
                    var c = new TcpClient();
                    c.Connect(IPAddress.Loopback, Port);
                    c.NoDelay = true;
                    NetworkStream s = c.GetStream();
                    Write(s, BitConverter.GetBytes(Plugin.PlayerSlot));
                    Serve(c, 1);
                }
                catch
                {
                }
                Thread.Sleep(2000);
            }
        }

        // Reads a peer's frames until it goes (`who` known for the owner's end: 1; else its first frame).
        private static void Serve(TcpClient c, ulong who)
        {
            NetworkStream s = c.GetStream();
            try
            {
                if (who == 0)
                {
                    byte[] hello = Read(s);
                    who = (ulong)BitConverter.ToInt32(hello, 0);
                }
                peers[who] = s;
                Plugin.Log.LogInfo($"KillNet: player {who} on this PC connected");
                while (true)
                {
                    inbox.Enqueue((who, Read(s)));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"KillNet: player {who} on this PC disconnected ({e.Message})");
            }
            finally
            {
                peers.TryRemove(who, out _);
                try
                {
                    c.Close();
                }
                catch
                {
                }
            }
        }

        private static byte[] Read(Stream s)
        {
            byte[] head = ReadFully(s, 4);
            int n = BitConverter.ToInt32(head, 0);
            if (n < 0 || n > 1 << 20)
            {
                throw new IOException("bad frame");
            }
            return ReadFully(s, n);
        }

        private static byte[] ReadFully(Stream s, int n)
        {
            var b = new byte[n];
            int at = 0;
            while (at < n)
            {
                int got = s.Read(b, at, n - at);
                if (got <= 0)
                {
                    throw new IOException("closed");
                }
                at += got;
            }
            return b;
        }

        private static void Write(NetworkStream s, byte[] data)
        {
            lock (writeLock)
            {
                s.Write(BitConverter.GetBytes(data.Length), 0, 4);
                s.Write(data, 0, data.Length);
            }
        }

        public static void Send(ulong to, byte[] data)
        {
            if (peers.TryGetValue(to, out NetworkStream s))
            {
                try
                {
                    Write(s, data);
                }
                catch
                {
                }
            }
        }

        public static bool TryReceive(out ulong from, out byte[] data)
        {
            if (inbox.TryDequeue(out var m))
            {
                from = m.From;
                data = m.Data;
                return true;
            }
            from = 0;
            data = null;
            return false;
        }
    }
}
