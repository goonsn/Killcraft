#if KILLNET_ONLY
using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // KillNet's own build (dotnet build -c Release -p:KillNetOnly=true): ULTRAKILL multiplayer over Steam
    // and nothing else. Killcraft's Minecraft side isn't in it (no Minecraft started, no shared memory, no
    // local port, no Discord pipe, no files outside BepInEx's): the rest of the mod still asks after it,
    // and these stand-ins always say there's none, so it runs as KillNet always has without Minecraft.
    // None of them does anything.

    internal static class Proto
    {
        public const uint SkyInGame = 1, SkyMenuOpen = 2, SkyLoading = 4;
        public const uint McInWorld = 1, McScreenOpen = 2, McDead = 32;
        public const ushort InReleaseAll = 6;
        public const int MaxOverlayW = 3840, MaxOverlayH = 2160;
        public const ushort HurtMelee = 0, HurtProjectile = 1, HurtMagic = 2, HurtOther = 3;
        public const ushort InHurt = 7;
    }

    // (Timing from .NET's clock, keys from Unity's input: nothing of Windows' own called.)
    internal static class Native
    {
        public static bool QueryPerformanceFrequency(out long value)
        {
            value = System.Diagnostics.Stopwatch.Frequency;
            return true;
        }
        public static bool QueryPerformanceCounter(out long value)
        {
            value = System.Diagnostics.Stopwatch.GetTimestamp();
            return true;
        }
        public static short GetAsyncKeyState(int virtualKey) => 0;
    }

    internal static class Link
    {
        public static bool Ready => true;
        public static bool Create() => true;
        public static void Heartbeat() { }
        public static bool McAlive() => false;
        public static uint McPid() => 0;
        public static void WriteSkyState(in SkyState s) { }
        public static bool ReadMcState(ref McState m) => false;
        public static void PushInput(ushort type, ushort code = 0, int a = 0, int bArg = 0, int c = 0) { }
        public static bool PopEvent(out McEvent e)
        {
            e = default;
            return false;
        }
        public static void ResetOverlay() { }
    }

    internal static class Launcher
    {
        public enum Status { Off, Starting, SignIn, Running, NoLauncher, Failed }
        public static volatile Status State = Status.Off;
        public static void StartMinecraft() { }
    }

    internal static class ModeChoice
    {
        public static bool Pending => false;
        public static bool Decide() => false;
        public static void Hook() { }
        public static void Frame(bool mainMenu, bool loading) { }
    }

    internal static class Collision
    {
        public static int SentRegions => 0;
        public static bool ClearConsumed => true;
        public static void Reset(int newEpoch) { }
        public static void ForgetMeshes() { }
        public static void Update(double px, double py, double pz) { }
    }

    internal static class Combat
    {
        public static float LastSignal = -100f, LastStop = -100f;
        public static bool DimensionNether;
        public static float DimensionAt = -100f;
        public static void Reset() { }
        public static void Frame(bool active, bool linked, bool following, NewMovement nm) { }
        public static void Handle(in McEvent e, NewMovement nm) { }
        public static uint AttackerFor(EnemyIdentifier eid, Vector3 from, Vector3 dir) => 0;
        public static bool IsBoss(uint id) => false;
        public static void HurtBy(uint attacker) { }
    }

    internal static class Destruction
    {
        public static void Damage(uint entityId, float amount, Vector3 direction) { }
        public static void LeaveNether() { }
        public static void Heal(float amount) { }
        public static void Explosion(Explosion e) { }
        public static void Beam(RevolverBeam beam) { }
        public static void ChargedShot(Revolver revolver) { }
        public static void Saw(Vector3 at) { }
    }

    internal static class HeldItems
    {
        public static void LevelChanged() { }
        public static void Frame(bool minecraftHasPlayer, CameraController cc) { }
        public static void Pose(CameraController cc) { }
        public static void BeforePickUp(Punch punch, Transform target) { }
    }

    internal static class HudHider
    {
        public static void Frame(bool hide, NewMovement nm) { }
    }

    internal static class InputForward
    {
        public static bool UltrakillMoves, UsingItem;
        public static Vector2 Cursor => Vector2.zero;
        public static void Frame(bool route, bool screenOpen, int width, int height) { }
    }

    internal static class Lockout
    {
        public static void Frame(bool minecraftHasPlayer, bool ultrakillMoves) { }
    }

    internal static class McAudio
    {
        public static void SetMuted(uint pid, bool muted) { }
    }

    internal static class McCommand
    {
        public static bool Busy => false;
        public static void Clear() { }
        public static void Frame(bool ready, bool screenOpen) { }
    }

    internal static class MobHits
    {
        public static bool Active => false;
        public static void Frame(bool on, Vector3 player) { }
        public static void Moving(Object shot, Vector3 now, float radiusBlocks, float ukDamage, bool keepsCutting) { }
        public static void Punch(Punch punch) { }
        public static void Beam(RevolverBeam beam) { }
        public static void Chainsaw(Chainsaw saw) { }
        public static void Explosion(Explosion e) { }
    }

    internal static class Nether
    {
        public static void Frame(bool inNether, CameraController cc) { }
        public static void Landed(Vector3 feet) { }
        public static void LevelChanged() { }
    }

    internal static class Overlay
    {
        public static void Frame(bool show) { }
    }

    internal static class SmileOs
    {
        public static void LevelChanged() { }
        public static void Frame(bool active) { }
    }

    internal static class WorldRender
    {
        public static bool IsOurs(Collider c) => false;
        internal static void UseWater(Shader shader) { }
        public static void DebugCycle() { }
        internal static void BrightnessChanged() { }
        public static void LevelLoaded() { }
        public static void Drain() { }
        public static void PlaceAvatar(bool show, Vector3 feet) { }
        public static void Relight() { }
        public static void Reposition() { }
        public static void Frame(bool show) { }
        internal static bool HideForShadows() => false;
        internal static void ShowAfterShadows() { }
    }

    internal static class LocalLink
    {
        public static bool Wanted => false;
        public static bool IAmOwner => false;
        public static ulong Me => 0;
        public static bool Up => false;
        public static IEnumerable<ulong> Members
        {
            get
            {
                yield break;
            }
        }
        public static void Start() { }
        public static void Send(ulong to, byte[] data) { }
        public static bool TryReceive(out ulong from, out byte[] data)
        {
            from = 0;
            data = null;
            return false;
        }
    }

    internal static class DiscordRpc
    {
        public static void Frame(bool inGame) { }
    }

    // (Two ULTRAKILLs on one PC, second-player.bat: a test of the full build's, not in this one.)
    internal static class OwnFolders
    {
        public static void Install(HarmonyLib.Harmony harmony) { }
    }

    // The one real piece: an enemy's prefab from ULTRAKILL's own list of them (the sandbox spawner's),
    // for Minos in any level (MinosMode) and V2 skins (RemotePlayers).
    internal static class NetherWorld
    {
        private static SpawnableObjectsDatabase database;
        private static bool warned;

        public static void Frame(bool active, Vector3 v1) { }

        public static GameObject PrefabFor(EnemyType type)
        {
            if (database == null)
            {
                foreach (SpawnableObjectsDatabase db in Resources.FindObjectsOfTypeAll<SpawnableObjectsDatabase>())
                {
                    if (db != null && db.enemies != null && db.enemies.Length > 0)
                    {
                        database = db;
                        break;
                    }
                }
                if (database == null)
                {
                    var field = HarmonyLib.AccessTools.Field(typeof(SpawnMenu), "objects");
                    foreach (SpawnMenu menu in Resources.FindObjectsOfTypeAll<SpawnMenu>())
                    {
                        if (field?.GetValue(menu) is SpawnableObjectsDatabase db && db.enemies != null && db.enemies.Length > 0)
                        {
                            database = db;
                            break;
                        }
                    }
                }
                if (database == null && !warned)
                {
                    warned = true;  // (tried again next time: it loads with a level)
                    Plugin.Log.LogWarning("KillNet: ULTRAKILL's list of enemies isn't loaded");
                }
            }
            if (database == null)
            {
                return null;
            }
            foreach (SpawnableObject o in database.enemies)
            {
                if (o != null && o.gameObject != null && o.enemyType == type)
                {
                    return o.gameObject;
                }
            }
            return null;
        }
    }
}
#endif
