# Killcraft

**Minecraft inside ULTRAKILL.** You play as a Minecraft player in ULTRAKILL's levels: Minecraft's movement, health, hunger, hotbar, inventory, swords, bows, shields, armour, TNT and block building, against ULTRAKILL's enemies in ULTRAKILL's levels.

It works like [SkyCraft](https://github.com/chasmlol/SkyCraft) (Minecraft inside Skyrim), and is built on it: a real Minecraft runs hidden in the background with SkyCraft's mod, and ULTRAKILL shows Minecraft's player, HUD, blocks and items in its own world.

## What works

- Minecraft movement and physics on ULTRAKILL's level geometry (walls, floors, slopes, doors).
- Minecraft's HUD (hotbar, hearts, hunger, armour, held items), with ULTRAKILL's HUD hidden.
- Fighting: swords, crits, sweeps, bows, tridents and TNT hurt ULTRAKILL's enemies through ULTRAKILL's own damage system.
- Getting hurt: enemy hits go to Minecraft's health, so armour, Protection enchantments, golden apples and shields all work (raise the shield towards the enemy).
- Blocks you place show up in ULTRAKILL with Minecraft's textures, block enemies and their shots, and light the level (torches, lanterns).
- Arrows stick in walls and in enemies, dropped items lie on the floor, and lit TNT flashes and explodes with an ULTRAKILL explosion.
- Dying in Minecraft is dying in ULTRAKILL (and the other way round). You respawn at ULTRAKILL's checkpoint.
- Every ULTRAKILL level gets its own place in the Minecraft world.

## What you need

- **ULTRAKILL** (Steam).
- **BepInEx 5** for ULTRAKILL: [BepInEx 5 releases](https://github.com/BepInEx/BepInEx/releases) (`BepInEx_win_x64_5.4.x.zip`), unzipped into the ULTRAKILL folder. Start the game once so BepInEx sets itself up.
- **Minecraft Java Edition** (a Microsoft account that owns it).
- **SkyCraft's Minecraft bundle**: `SkyCraft-Minecraft.zip` from the [SkyCraft releases](https://github.com/chasmlol/SkyCraft/releases). It has a portable Prism Launcher with the SkyCraft Minecraft instance; Minecraft and Java download by themselves the first time.

## Install

1. Download `Killcraft-<version>.zip` from this page's **Releases** and unzip it into `ULTRAKILL\BepInEx\plugins\`, so you have `ULTRAKILL\BepInEx\plugins\Killcraft\Killcraft.dll`.
2. Put `SkyCraft-Minecraft.zip` in the same folder: `ULTRAKILL\BepInEx\plugins\Killcraft\SkyCraft-Minecraft.zip` (don't unzip it).
3. Start ULTRAKILL. Killcraft unpacks and starts Minecraft by itself (hidden). **The first time**, Prism Launcher asks you to sign in to your Microsoft account, and Minecraft and Java download (a few minutes).
4. Start any level. After a moment Minecraft takes over V1.

## Controls

Minecraft's own controls (WASD, space, shift, mouse buttons, 1-9, E for the inventory, Q to drop, ...), plus:

| Key | |
|---|---|
| **F9** | Turn Minecraft off (plain ULTRAKILL with your guns) and back on |
| **O** | Minecraft's menu (options, ...) |
| **Esc** | ULTRAKILL's pause menu (or closes an open Minecraft screen) |

## Settings

`ULTRAKILL\BepInEx\config\dev.killcraft.cfg` (made on the first start):

| Setting | Default | |
|---|---|---|
| `ToggleMinecraft` | `F9` | The key that turns Minecraft off and on |
| `FreshWorldEachLaunch` | `true` | Every session starts with a fresh Minecraft world and the starting kit. `false` keeps your builds and inventory between sessions |
| `TntFuseTicks` | `40` | How long lit TNT burns (20 = one second; Minecraft's own is 80) |
| `DigIntoLevels` | `false` | Let TNT and pickaxes dig into ULTRAKILL's level geometry (the holes don't show in ULTRAKILL) |
| `DamageToUltrakill` | `0.3` | ULTRAKILL damage per point of Minecraft damage you deal |
| `DamageToMinecraft` | `1.0` | Multiplier on the damage enemies do to you |
| `BlockBrightness` | `1.0` | How bright Minecraft blocks are drawn (lower it in dark levels) |
| `UnitsPerBlock` | `2` | ULTRAKILL units per Minecraft block (2 makes the Minecraft player about V1's height) |
| `StartWithUltrakill` | `true` | Start Minecraft when ULTRAKILL starts |

## Known limits

- ULTRAKILL's own weapons, movement tech and style meter aren't used while Minecraft has the player (F9 switches back).
- Blocks don't get ULTRAKILL's baked level lighting, so they can look bright in dark levels (see `BlockBrightness`).
- Minecraft mobs and particles other than TNT, falling blocks and explosions are only partly shown.

## Building from source

Needs the .NET SDK (6 or newer) and ULTRAKILL with BepInEx installed.

```
dotnet build -c Release
```

If ULTRAKILL isn't in `C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL`, add `-p:GameDir="X:\path\to\ULTRAKILL"`. The build copies `Killcraft.dll` into `BepInEx\plugins\Killcraft\`.

The code: `Link.cs`/`Proto.cs` speak SkyCraft's shared-memory protocol, `Host.cs` hands V1 to Minecraft and back, `Collision.cs` streams ULTRAKILL's level geometry to Minecraft, `Combat.cs` mirrors enemies and applies hits, `WorldRender.cs` draws Minecraft's blocks, items and entities, `Overlay.cs` shows Minecraft's HUD, `InputForward.cs` sends the keyboard and mouse to Minecraft, and `McSave.cs` prepares Minecraft's world before it starts.

## Credits

- [SkyCraft](https://github.com/chasmlol/SkyCraft) by chasmlol (MIT): the Minecraft mod, the link protocol and the idea. Killcraft is the ULTRAKILL side of it.
- ULTRAKILL by Arsi "Hakita" Patala / New Blood Interactive. Minecraft by Mojang Studios. Killcraft isn't affiliated with either and contains none of their files.

## License

[MIT](LICENSE)
