# Draws hats.bin as the game would read it (on V1's head, back faces dropped as Unity does): check.png.
import struct, sys
import numpy as np
from PIL import Image, ImageDraw
import preview


def read(path):
    b = open(path, "rb").read()
    assert b[:4] == b"KCHT"
    o = 4
    ver, width, n = struct.unpack_from("<ifi", b, o); o += 12
    hats = []
    for _ in range(n):
        ln = b[o]; o += 1
        name = b[o:o + ln].decode(); o += ln
        parts, = struct.unpack_from("<i", b, o); o += 4
        tris = []
        for _ in range(parts):
            px, py, pz, anim = struct.unpack_from("<3fB", b, o); o += 13
            nv, = struct.unpack_from("<i", b, o); o += 4
            V = np.frombuffer(b, "<f4", nv * 6, o).reshape(nv, 6); o += nv * 24
            P = V[:, :3] + np.array((px, py, pz))
            subs, = struct.unpack_from("<i", b, o); o += 4
            for _ in range(subs):
                r, g, bl, fl = b[o:o + 4]; o += 4
                ni, = struct.unpack_from("<i", b, o); o += 4
                idx = np.frombuffer(b, "<u2", ni, o); o += ni * 2
                col = preview.PLAYER_SAMPLE if fl & 2 else (r / 255, g / 255, bl / 255)
                for k in range(0, ni, 3):
                    tris.append((P[idx[k:k + 3]], col, bool(fl & 1), False))
        hats.append((name, tris))
    return hats


hd = preview.head()
hats = read("hats.bin")
size = 260
out = Image.new("RGB", (size * len(preview.VIEWS), (size + 16) * len(hats)), (18, 18, 22))
d = ImageDraw.Draw(out)
for r, (name, tris) in enumerate(hats):
    for c, (label, yaw, pitch) in enumerate(preview.VIEWS):
        out.paste(preview.render(hd + tris, yaw, pitch, size), (c * size, r * (size + 16) + 16))
        d.text((c * size + 6, r * (size + 16) + 2), f"{name} - {label}", fill=(235, 235, 235))
out.save("check.png")
print("check.png")
