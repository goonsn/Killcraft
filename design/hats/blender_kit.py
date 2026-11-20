# Helpers for hats.blend (run inside Blender: exec(open(<this file>).read())).
#
# hats.blend holds V1's head (reference, from the game's files: not shipped) and the hats, one
# collection each ("Hat: NAME"), modelled on it. Blender's axes here: X right, Y back (V1 faces -Y),
# Z up; the origin is the middle of the top of the head's bounds (the frame Hats.cs fits to V1's head
# in the game). Each hat's materials say its colour (their viewport colour) and, as custom properties,
# whether it glows ("glow"), takes the player's colour ("player") or blinks ("blink"); an object with an
# "anim" property moves about its origin in the game (1 floats, 2 sways on a spring).
#
# export() writes hats.bin (read by src/Net/Hats.cs).
import bpy, bmesh, math, struct
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

HERE = r"C:/Users/Issei/Downloads/UltraCraft/plugin/design/hats/"
ORDER = ["HALO", "HORNS", "CROWN", "PARTY HAT", "ANTENNA", "TOP HAT", "TRAFFIC CONE"]
HEAD_WIDTH = 0.1710


def mat(name, rgb, glow=False, player=False, blink=False, rough=0.6, metal=0.0):
    m = bpy.data.materials.get("hat " + name) or bpy.data.materials.new("hat " + name)
    m.diffuse_color = (*rgb, 1.0)
    m["glow"], m["player"], m["blink"] = int(glow), int(player), int(blink)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = metal
        if glow:
            bsdf.inputs["Emission Color"].default_value = (*rgb, 1.0)
            bsdf.inputs["Emission Strength"].default_value = 2.0
    return m


def hat_collection(name, clear=True):
    cname = "Hat: " + name
    c = bpy.data.collections.get(cname)
    if c is None:
        c = bpy.data.collections.new(cname)
        bpy.context.scene.collection.children.link(c)
    elif clear:
        for o in list(c.objects):
            bpy.data.objects.remove(o, do_unlink=True)
    return c


def obj(name, me, coll, mats=()):
    o = bpy.data.objects.new(name, me)
    coll.objects.link(o)
    for m in mats:
        o.data.materials.append(m)
    return o


def lathe(name, coll, profile, mats, segs=24, smooth=True, mat_of=None):
    """A shape turned about Z: profile [(r, z), ...] (r 0 at an end: closed there); mats per profile
    step (indices into the object's materials), or mat_of(step, seg) -> index."""
    bm = bmesh.new()
    rings = []
    for (r, z) in profile:
        if r < 1e-7:
            rings.append([bm.verts.new((0, 0, z))])
        else:
            rings.append([bm.verts.new((r * math.sin(2 * math.pi * j / segs), -r * math.cos(2 * math.pi * j / segs), z)) for j in range(segs)])
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(segs):
            if len(a) == 1 and len(b) == 1:
                continue
            if len(a) == 1:
                vs = [a[0], b[(j + 1) % segs], b[j]]
            elif len(b) == 1:
                vs = [a[j], a[(j + 1) % segs], b[0]]
            else:
                vs = [a[j], a[(j + 1) % segs], b[(j + 1) % segs], b[j]]
            try:
                f = bm.faces.new(vs)
            except ValueError:
                continue
            f.material_index = mat_of(i, j) if mat_of else mats[min(i, len(mats) - 1)]
            f.smooth = smooth
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    return obj(name, me, coll)


def curve_tube(name, coll, points, radius, radii=None, res=3, bevel_res=4, closed=False):
    """A tube along a smooth curve through points, radius thick (radii: each point's scale)."""
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = radius
    cu.bevel_resolution = bevel_res
    cu.resolution_u = res
    cu.use_fill_caps = True
    sp = cu.splines.new('NURBS')
    sp.points.add(len(points) - 1)
    for i, p in enumerate(points):
        sp.points[i].co = (*p, 1.0)
        sp.points[i].radius = radii[i] if radii else 1.0
    sp.use_endpoint_u = True
    sp.order_u = 4 if len(points) >= 4 else len(points)
    sp.use_smooth = True
    return obj(name, cu, coll)


def place(o, loc=(0, 0, 0), rot=(0, 0, 0), scale=None):
    o.location = loc
    o.rotation_euler = [math.radians(a) for a in rot]
    if scale is not None:
        o.scale = scale if hasattr(scale, "__len__") else (scale, scale, scale)


def parent_keep(child, parent):
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()


# ---- the head ----

def head_bvh():
    dg = bpy.context.evaluated_depsgraph_get()
    h = bpy.data.objects["V1 head"]
    bm = bmesh.new()
    bm.from_object(h, dg)
    bm.transform(h.matrix_world)
    t = BVHTree.FromBMesh(bm)
    bm.free()
    return t


def head_top(bvh, x, y):
    hit = bvh.ray_cast(Vector((x, y, 1.0)), Vector((0, 0, -1)))
    return hit[0].z if hit[0] is not None else None


def world_verts(o):
    dg = bpy.context.evaluated_depsgraph_get()
    e = o.evaluated_get(dg)
    me = e.to_mesh()
    vs = [e.matrix_world @ v.co for v in me.vertices]
    e.to_mesh_clear()
    return vs


def clearance(objs, bvh=None):
    """Of the hat's vertices over the head: the least height above it (negative: in it), and the
    share of them more than 4 mm above it (floating)."""
    bvh = bvh or head_bvh()
    least, n, high = 1.0, 0, 0
    for o in objs:
        for v in world_verts(o):
            t = head_top(bvh, v.x, v.y)
            if t is None:
                continue
            n += 1
            least = min(least, v.z - t)
    return least


