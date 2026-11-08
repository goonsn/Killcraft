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

def layer(mask, x, y, halo):
    page = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    black = mask.filter(ImageFilter.MaxFilter(halo))
    page.paste((0, 0, 0, 255), (x, y), black)
    page.paste((255, 255, 255, 255), (x, y), mask)
    return page

# ---- the logo: KILLNET, drawn on a coarse grid (3 px a cell) like ULTRAKILL's ----
U, LH, S = 3, 48, 13
def checker(m, poly):
    k = Image.new("L", m.size, 0); ImageDraw.Draw(k).polygon(poly, fill=255)
    mp, kp = m.load(), k.load()
    for y in range(m.height):
        for x in range(m.width):
            if kp[x, y] and (x + y) % 2 == 0: mp[x, y] = 255
def letter(w, solids, cracks=(), dithers=(), outlines=(), dots=()):
    m = Image.new("L", (w + 12, LH + 1), 0)
    md = ImageDraw.Draw(m)
    for p in solids: md.polygon(p, fill=255)
    for p in dithers: checker(m, p)
    for p in outlines: md.polygon(p, outline=255)
    for a, b in cracks: md.line([a, b], fill=0, width=1)
    for x, y in dots: md.rectangle([x, y, x + 1, y + 1], fill=0)
    return m, w
letters = [
    letter(40, [[(0,0),(S,0),(S,LH),(0,LH)], [(S+1,20),(27,0),(40,0),(S+1,38)], [(22,27),(29,22),(40,40),(40,LH),(33,LH)]],
           cracks=[((S+1,20),(S+1,38)), ((20,30),(30,20))], dithers=[[(18,30),(22,27),(33,LH),(28,LH)]], dots=[(5,14),(5,32)]),
    letter(S, [[(0,0),(S,0),(S,LH),(0,LH)]]),
    letter(32, [[(0,0),(S,0),(S,35),(0,35)], [(0,35),(27,35),(32,40),(32,LH),(0,LH)]], cracks=[((0,35),(S,35))]),
    letter(34, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,35),(22,35),(34,LH),(S,LH)]],
           outlines=[[(25,35),(30,35),(44,LH),(38,LH)]], dithers=[[(27,37),(29,37),(41,LH-1),(39,LH-1)]]),
    letter(42, [[(0,0),(S,0),(S,LH),(0,LH)], [(29,0),(42,0),(42,LH),(29,LH)], [(S+1,0),(21,0),(28,30),(28,LH),(21,LH),(S+1,18)]],
           cracks=[((S+1,0),(S+1,18)), ((28,30),(28,LH))], dithers=[[(S+1,20),(S+3,20),(21,LH),(19,LH)]]),
    letter(32, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,0),(32,0),(27,11),(S,11)], [(S,18),(27,18),(23,29),(S,29)], [(S,36),(28,36),(32,40),(32,LH),(S,LH)]],
           cracks=[((S,0),(S,11)), ((S,36),(S,LH))]),
    letter(40, [[(5,0),(40,0),(40,12),(0,12)], [(14,12),(27,12),(27,LH),(14,LH)]], cracks=[((14,12),(21,0))]),
]
total = sum(w for _, w in letters) + 5 * len(letters)
logo = Image.new("L", (total + 16, LH + 3), 0)
x = 0
for m, w in letters:
    logo.paste(255, (x, 0), m)
    x += w + 5
logo = logo.resize((logo.width * U, logo.height * U), Image.NEAREST)
layer(logo, 128, 100, 9).save(os.path.join(out, "logo.png"), optimize=True)
print("logo bottom", 100 + logo.height)

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
fig = fig.resize((fig.width * 8 // 3, fig.height * 8 // 3), Image.NEAREST)
layer(fig, 1090, 110, 7).save(os.path.join(out, "figure.png"), optimize=True)
print("ok")
