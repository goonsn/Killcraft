# The launcher's pictures, in layers (it scrolls the background under the rest, and glitches the logo
# in): the approved design (mockup.py) taken apart. Writes to ../app/Art:
#   bg.png      the block diagram, 1920 wide and three screens tall, joined top to bottom (it loops);
#   logo.png    KILLNET, white, with its black outline, on a clear 1920x1080 page where it goes;
#   figure.png  the exploded block, likewise.
# (The frame, the status lines and the buttons are drawn by the launcher.)
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math, os, sys
here = os.path.dirname(os.path.abspath(__file__))
os.chdir(here)
sys.path.insert(0, here)
import bg

W, H = 1920, 1080
TALL = 3 * H
VCR = "fonts/VCR_OSD_Replayed.ttf"
out = os.path.join("..", "app", "Art")
os.makedirs(out, exist_ok=True)

# ---- the background ----
back = Image.new("RGB", (W, TALL), (0, 0, 0))
placed, left = bg.draw(back, ImageFont.truetype(VCR, 17), W, TALL, seed=11, wrap=True)
back.save(os.path.join(out, "bg.png"), optimize=True)
# (Its wires and blocks, for the packets the launcher sends along them: "W x,y x,y ..." and "B x0 y0 x1 y1".)
with open(os.path.join(out, "wires.txt"), "w") as f:
    for w in bg.WIRES:
        f.write("W " + " ".join(f"{int(x)},{int(y)}" for x, y in w) + chr(10))
    for b in bg.BOXES:
        f.write("B " + " ".join(str(int(v)) for v in b) + chr(10))
print(f"bg: {placed} blocks ({left} names unused)")

def layer(mask, x, y, halo, under=None):
    page = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    black = mask.filter(ImageFilter.MaxFilter(halo))
    page.paste((0, 0, 0, 255), (x, y), black)
    if under is not None:
        page.paste((0, 0, 0, 255), (x, y), under)
    page.paste((255, 255, 255, 255), (x, y), mask)
    return page

# ---- the logos: KILLNET and KILLCRAFT, drawn on a coarse grid (3 px a cell) like ULTRAKILL's ----
U, LH, S = 3, 48, 13
def checker(m, poly):
    k = Image.new("L", m.size, 0); ImageDraw.Draw(k).polygon(poly, fill=255)
    mp, kp = m.load(), k.load()
    for y in range(m.height):
        for x in range(m.width):
            if kp[x, y] and (x + y) % 2 == 0: mp[x, y] = 255
def letter(w, solids, cracks=(), dithers=(), outlines=(), dots=(), holes=()):
    m = Image.new("L", (w + 12, LH + 1), 0)
    md = ImageDraw.Draw(m)
    for p in solids: md.polygon(p, fill=255)
    for p in holes: md.polygon(p, fill=0)
    for p in dithers: checker(m, p)
    for p in outlines: md.polygon(p, outline=255)
    for a, b in cracks: md.line([a, b], fill=0, width=1)
    for x, y in dots: md.rectangle([x, y, x + 1, y + 1], fill=0)
    return m, w
L = {
    "K": letter(40, [[(0,0),(S,0),(S,LH),(0,LH)], [(S+1,20),(27,0),(40,0),(S+1,38)], [(22,27),(29,22),(40,40),(40,LH),(33,LH)]],
                cracks=[((S+1,20),(S+1,38)), ((20,30),(30,20))], dithers=[[(18,30),(22,27),(33,LH),(28,LH)]], dots=[(5,14),(5,32)]),
    "I": letter(S, [[(0,0),(S,0),(S,LH),(0,LH)]]),
    "L1": letter(32, [[(0,0),(S,0),(S,35),(0,35)], [(0,35),(27,35),(32,40),(32,LH),(0,LH)]], cracks=[((0,35),(S,35))]),
    "L2": letter(34, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,35),(22,35),(34,LH),(S,LH)]],
                 outlines=[[(25,35),(30,35),(44,LH),(38,LH)]], dithers=[[(27,37),(29,37),(41,LH-1),(39,LH-1)]]),
    "N": letter(42, [[(0,0),(S,0),(S,LH),(0,LH)], [(29,0),(42,0),(42,LH),(29,LH)], [(S+1,0),(21,0),(28,30),(28,LH),(21,LH),(S+1,18)]],
                cracks=[((S+1,0),(S+1,18)), ((28,30),(28,LH))], dithers=[[(S+1,20),(S+3,20),(21,LH),(19,LH)]]),
    "E": letter(32, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,0),(32,0),(27,11),(S,11)], [(S,18),(27,18),(23,29),(S,29)], [(S,36),(28,36),(32,40),(32,LH),(S,LH)]],
                cracks=[((S,0),(S,11)), ((S,36),(S,LH))]),
    "T": letter(40, [[(5,0),(40,0),(40,12),(0,12)], [(14,12),(27,12),(27,LH),(14,LH)]], cracks=[((14,12),(21,0))]),
    # (KILLCRAFT's own: the same stems, cut corners, cracks and dithered diagonals)
    "C": letter(34, [[(5,0),(34,0),(34,12),(S,12),(S,36),(34,36),(34,LH),(5,LH),(0,43),(0,5)]],
                cracks=[((0,24),(S,30)), ((S,36),(S+7,LH))], dithers=[[(22,36),(26,36),(34,44),(34,47)]], dots=[(5,14)]),
    "R": letter(36, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,0),(31,0),(36,5),(36,22),(31,27),(S,27)], [(S+5,27),(25,27),(36,LH),(29,LH)]],
                holes=[[(S,11),(24,11),(24,17),(S,17)]], cracks=[((S,0),(S,11)), ((25,30),(30,27))], dithers=[[(S+1,29),(S+5,29),(25,LH),(21,LH)]]),
    "A": letter(38, [[(11,0),(27,0),(38,LH),(25,LH),(23,38),(15,38),(13,LH),(0,LH)]],
                holes=[[(16,14),(22,14),(23,28),(15,28)]], cracks=[((19,0),(19,14))], dithers=[[(1,46),(4,40),(6,40),(3,46)]], dots=[(30,32)]),
    "F": letter(32, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,0),(32,0),(27,11),(S,11)], [(S,18),(27,18),(23,29),(S,29)]],
                cracks=[((S,0),(S,11)), ((0,40),(S,44))], dots=[(5,30)]),
}
def make_logo(seq, name):
    letters = [L[c] for c in seq]
    total = sum(w for _, w in letters) + 5 * len(letters)
    logo = Image.new("L", (total + 16, LH + 3), 0)
    x = 0
    for m, w in letters:
        logo.paste(255, (x, 0), m)
        x += w + 5
    logo = logo.resize((logo.width * U, logo.height * U), Image.NEAREST)
    layer(logo, 128, 100, 9).save(os.path.join(out, name), optimize=True)
    print(name, "right edge", 128 + logo.width, "bottom", 100 + logo.height)
