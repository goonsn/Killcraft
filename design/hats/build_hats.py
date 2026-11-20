# Builds the hats in hats.blend (run inside Blender, after blender_kit.py). Each hat is rebuilt from
# scratch by its function; tweak one, run it again, render (preview), export.
import bpy, bmesh, math, random
from mathutils import Vector, Matrix, noise


def root_of(c, name):
    e = bpy.data.objects.new(name, None)
    c.objects.link(e)
    e.empty_display_size = 0.03
    return e


def to_mesh_obj(o, name=None):
    """A curve (or anything) as a plain mesh object in its place."""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg))
    n = bpy.data.objects.new(name or o.name, me)
    for c in o.users_collection:
        c.objects.link(n)
    n.matrix_world = o.matrix_world.copy()
    n.parent = o.parent
    n.matrix_parent_inverse = o.matrix_parent_inverse.copy()
    bpy.data.objects.remove(o, do_unlink=True)
    return n


def smooth_all(o, angle=40):
    for p in o.data.polygons:
        p.use_smooth = True
    try:
        o.data.set_sharp_from_angle(angle=math.radians(angle))
    except Exception:
        pass


def subsurf(o, levels=1):
    m = o.modifiers.new("Subdivision", 'SUBSURF')
    m.levels = m.render_levels = levels
    return m


def bevel(o, width, segs=2):
    m = o.modifiers.new("Bevel", 'BEVEL')
    m.width = width
    m.segments = segs
    m.limit_method = 'ANGLE'
    return m


# ---- halo ----

def halo():
    c = hat_collection("HALO")
    gold = mat("halo gold", (1.0, 0.80, 0.28), glow=True)
    o = lathe("Halo", c, [(0.084, -0.003), (0.0865, 0.0), (0.084, 0.003), (0.067, 0.0035), (0.064, 0.0), (0.067, -0.0035), (0.084, -0.003)],
              [0] * 6, segs=40)
    o.data.materials.append(gold)
    place(o, (0, 0.03, 0.072), (-12, 0, 0))
    o["anim"] = 1


def conform(o, upto, offset=0.0015, reach=0.014):
    """Marks o's foot (its vertices up to upto above its origin, fading; 0: all of it) to be draped on
    V1's head (drape, once the hat is in place)."""
    o["drape"], o["drape_offset"], o["drape_reach"] = upto, offset, reach


def drape(o, root, bvh):
    """Each vertex of o moved up or down by the head's height under it (offset above it) against the
    hat's own foot (its root's plane): the foot lies on the head, its shape kept (a ruff stays a ruff, a
    base its thickness). Up as far as need be; down only reach (not down the head's sides)."""
    upto, off, reach = o["drape"], o["drape_offset"], o["drape_reach"]
    mw = o.matrix_world
    inv = mw.inverted()
    n = (root.matrix_world.to_3x3() @ Vector((0, 0, 1))).normalized()
    r0 = root.matrix_world.translation
    for v in o.data.vertices:
        w = 1.0 if upto <= 0 else min(1.0, max(0.0, 1.0 - v.co.z / upto))
        if w <= 0:
            continue
        p = mw @ v.co
        t = head_top(bvh, p.x, p.y)
        if t is None:
            continue
        plane = r0.z - (n.x * (p.x - r0.x) + n.y * (p.y - r0.y)) / n.z
        d = max(t + off - plane, -reach)
        v.co = inv @ (p + Vector((0, 0, d * w)))
    o.data.update()


def on_head(x, y):
    """The top of V1's head at (x, y)."""
    return head_top(head_bvh(), x, y)


# ---- horns ----

def _spline(ctrl, n):
    """n points along a smooth curve through ctrl (Catmull-Rom)."""
    P = [Vector(p) for p in ctrl]
    out = []
    segs = len(P) - 1
    for k in range(n):
        t = k / (n - 1) * segs
        i = min(int(t), segs - 1)
        u = t - i
        p0, p1, p2, p3 = P[max(i - 1, 0)], P[i], P[i + 1], P[min(i + 2, segs)]
        out.append(0.5 * ((2 * p1) + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u * u * u))
    return out


