using UnityEngine;

namespace Killcraft
{
    // Unity is left-handed (Y up, Z forward); Minecraft is right-handed (Y up, Z south). Flipping Z
    // keeps Minecraft's blocks and text from coming out mirrored.
    //
    // Minecraft keeps one world for every ULTRAKILL level, so each level is placed in its own area of
    // it (an X offset from the level's name): builds stay with their level and don't float into others.
    // Minecraft's Nether is shown too, high above the level (see NetherX).
    internal static class Coords
    {
        public static float U => Plugin.UnitsPerBlock.Value;

        // Feet of V1's capsule (radius 0.5, height 3.5, centre 0.25 above the transform).
        public const float FeetBelowRoot = 1.5f;

        // Kept within +-60000 blocks: the protocol sends entity and enemy positions as 32-bit floats,
        // which still resolve 1/128 of a block out there.
        public static double OffsetX { get; private set; }
        public static double OffsetZ { get; private set; }

        public static void SetLevel(uint levelHash)
        {
            OffsetX = ((int)(levelHash % 59) - 29) * 2048.0;
            OffsetZ = ((int)(levelHash / 59 % 59) - 29) * 2048.0;
        }

        // The Nether: Killcraft's data pack sends a player from (x, z) in a level to (NetherX + x/8, 70, z/8)
        // (see McSave), so every level has its own part of the Nether, and Minecraft's player being past
        // NetherX - 10000 means it's there (levels are within +-62000 of 0). ULTRAKILL shows it high
        // above the level (NetherLift), out of the level's sight.
        public const double NetherX = 100000;
        public const float NetherLift = 6000f;
        // The height the player lands at in the Nether (always, so Host knows where that is).
        public const int NetherLandingY = 70;

        public static bool InNether { get; private set; }

        public static bool IsNetherX(double x) => x > NetherX - 10000;

        public static void SetNether(bool nether) => InNether = nether;

        // Where V1 is in ULTRAKILL: in the Nether or in the level.
        public static bool IsNetherUnity(Vector3 u) => u.y > NetherLift * 0.5f;

        private static double AreaX => InNether ? NetherX + OffsetX / 8 : OffsetX;
        private static double AreaZ => InNether ? OffsetZ / 8 : OffsetZ;
        private static float Lift => InNether ? NetherLift : 0f;

        public static void ToMc(Vector3 u, out double x, out double y, out double z)
        {
            double k = 1.0 / U;
            x = u.x * k + AreaX;
            y = (u.y - Lift) * k;
            z = -u.z * k + AreaZ;
        }

        public static Vector3 ToUnity(double x, double y, double z)
        {
            float u = U;
            return new Vector3((float)((x - AreaX) * u), (float)(y * u) + Lift, (float)(-(z - AreaZ) * u));
        }

        public static Vector3 DirToUnity(float x, float y, float z) => new Vector3(x, y, -z);

        // Unity yaw 0 faces +Z, which is Minecraft's north (yaw 180).
        public static float YawToMc(float unityYaw) => Mathf.DeltaAngle(0f, unityYaw + 180f);

        public static float YawToUnity(float mcYaw) => mcYaw - 180f;

        // CameraController.rotationX is positive looking up; Minecraft pitch is positive looking down.
        public static float PitchToMc(float rotationX) => Mathf.Clamp(-Mathf.DeltaAngle(0f, rotationX), -90f, 90f);
    }
}