make_logo(["K", "I", "L1", "L2", "N", "E", "T"], "logo.png")
make_logo(["K", "I", "L1", "L2", "C", "R", "A", "F", "T"], "logo-craft.png")

# ---- the figure: an exploded block, its pieces spread like V1's wings ----
fig = Image.new("L", (300, 330), 0)
fd = ImageDraw.Draw(fig)
def cube(cx, cy, s):
    faces = [([(cx, cy - s), (cx + s, cy - s // 2), (cx, cy), (cx - s, cy - s // 2)], 1),
             ([(cx - s, cy - s // 2), (cx, cy), (cx, cy + s), (cx - s, cy + s // 2)], 2),
             ([(cx, cy), (cx + s, cy - s // 2), (cx + s, cy + s // 2), (cx, cy + s)], 3)]
    for poly, kind in faces:
        k = Image.new("L", fig.size, 0); ImageDraw.Draw(k).polygon(poly, fill=255)
        kp, fp = k.load(), fig.load()
        for yy in range(max(0, cy - s - 1), min(fig.height, cy + s + 2)):
            for xx in range(max(0, cx - s - 1), min(fig.width, cx + s + 2)):
                if kp[xx, yy]:
                    fp[xx, yy] = 255 if kind == 1 or (kind == 2 and (xx + yy) % 2 == 0) or (kind == 3 and xx % 2 == 0 and yy % 2 == 0) else 0
        fd.line(poly + [poly[0]], fill=255)
cx, cy = 150, 150
parts = []
for k in range(8):
    a = -math.pi / 2 + k * math.pi / 4
    r = 100 + (k % 2) * 18 if k != 4 else 0
    parts.append((int(cx + math.cos(a) * r), int(cy + math.sin(a) * r * 0.85), 13 + (k % 2) * 5))
parts = [p for p in parts if not (p[0] == cx and p[1] == cy)]
for px_, py_, s in parts:
    fd.line([cx, cy, px_, py_], fill=255)
    fd.line([px_ + 16, py_ - 4, px_ + 22, py_ + 2], fill=255); fd.line([px_ + 22, py_ - 4, px_ + 16, py_ + 2], fill=255)
for px_, py_, s in parts: cube(px_, py_, s)
cube(cx, cy, 42)
for j, s in enumerate((19, 18, 17)):
    cube(cx, cy + 74 + j * 40, s)
small = fig.resize((fig.width * 2, fig.height * 2), Image.NEAREST)
fig = fig.resize((fig.width * 8 // 3, fig.height * 8 // 3), Image.NEAREST)
layer(fig, 1090, 110, 7).save(os.path.join(out, "figure.png"), optimize=True)
# (Smaller, up in the right corner: the patch notes panel under it.)
bx = small.getbbox()
fx, fy = 1476 - (bx[0] + bx[2]) // 2, 62 - bx[1]
layer(small, fx, fy, 5).save(os.path.join(out, "figure-small.png"), optimize=True)
print("figure-small", fx + bx[0], fy + bx[1], fx + bx[2], fy + bx[3])

# ---- KillNet's figure (Minecraft off): a globe with ULTRAKILL's dithered shading, its grid cut into it,
# an orbit with a satellite, nodes linked all around (each with its packet's x), a chain of them below ----
solid = []  # (each sphere's disc: black behind it, so the diagram doesn't show through its dark side)
def sphere(img, cx, cy, r, grid=False, tilt=0.38):
    solid.append((cx, cy, r))
    px = img.load()
    for y in range(int(cy - r) - 1, int(cy + r) + 2):
        for x in range(int(cx - r) - 1, int(cx + r) + 2):
            if not (0 <= x < img.width and 0 <= y < img.height):
                continue
            dx, dy = (x + 0.5 - cx) / r, (y + 0.5 - cy) / r
            d2 = dx * dx + dy * dy
            if d2 > 1:
                continue
            dz = math.sqrt(1 - d2)
            light = max(0.0, -0.55 * dx - 0.6 * dy + 0.58 * dz)
            on = light > 0.62 or (light > 0.36 and (x + y) % 2 == 0) or (light > 0.14 and x % 2 == 0 and y % 2 == 0)
            if grid:
                # (the globe's lines, as gaps: latitude and longitude every 30 degrees, its axis tilted)
                yy = dy * math.cos(tilt) - dz * math.sin(tilt)
                zz = dy * math.sin(tilt) + dz * math.cos(tilt)
                lat = math.degrees(math.asin(max(-1, min(1, -yy))))
                lon = math.degrees(math.atan2(dx, zz))
                if min(abs(lat - k) for k in (-60, -30, 0, 30, 60)) < 2.2 or min(abs(lon - k) for k in range(-180, 181, 30)) < 2.6 * (1 + abs(lat) / 90):
                    on = False
            px[x, y] = 255 if on else 0
    ImageDraw.Draw(img).ellipse([cx - r, cy - r, cx + r, cy + r], outline=255)

net = Image.new("L", (300, 330), 0)
nd = ImageDraw.Draw(net)
gx, gy, gr = 150, 148, 46
nodes = []
for k in range(8):
    a = -math.pi / 2 + k * math.pi / 4 + math.pi / 8
    rr = 104 + (k % 2) * 16
    nodes.append((int(gx + math.cos(a) * rr), int(gy + math.sin(a) * rr * 0.85), 8 + (k % 2) * 4))
for x, y, r in nodes:
    nd.line([gx, gy, x, y], fill=255)
    nd.line([x + r + 3, y - 4, x + r + 9, y + 2], fill=255); nd.line([x + r + 9, y - 4, x + r + 3, y + 2], fill=255)
# (the ring of links between neighbouring nodes, dotted)
for i in range(8):
    (x0, y0, _), (x1, y1, _) = nodes[i], nodes[(i + 1) % 8]
    n = int(math.hypot(x1 - x0, y1 - y0) / 4)
    for j in range(1, n):
        net.putpixel((int(x0 + (x1 - x0) * j / n), int(y0 + (y1 - y0) * j / n)), 255)
for x, y, r in nodes:
    sphere(net, x, y, r)
# (the orbit: an ellipse round the globe, behind it where the globe is; a satellite on it)
orbit = Image.new("L", net.size, 0)
ImageDraw.Draw(orbit).ellipse([gx - 74, gy - 22, gx + 74, gy + 22], outline=255, width=2)
mask = Image.new("L", net.size, 0); ImageDraw.Draw(mask).ellipse([gx - gr - 3, gy - gr - 3, gx + gr + 3, gy + gr + 3], fill=255)
op, mp, np_ = orbit.load(), mask.load(), net.load()
for y in range(net.height):
    for x in range(net.width):
        if mp[x, y]:
            np_[x, y] = 0
sphere(net, gx, gy, gr, grid=True)
for y in range(net.height):
    for x in range(net.width):
        if op[x, y] and (not mp[x, y] or y > gy):
            np_[x, y] = 255
sx, sy = gx + 62, gy + 13
nd.rectangle([sx - 5, sy - 5, sx + 5, sy + 5], fill=0, outline=255)
nd.rectangle([sx - 2, sy - 2, sx + 2, sy + 2], fill=255)
# (below: a chain of three nodes, smaller each time, like the block's stack)
y = gy + gr + 8
for j, r in enumerate((13, 11, 9)):
    cy = y + r + 6
    nd.line([gx, y, gx, cy - r], fill=255)
    sphere(net, gx, cy, r)
    y = cy + r
under = Image.new("L", net.size, 0)
for cx, cy, r in solid:
    ImageDraw.Draw(under).ellipse([cx - r - 1, cy - r - 1, cx + r + 1, cy + r + 1], fill=255)
nsmall = net.resize((net.width * 2, net.height * 2), Image.NEAREST)
under = under.resize(nsmall.size, Image.NEAREST)
nb = nsmall.getbbox()
nx, ny = 1476 - (nb[0] + nb[2]) // 2, 62 - nb[1]
layer(nsmall, nx, ny, 5, under).save(os.path.join(out, "figure-net.png"), optimize=True)
print("figure-net", nx + nb[0], ny + nb[1], nx + nb[2], ny + nb[3])
print("ok")