def horns():
    c = hat_collection("HORNS")
    base = mat("horn base", (0.42, 0.05, 0.04), rough=0.55)
    mid = mat("horn middle", (0.26, 0.03, 0.03), rough=0.45)
    tip = mat("horn tip", (0.07, 0.02, 0.02), rough=0.3)
    Y = -0.030
    for side in (-1, 1):
        x0 = 0.044
        top = on_head(side * x0, Y)
        # Out of the head itself (its root down in it), up, a little out, the tips curling in.
        ctrl = [(x0 - 0.004, Y + 0.002, top - 0.014), (x0, Y, top), (x0 + 0.012, Y - 0.001, top + 0.020), (x0 + 0.022, Y - 0.001, top + 0.044),
                (x0 + 0.024, Y + 0.001, top + 0.070), (x0 + 0.016, Y + 0.004, top + 0.092), (x0 + 0.002, Y + 0.008, top + 0.104)]
        ctrl = [(side * x, y, z) for x, y, z in ctrl]
        n = 56
        pts = _spline(ctrl, n)
        radii = []
        for i in range(n):
            f = i / (n - 1)
            r = (1.0 - f) ** 0.8
            # Rings round it, plain to its tip.
            r *= 1.0 + 0.055 * max(0.0, math.cos(i * 2 * math.pi / 4.5)) * max(0.0, 1.0 - f / 0.6)
            radii.append(max(r, 0.015))
        cu = bpy.data.curves.new(f"Horn {'L' if side < 0 else 'R'}", 'CURVE')
        cu.dimensions = '3D'
        cu.bevel_depth = 0.019
        cu.bevel_resolution = 3
        cu.use_fill_caps = True
        sp = cu.splines.new('POLY')
        sp.points.add(n - 1)
        for i, p in enumerate(pts):
            sp.points[i].co = (*p, 1.0)
            sp.points[i].radius = radii[i]
        sp.use_smooth = True
        o = to_mesh_obj(obj(cu.name, cu, c))
        for m in (base, mid, tip):
            o.data.materials.append(m)
        bm = bmesh.new()
        bm.from_mesh(o.data)
        start = Vector(ctrl[1])
        length = sum((pts[i + 1] - pts[i]).length for i in range(n - 1))
        for f in bm.faces:
            q = f.calc_center_median()
            # (How far up it: by the nearest point along it.)
            k = min(range(n), key=lambda i: (pts[i] - q).length_squared) / (n - 1)
            f.material_index = 0 if k < 0.40 else 1 if k < 0.74 else 2
            f.smooth = True
        bm.to_mesh(o.data)
        bm.free()


# ---- crown ----

