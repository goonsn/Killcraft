using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // In a level, the other party members at the top left, a card each: their colour, the crown on
    // the party host, their name, health, and what's happening (a checkpoint, a skull they hold, dead,
    // the level finished with its rank and time, or somewhere else). Names over their V1s here. And
    // once this player's finished a level, who's still in it, then a notice when they all are.
    internal static class PartyHud
    {
        private sealed class Card
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public Image Back, Bar, Crown, HpBack, HpFill, Skull;
            public TextMeshProUGUI Name, Hp, Status, BarText;
        }

        private static Canvas canvas;
        private static RectTransform cards, tags, banner;
        private static TextMeshProUGUI bannerText;
        private static readonly Dictionary<int, Card> shown = new Dictionary<int, Card>();
        private sealed class Tag
        {
            public RectTransform Root, Box;
            public TextMeshProUGUI Text;
            public CanvasGroup Group;
        }

        private static readonly Dictionary<int, Tag> nameTags = new Dictionary<int, Tag>();
        private static readonly List<int> gone = new List<int>();
        private static string waitScene = "";
        private static int waitingWas;
        private static float bannerUntil = -100f;
        private static string bannerLine = "";

        public static void Frame(bool inLevel, bool paused)
        {
            Waiting();
            bool want = (inLevel || Time.unscaledTime < bannerUntil) && !paused && !PartyMenu.Open;
            if (!want)
            {
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
            }
            float scale = Mathf.Clamp(Plugin.HudScale.Value, 0.5f, 2f);
            cards.localScale = new Vector3(scale, scale, 1f);
            Cards(inLevel && Plugin.PartyHudOn.Value);
            Tags(inLevel && Plugin.NameTags.Value);
            Banner();
        }

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft party HUD", 10, true);
            cards = Ui.Rect(canvas.transform, "Cards", 24f, 40f, 400f, 900f);
            tags = Ui.Fill(canvas.transform, "Name tags");
            banner = Ui.Rect(canvas.transform, "Banner", 0f, 0f, 900f, 70f);
            banner.anchorMin = banner.anchorMax = new Vector2(0.5f, 1f);
            banner.pivot = new Vector2(0.5f, 1f);
            banner.anchoredPosition = new Vector2(0f, -150f);
            Ui.Picture(Ui.Fill(banner, "Back"), Ui.ChamFill, new Color(0f, 0f, 0f, 0.7f));
            Ui.Picture(Ui.Fill(banner, "Line"), Ui.ChamLine, Ui.Red);
            bannerText = Ui.Label(Ui.Fill(banner, "Text"), "", 34f, Color.white, TextAlignmentOptions.Center);
            banner.gameObject.SetActive(false);
        }

        private static Card MakeCard()
        {
            var c = new Card();
            c.Root = Ui.Rect(cards, "Card", 0f, 0f, 340f, 92f);
            c.Group = c.Root.gameObject.AddComponent<CanvasGroup>();
            c.Root.localEulerAngles = new Vector3(0f, 0f, -3f);
            // (Just around what's on it: the name and the health bar, and the status line under them
            // only when there is one.)
            c.Back = Ui.Image(c.Root, null, new Color(0f, 0f, 0f, 0.35f), 0f, 0f, 298f, 64f);
            c.Bar = Ui.Image(c.Root, null, Color.white, 0f, 4f, 5f, 58f);
            // (The host's crown sat on the name, over its first letters, as UltraLink's on the picture.)
            c.Crown = Ui.Image(c.Root, Ui.Crown, Color.white, 6f, -17f, 28f, 21f);
            c.Crown.rectTransform.localEulerAngles = new Vector3(0f, 0f, 18f);
            c.Name = Ui.Text(c.Root, "", 28f, Color.white, TextAlignmentOptions.MidlineLeft, 14f, 0f, 284f, 32f);
            c.HpBack = Ui.Image(c.Root, Ui.RoundFill, new Color(0.35f, 0.04f, 0.03f, 0.8f), 12f, 34f, 280f, 24f);
            c.HpFill = Ui.Image(c.Root, Ui.RoundFill, Ui.Red, 12f, 34f, 280f, 24f);
            c.Hp = Ui.Text(c.Root, "", 18f, Color.white, TextAlignmentOptions.MidlineLeft, 22f, 34f, 120f, 24f);
            c.BarText = Ui.Text(c.Root, "", 18f, Color.white, TextAlignmentOptions.Center, 12f, 34f, 280f, 24f);
            c.Skull = Ui.Image(c.Root, Ui.Skull, Ui.Cyan, 14f, 64f, 20f, 18f);
            c.Skull.preserveAspect = true;
            c.Status = Ui.Text(c.Root, "", 18f, Color.white, TextAlignmentOptions.MidlineLeft, 14f, 62f, 284f, 24f);
            return c;
        }

        private static void Cards(bool on)
        {
            gone.Clear();
            foreach (int id in shown.Keys)
            {
                gone.Add(id);
            }
            int row = 0;
            if (on && Party.Members.Count > 1)
            {
                foreach (Party.Member m in Party.Members)
                {
                    if (m.Id == Room.MyId)
                    {
                        continue;
                    }
                    if (!shown.TryGetValue(m.Id, out Card c))
                    {
                        shown[m.Id] = c = MakeCard();
                    }
                    gone.Remove(m.Id);
                    c.Root.anchoredPosition = new Vector2(0f, -row * 104f);
                    Fill(c, m);
                    row++;
                }
            }
            foreach (int id in gone)
            {
                Object.Destroy(shown[id].Root.gameObject);
                shown.Remove(id);
            }
        }

        private static void Fill(Card c, Party.Member m)
        {
            Profiles.Info i = Profiles.Of(m.Id);
            Color color = i.Color;
            c.Bar.color = color;
            c.Crown.gameObject.SetActive(m.PartyHost);
            c.Name.text = Typing.Escape(Profiles.NameOf(m.Id));
            bool here = i.Fresh && i.Scene == SceneHelper.CurrentScene && i.State != Profiles.StateMenu;
            bool dead = here && i.State == Profiles.StateDead;
            bool done = i.Fresh && i.State == Profiles.StateDone;
            c.Group.alpha = dead ? 0.5f : 1f;
            c.Name.color = dead ? Ui.Grey : Color.white;
            float hp = Mathf.Clamp01(i.Hp / 100f);
            c.HpFill.rectTransform.sizeDelta = new Vector2(Mathf.Max(24f, 280f * hp), 24f);
            c.HpFill.gameObject.SetActive(!dead && !done && hp > 0f && here);
            c.HpBack.color = done ? Color.white : dead ? new Color(0.2f, 0.2f, 0.2f, 0.9f) : new Color(0.35f, 0.04f, 0.03f, 0.8f);
            c.Hp.text = here && !dead && !done ? "+" + Mathf.Max(0, i.Hp) : "";
            c.BarText.text = done ? "<color=#000000>LEVEL COMPLETE</color>" : dead ? "<color=#E05040>DEAD</color>" : !here ? "<color=#AAAAAA>-</color>" : "";
            c.BarText.alignment = TextAlignmentOptions.Center;
            c.Skull.gameObject.SetActive(false);
            string status;
            if (!i.Fresh)
            {
                status = "<color=#888888>CONNECTING...</color>";
            }
            else if (done)
            {
                string scene = i.Scene == SceneHelper.CurrentScene ? "" : Party.LevelName(i.Scene).ToUpperInvariant() + "  ";
                status = $"<color=#FFD02E>{scene}{(string.IsNullOrEmpty(i.Rank) ? "" : i.Rank + " · ")}{Clock(i.Seconds)}</color>";
            }
            else if (i.State == Profiles.StateMenu)
            {
                status = "<color=#AAAAAA>IN THE MENU</color>";
            }
            else if (!here)
            {
                status = $"<color=#AAAAAA>IN {Typing.Escape(Party.LevelName(i.Scene).ToUpperInvariant())}</color>";
            }
            else if (dead)
            {
                if ((i.Flags & Profiles.FlagSpectating) != 0)
                {
                    c.Skull.gameObject.SetActive(true);
                    c.Skull.sprite = Ui.Eye;
                    c.Skull.color = new Color(1f, 0.63f, 0.19f);
                    status = (i.Flags & Profiles.FlagWatching) != 0 ? "      <color=#FFA030>WATCHING</color>" : "      <color=#FFA030>SPECTATING</color>";
                }
                else
                {
                    status = "<color=#FF9A2E>DEAD</color>";
                }
            }
            else if ((i.Flags & (Profiles.FlagBlueSkull | Profiles.FlagRedSkull)) != 0)
            {
                bool blue = (i.Flags & Profiles.FlagBlueSkull) != 0;
                c.Skull.gameObject.SetActive(true);
                c.Skull.sprite = Ui.Skull;
                c.Skull.color = blue ? new Color(0.25f, 0.55f, 1f) : new Color(1f, 0.25f, 0.2f);
                status = blue ? "      <color=#4D8CFF>BLUE SKULL</color>" : "      <color=#FF4033>RED SKULL</color>";
            }
            else if ((i.Flags & Profiles.FlagCheckpoint) != 0)
            {
                status = "CHECKPOINT";
            }
            else if ((i.Flags & Profiles.FlagPaused) != 0)
            {
                status = "<color=#AAAAAA>PAUSED</color>";
            }
            else
            {
                status = "";
            }
            c.Status.text = status;
            c.Back.rectTransform.sizeDelta = new Vector2(298f, status.Length > 0 ? 88f : 64f);
        }

        public static string Clock(float seconds)
        {
            int m = (int)(seconds / 60f);
            float s = seconds - m * 60f;
            return $"{m}:{s:00.000}";
        }

        // Their names over their V1s (or Minecraft players) here: white, on a dark box (UltraLink's).
        private static void Tags(bool on)
        {
            Camera cam = Camera.main;
            gone.Clear();
            foreach (int id in nameTags.Keys)
            {
                gone.Add(id);
            }
            if (on && cam != null)
            {
                var area = (RectTransform)canvas.transform;
                foreach (Party.Member m in Party.Members)
                {
                    if (m.Id == Room.MyId || !RemotePlayers.HeadOf(m.Id, out Vector3 head))
                    {
                        continue;
                    }
                    Vector3 sp = cam.WorldToScreenPoint(head + Vector3.up * 0.9f * Coords.U);
                    if (sp.z <= 0.5f)
                    {
                        continue;
                    }
                    if (!nameTags.TryGetValue(m.Id, out Tag t))
                    {
                        t = new Tag();
                        t.Root = Ui.Rect(tags, "Tag", 0f, 0f, 400f, 40f);
                        t.Root.anchorMin = t.Root.anchorMax = new Vector2(0.5f, 0.5f);
                        t.Root.pivot = new Vector2(0.5f, 0f);
                        t.Group = t.Root.gameObject.AddComponent<CanvasGroup>();
                        t.Group.blocksRaycasts = false;
                        t.Box = Ui.Rect(t.Root, "Box", 0f, 0f, 100f, 30f);
                        t.Box.anchorMin = t.Box.anchorMax = new Vector2(0.5f, 0f);
                        t.Box.pivot = new Vector2(0.5f, 0f);
                        t.Box.anchoredPosition = Vector2.zero;
                        Ui.Picture(t.Box, Ui.RoundFill, new Color(0f, 0f, 0f, 0.6f)).raycastTarget = false;
                        t.Text = Ui.Label(Ui.Fill(t.Box, "Text"), "", 24f, Color.white, TextAlignmentOptions.Center);
                        t.Text.raycastTarget = false;
                        nameTags[m.Id] = t;
                    }
                    gone.Remove(m.Id);
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(area, sp, null, out Vector2 local);
                    t.Root.anchoredPosition = local;
                    // (As a thing in the world: smaller further off, as their V1 is, faded out far away.)
                    float size = Mathf.Clamp(22f * 12f * Coords.U / Mathf.Max(sp.z, 1f), 7f, 22f);
                    t.Text.fontSize = size;
                    t.Group.alpha = Mathf.Clamp01(1.5f - sp.z / (90f * Coords.U));
                    t.Root.gameObject.SetActive(m.Id != RemotePlayers.HiddenId);
                    string talk = Voice.IsSpeaking(m.Id) ? " <color=#4FE6FF>))</color>" : "";
                    t.Text.text = Typing.Escape(Profiles.NameOf(m.Id)) + talk;
                    t.Box.sizeDelta = new Vector2(t.Text.preferredWidth + size * 0.9f, size * 1.45f);
                }
            }
            foreach (int id in gone)
            {
                Object.Destroy(nameTags[id].Root.gameObject);
                nameTags.Remove(id);
            }
        }

        // Once this player's finished a level: the others still in it; when they've all finished, a notice.
        private static void Waiting()
        {
            Profiles.Info me = Profiles.Mine;
            if (me.State == Profiles.StateDone && Party.IsLevel(me.Scene) && waitScene != me.Scene)
            {
                waitScene = me.Scene;
                waitingWas = Still(waitScene);
                if (waitingWas > 0)
                {
                    PartyChat.System($"You finished {Typing.Escape(Party.LevelName(waitScene))}: waiting for {waitingWas} more. Play on meanwhile, you'll hear when they're done.");
                }
            }
            if (waitScene == "")
            {
                return;
            }
            int still = Still(waitScene);
            if (still == 0 && waitingWas > 0)
            {
                bannerLine = $"EVERYONE FINISHED {Party.LevelName(waitScene).ToUpperInvariant()}";
                bannerUntil = Time.unscaledTime + 6f;
                PartyChat.System($"Everyone's finished {Typing.Escape(Party.LevelName(waitScene))}!");
                Plugin.Log.LogInfo($"party: everyone finished {waitScene}");
                Ui.ClickSound();
                waitScene = "";
            }
            else if (still == 0)
            {
                waitScene = "";
            }
            waitingWas = still;
            if (me.State != Profiles.StateDone && me.Scene == waitScene)
            {
                // (Playing it again.)
                waitScene = "";
            }
        }

        // How many others are still playing a level (there, not finished).
        private static int Still(string scene)
        {
            int n = 0;
            foreach (Party.Member m in Party.Members)
            {
                if (m.Id == Room.MyId)
                {
                    continue;
                }
                Profiles.Info i = Profiles.Of(m.Id);
                if (i.Fresh && i.Scene == scene && (i.State == Profiles.StatePlaying || i.State == Profiles.StateDead))
                {
                    n++;
                }
            }
            return n;
        }

        public static int WaitingFor => waitScene == "" ? 0 : waitingWas;

        private static void Banner()
        {
            string line = Time.unscaledTime < bannerUntil ? bannerLine
                : WaitingFor > 0 ? $"<size=24>WAITING FOR {WaitingFor} TO FINISH {Party.LevelName(waitScene).ToUpperInvariant()}</size>" : "";
            bool on = line.Length > 0 && (Time.unscaledTime < bannerUntil || SceneHelper.CurrentScene == waitScene);
            if (banner.gameObject.activeSelf != on)
            {
                banner.gameObject.SetActive(on);
            }
            if (on)
            {
                bannerText.text = line;
                // (This player's level done: ULTRAKILL's rank screen is up there; at the bottom instead.)
                bool done = Profiles.Mine.State == Profiles.StateDone;
                Vector2 anchor = done ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 1f);
                if (banner.anchorMin != anchor)
                {
                    banner.anchorMin = banner.anchorMax = anchor;
                    banner.pivot = anchor;
                    banner.anchoredPosition = done ? new Vector2(0f, 24f) : new Vector2(0f, -150f);
                    banner.localScale = done ? Vector3.one * 0.7f : Vector3.one;
                }
            }
        }
    }
}
