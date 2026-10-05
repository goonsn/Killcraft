# Killcraft
<img width="1919" height="1065" alt="665136776-12f90f9a-d887-431f-a8fd-c0272574ca5d" src="https://github.com/user-attachments/assets/75abbd76-06bb-455d-b836-d6737a5c9bfb" />

**Minecraft inside ULTRAKILL.** You play as a Minecraft player in ULTRAKILL's levels: Minecraft's movement, health, hunger, hotbar, inventory, swords, bows, shields, armour, TNT and block building, against ULTRAKILL's enemies in ULTRAKILL's levels.

It works like [SkyCraft](https://github.com/chasmlol/SkyCraft) (Minecraft inside Skyrim), and is built on it: a real Minecraft runs hidden in the background with SkyCraft's mod, and ULTRAKILL shows Minecraft's player, HUD, blocks and items in its own world.

## What works

- Minecraft movement and physics on ULTRAKILL's level geometry (walls, floors, slopes, doors).
- Minecraft's HUD (hotbar, hearts, hunger, armour, held items), with ULTRAKILL's HUD hidden.
- Fighting: swords, crits, sweeps, bows, tridents and TNT hurt ULTRAKILL's enemies through ULTRAKILL's own damage system.
- Getting hurt: enemy hits go to Minecraft's health, so armour, Protection enchantments, golden apples and shields all work (raise the shield towards the enemy).
- Blocks you place show up in ULTRAKILL with Minecraft's textures, block enemies and their shots, and light the level (torches, lanterns).
- Arrows stick in walls, doors (and move with them) and enemies, stay in their corpses, and can be picked up again from corpses and the floor. Dropped items lie on the floor, and lit TNT flashes and explodes with an ULTRAKILL explosion.
- Minecraft mobs fight on your side: spawn zombies, skeletons, iron golems, wolves, creepers, ... with spawn eggs and they go after ULTRAKILL's enemies near them. In the levels they leave you alone.
- **The Nether:** build a Nether portal anywhere in a level (`/give @s minecraft:obsidian 14`, then flint and steel) and stand in it for 3 seconds. You go to Minecraft's real Nether, with its own part for every level, and its mobs attack you there, along with ULTRAKILL enemies that keep turning up around you (filth, strays, drones, schisms, soldiers, and now and then a cerberus, swordsmachine or mindflayer). ULTRAKILL shows it around V1, with Minecraft's Nether fog. Any Nether portal brings you back to the portal you came through, and dying there respawns you in the level.
- Shields block ULTRAKILL's attacks, thorns armour hurts ULTRAKILL's enemies, and a swing parries projectiles (Stray energy balls, ...) like V1's punch.
- SMILEOS terminals and shops can be clicked.
- Potion effects work on the Minecraft player (speed, jump boost, strength, resistance, regeneration, ...).
- Dying in Minecraft is dying in ULTRAKILL (and the other way round). You respawn at ULTRAKILL's checkpoint.
- Every ULTRAKILL level gets its own place in the Minecraft world.

## What you need

- **ULTRAKILL** (Steam).
- **BepInEx 5** for ULTRAKILL: [BepInEx 5 releases](https://github.com/BepInEx/BepInEx/releases) (`BepInEx_win_x64_5.4.x.zip`), unzipped into the ULTRAKILL folder. Start the game once so BepInEx sets itself up.
- **Minecraft Java Edition** (a Microsoft account that owns it).

You don't need SkyCraft or Skyrim: the Killcraft download already has SkyCraft's Minecraft bundle in it (`SkyCraft-Minecraft.zip`, a portable Prism Launcher with the SkyCraft Minecraft instance). Minecraft and Java download by themselves the first time.

## Install

1. Download `Killcraft-<version>.zip` from this page's **Releases** and unzip it into `ULTRAKILL\BepInEx\plugins\`, so you have `ULTRAKILL\BepInEx\plugins\Killcraft\Killcraft.dll`. Leave `SkyCraft-Minecraft.zip` in that folder zipped.
2. Start ULTRAKILL. Killcraft unpacks and starts Minecraft by itself (hidden). **The first time**, Prism Launcher asks you to sign in to your Microsoft account, and Minecraft and Java download (a few minutes).
3. Start any level. After a moment Minecraft takes over V1.

To update, unzip the new version over the old one.

## Controls

Minecraft's own controls (WASD, space, shift, mouse buttons, 1-9, E for the inventory, Q to drop, ...), plus:

| Key | |
|---|---|
| **F9** | Turn Minecraft off (plain ULTRAKILL with your guns) and back on |
| **O** | Minecraft's menu (options, ...) |
| **Esc** | ULTRAKILL's pause menu, which pauses Minecraft too and mutes its sound (or closes an open Minecraft screen) |

## Settings

`ULTRAKILL\BepInEx\config\dev.killcraft.cfg` (made on the first start):

| Setting | Default | |
|---|---|---|
| `ToggleMinecraft` | `F9` | The key that turns Minecraft off and on |
| `FreshWorldEachLaunch` | `true` | Every session starts with a fresh Minecraft world and the starting kit. `false` keeps your builds and inventory between sessions |
| `MobsFightEnemies` | `true` | Minecraft mobs go after ULTRAKILL's enemies near them |
| `MobsAttackYouInLevels` | `false` | Minecraft mobs attack you in ULTRAKILL's levels too (off: they're on your side there; in the Nether they always attack you) |
| `NetherEnemies` | `6` | How many ULTRAKILL enemies are around you at a time in the Nether (0: none) |
| `AlwaysThorns` | `true` | Thorns armour hits back every time an enemy hurts you (Minecraft's own: 15% a level) |
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
- ULTRAKILL's enemies only ever attack V1, not Minecraft mobs.
- The level's own enemies wait in the level and ignore you while you're in the Nether. F9 doesn't work in the Nether, since ULTRAKILL's V1 would have nothing to stand on. You always arrive at height 70, in a small room dug out for the portal.
- Holding right click (a raised shield, a drawn bow, eating) stops left clicks attacking, as in Minecraft.
- Mobs on your side in the levels don't fight back when you hit them, and don't fight each other (an iron golem leaves zombies alone).
- Flint and steel and fire charges light ULTRAKILL's floors only from the side: aim at the bottom of a wall just above the floor, or at the side of a step (aiming straight down at a floor doesn't work). Minecraft blocks light as usual (netherrack burns forever). Enemies walking through fire or lava burn.
- Minecraft only has the level's collision about 36 blocks around you: an ender pearl thrown further comes back to you.
- Effects that only change how Minecraft looks (night vision, blindness, darkness, nausea, invisibility) don't show in ULTRAKILL, and invisibility doesn't hide you from its enemies.
- No third person: F5 does nothing (ULTRAKILL's camera stays in V1's head).
- Crouching doesn't stop you at edges (SkyCraft turns that off, since Minecraft's edge check only knows Minecraft blocks).
- Zombies, skeletons and other mobs that burn in daylight don't catch fire at all (ULTRAKILL's levels are in Minecraft daylight).
- Minecraft can't plan mob paths over ULTRAKILL's levels, so Killcraft walks fighting mobs straight at their enemy: they can't find their way around walls or up to other floors. The warden, piglins, hoglins and breezes aren't walked (only set on the enemy).
- Items held by mobs (a skeleton's bow) can show white.

## Building from source

Needs the .NET SDK (6 or newer) and ULTRAKILL with BepInEx installed.

```
dotnet build -c Release
```

If ULTRAKILL isn't in `C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL`, add `-p:GameDir="X:\path\to\ULTRAKILL"`. The build copies `Killcraft.dll` into `BepInEx\plugins\Killcraft\`.

The code: `Link.cs`/`Proto.cs` speak SkyCraft's shared-memory protocol, `Host.cs` hands V1 to Minecraft and back, `Collision.cs` streams ULTRAKILL's level geometry to Minecraft, `Combat.cs` mirrors enemies and applies hits, `WorldRender.cs` draws Minecraft's blocks, items and entities, `Overlay.cs` shows Minecraft's HUD, `InputForward.cs` sends the keyboard and mouse to Minecraft, `Nether.cs` makes ULTRAKILL look like the Nether while you're there, `NetherWorld.cs` gives the Nether's terrain collision, a navmesh and ULTRAKILL enemies, `McAudio.cs` mutes Minecraft while paused, and `McSave.cs` prepares Minecraft's world (and writes Killcraft's data pack) before it starts.

## Credits

- [SkyCraft](https://github.com/chasmlol/SkyCraft) by chasmlol (MIT): the Minecraft mod, the link protocol and the idea. Killcraft is the ULTRAKILL side of it. The release zip includes SkyCraft's unmodified `SkyCraft-Minecraft.zip` with its license (`SkyCraft-LICENSE.txt`) and third-party notices (`SkyCraft-THIRD-PARTY-NOTICES.md`).
- ULTRAKILL by Arsi "Hakita" Patala / New Blood Interactive. Minecraft by Mojang Studios. Killcraft isn't affiliated with either and contains none of their files.

## License

[MIT](LICENSE)