def crown():
    c = hat_collection("CROWN")
    gold = mat("crown gold", (1.0, 0.72, 0.15), rough=0.25, metal=1.0)
    velvet = mat("crown velvet", (0.55, 0.03, 0.07), rough=0.9)
    fur = mat("crown fur", (0.95, 0.93, 0.88), rough=1.0)
    spot = mat("crown fur spot", (0.08, 0.08, 0.09), rough=1.0)
    ruby = mat("ruby", (0.95, 0.06, 0.12), rough=0.1)
    sapphire = mat("sapphire", (0.12, 0.30, 1.0), rough=0.1)
    emerald = mat("emerald", (0.08, 0.80, 0.30), rough=0.1)
    pearl = mat("pearl", (1.0, 0.96, 0.90), rough=0.2)
    root = root_of(c, "Crown")
    R, H, n = 0.058, 0.030, 8
    # The band: a ring whose top rises into eight points (a wave), thick, smoothed.
    segs = n * 8
    bm = bmesh.new()
    bot, top = [], []
    for j in range(segs):
        a = 2 * math.pi * j / segs
        f = (j % 8) / 8.0
        tri = 1.0 - abs(f * 2 - 1.0)        # 0 at the points' middles .. 1 between
        peak = (1.0 - tri) ** 1.6
        flare = 1.0 + 0.12 * peak
        bot.append(bm.verts.new((R * math.sin(a), -R * math.cos(a), 0.0)))
        top.append(bm.verts.new((R * flare * math.sin(a), -R * flare * math.cos(a), H * 0.50 + H * 1.05 * peak)))
    for j in range(segs):
        k = (j + 1) % segs
        bm.faces.new((bot[j], bot[k], top[k], top[j]))
    me = bpy.data.meshes.new("Crown band")
    bm.to_mesh(me)
    bm.free()
    band = obj("Crown band", me, c, [gold])
    sol = band.modifiers.new("Solidify", 'SOLIDIFY')
    sol.thickness = 0.0045
    sol.offset = 1.0
    smooth_all(band, 60)
    # A fur rim round its foot (white, with black spots), and its velvet cap.
    rim = lathe("Crown rim", c, [(R - 0.002, -0.004), (R + 0.006, -0.006), (R + 0.0105, 0.0), (R + 0.006, 0.007), (R - 0.002, 0.006)],
                [0] * 4, segs=32, mat_of=lambda i, j: 1 if j % 6 == 0 and i in (1, 2) else 0)
    rim.data.materials.append(fur)
    rim.data.materials.append(spot)
    subsurf(rim, 1)
    cap = lathe("Crown cap", c, [(R - 0.003, 0.004), (R - 0.006, 0.022), (R - 0.020, 0.034), (R - 0.040, 0.039), (0.0, 0.040)], [0] * 4, segs=32)
    cap.data.materials.append(velvet)
    subsurf(cap, 1)
    # Pearls on the points, gems round the band.
    for i in range(n):
        # (On each point's tip: the band's top there, half the thickness in.)
        a = 2 * math.pi * i / n
        rr = R * 1.12 + 0.00225
        bpy.ops.mesh.primitive_uv_sphere_add(segments=10, ring_count=6, radius=0.0050, location=(rr * math.sin(a), -rr * math.cos(a), H * 1.55 + 0.0035))
        p = bpy.context.object
        p.name = "Pearl"
        for cc in p.users_collection:
            cc.objects.unlink(p)
        c.objects.link(p)
        p.data.materials.append(pearl)
        smooth_all(p, 90)
    gems = [ruby, emerald, sapphire, emerald, ruby, emerald, sapphire, emerald]
    for i in range(n):
        # (Under each point.)
        a = 2 * math.pi * i / n
        rr = R + 0.0055
        big = i == 0
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=0.0068 if big else 0.0052, location=(rr * math.sin(a), -rr * math.cos(a), H * 0.42))
        g = bpy.context.object
        g.name = "Gem"
        for cc in g.users_collection:
            cc.objects.unlink(g)
        c.objects.link(g)
        g.rotation_euler = (0, 0, a)
        g.scale = (1.0, 0.45, 1.25 if big else 1.0)
        g.data.materials.append(gems[i])
    for o in c.objects:
        if o is not root:
            o.parent = root
    place(root, (0.004, 0.012, 0.0), (8, 6, 0))


# ---- party hat ----

def party_hat():
    c = hat_collection("PARTY HAT")
    col = mat("party colour", (0.25, 0.65, 1.0), player=True, rough=0.7)
    white = mat("party white", (1.0, 1.0, 1.0), rough=0.7)
    gold = mat("party gold", (1.0, 0.78, 0.18), rough=0.3, metal=0.8)
    pom = mat("party pom", (1.0, 1.0, 1.0), rough=1.0)
    root = root_of(c, "Party hat")
    # Wide enough to cover the top of the head (longer front to back, as it is), upright.
    R, H, rows, segs, LONG = 0.064, 0.150, 20, 36, 1.28
    prof = [(R * (1 - i / rows), H * i / rows) for i in range(rows)] + [(0.0, H)]
    cone = lathe("Party cone", c, [(0.0, 0.0)] + prof, [0], segs=segs,
                 mat_of=lambda i, j: (0 if ((j / segs) * 3 + (i / rows) * 2.2) % 1.0 < 0.55 else 1) if i > 0 else 1)
    cone.data.materials.append(col)
    cone.data.materials.append(white)
    for v in cone.data.vertices:
        # (Its foot oval, rounding to a circle up it.)
        v.co.y *= 1.0 + (LONG - 1.0) * max(0.0, 1.0 - v.co.z / (H * 0.6))
    smooth_all(cone, 50)
    ruff = lathe("Party ruff", c, [(R - 0.003, -0.003), (R + 0.0045, -0.0045), (R + 0.0075, 0.0015), (R + 0.004, 0.0075), (R - 0.004, 0.0075)],
                 [0], segs=segs, mat_of=lambda i, j: j % 2)
    for v in ruff.data.vertices:
        v.co.y *= LONG
    ruff.data.materials.append(gold)
    ruff.data.materials.append(white)
    subsurf(ruff, 1)
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=0.014, location=(0, 0, 0))
    pp = bpy.context.object
    pp.name = "Party pom"
    for cc in pp.users_collection:
        cc.objects.unlink(pp)
    c.objects.link(pp)
    for v in pp.data.vertices:
        n_ = v.co.normalized()
        v.co *= 1.0 + 0.22 * noise.noise(n_ * 4.0) + 0.10 * noise.noise(n_ * 11.0)
    pp.data.materials.append(pom)
    smooth_all(pp, 180)
    x, y = 0.0, -0.012
    # (Leaning forward a little with the head's top, which slopes down to the face: +X turns toward -Y.)
    place(root, (x, y, on_head(x, y)), (7, 0, 0))
    for o in c.objects:
        if o is not root:
            o.parent = root
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    e = cone.evaluated_get(dg)
    tipv = max((v.co for v in e.data.vertices), key=lambda q: q.z)
    pp.location = tipv + Vector((0, 0, 0.005))


