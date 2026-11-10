using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Killcraft
{
    // More of the level, alike for everyone in it (KillNet; WorldSync has glass, doors, skulls, hook
    // points, lifts and waves):
    //  - trams (7-1's): a tram's speed control pressed in one player's ULTRAKILL takes the tram to that
    //    speed in the others' too, and it keeps it (their own control would stop it for being far from
    //    them);
    //  - hidden switches (Limbo's, the shotgun's): pressed for everyone;
    //  - fire: a thing set alight in one game burns in the others' too; an enemy set alight anywhere
    //    burns everywhere (its fire's damage dealt once, by its authority); gasoline lit in one game is
    //    lit in the others' at that spot (each game's own puddles: they fall much the same);
    //  - cutscene skips: a vote. Anyone in the cutscene presses the skip key (Pause) and, when more than
    //    half the players in the level have, it's skipped for everyone;
    //  - boss intros: the trigger that starts a boss (his intro, his arena) tripped in one game is
    //    tripped in the others' for whoever's near it (the rest get their own when they come).
    // All of it is kept in replays (Replay.Keeps) and done again when one plays.
    internal static class LevelEvents
    {
        public const byte Message = 57;
        private const byte KindTram = 0, KindSwitch = 1, KindBurn = 2, KindEnemyBurn = 3, KindIgnite = 4, KindSkipVote = 5, KindBoss = 6;
        private const float BossNear = 50f;
        private static bool applying;

        public static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(TramControl), "SpeedUp", null, nameof(TramAfter), Type.EmptyTypes);
            Patch(harmony, typeof(TramControl), "SpeedDown", null, nameof(TramAfter), Type.EmptyTypes);
            Patch(harmony, typeof(TramControl), "FixedUpdate", nameof(TramFixed), nameof(TramStopped));
            Patch(harmony, typeof(LimboSwitch), nameof(LimboSwitch.Pressed), null, nameof(SwitchAfter));
            Patch(harmony, typeof(Flammable), nameof(Flammable.Burn), nameof(BurnBefore), nameof(BurnAfter));
            Patch(harmony, typeof(StainVoxelManager), nameof(StainVoxelManager.TryIgniteAt), null, nameof(IgniteAfter), new[] { typeof(Vector3), typeof(int) });
            Patch(harmony, typeof(CutsceneSkip), "Start", null, nameof(SkipShown));
            Patch(harmony, typeof(CutsceneSkip), "OnEnable", null, nameof(SkipShown));
            Patch(harmony, typeof(CutsceneSkip), "LateUpdate", nameof(SkipLate), null);
            Patch(harmony, typeof(CutsceneSkip), "OnDisable", null, nameof(SkipOff));
            Patch(harmony, typeof(ObjectActivator), "Activate", nameof(ActivateBefore), nameof(ActivateAfter), new[] { typeof(bool) });
            Patch(harmony, typeof(Punch), "ParryProjectile", null, nameof(ParryAfter));
        }

        private static void Patch(Harmony harmony, Type type, string method, string prefix, string postfix, Type[] args = null)
        {
            try
            {
                MethodBase m = AccessTools.Method(type, method, args);
                if (m == null)
                {
                    throw new MissingMethodException(type.Name, method);
                }
                harmony.Patch(m, prefix != null ? new HarmonyMethod(typeof(LevelEvents), prefix) : null,
                    postfix != null ? new HarmonyMethod(typeof(LevelEvents), postfix) : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"KillNet: couldn't hook ULTRAKILL's {type.Name}.{method} ({e.Message}); it won't happen for other players");
            }
        }

        private static void Send(Action<BinaryWriter> write) => KillNet.Send(Message, KillNet.ToRoom, write);

        private static bool Telling => !applying && Room.Sharing && !ReplayPlayer.Active;

        public static void LevelChanged()
        {
            found.Clear();
            scannedAt.Clear();
            tramFromOthers.Clear();
            votes.Clear();
            sentIgnites.Clear();
            enemyBurnSentAt.Clear();
            skipTextWas = null;
        }

        // ---- the level's things by their place in it (EnemySync.PathId: the same in every game) ----

        private static readonly Dictionary<Type, Dictionary<uint, Component>> found = new Dictionary<Type, Dictionary<uint, Component>>();
        private static readonly Dictionary<Type, float> scannedAt = new Dictionary<Type, float>();

        private static T Find<T>(uint id) where T : Component
        {
            if (!found.TryGetValue(typeof(T), out Dictionary<uint, Component> map))
            {
                found[typeof(T)] = map = new Dictionary<uint, Component>();
            }
            if (map.TryGetValue(id, out Component c) && c != null)
            {
                return (T)c;
            }
            if (scannedAt.TryGetValue(typeof(T), out float at) && Time.unscaledTime - at < 5f)
            {
                return null;
            }
            scannedAt[typeof(T)] = Time.unscaledTime;
            foreach (T t in Resources.FindObjectsOfTypeAll<T>())
            {
                if (t != null && t.gameObject.scene.IsValid() && t.gameObject.scene.isLoaded)
                {
                    map[EnemySync.PathId(t.transform)] = t;
                }
            }
            return map.TryGetValue(id, out c) && c != null ? (T)c : null;
        }

        public static void Handle(int from, BinaryReader r)
        {
            byte kind = r.ReadByte();
            try
            {
                switch (kind)
                {
                    case KindTram:
                        HandleTram(r);
                        break;
                    case KindSwitch:
                        HandleSwitch(r);
                        break;
                    case KindBurn:
                        HandleBurn(r);
                        break;
                    case KindEnemyBurn:
                        HandleEnemyBurn(r);
                        break;
                    case KindIgnite:
                        HandleIgnite(r);
                        break;
                    case KindSkipVote:
                        Vote(r.ReadUInt32(), from);
                        break;
                    case KindBoss:
                        HandleBoss(from, r);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KillNet: couldn't do in the level what player {from} did ({kind}: {e.Message})");
            }
            finally
            {
                applying = false;
            }
        }

        // ---- trams ----

        private static readonly Dictionary<TramControl, float> tramFromOthers = new Dictionary<TramControl, float>();

        private static void TramAfter(TramControl __instance)
        {
            if (!Telling || __instance == null)
            {
                return;
            }
            tramFromOthers.Remove(__instance);
            uint id = EnemySync.PathId(__instance.transform);
            int step = __instance.currentSpeedStep;
            Send(w =>
            {
                w.Write(KindTram);
                w.Write(id);
                w.Write(step);
            });
        }

        // (Set going by another player: not stopped here for this player being far from its control.)
        private static bool TramFixed(TramControl __instance, out int __state)
        {
            __state = __instance != null ? __instance.currentSpeedStep : 0;
            return __instance == null || !tramFromOthers.TryGetValue(__instance, out float at) || Time.unscaledTime - at > 120f;
        }

        // (Stopped here for this player walking away from its control: stopped for the others too, or
        // theirs would run on without it.)
        private static void TramStopped(TramControl __instance, int __state)
        {
            if (!Telling || __instance == null || __state == 0 || __instance.currentSpeedStep != 0)
            {
                return;
            }
            uint id = EnemySync.PathId(__instance.transform);
            Send(w =>
            {
                w.Write(KindTram);
                w.Write(id);
                w.Write(0);
            });
        }

        private static void HandleTram(BinaryReader r)
        {
            uint id = r.ReadUInt32();
            int step = r.ReadInt32();
            TramControl tc = Find<TramControl>(id);
            if (tc == null)
            {
                return;
            }
            applying = true;
            tramFromOthers[tc] = Time.unscaledTime;
            if (tc.currentSpeedStep != step)
            {
                tc.currentSpeedStep = step;
                Plugin.Log.LogInfo($"KillNet: another player set {tc.name} to speed {step}: here too");
            }
        }

        // ---- hidden switches ----

        private static void SwitchAfter(LimboSwitch __instance)
        {
            if (!Telling || __instance == null)
            {
                return;
            }
            uint id = EnemySync.PathId(__instance.transform);
            Send(w =>
            {
                w.Write(KindSwitch);
                w.Write(id);
            });
        }

        private static void HandleSwitch(BinaryReader r)
        {
            uint id = r.ReadUInt32();
            LimboSwitch s = Find<LimboSwitch>(id);
            if (s == null || s.beenPressed)
            {
                return;
            }
            applying = true;
            s.Pressed();
            Plugin.Log.LogInfo($"KillNet: another player pressed {s.name}: pressed here too");
        }

        // ---- fire ----

        private static readonly AccessTools.FieldRef<Flammable, bool> flammableEnemy = AccessTools.FieldRefAccess<Flammable, bool>("enemy");
        private static readonly AccessTools.FieldRef<Flammable, EnemyIdentifierIdentifier> flammableEidid = AccessTools.FieldRefAccess<Flammable, EnemyIdentifierIdentifier>("eidid");
        private static readonly Dictionary<uint, float> enemyBurnSentAt = new Dictionary<uint, float>();

        private static void BurnBefore(Flammable __instance, out bool __state) => __state = __instance != null && __instance.burning;

        private static void BurnAfter(Flammable __instance, float newHeat, bool __state)
        {
            if (!Telling || __state || __instance == null || !__instance.burning || __instance.secondary)
            {
                return;
            }
            float heat = Mathf.Max(newHeat, __instance.heat);
            if (flammableEnemy(__instance))
            {
                EnemyIdentifierIdentifier ii = flammableEidid(__instance);
                EnemyIdentifier eid = ii != null ? ii.eid : null;
                if (eid == null || eid.dead)
                {
                    return;
                }
                uint id = EnemySync.IdOf(eid);
                if (id == 0 || (enemyBurnSentAt.TryGetValue(id, out float at) && Time.unscaledTime - at < 1f))
                {
                    return;
                }
                enemyBurnSentAt[id] = Time.unscaledTime;
                Send(w =>
                {
                    w.Write(KindEnemyBurn);
                    w.Write(id);
                    w.Write(heat);
                });
                return;
            }
            uint pid = EnemySync.PathId(__instance.transform);
            Send(w =>
            {
                w.Write(KindBurn);
                w.Write(pid);
                w.Write(heat);
            });
        }

        private static void HandleBurn(BinaryReader r)
        {
            uint id = r.ReadUInt32();
            float heat = r.ReadSingle();
            Flammable f = Find<Flammable>(id);
            if (f == null || f.burning)
            {
                return;
            }
            applying = true;
            f.Burn(heat);
        }

        private static void HandleEnemyBurn(BinaryReader r)
        {
            uint id = r.ReadUInt32();
            float heat = r.ReadSingle();
            EnemyIdentifier eid = EnemySync.Find(id);
            if (eid == null || eid.dead || (eid.burners != null && eid.burners.Count > 0))
            {
                return;
            }
            applying = true;
            eid.StartBurning(heat);
        }

        // Gasoline lit (a flame reaching it): lit in the others' games at that spot.
        private static readonly List<KeyValuePair<Vector3, float>> sentIgnites = new List<KeyValuePair<Vector3, float>>();

        private static void IgniteAfter(Vector3 worldPosition, bool __result)
        {
            if (!Telling || !__result)
            {
                return;
            }
            float now = Time.unscaledTime;
            for (int i = sentIgnites.Count - 1; i >= 0; i--)
            {
                if (now - sentIgnites[i].Value > 3f)
                {
                    sentIgnites.RemoveAt(i);
                }
                else if ((sentIgnites[i].Key - worldPosition).sqrMagnitude < 4f)
                {
                    return;
                }
            }
            sentIgnites.Add(new KeyValuePair<Vector3, float>(worldPosition, now));
            Vector3 p = worldPosition;
            Send(w =>
            {
                w.Write(KindIgnite);
                w.Write(p.x);
                w.Write(p.y);
                w.Write(p.z);
            });
        }

        private static void HandleIgnite(BinaryReader r)
        {
            var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var svm = MonoSingleton.GetInstance(typeof(StainVoxelManager)) as StainVoxelManager;
            if (svm == null)
            {
                return;
            }
            applying = true;
            svm.TryIgniteAt(at);
        }

        // ---- cutscenes: skipped by vote ----

        private static readonly AccessTools.FieldRef<CutsceneSkip, bool> skipWaiting = AccessTools.FieldRefAccess<CutsceneSkip, bool>("waitingForInput");
        private static readonly AccessTools.FieldRef<CutsceneSkip, float> skipTimeLeft = AccessTools.FieldRefAccess<CutsceneSkip, float>("timeLeft");
        private static readonly AccessTools.FieldRef<CutsceneSkip, bool> skipPrint = AccessTools.FieldRefAccess<CutsceneSkip, bool>("printLengthOfCutscene");
        private static readonly Dictionary<uint, Dictionary<int, float>> votes = new Dictionary<uint, Dictionary<int, float>>();
        private static string skipTextWas;

        // (With others in the level anyone in the cutscene can call the vote, whether or not they've
        // finished the level before, as ULTRAKILL asks of a skip alone.)
        private static void SkipShown(CutsceneSkip __instance)
        {
            if (__instance == null || !Room.Shared || skipWaiting(__instance))
            {
                return;
            }
            skipWaiting(__instance) = true;
            skipTimeLeft(__instance) = __instance.addToTimer;
            CutsceneSkipText t = MonoSingleton<CutsceneSkipText>.Instance;
            if (t != null)
            {
                t.Show();
            }
        }

        private static bool SkipLate(CutsceneSkip __instance)
        {
            if (__instance == null || !skipWaiting(__instance))
            {
                return true;
            }
            if (!Room.Shared)
            {
                // (Alone, recording a replay: ULTRAKILL's own skip, kept for the replay as a vote of one.)
                InputManager im1 = MonoSingleton<InputManager>.Instance;
                if (Replay.Recording && im1 != null && im1.InputSource != null && im1.InputSource.Pause.WasPerformedThisFrame)
                {
                    uint id1 = EnemySync.PathId(__instance.transform);
                    Send(w =>
                    {
                        w.Write(KindSkipVote);
                        w.Write(id1);
                    });
                }
                return true;
            }
            uint id = EnemySync.PathId(__instance.transform);
            ShowVotes(id);
            InputManager im = MonoSingleton<InputManager>.Instance;
            if (im == null || im.InputSource == null || !im.InputSource.Pause.WasPerformedThisFrame)
            {
                return false;
            }
            OptionsManager om = MonoSingleton<OptionsManager>.Instance;
            if (om != null && om.paused)
            {
                om.UnPause();
            }
            if (votes.TryGetValue(id, out Dictionary<int, float> v) && v.ContainsKey(Room.MyId))
            {
                return false;  // (voted already)
            }
            Send(w =>
            {
                w.Write(KindSkipVote);
                w.Write(id);
            });
            Vote(id, Room.MyId);
            return false;
        }

        // More than half of those in the level.
        private static int Needed() => (Room.Peers.Count + 1) / 2 + 1;

        private static void Vote(uint id, int who)
        {
            if (!votes.TryGetValue(id, out Dictionary<int, float> v))
            {
                votes[id] = v = new Dictionary<int, float>();
            }
            if (v.ContainsKey(who))
            {
                return;
            }
            v[who] = Time.unscaledTime;
            int needed = Needed();
            string name = who == Room.MyId ? "YOU" : Typing.Escape(Party.NameOf(who).ToUpperInvariant());
            PartyChat.Event($"{name} VOTED TO SKIP THE CUTSCENE  ·  {v.Count}/{needed}");
            if (v.Count >= needed)
            {
                Skip(id);
            }
        }

        // As ULTRAKILL's own skip (CutsceneSkip.LateUpdate), for everyone at once.
        private static void Skip(uint id)
        {
            votes.Remove(id);
            CutsceneSkip cs = Find<CutsceneSkip>(id);
            if (cs == null || !cs.isActiveAndEnabled)
            {
                Plugin.Log.LogInfo("KillNet: the cutscene skipped by vote isn't playing here");
                RestoreSkipText();
                return;
            }
            applying = true;
            try
            {
                skipWaiting(cs) = false;
                skipPrint(cs) = false;
                OptionsManager om = MonoSingleton<OptionsManager>.Instance;
                if (om != null && om.paused)
                {
                    om.UnPause();
                }
                cs.onSkip?.Invoke();
                CutsceneSkipText t = MonoSingleton<CutsceneSkipText>.Instance;
                if (t != null)
                {
                    t.Hide();
                }
                float left = skipTimeLeft(cs);
                if (left > 0f)
                {
                    StatsManager sm = MonoSingleton<StatsManager>.Instance;
                    if (sm != null)
                    {
                        sm.seconds += left;
                    }
                    WeaponCharges wc = MonoSingleton<WeaponCharges>.Instance;
                    if (wc != null)
                    {
                        wc.Charge(left);
                    }
                }
                RestoreSkipText();
                PartyChat.Event("CUTSCENE SKIPPED");
                Plugin.Log.LogInfo("KillNet: cutscene skipped by vote");
            }
            finally
            {
                applying = false;
            }
        }

        private static void ShowVotes(uint id)
        {
            CutsceneSkipText t = MonoSingleton<CutsceneSkipText>.Instance;
            TMP_Text txt = t != null ? t.GetComponent<TMP_Text>() : null;
            if (txt == null)
            {
                return;
            }
            if (skipTextWas == null)
            {
                skipTextWas = txt.text;
            }
            int n = votes.TryGetValue(id, out Dictionary<int, float> v) ? v.Count : 0;
            string want = $"{skipTextWas}  <color=#DB5447>SKIP VOTES {n}/{Needed()}</color>";
            if (txt.text != want)
            {
                txt.text = want;
            }
        }

        private static void RestoreSkipText()
        {
            if (skipTextWas == null)
            {
                return;
            }
            try
            {
                CutsceneSkipText t = MonoSingleton<CutsceneSkipText>.Instance;
                TMP_Text txt = t != null ? t.GetComponent<TMP_Text>() : null;
                if (txt != null)
                {
                    txt.text = skipTextWas;
                }
            }
            catch
            {
            }
            skipTextWas = null;
        }

        private static void SkipOff(CutsceneSkip __instance)
        {
            try
            {
                if (__instance != null)
                {
                    votes.Remove(EnemySync.PathId(__instance.transform));
                }
            }
            catch
            {
            }
            RestoreSkipText();
        }

        // ---- boss intros ----

        private static void ActivateBefore(ObjectActivator __instance, out bool __state) => __state = __instance != null && __instance.activated;

        private static void ActivateAfter(ObjectActivator __instance, bool __state)
        {
            if (!Telling || __state || __instance == null || !__instance.activated)
            {
                return;
            }
            try
            {
                if (!StartsABoss(__instance))
                {
                    return;
                }
                uint id = EnemySync.PathId(__instance.transform);
                Vector3 at = __instance.transform.position;
                Send(w =>
                {
                    w.Write(KindBoss);
                    w.Write(id);
                    w.Write(at.x);
                    w.Write(at.y);
                    w.Write(at.z);
                });
                Plugin.Log.LogInfo($"KillNet: {__instance.name} starts a boss: told the others near it");
            }
            catch
            {
            }
        }

        // One that switches a boss on, or starts his intro.
        private static bool StartsABoss(ObjectActivator oa)
        {
            UltrakillEvent ev = oa.events;
            if (ev == null)
            {
                return false;
            }
            if (ev.toActivateObjects != null)
            {
                foreach (GameObject g in ev.toActivateObjects)
                {
                    if (g != null && HasBoss(g))
                    {
                        return true;
                    }
                }
            }
            if (ev.onActivate != null)
            {
                for (int i = 0; i < ev.onActivate.GetPersistentEventCount(); i++)
                {
                    if (ev.onActivate.GetPersistentTarget(i) is Component c && c != null
                        && (c.GetType().Name.IndexOf("Intro", StringComparison.Ordinal) >= 0 || HasBoss(c.gameObject)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A boss itself (or its intro): the boss near the top of what's switched on, with few other
        // enemies beside it. A level section switched back on (a checkpoint's reactivator) holds many
        // enemies, its boss deep inside: not one.
        private const int BossDepth = 3, BossCompany = 4;

        private static bool HasBoss(GameObject g)
        {
            foreach (MonoBehaviour m in g.GetComponents<MonoBehaviour>())
            {
                if (m != null && m.GetType().Name.EndsWith("Intro", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            EnemyIdentifier[] enemies = g.GetComponentsInChildren<EnemyIdentifier>(true);
            if (enemies.Length > BossCompany)
            {
                return false;
            }
            foreach (EnemyIdentifier e in enemies)
            {
                if (e != null && e.isBoss && Depth(e.transform, g.transform) <= BossDepth)
                {
                    return true;
                }
            }
            foreach (BossHealthBar bar in g.GetComponentsInChildren<BossHealthBar>(true))
            {
                if (bar != null && Depth(bar.transform, g.transform) <= BossDepth)
                {
                    return true;
                }
            }
            return false;
        }

        private static int Depth(Transform t, Transform top)
        {
            int d = 0;
            for (; t != null && t != top; t = t.parent)
            {
                d++;
            }
            return d;
        }

        private static void HandleBoss(int from, BinaryReader r)
        {
            uint id = r.ReadUInt32();
            var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            NewMovement nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null || (nm.transform.position - at).sqrMagnitude > BossNear * BossNear)
            {
                return;  // (not near it: their own when they come)
            }
            ObjectActivator oa = Find<ObjectActivator>(id);
            if (oa == null || oa.activated || !oa.gameObject.activeInHierarchy)
            {
                return;  // (none here, done already, or in a room this player hasn't reached)
            }
            applying = true;
            oa.Activate(false);
            Plugin.Log.LogInfo($"KillNet: player {from} started a boss ({oa.name}): started here too");
        }

        // ---- a parried projectile: seen flying back by the others (Shots) ----

        private static void ParryAfter(Projectile proj)
        {
            try
            {
                if (proj != null)
                {
                    Shots.Track(proj);
                }
            }
            catch
            {
            }
        }
    }
}
