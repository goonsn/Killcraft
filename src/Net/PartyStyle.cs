using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Killcraft
{
    // CO-OP's party style meter: one style meter the whole party fills, in place of ULTRAKILL's own
    // (which still counts each player's own style for their level's rank, only unseen). Everything that
    // gives style (StyleHUD.AddPoints: a kill, a ricoshot, a parry...) and takes it (getting hurt,
    // RemovePoints) goes to the others in the level; each game runs the meter as ULTRAKILL's does - it
    // drains, a full rank goes up one, an empty one down - with every player's points, the ranks a bit
    // bigger for each player there; the one running the enemies says where it stands, and the others
    // keep to that. Shown in ULTRAKILL's own style meter (PartyStyleHud). Not in VERSUS, nor MINOS PRIME.
    [HarmonyPatch]
    internal static class PartyStyle
    {
        public const byte PointsMessage = 65, StateMessage = 66;

        public static readonly (string Letter, string Word)[] Ranks =
        {
            ("D", "EMOLITION"), ("C", "ARNAGE"), ("B", "LOODBATH"), ("A", "NNIHILATION"),
            ("S", "LAUGHTERHOUSE"), ("SS", "YNCHRONIZED"), ("SSS", "UPERMASSACRE"), ("U", "LTRAPARTY"),
        };

        public sealed class Item
        {
            public int Id;
            public string Text;
            public float At;
        }

        public static bool Active { get; private set; }
        public static int Rank { get; private set; }
        public static bool Combo { get; private set; }
        public static float Fill => Mathf.Clamp01(meter / Max(Rank));
        public static readonly List<Item> Items = new List<Item>();
        // Each player's share of what's filled it lately (their points, fading over a few seconds).
        public static readonly Dictionary<int, float> Shares = new Dictionary<int, float>();
        public static int Players { get; private set; } = 1;

        private static float meter;
        private static float sentAt = -100f, heardAt = -100f;
        private static string scene = "";

        // Whether it's shown: any co-op level (alone in it too: the party's ranks all the same), not VERSUS
        // nor MINOS PRIME, nor a replay or someone else's eyes.
        private static bool Wanted(bool inLevel) =>
            inLevel && Plugin.PartyStyleOn.Value && !Versus.On && !MinosMode.Active && !ReplayPlayer.Active && !Spectate.IsWatching;

        // ---- ULTRAKILL's ranks, a bit bigger with more players ----

        private static float Max(int rank)
        {
            float own = 1500f;
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null && sh.ranks != null && rank >= 0 && rank < sh.ranks.Count)
                {
                    own = sh.ranks[rank].maxMeter;
                }
            }
            catch
            {
            }
            return Mathf.Max(1f, own * (0.6f + 0.4f * Players));
        }

        private static float Drain(int rank)
        {
            try
            {
                StyleHUD sh = MonoSingleton<StyleHUD>.Instance;
                if (sh != null && sh.ranks != null && rank >= 0 && rank < sh.ranks.Count)
                {
                    return sh.ranks[rank].drainSpeed;
                }
            }
            catch
            {
            }
            return 1f;
        }

        // ---- this player's style, to the others ----

        [HarmonyPatch(typeof(StyleHUD), nameof(StyleHUD.AddPoints)), HarmonyPrefix]
        private static void BeforeAdd(out int __state)
        {
            StatsManager sm = MonoSingleton<StatsManager>.Instance;
            __state = sm != null ? sm.stylePoints : 0;
        }

        [HarmonyPatch(typeof(StyleHUD), nameof(StyleHUD.AddPoints)), HarmonyPostfix]
        private static void AfterAdd(StyleHUD __instance, int __state, string pointID, EnemyIdentifier eid, int count, string prefix, string postfix)
        {
            try
            {
                if (!Active || (eid != null && eid.puppet))
                {
                    return;
                }
                StatsManager sm = MonoSingleton<StatsManager>.Instance;
                int points = sm != null ? Mathf.Max(0, sm.stylePoints - __state) : 0;
                string name = __instance.GetLocalizedName(pointID);
                string label = string.IsNullOrEmpty(name) ? "" : "+ " + prefix + name + postfix + (count >= 0 ? " x" + count : "");
                if (points <= 0 && label.Length == 0)
                {
                    return;
                }
                Said(Room.MyId, points, label);
                KillNet.Send(PointsMessage, KillNet.ToRoom, w =>
                {
                    w.Write(points);
                    w.Write(label.Length > 80 ? label.Substring(0, 80) : label);
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"party style: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(StyleHUD), nameof(StyleHUD.RemovePoints)), HarmonyPostfix]
        private static void AfterRemove(int points)
        {
            if (!Active || points <= 0)
            {
                return;
            }
            Said(Room.MyId, -points, "");
            KillNet.Send(PointsMessage, KillNet.ToRoom, w =>
            {
                w.Write(-points);
                w.Write("");
            });
        }

        public static void Handle(int from, byte type, BinaryReader r)
        {
            if (type == PointsMessage)
            {
                int points = r.ReadInt32();
                string label = r.ReadString();
                if (Active)
                {
                    Said(from, points, label.Length > 80 ? label.Substring(0, 80) : label);
                }
                return;
            }
            // (Where the meter stands, from the one running the enemies.)
            string s = r.ReadString();
            int rank = r.ReadByte();
            float m = r.ReadSingle();
            bool combo = r.ReadBoolean();
            if (!Active || from != Room.AuthorityId || s != scene)
            {
                return;
            }
            heardAt = Time.unscaledTime;
            Rank = Mathf.Clamp(rank, 0, Ranks.Length - 1);
            meter = Mathf.Max(0f, m);
            Combo = combo;
        }

        private static void Said(int id, int points, string label)
        {
            if (points > 0)
            {
                Add(points);
                Shares.TryGetValue(id, out float had);
                Shares[id] = had + points;
            }
            else if (points < 0)
            {
                meter += points;
            }
            // (On screen: ULTRAKILL's own meter, PartyStyleHud. This player's own style it lists itself.)
            if (id != Room.MyId)
            {
                if (points > 0)
                {
                    PartyStyleHud.Bump();
                }
                PartyStyleHud.Item(id, label);
            }
            if (label.Length > 0)
            {
                Items.Add(new Item { Id = id, Text = label, At = Time.unscaledTime });
                if (Items.Count > 12)
                {
                    Items.RemoveAt(0);
                }
            }
        }

        // As StyleHUD.AddPoints: up a rank (or more) when full; the combo begun with a quarter of one.
        private static void Add(float points)
        {
            if (!Combo)
            {
                Combo = true;
                Rank = 0;
                meter = Max(0) / 4f;
            }
            meter += points;
            while (meter >= Max(Rank) && Rank < Ranks.Length - 1)
            {
                meter -= Max(Rank);
                Rank++;
                meter = Mathf.Max(meter, Max(Rank) / 4f);
            }
            if (Rank == Ranks.Length - 1)
            {
                meter = Mathf.Min(meter, Max(Rank));
            }
        }

        // ---- each frame ----

        public static void Frame(bool inLevel)
        {
            bool want = Wanted(inLevel);
            string now = SceneHelper.CurrentScene ?? "";
            if (want != Active || now != scene)
            {
                Active = want;
                scene = now;
                Combo = false;
                Rank = 0;
                meter = 0f;
                Items.Clear();
                Shares.Clear();
                if (want)
                {
                    Plugin.Log.LogInfo("party style: the party's style meter in place of ULTRAKILL's");
                }
            }
            if (!Active)
            {
                return;
            }
            Players = 1 + Room.Peers.Count;
            float dt = Time.deltaTime;
            // (As StyleHUD.UpdateMeter drains it: down a rank when empty, the combo over at the bottom.)
            if (Combo)
            {
                if (meter < 0f)
                {
                    if (Rank > 0)
                    {
                        Rank--;
                        meter = Max(Rank) * 0.75f;
                    }
                    else
                    {
                        Combo = false;
                        meter = 0f;
                        Shares.Clear();
                    }
                }
                else
                {
                    meter -= dt * Drain(Rank) * 15f;
                }
            }
            var keys = new List<int>(Shares.Keys);
            foreach (int id in keys)
            {
                Shares[id] *= Mathf.Exp(-dt / 6f);
            }
            Items.RemoveAll(i => Time.unscaledTime - i.At > 3f);
            if (Room.IAmAuthority && Time.unscaledTime - sentAt > 0.1f)
            {
                sentAt = Time.unscaledTime;
                string s = scene;
                int rank = Rank;
                float m = meter;
                bool combo = Combo;
                KillNet.Send(StateMessage, KillNet.ToRoom, w =>
                {
                    w.Write(s);
                    w.Write((byte)rank);
                    w.Write(m);
                    w.Write(combo);
                });
            }
        }
    }
}
