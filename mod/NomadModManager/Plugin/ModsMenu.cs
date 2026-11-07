using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using EvilCore.UI.MainMenu.Panels;
using UnityEngine;
using UnityEngine.UI;

namespace NomadModManager
{
    /// <summary>
    /// The in-game Mods screen, styled like Nomad Mod Downloader: the game's menu backdrop with a dark left-to-right
    /// shade, the logo and big navigation on the left, and a navy glass panel of cards on the right.
    /// </summary>
    internal sealed class ModsMenu
    {
        // ---- Nomad Mod Downloader's colours (taken from the game's menu) ----
        private static readonly Color CText = Hex(0xFFF9DF);
        private static readonly Color CAccent = Hex(0xFAC16E);
        private static readonly Color CAccentHover = Hex(0xFFD08A);
        private static readonly Color CMuted = Hex(0xB9B4A3);
        private static readonly Color CShade = Hex(0x12141E);
        private static readonly Color CPanel = Hex(0x1E2230);
        private static readonly Color CField = Hex(0x232734);
        private static readonly Color CRow = Hex(0x262B3A);
        private static readonly Color CCard = Hex(0x2C3241);
        private static readonly Color CLine = Hex(0x3C4154);
        private static readonly Color CEdge = Hex(0x545C73);
        private static readonly Color CGood = Hex(0x34B524);
        private static readonly Color CWarn = Hex(0xF2B240);
        private static readonly Color CBad = Hex(0xF06A5A);
        private static readonly Color CInk = Hex(0x1E2230);

        private const int TabInstalled = 0, TabBrowse = 1, TabChanges = 2, TabProfiles = 3;
        private static readonly int[] TabOrder = { TabBrowse, TabInstalled, TabProfiles, TabChanges };

        private readonly ModManagerPlugin _host;
        private readonly ModCatalog _cat;

        public bool IsOpen { get; private set; }
        private int _tab = TabBrowse;
        private CanvasGroup _blocked, _hidden;
        private bool _blockedWas;
        private float _hiddenAlpha = 1f;

        // Browse
        private string _search = "";
        private int _sort;
        private string _category; // null = all
        private bool _searchedOnce;
        private readonly List<NexusMod> _results = new List<NexusMod>();
        private int _total;
        private int _fetched, _skipped;   // results read from Nexus, and how many of them aren't shown
        private bool _moreOnNexus;
        private bool _loading;
        private string _error;
        private bool _checkedUpdates;
        private bool _enterPressed;
        private bool _textHooked;

        // Scrolling (GUI.BeginScrollView is stripped from the game)
        private float _scrollY;
        private bool _dragBar;

        private ModEntry _confirm;
        private float _confirmUntil;
        private readonly List<(string Text, bool Good, float Until)> _toasts = new List<(string, bool, float)>();

        // Game art
        private Sprite _logo;
        private Rect _logoRect;
        private bool _logoFromGame;

        // Style
        private float _s = 1f;
        private float _styleScale = -1;
        private Font _font;
        private GUIStyle _body, _bold, _small, _smallBold, _nav, _h2, _wrap, _wrapSmall, _invisible;
        private Texture2D _px, _shade;

        // Opened from the main menu, or with the hotkey during play
        public bool OpenedFromMainMenu { get; private set; }
        public bool OpenedFromPause { get; private set; }
        private CursorLockMode _cursorLock;
        private UnityEngine.EventSystems.EventSystem _events;
        private bool _eventsWas;
        private UnityEngine.EventSystems.EventSystem _restoreEvents; // given back one frame after closing
        private float _openedAt;
        private bool _cursorVisible, _cursorSaved;

        // Browse: a mod's page
        private NexusDetails _details;
        private int _detailsLoading;
        private string _detailsError;
        private float _browseScroll;

