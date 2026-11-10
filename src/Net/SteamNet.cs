using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Killcraft
{
    // KillNet over Steam: the party is a Steam lobby (ServerList keeps it), and its messages go between
    // the players' ULTRAKILLs peer to peer, through the lobby's owner. The owner's ULTRAKILL does what
    // Killcraft's Minecraft server did (the mod's KillNet.java): who's in which room (the same level),
    // who runs each room's enemies, who's in the party; and passes each message on to whom it's for.
    // Everyone else sends theirs to the owner. Works the same with Minecraft or without it, so players
    // of both can share a party.
    //
    // A message is as KillNet writes it (type, route, then what it says); passed on, it gets the
    // sender's id in front (4 bytes), -1 for the owner's own (type 100 rooms, 101 party, 103 kicked).
    internal static class SteamNet
    {
        // (Steam P2P channel: Killcraft's own, out of the way of ULTRAKILL's and other mods'.)
        private const int Channel = 41;
        private const int MaxFrame = 30000;
        public const byte KickedMessage = 103;

        private sealed class Member
        {
            public SteamId Steam;
            public int Id;
            public long Room = -1;
            public string Scene = "";
            public long Since;
            public float HeardAt;
            public long UuidMsb, UuidLsb;
        }

        private static readonly Dictionary<ulong, Member> members = new Dictionary<ulong, Member>();
        private static readonly HashSet<ulong> kicked = new HashSet<ulong>();
        private static readonly HashSet<ulong> inLobby = new HashSet<ulong>();
        private static float roomsAt = -100f, lobbyReadAt = -100f;
        private static bool roomsChanged, hooked;
        private static ulong routedFor;
        private static long sinceCounter;

        // (When the party's owner was last heard from, by a player who isn't it: Rejoin watches it.)
        public static float OwnerHeardAt { get; private set; } = -100f;
        public static event Action<ulong> ConnectionFailed;

        // (Trying multiplayer on one PC, second-player.bat: localhost instead of Steam, LocalLink.)
        private static bool Local => LocalLink.Wanted;

        public static bool InLobby => Local ? LocalLink.Up : ServerList.InParty;
        public static bool IAmOwner => Local ? LocalLink.IAmOwner : ServerList.PartyOwner;

        private static ulong Me
        {
            get
            {
                if (Local)
                {
                    return LocalLink.Me;
                }
                try
                {
                    return SteamClient.SteamId.Value;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private static ulong OwnerId
        {
            get
            {
                if (Local)
                {
                    return 1;
                }
                try
                {
                    return ServerList.Party != null ? ServerList.Party.Value.Owner.Id.Value : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private static string NameOf(ulong id)
        {
            if (Local)
            {
                return id == 1 && ServerList.SteamUp ? SteamClient.Name : "Player " + id;
            }
            try
            {
                return new Friend(new SteamId { Value = id }).Name ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void Transmit(ulong to, byte[] data, P2PSend how)
        {
            if (Local)
            {
                LocalLink.Send(to, data);
                return;
            }
            SteamNetworking.SendP2PPacket(new SteamId { Value = to }, data, data.Length, Channel, how);
        }

        private static bool Next(out ulong from, out byte[] data)
        {
            if (Local)
            {
                return LocalLink.TryReceive(out from, out data);
            }
            from = 0;
            data = null;
            if (!ServerList.SteamUp || !SteamNetworking.IsP2PPacketAvailable(out uint _, Channel))
            {
                return false;
            }
            P2Packet? p = SteamNetworking.ReadP2PPacket(Channel);
            if (p == null)
            {
                return false;
            }
            from = p.Value.SteamId.Value;
            data = p.Value.Data;
            return true;
        }

        // A player's id in KillNet: small (replays move theirs up by ReplayPlayer.Fake), the same whoever
        // owns the lobby (so nothing changes when it's handed on), from their Steam id.
        public static int IdOf(ulong steam)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < 8; i++)
            {
                h ^= (steam >> (8 * i)) & 0xFF;
                h *= 1099511628211UL;
            }
            return (int)(h % 900000UL) + 1;
        }

        public static int MyId => IdOf(Me);

        // A player's UUID, as KillNet's rooms say each player is by: their Minecraft one, if they play
        // with it (Minecraft hides the others' bodies by it); else made of their Steam id.
        private static void WriteUuid(BinaryWriter w, Member m)
        {
            if (m.UuidMsb != 0 || m.UuidLsb != 0)
            {
                w.Write(m.UuidMsb);
                w.Write(m.UuidLsb);
                return;
            }
            w.Write((long)m.Steam.Value);
            w.Write(0x214554454E4C4C49L);  // ("KILLNET!")
        }

        private static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            if (Local)
            {
                LocalLink.Start();
                return;
            }
            SteamNetworking.OnP2PSessionRequest += id =>
            {
                // (Only from someone in this party.)
                if (InLobby && IsInLobby(id.Value))
                {
                    SteamNetworking.AcceptP2PSessionWithUser(id);
                }
                else
                {
                    Plugin.Log.LogInfo($"KillNet: ignored a Steam connection from {id.Value} (not in the party)");
                }
            };
            SteamNetworking.OnP2PConnectionFailed += (id, error) =>
            {
                Plugin.Log.LogWarning($"KillNet: Steam couldn't reach {new Friend(id).Name} ({error})");
                ConnectionFailed?.Invoke(id.Value);
            };
            SteamMatchmaking.OnLobbyMemberJoined += (l, f) => roomsChanged = true;
            SteamMatchmaking.OnLobbyMemberLeave += (l, f) => Gone(f.Id.Value);
            SteamMatchmaking.OnLobbyMemberDisconnected += (l, f) => Gone(f.Id.Value);
        }

        private static void Gone(ulong steam)
        {
            members.Remove(steam);
            roomsChanged = true;
            try
            {
                SteamNetworking.CloseP2PSessionWithUser(new SteamId { Value = steam });
            }
            catch
            {
            }
        }

        private static bool IsInLobby(ulong steam)
        {
            ReadLobby();
            return inLobby.Contains(steam);
        }

        private static void ReadLobby(bool now = false)
        {
            if (!now && Time.unscaledTime - lobbyReadAt < 1f)
            {
                return;
            }
            lobbyReadAt = Time.unscaledTime;
            inLobby.Clear();
            if (Local)
            {
                foreach (ulong m in LocalLink.Members)
                {
                    inLobby.Add(m);
                }
                return;
            }
            Lobby? l = ServerList.Party;
            if (l == null)
            {
                return;
            }
            try
            {
                foreach (Friend f in l.Value.Members)
                {
                    inLobby.Add(f.Id.Value);
                }
            }
            catch
            {
            }
        }

        // A party joined or left: nothing kept of the last.
        public static void PartyChanged()
        {
            members.Clear();
            kicked.Clear();
            inLobby.Clear();
            lobbyReadAt = -100f;
            roomsChanged = true;
            routedFor = 0;
            OwnerHeardAt = Time.unscaledTime;
        }

        // ---- sending ----

        private static P2PSend HowFor(byte type, int length)
        {
            // (What's said again many times a second, newest wins: not held up by a lost one.)
            bool often = type == KillNet.PlayerState || type == V1Body.Moves || type == EnemySync.Snapshot || type == Voice.VoiceMessage || type == MinosMode.StateMessage;
            return often && length <= 1100 ? P2PSend.UnreliableNoDelay : P2PSend.Reliable;
        }

        // A message from this player (as KillNet wrote it).
        public static void Send(byte[] data)
        {
            if (!InLobby || data.Length < 2)
            {
                return;
            }
            Hook();
            if (IAmOwner)
            {
                Route(Me, data);
                return;
            }
            ulong owner = OwnerId;
            if (owner == 0 || owner == Me)
            {
                return;
            }
            Transmit(owner, data, HowFor(data[0], data.Length));
        }

        private static void SendTo(Member to, int fromId, byte[] d)
        {
            var o = new byte[4 + d.Length];
            o[0] = (byte)fromId;
            o[1] = (byte)(fromId >> 8);
            o[2] = (byte)(fromId >> 16);
            o[3] = (byte)(fromId >> 24);
            Buffer.BlockCopy(d, 0, o, 4, d.Length);
            if (to.Steam.Value == Me)
            {
                Arrived(o);
                return;
            }
            Transmit(to.Steam.Value, o, HowFor(d[0], o.Length));
        }

        // ---- each frame ----

        public static void Frame()
        {
            if (!ServerList.SteamUp && !Local)
            {
                return;
            }
            Hook();
            int n = 0;
            while (n++ < 2000 && Next(out ulong from, out byte[] data))
            {
                if (!InLobby)
                {
                    continue;
                }
                if (IAmOwner)
                {
                    // (To the owner: a player's own message, to pass on.)
                    if (data.Length >= 2 && data.Length <= MaxFrame && !kicked.Contains(from) && IsInLobby(from))
                    {
                        Route(from, data);
                    }
                }
                else if (OwnerId == from && data.Length >= 6)
                {
                    // (From the owner: passed on to this player.)
                    OwnerHeardAt = Time.unscaledTime;
                    Arrived(data);
                }
            }
            if (!InLobby)
            {
                return;
            }
            if (IAmOwner)
            {
                ulong partyId = Local ? 1UL : ServerList.Party.Value.Id.Value;
                if (routedFor != partyId)
                {
                    // (This player's the owner now, of a party made or handed to them.)
                    routedFor = partyId;
                    Plugin.Log.LogInfo("KillNet: this player runs the party (the Steam lobby's owner)");
                    roomsChanged = true;
                }
                ReadLobby();
                foreach (ulong s in new List<ulong>(members.Keys))
                {
                    if (!inLobby.Contains(s))
                    {
                        members.Remove(s);
                        roomsChanged = true;
                    }
                }
                if (roomsChanged || Time.unscaledTime - roomsAt > 2f)
                {
                    roomsChanged = false;
                    roomsAt = Time.unscaledTime;
                    SendRooms();
                }
            }
            else
            {
                routedFor = 0;
            }
        }

        private static void Arrived(byte[] withFrom)
        {
            // (Kicked out by the party's host: out of the lobby.)
            if (withFrom.Length >= 6 && withFrom[4] == KickedMessage && BitConverter.ToInt32(withFrom, 0) == -1)
            {
                Plugin.Log.LogInfo("KillNet: kicked out of the party");
                PartyChat.System("You were kicked out of the party.");
                if (!Local)
                {
                    ServerList.LeaveParty();
                }
                return;
            }
            KillNet.Deliver(withFrom);
        }

        // ---- the owner: passing messages on ----

        private static Member MemberOf(ulong steam)
        {
            if (!members.TryGetValue(steam, out Member m))
            {
                m = new Member { Steam = new SteamId { Value = steam }, Id = IdOf(steam) };
                members[steam] = m;
                roomsChanged = true;
            }
            m.HeardAt = Time.unscaledTime;
            return m;
        }

        private static void Route(ulong fromSteam, byte[] d)
        {
            Member m = MemberOf(fromSteam);
            int type = d[0], route = d[1];
            switch (route)
            {
                case KillNet.ToServer:
                    Server(m, type, d);
                    break;
                case KillNet.ToRoom:
                    if (m.Room < 0)
                    {
                        return;
                    }
                    foreach (Member o in members.Values)
                    {
                        if (o != m && o.Room == m.Room)
                        {
                            SendTo(o, m.Id, d);
                        }
                    }
                    break;
                case KillNet.ToEveryone:
                    foreach (Member o in members.Values)
                    {
                        if (o != m)
                        {
                            SendTo(o, m.Id, d);
                        }
                    }
                    break;
                case KillNet.ToAuthority:
                    Member a = Authority(m.Room);
                    if (a != null && a != m)
                    {
                        SendTo(a, m.Id, d);
                    }
                    break;
                case KillNet.ToPlayer:
                    if (d.Length >= 6)
                    {
                        int target = BitConverter.ToInt32(d, 2);
                        foreach (Member o in members.Values)
                        {
                            if (o.Id == target && o != m)
                            {
                                SendTo(o, m.Id, d);
                            }
                        }
                    }
                    break;
            }
        }

        private const byte Presence = 1, Kick = 4, Lead = 5, Go = 6;

        private static void Server(Member m, int type, byte[] d)
        {
            using (var r = new BinaryReader(new MemoryStream(d, 2, d.Length - 2)))
            {
                try
                {
                    if (type == Presence && d.Length >= 7)
                    {
                        uint level = r.ReadUInt32();
                        byte flags = r.ReadByte();
                        long room = (flags & 4) == 0 ? -1 : (flags & 2) != 0 ? level | (1L << 32) : level;
                        string scene = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "";
                        if (scene != m.Scene)
                        {
                            m.Scene = scene;
                            roomsChanged = true;
                        }
                        if (room != m.Room)
                        {
                            m.Room = room;
                            m.Since = ++sinceCounter;
                            roomsChanged = true;
                        }
                        if (r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == 1)
                        {
                            m.UuidMsb = r.ReadInt64();
                            m.UuidLsb = r.ReadInt64();
                        }
                    }
                    else if ((type == Kick || type == Lead) && d.Length >= 6 && m.Steam.Value == Me)
                    {
                        int target = r.ReadInt32();
                        Member t = null;
                        foreach (Member o in members.Values)
                        {
                            if (o.Id == target)
                            {
                                t = o;
                            }
                        }
                        if (t == null || t == m)
                        {
                            return;
                        }
                        if (type == Lead)
                        {
                            if (Local)
                            {
                                return;
                            }
                            Plugin.Log.LogInfo($"KillNet: handing the party to {NameOf(t.Steam.Value)}");
                            Lobby l = ServerList.Party.Value;
                            l.Owner = new Friend(t.Steam);
                            return;
                        }
                        Plugin.Log.LogInfo($"KillNet: kicking {NameOf(t.Steam.Value)} out of the party");
                        kicked.Add(t.Steam.Value);
                        SendTo(t, -1, new byte[] { KickedMessage, KillNet.ToRoom });
                        members.Remove(t.Steam.Value);
                        roomsChanged = true;
                    }
                    else if (type == Go && d.Length > 2 && m.Steam.Value == Me)
                    {
                        // (The party host took everyone to a level: told to the others as 102.)
                        byte[] going = (byte[])d.Clone();
                        going[0] = Party.GoingMessage;
                        going[1] = KillNet.ToRoom;
                        foreach (Member o in members.Values)
                        {
                            if (o != m)
                            {
                                SendTo(o, m.Id, going);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"KillNet: bad message {type} for the party's owner ({e.Message})");
                }
            }
        }

        // The room's authority: the party's host (the owner) when they're in it, else whoever came first.
        private static Member Authority(long room)
        {
            if (room < 0)
            {
                return null;
            }
            Member best = null;
            bool bestHost = false;
            ulong me = Me;
            foreach (Member m in members.Values)
            {
                if (m.Room != room)
                {
                    continue;
                }
                bool host = m.Steam.Value == me;
                if (best == null || (host && !bestHost) || (host == bestHost && (m.Since < best.Since || (m.Since == best.Since && m.Id < best.Id))))
                {
                    best = m;
                    bestHost = host;
                }
            }
            return best;
        }

        private static void SendRooms()
        {
            // (The owner's own entry, before they've said where they are.)
            MemberOf(Me);
            foreach (Member m in members.Values)
            {
                var inRoom = new List<Member>();
                if (m.Room >= 0)
                {
                    foreach (Member o in members.Values)
                    {
                        if (o.Room == m.Room)
                        {
                            inRoom.Add(o);
                        }
                    }
                }
                Member a = Authority(m.Room);
                byte[] d;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(KillNet.Room);
                    w.Write(KillNet.ToRoom);
                    w.Write(m.Id);
                    w.Write(a != null ? a.Id : 0);
                    w.Write(inRoom.Count);
                    foreach (Member o in inRoom)
                    {
                        w.Write(o.Id);
                        WriteUuid(w, o);
                    }
                    // (The host's PvP setting.)
                    w.Write((byte)(Plugin.PvP.Value ? 1 : 0));
                    w.Write(Mathf.Clamp(Plugin.PvpDamage.Value, 0.1f, 3f));
                    // (Whether the party's played with Minecraft: its host's.)
                    w.Write((byte)(Plugin.WithMinecraft ? 1 : 0));
                    // (Players bump into each other: the host's.)
                    w.Write((byte)(Plugin.PlayerCollision.Value ? 1 : 0));
                    w.Flush();
                    d = ms.ToArray();
                }
                SendTo(m, -1, d);
            }
            SendParty();
        }

        // Everyone in the party: id, UUID, name, level, flags (1 the world's host, 2 the party's host,
        // 4 in the Nether) - the owner is both.
        private static void SendParty()
        {
            var all = new List<Member>(members.Values);
            all.Sort((a, b) => a.Id.CompareTo(b.Id));
            ulong me = Me;
            byte[] d;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Party.PartyMessage);
                w.Write(KillNet.ToRoom);
                w.Write(all.Count);
                foreach (Member m in all)
                {
                    w.Write(m.Id);
                    WriteUuid(w, m);
                    string name = NameOf(m.Steam.Value);
                    w.Write(name.Length > 64 ? name.Substring(0, 64) : name);
                    w.Write(m.Room >= 0 ? m.Scene : "");
                    bool owner = m.Steam.Value == me;
                    w.Write((byte)((owner ? 3 : 0) | (m.Room >= 0 && (m.Room >> 32) != 0 ? 4 : 0)));
                }
                w.Flush();
                d = ms.ToArray();
            }
            foreach (Member m in all)
            {
                SendTo(m, -1, d);
            }
        }
    }
}
