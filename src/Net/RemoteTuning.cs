namespace Killcraft
{
    // The numbers the other players' V1s move by (RemotePlayers, V1Body, V1Motion): how far behind
    // they're shown, how their motion is smoothed, how their animations are timed. Seconds unless said.
    internal static class RemoteTuning
    {
        // How far behind what they said their V1 is shown: a base plus so much of how unevenly their
        // packets come (Jitter), kept between the two bounds. (Sent 20 a second: the least is just
        // over a packet and a half.)
        public const float DelayBase = 0.06f, DelayPerJitter = 2.5f, DelayMin = 0.08f, DelayMax = 0.3f;
        // Past their newest packet (lost, or late): carried on by their speed this long at most, then held.
        public const float ExtrapolateMax = 0.12f;
        // The drawn body eased towards where the timeline puts it (per second: 40 is a 25 ms lag), its
        // facing and look towards the timeline's (30), and the speed its legs go by (25).
        public const float PositionEase = 40f, YawEase = 30f, VelocityEase = 25f;
        // The animator's own damping of its run blend (seconds to settle); none while dashing.
        public const float VelocityDamp = 0.04f;
        // On the ground: the feet put on the floor found under them, if it's within this.
        public const float GroundSnapMax = 1f;
        // A jump or landing seen in their state without its own event (lost): made up, unless an event
        // came within this; and an event within this of one made up isn't played again.
        public const float EventFallbackWindow = 0.25f;
        // How much of their timeline's events are kept waiting at most (a clock gone wrong: dropped).
        public const float EventMaxWait = 2f;
        // The run clip's full stride is V1 at this speed (PlayerAnimations: velocity / 15).
        public const float RunClipSpeed = 15f;
        // The legs' cycle, faster than the clip above that speed and slower as the body's drawn bigger,
        // kept between these (the animator's speed: shots and punches go by it too while running).
        public const float LegSpeedMin = 0.6f, LegSpeedMax = 1.8f, LegSpeedEase = 10f;
        // Slamming down: the chest tucked forward this much (degrees); hooked: the free arm raised to the
        // whiplash over this long.
        public const float SlamLean = 28f, HookArmEase = 12f;
    }
}
