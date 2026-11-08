# KillNet's logo (Killcraft without Minecraft): a globe, the internet's sign, drawn on the same pixel
# grid as Killcraft's block. Prints the rows for Ui.cs and writes netlogo-preview.png (red).
from PIL import Image, ImageDraw
N = 39
S = 8
big = Image.new("L", (N * S, N * S), 0)
d = ImageDraw.Draw(big)
c, r = N * S / 2, (N / 2 - 0.6) * S
w = int(2.0 * S)
d.ellipse([c - r, c - r, c + r, c + r], outline=255, width=w)
iw = int(1.6 * S)
for rx in (r * 0.42, r * 0.8):
    d.ellipse([c - rx, c - r, c + rx, c + r], outline=255, width=iw)
d.line([c, c - r, c, c + r], fill=255, width=iw)
import math
for f in (-0.55, 0, 0.55):
    y = c + f * r
    hw = math.sqrt(max(0, r * r - (f * r) ** 2))
    d.line([c - hw, y, c + hw, y], fill=255, width=iw)
img = big.resize((N, N), Image.BOX).point(lambda v: 255 if v > 110 else 0)
rows = ["".join("#" if img.getpixel((x, y)) else " " for x in range(N)) for y in range(N)]
for row in rows: print('"' + row + '",')
prev = Image.new("RGB", (N * 6 + 40, N * 6 + 40), (0, 0, 0))
prev.paste((219, 84, 71), (20, 20), img.resize((N * 6, N * 6), Image.NEAREST))
prev.save("netlogo-preview.png")
