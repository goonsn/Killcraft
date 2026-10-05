using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Killcraft
{
    // Small fixes to SkyCraft's Minecraft world, made while Minecraft is closed (just before it is
    // started):
    //  - respawn radius 0: the world's spawn is in the void, so there's no safe ground around it to
    //    search for (Host walks the respawned player back to V1);
    //  - TNT and flint and steel, once per player, and a data pack with a shorter TNT fuse;
    //  - SkyCraft's digging into the host's geometry off (ULTRAKILL can't show the holes).
    internal static class McSave
    {
        private const string KitStamp = "killcraft-tnt-kit.txt";

        public static void Prepare(string installDir)
        {
            string game = Path.Combine(installDir, "Prism", "instances", "SkyCraft", ".minecraft");
            try
            {
                SetDestruction(Path.Combine(game, "config", "skycraft.properties"), Plugin.DigIntoLevels.Value);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Minecraft: couldn't update skycraft.properties: {e.Message}");
            }
            string world = Path.Combine(game, "saves", "SkyCraft");
            if (!Directory.Exists(world))
            {
                return;  // made on Minecraft's first start; fixed on the next
            }
            try
            {
                WriteDataPack(Path.Combine(world, "datapacks", "killcraft"), Plugin.TntFuseTicks.Value, Plugin.MobsFightEnemies.Value, Plugin.AlwaysThorns.Value,
                    !Plugin.MobsAttackYouInLevels.Value);
                string oldPack = Path.Combine(world, "datapacks", "ultracraft");  // this mod's name before 0.1.0
                if (Directory.Exists(oldPack))
                {
                    Directory.Delete(oldPack, true);
                }
                string rules = Path.Combine(world, "data", "minecraft", "game_rules.dat");
                if (File.Exists(rules))
                {
                    Edit(rules, root =>
                    {
                        Nbt data = root.Get("data");
                        // Killcraft 0.1.0-0.1.2 turned command messages off (it typed /spawnpoint itself;
                        // now its data pack does that silently): back on, so players' commands answer.
                        bool changed = false;
                        Nbt feedback = data?.Get("minecraft:send_command_feedback");
                        if (feedback != null && feedback.Type == Nbt.TByte && (sbyte)feedback.Value == 0)
                        {
                            feedback.Value = (sbyte)1;
                            changed = true;
                        }
                        Nbt radius = data?.Get("minecraft:respawn_radius");
                        if (radius == null || radius.Type != Nbt.TInt || (int)radius.Value == 0)
                        {
                            return changed;
                        }
                        radius.Value = 0;
                        Plugin.Log.LogInfo("Minecraft: respawn radius set to 0");
                        return true;
                    });
                }
                string gen = Path.Combine(world, "data", "minecraft", "world_gen_settings.dat");
                if (File.Exists(gen))
                {
                    Edit(gen, root => AddNether(root, Plugin.FreshWorld.Value));
                }
                string players = Path.Combine(world, "players", "data");
                // A player saved in the Nether starts in the level again (Host walks it to V1).
                if (Directory.Exists(players))
                {
                    foreach (string file in Directory.GetFiles(players, "*.dat"))
                    {
                        Edit(file, OutOfTheNether);
                    }
                }
                if (Plugin.FreshWorld.Value)
                {
                    FreshStart(world, players);
                    return;
                }
                OncePerPlayer(players, Path.Combine(world, KitStamp), GiveTnt);
                // Tools lost to the void (dropped while the player was stuck under the world).
                OncePerPlayer(players, Path.Combine(world, "killcraft-tools-back.txt"), GiveToolsBack);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Minecraft: couldn't update the world's settings: {e.Message}");
            }
        }

        // SkyCraft's world is its void Overworld alone, so portals lead nowhere: Minecraft's own Nether is
        // added, with its fortresses and bastions (the void Overworld has no structures either way).
        // A fresh session gets a new Nether too (FreshStart clears its saved chunks).
        private static bool AddNether(Nbt root, bool fresh)
        {
            Nbt data = root.Get("data");
            Nbt dims = data?.Get("dimensions");
            if (dims == null || dims.Type != Nbt.TCompound)
            {
                return false;
            }
            bool changed = false;
            if (dims.Get("minecraft:the_nether") == null)
            {
                var biomes = Nbt.Compound();
                biomes.Set("type", new Nbt(Nbt.TString, "minecraft:multi_noise"));
                biomes.Set("preset", new Nbt(Nbt.TString, "minecraft:nether"));
                var generator = Nbt.Compound();
                generator.Set("type", new Nbt(Nbt.TString, "minecraft:noise"));
                generator.Set("settings", new Nbt(Nbt.TString, "minecraft:nether"));
                generator.Set("biome_source", biomes);
                var nether = Nbt.Compound();
                nether.Set("type", new Nbt(Nbt.TString, "minecraft:the_nether"));
                nether.Set("generator", generator);
                dims.Set("minecraft:the_nether", nether);
                Plugin.Log.LogInfo("Minecraft: the world gets a Nether");
                changed = true;
            }
            if (data.Get("generate_structures") is Nbt structures && structures.Type == Nbt.TByte && (sbyte)structures.Value == 0)
            {
                structures.Value = (sbyte)1;
                changed = true;
            }
            if (fresh && data.Get("seed") is Nbt seed && seed.Type == Nbt.TLong)
            {
                var random = new Random();
                seed.Value = ((long)random.Next() << 32) ^ (uint)random.Next();
                changed = true;
            }
            return changed;
        }

        private static bool OutOfTheNether(Nbt root)
        {
            if (root.Get("Dimension") is Nbt dim && dim.Type == Nbt.TString && (string)dim.Value != "minecraft:overworld")
            {
                dim.Value = "minecraft:overworld";
                Plugin.Log.LogInfo("Minecraft: the player was saved in the Nether; it starts in the level");
                return true;
            }
            return false;
        }

        // SkyCraft's "Skyrim destruction" setting (its pause menu button): mining and explosions dig
        // into the host's geometry. ULTRAKILL can't cut holes in its levels, so it's off by default.
        private static void SetDestruction(string file, bool on)
        {
            var lines = new List<string>(File.Exists(file) ? File.ReadAllLines(file) : new[] { "# SkyCraft" });
            string line = "destruction=" + (on ? "true" : "false");
            int at = lines.FindIndex(l => l.Trim().StartsWith("destruction="));
            if (at >= 0 && lines[at].Trim() == line)
            {
                return;
            }
            if (at >= 0)
            {
                lines[at] = line;
            }
            else
            {
                lines.Add("# Mining and explosions dig into Skyrim's world (the pause menu's \"Skyrim destruction\" button).");
                lines.Add(line);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllLines(file, lines);
            Plugin.Log.LogInfo($"Minecraft: digging into ULTRAKILL's levels {(on ? "on" : "off")}");
        }

        // Mobs that fight back when hurt (their "hurt by" targeting), so a poke from an enemy's
        // stand-in sets them on it. Ones a Minecraft version doesn't have are skipped.
        // Minecraft can't plan a mob's way over ULTRAKILL's geometry (it isn't blocks), so the data pack
        // walks them there itself: melee mobs up to the enemy, ranged ones to within shooting range.
        // The rest (flying mobs; the warden, piglins, hoglins and breezes, whose "brain" AI would be
        // reset by it) are only set on the enemy.
        private static readonly string[] MeleeFighters =
        {
            "zombie", "husk", "drowned", "zombie_villager", "zombified_piglin", "wither_skeleton", "spider",
            "cave_spider", "creeper", "iron_golem", "wolf", "vindicator", "ravager", "enderman", "endermite",
            "silverfish", "polar_bear",
        };
        private static readonly string[] RangedFighters =
        {
            "skeleton", "stray", "bogged", "pillager", "evoker", "witch", "blaze", "llama", "trader_llama",
        };
        private static readonly string[] OtherFighters =
        {
            "piglin", "piglin_brute", "hoglin", "zoglin", "warden", "breeze", "bee", "vex",
        };

        private static string EntityTag(params string[][] lists)
        {
            var values = new StringBuilder();
            foreach (string[] list in lists)
            {
                foreach (string mob in list)
                {
                    values.Append(values.Length == 0 ? "" : ",\n").Append($"    {{ \"id\": \"minecraft:{mob}\", \"required\": false }}");
                }
            }
            return "{\n  \"values\": [\n" + values + "\n  ]\n}\n";
        }

        // A function tag entry that, if its function doesn't load, is skipped instead of failing the
        // whole tag (and with it the TNT fuse).
        private static string Optional(string function) => $", {{ \"id\": \"killcraft:{function}\", \"required\": false }}";

        // A data pack with what Minecraft has no setting for:
        //  - lit TNT gets at most `fuse` ticks;
        //  - the spawn point follows the player (where it last stood, checked every second), so
        //    respawning is a short move (from far away, Minecraft's server freezes for a long time);
        //  - mobs near an ULTRAKILL enemy go after it: its stand-in (SkyCraft's skyrim_actor, whose hurts
        //    become ULTRAKILL damage) gives them a harmless poke, and they fight back. Again every 5
        //    seconds, so they move on to the next enemy when theirs dies. See MeleeFighters;
        //  - arrows stuck in ULTRAKILL's geometry stay stuck when it moves (Killcraft draws them moving
        //    with it), undoing what older versions changed (see arrows);
        //  - ender pearls that leave the part of the level Minecraft has collision for (around the
        //    player) would fly through walls and floors: they come back instead;
        //  - stand-ins standing in fire or lava burn (they don't move the way Minecraft's mobs do, so
        //    Minecraft never checks what they're standing in);
        //  - Nether portals take the player to that level's own part of the Nether (see portals);
        //  - mobs leave the player alone in the levels, but not in the Nether (see allies).
        // Minecraft enables new packs in the world folder by itself.
        private static void WriteDataPack(string dir, int fuse, bool mobsFight, bool alwaysThorns, bool friendlyMobs)
        {
            fuse = Math.Max(1, Math.Min(fuse, 32767));
            const string standIn = "@e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",sort=nearest,limit=1]";
            var files = new Dictionary<string, string>
            {
                ["pack.mcmeta"] = "{\n  \"pack\": {\n    \"description\": \"Killcraft: shorter TNT fuse, mobs fight ULTRAKILL's enemies\",\n    \"min_format\": 121,\n    \"max_format\": 999\n  }\n}\n",
                ["data/minecraft/tags/function/load.json"] = "{ \"values\": [\"killcraft:load\"" + Optional("nether_setup") + Optional("allies_setup") + "] }\n",
                ["data/minecraft/tags/function/tick.json"] = "{ \"values\": [\"killcraft:tick\"" + Optional("arrows") + Optional("pearls") + Optional("burn") + Optional("spawn") + Optional("daylight") + Optional("feedback") + Optional("portals") + Optional("nether_mobs") + Optional("thorns_watch") + Optional("arrow_pickup") + (friendlyMobs ? Optional("allies") : "") + (mobsFight ? Optional("mobs") : "") + "] }\n",
                // The poke is "generic" damage, which shouldn't shove the mob (it is already in vanilla's list).
                ["data/minecraft/tags/damage_type/no_knockback.json"] = "{ \"values\": [\"minecraft:generic\"] }\n",
                ["data/killcraft/tags/entity_type/fighters.json"] = EntityTag(MeleeFighters, RangedFighters, OtherFighters),
                ["data/killcraft/tags/entity_type/melee_fighters.json"] = EntityTag(MeleeFighters),
                ["data/killcraft/tags/entity_type/ranged_fighters.json"] = EntityTag(RangedFighters),
                ["data/killcraft/function/load.mcfunction"] =
                    "scoreboard objectives add killcraft_fuse dummy\n" +
                    "scoreboard objectives add killcraft_timer dummy\n" +
                    "scoreboard objectives add killcraft_chase dummy\n" +
                    "scoreboard objectives add killcraft_hurt dummy\n" +
                    "scoreboard objectives add killcraft_hurt0 dummy\n",
                ["data/killcraft/function/nether_setup.mcfunction"] =
                    "scoreboard objectives add killcraft_portal dummy\n" +
                    "scoreboard objectives add killcraft_wait dummy\n" +
                    "scoreboard objectives add killcraft_ox dummy\n" +
                    "scoreboard objectives add killcraft_oy dummy\n" +
                    "scoreboard objectives add killcraft_oz dummy\n" +
                    "scoreboard objectives add killcraft_gx dummy\n" +
                    "scoreboard objectives add killcraft_gy dummy\n" +
                    "scoreboard objectives add killcraft_gz dummy\n" +
                    "scoreboard objectives add killcraft_nx dummy\n" +
                    "scoreboard objectives add killcraft_nz dummy\n" +
                    "scoreboard objectives add killcraft_ny dummy\n" +
                    "scoreboard players set #800 killcraft_timer 800\n" +
                    $"scoreboard players set #nether killcraft_timer {(int)Coords.NetherX}\n" +
                    // Players go through portals by Killcraft's portals function, not Minecraft's own trip.
                    "gamerule minecraft:players_nether_portal_default_delay 2147483647\n" +
                    "gamerule minecraft:players_nether_portal_creative_delay 2147483647\n",
                ["data/killcraft/function/tick.mcfunction"] =
                    "execute as @e[type=minecraft:tnt,tag=!killcraft_fuse] store result score @s killcraft_fuse run data get entity @s fuse\n" +
                    $"execute as @e[type=minecraft:tnt,tag=!killcraft_fuse,scores={{killcraft_fuse={fuse + 1}..}}] run data modify entity @s fuse set value {fuse}s\n" +
                    "tag @e[type=minecraft:tnt,tag=!killcraft_fuse] add killcraft_fuse\n",
                // (Killcraft 0.1.4-0.1.5 made arrows in ULTRAKILL's geometry check every tick whether it
                // was still there, so they'd drop when a door opened. Now Killcraft draws them moving with
                // the door (WorldRender pins them), so they stay stuck as Minecraft has them: undone.)
                // Arrows on ULTRAKILL's corpses and floors are only Killcraft's drawing; each is also a
                // tiny stand-in named "Killcraft arrow" (WorldRender.LooseArrows). A player walking up to
                // one gets the arrow (not in creative, as Minecraft's own), and the stand-in is poked
                // with Killcraft's own damage amount so Killcraft takes the arrow away.
                ["data/killcraft/function/arrow_pickup.mcfunction"] =
                    $"execute as @e[type=skycraft:skyrim_actor,name=\"{WorldRender.LooseArrowName}\",tag=!killcraft_picked] at @s " +
                    "if entity @a[distance=..2,gamemode=!spectator] run function killcraft:arrow_picked\n",
                ["data/killcraft/function/arrow_picked.mcfunction"] =
                    "tag @s add killcraft_picked\n" +
                    "give @p[distance=..2,gamemode=!spectator,gamemode=!creative] minecraft:arrow\n" +
                    "playsound minecraft:entity.item.pickup player @a ~ ~ ~ 0.2 2\n" +
                    $"damage @s {Combat.PickupPoke.ToString(System.Globalization.CultureInfo.InvariantCulture)} minecraft:generic\n",
                ["data/killcraft/function/arrows.mcfunction"] =
                    "execute as @e[type=#killcraft:sticking,nbt={inBlockState:{Name:\"minecraft:structure_void\"}}] " +
                    "run data modify entity @s inBlockState set value {Name:\"minecraft:air\"}\n",
                ["data/killcraft/function/pearls.mcfunction"] =
                    "execute as @e[type=minecraft:ender_pearl] at @s positioned ~-36 ~-200 ~-36 unless entity @a[dx=72,dy=220,dz=72] run function killcraft:pearl_back\n",
                ["data/killcraft/function/burn.mcfunction"] =
                    "execute as @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\"] at @s positioned ~ ~0.2 ~ if block ~ ~ ~ #minecraft:fire run damage @s 1 minecraft:in_fire\n" +
                    "execute as @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\"] at @s positioned ~ ~0.2 ~ if block ~ ~ ~ minecraft:lava run damage @s 4 minecraft:lava\n",
                ["data/killcraft/function/spawn.mcfunction"] =
                    "scoreboard players add #spawn killcraft_timer 1\n" +
                    "execute if score #spawn killcraft_timer matches 20.. as @a[nbt={OnGround:1b}] at @s if dimension minecraft:overworld run function killcraft:spawn_here\n" +
                    "execute if score #spawn killcraft_timer matches 20.. run scoreboard players set #spawn killcraft_timer 0\n",
                // (Also where a player who somehow got to the Nether without a portal goes back to.)
                ["data/killcraft/function/spawn_here.mcfunction"] =
                    "spawnpoint @s ~ ~ ~\n" +
                    "execute store result score @s killcraft_gx run data get entity @s Pos[0] 100\n" +
                    "execute store result score @s killcraft_gy run data get entity @s Pos[1] 100\n" +
                    "execute store result score @s killcraft_gz run data get entity @s Pos[2] 100\n",
                // Nether portals. Minecraft's own trip would put the player at x/8, z/8 in the Nether, a
                // place that can't be told apart from a level's area. Instead, standing in a portal for 3
                // seconds (half a second in creative) sends the player to (NetherX + x/8, z/8) in the
                // Nether, onto the first floor from y 120 down, with a portal built next to it; any
                // Nether portal sends the player back to the portal they came through. Killcraft sees
                // the player at NetherX and shows the Nether (Coords).
                ["data/killcraft/function/portals.mcfunction"] =
                    "execute as @a at @s unless block ~ ~ ~ minecraft:nether_portal run scoreboard players set @s killcraft_portal 0\n" +
                    "execute as @a at @s if block ~ ~ ~ minecraft:nether_portal run scoreboard players add @s killcraft_portal 1\n" +
                    "execute as @a[tag=killcraft_travelling] run function killcraft:portal_wait\n" +
                    "execute as @a[gamemode=!creative,tag=!killcraft_travelling,scores={killcraft_portal=60..}] at @s run function killcraft:portal_go\n" +
                    "execute as @a[gamemode=creative,tag=!killcraft_travelling,scores={killcraft_portal=10..}] at @s run function killcraft:portal_go\n" +
                    // In the wrong world's place (something teleported the player after the trip by the
                    // other world's coordinates): back to where the trip ended. Never otherwise: the
                    // levels are within +-62000, a level's Nether is past NetherX - 10000.
                    $"execute in minecraft:overworld as @a[x={(int)Coords.NetherX - 10000},y=-2048,z=-1000000,dx=1000000,dy=4096,dz=2000000] run function killcraft:to_overworld\n" +
                    $"execute in minecraft:the_nether as @a[x=-1000000,y=-2048,z=-1000000,dx={1000000 + (int)Coords.NetherX - 10000},dy=4096,dz=2000000,scores={{killcraft_ny=-1000..}}] run function killcraft:back_to_landing\n",
                ["data/killcraft/function/back_to_landing.mcfunction"] =
                    "execute store result storage killcraft:travel x int 1 run scoreboard players get @s killcraft_nx\n" +
                    "execute store result storage killcraft:travel y int 1 run scoreboard players get @s killcraft_ny\n" +
                    "execute store result storage killcraft:travel z int 1 run scoreboard players get @s killcraft_nz\n" +
                    "function killcraft:nether_tp with storage killcraft:travel\n",
                ["data/killcraft/function/nether_tp.mcfunction"] =
                    "$execute in minecraft:the_nether run tp @s $(x).5 $(y) $(z).5\n",
                // (The score stays below zero until the player has stepped out of the portal.)
                ["data/killcraft/function/portal_go.mcfunction"] =
                    "scoreboard players set @s killcraft_portal -1000000\n" +
                    "execute if dimension minecraft:the_nether run return run function killcraft:to_overworld\n" +
                    "execute if dimension minecraft:overworld run function killcraft:to_nether\n",
                ["data/killcraft/function/to_nether.mcfunction"] =
                    "execute store result score @s killcraft_ox run data get entity @s Pos[0] 100\n" +
                    "execute store result score @s killcraft_oy run data get entity @s Pos[1] 100\n" +
                    "execute store result score @s killcraft_oz run data get entity @s Pos[2] 100\n" +
                    "scoreboard players operation @s killcraft_nx = @s killcraft_ox\n" +
                    "scoreboard players operation @s killcraft_nx /= #800 killcraft_timer\n" +
                    "scoreboard players operation @s killcraft_nx += #nether killcraft_timer\n" +
                    "scoreboard players operation @s killcraft_nz = @s killcraft_oz\n" +
                    "scoreboard players operation @s killcraft_nz /= #800 killcraft_timer\n" +
                    "scoreboard players set @s killcraft_wait 0\n" +
                    "tag @s add killcraft_travelling\n" +
                    "execute store result storage killcraft:travel x int 1 run scoreboard players get @s killcraft_nx\n" +
                    "execute store result storage killcraft:travel z int 1 run scoreboard players get @s killcraft_nz\n" +
                    "function killcraft:nether_load with storage killcraft:travel\n",
                ["data/killcraft/function/nether_load.mcfunction"] =
                    "$execute in minecraft:the_nether run forceload add $(x) $(z)\n",
                // The player waits in the portal until that part of the Nether has loaded (10 seconds at most).
                ["data/killcraft/function/portal_wait.mcfunction"] =
                    "scoreboard players add @s killcraft_wait 1\n" +
                    "execute store result storage killcraft:travel x int 1 run scoreboard players get @s killcraft_nx\n" +
                    "execute store result storage killcraft:travel z int 1 run scoreboard players get @s killcraft_nz\n" +
                    "function killcraft:nether_try with storage killcraft:travel\n",
                ["data/killcraft/function/nether_try.mcfunction"] =
                    $"$execute in minecraft:the_nether positioned $(x).5 {Coords.NetherLandingY} $(z).5 if loaded ~ ~ ~ run return run function killcraft:nether_land\n" +
                    "$execute if score @s killcraft_wait matches 200.. in minecraft:the_nether run forceload remove $(x) $(z)\n" +
                    "execute if score @s killcraft_wait matches 200.. run tag @s remove killcraft_travelling\n",
                // An obsidian floor, room to stand and a lit portal back, the way Minecraft builds one,
                // always at the same height (Killcraft has to know where the player lands, see Host),
                // with any lava around it turned to netherrack.
                ["data/killcraft/function/nether_land.mcfunction"] =
                    "forceload remove ~ ~\n" +
                    "fill ~-2 ~-2 ~-2 ~3 ~4 ~3 minecraft:netherrack replace minecraft:lava\n" +
                    "fill ~-1 ~-1 ~-1 ~2 ~-1 ~2 minecraft:obsidian\n" +
                    "fill ~-1 ~ ~-1 ~2 ~2 ~1 minecraft:air\n" +
                    "fill ~-1 ~-1 ~2 ~2 ~3 ~2 minecraft:obsidian\n" +
                    "fill ~ ~ ~2 ~1 ~2 ~2 minecraft:air\n" +
                    "setblock ~ ~ ~2 minecraft:fire\n" +
                    "tp @s ~ ~ ~\n" +
                    "execute store result score @s killcraft_ny run data get entity @s Pos[1]\n" +
                    "tag @s remove killcraft_travelling\n" +
                    "playsound minecraft:block.portal.travel ambient @s ~ ~ ~ 0.25\n",
                ["data/killcraft/function/to_overworld.mcfunction"] =
                    "execute unless score @s killcraft_ox = @s killcraft_ox run scoreboard players operation @s killcraft_ox = @s killcraft_gx\n" +
                    "execute unless score @s killcraft_oy = @s killcraft_oy run scoreboard players operation @s killcraft_oy = @s killcraft_gy\n" +
                    "execute unless score @s killcraft_oz = @s killcraft_oz run scoreboard players operation @s killcraft_oz = @s killcraft_gz\n" +
                    "execute unless score @s killcraft_ox = @s killcraft_ox run scoreboard players set @s killcraft_ox 0\n" +
                    "execute unless score @s killcraft_oy = @s killcraft_oy run scoreboard players set @s killcraft_oy 10000\n" +
                    "execute unless score @s killcraft_oz = @s killcraft_oz run scoreboard players set @s killcraft_oz 0\n" +
                    "execute store result storage killcraft:travel x double 0.01 run scoreboard players get @s killcraft_ox\n" +
                    "execute store result storage killcraft:travel y double 0.01 run scoreboard players get @s killcraft_oy\n" +
                    "execute store result storage killcraft:travel z double 0.01 run scoreboard players get @s killcraft_oz\n" +
                    "function killcraft:overworld_tp with storage killcraft:travel\n",
                ["data/killcraft/function/overworld_tp.mcfunction"] =
                    "$execute in minecraft:overworld run tp @s $(x) $(y) $(z)\n" +
                    "playsound minecraft:block.portal.travel ambient @s ~ ~ ~ 0.25\n",
                // Typed by Killcraft when V1 is back in the level (a checkpoint after dying) but
                // Minecraft's player is still in the Nether.
                ["data/killcraft/function/leave_nether.mcfunction"] =
                    "execute if dimension minecraft:the_nether run function killcraft:to_overworld\n",
                // Mobs on the player's side in the levels: Minecraft's mobs never pick a target on their
                // own team (spawned and natural ones alike; ULTRAKILL enemies' stand-ins aren't on it, so
                // the mobs still go after those). Players are on it only in the Overworld (the levels): in
                // the Nether every mob is their enemy again. Every half second.
                ["data/killcraft/function/allies_setup.mcfunction"] = friendlyMobs
                    ? "team add killcraft\n"
                    : "team remove killcraft\n",
                ["data/killcraft/function/allies.mcfunction"] =
                    "scoreboard players add #allies killcraft_timer 1\n" +
                    "execute if score #allies killcraft_timer matches 10.. run function killcraft:allies_update\n",
                ["data/killcraft/function/allies_update.mcfunction"] =
                    "scoreboard players set #allies killcraft_timer 0\n" +
                    "execute as @a[team=!killcraft] at @s if dimension minecraft:overworld run team join killcraft @s\n" +
                    "execute as @a[team=killcraft] at @s unless dimension minecraft:overworld run team leave @s\n" +
                    "execute as @e[type=!minecraft:player,type=!skycraft:skyrim_actor,team=!killcraft,tag=!killcraft_notmob] run function killcraft:ally\n",
                // (Only mobs: items, arrows and the like are marked so they aren't looked at again.)
                ["data/killcraft/function/ally.mcfunction"] =
                    "execute if data entity @s Health run return run team join killcraft @s\n" +
                    "tag @s add killcraft_notmob\n",
                // Thorns against ULTRAKILL's enemies: Minecraft only runs it inside a mob's own attack,
                // and SkyCraft hurts the player directly with the stand-in as the attacker. So when a
                // stand-in has just hurt the player (its hurt time started again; a blocked hit doesn't
                // start it), each armour piece with thorns hits it back (Killcraft turns that into
                // ULTRAKILL damage, see Combat), every time with AlwaysThorns, else 15% of the time.
                // (Not an advancement: one Minecraft can't read stops the whole world from loading.)
                ["data/killcraft/function/thorns_watch.mcfunction"] =
                    "execute as @a store result score @s killcraft_hurt run data get entity @s HurtTime\n" +
                    "execute as @a if score @s killcraft_hurt > @s killcraft_hurt0 run function killcraft:thorns\n" +
                    "execute as @a run scoreboard players operation @s killcraft_hurt0 = @s killcraft_hurt\n",
                ["data/killcraft/function/thorns.mcfunction"] =
                    "execute if items entity @s armor.head *[minecraft:enchantments~[{enchantments:\"minecraft:thorns\"}]] run function killcraft:thorns_hit\n" +
                    "execute if items entity @s armor.chest *[minecraft:enchantments~[{enchantments:\"minecraft:thorns\"}]] run function killcraft:thorns_hit\n" +
                    "execute if items entity @s armor.legs *[minecraft:enchantments~[{enchantments:\"minecraft:thorns\"}]] run function killcraft:thorns_hit\n" +
                    "execute if items entity @s armor.feet *[minecraft:enchantments~[{enchantments:\"minecraft:thorns\"}]] run function killcraft:thorns_hit\n",
                ["data/killcraft/function/thorns_hit.mcfunction"] = (alwaysThorns ? "" : "execute unless predicate {condition:\"minecraft:random_chance\",chance:0.15} run return 0\n") +
                    "execute on attacker if entity @s[type=skycraft:skyrim_actor] run damage @s 3 minecraft:thorns\n",
                // SkyCraft turns mob spawning off whenever the world opens. The levels' void never
                // spawns anything (its one biome has no mobs), so on is only for the Nether.
                ["data/killcraft/function/nether_mobs.mcfunction"] =
                    "execute store result score #rule killcraft_timer run gamerule minecraft:spawn_mobs\n" +
                    "execute if score #rule killcraft_timer matches 0 run gamerule minecraft:spawn_mobs true\n" +
                    "execute store result score #rule killcraft_timer run gamerule minecraft:spawn_monsters\n" +
                    "execute if score #rule killcraft_timer matches 0 run gamerule minecraft:spawn_monsters true\n",
                // ULTRAKILL's levels are in Minecraft daylight: zombies and skeletons would burn (and a
                // burning zombie sets what it hits alight, V1 too). Mobs that burn in daylight don't
                // catch fire at all ("burning time" 0); fire and lava still hurt them.
                ["data/killcraft/function/daylight.mcfunction"] =
                    "execute as @e[type=#minecraft:burn_in_daylight,tag=!killcraft_fireproof] run attribute @s minecraft:burning_time base set 0\n" +
                    "tag @e[type=#minecraft:burn_in_daylight,tag=!killcraft_fireproof] add killcraft_fireproof\n",
                // Killcraft's pause: mute (typed just before /tick freeze) remembers whether command
                // messages are on and turns them off; unmute (typed after /tick unfreeze) asks for them
                // back, which happens on the next tick, after that /function's own message (unshown).
                ["data/killcraft/function/mute.mcfunction"] =
                    "execute store result score #feedback killcraft_timer run gamerule minecraft:send_command_feedback\n" +
                    "gamerule minecraft:send_command_feedback false\n",
                ["data/killcraft/function/unmute.mcfunction"] =
                    "scoreboard players set #unmute killcraft_timer 1\n",
                ["data/killcraft/function/feedback.mcfunction"] =
                    "execute if score #unmute killcraft_timer matches 1 if score #feedback killcraft_timer matches 1 run gamerule minecraft:send_command_feedback true\n" +
                    "execute if score #unmute killcraft_timer matches 1 run scoreboard players set #unmute killcraft_timer 0\n",
                ["data/killcraft/tags/entity_type/sticking.json"] =
                    "{ \"values\": [\"minecraft:arrow\", \"minecraft:spectral_arrow\", \"minecraft:trident\"] }\n",
                ["data/killcraft/function/pearl_back.mcfunction"] =
                    "execute on owner if entity @s[gamemode=!creative] run give @s minecraft:ender_pearl\n" +
                    "execute on owner run title @s actionbar \"Too far: that part of the level isn't loaded in Minecraft\"\n" +
                    "kill @s\n",
                ["data/killcraft/function/mobs.mcfunction"] =
                    "scoreboard players add #mobs killcraft_timer 1\n" +
                    "execute if score #mobs killcraft_timer matches 100.. run tag @e[tag=killcraft_provoked] remove killcraft_provoked\n" +
                    "execute if score #mobs killcraft_timer matches 100.. run scoreboard players set #mobs killcraft_timer 0\n" +
                    "execute as @e[type=#killcraft:fighters,tag=!killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",distance=..24] run function killcraft:provoke\n" +
                    // Close enough is measured sideways (a box reaching high and low): an enemy flying
                    // above would otherwise draw its mob right underneath it.
                    "execute as @e[type=#killcraft:melee_fighters,tag=killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",distance=..24] " +
                    "positioned ~-1 ~-6 ~-1 unless entity @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",dx=2,dy=12,dz=2] positioned as @s run function killcraft:chase\n" +
                    "execute as @e[type=#killcraft:ranged_fighters,tag=killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",distance=..24] " +
                    "positioned ~-7 ~-16 ~-7 unless entity @e[type=skycraft:skyrim_actor,name=!\"Killcraft arrow\",dx=14,dy=32,dz=14] positioned as @s run function killcraft:chase\n",
                // Walking a mob to the nearest stand-in: its sideways speed set straight at it, about a
                // zombie's chasing pace (gravity and collision as usual), and it turns to face it. The
                // direction comes from a marker summoned 0.2 blocks ahead of the mob, facing the
                // stand-in: the marker's position minus the mob's is the speed.
                ["data/killcraft/function/chase.mcfunction"] =
                    "execute store result score #x killcraft_chase run data get entity @s Pos[0] 1000\n" +
                    "execute store result score #z killcraft_chase run data get entity @s Pos[2] 1000\n" +
                    $"execute facing entity {standIn} feet rotated ~ 0 positioned ^ ^ ^0.2 summon minecraft:marker run function killcraft:chase_step\n" +
                    "scoreboard players operation #mx killcraft_chase -= #x killcraft_chase\n" +
                    "scoreboard players operation #mz killcraft_chase -= #z killcraft_chase\n" +
                    "execute store result entity @s Motion[0] double 0.001 run scoreboard players get #mx killcraft_chase\n" +
                    "execute store result entity @s Motion[2] double 0.001 run scoreboard players get #mz killcraft_chase\n" +
                    $"rotate @s facing entity {standIn} eyes\n",
                ["data/killcraft/function/chase_step.mcfunction"] =
                    "execute store result score #mx killcraft_chase run data get entity @s Pos[0] 1000\n" +
                    "execute store result score #mz killcraft_chase run data get entity @s Pos[2] 1000\n" +
                    "kill @s\n",
                ["data/killcraft/function/provoke.mcfunction"] =
                    $"damage @s 0 minecraft:generic by {standIn}\n" +
                    "tag @s add killcraft_provoked\n",
            };
            // Thorns hits back every time an enemy hurts the player, not 15% a level: Minecraft 26.3's
            // own thorns with only its chance taken out (an enchantment that doesn't load would stop
            // the whole world loading, so the rest is exactly Minecraft's).
            string thornsPath = Path.Combine(dir, "data", "minecraft", "enchantment", "thorns.json");
            if (alwaysThorns)
            {
                files["data/minecraft/enchantment/thorns.json"] =
                    "{\n" +
                    "  \"anvil_cost\": 8,\n" +
                    "  \"description\": {\n    \"translate\": \"enchantment.minecraft.thorns\"\n  },\n" +
                    "  \"effects\": {\n" +
                    "    \"minecraft:post_attack\": [\n" +
                    "      {\n" +
                    "        \"affected\": \"attacker\",\n" +
                    "        \"effect\": {\n" +
                    "          \"type\": \"minecraft:all_of\",\n" +
                    "          \"effects\": [\n" +
                    "            {\n              \"type\": \"minecraft:damage_entity\",\n              \"damage_type\": \"minecraft:thorns\",\n              \"max_damage\": 5.0,\n              \"min_damage\": 1.0\n            },\n" +
                    "            {\n              \"type\": \"minecraft:change_item_damage\",\n              \"amount\": 2.0\n            }\n" +
                    "          ]\n" +
                    "        },\n" +
                    "        \"enchanted\": \"victim\"\n" +
                    "      }\n" +
                    "    ]\n" +
                    "  },\n" +
                    "  \"max_cost\": {\n    \"base\": 60,\n    \"per_level_above_first\": 20\n  },\n" +
                    "  \"max_level\": 3,\n" +
                    "  \"min_cost\": {\n    \"base\": 10,\n    \"per_level_above_first\": 20\n  },\n" +
                    "  \"primary_items\": \"#minecraft:enchantable/chest_armor\",\n" +
                    "  \"slots\": [\n    \"any\"\n  ],\n" +
                    "  \"supported_items\": \"#minecraft:enchantable/armor\",\n" +
                    "  \"weight\": 1\n" +
                    "}\n";
            }
            else if (File.Exists(thornsPath))
            {
                File.Delete(thornsPath);
            }
            // Files earlier versions wrote that are gone now (a bad advancement stops the world loading).
            foreach (string stale in new[] { "data/killcraft/advancement/thorns.json", "data/killcraft/function/nether_find.mcfunction",
                "data/killcraft/function/nether_down.mcfunction", "data/killcraft/tags/block/nether_floor.json" })
            {
                string path = Path.Combine(dir, stale.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            foreach (var kv in files)
            {
                string path = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path) || File.ReadAllText(path) != kv.Value)
                {
                    File.WriteAllText(path, kv.Value);
                }
            }
        }

        // A fresh session: the world's blocks, entities and dug holes go (Minecraft makes new, empty
        // chunks of its void world), and every player gets the starting kit back.
        private static void FreshStart(string world, string players)
        {
            string dims = Path.Combine(world, "dimensions");
            int cleared = 0;
            if (Directory.Exists(dims))
            {
                foreach (string dir in Directory.GetDirectories(dims, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(dir);
                    if ((name == "region" || name == "entities" || name == "poi") && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                        cleared++;
                    }
                }
            }
            if (Directory.Exists(players))
            {
                foreach (string file in Directory.GetFiles(players, "*.dat"))
                {
                    Edit(file, StartingKit);
                }
            }
            Plugin.Log.LogInfo($"Minecraft: fresh world ({cleared} folders of saved blocks cleared, inventory reset to the starting kit)");
        }

        private static readonly (string id, int count)[] Kit =
        {
            ("diamond_sword", 1), ("diamond_pickaxe", 1), ("bow", 1), ("cooked_beef", 32), ("oak_planks", 64), ("tnt", 64),
            ("flint_and_steel", 1), ("cobblestone", 64), ("stone_bricks", 64), ("torch", 32), ("arrow", 64), ("arrow", 64),
            ("oak_log", 64), ("glass", 64), ("oak_stairs", 64), ("oak_slab", 64), ("oak_door", 8), ("ladder", 32),
            ("lantern", 16), ("crafting_table", 1), ("water_bucket", 1), ("golden_apple", 4),
        };

        private static Nbt Stack(string id, int count, int slot = -1)
        {
            var stack = Nbt.Compound();
            if (slot >= 0)
            {
                stack.Set("Slot", new Nbt(Nbt.TByte, (sbyte)slot));
            }
            stack.Set("id", new Nbt(Nbt.TString, "minecraft:" + id));
            stack.Set("count", new Nbt(Nbt.TInt, count));
            return stack;
        }

        private static bool StartingKit(Nbt root)
        {
            var items = new List<Nbt>();
            for (int i = 0; i < Kit.Length; i++)
            {
                items.Add(Stack(Kit[i].id, Kit[i].count, i));
            }
            root.Set("Inventory", new Nbt(Nbt.TList, items) { ListType = Nbt.TCompound });
            var equipment = Nbt.Compound();
            equipment.Set("head", Stack("iron_helmet", 1));
            equipment.Set("chest", Stack("iron_chestplate", 1));
            equipment.Set("legs", Stack("iron_leggings", 1));
            equipment.Set("feet", Stack("iron_boots", 1));
            equipment.Set("offhand", Stack("shield", 1));
            root.Set("equipment", equipment);
            if (root.Get("Health") is Nbt health && health.Type == Nbt.TFloat)
            {
                health.Value = 20f;
            }
            if (root.Get("foodLevel") is Nbt food && food.Type == Nbt.TInt)
            {
                food.Value = 20;
            }
            return true;
        }

        private static void OncePerPlayer(string players, string stampFile, Func<Nbt, bool> change)
        {
            if (!Directory.Exists(players))
            {
                return;
            }
            var done = new HashSet<string>(File.Exists(stampFile) ? File.ReadAllLines(stampFile) : new string[0]);
            foreach (string file in Directory.GetFiles(players, "*.dat"))
            {
                string uuid = Path.GetFileNameWithoutExtension(file);
                if (done.Contains(uuid))
                {
                    continue;
                }
                Edit(file, change);
                done.Add(uuid);
                File.WriteAllLines(stampFile, done);
            }
        }

        private static bool GiveToolsBack(Nbt root)
        {
            Nbt inventory = root.Get("Inventory");
            if (inventory == null || inventory.Type != Nbt.TList)
            {
                return false;
            }
            bool sword = false, pickaxe = false;
            foreach (Nbt item in (List<Nbt>)inventory.Value)
            {
                string id = item.Get("id")?.Value as string ?? "";
                sword |= id.EndsWith("_sword");
                pickaxe |= id.EndsWith("_pickaxe");
            }
            var missing = new List<(string, int)>();
            if (!sword)
            {
                missing.Add(("minecraft:diamond_sword", 1));
            }
            if (!pickaxe)
            {
                missing.Add(("minecraft:diamond_pickaxe", 1));
            }
            return Give(inventory, missing, "a diamond sword and pickaxe (lost to the void)");
        }

        private static bool GiveTnt(Nbt root)
        {
            Nbt inventory = root.Get("Inventory");
            if (inventory == null || inventory.Type != Nbt.TList)
            {
                return false;
            }
            return Give(inventory, new List<(string, int)> { ("minecraft:tnt", 64), ("minecraft:flint_and_steel", 1) }, "TNT and flint and steel");
        }

        // Puts stacks in the first free slots (hotbar first).
        private static bool Give(Nbt inventory, List<(string id, int count)> stacks, string what)
        {
            var items = (List<Nbt>)inventory.Value;
            var used = new HashSet<int>();
            foreach (Nbt item in items)
            {
                if (item.Get("Slot")?.Value is sbyte slotUsed)
                {
                    used.Add(slotUsed);
                }
            }
            int added = 0;
            foreach (var (id, count) in stacks)
            {
                int slot = 0;
                while (slot < 36 && used.Contains(slot))
                {
                    slot++;
                }
                if (slot >= 36)
                {
                    break;
                }
                used.Add(slot);
                var stack = Nbt.Compound();
                stack.Set("Slot", new Nbt(Nbt.TByte, (sbyte)slot));
                stack.Set("id", new Nbt(Nbt.TString, id));
                stack.Set("count", new Nbt(Nbt.TInt, count));
                items.Add(stack);
                added++;
            }
            if (added > 0)
            {
                inventory.ListType = Nbt.TCompound;
                Plugin.Log.LogInfo($"Minecraft: put {what} in the player's inventory");
            }
            return added > 0;
        }

        // Reads a gzipped NBT file, lets change() modify it and writes it back (keeping a backup).
        private static bool Edit(string path, Func<Nbt, bool> change)
        {
            Nbt root;
            string rootName;
            using (var input = new BinaryReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress)))
            {
                byte type = input.ReadByte();
                if (type != Nbt.TCompound)
                {
                    return false;
                }
                rootName = ReadString(input);
                root = ReadPayload(input, type);
            }
            if (!change(root))
            {
                return false;
            }
            File.Copy(path, path + ".killcraft-bak", true);
            string temp = path + ".killcraft-tmp";
            using (var output = new BinaryWriter(new GZipStream(File.Create(temp), CompressionMode.Compress)))
            {
                output.Write(Nbt.TCompound);
                WriteString(output, rootName);
                WritePayload(output, root);
            }
            File.Copy(temp, path, true);
            File.Delete(temp);
            return true;
        }

        // ---- NBT (big-endian) ----------------------------------------------------------------------

        internal sealed class Nbt
        {
            public const byte TEnd = 0, TByte = 1, TShort = 2, TInt = 3, TLong = 4, TFloat = 5, TDouble = 6, TByteArray = 7,
                TString = 8, TList = 9, TCompound = 10, TIntArray = 11, TLongArray = 12;

            public byte Type;
            public object Value;
            public byte ListType;  // TList: element type

            public Nbt(byte type, object value)
            {
                Type = type;
                Value = value;
            }

            public static Nbt Compound() => new Nbt(TCompound, new List<KeyValuePair<string, Nbt>>());

            public Nbt Get(string key)
            {
                if (Type != TCompound)
                {
                    return null;
                }
                foreach (var kv in (List<KeyValuePair<string, Nbt>>)Value)
                {
                    if (kv.Key == key)
                    {
                        return kv.Value;
                    }
                }
                return null;
            }

            public void Set(string key, Nbt value)
            {
                var entries = (List<KeyValuePair<string, Nbt>>)Value;
                entries.RemoveAll(kv => kv.Key == key);
                entries.Add(new KeyValuePair<string, Nbt>(key, value));
            }
        }

        private static byte[] Be(BinaryReader r, int n)
        {
            byte[] b = r.ReadBytes(n);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b);
            }
            return b;
        }

        private static string ReadString(BinaryReader r)
        {
            int len = BitConverter.ToUInt16(Be(r, 2), 0);
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        private static Nbt ReadPayload(BinaryReader r, byte type)
        {
            switch (type)
            {
                case Nbt.TByte: return new Nbt(type, r.ReadSByte());
                case Nbt.TShort: return new Nbt(type, BitConverter.ToInt16(Be(r, 2), 0));
                case Nbt.TInt: return new Nbt(type, BitConverter.ToInt32(Be(r, 4), 0));
                case Nbt.TLong: return new Nbt(type, BitConverter.ToInt64(Be(r, 8), 0));
                case Nbt.TFloat: return new Nbt(type, BitConverter.ToSingle(Be(r, 4), 0));
                case Nbt.TDouble: return new Nbt(type, BitConverter.ToDouble(Be(r, 8), 0));
                case Nbt.TByteArray:
                    return new Nbt(type, r.ReadBytes(BitConverter.ToInt32(Be(r, 4), 0)));
                case Nbt.TString: return new Nbt(type, ReadString(r));
                case Nbt.TList:
                    {
                        byte element = r.ReadByte();
                        int n = BitConverter.ToInt32(Be(r, 4), 0);
                        var list = new List<Nbt>(Math.Max(n, 0));
                        for (int i = 0; i < n; i++)
                        {
                            list.Add(ReadPayload(r, element));
                        }
                        return new Nbt(type, list) { ListType = element };
                    }
                case Nbt.TCompound:
                    {
                        var c = Nbt.Compound();
                        var entries = (List<KeyValuePair<string, Nbt>>)c.Value;
                        for (byte t = r.ReadByte(); t != Nbt.TEnd; t = r.ReadByte())
                        {
                            string key = ReadString(r);
                            entries.Add(new KeyValuePair<string, Nbt>(key, ReadPayload(r, t)));
                        }
                        return c;
                    }
                case Nbt.TIntArray:
                    {
                        var a = new int[BitConverter.ToInt32(Be(r, 4), 0)];
                        for (int i = 0; i < a.Length; i++)
                        {
                            a[i] = BitConverter.ToInt32(Be(r, 4), 0);
                        }
                        return new Nbt(type, a);
                    }
                case Nbt.TLongArray:
                    {
                        var a = new long[BitConverter.ToInt32(Be(r, 4), 0)];
                        for (int i = 0; i < a.Length; i++)
                        {
                            a[i] = BitConverter.ToInt64(Be(r, 8), 0);
                        }
                        return new Nbt(type, a);
                    }
                default:
                    throw new InvalidDataException($"unknown NBT tag type {type}");
            }
        }

        private static void WriteBe(BinaryWriter w, byte[] b)
        {
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b);
            }
            w.Write(b);
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            WriteBe(w, BitConverter.GetBytes((ushort)bytes.Length));
            w.Write(bytes);
        }

        private static void WritePayload(BinaryWriter w, Nbt n)
        {
            switch (n.Type)
            {
                case Nbt.TByte: w.Write((sbyte)n.Value); break;
                case Nbt.TShort: WriteBe(w, BitConverter.GetBytes((short)n.Value)); break;
                case Nbt.TInt: WriteBe(w, BitConverter.GetBytes((int)n.Value)); break;
                case Nbt.TLong: WriteBe(w, BitConverter.GetBytes((long)n.Value)); break;
                case Nbt.TFloat: WriteBe(w, BitConverter.GetBytes((float)n.Value)); break;
                case Nbt.TDouble: WriteBe(w, BitConverter.GetBytes((double)n.Value)); break;
                case Nbt.TByteArray:
                    {
                        var b = (byte[])n.Value;
                        WriteBe(w, BitConverter.GetBytes(b.Length));
                        w.Write(b);
                        break;
                    }
                case Nbt.TString: WriteString(w, (string)n.Value); break;
                case Nbt.TList:
                    {
                        var list = (List<Nbt>)n.Value;
                        w.Write(list.Count == 0 ? Nbt.TEnd : n.ListType);
                        WriteBe(w, BitConverter.GetBytes(list.Count));
                        foreach (Nbt e in list)
                        {
                            WritePayload(w, e);
                        }
                        break;
                    }
                case Nbt.TCompound:
                    foreach (var kv in (List<KeyValuePair<string, Nbt>>)n.Value)
                    {
                        w.Write(kv.Value.Type);
                        WriteString(w, kv.Key);
                        WritePayload(w, kv.Value);
                    }
                    w.Write(Nbt.TEnd);
                    break;
                case Nbt.TIntArray:
                    {
                        var a = (int[])n.Value;
                        WriteBe(w, BitConverter.GetBytes(a.Length));
                        foreach (int v in a)
                        {
                            WriteBe(w, BitConverter.GetBytes(v));
                        }
                        break;
                    }
                case Nbt.TLongArray:
                    {
                        var a = (long[])n.Value;
                        WriteBe(w, BitConverter.GetBytes(a.Length));
                        foreach (long v in a)
                        {
                            WriteBe(w, BitConverter.GetBytes(v));
                        }
                        break;
                    }
            }
        }
    }
}
