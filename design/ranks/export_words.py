import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ranks as R
from PIL import Image
OUT = os.path.dirname(os.path.abspath(__file__))
# Drawn 3x ULTRAKILL's size (the plugin makes its pictures 3x too: sharp on a big screen).
SC = 3
# The coloured letters' right edges in ULTRAKILL's own pictures (the words go 5 px after them).
for letter, rest in R.RANKS:
    _, right = R.prefix(letter)
    mask, k = R.word(rest, 45 * SC, (R.W - 4 - (right + 5)) * SC)
    img = Image.new("RGBA", mask.size, (255, 255, 255, 0))
    img.putalpha(mask)
    img.save(os.path.join(OUT, f"word-{letter}.png"))
    print(letter, rest, mask.size, f"{k:.2f}")
R.ultra(sc=SC, path=os.path.join(OUT, "rank-U.png"))
print("ok")
