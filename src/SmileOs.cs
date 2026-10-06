using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // SMILEOS terminals placed in Minecraft (Killcraft's mod's block; the idea: alfr0762): each gets a
    // working copy of ULTRAKILL's own SMILEOS shop terminal standing on it, facing where the block
    // faces. The copy is of a level's terminal (kept from an earlier level for levels without one).
    // The mod reports each terminal near the player every second; one not heard of for a while
    // (broken, or out of range) goes.
    internal static class SmileOs
    {
        private sealed class Terminal
        {
            public GameObject Go;
            public float Seen;
            public int Facing;
        }

        private static readonly Dictionary<Vector3Int, Terminal> terminals = new Dictionary<Vector3Int, Terminal>();
        // (Game time: a paused Minecraft says nothing, and that mustn't take the terminals away.)
        private const float ForgetSeconds = 10f;
        private const string CloneName = "Killcraft SMILEOS terminal";

        // The terminal to copy (inactive, kept across levels), and how it stood in its level: its
        // rotation there (levels keep it tilted, its parts turned back upright), its bottom below its
        // position, its middle off its position and its screen's direction (both flat, in the world).
        private static GameObject template;
        private static Quaternion templateRotation = Quaternion.identity;
        private static float bottomOffset;
        private static Vector3 worldCenter, worldScreen = Vector3.back;
        private static bool loggedNone;

        public static void Seen(int x, int y, int z, int facing)
        {
            if (Coords.IsNetherX(x) != Coords.InNether)
            {
                return;
            }
            var key = new Vector3Int(x, y, z);
            if (!terminals.TryGetValue(key, out Terminal t))
            {
                terminals[key] = t = new Terminal { Facing = facing };
            }
            if (t.Facing != facing && t.Go != null)
            {
                Object.Destroy(t.Go);
                t.Go = null;
            }
            t.Facing = facing;
            t.Seen = Time.time;
        }

        public static void LevelChanged()
        {
            foreach (Terminal t in terminals.Values)
            {
                if (t.Go != null)
                {
                    Object.Destroy(t.Go);
                }
            }
            terminals.Clear();
            loggedNone = false;
        }

        private static readonly List<Vector3Int> gone = new List<Vector3Int>();

        public static void Frame(bool active)
        {
            if (!active)
            {
                if (terminals.Count > 0)
                {
                    LevelChanged();
                }
                return;
            }
            float now = Time.time;
            gone.Clear();
            foreach (var kv in terminals)
            {
                Terminal t = kv.Value;
                // (Only those in the world shown: the level's aren't in the Nether.)
                if (now - t.Seen > ForgetSeconds || Coords.IsNetherX(kv.Key.x) != Coords.InNether)
                {
                    if (t.Go != null)
                    {
                        Object.Destroy(t.Go);
                    }
                    gone.Add(kv.Key);
                    continue;
                }
                if (t.Go == null)
                {
                    t.Go = Spawn();
                    if (t.Go == null)
                    {
                        continue;
                    }
                    Place(t.Go, kv.Key, t.Facing);
                    Plugin.Log.LogInfo($"SMILEOS: a terminal for the block at {kv.Key} (facing {t.Facing}), placed at {t.Go.transform.position}");
                }
                Place(t.Go, kv.Key, t.Facing);
            }
            foreach (Vector3Int key in gone)
            {
                terminals.Remove(key);
            }
        }

        private static GameObject Spawn()
        {
            FindTemplate();
            if (template == null)
            {
                if (!loggedNone)
                {
                    loggedNone = true;
                    Plugin.Log.LogWarning("SMILEOS: no shop to put there (ULTRAKILL's shop prefab didn't load)");
                }
                return null;
            }
            GameObject go = Object.Instantiate(template);
            go.name = CloneName;
            return go;
        }

        // Minecraft's horizontal facings (get2DDataValue: south, west, north, east) as ULTRAKILL
        // directions (Minecraft's +Z is ULTRAKILL's -Z).
        private static readonly Vector3[] facings = { Vector3.back, Vector3.left, Vector3.forward, Vector3.right };

        private static void Place(GameObject go, Vector3Int block, int facing)
        {
            Vector3 face = facings[Mathf.Clamp(facing, 0, 3)];
            // Turned about the vertical from how it stood, so its screen faces the block's way.
            Quaternion turn = Quaternion.AngleAxis(Vector3.SignedAngle(worldScreen, face, Vector3.up), Vector3.up);
            Vector3 foot = Coords.ToUnity(block.x + 0.5, block.y + 2.0 / 16.0, block.z + 0.5);
            go.transform.SetPositionAndRotation(foot - turn * worldCenter + Vector3.up * bottomOffset, turn * templateRotation);
        }

        // ULTRAKILL's own shop prefab: a level's shop can't be copied, its cabinet is merged into the
        // level's static geometry (Unity's static batching) and a copy of it draws nothing.
        private const string ShopPrefab = "Assets/Prefabs/Levels/Shop.prefab";
        private static bool triedPrefab;

        private static void FindTemplate()
        {
            if (template != null || triedPrefab)
            {
                return;
            }
            triedPrefab = true;
            try
            {
                template = AssetHelper.LoadPrefab(ShopPrefab);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"SMILEOS: couldn't load {ShopPrefab}: {e.Message}");
            }
            if (template == null)
            {
                return;
            }
            Transform root = template.transform;
            // Its size and footing from the cabinet's meshes (the prefab isn't in the world, so from
            // their own bounds, through the prefab's transforms).
            Transform model = root.Find("ShopTerminal") ?? root;
            Bounds bounds = default;
            bool any = false;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds b = mf.sharedMesh.bounds;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = m.MultiplyPoint3x4(new Vector3(
                        (c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z));
                    if (!any)
                    {
                        bounds = new Bounds(corner, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(corner);
                    }
                }
            }
            templateRotation = root.rotation;
            bottomOffset = any ? root.position.y - bounds.min.y : 0f;
            Vector3 center = any ? bounds.center - root.position : Vector3.zero;
            worldCenter = new Vector3(center.x, 0f, center.z);
            Canvas canvas = root.GetComponentInChildren<Canvas>(true);
            // A world-space canvas is read looking along its forward: its screen faces back.
            Vector3 screen = canvas != null ? -canvas.transform.forward : Vector3.back;
            screen.y = 0f;
            worldScreen = screen.sqrMagnitude > 1e-4f ? screen.normalized : Vector3.back;
            Plugin.Log.LogInfo($"SMILEOS: ULTRAKILL's shop, {bounds.size} big, bottom {bottomOffset:0.00} below it, middle {worldCenter}, " +
                $"screen facing {worldScreen}, rotation {root.rotation.eulerAngles}");
        }
    }
}
