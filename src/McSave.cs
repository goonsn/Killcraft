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
                WriteDataPack(Path.Combine(world, "datapacks", "killcraft"), Plugin.TntFuseTicks.Value, Plugin.MobsFightEnemies.Value);
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
                string players = Path.Combine(world, "players", "data");
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
        //  - arrows stuck in ULTRAKILL's geometry drop when it moves away (a door opening): Minecraft
        //    only checks a stuck arrow when the block it is in changes, and ULTRAKILL's geometry isn't
        //    blocks. Remembering another block than the air it's in makes it check every tick (near
        //    the player only: far away the level's collision may not be loaded);
        //  - ender pearls that leave the part of the level Minecraft has collision for (around the
        //    player) would fly through walls and floors: they come back instead;
        //  - stand-ins standing in fire or lava burn (they don't move the way Minecraft's mobs do, so
        //    Minecraft never checks what they're standing in).
        // Minecraft enables new packs in the world folder by itself.
        private static void WriteDataPack(string dir, int fuse, bool mobsFight)
        {
            fuse = Math.Max(1, Math.Min(fuse, 32767));
            const string standIn = "@e[type=skycraft:skyrim_actor,sort=nearest,limit=1]";
            var files = new Dictionary<string, string>
            {
                ["pack.mcmeta"] = "{\n  \"pack\": {\n    \"description\": \"Killcraft: shorter TNT fuse, mobs fight ULTRAKILL's enemies\",\n    \"min_format\": 121,\n    \"max_format\": 999\n  }\n}\n",
                ["data/minecraft/tags/function/load.json"] = "{ \"values\": [\"killcraft:load\"] }\n",
                ["data/minecraft/tags/function/tick.json"] = "{ \"values\": [\"killcraft:tick\"" + Optional("arrows") + Optional("pearls") + Optional("burn") + Optional("spawn") + Optional("daylight") + (mobsFight ? Optional("mobs") : "") + "] }\n",
                // The poke is "generic" damage, which shouldn't shove the mob (it is already in vanilla's list).
                ["data/minecraft/tags/damage_type/no_knockback.json"] = "{ \"values\": [\"minecraft:generic\"] }\n",
                ["data/killcraft/tags/entity_type/fighters.json"] = EntityTag(MeleeFighters, RangedFighters, OtherFighters),
                ["data/killcraft/tags/entity_type/melee_fighters.json"] = EntityTag(MeleeFighters),
                ["data/killcraft/tags/entity_type/ranged_fighters.json"] = EntityTag(RangedFighters),
                ["data/killcraft/function/load.mcfunction"] =
                    "scoreboard objectives add killcraft_fuse dummy\n" +
                    "scoreboard objectives add killcraft_timer dummy\n" +
                    "scoreboard objectives add killcraft_chase dummy\n",
                ["data/killcraft/function/tick.mcfunction"] =
                    "execute as @e[type=minecraft:tnt,tag=!killcraft_fuse] store result score @s killcraft_fuse run data get entity @s fuse\n" +
                    $"execute as @e[type=minecraft:tnt,tag=!killcraft_fuse,scores={{killcraft_fuse={fuse + 1}..}}] run data modify entity @s fuse set value {fuse}s\n" +
                    "tag @e[type=minecraft:tnt,tag=!killcraft_fuse] add killcraft_fuse\n",
                ["data/killcraft/function/arrows.mcfunction"] =
                    "execute at @a as @e[type=#killcraft:sticking,distance=..32,nbt={inGround:1b,inBlockState:{Name:\"minecraft:air\"}}] " +
                    "run data modify entity @s inBlockState set value {Name:\"minecraft:structure_void\"}\n" +
                    "execute as @e[type=#killcraft:sticking,nbt={inBlockState:{Name:\"minecraft:structure_void\"}}] at @s unless entity @a[distance=..40] " +
                    "run data modify entity @s inBlockState set value {Name:\"minecraft:air\"}\n",
                ["data/killcraft/function/pearls.mcfunction"] =
                    "execute as @e[type=minecraft:ender_pearl] at @s positioned ~-36 ~-200 ~-36 unless entity @a[dx=72,dy=220,dz=72] run function killcraft:pearl_back\n",
                ["data/killcraft/function/burn.mcfunction"] =
                    "execute as @e[type=skycraft:skyrim_actor] at @s positioned ~ ~0.2 ~ if block ~ ~ ~ #minecraft:fire run damage @s 1 minecraft:in_fire\n" +
                    "execute as @e[type=skycraft:skyrim_actor] at @s positioned ~ ~0.2 ~ if block ~ ~ ~ minecraft:lava run damage @s 4 minecraft:lava\n",
                ["data/killcraft/function/spawn.mcfunction"] =
                    "scoreboard players add #spawn killcraft_timer 1\n" +
                    "execute if score #spawn killcraft_timer matches 20.. as @a[nbt={OnGround:1b}] at @s run spawnpoint @s ~ ~ ~\n" +
                    "execute if score #spawn killcraft_timer matches 20.. run scoreboard players set #spawn killcraft_timer 0\n",
                // ULTRAKILL's levels are in Minecraft daylight: zombies and skeletons would burn (and a
                // burning zombie sets what it hits alight, V1 too). Mobs that burn in daylight don't
                // catch fire at all ("burning time" 0); fire and lava still hurt them.
                ["data/killcraft/function/daylight.mcfunction"] =
                    "execute as @e[type=#minecraft:burn_in_daylight,tag=!killcraft_fireproof] run attribute @s minecraft:burning_time base set 0\n" +
                    "tag @e[type=#minecraft:burn_in_daylight,tag=!killcraft_fireproof] add killcraft_fireproof\n",
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
                    "execute as @e[type=#killcraft:fighters,tag=!killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,distance=..24] run function killcraft:provoke\n" +
                    // Close enough is measured sideways (a box reaching high and low): an enemy flying
                    // above would otherwise draw its mob right underneath it.
                    "execute as @e[type=#killcraft:melee_fighters,tag=killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,distance=..24] " +
                    "positioned ~-1 ~-6 ~-1 unless entity @e[type=skycraft:skyrim_actor,dx=2,dy=12,dz=2] positioned as @s run function killcraft:chase\n" +
                    "execute as @e[type=#killcraft:ranged_fighters,tag=killcraft_provoked] at @s if entity @e[type=skycraft:skyrim_actor,distance=..24] " +
                    "positioned ~-7 ~-16 ~-7 unless entity @e[type=skycraft:skyrim_actor,dx=14,dy=32,dz=14] positioned as @s run function killcraft:chase\n",
                // Walking a mob to the nearest stand-in: its sideways speed set straight at it, about a
                // zombie's chasing pace (gravity and collision as usual). Its own AI turns it to face it.
                ["data/killcraft/function/chase.mcfunction"] =
                    "execute store result score #x killcraft_chase run data get entity @s Pos[0] 100\n" +
                    "execute store result score #z killcraft_chase run data get entity @s Pos[2] 100\n" +
                    $"execute store result score #dx killcraft_chase run data get entity {standIn} Pos[0] 100\n" +
                    $"execute store result score #dz killcraft_chase run data get entity {standIn} Pos[2] 100\n" +
                    "scoreboard players operation #dx killcraft_chase -= #x killcraft_chase\n" +
                    "scoreboard players operation #dz killcraft_chase -= #z killcraft_chase\n" +
                    "execute if score #dx killcraft_chase matches 30.. run data modify entity @s Motion[0] set value 0.18d\n" +
                    "execute if score #dx killcraft_chase matches ..-30 run data modify entity @s Motion[0] set value -0.18d\n" +
                    "execute if score #dz killcraft_chase matches 30.. run data modify entity @s Motion[2] set value 0.18d\n" +
                    "execute if score #dz killcraft_chase matches ..-30 run data modify entity @s Motion[2] set value -0.18d\n",
                ["data/killcraft/function/provoke.mcfunction"] =
                    $"damage @s 0 minecraft:generic by {standIn}\n" +
                    "tag @s add killcraft_provoked\n",
            };
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
