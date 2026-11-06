using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private const int TabInstalled = 0, TabBrowse = 1, TabChanges = 2;

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

        public ModsMenu(ModManagerPlugin host, ModCatalog catalog) { _host = host; _cat = catalog; }

        // ==================================================================
        // Open / close

        public void Open(MainMenuPanel panel)
        {
            if (IsOpen) return;
            IsOpen = true;
            _scrollY = 0;
            HookText();
            _cat.Refresh();
            FindGameArt(panel);

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
            if (_blocked != null) { _blocked.blocksRaycasts = _blockedWas; _blocked = null; }
            if (_hidden != null) { _hidden.alpha = _hiddenAlpha; _hidden = null; }
        }

        public void Toast(string text, bool good)
        {
            _toasts.Add((text, good, Time.unscaledTime + 5f));
            if (_toasts.Count > 4) _toasts.RemoveAt(0);
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
            if (!IsOpen || _tab != TabBrowse) return;
            if (ch == '\b') { if (_search.Length > 0) _search = _search.Substring(0, _search.Length - 1); }
            else if (ch == '\r' || ch == '\n') _enterPressed = true;
            else if (!char.IsControl(ch) && _search.Length < 60) _search += ch;
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
        private void FindGameArt(MainMenuPanel panel)
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
                case TabInstalled: DrawInstalled(inner); break;
                case TabBrowse: DrawBrowse(inner); break;
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
                (TabChanges, restart > 0 ? $"Changes ({restart})" : "Changes")
            };
            foreach (var (tab, label) in items)
            {
                var size = _nav.CalcSize(new GUIContent(label));
                var ir = new Rect(r.center.x - size.x / 2, y, size.x, 50 * _s);
                var hover = ir.Contains(Event.current.mousePosition);
                GUI.Label(ir, label, Tint(_nav, _tab == tab || hover ? CAccent : CText));
                if (GUI.Button(ir, GUIContent.none, _invisible) && _tab != tab)
                {
                    _tab = tab; _scrollY = 0;
                    if (tab == TabBrowse && !_searchedOnce) RunSearch(false);
                    if (tab == TabInstalled) _cat.Refresh();
                }
                y += 58 * _s;
            }
            var back = "Back";
            var bs = _nav.CalcSize(new GUIContent(back));
            var br = new Rect(r.center.x - bs.x / 2, y + 10 * _s, bs.x, 50 * _s);
            GUI.Label(br, back, Tint(_nav, br.Contains(Event.current.mousePosition) ? CAccent : CMuted));
            if (GUI.Button(br, GUIContent.none, _invisible)) { Close(); return; }

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
                if (GUI.Button(xr, GUIContent.none, _invisible)) { _search = ""; RunSearch(false); }
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
                if (GUI.Button(cr, GUIContent.none, _invisible) && !on) { _category = c; _scrollY = 0; }
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
                    : "Install uses Nomad Mod Downloader, a free app: Nexus needs you to click its download button, so the app opens the mod for you and installs it. Get it from GitHub with one click.";
                GUI.Label(new Rect(banner.x + 18 * _s, banner.y + 10 * _s, banner.width - 270 * _s, banner.height - 20 * _s), text, _wrapSmall);
                var br = new Rect(banner.xMax - 240 * _s, banner.y + 14 * _s, 222 * _s, 42 * _s);
                if (AppFetcher.Busy) ProgressBar(br, AppFetcher.Progress);
                else if (Btn(br, outdated ? "Update downloader" : "Get the downloader", true)) GetDownloader();
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
            if (Btn(new Rect(x + 120 * _s, by, 92 * _s, 40 * _s), "Page", false)) Application.OpenURL(m.PageUrl);
        }

        /// <summary>Downloads Nomad Mod Downloader from GitHub and opens it.</summary>
        private void GetDownloader()
        {
            if (AppFetcher.Busy) return;
            Toast("Getting Nomad Mod Downloader from GitHub…", true);
            AppFetcher.Start(_host, (ok, msg) => Toast(ok ? msg : msg + " You can also get it from GitHub: " + AppFetcher.ReleasesPage, ok));
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

            var mods = _cat.Mods;
            // Grouped by author; mods whose author isn't known go last.
            var groups = mods.GroupBy(m => m.Author.Length > 0 ? m.Author : ModCatalog.UnknownAuthor)
                .OrderBy(g => g.Key == ModCatalog.UnknownAuthor ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            const float rowH = 76, gap = 8;
            var contentH = mods.Count * (rowH + gap) * _s + groups.Count * 44 * _s + 10 * _s;
            var view = new Rect(r.x, r.y + 74 * _s, r.width, r.height - 74 * _s);
            var inner = BeginScroll(view, contentH);
            var y = inner.y;
            if (mods.Count == 0) GUI.Label(new Rect(0, 0, inner.width, 40 * _s), "No mods yet. Find some under Browse mods.", Tint(_body, CMuted));
            foreach (var g in groups)
            {
                Section(ref y, inner.width, g.Key + "  (" + g.Count() + ")");
                foreach (var m in g) { ModRow(new Rect(0, y, inner.width, rowH * _s), m); y += (rowH + gap) * _s; }
            }
            EndScroll();
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
            var line2 = new List<string>();
            line2.Add(m.Category);
            if (m.UpdateAvailable && !m.UpdateStaged) line2.Add("v" + m.NexusVersion.TrimStart('v') + " on Nexus");
            if (m.UpdateStaged) line2.Add("update applies on restart");
            if (m.Note.Length > 0 && m.State != ModState.Running && m.State != ModState.Settling) line2.Add(m.Note);
            var statusW = _smallBold.CalcSize(new GUIContent(label ?? "")).x;
            if (label != null) GUI.Label(new Rect(r.x + 20 * _s, r.y + 42 * _s, statusW + 4, 24 * _s), label, Tint(_smallBold, color));
            if (line2.Count > 0)
                GUI.Label(new Rect(r.x + 20 * _s + (label != null ? statusW + 12 * _s : 0), r.y + 42 * _s, r.width * 0.55f, 24 * _s),
                    (label != null ? "·  " : "") + string.Join("  ·  ", line2.ToArray()), Tint(_small, CMuted));

            var bx = r.xMax - 14 * _s;
            var by = r.y + (r.height - 40 * _s) / 2;
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
            if (m.NexusId > 0 && B("Page", false, 84)) Application.OpenURL($"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{m.NexusId}");
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
            GUI.BeginGroup(new Rect(view.x, view.y, view.width - 18 * _s, view.height), GUIContent.none, GUIStyle.none);
            return new Rect(0, -_scrollY, view.width - 18 * _s, contentH);
        }

        private void EndScroll() => GUI.EndGroup();

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
            return GUI.Button(r, GUIContent.none, _invisible);
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
            return GUI.Button(r, GUIContent.none, _invisible);
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
