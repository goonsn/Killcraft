using UnityEngine;
using UnityEngine.EventSystems;

namespace Killcraft
{
    // The party screen: in ULTRAKILL's main menu, or with a level paused, a "Party" button at the top
    // opens it. Two pages:
    //  - Party: who's in this world and where (Party), the world itself (open it to friends, who sees
    //    it in the server list; or leave a friend's for your own), and for the party host: hand the
    //    party on, kick players out, take everyone to a level;
    //  - Servers: the open worlds Steam knows of (ServerList), to join.
    // While the pointer is over it, ULTRAKILL's own menu underneath doesn't take the clicks.
    internal static class PartyScreen
    {
        private static bool shown, open, serversPage, searchedOnce;
        private static Rect button, window;
        private static Vector2 membersScroll, levelsScroll, serversScroll;
        private static string picked = "";
        private static EventSystem blocked;
        private static GUIStyle title, label, small, row, badge, pickedStyle;
        private static Texture2D back, rowBack;

        public static void Frame(bool inGame, bool paused, bool mainMenu, bool loading)
        {
            shown = !loading && (mainMenu || (inGame && paused)) && KillNet.Connected;
            if (!shown)
            {
                open = false;
            }
            float w = Mathf.Min(600f, Screen.width - 40f), h = Mathf.Min(620f, Screen.height - 90f);
            button = new Rect((Screen.width - 170f) / 2f, 8f, 170f, 30f);
            window = new Rect((Screen.width - w) / 2f, 44f, w, h);
            // (ULTRAKILL's menu under the pointer: not while it's over this.)
            bool over = false;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (shown && mouse != null)
            {
                Vector2 p = mouse.position.ReadValue();
                p.y = Screen.height - p.y;
                over = button.Contains(p) || (open && window.Contains(p));
            }
            if (over && blocked == null && EventSystem.current != null)
            {
                blocked = EventSystem.current;
                blocked.enabled = false;
            }
            else if (!over && blocked != null)
            {
                blocked.enabled = true;
                blocked = null;
            }
        }

