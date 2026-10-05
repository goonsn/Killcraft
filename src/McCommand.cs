using System.Collections.Generic;
using UnityEngine;

namespace Killcraft
{
    // Runs a Minecraft command the way a player would: T opens the chat, the command is typed in
    // (slash and all, as text: T is in the same place on every common keyboard layout, the slash
    // key isn't), Enter sends it. SkyCraft's link has no command channel, but it does carry keys and
    // text. The player's own input is held back meanwhile (Host checks Busy).
    internal static class McCommand
    {
        private const ushort SdlT = 23, SdlEnter = 40, SdlEscape = 41;

        private enum Step { Idle, Opening, Sent }

        private static readonly Queue<string> pending = new Queue<string>();
        private static Step step;
        private static float timer;
        private static string current;

        public static bool Busy => step != Step.Idle || pending.Count > 0;

        public static void Run(string command) => pending.Enqueue(command);

        public static void Clear()
        {
            pending.Clear();
            step = Step.Idle;
        }

        // ready: Minecraft's player is in its world. Commands wait while a Minecraft screen is open.
        public static void Frame(bool ready, bool screenOpen)
        {
            switch (step)
            {
                case Step.Idle:
                    if (!ready || screenOpen || pending.Count == 0)
                    {
                        return;
                    }
                    current = pending.Dequeue();
                    Link.PushInput(Proto.InReleaseAll);
                    Link.PushInput(Proto.InKey, SdlT, 1);
                    Link.PushInput(Proto.InKey, SdlT, 0);
                    step = Step.Opening;
                    timer = 0f;
                    return;
                case Step.Opening:
                    timer += Time.unscaledDeltaTime;
                    if (screenOpen)
                    {
                        foreach (char c in "/" + current)
                        {
                            Link.PushInput(Proto.InText, 0, c);
                        }
                        Link.PushInput(Proto.InKey, SdlEnter, 1);
                        Link.PushInput(Proto.InKey, SdlEnter, 0);
                        step = Step.Sent;
                        timer = 0f;
                    }
                    else if (timer > 2f)
                    {
                        Plugin.Log.LogWarning($"Minecraft: its chat didn't open; '/{current}' not run");
                        step = Step.Idle;
                    }
                    return;
                case Step.Sent:
                    timer += Time.unscaledDeltaTime;
                    if (!screenOpen)
                    {
                        Plugin.Log.LogInfo($"Minecraft: ran /{current}");
                        step = Step.Idle;
                    }
                    else if (timer > 2f)
                    {
                        Link.PushInput(Proto.InKey, SdlEscape, 1);
                        Link.PushInput(Proto.InKey, SdlEscape, 0);
                        Plugin.Log.LogWarning($"Minecraft: its chat stayed open after '/{current}'; closed it");
                        step = Step.Idle;
                    }
                    return;
            }
        }
    }
}