        // Installed: filter, explanations, settings
        private string _filter = "";
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float Until, bool Has)> _hasSettings = new Dictionary<string, (float, bool)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float Until, List<StartupProblem> List)> _problems = new Dictionary<string, (float, List<StartupProblem>)>(StringComparer.OrdinalIgnoreCase);
        private ModEntry _settingsFor;
        private List<ModSettings> _settings;
        private float _installedScroll;
        private Setting _editing, _capturing, _dragging;
        private ModSettings _editingOwner, _capturingOwner, _draggingOwner;
        private string _editText = "";
        private double _dragValue;

        // Profiles
        private bool _naming;
        private string _profileName = "";
        private string _confirmProfile;

        // Controller navigation
        private bool _padMode;
        private Rect _focus;
        private bool _hasFocus;
        private int _navDir = -1;          // 0 up, 1 down, 2 left, 3 right
        private bool _navActivate;
        private float _navRepeat, _holdOpen;
        private readonly List<(Rect R, Rect View, bool InScroll)> _navNow = new List<(Rect, Rect, bool)>();
        private List<(Rect R, Rect View, bool InScroll)> _navLast = new List<(Rect, Rect, bool)>();
        private Rect _curView;
        private bool _inScroll;

        public ModsMenu(ModManagerPlugin host, ModCatalog catalog) { _host = host; _cat = catalog; }

        // ==================================================================
        // Open / close

        /// <summary>From the pause (Esc) menu: shown over the game, with the pause menu hidden until this closes.</summary>
        public void OpenFromPause(Component pause)
        {
            Open(pause);
            OpenedFromMainMenu = false;
            OpenedFromPause = true;
        }

        public void Open(Component panel)
        {
            if (IsOpen) return;
            IsOpen = true;
            OpenedFromMainMenu = panel is MainMenuPanel;
            OpenedFromPause = false;
            _scrollY = 0;
            HookText();
            _cat.Refresh();
            StartupLog.Refresh();
            _problems.Clear();
            FindGameArt(panel);
            try { _cursorLock = Cursor.lockState; _cursorVisible = Cursor.visible; _cursorSaved = true; } catch { }
            // The game's own menus would get our clicks and controller presses too (they sit right behind this):
            // pause their input while the Mods menu is open.
            if (_restoreEvents != null) { _events = _restoreEvents; _restoreEvents = null; }  // reopened before it was given back
            else
                try { _events = UnityEngine.EventSystems.EventSystem.current; if (_events != null) { _eventsWas = _events.enabled; _events.enabled = false; } } catch { _events = null; }
            _openedAt = Time.unscaledTime;

            var canvas = panel != null ? panel.GetComponentInParent<Canvas>() : null;
            if (canvas != null)
            {
                // No clicks reach the game's menu, and its buttons are hidden while this is open.
                var root = canvas.rootCanvas.gameObject;
                _blocked = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
                _blockedWas = _blocked.blocksRaycasts;
                _blocked.blocksRaycasts = false;
                _hidden = panel.GetComponent<CanvasGroup>() ?? panel.gameObject.AddComponent<CanvasGroup>();
                _hiddenAlpha = _hidden.alpha;
                _hidden.alpha = 0f;
            }
            if (!_checkedUpdates) CheckUpdates();
            if (!_searchedOnce) RunSearch(false); // also used to find the Nexus page (and author) of mods added by hand
        }

        public void Close()
        {
            IsOpen = false;
            _confirm = null;
            _editing = _capturing = _dragging = null;
            _naming = false;
            if (_cursorSaved && !OpenedFromMainMenu) { try { Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible; } catch { } }
            OpenedFromPause = false;
            _cursorSaved = false;
            // Next frame: the button press that closed this mustn't also reach the game's menu.
            if (_events != null) { _restoreEvents = _eventsWas ? _events : null; _events = null; }
            if (_blocked != null) { _blocked.blocksRaycasts = _blockedWas; _blocked = null; }
            if (_hidden != null) { _hidden.alpha = _hiddenAlpha; _hidden = null; }
        }

        public void Toast(string text, bool good)
        {
            _toasts.Add((text, good, Time.unscaledTime + 5f));
            if (_toasts.Count > 4) _toasts.RemoveAt(0);
        }

        // ==================================================================
        // Input (called every frame from the plugin's Update)

        /// <summary>Hotkey, controller and key-capture handling. Returns true if the menu wants Esc for itself.</summary>
        public void Tick(UnityEngine.InputSystem.Key openKey, bool padOpen)
        {
            UnityEngine.InputSystem.Keyboard kb = null;
            UnityEngine.InputSystem.Gamepad pad = null;
            try { kb = UnityEngine.InputSystem.Keyboard.current; pad = UnityEngine.InputSystem.Gamepad.current; } catch { }

            // Open / close from anywhere: the hotkey, or holding the controller's View (Back) button for a second.
            var toggle = false;
            try { if (kb != null && _capturing == null && _editing == null && !_naming && openKey != UnityEngine.InputSystem.Key.None && kb[openKey].wasPressedThisFrame) toggle = true; } catch { }
            if (pad != null && padOpen)
            {
                if (pad.selectButton.isPressed) { _holdOpen += Time.unscaledDeltaTime; if (_holdOpen >= 1f) { toggle = true; _holdOpen = -10f; } }
                else _holdOpen = 0;
            }
            if (_restoreEvents != null && !IsOpen) { try { _restoreEvents.enabled = true; } catch { } _restoreEvents = null; }
            if (toggle)
            {
                if (IsOpen) Close();
                else { Open(_host.AtMainMenu ? _host.MainMenu : null); if (pad != null && pad.selectButton.isPressed) _padMode = true; }
                return;
            }
            if (!IsOpen) return;

            // While open during play, the game's own cursor lock is kept off.
            if (!OpenedFromMainMenu) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } }

            if (_capturing != null && kb != null) { CaptureKey(kb); return; }
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                if (_editing != null) { _editing = null; return; }
                if (_naming) { _naming = false; return; }
                Back();
                return;
            }

            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 9f || mouse.leftButton.wasPressedThisFrame)) _padMode = false;
            }
            catch { }
            if (pad == null) return;

            var stick = pad.leftStick.ReadValue();
            int dir = -1;
            if (pad.dpad.up.wasPressedThisFrame) dir = 0;
            else if (pad.dpad.down.wasPressedThisFrame) dir = 1;
            else if (pad.dpad.left.wasPressedThisFrame) dir = 2;
            else if (pad.dpad.right.wasPressedThisFrame) dir = 3;
            else
            {
                // Held stick or d-pad: repeat.
                int held = pad.dpad.up.isPressed || stick.y > 0.6f ? 0 : pad.dpad.down.isPressed || stick.y < -0.6f ? 1
                         : pad.dpad.left.isPressed || stick.x < -0.6f ? 2 : pad.dpad.right.isPressed || stick.x > 0.6f ? 3 : -1;
                if (held < 0) _navRepeat = 0;
                else if (_navRepeat == 0) { dir = held; _navRepeat = Time.unscaledTime + 0.35f; }
                else if (Time.unscaledTime >= _navRepeat) { dir = held; _navRepeat = Time.unscaledTime + 0.12f; }
            }
            if (dir >= 0) { _padMode = true; _navDir = dir; }
            // The press that opened the menu (A on the game's Mods button) isn't a press in here.
            var settled = Time.unscaledTime - _openedAt > 0.25f;
            if (pad.buttonSouth.wasPressedThisFrame && settled) { _padMode = true; _navActivate = true; }
            if (pad.buttonEast.wasPressedThisFrame && settled) { _padMode = true; Back(); }
            if (!settled && pad.buttonSouth.isPressed) _padMode = true;
            if (pad.leftShoulder.wasPressedThisFrame) { _padMode = true; NextTab(-1); }
            if (pad.rightShoulder.wasPressedThisFrame) { _padMode = true; NextTab(1); }
            var rs = pad.rightStick.ReadValue().y;
            if (Mathf.Abs(rs) > 0.2f) { _scrollY -= rs * 1400f * _s * Time.unscaledDeltaTime; _padMode = true; }
        }

        /// <summary>One step back: out of a page or settings, otherwise close the menu.</summary>
        private void Back()
        {
            if (_tab == TabBrowse && (_details != null || _detailsLoading != 0)) { CloseDetails(); return; }
            if (_tab == TabInstalled && _settingsFor != null) { CloseSettings(); return; }
            Close();
        }

        private void NextTab(int d)
        {
            var i = Array.IndexOf(TabOrder, _tab);
            ShowTab(TabOrder[(i + d + TabOrder.Length) % TabOrder.Length]);
        }

        private void ShowTab(int tab)
        {
            if (_tab == tab) return;
            _tab = tab; _scrollY = 0; _hasFocus = false;
            _editing = null; _naming = false;
            if (tab == TabBrowse && !_searchedOnce) RunSearch(false);
            if (tab == TabInstalled) { _cat.Refresh(); StartupLog.Refresh(); _problems.Clear(); }
        }

        /// <summary>The game's IMGUI has no text fields (stripped), so typing comes from the Input System.</summary>
        private void HookText()
        {
            if (_textHooked) return;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                kb.onTextInput += OnText;
                _textHooked = true;
            }
            catch { }
        }

        private void OnText(char ch)
        {
            if (!IsOpen || _capturing != null) return;
            if (_editing != null) { Type(ref _editText, ch, 200, () => CommitEdit()); return; }
            if (_tab == TabProfiles && _naming) { Type(ref _profileName, ch, 40, SaveProfile); return; }
            if (_tab == TabInstalled && _settingsFor == null) { Type(ref _filter, ch, 40, null); _installedScroll = 0; _scrollY = 0; return; }
            if (_tab == TabBrowse && _details == null && _detailsLoading == 0) Type(ref _search, ch, 60, () => _enterPressed = true);
        }

        private static void Type(ref string text, char ch, int max, Action enter)
        {
            if (ch == '\b') { if (text.Length > 0) text = text.Substring(0, text.Length - 1); }
            else if (ch == '\r' || ch == '\n') enter?.Invoke();
            else if (!char.IsControl(ch) && text.Length < max) text += ch;
        }

        private void CheckUpdates()
        {
            _checkedUpdates = true;
            var ids = _cat.NexusIds.ToList();
            if (ids.Count == 0) return;
            _host.StartCoroutine(Nexus.Lookup(ids, mods =>
            {
                foreach (var m in mods) _cat.SetNexusInfo(m.Id, m.Version, m.Author, m.Name, m.Summary);
                _cat.Refresh();
                if (_cat.UpdateCount > 0) Toast($"{_cat.UpdateCount} mod update(s) on Nexus.", true);
            }));
        }

        private void RunSearch(bool more)
        {
            if (_loading) return;
            _loading = true;
            _searchedOnce = true;
            _error = null;
            if (!more) { _results.Clear(); _scrollY = 0; }
            if (!more) { _fetched = 0; _skipped = 0; }
            _host.StartCoroutine(Nexus.Search(_search, _sort, _fetched, 100, (list, total, err) =>
            {
                _loading = false;
                if (err != null) { _error = err; return; }
                _fetched += list.Count;
                _skipped += list.Count(m => m.Hidden);
                _results.AddRange(list.Where(m => !m.Hidden && _results.All(r => r.Id != m.Id)));
                _total = Math.Max(0, total - _skipped);
                _moreOnNexus = _fetched < total;
                foreach (var m in list) _cat.SetNexusInfo(m.Id, m.Version, m.Author, m.Name, m.Summary);
                _cat.LearnNames(list);
                _cat.Refresh();
            }));
        }

        /// <summary>The game's logo sprite and where it sits on screen, so the left column lines up with the real menu.</summary>
        private void FindGameArt(Component panel)
        {
            if (panel == null) return;
            try
            {
                var canvas = panel.GetComponentInParent<Canvas>()?.rootCanvas;
                if (canvas == null) return;
                Image best = null;
                foreach (var img in canvas.GetComponentsInChildren<Image>(true))
                {
                    if (img.sprite == null) continue;
                    var n = (img.name + " " + img.sprite.name).ToLowerInvariant();
                    if (n.Contains("logo")) { best = img; break; }
                }
                if (best == null) return;
                _logo = best.sprite;
                var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                var c = new Vector3[4];
                best.rectTransform.GetWorldCorners(c);
                var a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
                var b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                _logoRect = Rect.MinMaxRect(a.x, Screen.height - b.y, b.x, Screen.height - a.y);
                _logoFromGame = _logoRect.width > 10 && _logoRect.height > 10;
            }
            catch (Exception e) { ModManagerPlugin.Log.LogInfo("No logo found: " + e.Message); }
        }

        // ==================================================================
        // Draw

        public void Draw()
        {
            _s = Mathf.Max(0.6f, Screen.height / 1080f * _host.Scale.Value);
            if (IsOpen)
            {
                Styles();
                DrawScreen();
                if (Event.current.type == EventType.Repaint) FinishNav();
            }
            DrawToasts();
        }

        private void DrawScreen()
        {
            // The game's menu backdrop stays; the shade goes over it, like the downloader's window.
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), ShadeTex());
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _invisible);

            var leftX = _logoFromGame ? Mathf.Max(24 * _s, _logoRect.center.x - 180 * _s) : 60 * _s;
            var left = new Rect(leftX, 0, 360 * _s, Screen.height);
            DrawLeft(left);

            var cx = left.xMax + 40 * _s;
            var content = new Rect(cx, 44 * _s, Screen.width - cx - 44 * _s, Screen.height - 88 * _s);
            Glass(content, 16);
            var inner = new Rect(content.x + 28 * _s, content.y + 24 * _s, content.width - 56 * _s, content.height - 48 * _s);
            switch (_tab)
            {
                case TabInstalled:
                    if (_settingsFor != null) DrawSettings(inner); else DrawInstalled(inner);
                    break;
                case TabBrowse:
                    if (_details != null || _detailsLoading != 0) DrawDetails(inner); else DrawBrowse(inner);
                    break;
                case TabProfiles: DrawProfiles(inner); break;
                default: DrawChanges(inner); break;
            }
        }

        private void DrawLeft(Rect r)
        {
            // Logo + subtitle
            Rect logo;
            if (_logoFromGame) logo = _logoRect;
            else logo = new Rect(r.x + 40 * _s, 50 * _s, r.width - 80 * _s, 200 * _s);
            if (_logo != null) DrawSprite(logo, _logo);
            else GUI.Label(logo, "NOMAD DRIVE", Center(Size(_nav, 46), CAccent));
            var y = logo.yMax + 6 * _s;
            GUI.Label(new Rect(r.x, y, r.width, 26 * _s), "MOD MANAGER", Center(_smallBold, CMuted));
            y += 64 * _s;

            // Navigation: big cream labels, amber when chosen (as in the downloader)
            var restart = _cat.RestartItems;
            var items = new[]
            {
                (TabBrowse, "Browse mods"),
                (TabInstalled, "Installed (" + _cat.Mods.Count(m => !m.IsSelf) + ")"),
                (TabProfiles, "Profiles"),
                (TabChanges, restart > 0 ? $"Changes ({restart})" : "Changes")
            };
            foreach (var (tab, label) in items)
            {
                var size = _nav.CalcSize(new GUIContent(label));
                var ir = new Rect(r.center.x - size.x / 2, y, size.x, 50 * _s);
                var hover = ir.Contains(Event.current.mousePosition);
                GUI.Label(ir, label, Tint(_nav, _tab == tab || hover ? CAccent : CText));
                if (Hit(ir)) ShowTab(tab);
                y += 58 * _s;
            }
            if (_padMode)
                GUI.Label(new Rect(r.x, y - 6 * _s, r.width, 22 * _s), "LB / RB switch pages   ·   B back", Center(_small, CMuted));
            var back = OpenedFromMainMenu || OpenedFromPause ? "Back" : "Close";
            var bs = _nav.CalcSize(new GUIContent(back));
            var br = new Rect(r.center.x - bs.x / 2, y + 10 * _s, bs.x, 50 * _s);
            GUI.Label(br, back, Tint(_nav, br.Contains(Event.current.mousePosition) ? CAccent : CMuted));
            if (Hit(br)) { Close(); return; }

            // Status card at the bottom, like the downloader's
            var cardH = (restart > 0 ? 210 : 150) * _s;
            var card = new Rect(r.x + 10 * _s, Screen.height - 44 * _s - cardH, r.width - 20 * _s, cardH);
            Glass(card, 16);
            var ky = card.y + 18 * _s;
            Row(card, ref ky, "Game", "Nomad Drive");
            Row(card, ref ky, "Downloader", DownloaderStatus(out var dlColor), dlColor);
            Row(card, ref ky, "Changes", restart > 0 ? $"{restart} waiting for a restart" : "None waiting", restart > 0 ? CWarn : CGood);
            var bw = (card.width - 44 * _s) / 2;
            if (restart > 0)
            {
                if (Btn(new Rect(card.x + 16 * _s, card.yMax - 106 * _s, card.width - 32 * _s, 42 * _s), "Restart now", true)) Restarter.Restart();
            }
            if (Btn(new Rect(card.x + 16 * _s, card.yMax - 56 * _s, bw, 40 * _s), "Mods folder", false)) OpenFolder(Paths.PluginPath);
            if (Btn(new Rect(card.x + 28 * _s + bw, card.yMax - 56 * _s, bw, 40 * _s), "Downloader", false))
            {
                if (!Downloader.Show()) GetDownloader();
            }
        }

        private void Row(Rect card, ref float y, string key, string value, Color? color = null)
        {
            GUI.Label(new Rect(card.x + 18 * _s, y, 110 * _s, 24 * _s), key, Tint(_small, CMuted));
            GUI.Label(new Rect(card.x + 128 * _s, y, card.width - 146 * _s, 24 * _s), value, Tint(_small, color ?? CText));
            y += 26 * _s;
        }

        private static string DownloaderStatus(out Color color)
        {
            var exe = Downloader.ExePath;
            if (exe == null) { color = CWarn; return "Not found"; }
            var v = Downloader.VersionOf(exe);
            if (v != null && v < Downloader.MinVersion) { color = CWarn; return $"v{v} — update to {Downloader.MinVersion}"; }
            color = CGood;
            return v != null ? "v" + v : "Ready";
        }

        // ------------------------------------------------------------------
        // Browse mods

        private void DrawBrowse(Rect r)
        {
            GUI.Label(new Rect(r.x, r.y, r.width, 36 * _s), "Browse mods", _h2);
            GUI.Label(new Rect(r.x, r.y + 34 * _s, r.width, 22 * _s), _total > 0 ? $"{_total} Nomad Drive mods on Nexus" : "Nomad Drive mods on Nexus", Tint(_small, CMuted));

            var y = r.y + 70 * _s;
            var sortW = 118 * _s;
            var fieldR = new Rect(r.x, y, r.width - 4 * (sortW + 8 * _s) - 118 * _s, 42 * _s);
            Fill(fieldR, CField, 11 * _s);
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            GUI.Label(new Rect(fieldR.x + 16 * _s, fieldR.y, fieldR.width - 56 * _s, fieldR.height),
                _search.Length > 0 ? _search + caret : "Search mods — just start typing", Tint(_body, _search.Length > 0 ? CText : CMuted));
            if (_search.Length > 0)
            {
                var xr = new Rect(fieldR.xMax - 38 * _s, fieldR.y + 7 * _s, 28 * _s, 28 * _s);
                GUI.Label(xr, "×", Center(_bold, xr.Contains(Event.current.mousePosition) ? CAccent : CMuted));
                if (Hit(xr)) { _search = ""; RunSearch(false); }
            }
            if (Btn(new Rect(fieldR.xMax + 8 * _s, y, 102 * _s, 42 * _s), "Search", true) || _enterPressed) { _enterPressed = false; RunSearch(false); }
            var sx = fieldR.xMax + 118 * _s;
            for (var i = 0; i < Nexus.SortNames.Length; i++)
            {
                if (Btn(new Rect(sx, y, sortW, 42 * _s), Nexus.SortNames[i], false, _sort == i) && _sort != i) { _sort = i; RunSearch(false); }
                sx += sortW + 8 * _s;
            }
            y += 54 * _s;

            // Category filter
            var present = new HashSet<string>(_results.Select(m => m.Category));
            var chips = new List<string> { null };
            chips.AddRange(Categories.Order.Where(present.Contains));
            var cx = r.x;
            foreach (var c in chips)
            {
                var label = c == null ? "All (" + _results.Count + ")" : c + " (" + _results.Count(m => m.Category == c) + ")";
                var w = _smallBold.CalcSize(new GUIContent(label)).x + 30 * _s;
                if (cx + w > r.xMax) break;
                var cr = new Rect(cx, y, w, 32 * _s);
                var on = _category == c;
                var hover = cr.Contains(Event.current.mousePosition);
                Fill(cr, on ? CAccent : hover ? CEdge : CLine, 16 * _s);
                GUI.Label(cr, label, Center(_smallBold, on ? CInk : CText));
                if (Hit(cr) && !on) { _category = c; _scrollY = 0; }
                cx += w + 8 * _s;
            }
            if (_category != null && !present.Contains(_category)) _category = null;
            y += 46 * _s;

            if (!Downloader.Available || Downloader.IsOutdated(out _) || AppFetcher.Busy)
            {
                var banner = new Rect(r.x, y, r.width, 70 * _s);
                Fill(banner, Alpha(CAccent, 0.14f), 12 * _s);
                var outdated = Downloader.Available;
                var text = AppFetcher.Busy ? AppFetcher.Status
                    : outdated ? "Your Nomad Mod Downloader is out of date. Update it to " + Downloader.MinVersion + " or newer so Install here opens the mod in it, ready to download."
                    : "Install uses Nomad Mod Downloader, a free app: Nexus needs you to click its download button, so the app opens the mod for you and installs it. Get it from the Microsoft Store, or from GitHub with one click.";
                GUI.Label(new Rect(banner.x + 18 * _s, banner.y + 10 * _s, banner.width - 470 * _s, banner.height - 20 * _s), text, _wrapSmall);
                var br = new Rect(banner.xMax - 240 * _s, banner.y + 14 * _s, 222 * _s, 42 * _s);
                if (AppFetcher.Busy) ProgressBar(br, AppFetcher.Progress);
                else
                {
                    if (Btn(br, outdated ? "Update from GitHub" : "Get from GitHub", false)) GetDownloader();
                    if (Btn(new Rect(br.x - 200 * _s, br.y, 190 * _s, br.height), "Microsoft Store", true)) OpenStore();
                }
                y += 84 * _s;
            }

            var view = new Rect(r.x, y, r.width, r.yMax - y);
            if (_error != null)
            {
                GUI.Label(new Rect(view.x, view.y, view.width, 60 * _s), _error, Tint(_wrap, CBad));
                if (Btn(new Rect(view.x, view.y + 64 * _s, 140 * _s, 40 * _s), "Try again", true)) RunSearch(false);
                return;
            }
            if (_results.Count == 0) { GUI.Label(new Rect(view.x, view.y, view.width, 40 * _s), _loading ? "Loading mods…" : "No mods found.", Tint(_body, CMuted)); return; }
            var shown = _category == null ? _results : _results.Where(m => m.Category == _category).ToList();

            const float cardH = 178, gap = 14;
            var cols = view.width > 1300 * _s ? 3 : 2;
            var colW = (view.width - 18 * _s - (cols - 1) * gap * _s) / cols;
            var rows = (shown.Count + cols - 1) / cols;
            var more = _moreOnNexus;
            var inner = BeginScroll(view, rows * (cardH + gap) * _s + (more ? 64 * _s : 0));
            for (var i = 0; i < shown.Count; i++)
            {
                var cr = new Rect((i % cols) * (colW + gap * _s), inner.y + (i / cols) * (cardH + gap) * _s, colW, cardH * _s);
                if (cr.yMax < -50 || cr.y > view.height + 50) continue;
                NexusCard(cr, shown[i]);
            }
            if (more && Btn(new Rect((inner.width - 220 * _s) / 2, inner.y + rows * (cardH + gap) * _s + 10 * _s, 220 * _s, 42 * _s), _loading ? "Loading…" : "Load more", false))
                RunSearch(true);
            EndScroll();
        }

        private void NexusCard(Rect r, NexusMod m)
        {
            var hover = r.Contains(Event.current.mousePosition);
            Fill(r, hover ? Lighten(CCard, 0.03f) : CCard, 14 * _s);

            // 16:9 picture on the left, like Nexus's own tiles
            var th = r.height - 24 * _s;
            var thumb = new Rect(r.x + 12 * _s, r.y + 12 * _s, Mathf.Min(th * 16f / 9f, r.width * 0.42f), th);
            Fill(thumb, CRow, 10 * _s);
            var tex = Nexus.Thumb(_host, m.Thumb);
            if (tex != null) GUI.DrawTexture(thumb, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 10 * _s);
            else GUI.Label(thumb, string.IsNullOrEmpty(m.Thumb) ? "No picture" : "…", Center(_small, CMuted));

            var x = thumb.xMax + 16 * _s;
            var w = r.xMax - x - 14 * _s;
            GUI.Label(new Rect(x, r.y + 10 * _s, w, 28 * _s), m.Name, _bold);
            GUI.Label(new Rect(x, r.y + 38 * _s, w, 22 * _s), $"{m.Category}  ·  by {m.Author}  ·  v{m.Version.TrimStart('v')}  ·  {Short(m.Downloads)} downloads", Tint(_small, CMuted));
            GUI.Label(new Rect(x, r.y + 64 * _s, w, 54 * _s), Clip(m.Summary, 140), _wrapSmall);

            var by = r.yMax - 52 * _s;
            var entry = _cat.Mods.FirstOrDefault(e => e.NexusId == m.Id);
            if (entry != null && entry.UpdateAvailable && !entry.UpdateStaged)
            { if (Btn(new Rect(x, by, 112 * _s, 40 * _s), "Update", true)) InstallFromNexus(m.Id); }
            else if (_cat.IsInstalled(m.Id)) StatusText(new Rect(x, by, 112 * _s, 40 * _s), "Installed", CGood);
            else if (m.Id == Nexus.DownloaderModId) StatusText(new Rect(x, by, 112 * _s, 40 * _s), "Installed", CGood); // the app and this menu
            else if (Btn(new Rect(x, by, 112 * _s, 40 * _s), "Install", true)) InstallFromNexus(m.Id);
            if (Btn(new Rect(x + 120 * _s, by, 108 * _s, 40 * _s), "Details", false)) OpenDetails(m);
            if (Hit(r)) OpenDetails(m); // the whole card opens the mod's page too
        }

        // ------------------------------------------------------------------
        // A mod's page

        private void OpenDetails(NexusMod m)
        {
            _browseScroll = _scrollY;
            _scrollY = 0;
            _details = null;
            _detailsError = null;
            _detailsLoading = m.Id;
            _hasFocus = false;
            var id = m.Id;
            _host.StartCoroutine(Nexus.Details(id, (d, err) =>
            {
                if (_detailsLoading != id) return;
                _detailsLoading = 0;
                _details = d;
                _detailsError = err;
                if (d == null) { Toast(err ?? "Couldn't load the mod's page.", false); CloseDetails(); }
                else
                {
                    _cat.SetNexusInfo(d.Mod.Id, d.Mod.Version, d.Mod.Author, d.Mod.Name, d.Mod.Summary);
                    var mods = d.Requirements.Where(q => q.ModId > 0).Select(q => q.ModId).ToList();
                    if (mods.Count > 0) _host.StartCoroutine(Nexus.Lookup(mods, list => { foreach (var x in list) _cat.SetNexusInfo(x.Id, x.Version, x.Author, x.Name, x.Summary); }));
                }
            }));
        }

        private void CloseDetails()
        {
            _details = null;
            _detailsLoading = 0;
            _scrollY = _browseScroll;
            _hasFocus = false;
        }

        private void DrawDetails(Rect r)
        {
            if (Btn(new Rect(r.x, r.y, 190 * _s, 40 * _s), "‹  Back to mods", false)) { CloseDetails(); return; }
            if (_details == null)
            {
                GUI.Label(new Rect(r.x, r.y + 60 * _s, r.width, 40 * _s), "Loading the mod's page…", Tint(_body, CMuted));
                return;
            }
            var d = _details;
            var m = d.Mod;
            var top = r.y + 56 * _s;

            // Left: picture and requirements
            var leftW = Mathf.Min(r.width * 0.4f, 640 * _s);
            var pic = new Rect(r.x, top, leftW, leftW * 9f / 16f);
            Fill(pic, CRow, 12 * _s);
            var tex = Nexus.Thumb(_host, d.Picture.Length > 0 ? d.Picture : m.Thumb);
            if (tex != null) GUI.DrawTexture(pic, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 12 * _s);
            else GUI.Label(pic, string.IsNullOrEmpty(d.Picture) ? "No picture" : "Loading picture…", Center(_small, CMuted));

            var ly = pic.yMax + 20 * _s;
            GUI.Label(new Rect(r.x, ly, leftW, 28 * _s), "Requirements", Tint(_bold, CAccent));
            ly += 34 * _s;
            if (d.Requirements.Count == 0)
            {
                GUI.Label(new Rect(r.x, ly, leftW, 24 * _s), "Just BepInEx (Nomad Mod Downloader sets it up).", Tint(_small, CMuted));
                ly += 30 * _s;
            }
            var missing = new List<int>();
            foreach (var q in d.Requirements.Take(8))
            {
                var have = q.ModId > 0 && (_cat.IsInstalled(q.ModId) || q.ModId == Nexus.DownloaderModId);
                if (q.ModId > 0 && !have) missing.Add(q.ModId);
                var row = new Rect(r.x, ly, leftW, 44 * _s);
                Fill(row, CRow, 10 * _s);
                StatusText(new Rect(row.x + 14 * _s, row.y, 30 * _s, row.height), "", have ? CGood : q.ModId > 0 ? CWarn : CMuted);
                GUI.Label(new Rect(row.x + 34 * _s, row.y, row.width - 180 * _s, row.height), q.Name + (q.External ? "  (outside Nexus)" : ""), _body);
                if (have) GUI.Label(new Rect(row.xMax - 130 * _s, row.y, 120 * _s, row.height), "Installed", Tint(_smallBold, CGood));
                else if (q.ModId > 0 && Btn(new Rect(row.xMax - 118 * _s, row.y + 4 * _s, 110 * _s, 36 * _s), "Install", true)) InstallFromNexus(q.ModId);
                else if (q.ModId == 0 && q.Url.Length > 0 && Btn(new Rect(row.xMax - 118 * _s, row.y + 4 * _s, 110 * _s, 36 * _s), "Open", false)) Application.OpenURL(q.Url);
                ly += 50 * _s;
            }

            // Right: title, buttons, then a scrolling description and the files
            var rx = pic.xMax + 28 * _s;
            var rw = r.xMax - rx;
            GUI.Label(new Rect(rx, top - 4 * _s, rw, 36 * _s), m.Name, _h2);
            GUI.Label(new Rect(rx, top + 34 * _s, rw, 22 * _s),
                $"{m.Category}  ·  by {m.Author}  ·  v{m.Version.TrimStart('v')}  ·  {Short(m.Downloads)} downloads  ·  {Short(m.Endorsements)} endorsements",
                Tint(_small, CMuted));
            var by = top + 70 * _s;
            var bx = rx;
            bool B(string text, bool primary, float w)
            {
                var hit = Btn(new Rect(bx, by, w * _s, 42 * _s), text, primary);
                bx += (w + 8) * _s;
                return hit;
            }
            var entry = _cat.Mods.FirstOrDefault(e => e.NexusId == m.Id);
            if (entry != null && entry.UpdateAvailable && !entry.UpdateStaged) { if (B("Update", true, 120)) InstallFromNexus(m.Id); }
            else if (_cat.IsInstalled(m.Id) || m.Id == Nexus.DownloaderModId) StatusText(new Rect(bx, by, 130 * _s, 42 * _s), "Installed", CGood);
            else if (B(missing.Count > 0 ? "Install with requirements" : "Install", true, missing.Count > 0 ? 270 : 120))
            {
                if (missing.Count > 0 && Downloader.CanQueue) InstallMany(missing.Concat(new[] { m.Id }).ToList(), "the mod and what it needs");
                else InstallFromNexus(m.Id);
            }
            if (bx < rx + 140 * _s) bx = rx + 140 * _s;
            if (B("Page", false, 92)) Application.OpenURL(m.PageUrl);
            if (B("Endorse", false, 120))
            {
                Application.OpenURL(m.PageUrl);
                Toast("Opened the page: press Endorse there (Nexus needs you to be logged in).", true);
            }

            var view = new Rect(rx, by + 60 * _s, rw, r.yMax - by - 60 * _s);
            var wrapW = rw - 30 * _s;
            var desc = d.Description.Length > 0 ? d.Description : (m.Summary.Length > 0 ? m.Summary : "No description.");
            var descH = _wrap.CalcHeight(new GUIContent(desc), wrapW);
            var files = d.Files.Where(f => f.IsCurrent).Concat(d.Files.Where(f => !f.IsCurrent).Take(6)).ToList();
            var fileH = new List<float>();
            foreach (var f in files)
            {
                var text = FileText(f);
                fileH.Add(64 * _s + (text.Length > 0 ? _wrapSmall.CalcHeight(new GUIContent(text), wrapW - 200 * _s) + 8 * _s : 0));
            }
            var total = descH + 60 * _s + fileH.Sum() + files.Count * 10 * _s + 20 * _s;
            var inner = BeginScroll(view, total);
            var y = inner.y;
            GUI.Label(new Rect(0, y, wrapW, descH), desc, _wrap);
            y += descH + 20 * _s;
            GUI.Label(new Rect(0, y, wrapW, 28 * _s), "Files", Tint(_bold, CAccent));
            y += 40 * _s;
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var fr = new Rect(0, y, wrapW, fileH[i]);
                if (fr.yMax > -20 && fr.y < view.height + 20)
                {
                    Fill(fr, CRow, 12 * _s);
                    var label = (f.Category == "MAIN" ? "Main" : f.Category == "OPTIONAL" ? "Optional" : f.Category == "UPDATE" ? "Update" : f.Category == "MISCELLANEOUS" ? "Extra" : "Old version");
                    GUI.Label(new Rect(fr.x + 16 * _s, fr.y + 8 * _s, fr.width - 220 * _s, 26 * _s), f.Name + (f.Version.Length > 0 ? "  v" + f.Version.TrimStart('v') : ""), _bold);
                    GUI.Label(new Rect(fr.x + 16 * _s, fr.y + 34 * _s, fr.width - 220 * _s, 22 * _s),
                        $"{label}  ·  {f.Date:d MMM yyyy}" + (f.Size > 0 ? $"  ·  {f.Size / 1048576f:0.#} MB" : ""), Tint(_small, f.IsCurrent ? CMuted : CWarn));
                    var text = FileText(f);
                    if (text.Length > 0) GUI.Label(new Rect(fr.x + 16 * _s, fr.y + 60 * _s, fr.width - 220 * _s, fr.height - 64 * _s), text, _wrapSmall);
                    var b = new Rect(fr.xMax - 196 * _s, fr.y + 12 * _s, 182 * _s, 40 * _s);
                    if (Downloader.CanQueue) { if (Btn(b, "Install this one", f.IsCurrent)) InstallFile(m.Id, f.Id); }
                    else if (f.IsCurrent && Btn(b, "Install", true)) InstallFromNexus(m.Id);
                }
                y += fileH[i] + 10 * _s;
            }
            EndScroll();
        }

        private static string FileText(NexusFile f)
        {
            var parts = new List<string>();
            if (f.Description.Length > 0) parts.Add(Clip(f.Description.Replace("\n", "  "), 260));
            if (f.Changelog.Count > 0) parts.Add("Changes:  " + string.Join("   •  ", f.Changelog.Take(6).Select(c => Clip(c.Replace("\n", " "), 120)).ToArray()));
            return string.Join("\n", parts.ToArray());
        }

        /// <summary>Opens Nomad Mod Downloader in the Microsoft Store app (the website if the Store app won't open).</summary>
        private void OpenStore()
        {
            try { Application.OpenURL(AppFetcher.StoreLink); Toast("Opening Nomad Mod Downloader in the Microsoft Store…", true); }
            catch { Application.OpenURL(AppFetcher.StorePage); }
        }

        /// <summary>Downloads Nomad Mod Downloader from GitHub and opens it.</summary>
        private void GetDownloader()
        {
            if (AppFetcher.Busy) return;
            Toast("Getting Nomad Mod Downloader from GitHub…", true);
            AppFetcher.Start(_host, (ok, msg) => Toast(ok ? msg : msg + " You can also get it from the Microsoft Store, or from GitHub: " + AppFetcher.ReleasesPage, ok));
        }

        private void ProgressBar(Rect r, float p)
        {
            Fill(r, CField, 11 * _s);
            Fill(new Rect(r.x, r.y, Mathf.Max(22 * _s, r.width * Mathf.Clamp01(p)), r.height), CAccent, 11 * _s);
            var mb = AppFetcher.Size > 0 ? $"  {AppFetcher.Size * Mathf.Clamp01(p) / 1048576f:0} / {AppFetcher.Size / 1048576f:0} MB" : "";
            GUI.Label(r, $"{p * 100:0}%{mb}", Center(_smallBold, p > 0.5f ? CInk : CText));
        }

        private void InstallFromNexus(int modId)
        {
            if (Downloader.IsOutdated(out var have))
            {
                Toast($"Your Nomad Mod Downloader is v{have}. Use Update downloader above, then press Install again.", false);
                return;
            }
            if (Downloader.Install(modId))
                Toast("Opened in Nomad Mod Downloader — click Nexus's download button there and it installs.", true);
            else
            {
                Application.OpenURL($"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{modId}?tab=files");
                Toast("Opened the mod on Nexus. Press Get the downloader above to install with one click.", false);
            }
        }

        // ------------------------------------------------------------------
        // Installed

        private void DrawInstalled(Rect r)
        {
            GUI.Label(new Rect(r.x, r.y, r.width, 36 * _s), "Installed", _h2);
            GUI.Label(new Rect(r.x, r.y + 34 * _s, r.width, 22 * _s), "Mods in BepInEx\\plugins", Tint(_small, CMuted));
            var auto = _host.AutoStart.Value;
            if (Toggle(new Rect(r.xMax - 380 * _s, r.y + 8 * _s, 380 * _s, 30 * _s), auto, "Start new mods without a restart"))
            { _host.AutoStart.Value = !auto; _host.Config.Save(); }

            // Filter + Update all
            var y = r.y + 70 * _s;
            var updates = _cat.Mods.Where(m => m.UpdateAvailable && !m.UpdateStaged && m.NexusId > 0 && m.State != ModState.WillRemove).ToList();
            var fieldW = r.width - (updates.Count > 0 ? 250 * _s : 0);
            var fieldR = new Rect(r.x, y, fieldW, 42 * _s);
            Fill(fieldR, CField, 11 * _s);
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            GUI.Label(new Rect(fieldR.x + 16 * _s, fieldR.y, fieldR.width - 56 * _s, fieldR.height),
                _filter.Length > 0 ? _filter + caret : "Find an installed mod — just start typing", Tint(_body, _filter.Length > 0 ? CText : CMuted));
            if (_filter.Length > 0)
            {
                var xr = new Rect(fieldR.xMax - 38 * _s, fieldR.y + 7 * _s, 28 * _s, 28 * _s);
                GUI.Label(xr, "×", Center(_bold, xr.Contains(Event.current.mousePosition) ? CAccent : CMuted));
                if (Hit(xr)) _filter = "";
            }
            if (updates.Count > 0 && Btn(new Rect(fieldR.xMax + 10 * _s, y, 240 * _s, 42 * _s), $"Update all ({updates.Count})", true))
                InstallMany(updates.Select(m => m.NexusId).ToList(), $"{updates.Count} update(s)");
            y += 54 * _s;

            // Something that needs attention for everyone
            var banner = HealthBanner(out var bannerFix, out var bannerAction);
            if (banner != null)
            {
                var br = new Rect(r.x, y, r.width, 62 * _s);
                Fill(br, Alpha(CWarn, 0.16f), 12 * _s);
                GUI.Label(new Rect(br.x + 18 * _s, br.y + 8 * _s, br.width - 290 * _s, br.height - 16 * _s), banner, Tint(_wrapSmall, CText));
                if (bannerFix != null && Btn(new Rect(br.xMax - 262 * _s, br.y + 10 * _s, 248 * _s, 42 * _s), bannerFix, true)) bannerAction();
                y += 74 * _s;
            }

            var mods = _cat.Mods.Where(m => _filter.Length == 0 || m.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
                                               || m.Author.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            // Grouped by author; mods whose author isn't known go last.
            var groups = mods.GroupBy(m => m.Author.Length > 0 ? m.Author : ModCatalog.UnknownAuthor)
                .OrderBy(g => g.Key == ModCatalog.UnknownAuthor ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            const float gap = 8;
            var view = new Rect(r.x, y + 4 * _s, r.width, r.yMax - y - 4 * _s);
            var heights = mods.ToDictionary(m => m, m => RowHeight(m, view.width - 18 * _s));
            var contentH = heights.Values.Sum() + mods.Count * gap * _s + groups.Count * 44 * _s + 10 * _s;
            var inner = BeginScroll(view, contentH);
            var ry = inner.y;
            if (mods.Count == 0)
                GUI.Label(new Rect(0, 0, inner.width, 40 * _s), _filter.Length > 0 ? "No installed mod matches \"" + _filter + "\"." : "No mods yet. Find some under Browse mods.", Tint(_body, CMuted));
            foreach (var g in groups)
            {
                Section(ref ry, inner.width, g.Key + "  (" + g.Count() + ")");
                foreach (var m in g)
                {
                    var h = heights[m];
                    if (ry + h > -40 && ry < view.height + 40) ModRow(new Rect(0, ry, inner.width, h), m);
                    ry += h + gap * _s;
                }
            }
            EndScroll();
        }

        /// <summary>A problem that affects more than one mod, with a fix: Unity code the game left out, or the wrong build of this menu.</summary>
        private string HealthBanner(out string fix, out Action action)
        {
            fix = null; action = null;
            if (StartupLog.AnyStrippedUnity && !StartupLog.UnityCompatInstalled)
            {
                fix = "Open Check BepInEx";
                action = OpenCheck;
                return "Some mods use Unity code that Nomad Drive's build left out. Unity Compatibility (in Nomad Mod Downloader > Check BepInEx) puts it back.";
            }
            if (Bep.Major == 5 && File.Exists(Path.Combine(Path.Combine(Paths.GameRootPath, "BepInEx"), "core\\BepInEx.Core.dll")))
            {
                fix = "Get the BepInEx 6 menu";
                action = () => InstallFromNexus(Nexus.DownloaderModId);
                return "This is the BepInEx 5 version of the Mods menu, and your game has BepInEx 6. Changes made here while the game runs wait until you switch to the BepInEx 6 version.";
            }
            return null;
        }

        private void OpenCheck()
        {
            if (Downloader.ShowCheck()) Toast("Opened Nomad Mod Downloader: see Check BepInEx.", true);
            else GetDownloader();
        }

        private List<StartupProblem> ProblemsOf(ModEntry m)
        {
            if (_problems.TryGetValue(m.Rel, out var c) && Time.unscaledTime < c.Until) return c.List;
            List<StartupProblem> list;
            try { list = m.IsSelf ? new List<StartupProblem>() : StartupLog.For(m); } catch { list = new List<StartupProblem>(); }
            // A mod that's running only gets its non-blocking reports.
            if (m.State == ModState.Running || m.State == ModState.StartedNow) list = list.Where(p => !p.Blocking || p.Kind == ProblemKind.Error).ToList();
            _problems[m.Rel] = (Time.unscaledTime + 3f, list);
            return list;
        }

        private bool HasSettings(ModEntry m)
        {
            if (_hasSettings.TryGetValue(m.Rel, out var c) && Time.unscaledTime < c.Until) return c.Has;
            bool has;
            try { has = ModSettings.Has(m); } catch { has = false; }
            _hasSettings[m.Rel] = (Time.unscaledTime + 5f, has);
            return has;
        }

        private float RowHeight(ModEntry m, float width)
        {
            var h = 76 * _s;
            if (_expanded.Contains(m.Rel))
            {
                var probs = ProblemsOf(m);
                if (probs.Count > 0) h += _wrapSmall.CalcHeight(new GUIContent(ProblemText(probs)), width - 40 * _s) + 18 * _s;
            }
            return h;
        }

        private static string ProblemText(List<StartupProblem> probs) =>
            string.Join("\n", probs.Take(3).Select(p => p.Title + ": " + p.Detail).ToArray());

        // ------------------------------------------------------------------
        // A mod's settings

        private void OpenSettings(ModEntry m)
        {
            _installedScroll = _scrollY;
            _scrollY = 0;
            _hasFocus = false;
            _settingsFor = m;
            try { _settings = ModSettings.For(m); } catch (Exception e) { _settings = new List<ModSettings>(); ModManagerPlugin.Log.LogWarning("Settings: " + e.Message); }
        }

        private void CloseSettings()
        {
            _settingsFor = null;
            _settings = null;
            _editing = _capturing = _dragging = null;
            _scrollY = _installedScroll;
            _hasFocus = false;
            _hasSettings.Clear();
        }

        private void DrawSettings(Rect r)
        {
            if (Btn(new Rect(r.x, r.y, 220 * _s, 40 * _s), "‹  Back to Installed", false)) { CloseSettings(); return; }
            var m = _settingsFor;
            GUI.Label(new Rect(r.x, r.y + 52 * _s, r.width, 36 * _s), m.Name + " settings", _h2);
            var live = _settings.Count > 0 && _settings.All(x => x.Live);
            GUI.Label(new Rect(r.x, r.y + 88 * _s, r.width - 240 * _s, 22 * _s),
                live ? "Changes are saved and used right away (some mods only read them when a game loads)."
                     : "Changes are saved to the mod's config file and used the next time the mod starts.", Tint(_small, CMuted));
            var first = _settings.FirstOrDefault();
            if (first != null && Btn(new Rect(r.xMax - 220 * _s, r.y + 52 * _s, 220 * _s, 40 * _s), "Config file", false)) OpenFolder(first.File);

            var items = _settings.SelectMany(ms => ms.Items.Select(it => (Owner: ms, It: it))).ToList();
            var view = new Rect(r.x, r.y + 124 * _s, r.width, r.height - 124 * _s);
            if (items.Count == 0)
            {
                GUI.Label(new Rect(view.x, view.y, view.width, 60 * _s), "This mod has no settings that BepInEx knows about. Some mods only create their config file after their first start.", Tint(_wrap, CMuted));
                return;
            }
            const float rowH = 92, gap = 8;
            var sections = items.Select(x => (x.Owner.Guid, x.It.Section)).Distinct().Count();
            var multi = _settings.Count > 1;
            var inner = BeginScroll(view, items.Count * (rowH + gap) * _s + sections * 44 * _s + 10 * _s);
            var y = inner.y;
            string lastKey = null;
            foreach (var (owner, it) in items)
            {
                var key = owner.Guid + "|" + it.Section;
                if (key != lastKey) { Section(ref y, inner.width, (multi ? owner.Title + "  ·  " : "") + it.Section); lastKey = key; }
                var row = new Rect(0, y, inner.width, rowH * _s);
                if (row.yMax > -20 && row.y < view.height + 20) SettingRow(row, owner, it);
                y += (rowH + gap) * _s;
            }
            EndScroll();
        }

        private void SettingRow(Rect r, ModSettings owner, Setting it)
        {
            Fill(r, r.Contains(Event.current.mousePosition) ? CCard : CRow, 12 * _s);
            var leftW = r.width * 0.52f;
            GUI.Label(new Rect(r.x + 18 * _s, r.y + 8 * _s, leftW, 28 * _s), Pretty(it.Key), _bold);
            var desc = it.Description.Length > 0 ? it.Description : "(no description)";
            GUI.Label(new Rect(r.x + 18 * _s, r.y + 38 * _s, leftW, r.height - 44 * _s), Clip(desc, 170), _wrapSmall);

            var cx = r.x + leftW + 30 * _s;
            var cw = r.xMax - cx - 16 * _s;
            var cy = r.y + (r.height - 42 * _s) / 2;
            var resetW = it.IsDefault ? 0 : 92 * _s;
            var ctrl = new Rect(cx, cy, cw - resetW - (resetW > 0 ? 8 * _s : 0), 42 * _s);
            void Apply(string v)
            {
                var err = owner.Set(it, v);
                if (err != null) Toast(Pretty(it.Key) + ": " + err, false);
            }

            if (_capturing == it)
            {
                Fill(ctrl, Alpha(CAccent, 0.25f), 11 * _s);
                GUI.Label(ctrl, "Press a key…  (Esc to cancel)", Center(_bold, CText));
            }
            else if (_editing == it)
            {
                Fill(new Rect(ctrl.x, ctrl.y, ctrl.width - 190 * _s, ctrl.height), CField, 11 * _s);
                var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
                GUI.Label(new Rect(ctrl.x + 14 * _s, ctrl.y, ctrl.width - 214 * _s, ctrl.height), _editText + caret, _body);
                if (Btn(new Rect(ctrl.xMax - 182 * _s, ctrl.y, 88 * _s, ctrl.height), "Save", true)) CommitEdit();
                if (Btn(new Rect(ctrl.xMax - 86 * _s, ctrl.y, 86 * _s, ctrl.height), "Cancel", false)) _editing = null;
            }
            else if (it.IsBool)
            {
                var on = string.Equals(it.Value, "true", StringComparison.OrdinalIgnoreCase);
                if (Toggle(ctrl, on, on ? "On" : "Off")) Apply(on ? "false" : "true");
            }
            else if (it.Options != null && it.Options.Count > 0)
            {
                var i = Math.Max(0, it.Options.FindIndex(o => string.Equals(o, it.Value, StringComparison.OrdinalIgnoreCase)));
                if (Btn(new Rect(ctrl.x, ctrl.y, 48 * _s, ctrl.height), "‹", false)) Apply(it.Options[(i - 1 + it.Options.Count) % it.Options.Count]);
                Fill(new Rect(ctrl.x + 54 * _s, ctrl.y, ctrl.width - 108 * _s, ctrl.height), CField, 11 * _s);
                GUI.Label(new Rect(ctrl.x + 54 * _s, ctrl.y, ctrl.width - 108 * _s, ctrl.height), Pretty(it.Value), Center(_body, CText));
                if (Btn(new Rect(ctrl.xMax - 48 * _s, ctrl.y, 48 * _s, ctrl.height), "›", false)) Apply(it.Options[(i + 1) % it.Options.Count]);
            }
            else if (it.IsNumber)
            {
                var v = Setting.Num(it.Value) ?? 0;
                var shown = _dragging == it ? _dragValue : v;
                if (Btn(new Rect(ctrl.x, ctrl.y, 48 * _s, ctrl.height), "−", false)) Apply((v - it.Step).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var mid = new Rect(ctrl.x + 54 * _s, ctrl.y, ctrl.width - 108 * _s, ctrl.height);
                Fill(mid, CField, 11 * _s);
                if (it.Min.HasValue && it.Max.HasValue && it.Max > it.Min)
                {
                    // A bar you can drag (GUI sliders are stripped from the game).
                    var t = (float)((shown - it.Min.Value) / (it.Max.Value - it.Min.Value));
                    Fill(new Rect(mid.x, mid.y, Mathf.Max(16 * _s, mid.width * Mathf.Clamp01(t)), mid.height), Alpha(CAccent, 0.45f), 11 * _s);
                    var e = Event.current;
                    if (e.type == EventType.MouseDown && mid.Contains(e.mousePosition)) { _dragging = it; _draggingOwner = owner; e.Use(); }
                    if (_dragging == it && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
                    {
                        var k = Mathf.Clamp01((e.mousePosition.x - mid.x) / mid.width);
                        _dragValue = it.Min.Value + k * (it.Max.Value - it.Min.Value);
                        if (it.IsInteger) _dragValue = Math.Round(_dragValue);
                        e.Use();
                    }
                    if (_dragging == it && e.type == EventType.MouseUp)
                    {
                        _dragging = null;
                        Apply(_dragValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        e.Use();
                    }
                }
                GUI.Label(mid, Num(shown, it.IsInteger), Center(_bold, CText));
                if (Btn(new Rect(ctrl.xMax - 48 * _s, ctrl.y, 48 * _s, ctrl.height), "+", false)) Apply((v + it.Step).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (it.IsKey)
            {
                Fill(new Rect(ctrl.x, ctrl.y, ctrl.width - 128 * _s, ctrl.height), CField, 11 * _s);
                GUI.Label(new Rect(ctrl.x, ctrl.y, ctrl.width - 128 * _s, ctrl.height), it.Value.Length > 0 ? it.Value : "None", Center(_body, CText));
                if (Btn(new Rect(ctrl.xMax - 120 * _s, ctrl.y, 120 * _s, ctrl.height), "Change", false)) { _capturing = it; _capturingOwner = owner; }
            }
            else
            {
                Fill(new Rect(ctrl.x, ctrl.y, ctrl.width - 108 * _s, ctrl.height), CField, 11 * _s);
                GUI.Label(new Rect(ctrl.x + 14 * _s, ctrl.y, ctrl.width - 130 * _s, ctrl.height), it.Value.Length > 0 ? it.Value : "(empty)", Tint(_body, it.Value.Length > 0 ? CText : CMuted));
                if (Btn(new Rect(ctrl.xMax - 100 * _s, ctrl.y, 100 * _s, ctrl.height), "Edit", false))
                { _editing = it; _editingOwner = owner; _editText = it.Value; }
            }
            if (resetW > 0 && _editing != it && _capturing != it && Btn(new Rect(r.xMax - 16 * _s - resetW, cy, resetW, 42 * _s), "Reset", false))
            {
                var err = owner.Reset(it);
                if (err != null) Toast(err, false);
            }
        }

        private void CommitEdit()
        {
            if (_editing == null) return;
            var err = _editingOwner.Set(_editing, _editText);
            if (err != null) Toast(Pretty(_editing.Key) + ": " + err, false);
            _editing = null;
        }

        /// <summary>Key rebinding: the next key pressed (with Ctrl/Shift/Alt held) becomes the setting.</summary>
        private void CaptureKey(UnityEngine.InputSystem.Keyboard kb)
        {
            if (kb.escapeKey.wasPressedThisFrame) { _capturing = null; return; }
            foreach (var k in kb.allKeys)
            {
                if (k == null || !k.wasPressedThisFrame) continue;
                var key = k.keyCode;
                if (IsModifier(key)) continue;
                var name = KeyCodeName(key);
                if (name == null) continue;
                var mods = new List<string>();
                if (kb.leftCtrlKey.isPressed) mods.Add("LeftControl");
                if (kb.rightCtrlKey.isPressed) mods.Add("RightControl");
                if (kb.leftShiftKey.isPressed) mods.Add("LeftShift");
                if (kb.rightShiftKey.isPressed) mods.Add("RightShift");
                if (kb.leftAltKey.isPressed) mods.Add("LeftAlt");
                if (kb.rightAltKey.isPressed) mods.Add("RightAlt");
                var it = _capturing;
                // A plain KeyCode setting takes just the key; a KeyboardShortcut takes "Key + Modifier…".
                var value = it.TypeName == "KeyCode" ? name : string.Join(" + ", new[] { name }.Concat(mods).ToArray());
                var err = _capturingOwner.Set(it, value);
                if (err != null) Toast(err, false);
                _capturing = null;
                return;
            }
        }

        private static bool IsModifier(UnityEngine.InputSystem.Key k) =>
            k == UnityEngine.InputSystem.Key.LeftCtrl || k == UnityEngine.InputSystem.Key.RightCtrl || k == UnityEngine.InputSystem.Key.LeftShift
            || k == UnityEngine.InputSystem.Key.RightShift || k == UnityEngine.InputSystem.Key.LeftAlt || k == UnityEngine.InputSystem.Key.RightAlt
            || k == UnityEngine.InputSystem.Key.LeftMeta || k == UnityEngine.InputSystem.Key.RightMeta;

        /// <summary>Input System key → the UnityEngine.KeyCode name BepInEx config files use.</summary>
        internal static string KeyCodeName(UnityEngine.InputSystem.Key k)
        {
            var n = k.ToString();
            if (n.StartsWith("Digit")) return "Alpha" + n.Substring(5);
            if (n.StartsWith("Numpad"))
            {
                var rest = n.Substring(6);
                switch (rest)
                {
                    case "Enter": return "KeypadEnter";
                    case "Divide": return "KeypadDivide";
                    case "Multiply": return "KeypadMultiply";
                    case "Plus": return "KeypadPlus";
                    case "Minus": return "KeypadMinus";
                    case "Period": return "KeypadPeriod";
                    case "Equals": return "KeypadEquals";
                    default: return "Keypad" + rest;
                }
            }
            switch (n)
            {
                case "Enter": return "Return";
                case "Backquote": return "BackQuote";
                case "Quote": return "Quote";
                case "ContextMenu": return "Menu";
                case "PrintScreen": return "Print";
                case "None": case "IMESelected": case "OEM1": case "OEM2": case "OEM3": case "OEM4": case "OEM5": return null;
            }
            return n; // letters, F-keys, arrows, Space, Tab, Home, End, PageUp... share the name
        }

        private static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Num(double v, bool integer) =>
            integer ? Math.Round(v).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    : v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", System.Globalization.CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Profiles

        private void DrawProfiles(Rect r)
        {
            GUI.Label(new Rect(r.x, r.y, r.width, 36 * _s), "Profiles", _h2);
            GUI.Label(new Rect(r.x, r.y + 40 * _s, r.width * 0.8f, 50 * _s),
                "Save which mods are on, then switch between sets in one go. Mods that aren't in a profile are switched off when you use it.",
                Tint(_wrapSmall, CMuted));
            var y = r.y + 100 * _s;
            if (_naming)
            {
                var field = new Rect(r.x, y, r.width - 380 * _s, 44 * _s);
                Fill(field, CField, 11 * _s);
                var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
                GUI.Label(new Rect(field.x + 16 * _s, field.y, field.width - 30 * _s, field.height), _profileName.Length > 0 ? _profileName + caret : "Type a name, e.g. \"Vanilla+\" or \"Everything\"", Tint(_body, _profileName.Length > 0 ? CText : CMuted));
                if (Btn(new Rect(field.xMax + 10 * _s, y, 180 * _s, 44 * _s), "Save", true)) SaveProfile();
                if (Btn(new Rect(field.xMax + 198 * _s, y, 170 * _s, 44 * _s), "Cancel", false)) _naming = false;
            }
            else if (Btn(new Rect(r.x, y, 300 * _s, 44 * _s), "Save what's on now…", true))
            {
                _naming = true;
                _profileName = "Profile " + (Profiles.Load().Count + 1);
            }
            y += 64 * _s;

            var list = Profiles.Load().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) { GUI.Label(new Rect(r.x, y, r.width, 40 * _s), "No profiles yet.", Tint(_body, CMuted)); return; }
            var view = new Rect(r.x, y, r.width, r.yMax - y);
            const float rowH = 76, gap = 8;
            var inner = BeginScroll(view, list.Count * (rowH + gap) * _s);
            var ry = inner.y;
            foreach (var p in list)
            {
                var row = new Rect(0, ry, inner.width, rowH * _s);
                Fill(row, row.Contains(Event.current.mousePosition) ? CCard : CRow, 12 * _s);
                var installed = p.On.Count(x => _cat.Mods.Any(mm => mm.Rel.Equals(x.Rel, StringComparison.OrdinalIgnoreCase)));
                var matches = MatchesNow(p);
                GUI.Label(new Rect(row.x + 20 * _s, row.y + 10 * _s, row.width * 0.5f, 28 * _s), p.Name, _bold);
                GUI.Label(new Rect(row.x + 20 * _s, row.y + 42 * _s, row.width * 0.6f, 24 * _s),
                    $"{p.On.Count} mod(s) on" + (installed < p.On.Count ? $"  ·  {p.On.Count - installed} not installed" : "") + $"  ·  saved {p.Saved:d MMM}" + (matches ? "  ·  in use" : ""),
                    Tint(_small, matches ? CGood : CMuted));
                var bx = row.xMax - 14 * _s;
                var by = row.y + (row.height - 40 * _s) / 2;
                bool B(string text, bool primary, float w)
                {
                    bx -= w * _s;
                    var hit = Btn(new Rect(bx, by, w * _s, 40 * _s), text, primary);
                    bx -= 8 * _s;
                    return hit;
                }
                var sure = _confirmProfile == p.Name && Time.unscaledTime < _confirmUntil;
                if (B(sure ? "Sure?" : "Delete", sure, 100))
                {
                    if (sure) { Profiles.Delete(p.Name); _confirmProfile = null; }
                    else { _confirmProfile = p.Name; _confirmUntil = Time.unscaledTime + 3f; }
                }
                if (B("Update", false, 100)) { Profiles.Capture(p.Name, _cat); Toast($"Saved what's on now as \"{p.Name}\".", true); }
                if (!matches && B("Use", true, 90)) UseProfile(p);
                ry += (rowH + gap) * _s;
            }
            EndScroll();
        }

        private bool MatchesNow(Profile p)
        {
            var want = new HashSet<string>(p.On.Select(x => x.Rel), StringComparer.OrdinalIgnoreCase);
            return _cat.Mods.Where(m => !m.IsSelf && m.State != ModState.Library).All(m => Profiles.IsOn(m) == want.Contains(m.Rel));
        }

        private void SaveProfile()
        {
            var name = _profileName.Trim();
            if (name.Length == 0) return;
            var p = Profiles.Capture(name, _cat);
            _naming = false;
            Toast($"Saved \"{p.Name}\" ({p.On.Count} mod(s) on).", true);
        }

        private void UseProfile(Profile p)
        {
            var (changed, missing) = Profiles.Apply(p, _cat);
            _cat.Refresh();
            var restart = _cat.RestartItems;
            Toast(changed == 0 ? $"\"{p.Name}\" was already in use." : $"Switched to \"{p.Name}\"" + (restart > 0 ? " — some changes apply when you restart." : "."), true);
            var ids = missing.Where(x => x.NexusId > 0).Select(x => x.NexusId).ToList();
            if (ids.Count > 0) InstallMany(ids, $"{ids.Count} mod(s) from \"{p.Name}\" that aren't installed");
        }

        // ------------------------------------------------------------------

        private void InstallMany(List<int> ids, string what)
        {
            if (ids.Count == 0) return;
            if (ids.Count == 1 || !Downloader.CanQueue)
            {
                InstallFromNexus(ids[0]);
                if (ids.Count > 1) Toast($"Update Nomad Mod Downloader to {Downloader.QueueVersion} to send all {ids.Count} at once. Opened the first one.", false);
                return;
            }
            if (Downloader.InstallMany(ids)) Toast($"Sent {what} to Nomad Mod Downloader. Click each download button there.", true);
            else GetDownloader();
        }

        private void InstallFile(int modId, int fileId)
        {
            if (Downloader.InstallFile(modId, fileId)) Toast("Opened that version in Nomad Mod Downloader — click Nexus's download button there.", true);
            else InstallFromNexus(modId);
        }

        private void Section(ref float y, float w, string text)
        {
            GUI.Label(new Rect(4 * _s, y + 8 * _s, w, 28 * _s), text, Tint(_bold, CAccent));
            y += 44 * _s;
        }

        private void ModRow(Rect r, ModEntry m)
        {
            var hover = r.Contains(Event.current.mousePosition);
            Fill(r, hover ? CCard : CRow, 12 * _s);
            var faded = m.State == ModState.Disabled || m.State == ModState.WillRemove;
            var name = Tint(_bold, faded ? CMuted : CText);
            GUI.Label(new Rect(r.x + 20 * _s, r.y + 10 * _s, r.width * 0.5f, 28 * _s), m.Name, name);
            var nw = name.CalcSize(new GUIContent(m.Name)).x;
            if (m.Version.Length > 0) GUI.Label(new Rect(r.x + 30 * _s + nw, r.y + 13 * _s, 140 * _s, 24 * _s), "v" + m.Version.TrimStart('v'), Tint(_small, CMuted));

            var (label, color) = StatusChip(m);
            var probs = ProblemsOf(m);
            var problem = probs.FirstOrDefault();
            if (problem != null && problem.Blocking && m.State == ModState.NotLoaded) { label = "Didn't start: " + problem.Title; color = CBad; }
            var line2 = new List<string>();
            line2.Add(m.Category);
            if (m.UpdateAvailable && !m.UpdateStaged) line2.Add("v" + m.NexusVersion.TrimStart('v') + " on Nexus");
            if (m.UpdateStaged) line2.Add("update applies on restart");
            if (problem != null && !(problem.Blocking && m.State == ModState.NotLoaded)) line2.Add(problem.Title);
            else if (problem == null && m.Note.Length > 0 && m.State != ModState.Running && m.State != ModState.Settling) line2.Add(m.Note);
            var statusW = _smallBold.CalcSize(new GUIContent(label ?? "")).x;
            if (label != null) GUI.Label(new Rect(r.x + 20 * _s, r.y + 42 * _s, statusW + 4, 24 * _s), label, Tint(_smallBold, color));
            var line2R = new Rect(r.x + 20 * _s + (label != null ? statusW + 12 * _s : 0), r.y + 42 * _s, r.width * 0.5f - statusW, 24 * _s);
            if (line2.Count > 0)
                GUI.Label(line2R, (label != null ? "·  " : "") + string.Join("  ·  ", line2.ToArray()), Tint(_small, problem != null ? CWarn : CMuted));

            // The why, under the row (click the status line, or "Why?").
            var open = _expanded.Contains(m.Rel);
            if (open && probs.Count > 0)
            {
                var text = ProblemText(probs);
                GUI.Label(new Rect(r.x + 20 * _s, r.y + 72 * _s, r.width - 40 * _s, r.height - 80 * _s), text, Tint(_wrapSmall, CText));
            }

            var bx = r.xMax - 14 * _s;
            var by = r.y + (76 * _s - 40 * _s) / 2;
            bool B(string text, bool primary, float width = 108)
            {
                bx -= width * _s;
                var hit = Btn(new Rect(bx, by, width * _s, 40 * _s), text, primary);
                bx -= 8 * _s;
                return hit;
            }
            if (m.IsSelf) return;

            var confirming = _confirm == m && Time.unscaledTime < _confirmUntil;
            switch (m.State)
            {
                case ModState.Running:
                case ModState.StartedNow:
                case ModState.NotLoaded:
                case ModState.Library:
                    if (B(confirming ? "Sure?" : "Remove", confirming, 100)) RemoveClicked(m, confirming);
                    if (B("Disable", false)) _cat.Disable(m);
                    break;
                case ModState.Disabled:
                    if (B(confirming ? "Sure?" : "Remove", confirming, 100)) RemoveClicked(m, confirming);
                    if (B("Enable", true)) _cat.Enable(m);
                    break;
                case ModState.NeedsRestart:
                case ModState.Failed:
                    if (B(confirming ? "Sure?" : "Remove", confirming, 100)) RemoveClicked(m, confirming);
                    if (ModCatalog.IsHotReady(m) && B("Start now", true, 120)) { if (_host.AtMainMenu) _cat.TryStart(m); }
                    break;
                case ModState.WillDisable:
                    if (B("Undo", false, 90)) _cat.Enable(m);
                    break;
                case ModState.WillEnable:
                    if (B("Undo", false, 90)) _cat.Disable(m);
                    break;
                case ModState.WillRemove:
                    if (B("Undo", false, 90)) _cat.UndoRemove(m);
                    break;
            }
            if (m.UpdateAvailable && !m.UpdateStaged && m.State != ModState.WillRemove && B("Update", true, 100)) InstallFromNexus(m.NexusId);
            if (problem != null)
            {
                var (fixLabel, fix) = FixFor(m, problem);
                if (fixLabel != null && B(fixLabel, true, Mathf.Max(100, _bold.CalcSize(new GUIContent(fixLabel)).x / _s + 30))) fix();
                if (B(open ? "Hide" : "Why?", false, 80)) { if (open) _expanded.Remove(m.Rel); else _expanded.Add(m.Rel); }
            }
            if (HasSettings(m) && m.State != ModState.WillRemove && B("Settings", false, 110)) OpenSettings(m);
            if (m.NexusId > 0 && problem == null && B("Page", false, 84)) Application.OpenURL($"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{m.NexusId}");
        }

        /// <summary>The one-click fix for a startup problem, if there is one.</summary>
        private (string, Action) FixFor(ModEntry m, StartupProblem p)
        {
            switch (p.Kind)
            {
                case ProblemKind.MissingDependency:
                {
                    var key = ModCatalog.NameKey(StartupLog.Friendly(p.Other));
                    var off = _cat.Mods.FirstOrDefault(x => x != m && (x.State == ModState.Disabled || x.State == ModState.WillDisable)
                                                         && (ModSettings.PluginsOf(x).Any(pl => pl.Guid == p.Other) || ModCatalog.NameKey(x.Name) == key));
                    if (off != null) return ("Turn on " + Clip(off.Name, 18), () => _cat.Enable(off));
                    return ("Find it", () => { _tab = TabBrowse; CloseDetails(); _search = StartupLog.Friendly(p.Other); RunSearch(false); });
                }
                case ProblemKind.Incompatible:
                case ProblemKind.WrongProcess:
                    return m.State == ModState.Disabled ? (null, null) : ("Turn off", (Action)(() => _cat.Disable(m)));
                case ProblemKind.NewerCopy:
                    return ("Remove copy", () => _cat.Remove(m));
                case ProblemKind.StrippedUnity:
                    return StartupLog.UnityCompatInstalled ? ("Restart", (Action)Restarter.Restart) : ("Fix it", (Action)OpenCheck);
                case ProblemKind.Error:
                    if (m.UpdateAvailable) return (null, null); // Update is already there
                    return m.NexusId > 0 ? ("Check page", (Action)(() => Application.OpenURL($"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{m.NexusId}?tab=posts")))
                                         : ("Open log", (Action)(() => OpenFolder(StartupLog.LogPath)));
            }
            return (null, null);
        }

        private (string, Color) StatusChip(ModEntry m)
        {
            if (m.IsSelf) return ("This menu", CAccent);
            switch (m.State)
            {
                case ModState.Running: return ("Enabled", CGood);
                case ModState.StartedNow: return ("Started — no restart needed", CGood);
                case ModState.Disabled: return ("Disabled", CMuted);
                case ModState.Settling: return ("Installing…", CAccent);
                case ModState.NeedsRestart:
                    if (!ModCatalog.IsHotReady(m)) return ("Restart to start", CWarn);
                    return (_host.AtMainMenu ? "Ready to start" : "Starts at the main menu", CAccent);
                case ModState.WillDisable: return ("Disables on restart", CWarn);
                case ModState.WillEnable: return ("Enables on restart", CWarn);
                case ModState.WillRemove: return ("Removed on restart", CWarn);
                case ModState.Library: return ("Support files", CMuted);
                case ModState.NotLoaded: return ("Not loaded", CBad);
                case ModState.Failed: return ("Restart to start", CWarn);
            }
            return (null, CMuted);
        }

        private void RemoveClicked(ModEntry m, bool confirming)
        {
            if (!confirming) { _confirm = m; _confirmUntil = Time.unscaledTime + 3f; return; }
            _confirm = null;
            _cat.Remove(m);
        }

        // ------------------------------------------------------------------
        // Changes

        private void DrawChanges(Rect r)
        {
            GUI.Label(new Rect(r.x, r.y, r.width, 36 * _s), "Changes", _h2);
            GUI.Label(new Rect(r.x, r.y + 40 * _s, r.width * 0.8f, 50 * _s),
                "Mods that are already running can't be swapped out while the game is open. These changes are applied the next time Nomad Drive starts, before any mod loads.",
                Tint(_wrapSmall, CMuted));

            var items = _cat.Mods.Where(m => m.CanRestartFix && !ModCatalog.IsHotReady(m)).ToList();
            var y = r.y + 108 * _s;
            if (items.Count == 0)
            {
                GUI.Label(new Rect(r.x, y, r.width, 40 * _s), "Nothing waiting — every change so far happened right away.", Tint(_body, CMuted));
                return;
            }
            foreach (var m in items)
            {
                var row = new Rect(r.x, y, r.width, 62 * _s);
                Fill(row, CRow, 12 * _s);
                string what = m.State == ModState.WillRemove ? "Remove" : m.State == ModState.WillDisable ? "Disable" : m.State == ModState.WillEnable ? "Enable" : m.UpdateStaged ? "Update" : "Start";
                GUI.Label(new Rect(row.x + 20 * _s, row.y, 120 * _s, row.height), what, Tint(_bold, CWarn));
                GUI.Label(new Rect(row.x + 140 * _s, row.y, row.width - 280 * _s, row.height),
                    m.Name + (what == "Start" && m.Note.Length > 0 ? "  —  " + m.Note : ""), _body);
                var b = new Rect(row.xMax - 104 * _s, row.y + 11 * _s, 90 * _s, 40 * _s);
                if (m.State == ModState.WillRemove) { if (Btn(b, "Undo", false)) _cat.UndoRemove(m); }
                else if (m.State == ModState.WillDisable) { if (Btn(b, "Undo", false)) _cat.Enable(m); }
                else if (m.State == ModState.WillEnable) { if (Btn(b, "Undo", false)) _cat.Disable(m); }
                y += 70 * _s;
                if (y > r.yMax - 80 * _s) break;
            }
            if (Btn(new Rect(r.x, Mathf.Min(y + 12 * _s, r.yMax - 48 * _s), 220 * _s, 46 * _s), "Restart now", true)) Restarter.Restart();
        }

        // ==================================================================
        // Toasts

        private void DrawToasts()
        {
            _toasts.RemoveAll(x => Time.unscaledTime > x.Until);
            if (_toasts.Count == 0) return;
            Styles();
            var y = Screen.height - 36 * _s;
            for (var i = _toasts.Count - 1; i >= 0; i--)
            {
                var (text, good, until) = _toasts[i];
                var size = _body.CalcSize(new GUIContent(text));
                var w = Mathf.Min(size.x + 56 * _s, Screen.width - 40);
                var r = new Rect((Screen.width - w) / 2, y - 52 * _s, w, 48 * _s);
                var a = Mathf.Clamp01((until - Time.unscaledTime) / 0.4f);
                Fill(r, Alpha(CPanel, 0.96f * a), 14 * _s);
                Fill(new Rect(r.x + 14 * _s, r.y + 17 * _s, 14 * _s, 14 * _s), Alpha(good ? CGood : CWarn, a), 7 * _s);
                GUI.Label(new Rect(r.x + 40 * _s, r.y, r.width - 50 * _s, r.height), text, Tint(_body, Alpha(CText, a)));
                y -= 58 * _s;
            }
        }

        // ==================================================================
        // Widgets

        /// <summary>Scroll area made from a clipping group. Returns the content rect, offset by the scroll.</summary>
        private Rect BeginScroll(Rect view, float contentH)
        {
            var max = Mathf.Max(0, contentH - view.height);
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && view.Contains(e.mousePosition)) { _scrollY += e.delta.y * 24 * _s; e.Use(); }
            var track = new Rect(view.xMax - 8 * _s, view.y, 8 * _s, view.height);
            if (max > 0)
            {
                var thumbH = Mathf.Max(44 * _s, view.height * view.height / contentH);
                if (e.type == EventType.MouseDown && Grow(track, 6 * _s).Contains(e.mousePosition)) { _dragBar = true; e.Use(); }
                if (e.type == EventType.MouseUp) _dragBar = false;
                if (_dragBar && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
                {
                    _scrollY = (e.mousePosition.y - view.y - thumbH / 2) / (view.height - thumbH) * max;
                    e.Use();
                }
                _scrollY = Mathf.Clamp(_scrollY, 0, max);
                Fill(track, CField, 4 * _s);
                Fill(new Rect(track.x, view.y + (view.height - thumbH) * (_scrollY / max), track.width, thumbH), _dragBar ? CAccent : CEdge, 4 * _s);
            }
            else _scrollY = 0;
            var groupR = new Rect(view.x, view.y, view.width - 18 * _s, view.height);
            _curView = new Rect(GUIUtility.GUIToScreenPoint(groupR.position), groupR.size);
            _inScroll = true;
            GUI.BeginGroup(groupR, GUIContent.none, GUIStyle.none);
            return new Rect(0, -_scrollY, view.width - 18 * _s, contentH);
        }

        private void EndScroll() { GUI.EndGroup(); _inScroll = false; }

        // ------------------------------------------------------------------
        // Controller navigation: every clickable thing goes through Hit(), which also makes it reachable with a pad.

        /// <summary>An invisible button over <paramref name="r"/>; also a stop for controller navigation.</summary>
        private bool Hit(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                var sr = new Rect(GUIUtility.GUIToScreenPoint(r.position), r.size);
                _navNow.Add((sr, _inScroll ? _curView : new Rect(0, 0, Screen.width, Screen.height), _inScroll));
                if (_padMode && _hasFocus && Near(sr, _focus))
                {
                    Ring(r);
                    if (_navActivate) { _navActivate = false; return true; }
                }
            }
            return GUI.Button(r, GUIContent.none, _invisible);
        }

        private static bool Near(Rect a, Rect b) => Mathf.Abs(a.x - b.x) < 3 && Mathf.Abs(a.y - b.y) < 3 && Mathf.Abs(a.width - b.width) < 3;

        private void Ring(Rect r)
        {
            var t = 3 * _s;
            Fill(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), CAccent, 0);
            Fill(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), CAccent, 0);
            Fill(new Rect(r.x - t, r.y, t, r.height), CAccent, 0);
            Fill(new Rect(r.xMax, r.y, t, r.height), CAccent, 0);
        }

        /// <summary>After a frame is drawn: keep the focus on something that exists, move it, and scroll it into view.</summary>
        private void FinishNav()
        {
            _navLast = new List<(Rect, Rect, bool)>(_navNow);
            _navNow.Clear();
            if (!_padMode || _navLast.Count == 0) { _navActivate = false; _navDir = -1; return; }

            var visible = _navLast.Where(n => !n.InScroll || n.View.Overlaps(n.R)).ToList();
            if (!_hasFocus || !_navLast.Any(n => Near(n.R, _focus)))
            {
                var pool = visible.Count > 0 ? visible : _navLast;
                var c = _hasFocus ? _focus.center : new Vector2(0, 0);
                var best = pool.OrderBy(n => (n.R.center - c).sqrMagnitude).First();
                // First time: start in the content area, not the left menu.
                if (!_hasFocus) best = pool.OrderBy(n => n.R.x < Screen.width * 0.3f ? 1 : 0).ThenBy(n => n.R.y).ThenBy(n => n.R.x).First();
                _focus = best.R; _hasFocus = true;
            }
            if (_navDir >= 0)
            {
                var f = _focus.center;
                (Rect R, Rect View, bool InScroll)? pick = null;
                var bestScore = float.MaxValue;
                foreach (var n in _navLast)
                {
                    if (Near(n.R, _focus)) continue;
                    var d = n.R.center - f;
                    float along, across;
                    switch (_navDir)
                    {
                        case 0: along = -d.y; across = Mathf.Abs(d.x); break;
                        case 1: along = d.y; across = Mathf.Abs(d.x); break;
                        case 2: along = -d.x; across = Mathf.Abs(d.y); break;
                        default: along = d.x; across = Mathf.Abs(d.y); break;
                    }
                    if (along < 4) continue;
                    var score = along + across * 2.5f;
                    if (score < bestScore) { bestScore = score; pick = n; }
                }
                if (pick != null) _focus = pick.Value.R;
                else if (_navDir == 1) _scrollY += 220 * _s;   // at the bottom of what's drawn: scroll for more
                else if (_navDir == 0) _scrollY -= 220 * _s;
                _navDir = -1;
            }
            // Scroll the focused item into view.
            var cur = _navLast.FirstOrDefault(n => Near(n.R, _focus));
            if (cur.InScroll)
            {
                float delta = 0;
                if (cur.R.y < cur.View.y + 8 * _s) delta = cur.R.y - cur.View.y - 16 * _s;
                else if (cur.R.yMax > cur.View.yMax - 8 * _s) delta = cur.R.yMax - cur.View.yMax + 16 * _s;
                if (Mathf.Abs(delta) > 0.5f) { _scrollY += delta; _focus.y -= delta; }
            }
            _navActivate = false;
        }

        /// <summary>The downloader's rounded button: amber with dark text (primary) or slate with cream text.</summary>
        private bool Btn(Rect r, string text, bool primary, bool selected = false)
        {
            var hover = r.Contains(Event.current.mousePosition);
            var down = hover && Input_MouseHeld();
            Color fill, fg;
            if (primary || selected)
            {
                fill = down ? CWarn : hover ? CAccentHover : CAccent;
                fg = CInk;
            }
            else
            {
                fill = down ? CCard : hover ? CEdge : CLine;
                fg = CText;
            }
            Fill(r, fill, 11 * _s);
            GUI.Label(r, text, Center(_bold, fg));
            return Hit(r);
        }

        private static bool Input_MouseHeld()
        {
            try { return UnityEngine.InputSystem.Mouse.current?.leftButton.isPressed == true; } catch { return false; }
        }

        private bool Toggle(Rect r, bool on, string text)
        {
            var box = new Rect(r.xMax - 50 * _s, r.y + 3 * _s, 50 * _s, 26 * _s);
            Fill(box, on ? CAccent : CLine, 13 * _s);
            Fill(new Rect(on ? box.xMax - 23 * _s : box.x + 3 * _s, box.y + 3 * _s, 20 * _s, 20 * _s), on ? CInk : CText, 10 * _s);
            var style = new GUIStyle(Tint(_small, CText)) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(r.x, r.y, r.width - 62 * _s, r.height), text, style);
            return Hit(r);
        }

        private void StatusText(Rect r, string text, Color c)
        {
            Fill(new Rect(r.x, r.y + r.height / 2 - 5 * _s, 10 * _s, 10 * _s), c, 5 * _s);
            GUI.Label(new Rect(r.x + 18 * _s, r.y, r.width, r.height), text, Tint(_smallBold, c));
        }

        private void Glass(Rect r, float radius)
        {
            Fill(r, Alpha(CPanel, 236f / 255f), radius * _s);
        }

        private void DrawSprite(Rect r, Sprite sp)
        {
            var tex = sp.texture;
            if (tex == null) return;
            var tr = sp.textureRect;
            // Fit inside r, keeping the sprite's shape
            var aspect = tr.width / tr.height;
            var w = r.width; var h = w / aspect;
            if (h > r.height) { h = r.height; w = h * aspect; }
            var dst = new Rect(r.center.x - w / 2, r.center.y - h / 2, w, h);
            if (Mathf.Approximately(tr.width, tex.width) && Mathf.Approximately(tr.height, tex.height))
            {
                GUI.DrawTexture(dst, tex, ScaleMode.StretchToFill, true);
                return;
            }
            // Part of an atlas (GUI.DrawTextureWithTexCoords is stripped): draw the whole atlas, clipped to the sprite.
            var k = dst.width / tr.width;
            GUI.BeginGroup(dst, GUIContent.none, GUIStyle.none);
            GUI.DrawTexture(new Rect(-tr.x * k, -(tex.height - tr.yMax) * k, tex.width * k, tex.height * k), tex, ScaleMode.StretchToFill, true);
            GUI.EndGroup();
        }

        // ==================================================================
        // Style

        private void Styles()
        {
            if (Mathf.Approximately(_styleScale, _s) && _body != null) return;
            _styleScale = _s;
            if (_font == null) _font = GameFont();
            GUIStyle S(int size, FontStyle fs, Color c)
            {
                var st = new GUIStyle { font = _font, fontSize = Mathf.RoundToInt(size * _s), fontStyle = fs, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, richText = false };
                st.normal.textColor = c;
                return st;
            }
            _body = S(18, FontStyle.Normal, CText);
            _bold = S(18, FontStyle.Bold, CText);
            _small = S(15, FontStyle.Normal, CMuted);
            _smallBold = S(15, FontStyle.Bold, CMuted);
            _nav = S(36, FontStyle.Bold, CText);
            _h2 = S(28, FontStyle.Bold, CText);
            _wrap = S(17, FontStyle.Normal, CText); _wrap.wordWrap = true; _wrap.alignment = TextAnchor.UpperLeft;
            _wrapSmall = S(15, FontStyle.Normal, CMuted); _wrapSmall.wordWrap = true; _wrapSmall.alignment = TextAnchor.UpperLeft;
            _invisible = new GUIStyle();
        }

        /// <summary>The game's own Noto Sans (from its TextMeshPro fonts), falling back to Windows fonts.</summary>
        private static Font GameFont()
        {
            try
            {
                Font best = null;
                foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null || !f.dynamic) continue;
                    var n = f.name.ToLowerInvariant();
                    if (!n.Contains("noto")) continue;
                    if (best == null || n.Contains("regular") || (!best.name.ToLowerInvariant().Contains("regular") && !n.Contains("bold") && !n.Contains("italic"))) best = f;
                }
                if (best != null) { ModManagerPlugin.Log.LogInfo("Mods menu font: " + best.name); return best; }
            }
            catch { }
            try { return Font.CreateDynamicFontFromOSFont(new[] { "Noto Sans", "Segoe UI", "Arial" }, 18); } catch { return null; }
        }

        /// <summary>The downloader's backdrop shade: dark on the left, lighter in the middle, a little darker on the right.</summary>
        private Texture2D ShadeTex()
        {
            if (_shade != null) return _shade;
            _shade = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float[] pos = { 0f, 0.3f, 0.65f, 1f };
            float[] alpha = { 240, 160, 95, 150 };
            for (var x = 0; x < 256; x++)
            {
                var t = x / 255f;
                var i = 0;
                while (i < pos.Length - 2 && t > pos[i + 1]) i++;
                var k = (t - pos[i]) / (pos[i + 1] - pos[i]);
                var a = Mathf.Lerp(alpha[i], alpha[i + 1], k) / 255f;
                _shade.SetPixel(x, 0, new Color(CShade.r, CShade.g, CShade.b, a));
            }
            _shade.Apply();
            UnityEngine.Object.DontDestroyOnLoad(_shade);
            return _shade;
        }

        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        private static GUIStyle Tint(GUIStyle s, Color c) { var n = new GUIStyle(s); n.normal.textColor = c; return n; }
        private static GUIStyle Center(GUIStyle s, Color c) { var n = Tint(s, c); n.alignment = TextAnchor.MiddleCenter; return n; }
        private GUIStyle Size(GUIStyle s, int size) { var n = new GUIStyle(s) { fontSize = Mathf.RoundToInt(size * _s) }; return n; }
        private static Color Alpha(Color c, float a) { c.a = a; return c; }
        private static Color Lighten(Color c, float d) => new Color(Mathf.Min(1, c.r + d), Mathf.Min(1, c.g + d), Mathf.Min(1, c.b + d), c.a);
        private static Rect Grow(Rect r, float d) => new Rect(r.x - d, r.y - d, r.width + 2 * d, r.height + 2 * d);
        private static string Short(long n) => n >= 1000000 ? (n / 1000000f).ToString("0.#") + "M" : n >= 1000 ? (n / 1000f).ToString("0.#") + "k" : n.ToString();
        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

        private void Fill(Rect r, Color c, float radius)
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                UnityEngine.Object.DontDestroyOnLoad(_px);
            }
            if (radius > 0.01f) { GUI.DrawTexture(r, _px, ScaleMode.StretchToFill, true, 0, c, 0, radius); return; }
            var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, _px); GUI.color = old;
        }

        private static void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch { Application.OpenURL("file:///" + path.Replace('\\', '/')); }
        }
    }
}
