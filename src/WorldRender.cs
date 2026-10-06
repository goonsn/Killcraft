using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace Killcraft
{
    // Draws Minecraft's things inside ULTRAKILL, from SkyCraft's render ring and world-entity table:
    //  - placed blocks, meshed by Minecraft's own block renderer (models, tint, ambient occlusion),
    //    with ULTRAKILL's environment shader so ULTRAKILL's lights, fog and PSX wobble apply;
    //  - their collision, so ULTRAKILL's enemies, projectiles and beams are stopped by your builds;
    //  - light from torches, lava and glowstone as ULTRAKILL point lights;
    //  - arrows (also stuck in walls and enemies), dropped items, mining cracks and the block outline;
    //  - every other entity and the particles (lit TNT, falling sand, explosion smoke), as one mesh.
    internal static unsafe class WorldRender
    {
        private sealed class Section
        {
            public int Sx, Sy, Sz;
            public GameObject Go;
            public Mesh Mesh;
            public GameObject Solids;
            public GameObject Lights;
        }

        private static GameObject root;
        private static Texture2D atlas;
        private static Material cutout, translucent, lines;
        private static readonly Dictionary<long, Section> sections = new Dictionary<long, Section>();
        private static readonly Dictionary<(int, int), Texture2D> regionScratch = new Dictionary<(int, int), Texture2D>();

        private static Mesh dynamicMesh, outlineMesh, arrowMesh;
        private static GameObject dynamicGo, outlineGo;
        private static readonly List<Vector3> dv = new List<Vector3>();
        private static readonly List<Vector2> duv = new List<Vector2>();
        private static readonly List<Color32> dc = new List<Color32>();
        private static readonly List<int> dSolid = new List<int>();
        private static readonly List<int> dCrack = new List<int>();
        private static readonly float[] arrowSide = new float[4], arrowBack = new float[4];
        private static bool haveArrowUv;
        private static readonly LinkedList<(GameObject Arrow, EnemyIdentifier In)> stuckArrows = new LinkedList<(GameObject, EnemyIdentifier)>();

        // Entity textures (TNT minecarts, particles' atlas, mobs, ...) for the scene, by Minecraft's id.
        private sealed class EntityTexture
        {
            public Texture2D Tex;
            public Material Cutout, Translucent;
        }

        private static readonly Dictionary<uint, EntityTexture> textures = new Dictionary<uint, EntityTexture>();

        // The latest scene (every entity but the player, arrows and items, plus all particles): kept
        // as it came and built once per frame, since Minecraft can send several between two frames.
        private static byte[] sceneBuf = new byte[0];
        private static int sceneLen;
        private static bool sceneNew;
        private static Mesh sceneMesh;
        private static GameObject sceneGo;

        private const float ArrowScale = 0.7f;

        public static bool IsOurs(Collider c) => root != null && c.transform.IsChildOf(root.transform);

        // Killcraft's own objects (Collision leaves them out: they're Minecraft's blocks already).
        public static Transform OurRoot => Root;

        private static Transform Root
        {
            get
            {
                if (root == null)
                {
                    root = new GameObject("Killcraft world");
                    UnityEngine.Object.DontDestroyOnLoad(root);
                }
                return root.transform;
            }
        }

        private static long Key(int x, int y, int z) => Collision.Key(x, y, z);

        // ---- materials ----------------------------------------------------------------------------

        private static readonly HashSet<string> keepKeywords = new HashSet<string>
        {
            "VERTEX_LIGHTING", "VERTEX_WARPING", "_FOG_ON", "_FOG_OFF", "NO_TEXTURE_WARPING",
        };

        private static bool fromTemplate;
        private static readonly List<Renderer> renderers = new List<Renderer>();
        private static int debugMode = -1;

        // F7 (debug): cycle block material set-ups to compare how ULTRAKILL lights them.
        public static void DebugCycle()
        {
            debugMode = (debugMode + 1) % 7;
            Material m = DebugMaterial(debugMode, out string what);
            Plugin.Log.LogInfo($"render debug: mode {debugMode}: {what} -> {(m != null ? m.shader.name : "unavailable")}");
            if (m == null)
            {
                return;
            }
            m.mainTexture = atlas;
            renderers.RemoveAll(r => r == null);
            foreach (Renderer r in renderers)
            {
                r.sharedMaterials = r.sharedMaterials.Length == 2 ? new[] { m, translucent } : new[] { m };
            }
        }

        private static Material DebugMaterial(int mode, out string what)
        {
            Shader master = FindShader("ULTRAKILL/Master");
            Material m = null;
            switch (mode)
            {
                case 0:
                    what = "level template clone";
                    return cutout;
                case 1:
                    what = "fresh Master + VERTEX_LIGHTING";
                    if (master != null)
                    {
                        m = new Material(master);
                        m.EnableKeyword("VERTEX_LIGHTING");
                        m.EnableKeyword("_FOG_ON");
                    }
                    break;
                case 2:
                    what = "fresh Master, no vertex lighting";
                    if (master != null)
                    {
                        m = new Material(master);
                        m.EnableKeyword("_FOG_ON");
                    }
                    break;
                case 3:
                    what = "enemy material clone";
                    foreach (Material t in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        if (t != null && t.shader == master && t.IsKeywordEnabled("ENEMY") && !t.IsKeywordEnabled("TRANSPARENCY"))
                        {
                            m = new Material(t);
                            m.shaderKeywords = Array.FindAll(m.shaderKeywords, k => keepKeywords.Contains(k) || k == "ENEMY");
                            what += $" ({t.name})";
                            break;
                        }
                    }
                    break;
                case 4:
                    what = "psx/vertexlit/alphatest";
                    Shader psx = FindShader("psx/vertexlit/alphatest");
                    m = psx != null ? new Material(psx) : null;
                    break;
                case 5:
                    what = "Particles/Standard Unlit cutout";
                    Shader p = FindShader("Particles/Standard Unlit", "ULTRAKILL/Particles/Standard Unlit");
                    if (p != null)
                    {
                        m = new Material(p);
                        m.EnableKeyword("_ALPHATEST_ON");
                        SetIfPresent(m, "_Mode", 1f);
                        SetIfPresent(m, "_Cutoff", 0.5f);
                    }
                    break;
                default:
                    what = "Unlit/Transparent Cutout";
                    Shader u = FindShader("Unlit/Transparent Cutout");
                    m = u != null ? new Material(u) : null;
                    break;
            }
            if (m != null && m.shader == master)
            {
                m.EnableKeyword("ALPHA_TEST");
                SetIfPresent(m, "_VertexColors", 1f);
                SetIfPresent(m, "_VertexLighting", mode == 2 ? 0f : 1f);
                SetIfPresent(m, "_CullMode", 0f);
                if (m.HasProperty("_Color"))
                {
                    m.SetColor("_Color", Color.white);
                }
                m.mainTextureScale = Vector2.one;
                m.mainTextureOffset = Vector2.zero;
            }
            if (m != null)
            {
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            return m;
        }

        // A level is loaded: if the materials were made before ULTRAKILL's level materials existed (on
        // the main menu), make them again from a real level material and give them to everything drawn.
        // ULTRAKILL lights levels through its own baked system (Unity's ambient is black), so block
        // brightness is a setting.
        private static void ApplyAmbient()
        {
            float k = Mathf.Clamp(Plugin.BlockBrightness.Value, 0.05f, 2f);
            var tint = new Color(k, k, k, 1f);
            var all = new List<Material> { cutout, translucent };
            foreach (EntityTexture t in textures.Values)
            {
                all.Add(t.Cutout);
                all.Add(t.Translucent);
            }
            foreach (Material m in all)
            {
                if (m != null && m.HasProperty("_Color"))
                {
                    m.SetColor("_Color", tint);
                }
            }
        }

        public static void LevelLoaded()
        {
            if (fromTemplate)
            {
                ApplyAmbient();
            }
            if (!fromTemplate)
            {
                cutout = null;
                if (EnsureMaterials())
                {
                    renderers.RemoveAll(r => r == null);
                    foreach (Renderer r in renderers)
                    {
                        r.sharedMaterials = r.sharedMaterials.Length == 2 ? new[] { cutout, translucent } : new[] { cutout };
                    }
                    if (outlineGo != null && lines != null)
                    {
                        outlineGo.GetComponent<MeshRenderer>().sharedMaterial = lines;
                    }
                }
            }
        }

        // ULTRAKILL's environment shader, copied from a level material so fog and lighting match.
        // False while no usable shader is loaded yet (main menu).
        private static bool EnsureMaterials()
        {
            if (cutout != null && cutout.shader != null)
            {
                return true;
            }
            // ULTRAKILL's environment shader without its vertex lighting (that needs ULTRAKILL's baked
            // per-mesh light data, which these meshes don't have, and renders them black): Minecraft's
            // own shading and light levels are in the vertex colours, and the level's ambient tints it.
            Material template = null;
            Shader master = FindShader("ULTRAKILL/Master");
            if (master != null)
            {
                cutout = new Material(master) { name = "Killcraft blocks" };
                cutout.EnableKeyword("_FOG_ON");
                SetIfPresent(cutout, "_VertexLighting", 0f);
            }
            else
            {
                Shader fallback = FindShader("Particles/Standard Unlit", "ULTRAKILL/Particles/Standard Unlit", "Unlit/Transparent Cutout");
                if (fallback == null)
                {
                    return false;
                }
                cutout = new Material(fallback) { name = "Killcraft blocks (fallback)" };
                cutout.EnableKeyword("_ALPHATEST_ON");
                SetIfPresent(cutout, "_Mode", 1f);
                Plugin.Log.LogWarning($"render: ULTRAKILL/Master not found; blocks use {fallback.name}");
            }
            fromTemplate = master != null;
            // Set up the way ULTRAKILL's own cutout materials (leaves, grates) are: blend mode 1 with
            // the cutoff in _Opacity. The keyword alone left transparent texels drawn (black boxes
            // around arrows, flowers and torches).
            cutout.EnableKeyword("ALPHA_TEST");
            SetIfPresent(cutout, "_BlendMode", 1f);
            SetIfPresent(cutout, "_VertexColors", 1f);
            SetIfPresent(cutout, "_CullMode", 0f);
            SetIfPresent(cutout, "_ZWrite", 1f);
            SetIfPresent(cutout, "_SrcBlend", (float)BlendMode.One);
            SetIfPresent(cutout, "_DstBlend", (float)BlendMode.Zero);
            SetIfPresent(cutout, "_Opacity", 0.5f);
            SetIfPresent(cutout, "_Cutoff", 0.5f);
            cutout.SetOverrideTag("RenderType", "TransparentCutout");
            ApplyAmbient();
            cutout.mainTextureOffset = Vector2.zero;
            cutout.mainTextureScale = Vector2.one;
            cutout.renderQueue = (int)RenderQueue.AlphaTest;

            translucent = new Material(cutout) { name = "Killcraft blocks (translucent)" };
            translucent.DisableKeyword("ALPHA_TEST");
            translucent.EnableKeyword("TRANSPARENCY");
            SetIfPresent(translucent, "_BlendMode", 2f);
            SetIfPresent(translucent, "_Opacity", 1f);
            SetIfPresent(translucent, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetIfPresent(translucent, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetIfPresent(translucent, "_ZWrite", 0f);
            translucent.SetOverrideTag("RenderType", "Transparent");
            translucent.renderQueue = (int)RenderQueue.Transparent;
            foreach (var tex in textures.Values)
            {
                tex.Cutout = null;  // remade from the new materials on next use
            }
            flash = null;

            Shader sprites = FindShader("Sprites/Default", "ULTRAKILL/Sprites/Default", "Unlit/Color");
            lines = sprites != null ? new Material(sprites) { name = "Killcraft outline", color = new Color(0f, 0f, 0f, 0.45f) } : null;
            Plugin.Log.LogInfo($"render: blocks drawn with {cutout.shader.name}{(template != null ? $" (from {template.name})" : "")}");
            if (atlas != null)
            {
                cutout.mainTexture = atlas;
                translucent.mainTexture = atlas;
            }
            return true;
        }

        private static Shader FindShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader s = Shader.Find(name);
                if (s != null)
                {
                    return s;
                }
            }
            foreach (Shader s in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (s != null && Array.IndexOf(names, s.name) >= 0)
                {
                    return s;
                }
            }
            return null;
        }

        private static void SetIfPresent(Material m, string name, float value)
        {
            if (m.HasProperty(name))
            {
                m.SetFloat(name, value);
            }
        }

        // ---- render ring ----------------------------------------------------------------------------

        private static float backlogLog;

        public static void Drain()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long before = Link.RenderBacklog();
            Link.DrainRender(OnMessage, 48L << 20);
            // Minecraft's render thread waits (holding the lock its server needs) while this ring is full.
            backlogLog -= Time.unscaledDeltaTime;
            if (Plugin.Diagnostics.Value && backlogLog <= 0f && (before > (8L << 20) || clock.ElapsedMilliseconds > 20))
            {
                backlogLog = 1f;
                Plugin.Log.LogInfo($"render ring: {before >> 10} KB waiting, {Link.RenderBacklog() >> 10} KB left after draining ({clock.ElapsedMilliseconds} ms)");
            }
        }

        private static void OnMessage(uint type, byte* p, int bytes)
        {
            try
            {
                switch (type)
                {
                    case Proto.RenAtlas:
                        OnAtlas(p);
                        break;
                    case Proto.RenAtlasRegion:
                        OnAtlasRegion(p);
                        break;
                    case Proto.RenSection:
                        OnSection(p);
                        break;
                    case Proto.RenSolids:
                        OnSolids(p);
                        break;
                    case Proto.RenLights:
                        OnLights(p);
                        break;
                    case Proto.RenClearAll:
                        ClearAll();
                        break;
                    case Proto.RenTexture:
                        OnTexture(p, bytes);
                        break;
                    case Proto.RenAvatar:
                        if (avatarBuf.Length < bytes)
                        {
                            avatarBuf = new byte[Math.Max(bytes, avatarBuf.Length * 2)];
                        }
                        System.Runtime.InteropServices.Marshal.Copy((IntPtr)p, avatarBuf, 0, bytes);
                        avatarLen = bytes;
                        avatarNew = true;
                        break;
                    case Proto.RenScene:
                        if (sceneBuf.Length < bytes)
                        {
                            sceneBuf = new byte[Math.Max(bytes, sceneBuf.Length * 2)];
                        }
                        System.Runtime.InteropServices.Marshal.Copy((IntPtr)p, sceneBuf, 0, bytes);
                        sceneLen = bytes;
                        sceneNew = true;
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"render: message {type} failed: {e}");
            }
        }

        private static void OnAtlas(byte* p)
        {
            int w = *(int*)p, h = *(int*)(p + 4);
            if (atlas != null)
            {
                UnityEngine.Object.Destroy(atlas);
            }
            atlas = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Minecraft atlas" };
            atlas.LoadRawTextureData((IntPtr)(p + 8), w * h * 4);
            atlasPixels = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(p + 8), atlasPixels, 0, atlasPixels.Length);
            itemModels.Clear();
            atlas.Apply(false, true);
            if (EnsureMaterials())
            {
                cutout.mainTexture = atlas;
                translucent.mainTexture = atlas;
            }
            Plugin.Log.LogInfo($"render: Minecraft atlas {w}x{h}");
        }

        private static void OnTexture(byte* p, int bytes)
        {
            uint id = *(uint*)p;
            int w = *(int*)(p + 4), h = *(int*)(p + 8);
            if (w <= 0 || h <= 0 || 16 + (long)w * h * 4 > bytes)
            {
                return;
            }
            if (textures.TryGetValue(id, out EntityTexture old) && old.Tex != null)
            {
                UnityEngine.Object.Destroy(old.Tex);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = $"Minecraft texture {id}" };
            tex.LoadRawTextureData((IntPtr)(p + 16), w * h * 4);
            tex.Apply(false, true);
            textures[id] = new EntityTexture { Tex = tex };
        }

        private static Material SceneMaterial(uint texture, bool blended)
        {
            if (texture == 0)
            {
                return blended ? translucent : cutout;
            }
            if (!textures.TryGetValue(texture, out EntityTexture t) || t.Tex == null)
            {
                return null;
            }
            if (t.Cutout == null)
            {
                t.Cutout = new Material(cutout) { mainTexture = t.Tex };
                t.Translucent = new Material(translucent) { mainTexture = t.Tex };
            }
            return blended ? t.Translucent : t.Cutout;
        }

        private static readonly List<Vector3> sv = new List<Vector3>();
        private static readonly List<Vector2> suv = new List<Vector2>();
        private static readonly List<Color32> sc = new List<Color32>();
        private static readonly List<List<int>> sIdx = new List<List<int>>();
        private static readonly List<Material> sMats = new List<Material>();
        private static int lastEntitySig = -1;
        private static bool[] blockAtlasVertex = new bool[0], flashVertex = new bool[0];
        private static readonly List<int> flashIdx = new List<int>();
        private static Material flash;

        // Lit TNT's flash: plain white, lit or not.
        private static Material FlashMaterial()
        {
            if (flash == null && cutout != null)
            {
                flash = new Material(cutout) { name = "Killcraft flash", mainTexture = Texture2D.whiteTexture };
                if (flash.HasProperty("_Color"))
                {
                    flash.SetColor("_Color", Color.white);
                }
            }
            return flash;
        }

        // RenScene: origin (3 doubles), batch count, vertex count, RenBatch[] (texture, first, count,
        // flags), RenVertex[] relative to the origin.
        // RenAvatar (Minecraft's own player, sent in third person): the same without the origin,
        // relative to the player's feet; built in place and moved with the player (PlaceAvatar).
        private static void BuildScene()
        {
            if (sceneNew)
            {
                sceneNew = false;
                BuildBatches(sceneBuf, sceneLen, true, ref sceneGo, ref sceneMesh, "Minecraft scene");
            }
            if (avatarNew)
            {
                avatarNew = false;
                BuildBatches(avatarBuf, avatarLen, false, ref avatarGo, ref avatarMesh, "Minecraft player");
                if (avatarGo != null && !avatarShown)
                {
                    avatarGo.SetActive(false);
                }
            }
        }

        private static byte[] avatarBuf = new byte[0];
        private static int avatarLen;
        private static bool avatarNew, avatarShown;
        private static GameObject avatarGo;
        private static Mesh avatarMesh;

        // Where Minecraft's player model goes (its feet), or hidden.
        public static void PlaceAvatar(bool show, Vector3 feet)
        {
            avatarShown = show;
            if (avatarGo == null)
            {
                return;
            }
            if (!show)
            {
                avatarGo.SetActive(false);
                return;
            }
            avatarGo.transform.position = feet;
            if (!avatarGo.activeSelf && avatarMesh != null && avatarMesh.vertexCount > 0)
            {
                avatarGo.SetActive(true);
            }
        }

        private static void BuildBatches(byte[] buf, int len, bool withOrigin, ref GameObject go, ref Mesh mesh, string name)
        {
            if (go == null)
            {
                mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
                go = new GameObject(name);
                go.transform.SetParent(Root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            GameObject sceneGo = go;
            Mesh sceneMesh = mesh;
            int head = withOrigin ? 24 : 0;
            float u = Coords.U;
            sv.Clear();
            suv.Clear();
            sc.Clear();
            sMats.Clear();
            flashIdx.Clear();
            int used = 0;
            fixed (byte* p = buf)
            {
                if (len < head + 8)
                {
                    sceneGo.SetActive(false);
                    return;
                }
                double ox = withOrigin ? *(double*)p : 0, oy = withOrigin ? *(double*)(p + 8) : 0, oz = withOrigin ? *(double*)(p + 16) : 0;
                int batches = *(int*)(p + head), vertices = *(int*)(p + head + 4);
                byte* vb = p + head + 8 + batches * 16;
                if (batches <= 0 || vertices <= 0 || head + 8 + batches * 16 + (long)vertices * 32 > len)
                {
                    sceneGo.SetActive(false);
                    return;
                }
                // Blocks drawn as entities (lit TNT, falling blocks) use the block atlas. SkyCraft
                // marks Minecraft's white flash (lit TNT about to go) as full block and sky light,
                // which nothing else in the block atlas gets: draw that white, as Minecraft does.
                if (blockAtlasVertex.Length < vertices)
                {
                    blockAtlasVertex = new bool[Math.Max(vertices, blockAtlasVertex.Length * 2)];
                    flashVertex = new bool[blockAtlasVertex.Length];
                }
                Array.Clear(blockAtlasVertex, 0, vertices);
                for (int b = 0; b < batches; b++)
                {
                    byte* hdr = p + head + 8 + b * 16;
                    int first = *(int*)(hdr + 4), count = *(int*)(hdr + 8);
                    if (*(uint*)hdr == 0 && first >= 0 && count > 0 && first + count <= vertices)
                    {
                        for (int i = first; i < first + count; i++)
                        {
                            blockAtlasVertex[i] = true;
                        }
                    }
                }
                for (int i = 0; i < vertices; i++)
                {
                    byte* v = vb + i * 32;
                    sv.Add(withOrigin ? Coords.ToUnity(ox + *(float*)v, oy + *(float*)(v + 4), oz + *(float*)(v + 8))
                        : new Vector3(*(float*)v * u, *(float*)(v + 4) * u, -*(float*)(v + 8) * u));
                    suv.Add(new Vector2(*(float*)(v + 12), *(float*)(v + 16)));
                    uint c = *(uint*)(v + 20), light = *(uint*)(v + 24), flags = *(uint*)(v + 28);
                    float k = faceShade[(flags >> 4) & 7] * (0.3f + 0.7f * Mathf.Max(LightCurve((int)(light & 0xFF)), LightCurve((int)((light >> 8) & 0xFF))));
                    var col = new Color32((byte)((c & 0xFF) * k), (byte)(((c >> 8) & 0xFF) * k), (byte)(((c >> 16) & 0xFF) * k), (byte)(c >> 24));
                    // (Vertex colours multiply the texture, so white needs its own white material.)
                    flashVertex[i] = blockAtlasVertex[i] && (light & 0xFF) >= 15 && ((light >> 8) & 0xFF) >= 15;
                    sc.Add(flashVertex[i] ? new Color32(255, 255, 255, 255) : col);
                }
                for (int b = 0; b < batches; b++)
                {
                    byte* hdr = p + head + 8 + b * 16;
                    uint texture = *(uint*)hdr;
                    int first = *(int*)(hdr + 4), count = *(int*)(hdr + 8);
                    uint flags = *(uint*)(hdr + 12);
                    Material m = SceneMaterial(texture, (flags & 1) != 0);
                    if (m == null || first < 0 || count <= 0 || first + count > vertices)
                    {
                        continue;
                    }
                    if (sIdx.Count <= used)
                    {
                        sIdx.Add(new List<int>());
                    }
                    List<int> idx = sIdx[used];
                    idx.Clear();
                    for (int t = first; t + 2 < first + count; t += 3)
                    {
                        List<int> to = flashVertex[t] ? flashIdx : idx;
                        to.Add(t);
                        to.Add(t + 2);
                        to.Add(t + 1);
                    }
                    sMats.Add(m);
                    used++;
                }
                if (flashIdx.Count > 0 && FlashMaterial() != null)
                {
                    if (sIdx.Count <= used)
                    {
                        sIdx.Add(new List<int>());
                    }
                    sIdx[used].Clear();
                    sIdx[used].AddRange(flashIdx);
                    sMats.Add(flash);
                    used++;
                }
            }
            if (used == 0)
            {
                sceneGo.SetActive(false);
                return;
            }
            sceneMesh.Clear();
            sceneMesh.SetVertices(sv);
            sceneMesh.SetUVs(0, suv);
            sceneMesh.SetColors(sc);
            sceneMesh.subMeshCount = used;
            for (int i = 0; i < used; i++)
            {
                sceneMesh.SetTriangles(sIdx[i], i, false);
            }
            sceneMesh.RecalculateBounds();
            sceneGo.GetComponent<MeshRenderer>().sharedMaterials = sMats.ToArray();
            sceneGo.SetActive(true);
        }

        // Animated textures (water, lava, fire): copied into the atlas on the GPU, not re-uploaded whole.
        private static void OnAtlasRegion(byte* p)
        {
            if (atlas == null)
            {
                return;
            }
            int x = *(int*)p, y = *(int*)(p + 4), w = *(int*)(p + 8), h = *(int*)(p + 12);
            if (w <= 0 || h <= 0 || x + w > atlas.width || y + h > atlas.height)
            {
                return;
            }
            if (!regionScratch.TryGetValue((w, h), out Texture2D scratch))
            {
                regionScratch[(w, h)] = scratch = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            }
            scratch.LoadRawTextureData((IntPtr)(p + 16), w * h * 4);
            if (atlasPixels != null)
            {
                for (int row = 0; row < h; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)(p + 16 + row * w * 4), atlasPixels, ((y + row) * atlas.width + x) * 4, w * 4);
                }
            }
            scratch.Apply(false, false);
            Graphics.CopyTexture(scratch, 0, 0, 0, 0, w, h, atlas, 0, 0, x, y);
        }

        private static Section GetSection(int sx, int sy, int sz, bool create)
        {
            long key = Key(sx, sy, sz);
            if (sections.TryGetValue(key, out Section s) && s.Go != null)
            {
                return s;
            }
            if (!create)
            {
                return null;
            }
            s = new Section { Sx = sx, Sy = sy, Sz = sz, Go = new GameObject($"Minecraft section {sx} {sy} {sz}") };
            s.Go.layer = 8;
            s.Go.transform.SetParent(Root, false);
            s.Go.transform.position = Coords.ToUnity(sx * 16, sy * 16, sz * 16);
            sections[key] = s;
            return s;
        }

        // Minecraft's fixed face shading (left out of the vertex colours): up 1, down 0.5, N/S 0.8, E/W 0.6.
        private static readonly float[] faceShade = { 1f, 0.5f, 1f, 0.8f, 0.8f, 0.6f, 0.6f, 1f };

        private static float LightCurve(int level)
        {
            float f = level / 15f;
            return f / (4f - 3f * f);
        }

        private static void OnSection(byte* p)
        {
            int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8), n = *(int*)(p + 12);
            Section s = GetSection(sx, sy, sz, n > 0);
            if (n == 0)
            {
                if (s != null && s.Mesh != null)
                {
                    s.Mesh.Clear();
                }
                return;
            }
            EnsureMaterials();
            float u = Coords.U;
            var verts = new Vector3[n];
            var uvs = new Vector2[n];
            var colors = new Color32[n];
            var solid = new List<int>(n);
            var glass = new List<int>();
            byte* v = p + 16;
            for (int i = 0; i < n; i++, v += 32)
            {
                float x = *(float*)v, y = *(float*)(v + 4), z = *(float*)(v + 8);
                verts[i] = new Vector3(x * u, y * u, -z * u);
                uvs[i] = new Vector2(*(float*)(v + 12), *(float*)(v + 16));
                uint c = *(uint*)(v + 20), light = *(uint*)(v + 24), flags = *(uint*)(v + 28);
                float shade = faceShade[(flags >> 4) & 7];
                float lit = 0.3f + 0.7f * Mathf.Max(LightCurve((int)(light & 0xFF)), LightCurve((int)((light >> 8) & 0xFF)));
                float k = shade * lit;
                colors[i] = new Color32((byte)((c & 0xFF) * k), (byte)(((c >> 8) & 0xFF) * k), (byte)(((c >> 16) & 0xFF) * k), (byte)(c >> 24));
            }
            for (int t = 0; t + 2 < n; t += 3)
            {
                uint flags = *(uint*)(p + 16 + t * 32 + 28);
                List<int> list = (flags & 2) != 0 ? glass : solid;
                // The Z mirror flips the winding back to front.
                list.Add(t);
                list.Add(t + 2);
                list.Add(t + 1);
            }
            if (s.Mesh == null)
            {
                s.Mesh = new Mesh { name = s.Go.name };
                s.Go.AddComponent<MeshFilter>().sharedMesh = s.Mesh;
                var mr = s.Go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = new[] { cutout, translucent };
                // (The Nether is thousands of sections: no shadows from its terrain.)
                mr.shadowCastingMode = Coords.IsNetherX(s.Sx * 16.0) ? ShadowCastingMode.Off : ShadowCastingMode.On;
                renderers.Add(mr);
            }
            s.Mesh.Clear();
            s.Mesh.indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            s.Mesh.vertices = verts;
            s.Mesh.uv = uvs;
            s.Mesh.colors32 = colors;
            s.Mesh.subMeshCount = 2;
            s.Mesh.SetTriangles(solid, 0);
            s.Mesh.SetTriangles(glass, 1);
            s.Mesh.RecalculateNormals();
            s.Mesh.RecalculateBounds();
        }

        // Which blocks are solid: ULTRAKILL colliders (environment layer, so enemies, projectiles and
        // hitscan stop at them) plus navmesh carving so walking enemies path around them.
        // (The Nether's terrain, thousands of sections full of blocks, gets merged colliders only near V1
        // and a navmesh of its own instead: NetherWorld.)
        private static void OnSolids(byte* p)
        {
            int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8), count = *(int*)(p + 12);
            if (Coords.IsNetherX(sx * 16.0))
            {
                NetherWorld.Solids(sx, sy, sz, count, (ulong*)(p + 16));
                count = 0;
            }
            Section s = GetSection(sx, sy, sz, count > 0);
            if (s == null)
            {
                return;
            }
            if (s.Solids != null)
            {
                UnityEngine.Object.Destroy(s.Solids);
                s.Solids = null;
            }
            if (count == 0)
            {
                return;
            }
            ulong* bits = (ulong*)(p + 16);
            bool Solid(int x, int y, int z)
            {
                int bit = x + 16 * z + 256 * y;
                return (bits[bit >> 6] & (1UL << (bit & 63))) != 0;
            }
            s.Solids = new GameObject("solids") { layer = 8 };
            s.Solids.transform.SetParent(s.Go.transform, false);
            float u = Coords.U;
            for (int y = 0; y < 16; y++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int x = 0; x < 16;)
                    {
                        if (!Solid(x, y, z))
                        {
                            x++;
                            continue;
                        }
                        int run = 1;
                        while (x + run < 16 && Solid(x + run, y, z))
                        {
                            run++;
                        }
                        // (Tagged as floor: ULTRAKILL's enemies only stand on tagged ground.)
                        var box = new GameObject("block") { layer = 8, tag = "Floor" };
                        box.transform.SetParent(s.Solids.transform, false);
                        box.transform.localPosition = new Vector3((x + run * 0.5f) * u, (y + 0.5f) * u, -(z + 0.5f) * u);
                        var col = box.AddComponent<BoxCollider>();
                        col.size = new Vector3(run * u, u, u);
                        var obstacle = box.AddComponent<NavMeshObstacle>();
                        obstacle.shape = NavMeshObstacleShape.Box;
                        obstacle.size = col.size;
                        obstacle.carving = true;
                        x += run;
                    }
                }
            }
        }

        // Light-emitting blocks (torches, lava, glowstone) light ULTRAKILL's world too. (Not in the
        // Nether: its lava seas would be thousands of lights. Minecraft's own light levels, in the
        // vertex colours, light it.)
        private static void OnLights(byte* p)
        {
            int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8), count = *(int*)(p + 12);
            if (Coords.IsNetherX(sx * 16.0))
            {
                count = 0;
            }
            Section s = GetSection(sx, sy, sz, count > 0);
            if (s == null)
            {
                return;
            }
            if (s.Lights != null)
            {
                UnityEngine.Object.Destroy(s.Lights);
                s.Lights = null;
            }
            if (count == 0)
            {
                return;
            }
            s.Lights = new GameObject("lights");
            s.Lights.transform.SetParent(s.Go.transform, false);
            float u = Coords.U;
            byte* e = p + 16;
            for (int i = 0; i < Math.Min(count, 32); i++, e += 8)
            {
                int x = e[0], y = e[1], z = e[2], level = e[3];
                uint color = *(uint*)(e + 4);
                var go = new GameObject("light");
                go.transform.SetParent(s.Lights.transform, false);
                go.transform.localPosition = new Vector3((x + 0.5f) * u, (y + 0.5f) * u, -(z + 0.5f) * u);
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color32((byte)color, (byte)(color >> 8), (byte)(color >> 16), 255);
                light.range = level * u * 0.9f;
                light.intensity = 1.2f;
                light.shadows = LightShadows.None;
            }
        }

        // Another ULTRAKILL level means another area of the Minecraft world: everything already
        // received stays (Minecraft won't send it again) and just moves with the new mapping, which puts
        // other levels' builds far away from this one.
        public static void Reposition()
        {
            foreach (Section s in sections.Values)
            {
                if (s.Go != null)
                {
                    s.Go.transform.position = Coords.ToUnity(s.Sx * 16, s.Sy * 16, s.Sz * 16);
                }
            }
        }

        public static void ClearAll()
        {
            NetherWorld.Clear();
            foreach (Section s in sections.Values)
            {
                if (s.Go != null)
                {
                    UnityEngine.Object.Destroy(s.Go);
                }
                if (s.Mesh != null)
                {
                    UnityEngine.Object.Destroy(s.Mesh);
                }
            }
            sections.Clear();
        }

        // ---- per-frame things: arrows, items, cracks, outline ------------------------------------------

        public static void Frame(bool show)
        {
            PruneStuckArrows();
            if (!show || cutout == null || atlas == null)
            {
                if (dynamicGo != null)
                {
                    dynamicGo.SetActive(false);
                    outlineGo.SetActive(false);
                }
                if (sceneGo != null)
                {
                    sceneGo.SetActive(false);
                    sceneNew = sceneLen > 0;  // shown again as soon as the world is
                }
                return;
            }
            BuildScene();
            EnsureDynamic();
            dv.Clear();
            duv.Clear();
            dc.Clear();
            dSolid.Clear();
            dCrack.Clear();
            if (!Link.ReadWorldEntities(out Link.WorldEntity[] entities, out int count, out bool hasSelection, out Vector3 selMin, out Vector3 selMax))
            {
                return;
            }
            if (Plugin.Diagnostics.Value)
            {
                int items = 0, blocks = 0, arrows = 0;
                for (int i = 0; i < count; i++)
                {
                    items += entities[i].Kind == 2 ? 1 : 0;
                    blocks += entities[i].Kind == 4 ? 1 : 0;
                    arrows += entities[i].Kind == 1 ? 1 : 0;
                }
                int sig = items * 10000 + blocks * 100 + arrows;
                if (sig != lastEntitySig)
                {
                    lastEntitySig = sig;
                    string first = "";
                    for (int i = 0; i < count; i++)
                    {
                        if (entities[i].Kind == 2 || entities[i].Kind == 4)
                        {
                            first = $"; first at Minecraft ({entities[i].X:0.0}, {entities[i].Y:0.0}, {entities[i].Z:0.0}) = ULTRAKILL {Mc(new Vector3(entities[i].X, entities[i].Y, entities[i].Z))}";
                            break;
                        }
                    }
                    Plugin.Log.LogInfo($"render: Minecraft shows {items} items, {blocks} block items, {arrows} arrows{first}");
                }
            }
            for (int i = 0; i < count; i++)
            {
                ref Link.WorldEntity e = ref entities[i];
                switch (e.Kind)
                {
                    case 1:  // arrow
                    case 3:  // trident
                        float yaw = e.Yaw * Mathf.Deg2Rad, pitch = e.Pitch * Mathf.Deg2Rad;
                        var d = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
                        if (e.Kind == 1)
                        {
                            Array.Copy(e.Uv, 0, arrowSide, 0, 4);
                            Array.Copy(e.Uv, 4, arrowBack, 0, 4);
                            haveArrowUv = true;
                        }
                        var at = new Vector3(e.X, e.Y, e.Z);
                        if (PinnedArrow(e.Id, ref at, ref d))
                        {
                            Arrow(at, d, e.Uv, e.Kind == 3);
                        }
                        break;
                    case 2:  // item: Minecraft's item model (the sprite, 1/16 thick) turning about the vertical
                        {
                            float spin = e.Yaw * Mathf.Deg2Rad, k = e.Scale;
                            var c = new Vector3(e.X, e.Y, e.Z);
                            var ax = new Vector3(Mathf.Cos(spin), 0, Mathf.Sin(spin)) * k;
                            var az = new Vector3(-Mathf.Sin(spin), 0, Mathf.Cos(spin)) * k;
                            var ay = Vector3.up * k;
                            foreach (ItemFace f in ItemModel(e.Uv))
                            {
                                Vector3 P(Vector3 l) => c + ax * l.x + ay * l.y + az * l.z;
                                Quad(P(f.A), P(f.B), P(f.C), P(f.D), f.Uv, 0, dSolid, f.Shade);
                            }
                            break;
                        }
                    case 4:  // dropped block: a small spinning cube
                        {
                            float sz = e.Scale;
                            Box(new Vector3(e.X - sz * 0.5f, e.Y - sz * 0.5f, e.Z - sz * 0.5f), new Vector3(sz, sz, sz), e.Yaw * Mathf.Deg2Rad, e.Uv, e.Tint, dSolid);
                            break;
                        }
                    case 5:  // mining cracks over a block
                        Box(new Vector3(e.X, e.Y, e.Z), new Vector3(e.Ext[0], e.Ext[1], e.Ext[2]), 0f, new[] { e.Uv[0], e.Uv[1], e.Uv[2], e.Uv[3], e.Uv[0], e.Uv[1], e.Uv[2], e.Uv[3], e.Uv[0], e.Uv[1], e.Uv[2], e.Uv[3] }, 0, dCrack);
                        break;
                }
            }
            ForgetUnseenPins();
            dynamicMesh.Clear();
            dynamicMesh.SetVertices(dv);
            dynamicMesh.SetUVs(0, duv);
            dynamicMesh.SetColors(dc);
            dynamicMesh.subMeshCount = 2;
            dynamicMesh.SetTriangles(dSolid, 0);
            dynamicMesh.SetTriangles(dCrack, 1);
            dynamicMesh.RecalculateNormals();
            dynamicMesh.RecalculateBounds();
            dynamicGo.SetActive(dv.Count > 0);
            Outline(hasSelection, selMin, selMax);
        }

        private static void EnsureDynamic()
        {
            if (dynamicGo != null)
            {
                return;
            }
            dynamicMesh = new Mesh { name = "Minecraft entities", indexFormat = IndexFormat.UInt32 };
            dynamicMesh.MarkDynamic();
            dynamicGo = new GameObject("Minecraft entities");
            dynamicGo.transform.SetParent(Root, false);
            dynamicGo.AddComponent<MeshFilter>().sharedMesh = dynamicMesh;
            var dr = dynamicGo.AddComponent<MeshRenderer>();
            dr.sharedMaterials = new[] { cutout, translucent };
            renderers.Add(dr);
            outlineMesh = new Mesh { name = "Minecraft outline" };
            outlineGo = new GameObject("Minecraft outline");
            outlineGo.transform.SetParent(Root, false);
            outlineGo.AddComponent<MeshFilter>().sharedMesh = outlineMesh;
            outlineGo.AddComponent<MeshRenderer>().sharedMaterial = lines;
        }

        // ---- dropped items as Minecraft's item models ------------------------------------------------

        private struct ItemFace
        {
            public Vector3 A, B, C, D;  // model space: the sprite spans -0.5..0.5 in x and y
            public float[] Uv;
            public byte Shade;
        }

        private static byte[] atlasPixels;  // a CPU copy of the atlas, to see which texels are solid
        private static readonly Dictionary<(int, int, int, int), List<ItemFace>> itemModels = new Dictionary<(int, int, int, int), List<ItemFace>>();

        // Minecraft's generated item model: the sprite on both faces 1/16 apart, and around every
        // solid texel bordering a clear one a side face in that texel's colour.
        private static List<ItemFace> ItemModel(float[] uv)
        {
            int aw = atlas.width, ah = atlas.height;
            int x0 = Mathf.RoundToInt(Mathf.Min(uv[0], uv[2]) * aw), x1 = Mathf.RoundToInt(Mathf.Max(uv[0], uv[2]) * aw);
            int y0 = Mathf.RoundToInt(Mathf.Min(uv[1], uv[3]) * ah), y1 = Mathf.RoundToInt(Mathf.Max(uv[1], uv[3]) * ah);
            var key = (x0, y0, x1, y1);
            if (itemModels.TryGetValue(key, out List<ItemFace> faces))
            {
                return faces;
            }
            faces = new List<ItemFace>();
            const float t = 0.5f / 16f;
            float[] all = { uv[0], uv[1], uv[2], uv[3] };
            faces.Add(new ItemFace { A = new Vector3(-0.5f, 0.5f, t), B = new Vector3(0.5f, 0.5f, t), C = new Vector3(0.5f, -0.5f, t), D = new Vector3(-0.5f, -0.5f, t), Uv = all, Shade = 255 });
            faces.Add(new ItemFace { A = new Vector3(-0.5f, 0.5f, -t), B = new Vector3(0.5f, 0.5f, -t), C = new Vector3(0.5f, -0.5f, -t), D = new Vector3(-0.5f, -0.5f, -t), Uv = all, Shade = 255 });
            int w = x1 - x0, h = y1 - y0;
            if (atlasPixels != null && w > 0 && h > 0 && w <= 64 && h <= 64)
            {
                bool Solid(int px, int py) => px >= 0 && py >= 0 && px < w && py < h && atlasPixels[((y0 + py) * aw + x0 + px) * 4 + 3] >= 128;
                for (int py = 0; py < h; py++)
                {
                    for (int px = 0; px < w; px++)
                    {
                        if (!Solid(px, py))
                        {
                            continue;
                        }
                        // One texel's colour (its centre), so neighbours don't bleed in.
                        float u = (x0 + px + 0.5f) / aw, v = (y0 + py + 0.5f) / ah;
                        float[] texel = { u, v, u, v };
                        float l = (float)px / w - 0.5f, r = (float)(px + 1) / w - 0.5f;
                        float top = 0.5f - (float)py / h, bottom = 0.5f - (float)(py + 1) / h;
                        if (!Solid(px - 1, py))
                        {
                            faces.Add(new ItemFace { A = new Vector3(l, top, -t), B = new Vector3(l, top, t), C = new Vector3(l, bottom, t), D = new Vector3(l, bottom, -t), Uv = texel, Shade = 153 });
                        }
                        if (!Solid(px + 1, py))
                        {
                            faces.Add(new ItemFace { A = new Vector3(r, top, t), B = new Vector3(r, top, -t), C = new Vector3(r, bottom, -t), D = new Vector3(r, bottom, t), Uv = texel, Shade = 153 });
                        }
                        if (!Solid(px, py - 1))
                        {
                            faces.Add(new ItemFace { A = new Vector3(l, top, -t), B = new Vector3(r, top, -t), C = new Vector3(r, top, t), D = new Vector3(l, top, t), Uv = texel, Shade = 230 });
                        }
                        if (!Solid(px, py + 1))
                        {
                            faces.Add(new ItemFace { A = new Vector3(l, bottom, t), B = new Vector3(r, bottom, t), C = new Vector3(r, bottom, -t), D = new Vector3(l, bottom, -t), Uv = texel, Shade = 128 });
                        }
                    }
                }
            }
            itemModels[key] = faces;
            return faces;
        }

        // Minecraft-space corners (absolute) -> a textured quad, drawn from both sides by the material.
        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float[] uv, int uvOffset, List<int> target, byte shade, uint tint = 0)
        {
            int i0 = dv.Count;
            dv.Add(Mc(a));
            dv.Add(Mc(b));
            dv.Add(Mc(c));
            dv.Add(Mc(d));
            float u0 = uv[uvOffset], v0 = uv[uvOffset + 1], u1 = uv[uvOffset + 2], v1 = uv[uvOffset + 3];
            duv.Add(new Vector2(u0, v0));
            duv.Add(new Vector2(u1, v0));
            duv.Add(new Vector2(u1, v1));
            duv.Add(new Vector2(u0, v1));
            Color32 col = tint != 0
                ? new Color32((byte)((tint & 0xFF) * shade / 255), (byte)(((tint >> 8) & 0xFF) * shade / 255), (byte)(((tint >> 16) & 0xFF) * shade / 255), 255)
                : new Color32(shade, shade, shade, 255);
            for (int k = 0; k < 4; k++)
            {
                dc.Add(col);
            }
            target.Add(i0);
            target.Add(i0 + 2);
            target.Add(i0 + 1);
            target.Add(i0);
            target.Add(i0 + 3);
            target.Add(i0 + 2);
        }

        private static Vector3 Mc(Vector3 p) => Coords.ToUnity(p.x, p.y, p.z);

        // An axis-aligned box turned by yaw about its vertical centre line. uv: side, top, bottom rects.
        private static void Box(Vector3 min, Vector3 size, float yaw, float[] uv, uint topTint, List<int> target)
        {
            float cx = min.x + size.x * 0.5f, cz = min.z + size.z * 0.5f;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            Vector3 Corner(int i)
            {
                float lx = ((i & 1) != 0 ? 0.5f : -0.5f) * size.x, lz = ((i & 4) != 0 ? 0.5f : -0.5f) * size.z;
                return new Vector3(cx + lx * c - lz * s, min.y + ((i & 2) != 0 ? size.y : 0f), cz + lx * s + lz * c);
            }
            int[,] faces = { { 6, 7, 5, 4 }, { 3, 2, 0, 1 }, { 7, 3, 1, 5 }, { 2, 6, 4, 0 }, { 2, 3, 7, 6 }, { 4, 5, 1, 0 } };
            byte[] shades = { 204, 204, 153, 153, 255, 128 };
            for (int f = 0; f < 6; f++)
            {
                int uvOffset = f == 4 ? 4 : f == 5 ? 8 : 0;
                Quad(Corner(faces[f, 0]), Corner(faces[f, 1]), Corner(faces[f, 2]), Corner(faces[f, 3]), uv, uvOffset, target, shades[f], f == 4 ? topTint : 0);
            }
        }

        // Minecraft's ArrowModel: two fins along the flight direction d and a back plate (1/16 block
        // units, scaled 0.9); tridents use their item icon on the same crossed fins.
        private static void Arrow(Vector3 p, Vector3 d, float[] uv, bool trident)
        {
            Vector3 side = new Vector3(d.z, 0, -d.x);
            side = side.sqrMagnitude < 1e-6f ? Vector3.right : side.normalized;
            Vector3 up = Vector3.Cross(side, d);
            const float r = 0.70710678f;
            Vector3 f0 = (up + side) * r, f1 = (up - side) * r;
            if (!trident)
            {
                float k = 0.9f / 16f * ArrowScale;
                foreach (Vector3 q in new[] { f0, f1 })
                {
                    Quad(p + d * (-12 * k) - q * (2 * k), p + d * (4 * k) - q * (2 * k), p + d * (4 * k) + q * (2 * k), p + d * (-12 * k) + q * (2 * k), uv, 0, dSolid, 255);
                }
                Vector3 back = p + d * (-11 * k);
                Quad(back - f0 * (2 * k) - f1 * (2 * k), back + f0 * (2 * k) - f1 * (2 * k), back + f0 * (2 * k) + f1 * (2 * k), back - f0 * (2 * k) + f1 * (2 * k), uv, 4, dSolid, 255);
            }
            else
            {
                const float h = 0.9f;
                foreach (Vector3 q in new[] { f0, f1 })
                {
                    Quad(p + q * h, p + d * h, p - q * h, p - d * h, uv, 0, dSolid, 255);
                }
            }
        }

        private static readonly List<Vector3> ov = new List<Vector3>();
        private static readonly List<Color32> oc = new List<Color32>();
        private static readonly List<int> oi = new List<int>();

        private static void Outline(bool has, Vector3 lo, Vector3 hi)
        {
            has &= lines != null;
            outlineGo.SetActive(has);
            if (!has)
            {
                return;
            }
            const float g = 0.002f;
            lo -= Vector3.one * g;
            hi += Vector3.one * g;
            ov.Clear();
            oc.Clear();
            oi.Clear();
            for (int i = 0; i < 8; i++)
            {
                ov.Add(Mc(new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z)));
                oc.Add(new Color32(0, 0, 0, 255));
            }
            int[] edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
            oi.AddRange(edges);
            outlineMesh.Clear();
            outlineMesh.SetVertices(ov);
            outlineMesh.SetColors(oc);
            outlineMesh.SetIndices(oi, MeshTopology.Lines, 0);
            outlineMesh.RecalculateBounds();
        }

        // ---- arrows stuck in ULTRAKILL's moving geometry ------------------------------------------
        //
        // Minecraft keeps a stuck arrow where it hit; ULTRAKILL's doors and platforms move on. An arrow
        // that has stopped is pinned to the ULTRAKILL object it's in and drawn moving with it, for as
        // long as Minecraft keeps it there (when Minecraft drops it, it's drawn falling as usual).

        private sealed class Pin
        {
            public Vector3 McPos;
            public float Still;
            public bool Tried;
            public Transform Target;
            public Vector3 LocalPos, LocalDir;
            public int Seen;
        }

        private static readonly Dictionary<uint, Pin> pins = new Dictionary<uint, Pin>();
        private static readonly List<uint> unpinned = new List<uint>();
        private static int pinFrame;
        private const int GeometryMask = (1 << 6) | (1 << 7) | (1 << 8) | (1 << 24) | (1 << 26);

        // p: the arrow's position in Minecraft; d: its direction, in Minecraft's axes. Both are replaced
        // by where the object it's pinned to has taken it. False: that object is switched off (a door
        // that vanishes), and the arrow with it.
        private static bool PinnedArrow(uint id, ref Vector3 p, ref Vector3 d)
        {
            if (!pins.TryGetValue(id, out Pin pin))
            {
                pins[id] = pin = new Pin { McPos = p };
            }
            pin.Seen = pinFrame;
            if ((p - pin.McPos).sqrMagnitude > 1e-6f)
            {
                // Moving (flying, or Minecraft dropped it): not pinned.
                pin.McPos = p;
                pin.Still = 0f;
                pin.Tried = false;
                pin.Target = null;
                return true;
            }
            pin.Still += Time.deltaTime;
            if (!pin.Tried && pin.Still > 0.2f)
            {
                pin.Tried = true;
                Vector3 at = Mc(p), dir = Coords.DirToUnity(d.x, d.y, d.z).normalized;
                float u = Coords.U;
                Collider hit = null;
                float nearest = float.MaxValue;
                foreach (RaycastHit h in Physics.RaycastAll(at - dir * u, dir, 2f * u, GeometryMask, QueryTriggerInteraction.Ignore))
                {
                    if (!IsOurs(h.collider) && h.distance < nearest)
                    {
                        nearest = h.distance;
                        hit = h.collider;
                    }
                }
                if (hit == null)
                {
                    foreach (Collider c in Physics.OverlapSphere(at, 0.3f * u, GeometryMask, QueryTriggerInteraction.Ignore))
                    {
                        if (!IsOurs(c))
                        {
                            hit = c;
                            break;
                        }
                    }
                }
                if (hit != null)
                {
                    pin.Target = hit.transform;
                    pin.LocalPos = hit.transform.InverseTransformPoint(at);
                    pin.LocalDir = hit.transform.InverseTransformDirection(dir);
                }
            }
            if (pin.Target == null)
            {
                return true;
            }
            if (!pin.Target.gameObject.activeInHierarchy)
            {
                return false;
            }
            Vector3 now = pin.Target.TransformPoint(pin.LocalPos);
            Vector3 nowDir = pin.Target.TransformDirection(pin.LocalDir);
            Coords.ToMc(now, out double x, out double y, out double z);
            p = new Vector3((float)x, (float)y, (float)z);
            d = new Vector3(nowDir.x, nowDir.y, -nowDir.z);
            return true;
        }

        // Arrows Minecraft no longer shows (picked up, gone) are forgotten.
        private static void ForgetUnseenPins()
        {
            unpinned.Clear();
            foreach (var kv in pins)
            {
                if (kv.Value.Seen != pinFrame)
                {
                    unpinned.Add(kv.Key);
                }
            }
            foreach (uint id in unpinned)
            {
                pins.Remove(id);
            }
            pinFrame++;
        }

        // ---- arrows stuck in ULTRAKILL enemies ------------------------------------------------------

        // Minecraft removes arrows that hit a creature; this pins one to the enemy's nearest body part so
        // it follows the animation. hit: Minecraft coords; yaw/pitch: flight direction (Minecraft degrees).
        public static void StickArrow(EnemyIdentifier eid, Vector3 hitMc, float yaw, float pitch)
        {
            if (!haveArrowUv || cutout == null || eid == null)
            {
                return;
            }
            float y = yaw * Mathf.Deg2Rad, p = pitch * Mathf.Deg2Rad;
            Vector3 dirMc = new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
            Vector3 dir = Coords.DirToUnity(dirMc.x, dirMc.y, dirMc.z).normalized;
            Vector3 hit = Mc(hitMc);
            Transform bone = eid.transform;
            float best = float.MaxValue;
            // Its hurtboxes (layers 10, 11) if it has any: not what it holds (a Stray's energy ball,
            // which it then throws, arrow and all).
            Collider[] cols = eid.GetComponentsInChildren<Collider>();
            bool anyHurtbox = Array.Exists(cols, c => c != null && !c.isTrigger && c.enabled && (c.gameObject.layer == 10 || c.gameObject.layer == 11));
            foreach (Collider c in cols)
            {
                if (c == null || c.isTrigger || !c.enabled || (anyHurtbox && c.gameObject.layer != 10 && c.gameObject.layer != 11))
                {
                    continue;
                }
                Vector3 closest = c is MeshCollider mc && !mc.convex ? c.bounds.ClosestPoint(hit) : c.ClosestPoint(hit);
                float dist = (closest - hit).sqrMagnitude;
                if (dist < best)
                {
                    best = dist;
                    bone = c.transform;
                    hit = closest;
                }
            }
            var go = new GameObject("Minecraft arrow");
            go.transform.SetPositionAndRotation(hit - dir * (4f / 16f * 0.9f * ArrowScale * Coords.U * 0.5f), Quaternion.LookRotation(dir));
            go.transform.localScale = Vector3.one;
            go.transform.SetParent(bone, true);
            go.AddComponent<MeshFilter>().sharedMesh = ArrowMesh();
            var ar = go.AddComponent<MeshRenderer>();
            ar.sharedMaterial = cutout;
            renderers.Add(ar);
            stuckArrows.AddLast((go, eid));
            while (stuckArrows.Count > 200)
            {
                if (stuckArrows.First.Value.Arrow != null)
                {
                    UnityEngine.Object.Destroy(stuckArrows.First.Value.Arrow);
                }
                stuckArrows.RemoveFirst();
            }
        }

        // A dead enemy's arrows stay in its body, moving with it, for as long as the body is drawn
        // where they are. Once it isn't (blown to gibs, the body part gone, a corpse swapped out),
        // they drop out instead of hanging in the air.
        private static float corpseCheck;

        private static bool OnVisibleBody(GameObject arrow, EnemyIdentifier eid)
        {
            Transform part = arrow.transform.parent;
            if (eid == null || part == null || !part.gameObject.activeInHierarchy)
            {
                return false;
            }
            Vector3 at = arrow.transform.position;
            float margin = 0.25f * Coords.U;
            foreach (Renderer r in eid.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.name == "Minecraft arrow"
                    || !(r is SkinnedMeshRenderer || r is MeshRenderer))
                {
                    continue;
                }
                Bounds b = r.bounds;
                b.Expand(margin);
                if (b.Contains(at))
                {
                    return true;
                }
            }
            return false;
        }

        private static void PruneStuckArrows()
        {
            corpseCheck -= Time.deltaTime;
            bool checkCorpses = corpseCheck <= 0f;
            if (checkCorpses)
            {
                corpseCheck = 0.2f;
            }
            var node = stuckArrows.First;
            while (node != null)
            {
                var next = node.Next;
                (GameObject arrow, EnemyIdentifier eid) = node.Value;
                bool gone = arrow == null || eid == null || (eid.dead && checkCorpses && !OnVisibleBody(arrow, eid));
                if (gone)
                {
                    // Off the body: the arrow drops out.
                    if (arrow != null)
                    {
                        arrow.transform.SetParent(null, true);
                        // (A minute on the floor, as Minecraft's arrows.)
                        fallingArrows.Add(new FallingArrow { Arrow = arrow, Life = 60f });
                    }
                    stuckArrows.Remove(node);
                }
                node = next;
            }
            float dt = Time.deltaTime;
            for (int i = fallingArrows.Count - 1; i >= 0; i--)
            {
                FallingArrow f = fallingArrows[i];
                f.Life -= dt;
                if (f.Arrow == null || f.Life <= 0f)
                {
                    if (f.Arrow != null)
                    {
                        renderers.Remove(f.Arrow.GetComponent<MeshRenderer>());
                        UnityEngine.Object.Destroy(f.Arrow);
                    }
                    fallingArrows.RemoveAt(i);
                    continue;
                }
                if (!f.Landed)
                {
                    f.Velocity += Physics.gravity * dt;
                    Vector3 step = f.Velocity * dt;
                    Vector3 from = f.Arrow.transform.position;
                    if (Physics.Raycast(from, step.normalized, out RaycastHit hit, step.magnitude + 0.05f, (1 << 8) | (1 << 24), QueryTriggerInteraction.Ignore))
                    {
                        f.Arrow.transform.position = hit.point;
                        f.Landed = true;
                    }
                    else
                    {
                        f.Arrow.transform.position = from + step;
                    }
                }
                fallingArrows[i] = f;
            }
        }

        // Arrows on a corpse or on the floor are Killcraft's drawing only (Minecraft removed them when
        // they hit the enemy's stand-in): each is also a tiny stand-in named "Killcraft arrow" (Combat),
        // which Killcraft's data pack turns into an arrow for a player who walks up to it, then pokes so
        // Killcraft hears it was picked up.
        public const string LooseArrowName = "Killcraft arrow";
        private const uint FirstLooseId = 0x40000000;
        private static readonly Dictionary<GameObject, uint> looseIds = new Dictionary<GameObject, uint>();
        private static readonly List<GameObject> looseGone = new List<GameObject>();
        private static uint nextLooseId = FirstLooseId;

        public static bool IsLooseArrow(uint id) => id >= FirstLooseId;

        public static void LooseArrows(List<(uint Id, Vector3 Pos)> into, Vector3 near, float range, int max)
        {
            into.Clear();
            foreach (var (arrow, eid) in stuckArrows)
            {
                if (arrow != null && eid != null && eid.dead)
                {
                    AddLoose(arrow, into, near, range);
                }
            }
            foreach (FallingArrow f in fallingArrows)
            {
                if (f.Arrow != null && f.Landed)
                {
                    AddLoose(f.Arrow, into, near, range);
                }
            }
            if (into.Count > max)
            {
                into.Sort((a, b) => (a.Pos - near).sqrMagnitude.CompareTo((b.Pos - near).sqrMagnitude));
                into.RemoveRange(max, into.Count - max);
            }
            looseGone.Clear();
            foreach (GameObject key in looseIds.Keys)
            {
                if (key == null)
                {
                    looseGone.Add(key);
                }
            }
            foreach (GameObject key in looseGone)
            {
                looseIds.Remove(key);
            }
        }

        private static void AddLoose(GameObject arrow, List<(uint Id, Vector3 Pos)> into, Vector3 near, float range)
        {
            Vector3 at = arrow.transform.position;
            if ((at - near).sqrMagnitude > range * range)
            {
                return;
            }
            if (!looseIds.TryGetValue(arrow, out uint id))
            {
                looseIds[arrow] = id = nextLooseId++;
            }
            into.Add((id, at));
        }

        public static void PickedUp(uint id)
        {
            foreach (var kv in looseIds)
            {
                if (kv.Value == id && kv.Key != null)
                {
                    renderers.Remove(kv.Key.GetComponent<MeshRenderer>());
                    UnityEngine.Object.Destroy(kv.Key);
                    break;
                }
            }
        }

        private struct FallingArrow
        {
            public GameObject Arrow;
            public Vector3 Velocity;
            public float Life;
            public bool Landed;
        }

        private static readonly List<FallingArrow> fallingArrows = new List<FallingArrow>();

        // The arrow model in local space, pointing along +Z, in ULTRAKILL units.
        private static Mesh ArrowMesh()
        {
            if (arrowMesh != null)
            {
                return arrowMesh;
            }
            float k = 0.9f / 16f * ArrowScale * Coords.U;
            Vector3 d = Vector3.forward, f0 = new Vector3(1, 1, 0).normalized, f1 = new Vector3(-1, 1, 0).normalized;
            var v = new List<Vector3>();
            var t = new List<Vector2>();
            var idx = new List<int>();
            void Q(Vector3 a, Vector3 b, Vector3 c, Vector3 e, float[] uv)
            {
                int i0 = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(e);
                t.Add(new Vector2(uv[0], uv[1])); t.Add(new Vector2(uv[2], uv[1]));
                t.Add(new Vector2(uv[2], uv[3])); t.Add(new Vector2(uv[0], uv[3]));
                idx.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
            }
            foreach (Vector3 q in new[] { f0, f1 })
            {
                Q(d * (-12 * k) - q * (2 * k), d * (4 * k) - q * (2 * k), d * (4 * k) + q * (2 * k), d * (-12 * k) + q * (2 * k), arrowSide);
            }
            Vector3 back = d * (-11 * k);
            Q(back - f0 * (2 * k) - f1 * (2 * k), back + f0 * (2 * k) - f1 * (2 * k), back + f0 * (2 * k) + f1 * (2 * k), back - f0 * (2 * k) + f1 * (2 * k), arrowBack);
            arrowMesh = new Mesh { name = "Minecraft arrow" };
            arrowMesh.SetVertices(v);
            arrowMesh.SetUVs(0, t);
            var white = new Color32[v.Count];
            for (int i = 0; i < white.Length; i++)
            {
                white[i] = new Color32(255, 255, 255, 255);
            }
            arrowMesh.colors32 = white;
            arrowMesh.SetTriangles(idx, 0);
            arrowMesh.RecalculateNormals();
            arrowMesh.RecalculateBounds();
            return arrowMesh;
        }
    }
}
