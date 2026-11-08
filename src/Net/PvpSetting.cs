using System.IO;
using UnityEngine;

namespace Killcraft
{
    // PvP is the party host's to turn on or off (and how hard players hit), for everyone. The world's
    // Minecraft (on the world host's PC) is what lets players hurt each other, from its own Killcraft's
    // setting: so the party host sends theirs, and the world host's Killcraft takes it. Before, only
    // the world host's setting counted, and a party host who wasn't the world host changed nothing.
    internal static class PvpSetting
    {
        public const byte Message = 39;
        private static float sentAt = -100f, sentDamage = -1f;
        private static bool sentOn;

        // Whether this player's setting is the one that counts: alone, or the party host.
        public static bool Mine => !(Party.Heard && Party.Members.Count > 1) || Party.IAmHost;

        // As it is for everyone now.
        public static bool On => Mine ? Plugin.PvP.Value : Room.Pvp;

        public static float Damage => Mine ? Plugin.PvpDamage.Value : Room.PvpDamage;

        public static void Frame()
        {
            if (!KillNet.Connected || !Party.IAmHost || Party.Members.Count < 2)
            {
                return;
            }
            bool on = Plugin.PvP.Value;
            float damage = Mathf.Clamp(Plugin.PvpDamage.Value, 0.1f, 3f);
            if (on == sentOn && Mathf.Abs(damage - sentDamage) < 0.01f && Time.unscaledTime - sentAt < 3f)
            {
                return;
            }
            sentOn = on;
            sentDamage = damage;
            sentAt = Time.unscaledTime;
            KillNet.Send(Message, KillNet.ToEveryone, w =>
            {
                w.Write(on);
                w.Write(damage);
            });
        }

        public static void Handle(int from, BinaryReader r)
        {
            bool on = r.ReadBoolean();
            float damage = r.ReadSingle();
            bool fromHost = false;
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == from && m.PartyHost)
                {
                    fromHost = true;
                }
            }
            if (!fromHost || !Party.IAmWorldHost || Party.IAmHost)
            {
                return;
            }
            if (Plugin.PvP.Value != on)
            {
                Plugin.PvP.Value = on;
                Plugin.Log.LogInfo($"PvP: {(on ? "on" : "off")}, as the party host ({Party.NameOf(from)}) has it");
                PartyChat.System($"{Typing.Escape(Party.NameOf(from))} turned PvP {(on ? "on" : "off")}.");
            }
            if (Mathf.Abs(Plugin.PvpDamage.Value - damage) > 0.01f)
            {
                Plugin.PvpDamage.Value = damage;
            }
        }

        // A change in the settings: only the party host's (else a note why not).
        public static void Set(bool on)
        {
            if (Mine)
            {
                Plugin.PvP.Value = on;
                return;
            }
            PartyChat.System("Only the party host can turn PvP on or off.");
        }

        public static void SetDamage(float damage)
        {
            if (Mine)
            {
                Plugin.PvpDamage.Value = damage;
            }
        }
    }
}
