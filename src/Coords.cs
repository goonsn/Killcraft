using UnityEngine;

namespace Killcraft
{
    // Unity is left-handed (Y up, Z forward); Minecraft is right-handed (Y up, Z south). Flipping Z
    // keeps Minecraft's blocks and text from coming out mirrored.
    //
    // Minecraft keeps one world for every ULTRAKILL level, so each level is placed in its own area of
    // it (an X offset from the level's name): builds stay with their level and don't float into others.
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

        public static void ToMc(Vector3 u, out double x, out double y, out double z)
        {
            double k = 1.0 / U;
            x = u.x * k + OffsetX;
            y = u.y * k;
            z = -u.z * k + OffsetZ;
        }

        public static Vector3 ToUnity(double x, double y, double z)
        {
            float u = U;
            return new Vector3((float)((x - OffsetX) * u), (float)(y * u), (float)(-(z - OffsetZ) * u));
        }

        public static Vector3 DirToUnity(float x, float y, float z) => new Vector3(x, y, -z);

        // Unity yaw 0 faces +Z, which is Minecraft's north (yaw 180).
        public static float YawToMc(float unityYaw) => Mathf.DeltaAngle(0f, unityYaw + 180f);

        public static float YawToUnity(float mcYaw) => mcYaw - 180f;

        // CameraController.rotationX is positive looking up; Minecraft pitch is positive looking down.
        public static float PitchToMc(float rotationX) => Mathf.Clamp(-Mathf.DeltaAngle(0f, rotationX), -90f, 90f);
    }
}
