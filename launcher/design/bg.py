# The launcher's background: one block diagram, nothing repeated (blocks of different sizes, each label
# once, register grids, buses). wrap=True: it joins up top to bottom (what leaves the top comes back at
# the bottom), so the launcher can scroll it up forever, as ULTRAKILL's menu scrolls its own.
import random
from PIL import ImageDraw

LABELS = ["Room Router", "Lobby List", "Replay Buffer", "Packet Queue", "Voice Codec", "Enemy Authority",
          "Steam P2P", "Checkpoint Sync", "Party Host", "Level Loader", "Harmony Patches", "Arena State",
          "Skin Cache", "Update Check", "Mode Select", "Boss Scaling", "Spectate Cam", "Chat Relay",
          "Wave Seed", "Door Latch", "Hit Ledger", "Clock Divider", "Proximity Mix", "Echo Filter",
          "Input Latch", "Snapshot Bus", "Restart Vote", "Tram Control", "Coin Tracker", "Death Flag",
          "Interrupt Control", "Timing & Control", "Address Buffer", "Stack Pointer", "Frame Counter",
          "Sandbox State", "Cutscene Vote", "Rank Screen", "Torch Light", "Glass Ledger",
          # (more, for the taller, scrolling one: still each once)
          "Style Meter", "Parry Window", "Rail Charge", "Nail Magnet", "Saw Bounce", "Coin Split",
          "Blood Heal", "Hook Tether", "Slam Storage", "Dash Stamina", "Slide Boost", "Wall Jump",
          "Rocket Ride", "Freeze Frame", "Overheat Vent", "Punch Queue", "Knuckle Blast", "Whiplash",
          "Feedbacker", "Explosion Pool", "Gib Counter", "Gore Zone", "Hurt Zone", "Death Zone",
          "Secret Orb", "Skull Socket", "Altar Check", "Lever State", "Elevator Ctrl", "Bridge Latch",
          "Music Cue", "Boss Bar", "Health Sync", "Ping Table", "Name Tags", "Kick Vote", "Code Lookup",
          "Invite Queue", "Rich Presence", "Join Secret", "Lobby Data", "Owner Swap", "Mute List",
          "Mic Gate", "Push To Talk", "Jitter Buffer", "Clock Offset", "Packet Loss", "Resend Queue",
          "Spawn Table", "Prop Ledger", "Block Mesher", "Navmesh Bake", "Radiance Tier", "Puppet Pool",
          "Wake Queue", "Template Map", "Path Hash", "Fall Guard", "Safe Ground", "Bump Solver",
          "Shot Mirror", "Beam Trace", "Projectile Map", "Parry Relay", "PvP Damage", "Target Marker",
          "Revive Timer", "Spectate Feed", "Level Exit", "Results Wait", "Grade Table", "Time Bonus",
          "Kill Counter", "Prank Check", "Cheat Flag", "Difficulty", "Save Slot", "Config Store",
          "Asset Bundle", "Shader Cache", "Light Probe", "Fog Volume", "Water Plane", "Sky Box"]


# What the last draw() made, for the launcher to animate over the picture: the wires (each a list of
# points, from the block they leave to the one they go into) and the blocks.
WIRES, BOXES = [], []


