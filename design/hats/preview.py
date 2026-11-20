# Renders the hats on V1's head (preview.png): its head's mesh, taken from the game's files into
# head/ (v1_mdl.json: its vertices, bone weights; v1_tris.json: its triangles; not shipped).
import json, math
import numpy as np
from PIL import Image, ImageDraw

HERE = __file__.replace("\\", "/").rsplit("/", 1)[0]
PLAYER_SAMPLE = (0.25, 0.75, 1.0)


def head():
    d = json.load(open(HERE + "/head/v1_mdl.json"))
    V = np.array(d["verts"]); bi = np.array(d["bi"]); bw = np.array(d["bw"])
    main = bi[np.arange(len(bi)), bw.argmax(1)]
    T = np.array(json.load(open(HERE + "/head/v1_tris.json")))
    h = V[main == 6]
    xmid = (h[:, 0].min() + h[:, 0].max()) / 2
    ymid = (h[:, 1].min() + h[:, 1].max()) / 2
    ztop = h[:, 2].max()
    A = np.c_[V[:, 0] - xmid, V[:, 2] - ztop, -(V[:, 1] - ymid)]
    out = []
    for t in T:
        b = main[t]
        if (b == 6).all():
            out.append((A[t], (0.55, 0.55, 0.6), False, True))
        elif (A[t][:, 1] > -0.42).all() and (np.abs(A[t][:, 0]) < 0.3).all() and not ((b >= 25) & (b <= 48)).any():
            out.append((A[t], (0.3, 0.3, 0.36), False, True))
    return out


def hat_tris(parts):
    out = []
    for part in parts:
        V = np.array(part.v)
        for (rgb, flags), idx in part.tris.items():
            col = PLAYER_SAMPLE if flags & 2 else tuple(c / 255 for c in rgb)
            for k in range(0, len(idx), 3):
                out.append((V[idx[k:k + 3]], col, bool(flags & 1), False))
    return out


def rot(yaw, pitch):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    # (pitch: from above.)
    cp, sp = math.cos(math.radians(-pitch)), math.sin(math.radians(-pitch))
    return np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]]) @ np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])


def render(tris, yaw, pitch, size=300, span=0.36, centre=(0, 0.02, 0)):
    """Orthographic, the camera looking along +z of its view; back faces (Unity's) dropped."""
    R = rot(yaw, pitch)
    img = np.zeros((size, size, 3)) + np.array([0.12, 0.13, 0.15])
    zb = np.full((size, size), np.inf)
    light = np.array([0.45, 0.7, -0.55]); light /= np.linalg.norm(light)
    c = np.array(centre)
    for tri, col, glow, two in tris:
        P = (R @ (np.asarray(tri) - c).T).T
        n = np.cross(P[1] - P[0], P[2] - P[0])
        l = np.linalg.norm(n)
        if l < 1e-14:
            continue
        n /= l
        if n[2] > 0:
            if not two:
                continue
            n = -n
        shade = 1.0 if glow else 0.3 + 0.7 * max(0.0, float(np.dot(n, light)))
        rgb = np.array(col) * shade
        sx = (P[:, 0] / span + 0.5) * size
        sy = (0.5 - P[:, 1] / span) * size
        x0, x1 = int(max(0, math.floor(sx.min()))), int(min(size - 1, math.ceil(sx.max())))
        y0, y1 = int(max(0, math.floor(sy.min()))), int(min(size - 1, math.ceil(sy.max())))
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = (sx[0], sy[0]), (sx[1], sy[1]), (sx[2], sy[2])
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            continue
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        w2 = 1 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        z = w0 * P[0, 2] + w1 * P[1, 2] + w2 * P[2, 2]
        sub = zb[y0:y1 + 1, x0:x1 + 1]
        m = inside & (z < sub)
        sub[m] = z[m]
        img[y0:y1 + 1, x0:x1 + 1][m] = rgb
    return Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))


VIEWS = [("front", 180, 8), ("3/4", 145, 18), ("side", 90, 4), ("other side", 270, 4), ("behind, above", 20, 28)]


def main(hats, path=None, only=None):
    hd = head()
    rows = [(n, m) for n, m in hats if only is None or n in only]
    size = 300
    out = Image.new("RGB", (size * len(VIEWS), (size + 16) * len(rows)), (18, 18, 22))
    d = ImageDraw.Draw(out)
    for r, (name, make) in enumerate(rows):
        tris = hd + hat_tris(make())
        for c, (label, yaw, pitch) in enumerate(VIEWS):
            out.paste(render(tris, yaw, pitch, size), (c * size, r * (size + 16) + 16))
            d.text((c * size + 6, r * (size + 16) + 2), f"{name} - {label}", fill=(235, 235, 235))
    out.save(path or HERE + "/preview.png")
    print("preview:", path or HERE + "/preview.png")
