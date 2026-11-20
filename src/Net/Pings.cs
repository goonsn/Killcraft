using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // Pings: PingKey (the mouse wheel's button) marks what this player looks at for everyone in the
    // level - an enemy (the marker follows it until it dies) or a spot - in their colour, with what it is
    // and how far, kept at the screen's edge when it's off it. Enemies are found by their place in the
    // level (EnemySync's names), the same in everyone's game.
    internal static class Pings
    {
        public const byte Message = 63;
        private const float SpotFor = 8f, EnemyFor = 12f, Cooldown = 0.35f;

        private sealed class Ping
        {
            public int From;
            public Vector3 At;
            public uint Enemy;
            public EnemyIdentifier Eid;
            public string What = "";
            public float Until, Made;
            public RectTransform Root;
            public Image Mark;
            public TextMeshProUGUI Text;
        }

        private static readonly List<Ping> pings = new List<Ping>();
        private static Canvas canvas;
        private static float pingedAt = -100f;
        private static string scene = "";

        public static void Frame(NewMovement nm, bool inLevel, bool paused)
        {
            if (SceneHelper.CurrentScene != scene)
            {
                scene = SceneHelper.CurrentScene;
                Clear();
            }
            if (!inLevel || ReplayPlayer.Active)
            {
                Show(false);
                return;
            }
            if (Plugin.Pings.Value && !paused && nm != null && !nm.dead && !Typing.Active && !PartyMenu.Open && !KillcraftPanel.Open
                && Time.unscaledTime - pingedAt > Cooldown && InputKeys.Pressed(Plugin.PingKey.Value))
            {
                pingedAt = Time.unscaledTime;
                Mark(nm);
            }
            Show(!paused);
        }

        // ---- this player's ----

        private static void Mark(NewMovement nm)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            float far = 600f;
            Vector3 at = ray.origin + ray.direction * 60f;
            EnemyIdentifier eid = null;
            foreach (RaycastHit h in Physics.RaycastAll(ray, far, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.collider.transform.IsChildOf(nm.transform))
                {
                    continue;
                }
                if (h.distance < far)
                {
                    far = h.distance;
                    at = h.point;
                    eid = EnemyOf(h.collider);
                }
            }
            // (Not quite on one: the enemy nearest the middle of the screen, a little either way, not behind a wall.)
            if (eid == null)
            {
                eid = Aimed(ray, far + 2f);
                if (eid != null)
                {
                    at = eid.transform.position;
                }
            }
            if (eid != null && (eid.dead || Pvp.Is(eid)))
            {
                eid = null;
            }
            uint id = eid != null ? EnemySync.IdOf(eid) : 0;
            Add(Room.MyId, at, id, eid);
            Vector3 p = at;
            if (KillNet.Connected)
            {
                KillNet.Send(Message, KillNet.ToRoom, w =>
                {
                    w.Write(p.x);
                    w.Write(p.y);
                    w.Write(p.z);
                    w.Write(id);
                });
            }
        }

        private static EnemyIdentifier EnemyOf(Collider c)
        {
            EnemyIdentifier eid = c.GetComponentInParent<EnemyIdentifier>();
            if (eid != null)
            {
                return eid;
            }
            EnemyIdentifierIdentifier eii = c.GetComponentInParent<EnemyIdentifierIdentifier>();
            return eii != null ? eii.eid : null;
        }

        private static EnemyIdentifier Aimed(Ray ray, float far)
        {
            var tracker = MonoSingleton.GetInstance(typeof(EnemyTracker)) as EnemyTracker;
            List<EnemyIdentifier> current = tracker != null ? tracker.GetCurrentEnemies() : null;
            if (current == null)
            {
                return null;
            }
            EnemyIdentifier best = null;
            float bestAngle = 4f;
            foreach (EnemyIdentifier e in current)
            {
                if (e == null || e.dead || Pvp.Is(e))
                {
                    continue;
                }
                Vector3 to = e.transform.position + Vector3.up - ray.origin;
                if (to.magnitude > far)
                {
                    continue;
                }
                float angle = Vector3.Angle(ray.direction, to);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = e;
                }
            }
            return best;
        }

        // ---- everyone's ----

        public static void Handle(int from, BinaryReader r)
        {
            var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            uint id = r.ReadUInt32();
            if (!Plugin.Pings.Value || ReplayPlayer.Active)
            {
                return;
            }
            EnemyIdentifier eid = id != 0 ? EnemySync.Find(id) : null;
            Add(from, at, id, eid);
        }

        private static void Add(int from, Vector3 at, uint enemy, EnemyIdentifier eid)
        {
            // (One each: a new one of theirs takes the old one's place.)
            for (int i = pings.Count - 1; i >= 0; i--)
            {
                if (pings[i].From == from)
                {
                    Drop(i);
                }
            }
            var p = new Ping
            {
                From = from,
                At = at,
                Enemy = enemy,
                Eid = eid,
                What = eid != null ? Name(eid) : "",
                Made = Time.unscaledTime,
                Until = Time.unscaledTime + (eid != null ? EnemyFor : SpotFor),
            };
            pings.Add(p);
            Ui.ClickSound();
        }

        private static string Name(EnemyIdentifier eid)
        {
            string n = eid.enemyType.ToString();
            return Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();
        }

        private static void Drop(int i)
        {
            if (pings[i].Root != null)
            {
                Object.Destroy(pings[i].Root.gameObject);
            }
            pings.RemoveAt(i);
        }

        private static void Clear()
        {
            for (int i = pings.Count - 1; i >= 0; i--)
            {
                Drop(i);
            }
        }

        private static void Show(bool on)
        {
            if (canvas == null)
            {
                if (pings.Count == 0)
                {
                    return;
                }
                canvas = Ui.Canvas("Killcraft pings", 9, true);
            }
            if (canvas.gameObject.activeSelf != on)
            {
                canvas.gameObject.SetActive(on);
            }
            if (!on)
            {
                return;
            }
            Camera cam = Camera.main;
            var area = (RectTransform)canvas.transform;
            float now = Time.unscaledTime;
            for (int i = pings.Count - 1; i >= 0; i--)
            {
                Ping p = pings[i];
                if (p.Eid != null && (p.Eid.dead || !p.Eid.gameObject.activeInHierarchy))
                {
                    p.Until = Mathf.Min(p.Until, now + 0.6f);
                    p.Eid = null;
                }
                if (now > p.Until || cam == null)
                {
                    Drop(i);
                    continue;
                }
                if (p.Eid != null)
                {
                    p.At = p.Eid.transform.position + Vector3.up * 1.2f;
                }
                if (p.Root == null)
                {
                    Make(p);
                }
                Vector3 sp = cam.WorldToScreenPoint(p.At);
                bool behind = sp.z < 0f;
                if (behind)
                {
                    sp = new Vector3(Screen.width - sp.x, Screen.height - sp.y, 0f);
                }
                // (Off the screen: kept at its edge, pointing the way.)
                float m = 40f;
                bool off = behind || sp.x < m || sp.x > Screen.width - m || sp.y < m || sp.y > Screen.height - m;
                if (off)
                {
                    Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    Vector2 d = new Vector2(sp.x, sp.y) - c;
                    if (behind && d.sqrMagnitude < 1f)
                    {
                        d = Vector2.down;
                    }
                    float k = Mathf.Min((c.x - m) / Mathf.Max(Mathf.Abs(d.x), 0.001f), (c.y - m) / Mathf.Max(Mathf.Abs(d.y), 0.001f));
                    Vector2 e = c + d * Mathf.Min(k, 1f);
                    sp = new Vector3(e.x, e.y, 0f);
                }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(area, sp, null, out Vector2 local);
                p.Root.anchoredPosition = local;
                Color col = Profiles.Of(p.From).Color;
                float age = now - p.Made;
                // (A pop as it's made, then steady, fading at the end.)
                float pop = 1f + Mathf.Max(0f, 0.6f - age) * 1.2f;
                p.Root.localScale = Vector3.one * pop;
                float fade = Mathf.Clamp01((p.Until - now) / 0.6f);
                p.Mark.color = new Color(col.r, col.g, col.b, fade);
                float dist = Vector3.Distance(cam.transform.position, p.At);
                string who = p.From == Room.MyId ? "" : Typing.Escape(Profiles.NameOf(p.From)) + "\n";
                p.Text.text = $"<size=15>{who}</size>{(p.What.Length > 0 ? p.What + "  " : "")}{dist:0}M";
                p.Text.color = new Color(1f, 1f, 1f, fade);
            }
        }

        private static void Make(Ping p)
        {
            p.Root = Ui.Rect(canvas.transform, "Ping", 0f, 0f, 44f, 44f);
            p.Root.anchorMin = p.Root.anchorMax = p.Root.pivot = new Vector2(0.5f, 0.5f);
            p.Mark = Ui.Picture(Ui.Fill(p.Root, "Mark"), Ui.PingMark, Color.white);
            p.Mark.preserveAspect = true;
            p.Text = Ui.Text(p.Root, "", 18f, Color.white, TextAlignmentOptions.Top, -130f, 46f, 304f, 50f);
            p.Text.overflowMode = TextOverflowModes.Overflow;
            p.Text.outlineWidth = 0.2f;
            p.Text.outlineColor = new Color32(0, 0, 0, 220);
        }
    }
}
