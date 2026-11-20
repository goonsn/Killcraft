using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Killcraft
{
    // What each V1 wears on its head (Plugin.Hat, said with the player's profile): a halo, horns, a crown,
    // a party hat (in the player's colour), an antenna, a top hat, a traffic cone. Modelled on V1's own
    // head (design/hats/hats.py, baked into hats.bin in this dll), lit as the level is (a copy of V1's own
    // material, flat coloured), the halo and the antenna's light glowing; the halo floats, the antenna
    // sways. On the others' V1s; on this player's own only in third person (in first person it'd sit in
    // front of the camera).
    internal static class Hats
    {
        public static readonly string[] Names = { "NONE", "HALO", "HORNS", "CROWN", "PARTY HAT", "ANTENNA", "TOP HAT", "TRAFFIC CONE" };

        private sealed class Worn
        {
            public int Kind = -1;
            public GameObject Go;
            public Color Color;
            public Transform Head;
            public Material Blink;
            public Color BlinkColor;
            public ShadowCastingMode Shadows = (ShadowCastingMode)(-1);
            public readonly List<Moving> Moving = new List<Moving>();
        }

        // A piece that moves: floats (the halo) or sways on a spring (the antenna's rod).
        private sealed class Moving
        {
            public Transform T;
            public Vector3 Pivot;
            public byte Anim;
            public float Phase;
            public Vector3 LastAt, LastVel;
            public bool Started;
            public Vector2 Tilt, TiltVel;
        }

        private static readonly Dictionary<int, Worn> worn = new Dictionary<int, Worn>();
        private static readonly List<int> gone = new List<int>();
        private static readonly HashSet<int> seen = new HashSet<int>();
        private static readonly Worn mine = new Worn();
        private static Shader glowShader;

        public static void LateFrame(bool inLevel)
        {
            seen.Clear();
            if (inLevel)
            {
                foreach (var (id, body, yaw) in RemotePlayers.V1Bodies())
                {
                    if (!worn.TryGetValue(id, out Worn w))
                    {
                        worn[id] = w = new Worn();
                    }
                    seen.Add(id);
                    Profiles.Info i = Profiles.Of(id);
                    Wear(w, i.Hat, i.Color, body.Head, body.Root, yaw, body.Height, body.HeadTop);
                }
            }
            gone.Clear();
            foreach (int id in worn.Keys)
            {
                if (!seen.Contains(id))
                {
                    gone.Add(id);
                }
            }
            foreach (int id in gone)
            {
                Take(worn[id]);
                worn.Remove(id);
            }
            // This player's own: on V1's own body, drawn as that is - its layer, shown or not. In first
            // person that's a layer only mirrors (and its shadow) see; in third person, the camera's.
            PlayerAnimations own = inLevel ? MonoSingleton<PlayerAnimations>.Instance : null;
            Transform head = V1Body.HeadOfAnimations(own);
            if (own != null && head != null && Plugin.Hat.Value > 0)
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                float yaw = nm != null ? nm.transform.eulerAngles.y : 0f;
                Wear(mine, Plugin.Hat.Value, Profiles.Mine.Color, head, own.gameObject, yaw, 3.5f, 0.35f);
                AsBody(mine, own);
            }
            else
            {
                Take(mine);
            }
        }

        private static Renderer ownBody;
        private static PlayerAnimations ownBodyOf;

        private static void AsBody(Worn w, PlayerAnimations own)
        {
            if (w.Go == null)
            {
                return;
            }
            if (ownBodyOf != own || ownBody == null)
            {
                ownBodyOf = own;
                ownBody = null;
                foreach (SkinnedMeshRenderer r in own.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r.name == "v1_mdl")
                    {
                        ownBody = r;
                        break;
                    }
                }
            }
            if (ownBody == null)
            {
                return;
            }
            bool shown = ownBody.enabled && ownBody.gameObject.activeInHierarchy && !ownBody.forceRenderingOff;
            if (w.Go.activeSelf != shown)
            {
                w.Go.SetActive(shown);
            }
            int layer = ownBody.gameObject.layer;
            ShadowCastingMode shadows = ownBody.shadowCastingMode;
            if (w.Go.layer != layer || w.Shadows != shadows)
            {
                w.Shadows = shadows;
                foreach (Transform t in w.Go.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = layer;
                }
                foreach (Renderer r in w.Go.GetComponentsInChildren<Renderer>(true))
                {
                    r.shadowCastingMode = shadows;
                }
            }
        }

        private static void Take(Worn w)
        {
            if (w.Go != null)
            {
                Object.Destroy(w.Go);
            }
            if (w.Blink != null)
            {
                Object.Destroy(w.Blink);
            }
            w.Go = null;
            w.Blink = null;
            w.Kind = -1;
            w.Head = null;
            w.Shadows = (ShadowCastingMode)(-1);
            w.Moving.Clear();
        }

        // On the head's bone (turning and tilting with it), as the fit says.
        private static void Wear(Worn w, int kind, Color color, Transform head, GameObject body, float yaw, float height, float headTop)
        {
            if (kind <= 0 || kind >= Names.Length || head == null)
            {
                Take(w);
                return;
            }
            if (w.Go == null || w.Kind != kind || w.Head != head || w.Color != color)
            {
                Take(w);
                w.Kind = kind;
                w.Color = color;
                w.Head = head;
                w.Go = Make(kind, color, Basis(body), w);
                if (w.Go == null)
                {
                    return;
                }
                Fit f = FitOf(head, body, yaw, height, headTop);
                Transform t = w.Go.transform;
                t.SetParent(head, false);
                t.localPosition = f.Pos;
                t.localRotation = f.Rot;
                t.localScale = Vector3.one * (f.Size / modelWidth);
            }
            if (w.Go == null)
            {
                return;
            }
            if (w.Blink != null)
            {
                bool lit = Mathf.Repeat(Time.time, 1.2f) < 0.7f;
                w.Blink.color = lit ? w.BlinkColor : w.BlinkColor * 0.25f;
            }
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.05f);
            foreach (Moving m in w.Moving)
            {
                Move(m, dt);
            }
        }

        private static void Move(Moving m, float dt)
        {
            if (m.T == null)
            {
                return;
            }
            if (m.Anim == 1)
            {
                // (Floating: up and down a little.)
                m.T.localPosition = m.Pivot + Vector3.up * (Mathf.Sin(Time.time * 2.2f + m.Phase) * 0.005f);
                return;
            }
            if (m.Anim != 2)
            {
                return;
            }
            // Swaying: a spring, pushed the other way to how its foot speeds up (its tip lags).
            Transform parent = m.T.parent;
            Vector3 at = parent.TransformPoint(m.Pivot);
            Vector3 vel = (at - m.LastAt) / dt;
            if (!m.Started || vel.sqrMagnitude > 400f * 400f)
            {
                // (Just put on, or moved at once: a teleport, not a push.)
                m.Started = true;
                m.LastAt = at;
                m.LastVel = Vector3.zero;
                return;
            }
            Vector3 acc = parent.InverseTransformDirection((vel - m.LastVel) / dt);
            m.LastAt = at;
            m.LastVel = vel;
            acc = Vector3.ClampMagnitude(acc, 800f);
            // (A dash, some 300 a second faster each second: about 10 degrees.)
            const float stiff = 140f, damp = 6f, push = 5f;
            var drive = new Vector2(-acc.z, acc.x) * push;
            m.TiltVel += (-stiff * m.Tilt - damp * m.TiltVel + drive) * dt;
            m.Tilt += m.TiltVel * dt;
            m.Tilt = Vector2.ClampMagnitude(m.Tilt, 30f);
            m.T.localRotation = Quaternion.Euler(m.Tilt.x, 0f, m.Tilt.y);
        }

        // ---- fitting a hat to V1's head ----

        // In the head bone's own space: the top of the head (the middle of its bounds' top: the frame the
        // hats are modelled in), which way is up and forward on it, and how wide the head is.
        private struct Fit
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public float Size;
        }

        private static readonly Dictionary<Transform, Fit> fits = new Dictionary<Transform, Fit>();
        private static bool fitTold;

        private static Fit FitOf(Transform head, GameObject body, float yaw, float height, float headTop)
        {
            if (fits.TryGetValue(head, out Fit f))
            {
                return f;
            }
            if (fits.Count > 32)
            {
                fits.Clear();
            }
            Vector3 facing = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            if (!FromMesh(head, body, facing, out f, out string how))
            {
                // (V1's mesh not readable: on the head's bone as it is now, upright, its size by V1's.)
                Vector3 at = head.position + Vector3.up * headTop;
                float k = Mathf.Max(Mathf.Abs(head.lossyScale.x), 0.0001f);
                f = new Fit
                {
                    Pos = head.InverseTransformPoint(at),
                    Rot = Quaternion.Inverse(head.rotation) * Quaternion.Euler(0f, yaw, 0f),
                    // (V1's head is about a tenth as wide as it's tall.)
                    Size = height * 0.107f / k,
                };
                how += "; placed by V1's size instead";
            }
            if (!fitTold)
            {
                fitTold = true;
                Plugin.Log.LogInfo($"hats: fitted to V1's head ({how})");
            }
            fits[head] = f;
            return f;
        }

        // From V1's mesh: the vertices that move with its head, as they are at rest (its bind pose), in
        // the head bone's space; up is the way from the body's middle to them, forward the way V1 faces.
        private static bool FromMesh(Transform head, GameObject body, Vector3 facing, out Fit fit, out string how)
        {
            fit = default;
            how = "no skinned mesh with the head";
            if (body == null)
            {
                return false;
            }
            try
            {
                foreach (SkinnedMeshRenderer smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh mesh = smr.sharedMesh;
                    Transform[] bones = smr.bones;
                    if (mesh == null || bones == null)
                    {
                        continue;
                    }
                    int hi = System.Array.IndexOf(bones, head);
                    if (hi < 0)
                    {
                        continue;
                    }
                    Matrix4x4[] binds = mesh.bindposes;
                    if (binds == null || hi >= binds.Length)
                    {
                        how = "no bind poses";
                        continue;
                    }
                    if (!ReadMesh(mesh, out Vector3[] pos, out int[] bone))
                    {
                        how = "its mesh couldn't be read";
                        continue;
                    }
                    // (The head's bone and those under it: all of the head.)
                    var onHead = new bool[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        onHead[b] = bones[b] != null && bones[b].IsChildOf(head);
                    }
                    Matrix4x4 toHead = binds[hi];
                    var pts = new List<Vector3>();
                    for (int v = 0; v < pos.Length; v++)
                    {
                        int b = bone[v];
                        if (b >= 0 && b < onHead.Length && onHead[b])
                        {
                            pts.Add(toHead.MultiplyPoint3x4(pos[v]));
                        }
                    }
                    if (pts.Count < 8)
                    {
                        how = $"only {pts.Count} vertices on the head";
                        continue;
                    }
                    Vector3 centre = Vector3.zero;
                    foreach (Vector3 p in pts)
                    {
                        centre += p;
                    }
                    centre /= pts.Count;
                    // Up: from the whole mesh's middle to the head's, along the nearest of the mesh's axes.
                    Vector3 headInMesh = toHead.inverse.MultiplyPoint3x4(centre);
                    Vector3 d = headInMesh - mesh.bounds.center;
                    Vector3 upMesh = Mathf.Abs(d.x) >= Mathf.Abs(d.y) && Mathf.Abs(d.x) >= Mathf.Abs(d.z) ? new Vector3(Mathf.Sign(d.x), 0f, 0f)
                        : Mathf.Abs(d.y) >= Mathf.Abs(d.z) ? new Vector3(0f, Mathf.Sign(d.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(d.z));
                    Vector3 up = toHead.MultiplyVector(upMesh).normalized;
                    // Forward: of the mesh's other axes, the one most the way V1 faces now.
                    Vector3 fwd = Vector3.zero;
                    float best = float.MinValue;
                    foreach (Vector3 axis in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
                    {
                        if (Mathf.Abs(Vector3.Dot(axis, upMesh)) > 0.5f)
                        {
                            continue;
                        }
                        Vector3 local = toHead.MultiplyVector(axis).normalized;
                        float dot = Vector3.Dot(head.TransformDirection(local), facing);
                        if (dot > best)
                        {
                            best = dot;
                            fwd = local;
                        }
                    }
                    fwd = Vector3.ProjectOnPlane(fwd, up).normalized;
                    if (fwd.sqrMagnitude < 0.5f)
                    {
                        how = "no forward for the head";
                        continue;
                    }
                    Vector3 right = Vector3.Cross(up, fwd).normalized;
                    float top = float.MinValue, minR = float.MaxValue, maxR = float.MinValue, minF = float.MaxValue, maxF = float.MinValue;
                    foreach (Vector3 p in pts)
                    {
                        top = Mathf.Max(top, Vector3.Dot(p, up));
                        float r = Vector3.Dot(p, right), q = Vector3.Dot(p, fwd);
                        minR = Mathf.Min(minR, r);
                        maxR = Mathf.Max(maxR, r);
                        minF = Mathf.Min(minF, q);
                        maxF = Mathf.Max(maxF, q);
                    }
                    float width = maxR - minR;
                    Vector3 mid = right * ((minR + maxR) / 2f) + fwd * ((minF + maxF) / 2f);
                    fit = new Fit
                    {
                        Pos = mid + up * top,
                        Rot = Quaternion.LookRotation(fwd, up),
                        Size = width,
                    };
                    how = $"{pts.Count} vertices, {(mesh.isReadable ? "read" : "read from the GPU")}, {width:0.###} wide and {maxF - minF:0.###} long in the bone's space";
                    return true;
                }
            }
            catch (System.Exception e)
            {
                how = "failed: " + e.Message;
            }
            return false;
        }

        // A mesh's vertices and each one's main bone: from its own copy, or (not readable) from the GPU.
        private static bool ReadMesh(Mesh mesh, out Vector3[] pos, out int[] bone)
        {
            pos = null;
            bone = null;
            int n = mesh.vertexCount;
            if (mesh.isReadable)
            {
                pos = mesh.vertices;
                BoneWeight[] bw = mesh.boneWeights;
                if (pos == null || bw == null || bw.Length != pos.Length)
                {
                    return false;
                }
                bone = new int[n];
                for (int i = 0; i < n; i++)
                {
                    bone[i] = bw[i].weight0 >= bw[i].weight1 ? bw[i].boneIndex0 : bw[i].boneIndex1;
                }
                return true;
            }
            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || !mesh.HasVertexAttribute(VertexAttribute.BlendIndices)
                || mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
            {
                return false;
            }
            byte[] ps = Stream(mesh, VertexAttribute.Position, out int pOff, out int pStride);
            byte[] bs = Stream(mesh, VertexAttribute.BlendIndices, out int bOff, out int bStride);
            if (ps == null || bs == null)
            {
                return false;
            }
            VertexAttributeFormat fmt = mesh.GetVertexAttributeFormat(VertexAttribute.BlendIndices);
            pos = new Vector3[n];
            bone = new int[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * pStride + pOff;
                pos[i] = new Vector3(System.BitConverter.ToSingle(ps, o), System.BitConverter.ToSingle(ps, o + 4), System.BitConverter.ToSingle(ps, o + 8));
                int q = i * bStride + bOff;
                switch (fmt)
                {
                    case VertexAttributeFormat.UInt8:
                    case VertexAttributeFormat.SInt8:
                        bone[i] = bs[q];
                        break;
                    case VertexAttributeFormat.UInt16:
                    case VertexAttributeFormat.SInt16:
                        bone[i] = System.BitConverter.ToUInt16(bs, q);
                        break;
                    case VertexAttributeFormat.UInt32:
                    case VertexAttributeFormat.SInt32:
                        bone[i] = System.BitConverter.ToInt32(bs, q);
                        break;
                    case VertexAttributeFormat.Float32:
                        bone[i] = (int)System.BitConverter.ToSingle(bs, q);
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static byte[] Stream(Mesh mesh, VertexAttribute a, out int offset, out int stride)
        {
            int s = mesh.GetVertexAttributeStream(a);
            offset = mesh.GetVertexAttributeOffset(a);
            stride = mesh.GetVertexBufferStride(s);
            var raw = new byte[mesh.vertexCount * stride];
            using (GraphicsBuffer vb = mesh.GetVertexBuffer(s))
            {
                if (vb == null)
                {
                    return null;
                }
                vb.GetData(raw);
            }
            return raw;
        }

        // V1's own lit material (its body's), to copy in flat colours.
        private static Material Basis(GameObject body)
        {
            if (body == null)
            {
                return null;
            }
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer && r.sharedMaterial != null)
                {
                    return r.sharedMaterial;
                }
            }
            return null;
        }

        private static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        private static Material Mat(Color c, bool glow, Material basis)
        {
            string key = (glow ? "g" : "l") + ColorUtility.ToHtmlStringRGB(c) + (basis != null ? basis.GetInstanceID().ToString() : "");
            if (mats.TryGetValue(key, out Material m) && m != null)
            {
                return m;
            }
            m = NewMat(c, glow, basis);
            mats[key] = m;
            return m;
        }

        private static Material NewMat(Color c, bool glow, Material basis)
        {
            if (!glow && basis != null && basis.HasProperty("_Color"))
            {
                var m = new Material(basis) { name = "Killcraft hat" };
                if (m.HasProperty("_MainTex"))
                {
                    m.mainTexture = Texture2D.whiteTexture;
                }
                m.color = c;
                return m;
            }
            if (glowShader == null)
            {
                glowShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            }
            return new Material(glowShader) { name = "Killcraft hat (glow)", color = glow ? c : c * 0.8f };
        }

        // ---- the hats' models (hats.bin) ----

        private const byte Glow = 1, PlayerColour = 2, Blinks = 4;

        private sealed class Model
        {
            public string Name;
            public Piece[] Pieces;
        }

        private sealed class Piece
        {
            public Vector3 Pivot;
            public byte Anim;
            public Mesh Mesh;
            public Color32[] Colors;
            public byte[] Flags;
        }

        private static Dictionary<string, Model> models;
        private static float modelWidth = 0.171f;

        private static Model ModelOf(int kind)
        {
            if (models == null)
            {
                models = new Dictionary<string, Model>();
                try
                {
                    Load();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"hats: couldn't read their models ({e.Message})");
                }
            }
            return models.TryGetValue(Names[kind], out Model m) ? m : null;
        }

        private static void Load()
        {
            using (Stream s = typeof(Hats).Assembly.GetManifestResourceStream("Killcraft.hats.bin"))
            {
                if (s == null)
                {
                    throw new IOException("not in the dll");
                }
                var r = new BinaryReader(s);
                if (new string(r.ReadChars(4)) != "KCHT" || r.ReadInt32() != 1)
                {
                    throw new IOException("not a version this reads");
                }
                modelWidth = Mathf.Max(r.ReadSingle(), 0.001f);
                int hats = r.ReadInt32();
                for (int h = 0; h < hats; h++)
                {
                    var m = new Model { Name = System.Text.Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte())) };
                    m.Pieces = new Piece[r.ReadInt32()];
                    for (int p = 0; p < m.Pieces.Length; p++)
                    {
                        var piece = new Piece
                        {
                            Pivot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                            Anim = r.ReadByte(),
                        };
                        int n = r.ReadInt32();
                        var v = new Vector3[n];
                        var nr = new Vector3[n];
                        for (int i = 0; i < n; i++)
                        {
                            v[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            nr[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        }
                        int subs = r.ReadInt32();
                        piece.Colors = new Color32[subs];
                        piece.Flags = new byte[subs];
                        var tris = new int[subs][];
                        for (int k = 0; k < subs; k++)
                        {
                            piece.Colors[k] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);
                            piece.Flags[k] = r.ReadByte();
                            tris[k] = new int[r.ReadInt32()];
                            for (int i = 0; i < tris[k].Length; i++)
                            {
                                tris[k][i] = r.ReadUInt16();
                            }
                        }
                        var mesh = new Mesh { name = "Killcraft hat: " + m.Name };
                        mesh.vertices = v;
                        mesh.normals = nr;
                        mesh.subMeshCount = subs;
                        for (int k = 0; k < subs; k++)
                        {
                            mesh.SetTriangles(tris[k], k);
                        }
                        mesh.RecalculateBounds();
                        piece.Mesh = mesh;
                        m.Pieces[p] = piece;
                    }
                    models[m.Name] = m;
                }
                Plugin.Log.LogInfo($"hats: {models.Count} models read");
            }
        }

        private static GameObject Make(int kind, Color player, Material basis, Worn w)
        {
            Model model = ModelOf(kind);
            if (model == null)
            {
                return null;
            }
            var root = new GameObject("Killcraft hat: " + Names[kind]);
            foreach (Piece p in model.Pieces)
            {
                var go = new GameObject("Piece");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = p.Pivot;
                go.AddComponent<MeshFilter>().sharedMesh = p.Mesh;
                var r = go.AddComponent<MeshRenderer>();
                var ms = new Material[p.Colors.Length];
                for (int k = 0; k < ms.Length; k++)
                {
                    bool glow = (p.Flags[k] & Glow) != 0;
                    Color c = (p.Flags[k] & PlayerColour) != 0 ? player : (Color)p.Colors[k];
                    if ((p.Flags[k] & Blinks) != 0)
                    {
                        // (Its own: it dims and lights.)
                        w.Blink = NewMat(c, glow, basis);
                        w.BlinkColor = c;
                        ms[k] = w.Blink;
                    }
                    else
                    {
                        ms[k] = Mat(c, glow, basis);
                    }
                }
                r.sharedMaterials = ms;
                r.shadowCastingMode = ShadowCastingMode.On;
                if (p.Anim != 0)
                {
                    w.Moving.Add(new Moving { T = go.transform, Pivot = p.Pivot, Anim = p.Anim, Phase = Random.value * 6.28f });
                }
            }
            return root;
        }
    }
}
