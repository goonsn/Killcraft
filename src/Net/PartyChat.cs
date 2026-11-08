using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // The party's text chat (KillNet, to everyone in the world), with Killcraft's own notices ([SYSTEM]:
    // who joined, left, became the party host, invites, ...) and Minecraft's chat lines among them; and
    // a log of what happened, timestamped. Shown in the party menu, and in a level at the bottom left
    // (ChatBox): Enter (or T, with Minecraft off) to type there.
    internal static class PartyChat
    {
        public const byte ChatMessage = 33;
        public enum Kind { Chat, System, Tip, Minecraft }

        public sealed class Line
        {
            public string Text;
            public float At;
            public Kind Kind;
        }

        public static readonly List<Line> Lines = new List<Line>();
        public static readonly List<string> Log = new List<string>();
        public static int Version { get; private set; }

        private static readonly Dictionary<int, string> known = new Dictionary<int, string>();
        private static int lastHost = -1, knownFor = int.MinValue;
        private static bool tipped;
        private static float lastSaidAt = -100f;

        public static void Say(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                return;
            }
            if (text.Length > 200)
            {
                text = text.Substring(0, 200);
            }
            if (Time.unscaledTime - lastSaidAt < 0.3f)
            {
                return;
            }
            lastSaidAt = Time.unscaledTime;
            string t = text;
            KillNet.Send(ChatMessage, KillNet.ToEveryone, w => w.Write(t));
            int me = Room.MyId;
            string name = me != 0 ? Profiles.Colored(me) : $"<color=#{Ui.Hex(Profiles.Mine.Color)}>{Typing.Escape(Profiles.Mine.SteamName)}</color>";
            Add($"{name}: {Typing.Escape(text)}", Kind.Chat);
            Plugin.Log.LogInfo($"chat: {text}");
        }

        public static void Handle(int from, BinaryReader r)
        {
            string text = r.ReadString();
            if (text.Length > 200)
            {
                text = text.Substring(0, 200);
            }
            Add($"{Profiles.Colored(from)}: {Typing.Escape(text)}", Kind.Chat);
            Plugin.Log.LogInfo($"chat: {Profiles.NameOf(from)}: {text}");
        }

        public static void System(string text) => Add($"<color=#FFD02E>[SYSTEM]: {text}</color>", Kind.System);

        public static void Tip(string text) => Add($"<color=#4FE6FF>[TIP]: {text}</color>", Kind.Tip);

        public static void FromMinecraft(string text) => Add($"<color=#BBBBBB>{Typing.Escape(text)}</color>", Kind.Minecraft);

        public static void Event(string text)
        {
            Log.Add($"<color=#888888>{DateTime.Now:HH:mm:ss}</color>  {text}");
            if (Log.Count > 30)
            {
                Log.RemoveAt(0);
            }
            Version++;
        }

        private static void Add(string text, Kind kind)
        {
            Lines.Add(new Line { Text = text, At = Time.unscaledTime, Kind = kind });
            if (Lines.Count > 80)
            {
                Lines.RemoveAt(0);
            }
            Version++;
        }

        // Each frame: who's come and gone (the party's members), who's the host.
        public static void Frame()
        {
            if (!tipped)
            {
                tipped = true;
                Event($"<color=#DB5447>[ {Brand.Name} ]</color> LINK ESTABLISHED");
                Tip($"Voice chat and every {Brand.Name} setting are in the pause menu > {Brand.Name}. Enter to chat in a level.");
            }
            if (!Party.Heard)
            {
                return;
            }
            var now = new HashSet<int>();
            int host = -1;
            // (Coming into a party (another world: this player's id there is new): those already there aren't news.)
            if (Room.MyId != knownFor)
            {
                knownFor = Room.MyId;
                known.Clear();
            }
            bool first = known.Count == 0;
            foreach (Party.Member m in Party.Members)
            {
                now.Add(m.Id);
                if (m.PartyHost)
                {
                    host = m.Id;
                }
                if (!known.ContainsKey(m.Id))
                {
                    known[m.Id] = m.Name;
                    if (m.Id != Room.MyId && Room.MyId != 0 && !first)
                    {
                        System($"{Typing.Escape(m.Name)} has joined this party");
                        Event($"NODE '{Typing.Escape(m.Name).ToUpperInvariant()}' CONNECTED");
                    }
                }
            }
            var gone = new List<int>();
            foreach (var kv in known)
            {
                if (!now.Contains(kv.Key))
                {
                    gone.Add(kv.Key);
                }
            }
            foreach (int id in gone)
            {
                string name = known[id];
                known.Remove(id);
                if (id != Room.MyId && !first)
                {
                    System($"{Typing.Escape(name)} has left this party");
                    Event($"NODE '{Typing.Escape(name).ToUpperInvariant()}' DISCONNECTED");
                }
            }
            if (host != lastHost && host != -1)
            {
                if (lastHost != -1)
                {
                    System($"{Profiles.Colored(host)} is the party host now");
                }
                lastHost = host;
            }
        }
    }

    // The chat in a level: the latest lines at the bottom left, fading after a while; Enter (or T,
    // Minecraft off) to type, Enter to send, Esc to stop. While typing, V1 doesn't move.
    internal static class ChatBox
    {
        private const int Shown = 8;
        private const float Stay = 10f, Fade = 1.5f;
        private const string StateKey = "killcraft-chat";

        private static Canvas canvas;
        private static RectTransform box;
        private static Image back, inputBack;
        private static TextMeshProUGUI lines, input;
        private static int shownVersion = -1;
        private static bool locked;
        private static readonly object owner = new object();

        public static bool Typing => global::Killcraft.Typing.Owner == owner;

        public static void Frame(bool inLevel, bool paused, bool minecraftOff)
        {
            bool want = inLevel && !paused && Plugin.ShowChat.Value;
            if (!want)
            {
                if (Typing)
                {
                    global::Killcraft.Typing.End();
                }
                Unlock();
                if (canvas != null && canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(false);
                }
                return;
            }
            if (canvas == null)
            {
                Build();
            }
            if (!canvas.gameObject.activeSelf)
            {
                canvas.gameObject.SetActive(true);
                shownVersion = -1;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (!global::Killcraft.Typing.Active && global::Killcraft.Typing.EndedFrame != Time.frameCount && kb != null && !Patches.McScreenOpen && !McCommand.Busy
                && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || (minecraftOff && kb.tKey.wasPressedThisFrame)))
            {
                global::Killcraft.Typing.Begin(owner, 200, PartyChat.Say);
            }
            if (Typing)
            {
                Lock();
            }
            else
            {
                Unlock();
            }
            Draw(minecraftOff);
        }

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft chat", 20, true);
            box = Ui.Rect(canvas.transform, "Chat", 24f, 0f, 760f, 330f);
            box.anchorMin = box.anchorMax = new Vector2(0f, 0f);
            box.pivot = new Vector2(0f, 0f);
            box.anchoredPosition = new Vector2(24f, 300f);
            back = Ui.Picture(Ui.Fill(box, "Back"), null, new Color(0f, 0f, 0f, 0.5f));
            RectTransform lr = Ui.Fill(box, "Lines", 10f);
            lr.offsetMin = new Vector2(10f, 46f);
            lines = Ui.Label(lr, "", 22f, Color.white, TextAlignmentOptions.BottomLeft);
            lines.enableWordWrapping = true;
            lines.overflowMode = TextOverflowModes.Truncate;
            lines.outlineWidth = 0.18f;
            lines.outlineColor = new Color32(0, 0, 0, 255);
            RectTransform ir = Ui.Rect(box, "Input", 0f, 0f, 760f, 40f);
            ir.anchorMin = ir.anchorMax = new Vector2(0f, 0f);
            ir.pivot = new Vector2(0f, 0f);
            ir.anchoredPosition = Vector2.zero;
            inputBack = Ui.Picture(ir, Ui.RoundLine, Color.white);
            RectTransform it = Ui.Fill(ir, "Text");
            it.offsetMin = new Vector2(14f, 0f);
            it.offsetMax = new Vector2(-14f, 0f);
            input = Ui.Label(it, "", 22f, Color.white, TextAlignmentOptions.MidlineLeft);
        }

        private static void Draw(bool minecraftOff)
        {
            bool typing = Typing;
            back.enabled = typing;
            inputBack.gameObject.SetActive(typing);
            if (typing)
            {
                input.text = global::Killcraft.Typing.ShownIn(input, "> ", "TYPE HERE...");
            }
            var sb = new System.Text.StringBuilder();
            int n = 0;
            float now = Time.unscaledTime;
            var shown = new List<string>();
            for (int i = PartyChat.Lines.Count - 1; i >= 0 && n < (typing ? 12 : Shown); i--)
            {
                PartyChat.Line l = PartyChat.Lines[i];
                // (Minecraft's own chat shows its lines while it has the screen.)
                if (l.Kind == PartyChat.Kind.Minecraft && !minecraftOff)
                {
                    continue;
                }
                float age = now - l.At;
                if (!typing && age > Stay + Fade)
                {
                    break;
                }
                float alpha = typing || age < Stay ? 1f : 1f - (age - Stay) / Fade;
                // (No band behind: the letters' black outline keeps them readable over the gun and the HUD.)
                shown.Insert(0, $"<alpha=#{(int)(alpha * 255f):X2}>{l.Text}");
                n++;
            }
            foreach (string s in shown)
            {
                sb.AppendLine(s);
            }
            lines.text = sb.ToString();
        }

        private static void Lock()
        {
            if (locked)
            {
                return;
            }
            try
            {
                GameStateManager.Instance.RegisterState(new GameState(StateKey)
                {
                    playerInputLock = LockMode.Lock,
                    cameraInputLock = LockMode.Lock,
                    priority = 100,
                });
                locked = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"chat: couldn't hold V1 still while typing ({e.Message})");
                locked = true;
            }
        }

        private static void Unlock()
        {
            if (!locked)
            {
                return;
            }
            locked = false;
            try
            {
                GameStateManager.Instance.PopState(StateKey);
            }
            catch
            {
            }
        }
    }
}
