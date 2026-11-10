namespace Killcraft
{
    // What ULTRAKILL and Killcraft's Minecraft tell each other each frame (Link), as plain records. In
    // KillNet's own build (KILLNET_ONLY) there's no Minecraft: they stay empty.
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
        public float CameraDistance;
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
}
