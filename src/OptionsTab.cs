using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SettingsMenu.Components;
using SettingsMenu.Models;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Killcraft
{
    // Killcraft's settings in ULTRAKILL's own options menu: a KILLCRAFT button on its left (under
    // COLORS) and a page built the way ULTRAKILL builds its own (its SettingsPage, categories and
    // items: the same rows, toggles, sliders and dropdowns), for multiplayer, voice chat, the shaders,
    // gameplay and Minecraft. Each setting is Killcraft's config entry (read as the page opens, saved as
    // it changes); ULTRAKILL's own preferences aren't touched.
    internal static class OptionsTab
    {
        private const string PageName = "Killcraft", ButtonName = "Killcraft Button";
        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private abstract class Row
        {
            public SettingsItem Item;
            public abstract Action Bind(SettingsBuilderBase builder);
        }

        private sealed class ToggleRow : Row
        {
            public Func<bool> Get;
            public Action<bool> Set;

            public override Action Bind(SettingsBuilderBase b)
            {
                Toggle t = b.GetComponentInChildren<Toggle>(true);
                if (t == null)
                {
                    return null;
                }
                t.onValueChanged.AddListener(v => Set(v));
                return () => t.SetIsOnWithoutNotify(Get());
            }
        }

        private sealed class SliderRow : Row
        {
            public Func<float> Get;
            public Action<float> Set;
            public float Scale = 1f;

            public override Action Bind(SettingsBuilderBase b)
            {
                Slider s = b.GetComponentInChildren<Slider>(true);
                if (s == null)
                {
                    return null;
                }
                s.onValueChanged.AddListener(v => Set(v / Scale));
                return () => s.SetValueWithoutNotify(Get() * Scale);
            }
        }

        private sealed class DropdownRow : Row
        {
            public Func<int> Get;
            public Action<int> Set;

            public override Action Bind(SettingsBuilderBase b)
            {
                TMP_Dropdown d = b.GetComponentInChildren<TMP_Dropdown>(true);
                if (d == null)
                {
                    return null;
                }
                d.onValueChanged.AddListener(v => Set(v));
                return () =>
                {
                    d.SetValueWithoutNotify(Mathf.Clamp(Get(), 0, Mathf.Max(0, d.options.Count - 1)));
                    d.RefreshShownValue();
                };
            }
        }

        private sealed class ButtonRow : Row
        {
            public Action Click;

            public override Action Bind(SettingsBuilderBase b) => null;
        }

        // Refreshes the page's controls from the config as it opens; saves the shaders' as it closes.
        private sealed class PageState : MonoBehaviour
        {
            public readonly List<Action> Refresh = new List<Action>();

            private void OnEnable()
            {
                foreach (Action a in Refresh)
                {
                    try
                    {
                        a();
                    }
                    catch
                    {
                    }
                }
            }

            private void OnDisable() => ShaderPack.SaveNow();
        }

        private static SettingsPage model;
        private static readonly List<Row> rows = new List<Row>();
        private static readonly HashSet<int> done = new HashSet<int>();
        private static float lookedAt = -100f;
        private static bool patched;

        // ---- the page ----

        private static SettingsCategory Category(string title, string description = null)
        {
            var c = ScriptableObject.CreateInstance<SettingsCategory>();
            c.title = title;
            c.titleDecorator = "--";
            c.description = description;
            c.items = new List<SettingsItem>();
            c.unusedItems = new List<SettingsItem>();
            c.hideFlags = HideFlags.HideAndDontSave;
            return c;
        }

        private static SettingsItem Item(SettingsCategory c, string label, SettingsItemType type, string note = null)
        {
            var i = ScriptableObject.CreateInstance<SettingsItem>();
            i.label = label;
            i.itemType = type;
            i.style = SettingsItemStyle.Normal;
            i.noResetButton = true;
            i.sideNote = note;
            i.valueType = SettingsMenu.Models.ValueType.Bool;
            i.hideFlags = HideFlags.HideAndDontSave;
            c.items.Add(i);
            return i;
        }

        private static void Toggle(SettingsCategory c, string label, Func<bool> get, Action<bool> set, string note = null) =>
            rows.Add(new ToggleRow { Item = Item(c, label, SettingsItemType.Toggle, note), Get = get, Set = set });

        private static void Toggle(SettingsCategory c, string label, BepInEx.Configuration.ConfigEntry<bool> e, string note = null) =>
            Toggle(c, label, () => e.Value, v =>
            {
                e.Value = v;
                Plugin.Log.LogInfo($"options: {e.Definition.Key} {(v ? "on" : "off")}");
            }, note);

        // A slider: shown as its value times scale (a percentage: 100), whole numbers, with suffix.
        private static void Slider(SettingsCategory c, string label, float min, float max, Func<float> get, Action<float> set,
            float scale = 100f, string suffix = "%", string note = null)
        {
            SettingsItem i = Item(c, label, SettingsItemType.Slider, note);
            i.sliderConfig = new SliderConfig
            {
                minValue = min * scale,
                maxValue = max * scale,
                wholeNumbers = true,
                textConfig = new SliderValueToTextConfig
                {
                    decimalType = DecimalType.NoDecimals,
                    multiplier = 1f,
                    suffix = suffix,
                    ifMax = "",
                    ifMin = "",
                },
            };
            rows.Add(new SliderRow { Item = i, Get = get, Set = set, Scale = scale });
        }

        private static void Slider(SettingsCategory c, string label, float min, float max, BepInEx.Configuration.ConfigEntry<float> e, string note = null, Action changed = null) =>
            Slider(c, label, min, max, () => e.Value, v =>
            {
                e.Value = v;
                changed?.Invoke();
            }, note: note);

        private static void Slider(SettingsCategory c, string label, int min, int max, BepInEx.Configuration.ConfigEntry<int> e, string suffix = "", string note = null) =>
            Slider(c, label, min, max, () => e.Value, v => e.Value = Mathf.RoundToInt(v), 1f, suffix, note);

        private static void Dropdown(SettingsCategory c, string label, string[] options, Func<int> get, Action<int> set, string note = null)
        {
            SettingsItem i = Item(c, label, SettingsItemType.Dropdown, note);
            i.dropdownType = SettingsDropdownType.List;
            i.dropdownList = options;
            rows.Add(new DropdownRow { Item = i, Get = get, Set = set });
        }

        private static void Button(SettingsCategory c, string label, string button, Action click)
        {
            SettingsItem i = Item(c, label, SettingsItemType.Button);
            i.buttonLabel = button;
            rows.Add(new ButtonRow { Item = i, Click = click });
        }

        private static readonly string[] Keys = { "V", "B", "T", "G", "X", "Z", "CapsLock", "LeftAlt", "Mouse4", "Mouse5" };

        private static SettingsPage Model()
        {
            if (model != null)
            {
                return model;
            }
            rows.Clear();
            var cats = new List<SettingsCategory>();

            SettingsCategory mp = Category("Multiplayer", "Your world's settings count for everyone in it.");
            cats.Add(mp);
            Toggle(mp, "PvP (the party host's)", () => PvpSetting.On, PvpSetting.Set);
            Slider(mp, "PvP damage (the party host's)", 0.1f, 3f, () => PvpSetting.Damage, PvpSetting.SetDamage);
            Toggle(mp, "Players bump into each other (the party host's)", () => PlayerCollision.On, PlayerCollision.Set);
            Toggle(mp, "Put back on solid ground after falling out of the map", Plugin.OutOfMapRescue);
            Toggle(mp, "Go where the party host goes", Plugin.FollowHost);
            Toggle(mp, "Rejoin the party by itself after a drop or a crash", Plugin.AutoRejoin);
            Slider(mp, "Boss health per extra player", 0f, 2f, Plugin.BossHealthPerPlayer);
            Dropdown(mp, "Server list", new[] { "INVITE ONLY", "STEAM FRIENDS", "EVERYONE" },
                () => Plugin.ListWorld.Value, v => Plugin.ListWorld.Value = v);

            SettingsCategory dying = Category("Dying in a party", "While someone else in the party is still alive in the level.");
            cats.Add(dying);
            Toggle(dying, "Spectate them until a checkpoint", Plugin.SpectateOnDeath);
            Toggle(dying, "Teammates can revive you", Plugin.Revive);
            Slider(dying, "Time to revive (seconds)", 1f, 10f, () => Plugin.ReviveSeconds.Value, v => Plugin.ReviveSeconds.Value = v, 1f, " S");

            SettingsCategory rp = Category("Replays", $"Each run kept from level start; saved at the end, or from the {Brand.Name} panel's REPLAY tab.");
            cats.Add(rp);
            Toggle(rp, "Record replays", Plugin.ReplaysOn);

            SettingsCategory voice = Category("Voice chat", "Through Steam: its microphone and levels (Steam's voice settings).");
            cats.Add(voice);
            Dropdown(voice, "Voice chat", new[] { "OFF", "PUSH TO TALK", "OPEN MIC" }, () => Plugin.VoiceMode.Value, v =>
            {
                Plugin.VoiceMode.Value = v;
                Plugin.Log.LogInfo($"options: voice chat {v}");
            });
            Dropdown(voice, "Push to talk", Keys, () => Mathf.Max(0, Array.FindIndex(Keys, k => string.Equals(k, Plugin.VoiceKey.Value, StringComparison.OrdinalIgnoreCase))),
                v => Plugin.VoiceKey.Value = Keys[Mathf.Clamp(v, 0, Keys.Length - 1)]);
            Slider(voice, "Voice volume", 0f, 3f, Plugin.VoiceVolume);
            Toggle(voice, "Voices from their V1", Plugin.VoiceProximity);
            Toggle(voice, "Echo in big rooms, muffled underwater", Plugin.VoiceEffects);

            SettingsCategory sh = Category("Shaders", "K turns them on and off in a level.");
            cats.Add(sh);
            Toggle(sh, "Shaders", () => ShaderPack.On, v => ShaderPack.On = v);
            Dropdown(sh, "Quality", Upper(ShaderPack.TierNames), () => ShaderPack.TierIndex, v => ShaderPack.TierIndex = v);
            for (int k = 0; k < ShaderPack.SettingCount; k++)
            {
                int n = k;
                Slider(sh, ShaderPack.SettingName(n), ShaderPack.SettingMin(n), ShaderPack.SettingMax(n),
                    () => ShaderPack.GetSetting(n), v => ShaderPack.SetSetting(n, v));
            }
            Button(sh, "The preset's own look", "RESET", () =>
            {
                ShaderPack.ResetLook();
                RefreshShown();
            });

            SettingsCategory gp = Category("Gameplay");
            cats.Add(gp);
            Toggle(gp, "Enemies hit harder on harder difficulties", Plugin.DamageByDifficulty);
            Toggle(gp, "Bosses' hits go through armour", Plugin.BossesPierceArmour);
            Toggle(gp, "Mini-bosses in the Nether", Plugin.NetherMiniBosses);
            Slider(gp, "Your damage to enemies", 0.05f, 2f, Plugin.DamageToUltrakill);
            Slider(gp, "Enemies' damage to you", 0.1f, 3f, Plugin.DamageToMinecraft);
            Slider(gp, "Bosses' extra damage", 0.5f, 3f, Plugin.BossDamage);
            Slider(gp, "Enemies at a time in the Nether", 0, 20, Plugin.NetherEnemies);
            Slider(gp, "Block brightness", 0.05f, 2f, Plugin.BlockBrightness, null, WorldRender.BrightnessChanged);

            SettingsCategory mc = Category("Minecraft", "These count from the next time Minecraft starts.");
            cats.Add(mc);
            Toggle(mc, "Minecraft mobs fight enemies", Plugin.MobsFightEnemies);
            Toggle(mc, "Minecraft mobs attack you in levels", Plugin.MobsAttackYouInLevels);
            Toggle(mc, "Thorns always hits back", Plugin.AlwaysThorns);
            Toggle(mc, "Dig into ULTRAKILL's levels", Plugin.DigIntoLevels);
            Toggle(mc, "Fresh Minecraft world each launch", Plugin.FreshWorld);
            Slider(mc, "TNT fuse (ticks)", 10, 80, Plugin.TntFuseTicks);
            Toggle(mc, "KILLCRAFT in the start-up check", Plugin.BootLine);
            if (Plugin.MinecraftThere)
            {
                Toggle(mc, "Ask KILLNET or KILLCRAFT at start", Plugin.AskMode);
            }

            model = ScriptableObject.CreateInstance<SettingsPage>();
            model.hideFlags = HideFlags.HideAndDontSave;
            model.categories = cats.ToArray();
            return model;
        }

        private static string[] Upper(string[] a)
        {
            var b = new string[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                b[i] = a[i].ToUpperInvariant();
            }
            return b;
        }

        private static readonly List<PageState> states = new List<PageState>();

        private static void RefreshShown()
        {
            foreach (PageState s in states)
            {
                if (s != null && s.isActiveAndEnabled)
                {
                    foreach (Action a in s.Refresh)
                    {
                        a();
                    }
                }
            }
        }

        // ---- into ULTRAKILL's options menu ----

        // Each frame (cheaply): any options menu in the scene without Killcraft's page gets it.
        public static void Frame()
        {
            if (Time.unscaledTime - lookedAt < 1f)
            {
                return;
            }
            lookedAt = Time.unscaledTime;
            if (!patched)
            {
                patched = true;
                try
                {
                    var h = new Harmony("dev.killcraft.options");
                    h.Patch(AccessTools.Method(typeof(SettingsPageBuilder), "BuildPage"),
                        postfix: new HarmonyMethod(typeof(OptionsTab), nameof(AfterBuild)));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"options: can't add Killcraft's page ({e.Message})");
                    return;
                }
            }
            foreach (SettingsMenu.Components.SettingsMenu menu in Resources.FindObjectsOfTypeAll<SettingsMenu.Components.SettingsMenu>())
            {
                if (menu == null || !menu.gameObject.scene.IsValid() || done.Contains(menu.GetInstanceID()))
                {
                    continue;
                }
                done.Add(menu.GetInstanceID());
                try
                {
                    AddTo(menu);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"options: couldn't add Killcraft's page to the options menu ({e})");
                }
            }
        }

        private static void AddTo(SettingsMenu.Components.SettingsMenu menu)
        {
            var rail = typeof(SettingsMenu.Components.SettingsMenu).GetField("navigationRail", Any)?.GetValue(menu) as Transform;
            var pages = typeof(SettingsMenu.Components.SettingsMenu).GetField("pageContainer", Any)?.GetValue(menu) as Transform;
            if (rail == null || pages == null || pages.Find(PageName) != null)
            {
                return;
            }
            Transform templatePage = pages.Find("General");
            if (templatePage == null || templatePage.GetComponent<SettingsPageBuilder>() == null)
            {
                foreach (Transform t in pages)
                {
                    if (t.GetComponent<SettingsPageBuilder>() != null && t.GetComponent<SettingsLogicBase>() == null)
                    {
                        templatePage = t;
                        break;
                    }
                }
            }
            Transform templateButton = rail.Find("Colors") ?? rail.Find("HUD") ?? rail.Find("General");
            if (templatePage == null || templateButton == null)
            {
                Plugin.Log.LogWarning("options: ULTRAKILL's options menu isn't as expected; no Killcraft page");
                return;
            }

            // The page: a copy of ULTRAKILL's GENERAL page's, made while hidden (its builder only
            // builds once it's first shown, then Killcraft's page instead).
            var holder = new GameObject("Killcraft holder");
            holder.SetActive(false);
            GameObject page = UnityEngine.Object.Instantiate(templatePage.gameObject, holder.transform);
            page.name = PageName;
            foreach (SettingsLogicBase logic in page.GetComponentsInChildren<SettingsLogicBase>(true))
            {
                UnityEngine.Object.DestroyImmediate(logic);
            }
            var builder = page.GetComponent<SettingsPageBuilder>();
            typeof(SettingsPageBuilder).GetField("page", Any)?.SetValue(builder, Model());
            var events = new List<SettingsButtonEvent>();
            foreach (Row r in rows)
            {
                if (r is ButtonRow b)
                {
                    var ev = new UnityEvent();
                    ev.AddListener(() => b.Click());
                    events.Add(new SettingsButtonEvent { buttonItem = b.Item, onClickEvent = ev });
                }
            }
            builder.buttonEvents = events;
            builder.groupInterrupts = new List<SettingsGroupInterrupt>();
            states.Add(page.AddComponent<PageState>());

            // The button: a copy of COLORS's, under it.
            GameObject button = UnityEngine.Object.Instantiate(templateButton.gameObject, rail);
            button.name = ButtonName;
            button.transform.SetSiblingIndex(templateButton.GetSiblingIndex() + 1);
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
            {
                text.text = Brand.Name;
            }
            var ui = button.GetComponent<Button>();
            ui.onClick = new Button.ButtonClickedEvent();
            var highlight = rail.GetComponent<ButtonHighlightParent>();
            Image image = button.GetComponent<Image>();
            ui.onClick.AddListener(() =>
            {
                menu.SetActivePage(page);
                if (highlight != null)
                {
                    highlight.ChangeButton(image);
                }
            });
            builder.navigationButtonSelectable = ui;
            // (Back from the page: to its button.)
            foreach (MonoBehaviour mb in page.GetComponents<MonoBehaviour>())
            {
                if (mb != null && mb.GetType().Name == "BackSelectEvent")
                {
                    var back = new UnityEvent();
                    back.AddListener(() => ui.Select());
                    mb.GetType().GetField("m_OnBack", Any)?.SetValue(mb, back);
                }
            }

            page.SetActive(false);
            page.transform.SetParent(pages, false);
            UnityEngine.Object.Destroy(holder);

            // ULTRAKILL's menu knows its pages from when it started: this one too.
            var initialized = typeof(SettingsMenu.Components.SettingsMenu).GetField("initialized", Any);
            if (initialized != null && (bool)initialized.GetValue(menu))
            {
                var f = typeof(SettingsMenu.Components.SettingsMenu).GetField("pageGameObjects", Any);
                var list = new List<GameObject>((GameObject[])f.GetValue(menu) ?? new GameObject[0]) { page };
                f.SetValue(menu, list.ToArray());
            }
            // (The buttons' highlight knows its buttons from when it started too.)
            if (highlight != null && typeof(ButtonHighlightParent).GetField("buttons", Any)?.GetValue(highlight) != null)
            {
                highlight.ChangeButton(null);
                typeof(ButtonHighlightParent).GetMethod("Start", Any)?.Invoke(highlight, null);
            }
            Plugin.Log.LogInfo($"options: KILLCRAFT page added to ULTRAKILL's options ({menu.gameObject.scene.name})");
        }

        // ULTRAKILL built Killcraft's page: its controls set from Killcraft's settings, and changing them changes those.
        private static void AfterBuild(SettingsPageBuilder __instance, SettingsPage settingsPage)
        {
            if (settingsPage == null || settingsPage != model)
            {
                return;
            }
            PageState state = __instance.GetComponent<PageState>();
            foreach (Row r in rows)
            {
                try
                {
                    if (__instance.TryGetItemBuilderInstance<SettingsBuilderBase>(r.Item, out SettingsBuilderBase b) && b != null)
                    {
                        Action refresh = r.Bind(b);
                        if (refresh != null)
                        {
                            refresh();
                            state?.Refresh.Add(refresh);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"options: {r.Item.label}: {e.Message}");
                }
            }
        }
    }
}