        private static void Styles()
        {
            if (title != null)
            {
                return;
            }
            title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            label = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            row = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            badge = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            pickedStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            pickedStyle.normal.textColor = new Color(1f, 0.8f, 0.2f);
            back = new Texture2D(1, 1);
            back.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
            back.Apply();
            rowBack = new Texture2D(1, 1);
            rowBack.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.06f));
            rowBack.Apply();
        }

        public static void Gui()
        {
            if (!shown)
            {
                return;
            }
            Styles();
            int n = Mathf.Max(Party.Members.Count, 1);
            if (GUI.Button(button, open ? "Close party" : $"Party ({n})"))
            {
                open = !open;
            }
            if (!open)
            {
                return;
            }
            GUI.DrawTexture(window, back);
            GUILayout.BeginArea(new Rect(window.x + 12f, window.y + 10f, window.width - 24f, window.height - 20f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!serversPage, " Party", GUI.skin.button, GUILayout.Width(120f)))
            {
                serversPage = false;
            }
            if (GUILayout.Toggle(serversPage, " Servers", GUI.skin.button, GUILayout.Width(120f)) && !serversPage)
            {
                serversPage = true;
                if (!searchedOnce)
                {
                    searchedOnce = true;
                    ServerList.Refresh();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
            if (serversPage)
            {
                Servers();
            }
            else
            {
                PartyPage();
            }
            GUILayout.EndArea();
        }

        // ---- the party ----

        private static void PartyPage()
        {
            World();
            GUILayout.Space(8f);
            GUILayout.Label("In this world", title);
            membersScroll = GUILayout.BeginScrollView(membersScroll, GUILayout.Height(Mathf.Min(170f, 34f * Mathf.Max(Party.Members.Count, 1) + 10f)));
            if (Party.Members.Count == 0)
            {
                GUILayout.Label("Just you.", label);
            }
            bool iAmHost = Party.IAmHost;
            foreach (Party.Member m in Party.Members)
            {
                bool me = m.Id == Room.MyId;
                Rect r = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(r, rowBack);
                string where = m.Nether ? "the Nether" : Party.LevelName(m.Scene);
                GUI.Label(new Rect(r.x + 6f, r.y + 5f, r.width * 0.36f, 22f), m.Name + (me ? " (you)" : ""), row);
                string badges = (m.PartyHost ? "HOST " : "") + (m.WorldHost ? "WORLD" : "");
                GUI.Label(new Rect(r.x + r.width * 0.36f, r.y + 6f, 110f, 22f), badges, badge);
                GUI.Label(new Rect(r.x + r.width * 0.36f + 110f, r.y + 5f, r.width * 0.64f - 300f, 22f), where, small);
                if (iAmHost && !me)
                {
                    if (GUI.Button(new Rect(r.xMax - 180f, r.y + 3f, 96f, 24f), "Make host"))
                    {
                        Party.MakeHost(m);
                    }
                    if (!m.WorldHost && GUI.Button(new Rect(r.xMax - 78f, r.y + 3f, 72f, 24f), "Kick"))
                    {
                        Party.KickOut(m);
                    }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Space(8f);
            if (iAmHost)
            {
                Levels();
            }
            else
            {
                GUILayout.Label("The party host picks the levels and runs the enemies.", label);
                bool follow = GUILayout.Toggle(Plugin.FollowHost.Value, " Go where the party host takes everyone");
                if (follow != Plugin.FollowHost.Value)
                {
                    Plugin.FollowHost.Value = follow;
                }
            }
        }

        private static void World()
        {
            if (!Party.WorldKnown)
            {
                GUILayout.Label("Waiting for Steam...", label);
                return;
            }
            if (!Party.OwnWorld)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("You're in a friend's party.", label);
                if (GUILayout.Button("Leave", GUILayout.Width(90f)))
                {
                    Party.LeaveToOwnWorld();
                }
                GUILayout.EndHorizontal();
                return;
            }
            if (!Party.Open)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("No party: only you play.", label);
                if (GUILayout.Button("Make a party", GUILayout.Width(140f)))
                {
                    Party.OpenWorld();
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label(string.IsNullOrEmpty(ServerList.Code) ? "Your party is open." : $"Your party is open: code {ServerList.Code}", label);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("In the server list:", label, GUILayout.Width(130f));
            string[] names = { "Nobody", "Steam friends", "Everyone" };
            for (int i = 0; i < 3; i++)
            {
                if (GUILayout.Toggle(Plugin.ListWorld.Value == i, names[i], GUI.skin.button, GUILayout.Width(120f)) && Plugin.ListWorld.Value != i)
                {
                    Plugin.ListWorld.Value = i;
                    Plugin.Log.LogInfo($"party: world listed for {names[i]}");
                }
            }
            GUILayout.EndHorizontal();
        }

        private static void Levels()
        {
            GUILayout.Label("Take everyone to a level", title);
            levelsScroll = GUILayout.BeginScrollView(levelsScroll, GUILayout.ExpandHeight(true));
            foreach (var (scene, name) in Party.Levels())
            {
                if (GUILayout.Button(name, scene == picked ? pickedStyle : GUI.skin.button))
                {
                    picked = scene;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            GUI.enabled = Party.IsLevel(picked);
            if (GUILayout.Button(Party.IsLevel(picked) ? $"Start {Party.LevelName(picked)} for everyone" : "Pick a level above", GUILayout.Height(30f)))
            {
                Party.TakeEveryone(picked);
                open = false;
            }
            string here = SceneHelper.CurrentScene;
            GUI.enabled = Party.IsLevel(here) && Party.Members.Count > 1;
            if (GUILayout.Button("Bring everyone here", GUILayout.Width(170f), GUILayout.Height(30f)))
            {
                Party.TakeEveryone(here);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // ---- the server list ----

        private static void Servers()
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = !ServerList.Searching;
            if (GUILayout.Button(ServerList.Searching ? "Looking..." : "Refresh", GUILayout.Width(120f)))
            {
                ServerList.Refresh();
            }
            GUI.enabled = true;
            GUILayout.Label(ServerList.Status, label);
            GUILayout.EndHorizontal();
            GUILayout.Label("Joining puts you in their party (and your Minecraft in their world, if you both play with it).", small);
            GUILayout.Space(6f);
            serversScroll = GUILayout.BeginScrollView(serversScroll, GUILayout.ExpandHeight(true));
            foreach (ServerList.Entry e in ServerList.Found)
            {
                Rect r = GUILayoutUtility.GetRect(10f, 44f, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(r, rowBack);
                GUI.Label(new Rect(r.x + 6f, r.y + 3f, r.width - 120f, 22f), (e.Friend ? "★ " : "") + e.Name + "'s party", row);
                GUI.Label(new Rect(r.x + 6f, r.y + 23f, r.width - 120f, 20f),
                    $"{e.Level} · {e.Players} player{(e.Players == 1 ? "" : "s")}{(e.Pvp ? " · PvP on" : "")}", small);
                bool here = ServerList.Party != null && ServerList.Party.Value.Id.Value == e.Lobby;
                GUI.enabled = !here;
                if (GUI.Button(new Rect(r.xMax - 106f, r.y + 8f, 100f, 28f), here ? "You're in" : "Join"))
                {
                    ServerList.JoinParty(e.Lobby, e.Name);
                }
                GUI.enabled = true;
                GUILayout.Space(4f);
            }
            GUILayout.EndScrollView();
        }
    }
}
