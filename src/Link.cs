using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Killcraft
{
    internal struct McState
    {
        public uint Flags;
        public double X, Y, Z;
        public float Yaw, Pitch, EyeHeight, Sensitivity;
        public uint TeleportAck, GuiScale;
        public ulong FrameCounter;
        public float Fov;
        public double EyeX, EyeY, EyeZ;
        public long TickQpc;
        public double PrevX, PrevY, PrevZ, CurX, CurY, CurZ;
        public float TickEyeO, TickEye, TickMs;
        public uint CameraMode;
    }

    internal struct SkyState
    {
        public uint Flags, WorldId, Epoch, TeleportSeq, ViewportW, ViewportH;
        public double X, Y, Z;
        public float Yaw, Pitch, GameHour;
    }

    internal struct McEvent
    {
        public uint Type, FormId, Flags, Weapon;
        public float A, B, C, D;
    }

    // The host end of SkyCraft's shared memory. ULTRAKILL plays the part Skyrim's SKSE plugin plays:
    // it creates the mapping, and SkyCraft's Minecraft mod opens it.
    internal static unsafe class Link
    {
        private static IntPtr mapping;
        private static byte* b;
        private static int overlayFront = 2;
        private static readonly object eventLock = new object();

        public static bool Ready => b != null;

        public static bool Create()
        {
            if (b != null)
            {
                return true;
            }
            ulong size = (ulong)Proto.MappingBytes;
            mapping = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, Native.PageReadWrite, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), Proto.MappingName);
            int error = Marshal.GetLastWin32Error();
            if (mapping == IntPtr.Zero)
            {
                Plugin.Log.LogError($"CreateFileMapping failed ({error})");
                return false;
            }
            IntPtr view = Native.MapViewOfFile(mapping, Native.FileMapAllAccess, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                Plugin.Log.LogError($"MapViewOfFile failed ({Marshal.GetLastWin32Error()})");
                Native.CloseHandle(mapping);
                mapping = IntPtr.Zero;
                return false;
            }
            b = (byte*)view;

            // A mapping left over from an earlier run (Minecraft still holds it): reset everything the
            // host owns so the rings and the overlay swap start from a known state.
            Zero(Proto.OffSkyState, Proto.SsSize);
            Zero(Proto.OffOverlayCtl, 0x100);
            Zero(Proto.OffWaterGrid, Proto.WaterGridBytes);
            Zero(Proto.OffInputRing, Proto.RingData);
            Zero(Proto.OffCollisionRing, Proto.RingData);
            Zero(Proto.OffActorTable, Proto.AtRecords + Proto.ActorRecordBytes * Proto.MaxActors);
            Zero(Proto.OffEventRing, Proto.RingData);
            Zero(Proto.OffWorldEntities, 0x40 + 96 * 160);
            Zero(Proto.OffRenderRing, Proto.RingData);
            U32(Proto.OffHeader + Proto.HVersion) = Proto.Version;
            U32(Proto.OffHeader + Proto.HHostPid) = Native.GetCurrentProcessId();
            Volatile.Write(ref I64(Proto.OffHeader + Proto.HHostBeat), (long)Native.GetTickCount64());
            Volatile.Write(ref I32(Proto.OffHeader + Proto.HMagic), unchecked((int)Proto.Magic));
            Plugin.Log.LogInfo($"shared memory {Proto.MappingName} ({size >> 20} MB, {(error == Native.ErrorAlreadyExists ? "reused" : "created")})");
            return true;
        }

        private static void Zero(long off, long len)
        {
            byte* p = b + off;
            for (long i = 0; i < len; i++)
            {
                p[i] = 0;
            }
        }

        private static ref int I32(long off) => ref *(int*)(b + off);
        private static ref uint U32(long off) => ref *(uint*)(b + off);
        private static ref long I64(long off) => ref *(long*)(b + off);
        private static ref float F32(long off) => ref *(float*)(b + off);
        private static ref double F64(long off) => ref *(double*)(b + off);

        public static void Heartbeat()
        {
            if (b != null)
            {
                Volatile.Write(ref I64(Proto.OffHeader + Proto.HHostBeat), (long)Native.GetTickCount64());
            }
        }

        public static bool McAlive()
        {
            if (b == null)
            {
                return false;
            }
            long last = Volatile.Read(ref I64(Proto.OffHeader + Proto.HMcBeat));
            return last != 0 && (long)Native.GetTickCount64() - last < 3000;
        }

        public static uint McPid() => b == null ? 0 : (uint)Volatile.Read(ref I32(Proto.OffHeader + Proto.HMcPid));

        public static void WriteSkyState(in SkyState s)
        {
            if (b == null)
            {
                return;
            }
            long o = Proto.OffSkyState;
            int seq = I32(o + Proto.SsSeq);
            Volatile.Write(ref I32(o + Proto.SsSeq), seq + 1);
            Thread.MemoryBarrier();
            U32(o + Proto.SsFlags) = s.Flags;
            U32(o + Proto.SsWorldId) = s.WorldId;
            U32(o + Proto.SsEpoch) = s.Epoch;
            F64(o + Proto.SsPosX) = s.X;
            F64(o + Proto.SsPosY) = s.Y;
            F64(o + Proto.SsPosZ) = s.Z;
            F32(o + Proto.SsYaw) = s.Yaw;
            F32(o + Proto.SsPitch) = s.Pitch;
            U32(o + Proto.SsTeleportSeq) = s.TeleportSeq;
            U32(o + Proto.SsViewportW) = s.ViewportW;
            U32(o + Proto.SsViewportH) = s.ViewportH;
            F32(o + Proto.SsGameHour) = s.GameHour;
            Volatile.Write(ref I32(o + Proto.SsSeq), seq + 2);
        }

        public static bool ReadMcState(ref McState m)
        {
            if (b == null)
            {
                return false;
            }
            long o = Proto.OffMcState;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int s1 = Volatile.Read(ref I32(o + Proto.MsSeq));
                if ((s1 & 1) != 0)
                {
                    Thread.SpinWait(8);
                    continue;
                }
                m.Flags = U32(o + Proto.MsFlags);
                m.X = F64(o + Proto.MsX);
                m.Y = F64(o + Proto.MsY);
                m.Z = F64(o + Proto.MsZ);
                m.Yaw = F32(o + Proto.MsYaw);
                m.Pitch = F32(o + Proto.MsPitch);
                m.EyeHeight = F32(o + Proto.MsEyeHeight);
                m.Sensitivity = F32(o + Proto.MsSensitivity);
                m.TeleportAck = U32(o + Proto.MsTeleportAck);
                m.GuiScale = U32(o + Proto.MsGuiScale);
                m.FrameCounter = (ulong)I64(o + Proto.MsFrameCounter);
                m.Fov = F32(o + Proto.MsFov);
                m.EyeX = F64(o + Proto.MsEyeX);
                m.EyeY = F64(o + Proto.MsEyeY);
                m.EyeZ = F64(o + Proto.MsEyeZ);
                m.TickQpc = I64(o + Proto.MsTickQpc);
                m.PrevX = F64(o + Proto.MsPrevX);
                m.PrevY = F64(o + Proto.MsPrevY);
                m.PrevZ = F64(o + Proto.MsPrevZ);
                m.CurX = F64(o + Proto.MsCurX);
                m.CurY = F64(o + Proto.MsCurY);
                m.CurZ = F64(o + Proto.MsCurZ);
                m.TickEyeO = F32(o + Proto.MsTickEyeO);
                m.TickEye = F32(o + Proto.MsTickEye);
                m.TickMs = F32(o + Proto.MsTickMs);
                m.CameraMode = U32(o + Proto.MsCameraMode);
                Thread.MemoryBarrier();
                if (Volatile.Read(ref I32(o + Proto.MsSeq)) == s1)
                {
                    return true;
                }
            }
            return false;
        }

        public static void PushInput(ushort type, ushort code = 0, int a = 0, int bArg = 0, int c = 0)
        {
            if (b == null)
            {
                return;
            }
            long ring = Proto.OffInputRing;
            long head = I64(ring + Proto.RingHead);
            long tail = Volatile.Read(ref I64(ring + Proto.RingTail));
            if (head - tail >= Proto.InputRingEntries)
            {
                return;
            }
            byte* e = b + ring + Proto.RingData + (head & (Proto.InputRingEntries - 1)) * 16;
            *(ushort*)e = type;
            *(ushort*)(e + 2) = code;
            *(int*)(e + 4) = a;
            *(int*)(e + 8) = bArg;
            *(int*)(e + 12) = c;
            Volatile.Write(ref I64(ring + Proto.RingHead), head + 1);
        }

        // Single producer: only the collision worker thread calls this.
        public static bool WriteCollision(uint type, byte[] payload, int bytes)
        {
            if (b == null)
            {
                return false;
            }
            long ring = Proto.OffCollisionRing;
            byte* data = b + ring + Proto.RingData;
            long size = Proto.ColRingDataBytes;
            long msgBytes = (8 + bytes + 7) & ~7L;
            if (msgBytes > size / 2)
            {
                Plugin.Log.LogError($"collision message too large ({msgBytes} bytes)");
                return false;
            }
            long head = I64(ring + Proto.RingHead);
            long tail = Volatile.Read(ref I64(ring + Proto.RingTail));
            long pos = head % size;
            long pad = pos + msgBytes > size ? size - pos : 0;
            if (size - (head - tail) < msgBytes + pad)
            {
                return false;
            }
            if (pad > 0)
            {
                *(uint*)(data + pos) = Proto.ColPad;
                *(uint*)(data + pos + 4) = 0;
                head += pad;
                pos = 0;
            }
            *(uint*)(data + pos) = type;
            *(uint*)(data + pos + 4) = (uint)bytes;
            if (bytes > 0)
            {
                Marshal.Copy(payload, 0, (IntPtr)(data + pos + 8), bytes);
            }
            Volatile.Write(ref I64(ring + Proto.RingHead), head + msgBytes);
            return true;
        }

        public static long CollisionHead() => b == null ? 0 : Volatile.Read(ref I64(Proto.OffCollisionRing + Proto.RingHead));

        public static long CollisionTail() => b == null ? 0 : Volatile.Read(ref I64(Proto.OffCollisionRing + Proto.RingTail));

        public static void WriteActors(ActorRecord[] records, int count)
        {
            if (b == null)
            {
                return;
            }
            long o = Proto.OffActorTable;
            int seq = I32(o + Proto.AtSeq);
            Volatile.Write(ref I32(o + Proto.AtSeq), seq + 1);
            Thread.MemoryBarrier();
            count = Math.Min(count, Proto.MaxActors);
            I32(o + Proto.AtCount) = count;
            for (int i = 0; i < count; i++)
            {
                byte* r = b + o + Proto.AtRecords + i * Proto.ActorRecordBytes;
                ref ActorRecord a = ref records[i];
                *(uint*)r = a.FormId;
                *(uint*)(r + 4) = a.Flags;
                *(float*)(r + 8) = a.X;
                *(float*)(r + 12) = a.Y;
                *(float*)(r + 16) = a.Z;
                *(float*)(r + 20) = a.Yaw;
                *(float*)(r + 24) = a.Width;
                *(float*)(r + 28) = a.Height;
                *(float*)(r + 32) = a.HealthFrac;
                *(ushort*)(r + 36) = a.Level;
                *(ushort*)(r + 38) = 0;
                byte[] name = a.Name;
                int n = name == null ? 0 : Math.Min(name.Length, 23);
                for (int k = 0; k < 24; k++)
                {
                    r[40 + k] = k < n ? name[k] : (byte)0;
                }
            }
            Volatile.Write(ref I32(o + Proto.AtSeq), seq + 2);
        }

        public static bool PopEvent(out McEvent e)
        {
            e = default;
            if (b == null)
            {
                return false;
            }
            lock (eventLock)
            {
                long ring = Proto.OffEventRing;
                long head = Volatile.Read(ref I64(ring + Proto.RingHead));
                long tail = I64(ring + Proto.RingTail);
                if (tail >= head)
                {
                    return false;
                }
                if (head - tail > Proto.EventRingEntries)
                {
                    tail = head - Proto.EventRingEntries;
                }
                byte* p = b + ring + Proto.RingData + (tail & (Proto.EventRingEntries - 1)) * Proto.EventBytes;
                e.Type = *(uint*)p;
                e.FormId = *(uint*)(p + 4);
                e.A = *(float*)(p + 8);
                e.B = *(float*)(p + 12);
                e.C = *(float*)(p + 16);
                e.D = *(float*)(p + 20);
                e.Flags = *(uint*)(p + 24);
                e.Weapon = *(uint*)(p + 28);
                Volatile.Write(ref I64(ring + Proto.RingTail), tail + 1);
                return true;
            }
        }

        public struct WorldEntity
        {
            public uint Kind, Id, Tint;
            public float X, Y, Z, Yaw, Pitch, Scale;
            public float[] Ext;  // 3
            public float[] Uv;   // 3 rects {u0, v0, u1, v1}
        }

        private static readonly WorldEntity[] worldEntities = CreateEntities();

        private static WorldEntity[] CreateEntities()
        {
            var a = new WorldEntity[160];
            for (int i = 0; i < a.Length; i++)
            {
                a[i].Ext = new float[3];
                a[i].Uv = new float[12];
            }
            return a;
        }

        // Seqlock read of the things Minecraft wants drawn this frame (arrows, items, cracks, outline).
        public static bool ReadWorldEntities(out WorldEntity[] entities, out int count, out bool hasSelection, out UnityEngine.Vector3 selMin, out UnityEngine.Vector3 selMax)
        {
            entities = worldEntities;
            count = 0;
            hasSelection = false;
            selMin = selMax = default;
            if (b == null)
            {
                return false;
            }
            long o = Proto.OffWorldEntities;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int s1 = Volatile.Read(ref I32(o));
                if ((s1 & 1) != 0)
                {
                    Thread.SpinWait(8);
                    continue;
                }
                count = Math.Min(I32(o + 4), worldEntities.Length);
                hasSelection = I32(o + 8) != 0;
                selMin = new UnityEngine.Vector3(F32(o + 12), F32(o + 16), F32(o + 20));
                selMax = new UnityEngine.Vector3(F32(o + 24), F32(o + 28), F32(o + 32));
                for (int i = 0; i < count; i++)
                {
                    long r = o + 0x40 + i * 96;
                    ref WorldEntity e = ref worldEntities[i];
                    e.Kind = U32(r);
                    e.Id = U32(r + 4);
                    e.X = F32(r + 8);
                    e.Y = F32(r + 12);
                    e.Z = F32(r + 16);
                    e.Yaw = F32(r + 20);
                    e.Pitch = F32(r + 24);
                    e.Scale = F32(r + 28);
                    for (int k = 0; k < 3; k++)
                    {
                        e.Ext[k] = F32(r + 32 + k * 4);
                    }
                    for (int k = 0; k < 12; k++)
                    {
                        e.Uv[k] = F32(r + 44 + k * 4);
                    }
                    e.Tint = U32(r + 92);
                }
                Thread.MemoryBarrier();
                if (Volatile.Read(ref I32(o)) == s1)
                {
                    return true;
                }
            }
            count = 0;
            return false;
        }

        public delegate void RenderSink(uint type, byte* payload, int bytes);

        // Minecraft blocks on a full render ring, so it has to be drained every frame whether or not
        // anything is drawn from it.
        public static void DrainRender(RenderSink sink, long maxBytes)
        {
            if (b == null)
            {
                return;
            }
            long ring = Proto.OffRenderRing;
            long head = Volatile.Read(ref I64(ring + Proto.RingHead));
            long tail = I64(ring + Proto.RingTail);
            byte* data = b + ring + Proto.RingData;
            long size = Proto.RenRingDataBytes;
            long done = 0;
            while (tail < head && done < maxBytes)
            {
                long pos = tail % size;
                uint type = *(uint*)(data + pos);
                int payload = *(int*)(data + pos + 4);
                if (type == Proto.RenPad)
                {
                    tail += size - pos;
                    continue;
                }
                sink?.Invoke(type, data + pos + 8, payload);
                long msgBytes = (8 + payload + 7) & ~7L;
                tail += msgBytes;
                done += msgBytes;
            }
            Volatile.Write(ref I64(ring + Proto.RingTail), tail);
        }

        public static long RenderBacklog() => b == null ? 0 : Volatile.Read(ref I64(Proto.OffRenderRing + Proto.RingHead)) - I64(Proto.OffRenderRing + Proto.RingTail);

        public static bool AcquireOverlayFrame()
        {
            if (b == null)
            {
                return false;
            }
            ref int state = ref I32(Proto.OffOverlayCtl + Proto.OcState);
            if ((Volatile.Read(ref state) & Proto.OverlayDirty) == 0)
            {
                return false;
            }
            int old = Interlocked.Exchange(ref state, overlayFront);
            overlayFront = old & 3;
            return true;
        }

        public static void ResetOverlay()
        {
            if (b == null)
            {
                return;
            }
            Volatile.Write(ref I32(Proto.OffOverlayCtl + Proto.OcState), 0);
            overlayFront = 2;
        }

        public static IntPtr FrontPixels => (IntPtr)(b + Proto.OffOverlayPixels + Proto.OverlaySlotBytes * overlayFront);

        public static void FrontHeader(out int width, out int height, out bool bottomUp)
        {
            byte* h = b + Proto.OffOverlaySlotHdr + Proto.SlotHdrSize * overlayFront;
            width = *(int*)(h + Proto.ShWidth);
            height = *(int*)(h + Proto.ShHeight);
            bottomUp = (*(uint*)(h + Proto.ShFlags) & 1) != 0;
        }
    }

    internal struct ActorRecord
    {
        public uint FormId, Flags;
        public float X, Y, Z, Yaw, Width, Height, HealthFrac;
        public ushort Level;
        public byte[] Name;
    }

    internal static class Native
    {
        public const uint PageReadWrite = 0x04;
        public const uint FileMapAllAccess = 0xF001F;
        public const int ErrorAlreadyExists = 183;
        public const uint Synchronize = 0x00100000;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint maxHigh, uint maxLow, string name);

        [DllImport("kernel32", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32")]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32")]
        public static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32")]
        public static extern bool QueryPerformanceFrequency(out long value);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
    }
}
