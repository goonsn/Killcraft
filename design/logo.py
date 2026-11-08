# Killcraft's logo: a Minecraft block (isometric, ULTRAKILL's dithered shading) torn by three claw marks.
# Prints the pixel rows for Ui.cs and writes logo-preview.png (red, as the party screen shows it).
from PIL import Image, ImageDraw
W, H = 41, 41
img = Image.new("L", (W, H), 0)
d = ImageDraw.Draw(img)
cx, top, s = 20, 1, 19
T = [(cx, top), (cx + s, top + s // 2 + 0), (cx, top + s), (cx - s, top + s // 2)]
L = [(cx - s, top + s // 2), (cx, top + s), (cx, top + s + 19), (cx - s, top + s // 2 + 19)]
R = [(cx, top + s), (cx + s, top + s // 2), (cx + s, top + s // 2 + 19), (cx, top + s + 19)]
def face(poly, kind):
    m = Image.new("L", (W, H), 0); ImageDraw.Draw(m).polygon(poly, fill=255)
    mp, ip = m.load(), img.load()
    for y in range(H):
        for x in range(W):
            if mp[x, y]:
                on = kind == 0 or (kind == 1 and (x + y) % 2 == 0) or (kind == 2 and x % 2 == 0)
                ip[x, y] = 255 if on else 0
face(T, 0); face(L, 1); face(R, 2)
for poly in (T, L, R):
    d.line(poly + [poly[0]], fill=255)
# three claw marks across it, top right to bottom left
ip = img.load()
for k, (x0, y0) in enumerate([(27, 4), (31, 7), (35, 10)]):
    for i in range(0, 25):
        x, y = x0 - i * 0.95, y0 + i * 0.95
        for w in (0, 1, 2):
            xi, yi = int(round(x)) + w, int(round(y))
            if 0 <= xi < W and 0 <= yi < H:
                ip[xi, yi] = 0
bbox = img.getbbox(); img = img.crop(bbox)
rows = ["".join("#" if img.getpixel((x, y)) else " " for x in range(img.width)) for y in range(img.height)]
print(img.size)
for r in rows: print('"' + r + '",')
red = Image.new("RGB", (img.width * 6 + 40, img.height * 6 + 40), (0, 0, 0))
big = img.resize((img.width * 6, img.height * 6), Image.NEAREST)
red.paste((219, 84, 71), (20, 20), big)
red.save("logo-preview.png")