def draw(img, font, W, H, seed=7, gray=(72, 72, 72), label=(88, 88, 88), wrap=False):
    d = ImageDraw.Draw(img)
    WIRES.clear()
    BOXES.clear()
    rnd = random.Random(seed)
    labels = LABELS[:]
    rnd.shuffle(labels)
    boxes = []
    copies = (-H, 0, H) if wrap else (0,)

    def free(r, pad=34):
        x0, y0, x1, y1 = r
        if x0 < 24 or x1 > W - 24 or (not wrap and (y0 < 24 or y1 > H - 24)) or (wrap and (y0 < 0 or y0 >= H)):
            return False
        for b in boxes:
            for dy in copies:
                if not (x1 + pad < b[0] or b[2] + pad < x0 or y1 + pad < b[1] + dy or b[3] + dy + pad < y0):
                    return False
        return True

    def ypos(h):
        return rnd.randrange(0, H) if wrap else rnd.randrange(30, H - h - 30)

    # a few big register arrays (a grid of cells inside), then blocks of mixed sizes
    grids = []
    for _ in range(max(3, H // 360)):
        for _ in range(200):
            w, h = rnd.choice([(200, 330), (260, 250), (180, 410)])
            x, y = rnd.randrange(30, W - w - 30), ypos(h)
            if free((x, y, x + w, y + h), 60):
                boxes.append((x, y, x + w, y + h)); grids.append((x, y, x + w, y + h))
                break
    for _ in range(4000):
        if not labels:
            break
        w, h = rnd.choice([(118, 74), (150, 74), (118, 92), (190, 60), (96, 96), (230, 74)])
        name = labels[-1]
        # (wide enough for its name, on one line or two)
        lines = name.split(" ", 1) if w < 180 else [name]
        w = max(w, max(int(d.textlength(t, font=font)) for t in lines) + 24)
        x, y = rnd.randrange(30, max(31, W - w - 30)), ypos(h)
        if free((x, y, x + w, y + h)):
            boxes.append((x, y, x + w, y + h, labels.pop()))

    def line(points):
        WIRES.append(list(points))
        for dy in copies:
            d.line([(x, y + dy) for x, y in points], fill=gray, width=2)

    def arrow_head(x, y, dx, dy_):
        for dy in copies:
            if dx:
                s = 7 * dx
                d.polygon([(x - s, y + dy - 5), (x - s, y + dy + 5), (x, y + dy)], fill=gray)
            else:
                s = 7 * dy_
                d.polygon([(x - 5, y + dy - s), (x + 5, y + dy - s), (x, y + dy)], fill=gray)

    # buses: long lines across, at uneven places (the upright ones run the whole height)
    for _ in range(max(3, H // 360)):
        y = rnd.randrange(40, H - 40)
        WIRES.append([(16, y), (W - 16, y)])
        d.line([(16, y), (W - 16, y)], fill=gray, width=2)
    for _ in range(4):
        x = rnd.randrange(40, W - 40)
        WIRES.append([(x, 0 if wrap else 16), (x, H if wrap else H - 16)])
        d.line([(x, 0 if wrap else 16), (x, H if wrap else H - 16)], fill=gray, width=2)

    # each block wired to a near one (the nearer way round, wrapped): an elbow, an arrow into it
    plain = [b for b in boxes if len(b) == 5]
    wired = set()

    def near(b, o):
        cx, cy = (b[0] + b[2]) // 2, (b[1] + b[3]) // 2
        ox, oy = (o[0] + o[2]) // 2, (o[1] + o[3]) // 2
        best = None
        for dy in copies:
            dist = abs(ox - cx) + abs(oy + dy - cy)
            if best is None or dist < best[0]:
                best = (dist, dy)
        return best

    for b in plain:
        cx, cy = (b[0] + b[2]) // 2, (b[1] + b[3]) // 2
        others = sorted((o for o in boxes if o is not b), key=lambda o: near(b, o)[0])
        for o in others[:rnd.choice([1, 1, 2])]:
            key = frozenset((b[:4], o[:4]))
            if key in wired:
                continue
            wired.add(key)
            dy = near(b, o)[1]
            o = (o[0], o[1] + dy, o[2], o[3] + dy)
            ox, oy = (o[0] + o[2]) // 2, (o[1] + o[3]) // 2
            if abs(ox - cx) > abs(oy - cy):
                sx = b[2] if ox > cx else b[0]
                ex = o[0] if ox > cx else o[2]
                ty = max(o[1] + 10, min(o[3] - 10, cy))
                mid = (sx + ex) // 2
                line([(sx, cy), (mid, cy), (mid, ty), (ex, ty)])
                arrow_head(ex, ty, 1 if ox > cx else -1, 0)
            else:
                sy = b[3] if oy > cy else b[1]
                ey = o[1] if oy > cy else o[3]
                tx = max(o[0] + 10, min(o[2] - 10, cx))
                mid = (sy + ey) // 2
                line([(cx, sy), (cx, mid), (tx, mid), (tx, ey)])
                arrow_head(tx, ey, 0, 1 if oy > cy else -1)

    # blocks over the wires (black inside), their names
    BOXES.extend(b[:4] for b in boxes)
    for b in boxes:
        for dy in copies:
            d.rectangle((b[0], b[1] + dy, b[2], b[3] + dy), fill=(0, 0, 0), outline=gray, width=2)
    for b in plain:
        text = b[4].replace(" ", "\n", 1) if b[2] - b[0] < 180 else b[4]
        for dy in copies:
            d.multiline_text(((b[0] + b[2]) / 2, (b[1] + b[3]) / 2 + dy), text, font=font, fill=label, anchor="mm", align="center", spacing=2)
    cells = ["N", "E", "W", "B", "L", "01", "02", "D", "S", "R"]
    for g in grids:
        x0, y0, x1, y1 = g
        head = 34
        title = rnd.choice(["Register Array", "Multiplexer", "Peer Table", "Room Table"])
        cols = 2 if x1 - x0 < 240 else 3
        rows = max(3, (y1 - y0 - head) // 70)
        cw, rh = (x1 - x0) / cols, (y1 - y0 - head) / rows
        marks = [[rnd.choice(cells) for c in range(cols)] for r in range(rows)]
        for dy in copies:
            d.line([(x0, y0 + head + dy), (x1, y0 + head + dy)], fill=gray, width=2)
            d.text(((x0 + x1) / 2, y0 + head / 2 + dy), title, font=font, fill=label, anchor="mm")
            for c in range(1, cols):
                d.line([(x0 + c * cw, y0 + head + dy), (x0 + c * cw, y1 + dy)], fill=gray, width=2)
            for r in range(1, rows):
                d.line([(x0, y0 + head + r * rh + dy), (x1, y0 + head + r * rh + dy)], fill=gray, width=2)
            for r in range(rows):
                for c in range(cols):
                    d.text((x0 + (c + 0.5) * cw, y0 + head + (r + 0.5) * rh + dy), marks[r][c], font=font, fill=label, anchor="mm")
    return len(plain), len(labels)