def settle(root, objs, clear=0.0015):
    """Moves root (everything under it) up or down till the lowest of objs is clear above the head."""
    bpy.context.view_layer.update()
    c = clearance(objs)
    root.location.z += clear - c
    bpy.context.view_layer.update()
    return clear - c


# ---- previews ----

VIEWS = [("front", 0, 8), ("3/4", 35, 18), ("side", 90, 4), ("other side", -90, 4), ("behind, above", 160, 28)]


def preview(names, out_dir, size=360):
    """Renders each hat on the head from each view: out_dir/<hat>_<i>.png."""
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.film_transparent = False
    sc.view_settings.view_transform = 'Standard'
    w = sc.world or bpy.data.worlds.new("World")
    sc.world = w
    w.use_nodes = True
    bg = w.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (0.035, 0.038, 0.045, 1)
    bg.inputs[1].default_value = 1.0
    cam = bpy.data.objects.get("Preview camera")
    if cam is None:
        cam = bpy.data.objects.new("Preview camera", bpy.data.cameras.new("Preview camera"))
        sc.collection.objects.link(cam)
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = 0.40
    cam.data.clip_start = 0.01
    sc.camera = cam
    sun = bpy.data.objects.get("Preview sun")
    if sun is None:
        sun = bpy.data.objects.new("Preview sun", bpy.data.lights.new("Preview sun", 'SUN'))
        sc.collection.objects.link(sun)
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(40), math.radians(15), math.radians(-35))
    hats = [c for c in bpy.data.collections if c.name.startswith("Hat: ")]
    files = []
    for name in names:
        for c in hats:
            c.hide_render = c.name != "Hat: " + name
        for i, (label, yaw, pitch) in enumerate(VIEWS):
            # (yaw 0: in front of V1, at -Y, looking at it.)
            d = Matrix.Rotation(math.radians(yaw), 3, 'Z') @ (Matrix.Rotation(math.radians(-pitch), 3, 'X') @ Vector((0, -1, 0)))
            target = Vector((0, 0, 0.03))
            cam.location = target + d * 1.0
            cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
            sc.render.filepath = f"{out_dir}/{name.replace(' ', '_')}_{i}.png"
            bpy.ops.render.render(write_still=True)
            files.append(sc.render.filepath)
    for c in hats:
        c.hide_render = False
    return files


# ---- hats.bin ----
# "KCHT", version 1, head width, hat count; each hat: its name, its parts; each part: its pivot, how it
# moves, its vertices (position, normal: about its pivot, in the game's frame), its materials (colour,
# flags, triangles). The game's frame (Unity's): x right, y up, z forward = Blender's (X, Z, -Y).

def to_game(v):
    return (v.x, v.z, -v.y)


def export(path=HERE + "hats.bin"):
    dg = bpy.context.evaluated_depsgraph_get()
    out = bytearray(b"KCHT")
    out += struct.pack("<ifi", 1, HEAD_WIDTH, len(ORDER))
    report = {}
    for name in ORDER:
        c = bpy.data.collections["Hat: " + name]
        objs = [o for o in c.all_objects if o.type in ('MESH', 'CURVE') and not o.hide_render]
        # Parts: each animated object its own; the rest together.
        groups = [[o] for o in objs if "anim" in o]
        still = [o for o in objs if "anim" not in o]
        if still:
            groups.insert(0, still)
        nb = name.encode("ascii")
        out += struct.pack("<B", len(nb)) + nb
        out += struct.pack("<i", len(groups))
        nverts = 0
        for g in groups:
            anim = int(g[0].get("anim", 0)) if len(g) == 1 else 0
            pivot = g[0].matrix_world.translation.copy() if anim else Vector((0, 0, 0))
            verts, index, subs = [], {}, {}
            for o in g:
                e = o.evaluated_get(dg)
                me = e.to_mesh()
                me.calc_loop_triangles()
                mw = e.matrix_world
                nm = mw.to_3x3().inverted().transposed()
                mats = [s.material for s in e.material_slots]
                for tri in me.loop_triangles:
                    m = mats[tri.material_index] if tri.material_index < len(mats) else None
                    if m is None:
                        continue
                    rgb = tuple(int(round(max(0, min(1, c_)) * 255)) for c_ in m.diffuse_color[:3])
                    flags = (1 if m.get("glow") else 0) | (2 if m.get("player") else 0) | (4 if m.get("blink") else 0)
                    key = (rgb, flags)
                    idx = subs.setdefault(key, [])
                    # Blender's front faces wind counterclockwise; Unity's (after the axis swap, a
                    # mirror) the other way: the same order serves.
                    for li in tri.loops:
                        vi = me.loops[li].vertex_index
                        p = mw @ me.vertices[vi].co - pivot
                        n = (nm @ Vector(me.corner_normals[li].vector)).normalized()
                        k = (round(p.x, 6), round(p.y, 6), round(p.z, 6), round(n.x, 4), round(n.y, 4), round(n.z, 4))
                        if k not in index:
                            index[k] = len(verts)
                            verts.append((to_game(p), to_game(n)))
                        idx.append(index[k])
                e.to_mesh_clear()
            out += struct.pack("<3fB", *to_game(pivot), anim)
            out += struct.pack("<i", len(verts))
            for p, n in verts:
                out += struct.pack("<6f", *p, *n)
            out += struct.pack("<i", len(subs))
            for (rgb, flags), idx in subs.items():
                out += struct.pack("<4B", *rgb, flags)
                out += struct.pack("<i", len(idx))
                out += struct.pack(f"<{len(idx)}H", *idx)
            nverts += len(verts)
        report[name] = nverts
    open(path, "wb").write(bytes(out))
    return {"bytes": len(out), "verts": report}
