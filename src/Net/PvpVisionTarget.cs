using System.Threading;
using ULTRAKILL.Enemy;
using UnityEngine;

namespace Killcraft
{
    // Another player's PvP target (Pvp), as ULTRAKILL's newer targeting sees its enemies: coins
    // looking for something to ricochet to (and whatever else asks its TargetTracker for enemies in
    // sight) only know what's registered there, which each enemy's own Enemy script does as it starts.
    // The target has none, so this registers it: an enemy where the other player is drawn, its body's
    // middle and its head (coins go for heads). While it's on (the other player alive, here).
    internal sealed class PvpVisionTarget : MonoBehaviour, ITarget
    {
        public EnemyIdentifier Eid;
        public Transform Body, Head;

        private CancellationTokenSource registered;
        private Vector3 position, headPosition, velocity, last;
        private Quaternion rotation = Quaternion.identity;
        private static bool loggedOn, loggedFailed;

        public int Id => GetInstanceID();
        public TargetType Type => TargetType.ENEMY;
        public EnemyIdentifier EID => Eid;
        public GameObject GameObject => this == null ? null : gameObject;
        public Rigidbody Rigidbody => null;
        public Transform Transform => transform;
        public Vector3 Position => position;
        public Vector3 HeadPosition => headPosition;

        public void SetData(ref TargetData data)
        {
            data.position = position;
            data.realPosition = position;
            data.headPosition = headPosition;
            data.realHeadPosition = headPosition;
            data.rotation = rotation;
            data.velocity = velocity;
        }

        public void UpdateCachedTransformData()
        {
            if (this == null)
            {
                return;
            }
            Vector3 now = Body != null ? Body.position : transform.position;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            velocity = (now - last) / dt;
            last = now;
            position = now;
            headPosition = Head != null ? Head.position : now;
            rotation = transform.rotation;
        }

        private void OnEnable()
        {
            UpdateCachedTransformData();
            velocity = Vector3.zero;
            try
            {
                var portals = MonoSingleton.GetInstance(typeof(ULTRAKILL.Portal.PortalManagerV2)) as ULTRAKILL.Portal.PortalManagerV2;
                TargetTracker tracker = portals != null ? portals.TargetTracker : null;
                if (tracker == null)
                {
                    return;
                }
                registered = new CancellationTokenSource();
                tracker.RegisterTarget(this, registered.Token);
                if (!loggedOn)
                {
                    loggedOn = true;
                    Plugin.Log.LogInfo("PvP: other players are in ULTRAKILL's targeting (coins find them)");
                }
            }
            catch (System.Exception e)
            {
                registered = null;
                if (!loggedFailed)
                {
                    loggedFailed = true;
                    Plugin.Log.LogWarning($"PvP: couldn't put another player in ULTRAKILL's targeting ({e.Message}); coins won't go to them");
                }
            }
        }

        private void OnDisable()
        {
            if (registered != null)
            {
                try
                {
                    registered.Cancel();
                    registered.Dispose();
                }
                catch
                {
                }
                registered = null;
            }
        }
    }
}