# ---- antenna ----

def antenna():
    c = hat_collection("ANTENNA")
    metal = mat("antenna metal", (0.55, 0.56, 0.60), rough=0.3, metal=1.0)
    dark = mat("antenna dark", (0.12, 0.12, 0.14), rough=0.5, metal=0.6)
    bulb = mat("antenna bulb", (1.0, 0.14, 0.08), glow=True, blink=True)
    root = root_of(c, "Antenna")
    base = lathe("Antenna base", c, [(0.0, 0.0), (0.026, 0.0), (0.026, 0.004), (0.021, 0.008), (0.011, 0.0095), (0.011, 0.014), (0.0, 0.014)],
                 [0, 0, 1, 1, 1, 1], segs=20)
    base.data.materials.append(dark)
    base.data.materials.append(metal)
    bevel(base, 0.0012, 2)
    smooth_all(base, 35)
    for i in range(4):
        a = 2 * math.pi * (i + 0.5) / 4
        bpy.ops.mesh.primitive_cylinder_add(vertices=6, radius=0.0028, depth=0.002, location=(0.019 * math.sin(a), -0.019 * math.cos(a), 0.0055))
        b = bpy.context.object
        b.name = "Antenna bolt"
        for cc in b.users_collection:
            cc.objects.unlink(b)
        c.objects.link(b)
        b.data.materials.append(metal)
    # The moving part: a spring, a wire out of it (bent back a little), the light on it.
    turns, ch, cr, steps = 5, 0.032, 0.0072, 90
    helix = [(cr * math.sin(2 * math.pi * turns * i / steps), -cr * math.cos(2 * math.pi * turns * i / steps), 0.014 + ch * i / steps) for i in range(steps + 1)]
    spring = to_mesh_obj(curve_tube("Antenna spring", c, helix, 0.0017, res=2, bevel_res=1))
    top = 0.014 + ch
    wire_pts = [(0, 0, top - 0.004), (0, 0.0005, top + 0.015), (0, 0.003, top + 0.035), (0, 0.008, top + 0.052), (0, 0.013, top + 0.062)]
    wire = to_mesh_obj(curve_tube("Antenna wire", c, wire_pts, 0.0024, res=6, bevel_res=2))
    tip = Vector(wire_pts[-1])
    bpy.ops.mesh.primitive_uv_sphere_add(segments=16, ring_count=10, radius=0.0135, location=tip + Vector((0, 0.002, 0.011)))
    bl = bpy.context.object
    bl.name = "Antenna light"
    for cc in bl.users_collection:
        cc.objects.unlink(bl)
    c.objects.link(bl)
    socket = lathe("Antenna socket", c, [(0.0, -0.002), (0.0055, 0.0), (0.0065, 0.004), (0.0, 0.0045)], [0], segs=12)
    socket.location = tip + Vector((0, 0.0005, -0.001))
    for o, m in ((spring, metal), (wire, metal), (bl, bulb), (socket, metal)):
        o.data.materials.clear()
        o.data.materials.append(m)
        smooth_all(o, 60)
    # (One object, its origin at the spring's foot: it sways from there.)
    bpy.ops.object.select_all(action='DESELECT')
    for o in (spring, wire, bl, socket):
        o.select_set(True)
    bpy.context.view_layer.objects.active = spring
    bpy.ops.object.join()
    rod = bpy.context.object
    rod.name = "Antenna rod"
    bpy.context.scene.cursor.location = (0, 0, 0.014)
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    bpy.context.scene.cursor.location = (0, 0, 0)
    rod["anim"] = 2
    for o in c.objects:
        if o is not root:
            o.parent = root
    place(root, (0.0, 0.045, 0.0), (6, 0, 0))


# ---- top hat ----

