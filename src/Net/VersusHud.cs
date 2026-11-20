using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Killcraft
{
    // What VERSUS shows. A line at the top for a few seconds: someone else finishing while this player
    // plays on (or this player finishing a level of a whole-layer race). Once this player's finished (the
    // level, or the layer): a card at the top over ULTRAKILL's results - VERSUS and how it's won, their
    // place big, what's happened (YOU FINISHED, waiting for whoever's still playing; then YOU WIN, or who
    // did) and everyone's line under it (their place, name, rank, style, time; a whole layer's: levels,
    // style, total time). Black, ULTRAKILL's white outline, as its own results and the scoreboard.
    internal static class VersusHud
    {
        private const float CardW = 900f, ToastW = 760f, RowH = 44f, ColX = 420f, ColStep = 160f;

        private static Canvas canvas;
        private static RectTransform toast, card, rows;
        private static CanvasGroup toastGroup;
        private static Image toastStrip;
        private static TextMeshProUGUI toastText, cardMode, cardPlace, cardTitle, cardSub, cardHead;
        private static float toastUntil = -100f, noteUntil = -100f, builtAt = -100f;
        private static string note = "", shownKey = "";

        private static int resultFor;
        private static bool resultLayer;
        private static int grindWinner;
        private static string grindHow = "";

        // ---- what happened ----

        public static void Finished(int id, int place, float seconds)
        {
            string at = place > 0 ? $" {Score.Ordinal(place)}" : "";
            Toast(Profiles.Of(id).Color, $"<color=#{Ui.Hex(Profiles.Of(id).Color)}>{Typing.Escape(Profiles.NameOf(id).ToUpperInvariant())}</color>  FINISHED{at}  ·  {PartyHud.Clock(seconds)}");
        }

        public static void Result(int id, bool layer)
        {
            resultFor = id;
            resultLayer = layer;
            shownKey = "";
            Ui.ClickSound();
        }

        public static void GrindResult(int id, string how)
        {
            grindWinner = id;
            grindHow = how ?? "";
            shownKey = "";
            Ui.ClickSound();
        }

        // A note (waiting for the others): on the card if it's up, else a line at the top.
        public static void Note(string line)
        {
            note = line;
            noteUntil = Time.unscaledTime + 3.5f;
            shownKey = "";
            if (!CardWanted())
            {
                Toast(Color.white, line);
            }
            Ui.ClickSound();
        }

        public static void Toast(Color strip, string line)
        {
            Make();
            toastStrip.color = strip;
            toastText.text = line;
            toastUntil = Time.unscaledTime + 4f;
            Ui.Refont(toast);
        }

        // ---- each frame (Versus) ----

        private static bool CardWanted()
        {
            if (!Versus.On)
            {
                return false;
            }
            if (Versus.Grind)
            {
                return grindWinner != 0 || Profiles.Mine.State == Profiles.StateDead;
            }
            return Versus.WholeLayer ? Versus.Mine.LayerDone : Versus.Mine.Done;
        }

        private static bool wasDone;

        public static void Frame()
        {
            if (!Versus.On)
            {
                Hide();
                return;
            }
            // (A level of a whole-layer race finished, not its last: a line, on with the next.)
            bool done = Versus.Mine.Done;
            if (done && !wasDone && Versus.WholeLayer && !Versus.Mine.LayerDone)
            {
                Toast(Profiles.Mine.Color, $"YOU FINISHED {Typing.Escape(Party.LevelName(Versus.Mine.Scene).ToUpperInvariant())}  ·  LAYER {PartyHud.Clock(Versus.Mine.LayerSeconds)}");
            }
            wasDone = done;
            Make();
            float now = Time.unscaledTime;
            float ta = Mathf.Clamp01((toastUntil - now) / 0.4f);
            toast.gameObject.SetActive(ta > 0f);
            toastGroup.alpha = ta;
            bool want = CardWanted();
            if (card.gameObject.activeSelf != want)
            {
                card.gameObject.SetActive(want);
                shownKey = "";
            }
            if (want && now - builtAt > 0.25f)
            {
                builtAt = now;
                Fill();
            }
        }

        public static void LevelStarted()
        {
            resultFor = grindWinner = 0;
            grindHow = note = "";
            noteUntil = -100f;
            wasDone = false;
            shownKey = "";
        }

        public static void Hide()
        {
            if (canvas != null && canvas.gameObject.activeSelf)
            {
                canvas.gameObject.SetActive(false);
            }
        }

        private static void Make()
        {
            if (canvas == null)
            {
                Build();
            }
            if (!canvas.gameObject.activeSelf)
            {
                canvas.gameObject.SetActive(true);
            }
        }

        private static void Build()
        {
            canvas = Ui.Canvas("Killcraft versus", 31, true);
            toast = Ui.Rect(canvas.transform, "Finished", 0f, 0f, ToastW, 56f);
            toast.anchorMin = toast.anchorMax = toast.pivot = new Vector2(0.5f, 1f);
            toast.anchoredPosition = new Vector2(0f, -110f);
            toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
            Ui.Picture(Ui.Fill(toast, "Back"), Ui.ChamFill, new Color(0f, 0f, 0f, 0.66f));
            toastStrip = Ui.Image(toast, null, Color.white, 16f, 14f, 6f, 28f);
            toastText = Ui.Text(toast, "", 28f, Color.white, TextAlignmentOptions.MidlineLeft, 34f, 0f, ToastW - 50f, 56f);
            toast.gameObject.SetActive(false);

            card = Ui.Rect(canvas.transform, "Result", 0f, 0f, CardW, 200f);
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 1f);
            card.anchoredPosition = new Vector2(0f, -90f);
            Ui.Picture(Ui.Fill(card, "Back"), null, new Color(0f, 0f, 0f, 0.8f));
            Ui.Sliced(Ui.Picture(Ui.Fill(card, "Frame"), Ui.BoxLine, Color.white), 3f);
            Ui.Text(card, "VERSUS", 22f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 28f, 16f, 300f, 28f);
            cardMode = Ui.Text(card, "", 22f, Ui.Grey, TextAlignmentOptions.MidlineRight, CardW - 428f, 16f, 400f, 28f);
            cardPlace = Ui.Text(card, "", 88f, Color.white, TextAlignmentOptions.TopLeft, 28f, 40f, 260f, 100f);
            cardPlace.overflowMode = TextOverflowModes.Overflow;
            cardTitle = Ui.Text(card, "", 40f, Color.white, TextAlignmentOptions.MidlineLeft, 250f, 56f, CardW - 280f, 48f);
            cardSub = Ui.Text(card, "", 18f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 250f, 104f, CardW - 280f, 26f);
            cardHead = Ui.Text(card, "", 18f, Ui.Grey, TextAlignmentOptions.MidlineLeft, 0f, 150f, CardW, 26f);
            cardHead.overflowMode = TextOverflowModes.Overflow;
            Ui.Line(card, Color.white, 24f, 180f, CardW - 48f, 2f);
            rows = Ui.Rect(card, "Rows", 0f, 188f, CardW, 0f);
            card.gameObject.SetActive(false);
            Ui.Refont(canvas.transform);
        }

        private static string Col(string s, float x) => $"<pos={x:0}>{s}";

        private static void Fill()
        {
            bool grind = Versus.Grind, layer = Versus.WholeLayer && !grind, score = Versus.ByScore;
            List<Versus.Run> order = grind ? Versus.InLevel() : Versus.Standings();
            if (grind)
            {
                order.Sort((a, b) =>
                {
                    int pa = Versus.PlaceOf(a.Id), pb = Versus.PlaceOf(b.Id);
                    return (pa <= 0 ? 99 : pa).CompareTo(pb <= 0 ? 99 : pb);
                });
            }
            int winner = grind ? grindWinner : resultFor;
            bool decided = winner != 0 && (grind || resultLayer == layer);
            string noteNow = Time.unscaledTime < noteUntil ? note : "";
            var key = new System.Text.StringBuilder();
            foreach (Versus.Run r in order)
            {
                key.Append(r.Id).Append(r.Done).Append(r.LayerDone).Append(r.Points).Append(r.Style / 10).Append((int)(r.Seconds * 10f))
                    .Append(r.Levels).Append(Score.Of(r.Id).Wave).Append(';');
            }
            key.Append(winner).Append(decided).Append(noteNow);
            if (key.ToString() == shownKey)
            {
                return;
            }
            shownKey = key.ToString();

            cardMode.text = grind ? "LAST ONE STANDING" : $"{(score ? "SCORE" : "TIME")}  ·  {(layer ? "WHOLE LAYER" : "EACH LEVEL")}";
            int myPlace = 0;
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].Id == Room.MyId)
                {
                    myPlace = grind ? Versus.PlaceOf(Room.MyId) : i + 1;
                }
            }
            Versus.Run me = Versus.Mine;
            List<int> waiting = new List<int>();
            foreach (Versus.Run r in order)
            {
                if (r.Id != Room.MyId && !(grind ? Profiles.Of(r.Id).State == Profiles.StateDead : layer ? r.LayerDone : r.Done))
                {
                    waiting.Add(r.Id);
                }
            }
            string layerName = Versus.LayerName(me.Layer);
            if (decided)
            {
                bool won = winner == Room.MyId;
                cardPlace.text = Score.Ordinal(won ? 1 : Mathf.Max(myPlace, 2));
                cardPlace.color = won ? Ui.Yellow : Color.white;
                Color wc = Profiles.Of(winner).Color;
                string what = layer ? " " + layerName : "";
                cardTitle.text = won ? $"YOU WIN{what}" : $"<color=#{Ui.Hex(wc)}>{Typing.Escape(Profiles.NameOf(winner).ToUpperInvariant())}</color> WINS{what}";
                cardTitle.color = won ? Ui.Yellow : Color.white;
                if (grind)
                {
                    cardSub.text = grindHow.ToUpperInvariant();
                }
                else if (layer)
                {
                    cardSub.text = $"{layerName}, {me.Levels} LEVELS  ·  YOUR TOTAL {PartyHud.Clock(me.LayerSeconds)}";
                }
                else if (score)
                {
                    cardSub.text = "BEST RANK, THEN STYLE, THEN TIME";
                }
                else
                {
                    Versus.Run w = Versus.Of(winner);
                    cardSub.text = won ? "FIRST TO FINISH" : me.Done && w.Done ? $"{Mathf.Max(0f, me.Seconds - w.Seconds):0.000} S BEHIND" : "FIRST TO FINISH";
                }
            }
            else
            {
                cardPlace.text = grind ? "OUT" : myPlace > 0 ? Score.Ordinal(myPlace) : "...";
                cardPlace.color = Color.white;
                cardTitle.text = grind ? "YOU'RE OUT" : layer ? "YOU FINISHED THE LAYER" : "YOU FINISHED";
                cardTitle.color = Color.white;
                cardSub.text = waiting.Count > 0 ? $"WAITING FOR {Names(waiting)} TO FINISH" : "";
            }
            if (noteNow.Length > 0)
            {
                cardSub.text = noteNow;
            }

            string[] heads = grind ? new[] { "WAVE", "STYLE", "TIME" } : layer ? new[] { "LEVELS", "STYLE", "TOTAL TIME" } : new[] { "RANK", "STYLE", "TIME" };
            cardHead.text = Col(heads[0], ColX) + Col(heads[1], ColX + ColStep) + Col(heads[2], ColX + 2f * ColStep);
            Ui.Clear(rows);
            float y = 0f;
            for (int i = 0; i < order.Count; i++)
            {
                Versus.Run r = order[i];
                bool mine = r.Id == Room.MyId;
                bool over = grind ? Profiles.Of(r.Id).State == Profiles.StateDead || r.Id == grindWinner : layer ? r.LayerDone : r.Done;
                int place = grind ? Versus.PlaceOf(r.Id) : over ? i + 1 : 0;
                RectTransform row = Ui.Rect(rows, "Row", 0f, y, CardW, RowH);
                if (mine)
                {
                    Ui.Image(row, null, new Color(1f, 1f, 1f, 0.07f), 16f, 2f, CardW - 32f, RowH - 4f);
                }
                Ui.Image(row, null, Profiles.Of(r.Id).Color, 24f, 9f, 5f, RowH - 18f);
                Ui.Text(row, place > 0 ? Score.Ordinal(place) : "...", 24f, place == 1 ? Ui.Yellow : Color.white, TextAlignmentOptions.MidlineLeft, 40f, 0f, 70f, RowH);
                string name = mine ? Profiles.Mine.SteamName : Profiles.NameOf(r.Id);
                Ui.Text(row, Typing.Escape(name), 24f, Color.white, TextAlignmentOptions.MidlineLeft, 110f, 0f, ColX - 120f, RowH);
                string a, b, c;
                Color ac = Color.white;
                if (grind)
                {
                    a = Score.Of(r.Id).Wave.ToString();
                    b = Score.Of(r.Id).Style.ToString("N0");
                    c = PartyHud.Clock(mine ? Profiles.Mine.Seconds : Profiles.Of(r.Id).Seconds);
                }
                else if (layer)
                {
                    a = r.Levels.ToString();
                    b = (r.LayerStyle + (r.Done ? 0 : r.Style)).ToString("N0");
                    c = PartyHud.Clock(r.LayerSeconds + (r.Done ? 0f : r.Seconds));
                }
                else
                {
                    a = r.Done ? r.Rank : "-";
                    ac = r.Done ? Versus.RankColor(r.Rank) : Ui.Grey;
                    b = r.Style.ToString("N0");
                    c = PartyHud.Clock(r.Seconds);
                }
                Ui.Text(row, a, 24f, ac, TextAlignmentOptions.MidlineLeft, ColX, 0f, ColStep - 10f, RowH);
                Ui.Text(row, b, 24f, Ui.Yellow, TextAlignmentOptions.MidlineLeft, ColX + ColStep, 0f, ColStep - 10f, RowH);
                Ui.Text(row, c, 24f, over ? Color.white : Ui.Grey, TextAlignmentOptions.MidlineLeft, ColX + 2f * ColStep, 0f, ColStep + 40f, RowH);
                y += RowH;
            }
            card.sizeDelta = new Vector2(CardW, 188f + y + 16f);
            Ui.Refont(card);
        }

        private static string Names(List<int> ids)
        {
            var names = new List<string>();
            foreach (int id in ids)
            {
                names.Add(Profiles.NameOf(id).ToUpperInvariant());
            }
            return Typing.Escape(string.Join(", ", names));
        }
    }
}
