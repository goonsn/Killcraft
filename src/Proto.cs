namespace Killcraft
{
    // Byte layout of SkyCraft's shared memory (protocol/skycraft_protocol.h, version 11). The
    // Minecraft half is SkyCraft's own Fabric mod, so every offset here must match that header.
    internal static class Proto
    {
        public const uint Magic = 0x43594B53;
        public const uint Version = 11;
        public const string MappingName = "Local\\SkyCraft_v1";

        public const long OffHeader = 0x0;
        public const long OffSkyState = 0x100;
        public const long OffMcState = 0x200;
        public const long OffOverlayCtl = 0x300;
        public const long OffOverlaySlotHdr = 0x340;
        public const long OffWaterGrid = 0x400;
        public const long OffInputRing = 0x1000;
        public const long OffActorTable = 0x12000;
        public const long OffEventRing = 0x17000;
        public const long OffWorldEntities = 0x1C000;
        public const long OffCollisionRing = 0x20000;
        public const long CollisionRingBytes = 32L << 20;
        public const long OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
        public const int MaxOverlayW = 3840;
        public const int MaxOverlayH = 2160;
        public const long OverlaySlotBytes = (long)MaxOverlayW * MaxOverlayH * 4;
        public const int OverlaySlots = 3;
        public const long OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
        public const long RenderRingBytes = 64L << 20;
        public const long MappingBytes = OffRenderRing + RenderRingBytes;

        // Header
        public const long HMagic = 0x00, HVersion = 0x04, HHostPid = 0x08, HMcPid = 0x0C, HHostBeat = 0x10, HMcBeat = 0x18;

        // SkyState (host -> MC), seqlock
        public const long SsSeq = 0, SsFlags = 4, SsWorldId = 8, SsEpoch = 12, SsPosX = 16, SsPosY = 24, SsPosZ = 32,
            SsYaw = 40, SsPitch = 44, SsTeleportSeq = 48, SsViewportW = 52, SsViewportH = 56, SsGameHour = 60, SsSize = 0x40;
        public const uint SkyInGame = 1, SkyMenuOpen = 2, SkyLoading = 4;

        // McState (MC -> host), seqlock
        public const long MsSeq = 0, MsFlags = 4, MsX = 8, MsY = 16, MsZ = 24, MsYaw = 32, MsPitch = 36, MsEyeHeight = 40,
            MsSensitivity = 44, MsTeleportAck = 48, MsGuiScale = 52, MsFrameCounter = 56, MsFov = 64, MsBobPhase = 68,
            MsBobAmount = 72, MsEyeX = 80, MsEyeY = 88, MsEyeZ = 96, MsTickQpc = 104, MsPrevX = 112, MsPrevY = 120,
            MsPrevZ = 128, MsCurX = 136, MsCurY = 144, MsCurZ = 152, MsTickEyeO = 160, MsTickEye = 164, MsWalkO = 168,
            MsWalk = 172, MsBobO = 176, MsBob = 180, MsTickMs = 184, MsCameraMode = 192, MsCameraDistance = 196, MsSize = 0xC8;
        public const uint McInWorld = 1, McScreenOpen = 2, McOnGround = 4, McSneaking = 8, McSprinting = 16, McDead = 32,
            McSwimming = 64, McFlying = 128;

        // Overlay triple buffer
        public const uint OverlayDirty = 1u << 2;
        public const long OcState = 0, OcFramesPublished = 8;
        public const long SlotHdrSize = 0x40, ShWidth = 0, ShHeight = 4, ShFlags = 8, ShFrameId = 16;

        // Input ring (host -> MC)
        public const int InputRingEntries = 4096;
        public const long RingHead = 0x00, RingTail = 0x40, RingData = 0x80;
        public const ushort InKey = 1, InMouseButton = 2, InScroll = 3, InCursor = 4, InText = 5, InReleaseAll = 6,
            InHurt = 7, InOpenMenu = 8;
        public const ushort HurtMelee = 0, HurtProjectile = 1, HurtMagic = 2, HurtOther = 3;

        // Actor table (host -> MC), seqlock
        public const int MaxActors = 256;
        public const long AtSeq = 0, AtCount = 4, AtRecords = 0x40, ActorRecordBytes = 64;
        public const uint ActorHostile = 1, ActorDead = 2, ActorEssential = 4, ActorInCombat = 8;

        // Event ring (MC -> host)
        public const int EventRingEntries = 512;
        public const long EventBytes = 32;
        public const uint EvHitActor = 1, EvPlayerDied = 2, EvExplosion = 3, EvArrowStuck = 4, EvSkillUse = 5;
        public const uint HitCritical = 1, HitProjectile = 2, HitSweep = 4, HitFire = 8;

        // Collision ring (host -> MC)
        public const long ColRingDataBytes = CollisionRingBytes - RingData;
        public const uint ColPad = 0, ColClear = 1, ColRegion = 2, ColTris = 3;
        public const uint TriStairHelper = 1, TriDiggable = 2, TriGhost = 4, TriTerrain = 8;
        public const int ColRegionBytes = 32, ColBlockBytes = 80, ColTriBytes = 40;

        // Render ring (MC -> host)
        public const long RenRingDataBytes = RenderRingBytes - RingData;
        public const uint RenPad = 0, RenAtlas = 1, RenSection = 2, RenClearAll = 3, RenTexture = 4, RenAvatar = 5,
            RenScene = 6, RenAtlasRegion = 7, RenLights = 8, RenRagdoll = 9, RenSolids = 10, RenDug = 11;

        public const int WaterGridBytes = 16 + 16 * 16 * 4;
    }
}
