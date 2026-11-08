from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math, os
os.chdir(os.path.dirname(os.path.abspath(__file__)))
W, H = 1920, 1080
img = Image.new("RGB", (W, H), (0, 0, 0))
d = ImageDraw.Draw(img)
VCR = "fonts/VCR_OSD_Replayed.ttf"
def font(px): return ImageFont.truetype(VCR, px)
GRAY, LBL = (72, 72, 72), (88, 88, 88)

def text_mask(s, px):
    f = font(px)
    l, t, r, b = f.getbbox(s)
    m = Image.new("L", (r - l + 4, b - t + 4), 0)
    md = ImageDraw.Draw(m); md.fontmode = "1"
    md.text((-l + 2, -t + 2), s, font=f, fill=255)
    return m
def paste(mask, x, y, color=(255, 255, 255)):
    img.paste(Image.new("RGB", mask.size, color), (int(x), int(y)), mask)

# ---- background: one block diagram, nothing repeated ----
import sys; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bg
bg.draw(img, font(17), W, H)

# ---- the logo: KILLNET, drawn on a coarse grid (3 px a cell) like ULTRAKILL's ----
U = 3
LH = 48
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
S = 13  # stroke
letters = [
    # K: its bar, its arms split off by a crack, a dithered edge on the leg, two rivets
    letter(40, [[(0,0),(S,0),(S,LH),(0,LH)], [(S+1,20),(27,0),(40,0),(S+1,38)], [(22,27),(29,22),(40,40),(40,LH),(33,LH)]],
           cracks=[((S+1,20),(S+1,38)), ((20,30),(30,20))], dithers=[[(18,30),(22,27),(33,LH),(28,LH)]], dots=[(5,14),(5,32)]),
    # I
    letter(S, [[(0,0),(S,0),(S,LH),(0,LH)]]),
    # L
    letter(32, [[(0,0),(S,0),(S,35),(0,35)], [(0,35),(27,35),(32,40),(32,LH),(0,LH)]], cracks=[((0,35),(S,35))]),
    # L, its foot ending in a slope with a hollow, dithered piece after it (as ULTRAKILL's last L)
    letter(34, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,35),(22,35),(34,LH),(S,LH)]],
           outlines=[[(25,35),(30,35),(44,LH),(38,LH)]], dithers=[[(27,37),(29,37),(41,LH-1),(39,LH-1)]]),
    # N: the diagonal cracked off both bars, dithered along it
    letter(42, [[(0,0),(S,0),(S,LH),(0,LH)], [(29,0),(42,0),(42,LH),(29,LH)], [(S+1,0),(21,0),(28,30),(28,LH),(21,LH),(S+1,18)]],
           cracks=[((S+1,0),(S+1,18)), ((28,30),(28,LH))], dithers=[[(S+1,20),(S+3,20),(21,LH),(19,LH)]]),
    # E: its arms' ends cut at 45
    letter(32, [[(0,0),(S,0),(S,LH),(0,LH)], [(S,0),(32,0),(27,11),(S,11)], [(S,18),(27,18),(23,29),(S,29)], [(S,36),(28,36),(32,40),(32,LH),(S,LH)]],
           cracks=[((S,0),(S,11)), ((S,36),(S,LH))]),
    # T: the bar's end cut, a crack across it where the stem meets it
    letter(40, [[(5,0),(40,0),(40,12),(0,12)], [(14,12),(27,12),(27,LH),(14,LH)]], cracks=[((14,12),(21,0))]),
]
total = sum(w for _, w in letters) + 5 * len(letters)
logo = Image.new("L", (total + 16, LH + 3), 0)
x = 0
for m, w in letters:
    logo.paste(255, (x, 0), m)
    x += w + 5
logo = logo.resize((logo.width * U, logo.height * U), Image.NEAREST)
lx, ly = 128, 100
back = logo.filter(ImageFilter.MaxFilter(9))
img.paste((0, 0, 0), (lx, ly), back)
paste(logo, lx, ly)
logo_bottom = ly + logo.height

def term(lines, x, y, px=27, gap=2, color=(205, 205, 205)):
    for ln in lines:
        m = text_mask(ln, px)
        img.paste((0, 0, 0), (x - 2, y - 2, x + m.width + 2, y + m.height + 2))
        paste(m, x, y, color)
        y += m.height + gap
    return y
term(["KILLNET_2.0.0 READY"], 130, logo_bottom + 8)
term(["ULTRAKILL FOUND...OK", "BEPINEX 5.4.23...OK", "STANDBY - WAIT FOR LAUNCH"], 130, 322)

# ---- buttons, as ULTRAKILL's menu draws them ----
def shape(x, y, w, h, c):
    return [(x + c, y), (x + w - c, y), (x + w, y + c), (x + w, y + h - c), (x + w - c, y + h), (x + c, y + h), (x, y + h - c), (x, y + c)]
def button(x, y, w, h, label, selected=False):
    d.polygon(shape(x, y, w, h, 12), fill=(255, 255, 255))
    if not selected:
        d.polygon(shape(x + 5, y + 5, w - 10, h - 10, 9), fill=(0, 0, 0))
    m = text_mask(label, 48)
    paste(m, x + (w - m.width) / 2, y + (h - m.height) / 2 + 2, (0, 0, 0) if selected else (255, 255, 255))
bx, bw, bh, gap = 128, 628, 100, 13
y = 436
button(bx, y, bw, bh, "PLAY", True); y += bh + gap
button(bx, y, bw, bh, "MINECRAFT: OFF"); y += bh + gap
half = (bw - gap) // 2
button(bx, y, half, bh, "UPDATE"); button(bx + half + gap, y, half, bh, "OPTIONS"); y += bh + gap
button(bx, y, bw, bh, "QUIT"); y += bh + 22
term(["INIT SOCIALS...OK", "GITHUB  DISCORD  THUNDERSTORE"], 130, y)

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
fx, fy = 1090, 110
img.paste((0, 0, 0), (fx, fy), fig.filter(ImageFilter.MaxFilter(7)))
paste(fig, fx, fy)

# ---- the frame ----
d.rounded_rectangle([6, 6, W - 7, H - 7], radius=22, outline=(255, 255, 255), width=5)
img.save("launcher-mockup2.png")
print("ok")
