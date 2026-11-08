using UnityEngine;

namespace Killcraft
{
    // What the mod calls itself, by how ULTRAKILL was started: with Minecraft it's KILLCRAFT (the torn
    // block), without it KILLNET (the globe) - in the party screen, the settings, the chat.
    internal static class Brand
    {
        public static bool Craft => Plugin.WithMinecraft;
        public static string Name => Craft ? "KILLCRAFT" : "KILLNET";
        public static string Title => Craft ? "Killcraft" : "KillNet";
        public static Sprite Logo => Craft ? Ui.Logo : Ui.NetLogo;
    }
}