def top_hat():
    c = hat_collection("TOP HAT")
    felt = mat("top hat felt", (0.075, 0.075, 0.09), rough=0.85)
    band = mat("top hat band", (0.60, 0.03, 0.06), rough=0.5)
    buckle = mat("top hat buckle", (1.0, 0.78, 0.25), rough=0.25, metal=1.0)
    root = root_of(c, "Top hat")
    Rb, Rc, Rt, H = 0.098, 0.057, 0.063, 0.118
    prof = [(Rc - 0.004, 0.0), (Rb - 0.010, -0.002), (Rb, 0.002), (Rb + 0.0015, 0.007), (Rb - 0.003, 0.0065), (Rb - 0.014, 0.0035),
            (Rc, 0.0045), (Rc - 0.0005, 0.018), (Rc - 0.0015, 0.032), (Rc - 0.002, 0.050), (Rt - 0.004, 0.090), (Rt - 0.001, H - 0.004),
            (Rt - 0.004, H), (Rt - 0.020, H + 0.0015), (0.0, H + 0.002)]
    # Band: the crown's foot (steps 6..7).
    hat = lathe("Top hat", c, prof, [0] * 6 + [1, 1] + [0] * 6, segs=40)
    hat.data.materials.append(felt)
    hat.data.materials.append(band)
    # The brim curled up at its sides (and down a touch front and back).
    for v in hat.data.vertices:
        r = math.hypot(v.co.x, v.co.y)
        if r > Rc + 0.001 and v.co.z < 0.012:
            f = (r - Rc) / (Rb - Rc)
            side = (v.co.x / r) ** 2
            v.co.z += 0.026 * f * f * side - 0.004 * f * (1 - side)
    hat.scale = (1.0, 1.10, 1.0)
    smooth_all(hat, 50)
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(Rc + 0.0015, 0, 0.018))
    b = bpy.context.object
    b.name = "Top hat buckle"
    for cc in b.users_collection:
        cc.objects.unlink(b)
    c.objects.link(b)
    b.scale = (0.003, 0.020, 0.017)
    b.data.materials.append(buckle)
    bevel(b, 0.25, 2)
    for o in c.objects:
        if o is not root:
            o.parent = root
    place(root, (0.0, 0.010, 0.0), (8, -5, -10))


# ---- traffic cone ----

def traffic_cone():
    c = hat_collection("TRAFFIC CONE")
    orange = mat("cone orange", (1.0, 0.33, 0.03), rough=0.55)
    deep = mat("cone base", (0.75, 0.21, 0.02), rough=0.65)
    white = mat("cone white", (0.97, 0.97, 0.95), rough=0.3)
    hole = mat("cone inside", (0.18, 0.05, 0.01), rough=0.9)
    root = root_of(c, "Traffic cone")
    # The base: a rubber square as wide as the top of the head, lying on it.
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=18, y_segments=22, size=0.5)
    me = bpy.data.meshes.new("Cone base")
    bm.to_mesh(me)
    bm.free()
    base = obj("Cone base", me, c, [deep])
    span = max(abs(v.co.x) for v in base.data.vertices)
    for v in base.data.vertices:
        # (Its corners cut round.)
        x, y = v.co.x / span * 0.068, v.co.y / span * 0.088
        r = 0.018
        ex, ey = max(0.0, abs(x) - (0.068 - r)), max(0.0, abs(y) - (0.088 - r))
        if ex > 0 and ey > 0:
            d = math.hypot(ex, ey)
            if d > r:
                x -= math.copysign(ex - ex * r / d, x)
                y -= math.copysign(ey - ey * r / d, y)
        v.co = (x, y, 0.0)
    so = base.modifiers.new("Solidify", 'SOLIDIFY')
    so.thickness, so.offset = 0.008, 1.0
    bv = base.modifiers.new("Bevel", 'BEVEL')
    bv.width, bv.segments, bv.limit_method = 0.002, 2, 'ANGLE'
    smooth_all(base, 45)
    R0, R1, H = 0.052, 0.012, 0.150
    ys = [0.006, 0.040, 0.060, 0.086, 0.100, H]

    def r_at(z):
        return R0 + (R1 - R0) * z / H

    prof = [(r_at(z), z) for z in ys] + [(R1 - 0.003, H + 0.001), (R1 - 0.003, H - 0.02), (0.0, H - 0.02)]
    cone = lathe("Cone", c, prof, [0, 1, 0, 1, 0, 0, 2, 2], segs=32)
    for m in (orange, white, hole):
        cone.data.materials.append(m)
    # (Its foot on the base, wherever that lies.)
    smooth_all(cone, 40)
    x, y = 0.0, -0.012
    place(root, (x, y, on_head(x, y)), (7, 0, 0))
    for o in c.objects:
        if o is not root:
            o.parent = root


