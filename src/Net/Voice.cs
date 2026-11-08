using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace Killcraft
{
    // Voice chat between the players in this world. Steam records and compresses this player's
    // microphone (its own voice chat's: Steam's input device and levels), sent to everyone in the world
    // (KillNet's route 4, through Minecraft as the rest), and played back per player: from their V1
    // when they're in this level (it's heard from where they are), plainly otherwise.
    // Push to talk (VoiceKey, V), an open microphone, or off: Killcraft's options.
    internal static class Voice
    {
        public const byte VoiceMessage = 30;
        private const int Rate = 48000;

        private sealed class Speaker
        {
            public GameObject Go;
            public AudioSource Source;
            public readonly object Lock = new object();
            public float[] Ring = new float[Rate * 2];
            public int Read, Write, Count;
            public bool Started;
            public float HeardAt = -100f;
            public float Level;
            // (Echo in big rooms, muffled underwater: ECHO & UNDERWATER.)
            public AudioLowPassFilter Muffle;
            public AudioReverbFilter Echo;
            public float RoomAt = -100f;
            public float Room;

            public void OnRead(float[] data)
            {
                lock (Lock)
                {
                    // (Waits for a little to be there first: a steady stream, not stutter.)
                    if (!Started && Count >= Rate / 10)
                    {
                        Started = true;
                    }
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (Started && Count > 0)
                        {
                            data[i] = Ring[Read];
                            Read = (Read + 1) % Ring.Length;
                            Count--;
                        }
                        else
                        {
                            data[i] = 0f;
                            Started = false;
                        }
                    }
                }
            }
        }

        private static readonly Dictionary<int, Speaker> speakers = new Dictionary<int, Speaker>();
        private static bool recording, failed;
        private static float sentAt = -100f;
        private static UnityEngine.InputSystem.Key key = UnityEngine.InputSystem.Key.None;
        private static string keyName = "";
        private static readonly MemoryStream pcm = new MemoryStream();

        // Deafened: nobody's heard (this player still talks). Testing the mic: this player hears themselves.
        public static bool Deafened;
        public static bool TestingMic;
        private const int MeId = -77;

        public static bool IsSpeaking(int id) => speakers.TryGetValue(id, out Speaker s) && Time.unscaledTime - s.HeardAt < 0.35f;

        // This player's microphone is on (and Steam's sending what it hears).
        public static bool Talking => recording && Time.unscaledTime - sentAt < 0.4f;
        public static bool MicOpen => recording;

        // Whose voice is coming in now.
        public static IEnumerable<int> Speaking()
        {
            foreach (var kv in speakers)
            {
                if (Time.unscaledTime - kv.Value.HeardAt < 0.35f)
                {
                    yield return kv.Key;
                }
            }
        }

        public static void Frame(bool typing)
        {
            if (failed)
            {
                return;
            }
            try
            {
                Record(typing);
                Place();
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning($"voice: Steam's voice chat failed ({e.Message}); no voice chat this time");
            }
        }

        private static void Record(bool typing)
        {
            bool steam = ServerList.SteamUp;
            int mode = Plugin.VoiceMode.Value;
            bool want = steam && (KillNet.Connected || TestingMic) && (mode != 0 || TestingMic) && !typing && (mode == 2 || TestingMic || KeyHeld());
            if (want != recording && steam)
            {
                SteamUser.VoiceRecord = want;
                recording = want;
            }
            if (!steam)
            {
                recording = false;
                return;
            }
            // (Also just after letting go: the last of it.)
            if (SteamUser.HasVoiceData)
            {
                byte[] data = SteamUser.ReadVoiceDataBytes();
                if (data != null && data.Length > 0 && data.Length < 20000 && TestingMic)
                {
                    Hear(MeId, data);
                }
                if (data != null && data.Length > 0 && data.Length < 20000 && KillNet.Connected && mode != 0)
                {
                    KillNet.Send(VoiceMessage, KillNet.ToEveryone, w =>
                    {
                        w.Write((ushort)data.Length);
                        w.Write(data);
                    });
                    sentAt = Time.unscaledTime;
                }
            }
        }

        private static bool KeyHeld()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            string name = Plugin.VoiceKey.Value ?? "";
            if (name != keyName)
            {
                keyName = name;
                if (!Enum.TryParse(name, true, out key))
                {
                    key = UnityEngine.InputSystem.Key.None;
                }
            }
            if (mouse != null)
            {
                if (name.Equals("Mouse4", StringComparison.OrdinalIgnoreCase))
                {
                    return mouse.backButton.isPressed;
                }
                if (name.Equals("Mouse5", StringComparison.OrdinalIgnoreCase))
                {
                    return mouse.forwardButton.isPressed;
                }
            }
            return keyboard != null && key != UnityEngine.InputSystem.Key.None && keyboard[key].isPressed;
        }

        // Another player's voice: decompressed (Steam's), into their speaker.
        public static void Handle(int from, BinaryReader r)
        {
            int n = r.ReadUInt16();
            byte[] data = r.ReadBytes(n);
            if (failed || !ServerList.SteamUp || Plugin.VoiceMode.Value == 0 || Deafened || Profiles.Of(from).Muted)
            {
                return;
            }
            Hear(from, data);
        }

        private static void Hear(int from, byte[] data)
        {
            pcm.SetLength(0);
            int bytes;
            try
            {
                // (Steam decodes at its "optimal" rate, 24000 or so, unless told: played back at 48000,
                // that was twice as fast and an octave up.)
                if (SteamUser.SampleRate != Rate)
                {
                    SteamUser.SampleRate = Rate;
                }
                bytes = SteamUser.DecompressVoice(data, pcm);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"voice: couldn't decode player {from}'s voice ({e.Message})");
                return;
            }
            if (bytes <= 0)
            {
                return;
            }
            Speaker s = SpeakerOf(from);
            byte[] raw = pcm.GetBuffer();
            int samples = bytes / 2;
            float peak = 0f;
            lock (s.Lock)
            {
                for (int i = 0; i < samples; i++)
                {
                    float v = (short)(raw[2 * i] | raw[2 * i + 1] << 8) / 32768f;
                    peak = Mathf.Max(peak, Mathf.Abs(v));
                    if (s.Count == s.Ring.Length)
                    {
                        // (Too far behind: the oldest goes.)
                        s.Read = (s.Read + 1) % s.Ring.Length;
                        s.Count--;
                    }
                    s.Ring[s.Write] = v;
                    s.Write = (s.Write + 1) % s.Ring.Length;
                    s.Count++;
                }
                // (More than half a second waiting: catch up.)
                while (s.Count > Rate / 2)
                {
                    s.Read = (s.Read + 1) % s.Ring.Length;
                    s.Count--;
                }
            }
            if (peak > 0.02f)
            {
                s.HeardAt = Time.unscaledTime;
            }
        }

        private static Speaker SpeakerOf(int id)
        {
            if (speakers.TryGetValue(id, out Speaker s) && s.Go != null)
            {
                return s;
            }
            s = new Speaker();
            s.Go = new GameObject($"Killcraft voice {id}");
            UnityEngine.Object.DontDestroyOnLoad(s.Go);
            s.Go.hideFlags = HideFlags.HideAndDontSave;
            s.Source = s.Go.AddComponent<AudioSource>();
            Speaker captured = s;
            s.Source.clip = AudioClip.Create($"voice {id}", Rate, 1, Rate, true, captured.OnRead);
            s.Source.loop = true;
            s.Source.ignoreListenerPause = true;
            s.Source.ignoreListenerVolume = false;
            s.Source.dopplerLevel = 0f;
            s.Source.rolloffMode = AudioRolloffMode.Linear;
            s.Muffle = s.Go.AddComponent<AudioLowPassFilter>();
            s.Muffle.cutoffFrequency = 22000f;
            s.Echo = s.Go.AddComponent<AudioReverbFilter>();
            s.Echo.reverbPreset = AudioReverbPreset.Off;
            s.Source.Play();
            speakers[id] = s;
            return s;
        }

        // Each speaker where its player is (in this level), or everywhere.
        private static void Place()
        {
            float volume = Mathf.Clamp(Plugin.VoiceVolume.Value, 0f, 3f);
            try
            {
                volume *= MonoSingleton<PrefsManager>.Instance.GetFloat("allVolume", 1f);
            }
            catch
            {
            }
            List<int> stale = null;
            foreach (var kv in speakers)
            {
                Speaker s = kv.Value;
                if (s.Go == null)
                {
                    (stale ??= new List<int>()).Add(kv.Key);
                    continue;
                }
                bool near = kv.Key != MeId && Plugin.VoiceProximity.Value && RemotePlayers.HeadOf(kv.Key, out Vector3 at);
                if (near)
                {
                    RemotePlayers.HeadOf(kv.Key, out at);
                    s.Go.transform.position = at;
                    s.Source.spatialBlend = 0.85f;
                    s.Source.minDistance = 6f * Coords.U;
                    s.Source.maxDistance = 90f * Coords.U;
                }
                else
                {
                    s.Source.spatialBlend = 0f;
                }
                float own = kv.Key == MeId ? (TestingMic ? 1f : 0f) : Deafened ? 0f : Profiles.Of(kv.Key).Muted ? 0f : Mathf.Clamp(Profiles.Of(kv.Key).Volume, 0f, 2f);
                s.Source.volume = volume * own;
                Effects(kv.Key, s, near);
            }
            if (stale != null)
            {
                foreach (int id in stale)
                {
                    speakers.Remove(id);
                }
            }
        }

        // Underwater (them, or this player): muffled. In a big closed room (round them): an echo.
        private static void Effects(int id, Speaker s, bool near)
        {
            if (s.Muffle == null || s.Echo == null)
            {
                return;
            }
            bool on = Plugin.VoiceEffects.Value && id != MeId;
            bool under = false;
            if (on)
            {
                try
                {
                    UnderwaterController uw = MonoSingleton<UnderwaterController>.Instance;
                    under = uw != null && uw.inWater;
                }
                catch
                {
                }
                under |= near && (Profiles.Of(id).Flags & Profiles.FlagUnderwater) != 0;
            }
            s.Muffle.cutoffFrequency = Mathf.MoveTowards(s.Muffle.cutoffFrequency, under ? 700f : 22000f, Time.unscaledDeltaTime * 60000f);
            if (on && near && Time.unscaledTime - s.RoomAt > 0.5f)
            {
                s.RoomAt = Time.unscaledTime;
                s.Room = RoomSize(s.Go.transform.position);
            }
            AudioReverbPreset want = !on || !near ? AudioReverbPreset.Off
                : s.Room > 45f * Coords.U ? AudioReverbPreset.Arena
                : s.Room > 22f * Coords.U ? AudioReverbPreset.Hallway
                : s.Room > 9f * Coords.U ? AudioReverbPreset.Room : AudioReverbPreset.Off;
            if (s.Echo.reverbPreset != want)
            {
                s.Echo.reverbPreset = want;
            }
        }

        // How big the closed room round a point is (0: open, the rays don't all hit).
        private static readonly Vector3[] Rays = { Vector3.up, Vector3.left, Vector3.right, Vector3.forward, Vector3.back, new Vector3(1f, 0f, 1f).normalized, new Vector3(-1f, 0f, -1f).normalized };

        private static float RoomSize(Vector3 at)
        {
            float max = 80f * Coords.U, sum = 0f;
            int hits = 0;
            int mask = LayerMaskDefaults.Get(LMD.Environment);
            foreach (Vector3 d in Rays)
            {
                if (Physics.Raycast(at, d, out RaycastHit hit, max, mask, QueryTriggerInteraction.Ignore))
                {
                    sum += hit.distance;
                    hits++;
                }
            }
            return hits >= Rays.Length - 1 ? sum / hits : 0f;
        }

        // ---- who's talking: a line at the top left ----

        private static GUIStyle style;

        public static void Gui()
        {
            if (failed)
            {
                return;
            }
            string line = "";
            if (Talking || (recording && Plugin.VoiceMode.Value == 1))
            {
                line = "<color=#ff3030>● MIC</color>";
            }
            foreach (int id in Speaking())
            {
                if (id == MeId)
                {
                    continue;
                }
                line += (line.Length > 0 ? "   " : "") + "<color=#ffffff>♪ " + Profiles.NameOf(id) + "</color>";
            }
            if (line.Length == 0)
            {
                return;
            }
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
            var r = new Rect(14f, Screen.height - 140f, 600f, 26f);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), System.Text.RegularExpressions.Regex.Replace(line, "<.*?>", ""), style);
            GUI.color = Color.white;
            GUI.Label(r, line, style);
        }
    }
}
