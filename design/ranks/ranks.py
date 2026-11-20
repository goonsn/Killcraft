import os
from PIL import Image, ImageDraw, ImageFont
HERE = os.path.dirname(os.path.abspath(__file__))
UK = os.path.join(HERE, "uk")
OUT = os.path.join(HERE, "ranks")
os.makedirs(OUT, exist_ok=True)
BEBAS = r"C:\Windows\Fonts\BebasNeue-Regular.ttf"
ANTON = r"C:\Windows\Fonts\Anton-Regular.ttf"
for p in (BEBAS, ANTON):
    if not os.path.exists(p):
        alt = os.path.join(os.environ["LOCALAPPDATA"], "Microsoft", "Windows", "Fonts", os.path.basename(p))
        if p == BEBAS: BEBAS = alt
        else: ANTON = alt
S = 4  # drawn 4x, shrunk: smooth edges as ULTRAKILL's
W, H = 300, 100
RANKS = [("D", "EMOLITION"), ("C", "ARNAGE"), ("B", "LOODBATH"), ("A", "NNIHILATION"),
         ("S", "LAUGHTERHOUSE"), ("SS", "YNCHRONIZED"), ("SSS", "UPERMASSACRE")]


def prefix(letter):
    """ULTRAKILL's own coloured letter(s), cut from its picture: everything not white."""
    im = Image.open(os.path.join(UK, f"Rank{letter}.png")).convert("RGBA")
    px = im.load()
    out = Image.new("RGBA", im.size, (0, 0, 0, 0))
    po = out.load()
    right = 0
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            white = r > 200 and g > 200 and b > 200
            # (Anti-aliased white edges: greyish; kept out.)
            if white or (abs(r - g) < 30 and abs(g - b) < 30 and r > 90):
                continue
            po[x, y] = (r, g, b, a)
            right = max(right, x)
    return out, right


def word(text, height_px, max_w):
    """The white word: Bebas Neue, its capitals height_px tall, narrowed to fit max_w."""
    f = ImageFont.truetype(BEBAS, 10)
    # (Size for the capitals' height.)
    size = 10
    while True:
        f = ImageFont.truetype(BEBAS, size * S)
        l, t, r, b = f.getbbox("H")
        if (b - t) >= height_px * S:
            break
        size += 1
    l, t, r, b = f.getbbox(text)
    img = Image.new("L", (r - l + 8 * S, b - t + 8 * S), 0)
    ImageDraw.Draw(img).text((-l + 4 * S, -t + 4 * S), text, font=f, fill=255)
    img = img.crop(img.getbbox())
    w = img.width / S
    k = min(1.0, max_w / w)
    img = img.resize((max(1, int(img.width * k / S)), height_px), Image.LANCZOS)
    return img, k


def make(letter, rest):
    pre, right = prefix(letter)
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    canvas.alpha_composite(pre)
    x0 = right + 5
    mask, k = word(rest, 45, W - 4 - x0)
    white = Image.new("RGBA", mask.size, (255, 255, 255, 255))
    canvas.paste(white, (x0, 36), mask)
    canvas.save(os.path.join(OUT, f"Party{letter}.png"))
    return k


def ultra(text="ULTRAPARTY", sc=1, path=None):
    """ULTRAPARTY as ULTRAKILL's ULTRAKILL; sc: drawn that many times as big (the plugin's: 3)."""
    f = None
    size = 10
    while True:
        f = ImageFont.truetype(ANTON, size * S)
        l, t, r, b = f.getbbox("H")
        if (b - t) >= 66 * sc * S:
            break
        size += 1
    l, t, r, b = f.getbbox(text)
    m = Image.new("L", (r - l + 12 * S, b - t + 12 * S), 0)
    ImageDraw.Draw(m).text((-l + 6 * S, -t + 6 * S), text, font=f, fill=255)
    m = m.crop(m.getbbox())
    tw = 250 * sc
    m = m.resize((min(tw, int(m.width / S)), 66 * sc), Image.LANCZOS)
    canvas = Image.new("RGBA", (W * sc, H * sc), (0, 0, 0, 0))
    x, y = 22 * sc + (tw - m.width) // 2, 17 * sc
    # (ULTRAKILL's: yellow, an orange edge down and to the right of it.)
    for dx, dy in ((2, 2), (1, 2), (2, 1), (1, 1)):
        for i in range(sc):
            canvas.paste(Image.new("RGBA", m.size, (240, 96, 0, 255)), (x + dx * sc - i, y + dy * sc - i), m)
    canvas.paste(Image.new("RGBA", m.size, (255, 216, 0, 255)), (x, y), m)
    canvas.save(path or os.path.join(OUT, "PartyU.png"))


def main():
    for l, r in RANKS:
        k = make(l, r)
        print(l + r, f"narrowed to {k:.2f}")
    ultra()
    # Side by side: ULTRAKILL's and the party's.
    sheet = Image.new("RGBA", (640, 830), (24, 24, 24, 255))
    for i, n in enumerate(["D", "C", "B", "A", "S", "SS", "SSS", "U"]):
        sheet.alpha_composite(Image.open(os.path.join(UK, f"Rank{n}.png")).convert("RGBA"), (10, 10 + i * 102))
        sheet.alpha_composite(Image.open(os.path.join(OUT, f"Party{n}.png")).convert("RGBA"), (330, 10 + i * 102))
    sheet.save(os.path.join(HERE, "6-party-ranks-vs-ultrakill.png"))
    print("ok")


if __name__ == "__main__":
    main()
