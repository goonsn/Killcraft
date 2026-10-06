using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Killcraft
{
    // ULTRAKILL's explosions and charged beams break Minecraft's Nether (Killcraft's mod does it: see
    // mcmod's Destruction). Each is asked for as a dead actor (no stand-in) with an id from RequestIds,
    // published for a moment under a name of its own: an explosion's power and fire follow ULTRAKILL's
    // (its size, whether it sets things alight, how big a blast it is); a charged revolver shot or a
    // railcannon beam bores a tunnel along its path.
    internal static class Destruction
    {
        private const uint RequestIds = 0x3FFFFE00;
        private const int RequestCount = 256;
        private const float PublishSeconds = 0.5f;
        private const int KindExplosion = 1, KindTunnel = 2, KindDamage = 3, KindDig = 4, KindLeaveNether = 5, Fire = 0x100;

        // (A class: an explosion's pieces are merged into one still being gathered.)
        private sealed class Request
        {
            public ActorRecord Record;
            public float From, Until;
        }

        // One ULTRAKILL blast is several Explosion pieces (rings of it, at the same spot): gathered for a
        // moment into one Minecraft explosion, the biggest piece's power.
        private const float GatherSeconds = 0.1f;

        private static readonly List<Request> requests = new List<Request>();
        private static int seq;

        // Only up in the Nether (in the levels Minecraft's blocks are the player's own builds).
        private static bool InNether(Vector3 at) => Coords.InNether && Coords.IsNetherUnity(at);

        public static void Explosion(Explosion e)
        {
            if (e == null || fromMinecraft.Remove(e) || e.harmless || !InNether(e.transform.position))
            {
                return;
            }
            // ULTRAKILL's radius (units) to Minecraft's power: TNT (power 4) clears about 3-4 blocks.
            float radiusBlocks = e.maxSize / Coords.U;
            float power = Mathf.Clamp(radiusBlocks * 0.8f, 1f, 12f);
            if (e.enemy)
            {
                power *= 0.6f;
            }
            if (e.halved)
            {
                power *= 0.5f;
            }
            // Fire: what ULTRAKILL itself sets alight, and the big blasts (a rocket shot out of the air,
            // a super explosion).
            bool fire = e.ignite || power >= 6f;
            Coords.ToMc(e.transform.position, out double x, out double y, out double z);
            foreach (Request r in requests)
            {
                if (Time.unscaledTime < r.From && (r.Record.Level & 0xFF) == KindExplosion
                    && Mathf.Abs(r.Record.X - (float)x) < 3f && Mathf.Abs(r.Record.Y - (float)y) < 3f && Mathf.Abs(r.Record.Z - (float)z) < 3f)
                {
                    r.Record.Width = Mathf.Max(r.Record.Width, power);
                    r.Record.Level |= (ushort)(fire ? Fire : 0);
                    return;
                }
            }
            Add(new ActorRecord { X = (float)x, Y = (float)y, Z = (float)z, Width = power, Level = (ushort)(KindExplosion | (fire ? Fire : 0)) });
        }

        // Minecraft's own explosions shown as ULTRAKILL's (Combat): not sent back to Minecraft, where each
        // would make another one, over and over.
        private static readonly HashSet<Explosion> fromMinecraft = new HashSet<Explosion>();

        public static bool IsFromMinecraft(Explosion e) => e != null && fromMinecraft.Contains(e);

        public static void FromMinecraft(Explosion e)
        {
            fromMinecraft.RemoveWhere(x => x == null);
            fromMinecraft.Add(e);
        }

        public static void Beam(RevolverBeam beam)
        {
            if (beam == null || beam.fake || !InNether(beam.transform.position))
            {
                return;
            }
            // Which beams dig: the railcannon's, and the revolver's charged or strong ones (not plain
            // shots), the electric railcannon widest.
            float radius, depth;
            if (beam.beamType == BeamType.Railgun)
            {
                radius = 1.5f;
                depth = 6f;
            }
            else if (beam.beamType == BeamType.Revolver && superBeams.Contains(beam.name.Replace("(Clone)", "").Trim()))
            {
                radius = 0.9f;
                depth = 3f;
            }
            else
            {
                return;
            }
            var lr = beam.GetComponent<LineRenderer>();
            if (lr == null || lr.positionCount < 2)
            {
                return;
            }
            Vector3 from = lr.GetPosition(0), to = lr.GetPosition(lr.positionCount - 1);
            if (!lr.useWorldSpace)
            {
                from = lr.transform.TransformPoint(from);
                to = lr.transform.TransformPoint(to);
            }
            Vector3 dir = to - from;
            if (dir.sqrMagnitude < 0.01f)
            {
                return;
            }
            to += dir.normalized * depth * Coords.U;  // on into what it hit
            Coords.ToMc(from, out double fx, out double fy, out double fz);
            Coords.ToMc(to, out double tx, out double ty, out double tz);
            Add(new ActorRecord
            {
                X = (float)fx, Y = (float)fy, Z = (float)fz,
                Yaw = (float)tx, Width = (float)ty, Height = (float)tz,
                HealthFrac = radius,
                Level = KindTunnel,
            });
            Plugin.Log.LogInfo($"destruction: {beam.beamType} beam tunnel, radius {radius}");
        }

        // The revolvers' charged shots' beams (Patches: Revolver.Shoot), by name: only those dig.
        private static readonly HashSet<string> superBeams = new HashSet<string>();

        public static void ChargedShot(Revolver revolver)
        {
            if (revolver != null && revolver.revolverBeamSuper != null)
            {
                superBeams.Add(revolver.revolverBeamSuper.name);
            }
        }

        // ULTRAKILL's weapons hitting one of Minecraft's mobs (MobHits): Minecraft damage, and which way
        // it's pushed.
        public static void Damage(uint entityId, float amount, Vector3 direction)
        {
            Vector3 d = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero;
            Add(new ActorRecord
            {
                X = entityId, Width = amount,
                Yaw = d.x, Height = -d.z,  // (Minecraft's z runs the other way)
                HealthFrac = 0.4f,
                Level = KindDamage,
            }, 0f, 0.2f);
        }

        // The red shotgun's chainsaw cutting into the Nether: the blocks it touches break (and drop).
        private static float sawAt = -100f;

        public static void Saw(Vector3 at)
        {
            if (!InNether(at) || Time.unscaledTime - sawAt < 0.15f)
            {
                return;
            }
            sawAt = Time.unscaledTime;
            Coords.ToMc(at, out double x, out double y, out double z);
            Add(new ActorRecord { X = (float)x, Y = (float)y, Z = (float)z, HealthFrac = 1.2f, Level = KindDig }, 0f, 0.3f);
        }

        // Minecraft's player out of the Nether, back through its portal (Killcraft's data pack:
        // killcraft:leave_nether, run by the mod as the player).
        // (Asked for until Minecraft's player is out, for at most 10 seconds: Killcraft publishes
        // nothing between levels, so a request made on the way would be gone before the mod saw it.)
        private static float leaveUntil = -100f;
        private static int leaveSeq;

        public static void LeaveNether()
        {
            leaveSeq++;
            leaveUntil = Time.unscaledTime + 10f;
            Plugin.Log.LogInfo("Minecraft's player leaves the Nether");
        }

        private static void Add(ActorRecord record) => Add(record, GatherSeconds, PublishSeconds);

        private static void Add(ActorRecord record, float gather, float publish)
        {
            seq++;
            record.FormId = RequestIds + (uint)(seq % RequestCount);
            record.Flags = Proto.ActorDead;
            record.Name = Encoding.UTF8.GetBytes("kc" + seq);
            float now = Time.unscaledTime;
            requests.Add(new Request { Record = record, From = now + gather, Until = now + gather + publish });
        }

        // Adds the requests still being published to the actor table; returns the new count.
        public static int Publish(ActorRecord[] records, int count)
        {
            float now = Time.unscaledTime;
            requests.RemoveAll(r =>
            {
                if (now > r.Until)
                {
                    if ((r.Record.Level & 0xFF) == KindExplosion)
                    {
                        Plugin.Log.LogInfo($"destruction: explosion of power {r.Record.Width:0.0}{((r.Record.Level & Fire) != 0 ? " with fire" : "")} " +
                            $"at ({r.Record.X:0}, {r.Record.Y:0}, {r.Record.Z:0})");
                    }
                    return true;
                }
                return false;
            });
            bool stillInNether = Time.unscaledTime - Combat.DimensionAt > 2f || Combat.DimensionNether;
            if (now < leaveUntil && stillInNether && count < records.Length)
            {
                records[count++] = new ActorRecord
                {
                    FormId = RequestIds + RequestCount,  // (its own id: never one of the others)
                    Flags = Proto.ActorDead,
                    Level = KindLeaveNether,
                    Name = Encoding.UTF8.GetBytes("kcleave" + leaveSeq),
                };
            }
            foreach (Request r in requests)
            {
                if (now < r.From)
                {
                    continue;
                }
                if (count >= records.Length)
                {
                    break;
                }
                records[count++] = r.Record;
            }
            return count;
        }
    }
}