BUILD = {"HALO": halo, "HORNS": horns, "CROWN": crown, "PARTY HAT": party_hat, "ANTENNA": antenna,
         "TOP HAT": top_hat, "TRAFFIC CONE": traffic_cone}

# Sat on the head (their lowest just clear of it); the halo floats, the horns go into it.
# (The party hat, the traffic cone and the horns' band lie on it of themselves: conform.)
SETTLE = {"CROWN": "Crown", "ANTENNA": "Antenna", "TOP HAT": "Top hat"}


def seat_flat(root, rx, ry, lean=1.0):
    """Stands root's hat (rigid) on the top of the head over an oval rx by ry round it: tipped to the
    slope of the head there (lean: how much of it), then just clear of the head everywhere."""
    bvh = head_bvh()
    x0, y0 = root.location.x, root.location.y
    pts = []
    for i in range(-6, 7):
        for j in range(-6, 7):
            x, y = x0 + rx * i / 6, y0 + ry * j / 6
            if (i / 6) ** 2 + (j / 6) ** 2 > 1:
                continue
            t = head_top(bvh, x, y)
            if t is not None:
                pts.append((x - x0, y - y0, t))
    # (A plane through them: z = a + b x + c y, least squares.)
    n = len(pts)
    sx = sum(p[0] for p in pts); sy = sum(p[1] for p in pts); sz = sum(p[2] for p in pts)
    sxx = sum(p[0] * p[0] for p in pts); syy = sum(p[1] * p[1] for p in pts); sxy = sum(p[0] * p[1] for p in pts)
    sxz = sum(p[0] * p[2] for p in pts); syz = sum(p[1] * p[2] for p in pts)
    M = Matrix(((n, sx, sy), (sx, sxx, sxy), (sy, sxy, syy)))
    a_, b_, c_ = M.inverted() @ Vector((sz, sxz, syz))
    normal = Vector((-b_ * lean, -c_ * lean, 1.0)).normalized()
    root.rotation_euler = Vector((0, 0, 1)).rotation_difference(normal).to_euler()
    root.location.z = a_
    bpy.context.view_layer.update()
    objs = [o for o in root.children_recursive if o.type == 'MESH']
    return settle(root, objs)


# Rigid, flat on the top of the head: (root, oval round it).
FLAT = {"PARTY HAT": ("Party hat", 0.066, 0.084), "TRAFFIC CONE": ("Traffic cone", 0.068, 0.088)}


def build(names=None):
    out = {}
    for n in names or BUILD:
        BUILD[n]()
        bpy.context.view_layer.update()
        draped = [o for o in bpy.data.collections["Hat: " + n].objects if "drape" in o]
        if draped:
            bvh = head_bvh()
            # Up onto the head's bumps (most of the foot over the head: on it, not in its groove).
            root = draped[0].parent
            n_ = (root.matrix_world.to_3x3() @ Vector((0, 0, 1))).normalized()
            r0 = root.matrix_world.translation
            ds = []
            for o in draped:
                if o["drape"] > 0:
                    continue
                for v in o.data.vertices:
                    p_ = o.matrix_world @ v.co
                    t = head_top(bvh, p_.x, p_.y)
                    if t is not None:
                        ds.append(t - (r0.z - (n_.x * (p_.x - r0.x) + n_.y * (p_.y - r0.y)) / n_.z))
            if ds:
                ds.sort()
                root.location.z += ds[int(len(ds) * 0.85)]
                bpy.context.view_layer.update()
            for o in draped:
                drape(o, o.parent, bvh)
            bpy.context.view_layer.update()
        if n in FLAT:
            out[n] = round(seat_flat(bpy.data.objects[FLAT[n][0]], FLAT[n][1], FLAT[n][2]), 4)
        if n in SETTLE:
            c = bpy.data.collections["Hat: " + n]
            root = bpy.data.objects[SETTLE[n]]
            objs = [o for o in c.objects if o.type == 'MESH']
            out[n] = round(settle(root, objs), 4)
    return out
