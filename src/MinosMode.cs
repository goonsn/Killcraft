using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Killcraft
{
    // The MINOS PRIME game mode (the party host's: the party screen's MODE). One player is Minos Prime
    // (MinosControl), the rest are V1, in his arena from P-1 (the Flesh Prison's room, as it is when
    // he comes down).
    //  - Who's Minos: the host's pick (a player's ... menu, MAKE MINOS), a random one, or a random one of
    //    those who volunteered (VOLUNTEER AS MINOS).
    //  - The V1s: one life each (dead: spectating), or a shared pool of lives (so many a V1). Minos has
    //    his own health times the number of V1s.
    //  - V1s win when he dies; Minos wins when every V1 is out. Then everyone's back on the party screen.
    // The round goes by the Minos player's game: he runs Minos (his health, his death), says when it
    // starts (everyone's arena ready, or 15 s), who's back in or out after dying, and who won.
    [HarmonyPatch]
    internal static class MinosMode
    {
        public const byte SettingsMessage = 52, VolunteerMessage = 53, RoundMessage = 54, StateMessage = 55, AttackMessage = 56;
        public const string Scene = "Level P-1";
        public const byte PickHost = 0, PickRandom = 1, PickVolunteer = 2;
        public const byte RulesOneLife = 0, RulesShared = 1;
        private const byte KStart = 0, KReady = 1, KCountdown = 2, KGo = 3, KDied = 4, KVerdict = 5, KEnd = 6, KFree = 7, KFreeOff = 8;
        private const byte WinV1 = 0, WinMinos = 1;
        private const float CountdownLength = 3f, RespawnAfter = 3f, OverFor = 7f, ReadyWait = 15f;

        // ---- the party's settings (the host's) ----

        private static bool selectedHere;
        private static int hostChoiceHere = int.MinValue;
        private static bool heardSelected;
        private static byte heardPick, heardRules = RulesOneLife;
        private static int heardLives = 3, heardChoice = int.MinValue;
        private static float settingsSentAt = -100f, volunteerSentAt = -100f;
        private static string sentKey = "";
        private static bool iVolunteer, sentVolunteer;
        private static readonly HashSet<int> volunteers = new HashSet<int>();

        // Whether this player's settings are the ones that count: alone, or the party host.
        public static bool Mine => !(Party.Heard && Party.Members.Count > 1) || Party.IAmHost;
        public static bool Selected => Mine ? selectedHere : heardSelected;
        public static byte PickMode => Mine ? (byte)Mathf.Clamp(Plugin.MinosPick.Value, 0, 2) : heardPick;
        public static byte RulesMode => Mine ? (byte)Mathf.Clamp(Plugin.MinosRules.Value, 0, 1) : heardRules;
        public static int LivesSetting => Mine ? Mathf.Clamp(Plugin.MinosLives.Value, 1, 20) : heardLives;
        public static int HostChoice => Mine ? hostChoiceHere : heardChoice;
        public static bool Volunteering => iVolunteer;
        public static bool Volunteered(int id) => id == Room.MyId ? iVolunteer : volunteers.Contains(id);

        public static void SetSelected(bool on)
        {
            if (!Mine)
            {
                return;
            }
            selectedHere = on;
            settingsSentAt = -100f;
            PartyChat.Event(on ? "<color=#DB5447>MODE: MINOS PRIME</color>" : "MODE: NORMAL");
        }

        public static void CyclePick()
        {
            if (Mine)
            {
                Plugin.MinosPick.Value = (Mathf.Clamp(Plugin.MinosPick.Value, 0, 2) + 1) % 3;
                settingsSentAt = -100f;
            }
        }

        public static void CycleRules()
        {
            if (Mine)
            {
                Plugin.MinosRules.Value = Mathf.Clamp(Plugin.MinosRules.Value, 0, 1) == 0 ? 1 : 0;
                settingsSentAt = -100f;
            }
        }

        public static void CycleLives()
        {
            if (Mine)
            {
                int l = Mathf.Clamp(Plugin.MinosLives.Value, 1, 20);
                Plugin.MinosLives.Value = l >= 10 ? 1 : l + 1;
                settingsSentAt = -100f;
            }
        }

        // The host's MAKE MINOS (again: not any more).
        public static void HostPick(int id)
        {
            if (!Mine)
            {
                return;
            }
            hostChoiceHere = hostChoiceHere == id ? int.MinValue : id;
            settingsSentAt = -100f;
            if (hostChoiceHere != int.MinValue)
            {
                PartyChat.Event($"<color=#DB5447>{Typing.Escape(Party.NameOf(id).ToUpperInvariant())} WILL BE MINOS PRIME</color>");
            }
        }

        public static void ToggleVolunteer()
        {
            iVolunteer = !iVolunteer;
            volunteerSentAt = -100f;
        }

        public static string PickName(byte p) => p == PickHost ? "HOST PICKS" : p == PickRandom ? "RANDOM" : "VOLUNTEERS";

        public static string RulesName(byte r) => r == RulesOneLife ? "ONE LIFE" : "SHARED LIVES";

        // A squad row's tag (the party screen).
        public static string RowTag(int id)
        {
            if (!Selected)
            {
                return "";
            }
            if (PickMode == PickHost && HostChoice == id)
            {
                return "  <color=#DB5447>MINOS</color>";
            }
            if (PickMode == PickVolunteer && Volunteered(id))
            {
                return "  <color=#DB5447>VOLUNTEER</color>";
            }
            return "";
        }

        private static void SendSettings()
        {
            if (!KillNet.Connected || !Party.IAmHost || Party.Members.Count < 2)
            {
                return;
            }
            string key = $"{selectedHere}|{PickMode}|{RulesMode}|{LivesSetting}|{hostChoiceHere}";
            if (key == sentKey && Time.unscaledTime - settingsSentAt < 2f)
            {
                return;
            }
            sentKey = key;
            settingsSentAt = Time.unscaledTime;
            KillNet.Send(SettingsMessage, KillNet.ToEveryone, w =>
            {
                w.Write(selectedHere);
                w.Write(PickMode);
                w.Write(RulesMode);
                w.Write((byte)LivesSetting);
                w.Write(hostChoiceHere);
            });
        }

        private static void SendVolunteer()
        {
            if (!KillNet.Connected || Party.Members.Count < 2)
            {
                return;
            }
            if (iVolunteer == sentVolunteer && Time.unscaledTime - volunteerSentAt < 3f)
            {
                return;
            }
            sentVolunteer = iVolunteer;
            volunteerSentAt = Time.unscaledTime;
            KillNet.Send(VolunteerMessage, KillNet.ToEveryone, w => w.Write(iVolunteer));
        }

        // ---- the round ----

        public static bool Active { get; private set; }
        // Minos in any level (the host's TURN INTO MINOS, the pause panel): no arena, no lives, no end.
        public static bool Free { get; private set; }
        // A round in his arena (not Free).
        public static bool Round => Active && !Free;
        public static int MinosId { get; private set; } = int.MinValue;
        public static bool IAmMinos => Active && MinosId == Room.MyId;
        public static bool IsMinos(int id) => Active && id == MinosId;
        public static readonly List<int> V1s = new List<int>();
        private static readonly HashSet<int> outIds = new HashSet<int>(), readyIds = new HashSet<int>();
        private static byte rules;
        private static int livesPer;
        public static int LivesLeft { get; private set; }

        private enum Phase { Loading, Waiting, Countdown, Fight, Over }
        private static Phase phase;
        private static float startedAt, phaseAt, sceneAt = -1f, absentSince = -1f;
        private static bool arenaReady, iAmOut, awaitingVerdict, sentReady;
        private static float respawnAt = -1f;
        private static byte winner;
        private static readonly Dictionary<int, float> goneSince = new Dictionary<int, float>();

        public static bool Fighting => Active && phase == Phase.Fight;

        // A V1 still in it (not out, in the party).
        public static bool InPlay(int id) => Active && V1s.Contains(id) && !outIds.Contains(id);

        // The host's START MINOS PRIME (the party screen): who's Minos, then the WAKE to his arena.
        public static void HostStart()
        {
            if (!Party.IAmHost)
            {
                return;
            }
            var ids = new List<int>();
            foreach (Party.Member m in Party.Members)
            {
                ids.Add(m.Id);
            }
            if (!ids.Contains(Room.MyId))
            {
                ids.Add(Room.MyId);
            }
            if (ids.Count < 2)
            {
                PartyChat.System("Minos Prime needs at least 2 players: one Minos, one V1.");
                return;
            }
            int minos = int.MinValue;
            byte pick = PickMode;
            if (pick == PickHost && ids.Contains(hostChoiceHere))
            {
                minos = hostChoiceHere;
            }
            else if (pick == PickVolunteer)
            {
                var willing = ids.FindAll(Volunteered);
                if (willing.Count > 0)
                {
                    minos = willing[UnityEngine.Random.Range(0, willing.Count)];
                }
            }
            if (minos == int.MinValue)
            {
                if (pick == PickHost)
                {
                    PartyChat.System("No one picked to be Minos (... on a player, MAKE MINOS): a random player is.");
                }
                else if (pick == PickVolunteer)
                {
                    PartyChat.System("No volunteers: a random player is Minos.");
                }
                minos = ids[UnityEngine.Random.Range(0, ids.Count)];
            }
            var v1s = ids.FindAll(i => i != minos);
            byte r = RulesMode;
            int lives = LivesSetting;
            KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
            {
                w.Write(KStart);
                w.Write(minos);
                w.Write(r);
                w.Write((byte)lives);
                w.Write(v1s.Count);
                foreach (int v in v1s)
                {
                    w.Write(v);
                }
            });
            Begin(minos, r, lives, v1s);
            PartyWake.Begin(Scene);
        }

        private static void Begin(int minos, byte r, int lives, List<int> v1s)
        {
            if (Free)
            {
                EndFree(true);
            }
            Active = true;
            MinosId = minos;
            rules = r;
            livesPer = Mathf.Max(1, lives);
            V1s.Clear();
            V1s.AddRange(v1s);
            outIds.Clear();
            readyIds.Clear();
            goneSince.Clear();
            LivesLeft = rules == RulesShared ? livesPer * V1s.Count : 0;
            phase = Phase.Loading;
            startedAt = phaseAt = Time.unscaledTime;
            sceneAt = -1f;
            absentSince = -1f;
            arenaReady = iAmOut = awaitingVerdict = sentReady = false;
            respawnAt = -1f;
            string who = minos == Room.MyId ? "YOU ARE" : Typing.Escape(Party.NameOf(minos).ToUpperInvariant()) + " IS";
            PartyChat.Event($"<color=#DB5447>{who} MINOS PRIME</color>  ·  {RulesName(rules)}" + (rules == RulesShared ? $" ({LivesLeft} LIVES)" : ""));
            Plugin.Log.LogInfo($"minos: round begun, Minos {minos}, V1s {string.Join(", ", V1s)}, {RulesName(rules)}, {livesPer} a V1");
        }

        public static void Handle(int from, byte type, BinaryReader r)
        {
            switch (type)
            {
                case SettingsMessage:
                    if (IsHost(from))
                    {
                        heardSelected = r.ReadBoolean();
                        heardPick = r.ReadByte();
                        heardRules = r.ReadByte();
                        heardLives = r.ReadByte();
                        heardChoice = r.ReadInt32();
                    }
                    break;
                case VolunteerMessage:
                    if (r.ReadBoolean())
                    {
                        volunteers.Add(from);
                    }
                    else
                    {
                        volunteers.Remove(from);
                    }
                    break;
                case RoundMessage:
                    HandleRound(from, r);
                    break;
                case StateMessage:
                    MinosControl.HandleState(from, r);
                    break;
                case AttackMessage:
                    MinosControl.HandleAttack(from, r);
                    break;
            }
        }

        private static bool IsHost(int id)
        {
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == id && m.PartyHost)
                {
                    return true;
                }
            }
            return false;
        }

        private static void HandleRound(int from, BinaryReader r)
        {
            byte kind = r.ReadByte();
            switch (kind)
            {
                case KStart:
                {
                    int minos = r.ReadInt32();
                    byte rl = r.ReadByte();
                    int lives = r.ReadByte();
                    int n = r.ReadInt32();
                    var v1s = new List<int>();
                    for (int i = 0; i < n; i++)
                    {
                        v1s.Add(r.ReadInt32());
                    }
                    if (!IsHost(from) || !Plugin.FollowHost.Value)
                    {
                        return;  // (only the party host starts a round)
                    }
                    Begin(minos, rl, lives, v1s);
                    break;
                }
                case KReady:
                    readyIds.Add(from);
                    // (In late: told where the round is.)
                    if (IAmMinos && phase == Phase.Countdown)
                    {
                        KillNet.Send(RoundMessage, KillNet.ToPlayer, w => w.Write(KCountdown), from);
                    }
                    else if (IAmMinos && phase >= Phase.Fight)
                    {
                        KillNet.Send(RoundMessage, KillNet.ToPlayer, w => w.Write(KGo), from);
                    }
                    break;
                case KCountdown:
                    if (Active && from == MinosId && phase < Phase.Countdown)
                    {
                        SetPhase(Phase.Countdown);
                    }
                    break;
                case KGo:
                    if (Active && from == MinosId && phase < Phase.Fight)
                    {
                        Go();
                    }
                    break;
                case KDied:
                    if (IAmMinos)
                    {
                        Judge(from);
                    }
                    break;
                case KVerdict:
                {
                    int id = r.ReadInt32();
                    bool back = r.ReadBoolean();
                    int left = r.ReadInt32();
                    if (Active && from == MinosId)
                    {
                        Verdict(id, back, left);
                    }
                    break;
                }
                case KFree:
                {
                    int id = r.ReadInt32();
                    string scene = r.ReadString();
                    int serial = r.BaseStream.Length - r.BaseStream.Position >= 4 ? r.ReadInt32() : 0;
                    if (IsHost(from))
                    {
                        StartFree(id, scene, serial);
                    }
                    break;
                }
                case KFreeOff:
                {
                    int id = r.ReadInt32();
                    if (Free && id == MinosId && (from == MinosId || IsHost(from)))
                    {
                        EndFree(true);
                    }
                    break;
                }
                case KEnd:
                {
                    byte w = r.ReadByte();
                    if (Active && phase != Phase.Over && (from == MinosId || w == WinV1))
                    {
                        End(w, false);
                    }
                    break;
                }
            }
        }

        private static void SetPhase(Phase p)
        {
            phase = p;
            phaseAt = Time.unscaledTime;
        }

        // ---- each frame (Host) ----

        public static void Frame(NewMovement nm, bool inLevel, bool paused)
        {
            SendSettings();
            if (Selected && PickMode == PickVolunteer)
            {
                SendVolunteer();
            }
            inLevelNow = inLevel && nm != null;
            if (!Active)
            {
                Hud.Hide();
                return;
            }
            if (Free)
            {
                FreeFrame(nm, Time.unscaledTime);
                return;
            }
            float now = Time.unscaledTime;
            string current = SceneHelper.CurrentScene ?? "";
            bool loading = SceneHelper.PendingScene != null;
            if (current != Scene)
            {
                // (The wake stopped before going; or someone left the level after it began.)
                if (arenaReady && !loading)
                {
                    Plugin.Log.LogInfo("minos: out of the arena: the round's over for this player");
                    Stop();
                    return;
                }
                if (!arenaReady && !loading && !PartyWake.Active && now - startedAt > 8f)
                {
                    Plugin.Log.LogInfo("minos: the round never started (the wake stopped)");
                    Stop();
                    return;
                }
                Hud.Hide();
                return;
            }
            if (sceneAt < 0f)
            {
                sceneAt = now;
            }
            if (!arenaReady)
            {
                if (nm != null && inLevel && !loading && now - sceneAt > 1f)
                {
                    arenaReady = MinosArena.Setup(nm);
                    if (arenaReady)
                    {
                        SetPhase(Phase.Waiting);
                    }
                }
                Hud.Show("", "", "LOADING THE ARENA", "");
                return;
            }
            if (!sentReady)
            {
                sentReady = true;
                readyIds.Add(Room.MyId);
                KillNet.Send(RoundMessage, KillNet.ToEveryone, w => w.Write(KReady));
            }
            if (IAmMinos)
            {
                MinosControl.KeepV1Away(nm);
                Run(now);
            }
            else
            {
                Watch(now, nm);
            }
            if (phase == Phase.Countdown && now - phaseAt >= CountdownLength && IAmMinos)
            {
                KillNet.Send(RoundMessage, KillNet.ToEveryone, w => w.Write(KGo));
                Go();
            }
            if (phase == Phase.Over && now - phaseAt > OverFor)
            {
                ToParty();
                return;
            }
            MinosControl.Frame();
            Hud.Update(now);
        }

        // The Minos player's: when to start, who's left, who won.
        private static void Run(float now)
        {
            if (phase == Phase.Waiting)
            {
                bool all = V1s.TrueForAll(id => readyIds.Contains(id) || !Present(id));
                if (all || now - phaseAt > ReadyWait)
                {
                    KillNet.Send(RoundMessage, KillNet.ToEveryone, w => w.Write(KCountdown));
                    SetPhase(Phase.Countdown);
                }
                return;
            }
            if (phase != Phase.Fight)
            {
                return;
            }
            if (MinosControl.Dead)
            {
                SendEnd(WinV1);
                return;
            }
            // (A V1 gone from the level for a while: out.)
            foreach (int id in V1s)
            {
                if (outIds.Contains(id))
                {
                    continue;
                }
                if (Present(id))
                {
                    goneSince.Remove(id);
                }
                else if (!goneSince.TryGetValue(id, out float since))
                {
                    goneSince[id] = now;
                }
                else if (now - since > 5f)
                {
                    Plugin.Log.LogInfo($"minos: {id} left: out");
                    SendVerdict(id, false);
                }
            }
            if (V1s.TrueForAll(outIds.Contains))
            {
                SendEnd(WinMinos);
            }
        }

        // A V1 player's: Minos still there (gone: the V1s win), coming back after dying.
        private static void Watch(float now, NewMovement nm)
        {
            if (phase == Phase.Fight || phase == Phase.Waiting || phase == Phase.Countdown)
            {
                if (Present(MinosId))
                {
                    absentSince = -1f;
                }
                else if (absentSince < 0f)
                {
                    absentSince = now;
                }
                else if (now - absentSince > 6f)
                {
                    Plugin.Log.LogInfo("minos: the Minos player left: the V1s win");
                    End(WinV1, true);
                    return;
                }
            }
            if (respawnAt > 0f && now >= respawnAt)
            {
                respawnAt = -1f;
                Spectate.ReviveAt(MinosArena.SpawnPoint(UnityEngine.Random.Range(0, 8)), 90f);
            }
        }

        private static bool Present(int id) => id == Room.MyId || Room.Peers.Contains(id);

        // GO: Minos woken (on every game, at the Minos player's word).
        private static void Go()
        {
            SetPhase(Phase.Fight);
            MinosArena.Wake();
            Plugin.Log.LogInfo("minos: GO");
        }

        // ---- dying ----

        // This V1 down (Spectate's hook): the Minos player says whether it's back.
        public static void LocalDied(NewMovement nm)
        {
            if (!Round || IAmMinos || iAmOut || awaitingVerdict || phase == Phase.Over)
            {
                return;
            }
            try
            {
                if (nm.deathSequence != null)
                {
                    nm.deathSequence.gameObject.SetActive(false);
                }
            }
            catch
            {
            }
            awaitingVerdict = true;
            KillNet.Send(RoundMessage, KillNet.ToEveryone, w => w.Write(KDied));
            Plugin.Log.LogInfo("minos: V1 down");
        }

        private static void Judge(int id)
        {
            if (!V1s.Contains(id) || outIds.Contains(id) || phase == Phase.Over)
            {
                return;
            }
            bool back = rules == RulesShared && LivesLeft > 0;
            if (back)
            {
                LivesLeft--;
            }
            SendVerdict(id, back);
            if (V1s.TrueForAll(outIds.Contains))
            {
                SendEnd(WinMinos);
            }
        }

        private static void SendVerdict(int id, bool back)
        {
            int left = LivesLeft;
            KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
            {
                w.Write(KVerdict);
                w.Write(id);
                w.Write(back);
                w.Write(left);
            });
            Verdict(id, back, left);
        }

        private static void Verdict(int id, bool back, int left)
        {
            LivesLeft = left;
            if (!back)
            {
                outIds.Add(id);
            }
            string name = id == Room.MyId ? "YOU" : Typing.Escape(Party.NameOf(id).ToUpperInvariant());
            PartyChat.Event(back ? $"{name} DIED  ·  {left} LIVES LEFT" : $"<color=#DB5447>{name} {(id == Room.MyId ? "ARE" : "IS")} OUT</color>");
            if (id != Room.MyId)
            {
                return;
            }
            awaitingVerdict = false;
            if (back)
            {
                respawnAt = Time.unscaledTime + RespawnAfter;
                return;
            }
            iAmOut = true;
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (nm != null && nm.dead)
            {
                Spectate.BeginFor(nm);
            }
        }

        public static bool Out => Active && iAmOut;
        public static float RespawnIn => respawnAt > 0f ? Mathf.Max(0f, respawnAt - Time.unscaledTime) : -1f;
        public static bool WaitingVerdict => awaitingVerdict;

        // ---- the end ----

        private static void SendEnd(byte w)
        {
            if (phase == Phase.Over)
            {
                return;
            }
            KillNet.Send(RoundMessage, KillNet.ToEveryone, wr =>
            {
                wr.Write(KEnd);
                wr.Write(w);
            });
            End(w, false);
        }

        private static void End(byte w, bool forfeit)
        {
            if (phase == Phase.Over)
            {
                return;
            }
            winner = w;
            SetPhase(Phase.Over);
            respawnAt = -1f;
            Plugin.Log.LogInfo($"minos: round over, {(w == WinV1 ? "the V1s" : "Minos")} won{(forfeit ? " (Minos left)" : "")}");
            PartyChat.Event(w == WinV1 ? "<color=#DB5447>MINOS PRIME HAS FALLEN</color>" : "<color=#DB5447>MINOS PRIME WINS</color>");
        }

        public static bool Over => Active && phase == Phase.Over;
        public static bool V1sWon => winner == WinV1;

        // Back to the party screen (everyone, each by themselves).
        private static void ToParty()
        {
            Stop();
            PartyMenu.OpenOnMenu = SteamNet.InLobby;
            Party.LoadQuiet("Main Menu");
        }

        private static void Stop()
        {
            if (!Active)
            {
                return;
            }
            MinosControl.Release();
            MinosArena.Forget();
            Active = false;
            Free = false;
            freeRoot = null;
            MinosId = int.MinValue;
            V1s.Clear();
            outIds.Clear();
            Hud.Hide();
        }

        // ---- not mid-round: a checkpoint or a restart (they'd bring the level back for one player) ----

        [HarmonyPatch(typeof(OptionsMenuToManager), nameof(OptionsMenuToManager.RestartCheckpoint)), HarmonyPrefix]
        private static bool NoCheckpoint() => Allowed();

        [HarmonyPatch(typeof(OptionsMenuToManager), nameof(OptionsMenuToManager.RestartMission)), HarmonyPrefix]
        private static bool NoRestart() => Allowed();

        [HarmonyPatch(typeof(OptionsMenuToManager), nameof(OptionsMenuToManager.RestartMissionNoConfirm)), HarmonyPrefix]
        private static bool NoRestartNow() => Allowed();

        private static bool Allowed()
        {
            if (!Round || SceneHelper.CurrentScene != Scene)
            {
                return true;
            }
            PartyChat.System("Not during a Minos Prime round (QUIT MISSION leaves it).");
            return false;
        }

        // Bosses' health with more players (BossScaling): Minos's, times the V1s.
        public static bool HealthFor(EnemyIdentifier eid, out float times)
        {
            times = 1f;
            if (!Active || eid == null || eid.enemyType != EnemyType.MinosPrime)
            {
                return false;
            }
            times = Mathf.Max(1, V1s.Count);
            return true;
        }

        // ---- Minos in any level (the host's TURN INTO MINOS) ----

        private const float FreeBackAfter = 3f;
        private static string freeScene = "";
        private static int freeSerial;
        public static int FreeSerial => freeSerial;
        private static float freeDeadAt = -1f, freeEndedAt = -100f;
        private static int freeEndedId = int.MinValue;
        private static GameObject freeRoot;
        private static bool inLevelNow;

        // The host's (or no party's), in a level, no round on.
        public static bool CanFree => Mine && inLevelNow && !Round && SceneHelper.PendingScene == null;

        public static void HostFree(int id)
        {
            if (!CanFree)
            {
                return;
            }
            if (Free && MinosId == id)
            {
                HostUnfree();
                return;
            }
            string scene = SceneHelper.CurrentScene ?? "";
            int serial = unchecked(Environment.TickCount * 31 + Room.MyId);
            KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
            {
                w.Write(KFree);
                w.Write(id);
                w.Write(scene);
                w.Write(serial);
            });
            StartFree(id, scene, serial);
        }

        public static void HostUnfree()
        {
            if (!Mine || !Free)
            {
                return;
            }
            int id = MinosId;
            KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
            {
                w.Write(KFreeOff);
                w.Write(id);
            });
            EndFree(true);
        }

        private static void StartFree(int id, string scene, int serial)
        {
            if (Free && MinosId == id)
            {
                return;  // (heard twice: from the host and from his stream)
            }
            if (Round)
            {
                return;
            }
            bool here = (SceneHelper.CurrentScene ?? "") == scene && SceneHelper.PendingScene == null;
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (id == Room.MyId && (!here || nm == null || nm.dead || !inLevelNow))
            {
                // (Dead, or not in that level: can't be him; the host hears so.)
                KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
                {
                    w.Write(KFreeOff);
                    w.Write(id);
                });
                PartyChat.System("Can't turn into Minos Prime right now (dead, or not in the level).");
                return;
            }
            if (!here)
            {
                return;  // (somewhere else: not here)
            }
            if (Free)
            {
                EndFree(true);
            }
            Active = true;
            Free = true;
            MinosId = id;
            freeScene = scene;
            freeSerial = serial;
            V1s.Clear();
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id != id && !V1s.Contains(m.Id))
                {
                    V1s.Add(m.Id);
                }
            }
            if (id != Room.MyId && !V1s.Contains(Room.MyId))
            {
                V1s.Add(Room.MyId);
            }
            outIds.Clear();
            readyIds.Clear();
            goneSince.Clear();
            rules = RulesOneLife;
            livesPer = 1;
            LivesLeft = 0;
            phase = Phase.Fight;
            startedAt = phaseAt = Time.unscaledTime;
            arenaReady = sentReady = true;
            iAmOut = awaitingVerdict = false;
            respawnAt = freeDeadAt = absentSince = -1f;
            if (id == Room.MyId)
            {
                Vector3 at = nm.transform.position;
                if (Physics.Raycast(at, Vector3.down, out RaycastHit hit, 40f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
                {
                    at = hit.point;
                }
                CameraController cc = nm.cc != null ? nm.cc : MonoSingleton<CameraController>.Instance;
                float yaw = cc != null ? cc.transform.eulerAngles.y : nm.transform.eulerAngles.y;
                if (!Spawn(at, yaw))
                {
                    KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
                    {
                        w.Write(KFreeOff);
                        w.Write(id);
                    });
                    Stop();
                    return;
                }
            }
            string who = id == Room.MyId ? "YOU ARE" : Typing.Escape(Party.NameOf(id).ToUpperInvariant()) + " IS";
            PartyChat.Event($"<color=#DB5447>{who} MINOS PRIME</color>");
            Plugin.Log.LogInfo($"minos: {id} is Minos Prime in {scene}");
        }

        // His stream heard here, no word from the host (in late, or the word lost): his copy too.
        public static void JoinFree(int from, int sceneHash, int serial)
        {
            if (Active || !inLevelNow || (from == freeEndedId && Time.unscaledTime - freeEndedAt < 3f))
            {
                return;
            }
            string scene = SceneHelper.CurrentScene ?? "";
            if (SceneHash(scene) != sceneHash || !(Room.Peers.Contains(from)))
            {
                return;
            }
            StartFree(from, scene, serial);
        }

        public static int SceneHash(string scene)
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (char c in scene ?? "")
                {
                    h = (h ^ c) * 16777619;
                }
                return h;
            }
        }

        public static int FreeSceneHash => SceneHash(freeScene);

        // Minos Prime from ULTRAKILL's list of enemies (the sandbox's), where V1 stood / his stream says.
        public static bool Spawn(Vector3 at, float yaw)
        {
            try
            {
                GameObject prefab = NetherWorld.PrefabFor(EnemyType.MinosPrime);
                if (prefab == null)
                {
                    Plugin.Log.LogWarning("minos: no Minos Prime in ULTRAKILL's list of enemies");
                    PartyChat.System("Couldn't make Minos Prime here (ULTRAKILL's list of enemies isn't loaded).");
                    return false;
                }
                GameObject go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.Euler(0f, yaw, 0f));
                go.name = $"Killcraft Minos Prime {freeSerial}";  // (his own name: EnemySync tells enemies apart by it)
                MinosPrime mp = go.GetComponentInChildren<MinosPrime>(true);
                if (mp == null)
                {
                    UnityEngine.Object.Destroy(go);
                    Plugin.Log.LogWarning("minos: the Minos Prime made has no MinosPrime");
                    return false;
                }
                freeRoot = go;
                MinosControl.Body = mp;
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }
                Plugin.Log.LogInfo($"minos: Minos Prime made at {at}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"minos: couldn't make Minos Prime ({e})");
                return false;
            }
        }

        private static void FreeFrame(NewMovement nm, float now)
        {
            if ((SceneHelper.CurrentScene ?? "") != freeScene || SceneHelper.PendingScene != null)
            {
                if (IAmMinos)
                {
                    int id = MinosId;
                    KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
                    {
                        w.Write(KFreeOff);
                        w.Write(id);
                    });
                }
                EndFree(false);
                return;
            }
            // (Whoever's in the level with him is a V1 of his: those who came after too.)
            foreach (int id in Room.Peers)
            {
                if (id != MinosId && !V1s.Contains(id))
                {
                    V1s.Add(id);
                }
            }
            if (!IAmMinos && !V1s.Contains(Room.MyId))
            {
                V1s.Add(Room.MyId);
            }
            if (MinosControl.Body == null)
            {
                // (The others': his copy comes with his stream, MinosControl.HandleState.)
                if (IAmMinos || now - startedAt > 8f)
                {
                    Plugin.Log.LogInfo("minos: no Minos Prime: back to V1");
                    EndFree(false);
                    return;
                }
                Hud.Hide();
                return;
            }
            if (IAmMinos)
            {
                MinosControl.KeepV1Away(nm);
                if (MinosControl.Dead)
                {
                    if (freeDeadAt < 0f)
                    {
                        freeDeadAt = now;
                    }
                    else if (now - freeDeadAt > FreeBackAfter)
                    {
                        int id = MinosId;
                        KillNet.Send(RoundMessage, KillNet.ToEveryone, w =>
                        {
                            w.Write(KFreeOff);
                            w.Write(id);
                        });
                        EndFree(false);
                        return;
                    }
                }
            }
            else if (Present(MinosId))
            {
                absentSince = -1f;
            }
            else if (absentSince < 0f)
            {
                absentSince = now;
            }
            else if (now - absentSince > 6f)
            {
                Plugin.Log.LogInfo("minos: the Minos player left");
                EndFree(true);
                return;
            }
            MinosControl.Frame();
            Hud.Update(now);
        }

        // Back to V1 (where he stood: KeepV1Away kept V1 with him); his body gone if he's alive still.
        private static void EndFree(bool quiet)
        {
            if (!Free)
            {
                return;
            }
            freeEndedAt = Time.unscaledTime;
            freeEndedId = MinosId;
            bool wasMe = IAmMinos;
            string name = wasMe ? "YOU ARE" : Typing.Escape(Party.NameOf(MinosId).ToUpperInvariant()) + " IS";
            GameObject body = freeRoot != null ? freeRoot : MinosControl.Body != null ? MinosControl.Body.gameObject : null;
            Stop();
            if (body != null)
            {
                UnityEngine.Object.Destroy(body);
            }
            if (wasMe)
            {
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm != null && !nm.dead)
                {
                    nm.ActivatePlayer();
                }
            }
            PartyChat.Event($"{name} V1 AGAIN");
            Plugin.Log.LogInfo("minos: back to V1");
        }

        // ---- on screen ----

        private static class Hud
        {
            private static Canvas canvas;
            private static TextMeshProUGUI top, sub, big, help, status;
            private static RectTransform reticle;
            private static readonly RectTransform[] chevrons = new RectTransform[4];
            private static long reticleOn = long.MinValue;
            private static float reticleSince;
            private static Sprite chevron;

            // A chevron (a V, its point down), smooth edged, white (shown in its colour).
            private static Sprite Chevron
            {
                get
                {
                    if (chevron != null)
                    {
                        return chevron;
                    }
                    const int s = 48;
                    var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                    var px = new Color32[s * s];
                    Vector2 a = new Vector2(5f, 34f), tip = new Vector2(24f, 13f), c = new Vector2(43f, 34f);
                    const float half = 4.5f;
                    for (int y = 0; y < s; y++)
                    {
                        for (int x = 0; x < s; x++)
                        {
                            Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                            float d = Mathf.Min(Seg(p, a, tip), Seg(p, tip, c));
                            float al = Mathf.Clamp01(half - d + 0.5f);
                            px[y * s + x] = new Color32(255, 255, 255, (byte)(al * 255f));
                        }
                    }
                    tex.SetPixels32(px);
                    tex.Apply();
                    return chevron = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
                }
            }

            private static float Seg(Vector2 p, Vector2 a, Vector2 b)
            {
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                return Vector2.Distance(p, a + ab * t);
            }
            private static string bigWas = "";
            private static float bigAt;

            public static void Hide()
            {
                if (canvas != null && canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(false);
                }
            }

            private static void Build()
            {
                if (canvas != null)
                {
                    return;
                }
                canvas = Ui.Canvas("Killcraft Minos Prime", 60, true);
                Transform root = canvas.transform;
                top = Ui.Text(root, "", 30f, Color.white, TextAlignmentOptions.Center, 360f, 118f, 1200f, 40f);
                sub = Ui.Text(root, "", 22f, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.Center, 360f, 158f, 1200f, 32f);
                big = Ui.Text(root, "", 120f, Color.white, TextAlignmentOptions.Center, 160f, 380f, 1600f, 160f);
                big.overflowMode = TextOverflowModes.Overflow;
                status = Ui.Text(root, "", 26f, Ui.Red, TextAlignmentOptions.Center, 360f, 940f, 1200f, 36f);
                help = Ui.Text(root, "", 19f, Color.white, TextAlignmentOptions.Center, 160f, 982f, 1600f, 70f);
                help.enableWordWrapping = true;
                reticle = Ui.Rect(root, "Target", 0f, 0f, 0f, 0f);
                reticle.pivot = new Vector2(0.5f, 0.5f);
                reticle.sizeDelta = Vector2.zero;
                for (int i = 0; i < 4; i++)
                {
                    RectTransform arm = Ui.Rect(reticle, "Chevron", 0f, 0f, 22f, 22f);
                    arm.anchorMin = arm.anchorMax = arm.pivot = new Vector2(0.5f, 0.5f);
                    arm.sizeDelta = new Vector2(22f, 22f);
                    Image shade = Ui.Picture(Ui.Fill(arm, "Shade"), Chevron, new Color(0f, 0f, 0f, 0.75f));
                    ((RectTransform)shade.transform).offsetMin = new Vector2(1.5f, -1.5f);
                    ((RectTransform)shade.transform).offsetMax = new Vector2(1.5f, -1.5f);
                    Ui.Picture(Ui.Fill(arm, "Mark"), Chevron, Ui.Red);
                    chevrons[i] = arm;
                }
                RectTransform dot = Ui.Rect(reticle, "Dot", 0f, 0f, 5f, 5f);
                dot.anchorMin = dot.anchorMax = dot.pivot = new Vector2(0.5f, 0.5f);
                dot.sizeDelta = new Vector2(5f, 5f);
                dot.anchoredPosition = Vector2.zero;
                Ui.Picture(dot, Ui.CircleFill, new Color(1f, 1f, 1f, 0.85f));
                reticle.gameObject.SetActive(false);
                Ui.Refont(root);
            }

            public static void Show(string t, string s, string b, string st)
            {
                Build();
                if (!canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(true);
                }
                top.text = t;
                sub.text = s;
                SetBig(b);
                status.text = st;
                help.text = "";
                reticle.gameObject.SetActive(false);
            }

            private static void SetBig(string b)
            {
                if (b != bigWas)
                {
                    bigWas = b;
                    bigAt = Time.unscaledTime;
                }
                big.text = b;
                // (Each new word in big and settling, as the wake's countdown.)
                float into = Mathf.Clamp01((Time.unscaledTime - bigAt) / 0.5f);
                float shrink = 1f - Mathf.Pow(1f - into, 3f);
                big.transform.localScale = Vector3.one * Mathf.Lerp(1.4f, 1f, shrink);
            }

            public static void Update(float now)
            {
                Build();
                if (!canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(true);
                }
                string minosName = IAmMinos ? "YOU" : Typing.Escape(Party.NameOf(MinosId).ToUpperInvariant());
                int left = V1s.FindAll(InPlay).Count;
                top.text = IAmMinos ? "<color=#DB5447>YOU ARE MINOS PRIME</color>" : $"MINOS PRIME: <color=#DB5447>{minosName}</color>";
                sub.text = Free ? (IAmMinos ? "BACK TO V1 WHEN HE DIES (OR THE HOST SAYS)" : "")
                    : $"V1S LEFT {left}/{V1s.Count}" + (rules == RulesShared ? $"  ·  LIVES {LivesLeft}" : "  ·  ONE LIFE");
                string b = "", st = "";
                switch (phase)
                {
                    case Phase.Waiting:
                        b = "";
                        st = "WAITING FOR EVERYONE";
                        break;
                    case Phase.Countdown:
                        b = Mathf.Max(1, Mathf.CeilToInt(CountdownLength - (now - phaseAt))).ToString();
                        break;
                    case Phase.Fight:
                        b = now - phaseAt < 1.2f ? (IAmMinos ? "JUDGEMENT" : Free ? "" : "FIGHT") : "";
                        break;
                    case Phase.Over:
                        b = V1sWon ? "V1 WINS" : "MINOS PRIME WINS";
                        st = $"BACK TO THE PARTY IN {Mathf.Max(0, Mathf.CeilToInt(OverFor - (now - phaseAt)))}";
                        break;
                }
                if (phase == Phase.Fight && !IAmMinos)
                {
                    if (RespawnIn >= 0f)
                    {
                        st = $"BACK IN {Mathf.CeilToInt(RespawnIn)}";
                    }
                    else if (awaitingVerdict)
                    {
                        st = "DOWN...";
                    }
                    else if (iAmOut)
                    {
                        st = "YOU'RE OUT: SPECTATING";
                    }
                }
                if (phase == Phase.Fight && IAmMinos)
                {
                    st = Free && freeDeadAt > 0f ? $"BACK TO V1 IN {Mathf.Max(1, Mathf.CeilToInt(FreeBackAfter - (now - freeDeadAt)))}"
                        : MinosControl.Tired ? "CATCHING HIS BREATH" : "";
                }
                SetBig(b);
                status.text = st;
                help.text = IAmMinos && !(Free && freeDeadAt > 0f) && (phase == Phase.Fight || phase == Phase.Countdown || phase == Phase.Waiting)
                    ? "GROUND:  LEFT CLICK COMBO  ·  RIGHT CLICK PROJECTILE  ·  PUNCH UPPERCUT  ·  SLIDE DROPKICK  ·  JUMP\n"
                      + "AIR:  LEFT CLICK DOWN SWING  ·  RIGHT CLICK RIDER KICK  ·  SLIDE DROP ATTACK  ·  "
                      + (MinosControl.TeleportLeft > 0f ? $"<color=#888888>DASH TELEPORT {MinosControl.TeleportLeft:0.0}</color>" : "DASH TELEPORT")
                      + $"  ·  {UkThirdPerson.KeyName()} {(MinosControl.FirstPerson ? "BEHIND HIM" : "HIS EYES")}"
                    : "";
                Reticle();
            }

            // Four chevrons pointing in at the one he's going at (the Minos player's): they close in and
            // turn square on a new target, then breathe; nearer, wider apart.
            private static void Reticle()
            {
                bool aimed = MinosControl.TargetHead(out Vector3 at);
                bool show = IAmMinos && phase == Phase.Fight && aimed;
                Camera cam = MonoSingleton<CameraController>.Instance != null ? MonoSingleton<CameraController>.Instance.cam : null;
                if (!show || cam == null)
                {
                    reticle.gameObject.SetActive(false);
                    return;
                }
                Vector3 sp = cam.WorldToScreenPoint(at - Vector3.up * 1f);
                if (sp.z <= 0f)
                {
                    reticle.gameObject.SetActive(false);
                    return;
                }
                RectTransform rt = (RectTransform)canvas.transform;
                float x = sp.x / Screen.width * rt.rect.width, y = sp.y / Screen.height * rt.rect.height;
                reticle.anchorMin = reticle.anchorMax = Vector2.zero;
                reticle.anchoredPosition = new Vector2(x, y);
                reticle.gameObject.SetActive(true);
                float now = Time.unscaledTime;
                long key = MinosControl.TargetKey;
                if (key != reticleOn)
                {
                    reticleOn = key;
                    reticleSince = now;
                }
                float lock01 = Mathf.Clamp01((now - reticleSince) / 0.22f);
                float ease = 1f - (1f - lock01) * (1f - lock01) * (1f - lock01);
                float near = Mathf.Clamp(900f / Mathf.Max(sp.z, 1f), 22f, 54f);
                float gap = near * Mathf.Lerp(2.6f, 1f, ease) + Mathf.Sin(now * 5f) * 2.5f * ease;
                float spin = Mathf.Lerp(90f, 0f, ease);
                reticle.localEulerAngles = new Vector3(0f, 0f, spin);
                for (int i = 0; i < 4; i++)
                {
                    float ang = i * 90f;
                    Vector2 d = new Vector2(Mathf.Sin(ang * Mathf.Deg2Rad), Mathf.Cos(ang * Mathf.Deg2Rad));
                    chevrons[i].anchoredPosition = d * gap;
                    chevrons[i].localEulerAngles = new Vector3(0f, 0f, -ang);
                    chevrons[i].localScale = Vector3.one * Mathf.Lerp(1.4f, 1f, ease);
                }
            }
        }
    }

    // His arena in P-1 (its last room, "3 - Fuckatorium"), set as ULTRAKILL sets it when Minos comes
    // down after the Flesh Prison: the room on, the Flesh Prison and his intro off, his wave on (its
    // floor, its ceiling's mouth shut), its lights on and its entrance shut, the music before it off.
    // He's woken at the round's GO.
    internal static class MinosArena
    {
        private static GameObject minos;
        private static readonly Vector3 Center = new Vector3(405f, -598.5f, 110f);

        public static bool Setup(NewMovement nm)
        {
            try
            {
                Scene scene = SceneManager.GetActiveScene();
                GameObject room = Root(scene, "3 - Fuckatorium");
                if (room == null)
                {
                    Plugin.Log.LogWarning("minos: P-1 has no '3 - Fuckatorium' here");
                    return false;
                }
                room.SetActive(true);
                SetActive(room, "3 Stuff/FleshPrisonWave", false);
                SetActive(room, "3 Stuff/MinosPrimeIntro", false);
                SetActive(room, "3 Stuff/No Enemies Version", false);
                Transform wave = room.transform.Find("3 Stuff/MinosPrimeWave");
                if (wave == null)
                {
                    Plugin.Log.LogWarning("minos: P-1 has no Minos Prime wave here");
                    return false;
                }
                wave.gameObject.SetActive(true);
                SetActive(wave.gameObject, "Trigger (1)", false);
                Transform mp = wave.Find("MinosPrime");
                minos = mp != null ? mp.gameObject : null;
                MinosControl.Body = minos != null ? minos.GetComponent<MinosPrime>() : null;
                if (MinosControl.Body == null)
                {
                    Plugin.Log.LogWarning("minos: P-1 has no Minos Prime here");
                    return false;
                }
                SetActive(room, "3 Stuff/Lights", true);
                SetActive(room, "3 Stuff/EntranceCloser", true);
                foreach (string off in new[] { "Music 2/Ominous Ambiance 2", "Reverb", "Music 2" })
                {
                    string[] parts = off.Split(new[] { '/' }, 2);
                    GameObject r = Root(scene, parts[0]);
                    if (r != null)
                    {
                        if (parts.Length == 1)
                        {
                            r.SetActive(false);
                        }
                        else
                        {
                            SetActive(r, parts[1], false);
                        }
                    }
                }
                if (!MinosMode.IAmMinos)
                {
                    int i = Mathf.Max(0, MinosMode.V1s.IndexOf(Room.MyId));
                    Place(nm, SpawnPoint(i), 90f);
                }
                else
                {
                    nm.ActivatePlayer();
                    CameraController cc = nm.cc != null ? nm.cc : MonoSingleton<CameraController>.Instance;
                    if (cc != null)
                    {
                        cc.ResetCamera(90f);
                    }
                }
                Plugin.Log.LogInfo("minos: the arena's ready");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"minos: couldn't set up the arena ({e})");
                return false;
            }
        }

        // The round's GO: Minos down.
        public static void Wake()
        {
            if (minos != null && !minos.activeSelf)
            {
                minos.SetActive(true);
            }
        }

        public static void Forget() => minos = null;

        // Where V1s come in: across from him, near the shut entrance, spread out.
        public static Vector3 SpawnPoint(int i)
        {
            float z = Center.z + ((i % 5) - 2) * 6f;
            float x = 368f + (i / 5) * 5f;
            Vector3 from = new Vector3(x, Center.y + 20f, z);
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 60f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * Coords.FeetBelowRoot;
            }
            return new Vector3(x, Center.y + Coords.FeetBelowRoot, z);
        }

        private static void Place(NewMovement nm, Vector3 at, float facing)
        {
            nm.transform.position = at;
            if (nm.rb != null)
            {
                nm.rb.velocity = Vector3.zero;
            }
            CameraController cc = nm.cc != null ? nm.cc : MonoSingleton<CameraController>.Instance;
            if (cc != null)
            {
                cc.ResetCamera(facing);
                cc.ApplyRotations();
            }
            nm.ActivatePlayer();
        }

        private static GameObject Root(Scene scene, string name)
        {
            foreach (GameObject g in scene.GetRootGameObjects())
            {
                if (g.name == name)
                {
                    return g;
                }
            }
            return null;
        }

        private static void SetActive(GameObject under, string path, bool on)
        {
            Transform t = under.transform.Find(path);
            if (t != null && t.gameObject.activeSelf != on)
            {
                t.gameObject.SetActive(on);
            }
        }
    }
}
