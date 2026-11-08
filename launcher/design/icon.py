# The launcher's icon: the logo's K (as art.py draws it, 3 px cells), white on black with ULTRAKILL's
# cut corners. Writes ../app/Art/icon.ico (16 to 256 px).
import os
from PIL import Image, ImageDraw
os.chdir(os.path.dirname(os.path.abspath(__file__)))

LH, S = 48, 13
def checker(m, poly):
    k = Image.new("L", m.size, 0); ImageDraw.Draw(k).polygon(poly, fill=255)
    mp, kp = m.load(), k.load()
    for y in range(m.height):
        for x in range(m.width):
            if kp[x, y] and (x + y) % 2 == 0: mp[x, y] = 255

k = Image.new("L", (41, LH + 1), 0)
d = ImageDraw.Draw(k)
for p in [[(0,0),(S,0),(S,LH),(0,LH)], [(S+1,20),(27,0),(40,0),(S+1,38)], [(22,27),(29,22),(40,40),(40,LH),(33,LH)]]:
    d.polygon(p, fill=255)
checker(k, [(18,30),(22,27),(33,LH),(28,LH)])
for a, b in [((S+1,20),(S+1,38)), ((20,30),(30,20))]:
    d.line([a, b], fill=0, width=1)
for x, y in [(5,14),(5,32)]:
    d.rectangle([x, y, x + 1, y + 1], fill=0)

def icon(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    dd = ImageDraw.Draw(img)
    c = max(2, size // 10)
    dd.polygon([(c,0),(size-1-c,0),(size-1,c),(size-1,size-1-c),(size-1-c,size-1),(c,size-1),(0,size-1-c),(0,c)], fill=(0,0,0,255))
    inner = int(size * 0.78)
    scale = inner / max(k.width, k.height)
    w, h = max(1, round(k.width * scale)), max(1, round(k.height * scale))
    kk = k.resize((w, h), Image.NEAREST if size >= 48 else Image.LANCZOS)
    img.paste((255, 255, 255, 255), ((size - w) // 2, (size - h) // 2), kk)
    return img

sizes = [256, 128, 64, 48, 32, 24, 16]
out = os.path.join("..", "app", "Art", "icon.ico")
icon(256).save(out, sizes=[(s, s) for s in sizes], append_images=[icon(s) for s in sizes[1:]])
icon(256).save(os.path.join("..", "app", "Art", "icon-preview.png"))
print("ok")
