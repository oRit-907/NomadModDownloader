using System.IO.Pipes;
using NomadModDownloader.Core;
using NomadModDownloader.UI;

namespace NomadModDownloader;

internal sealed partial class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private Manifest _manifest = Manifest.Load();
    private readonly HttpClient _http = NexusApi.CreateHttpClient();
    private readonly NexusApi _api;
    private readonly Translator _translator;
    private LinkLabel _translateLink = null!;
    private bool _showingOriginal;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly string? _startupLink;

    private GameInstall? _game;
    private bool _loggedIn, _lastStatusGood;

    /// <summary>An Installed-tab row for a plugin this app didn't install.</summary>
    private sealed record Untracked(string Rel, bool Disabled);

    // header
    private Label _gameLabel = null!, _bepLabel = null!, _accountLabel = null!;
    private Button _bepButton = null!, _loginButton = null!;

    private Panel _pageHost = null!;

    // browse
    private TextBox _search = null!;
    private ComboBox _sort = null!;
    private ListView _modList = null!;
    private Button _loadMore = null!;
    private PictureBox _picture = null!;
    private Label _modTitle = null!, _modMeta = null!, _modSummary = null!, _fileInfo = null!;
    private ComboBox _files = null!;
    private CheckBox _showOld = null!;
    private List<ModFileInfo> _allFiles = new();
    private Button _installButton = null!, _nexusButton = null!;
    private int _browseOffset, _browseTotal, _browseHidden;

    /// <summary>This app's own Nexus page (Nomad Mod Downloader): it's always "installed", and updates itself.</summary>
    public const int SelfModId = 51;

    /// <summary>BepInEx packs aren't listed: the app checks for BepInEx and installs it itself.</summary>
    private static bool IsLoaderPack(ModInfo m) =>
        m.ModId == 7 || (m.Name.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("Pack", StringComparison.OrdinalIgnoreCase));

    private int _browseRequest;
    private ModInfo? _selectedMod;
    /// <summary>Nexus mods seen while browsing, by simplified name: lets mods added by hand show their author.</summary>
    private readonly Dictionary<string, ModInfo> _knownByName = new();
    private bool _fillingAuthors;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 450 };

    // installed
    private ListView _installedList = null!;

    // built-in Nexus browser (free accounts)
    private NexusBrowser _browser = null!;
    private Button _updateButton = null!, _toggleButton = null!, _removeButton = null!;

    // activity + status
    private TextBox _log = null!;
    private Label _status = null!;
    private ProgressBar _progress = null!;

    // sidebar menu (like the game's main menu)
    private readonly Dictionary<string, Label> _nav = new();
    private Image? _bgImage;
    private Bitmap? _bgCache;
    private PictureBox _logo = null!;
    private DateTime _lastQuickFind;

    private readonly bool _appUpdateChecked;

    public MainForm(string? startupLink, bool appUpdateChecked = false)
    {
        _startupLink = startupLink;
        _appUpdateChecked = appUpdateChecked;
        _api = new NexusApi(_http) { ApiKey = _settings.GetApiKey() };
        _translator = new Translator(_http);

        Text = L.T("Nomad Mod Downloader");
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        MinimumSize = new Size(1100, 700);
        Size = new Size(1320, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUi();
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += async (_, _) => { StartGamepad(); await StartupAsync(); };
        FormClosed += (_, _) => _closing.Cancel();
        Resize += (_, _) => { _bgCache?.Dispose(); _bgCache = null; };
    }

    /// <summary>The menu background, scaled to cover the window, with the game's dark left-to-right shade over it.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_bgCache == null || _bgCache.Size != ClientSize)
        {
            _bgCache?.Dispose();
            _bgCache = new Bitmap(ClientSize.Width, ClientSize.Height);
            using var g = Graphics.FromImage(_bgCache);
            g.Clear(Theme.Bg);
            if (_bgImage != null)
            {
                var scale = Math.Max(ClientSize.Width / (float)_bgImage.Width, ClientSize.Height / (float)_bgImage.Height);
                var w = _bgImage.Width * scale; var h = _bgImage.Height * scale;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_bgImage, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            }
            var rect = new Rectangle(Point.Empty, ClientSize);
            using var shade = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.Black, Color.Black, 0f);
            shade.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
            {
                Colors = new[] { Color.FromArgb(240, 18, 20, 30), Color.FromArgb(160, 18, 20, 30), Color.FromArgb(95, 18, 20, 30), Color.FromArgb(150, 18, 20, 30) },
                Positions = new[] { 0f, 0.3f, 0.65f, 1f }
            };
            g.FillRectangle(shade, rect);
        }
        e.Graphics.DrawImageUnscaled(_bgCache, 0, 0);
    }

    // =====================================================================
    // Layout
    // =====================================================================

    private void BuildUi()
    {
        // ================= left: logo, menu, status =================
        var side = new Panel { Dock = DockStyle.Left, Width = 320, BackColor = Color.Transparent, Padding = new Padding(28, 18, 16, 16) };

        // The game's logo comes from the player's own install (see GameArt); until then, its name in text.
        var logo = _logo = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
            Dock = DockStyle.Top, Height = 118
        };
        logo.Paint += (_, e) =>
        {
            if (logo.Image != null) return;
            using var f = new Font(Theme.Bold.FontFamily, 30f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, "NOMAD DRIVE", f, logo.ClientRectangle, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        var subtitle = new Label
        {
            Text = L.T("MOD DOWNLOADER"), Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Muted, Font = Theme.Bold,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.TopCenter
        };

        var menu = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0)
        };
        foreach (var name in new[] { "Browse mods", "Installed", "Mod settings", "Profiles", "Saves", "Problems", "Check BepInEx", "Activity" })
        {
            var item = new Label
            {
                Text = L.T(name), AutoSize = true, Font = Theme.Nav, ForeColor = Theme.Text, BackColor = Color.Transparent,
                Cursor = Cursors.Hand, Margin = new Padding(0, 1, 0, 3), Tag = name
            };
            item.Click += (_, _) => ShowPage(name);
            item.MouseEnter += (_, _) => { if (_current != name) item.ForeColor = Theme.Accent; };
            item.MouseLeave += (_, _) => { if (_current != name) item.ForeColor = Theme.Text; };
            menu.Controls.Add(item);
            _nav[name] = item;
        }

        // status card: game, BepInEx, Nexus + their buttons
        var statusCard = new GlassPanel { Dock = DockStyle.Bottom, Padding = new Padding(16, 14, 16, 14), Height = 10 };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 3,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _gameLabel = Theme.Label(L.T("Looking for Nomad Drive…"), Theme.Small, Theme.Muted);
        _bepLabel = Theme.Label("—", Theme.Small, Theme.Muted);
        _accountLabel = Theme.Label(L.T("Checking…"), Theme.Small, Theme.Muted);
        Label Key(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Margin = new Padding(0, 3, 12, 3) };
        Label Val(Label l) { l.AutoSize = true; l.MaximumSize = new Size(190, 0); l.Margin = new Padding(0, 3, 0, 3); return l; }
        grid.Controls.Add(Key(L.T("Game")), 0, 0); grid.Controls.Add(Val(_gameLabel), 1, 0);
        grid.Controls.Add(Key("BepInEx"), 0, 1); grid.Controls.Add(Val(_bepLabel), 1, 1);
        grid.Controls.Add(Key("Nexus"), 0, 2); grid.Controls.Add(Val(_accountLabel), 1, 2);

        var gameBtn = Theme.Button(L.T("Change folder…"));
        gameBtn.Click += (_, _) => PickGameFolder();
        _bepButton = Theme.Button(L.T("Install BepInEx"), true);
        _bepButton.Visible = false;
        _bepButton.Click += async (_, _) => await RunExclusive(() => EnsureBepInExAsync(askEvenIfInstalled: true));
        _loginButton = Theme.Button(L.T("Log in"), true);
        _loginButton.Click += async (_, _) => await LoginOrLogoutAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0), WrapContents = true };
        foreach (var b in new[] { _loginButton, _bepButton, gameBtn }) { b.Margin = new Padding(0, 0, 8, 8); buttons.Controls.Add(b); }
        statusCard.Controls.Add(buttons);
        statusCard.Controls.Add(grid);
        void FitCard() => statusCard.Height = statusCard.Padding.Vertical + grid.Height + buttons.Height;
        grid.SizeChanged += (_, _) => FitCard();
        buttons.SizeChanged += (_, _) => FitCard();

        // language, under the card
        var langRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        var langPick = Theme.Combo(170);
        foreach (var lang in L.Languages) langPick.Items.Add(lang.Name);
        langPick.SelectedIndex = Math.Max(0, L.Languages.ToList().FindIndex(x => x.Code == L.Current.Code));
        langPick.SelectedIndexChanged += (_, _) => ChangeLanguage(L.Languages[langPick.SelectedIndex], langPick);
        langRow.Controls.Add(new Label { Text = "🌐 " + L.T("Language"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Margin = new Padding(0, 8, 8, 0) });
        langRow.Controls.Add(langPick);

        var play = BuildPlayArea();
        side.Controls.Add(menu);
        side.Controls.Add(play);
        side.Controls.Add(subtitle);
        side.Controls.Add(logo);
        side.Controls.Add(statusCard);
        side.Controls.Add(langRow);

        // Short windows: shrink the menu text, then the logo, so the menu never runs into the status card.
        var fittingSide = false;
        void FitSidebar()
        {
            if (fittingSide || side.Height <= 0) return;
            fittingSide = true;
            try
            {
                int Need() => logo.Height + subtitle.Height + play.GetPreferredSize(new Size(side.Width, 0)).Height + menu.GetPreferredSize(new Size(side.Width, 0)).Height
                              + statusCard.Height + langRow.GetPreferredSize(new Size(side.Width, 0)).Height + side.Padding.Vertical + 6;
                var steps = new (Font Nav, int Logo, Padding Item)[]
                {
                    (Theme.Nav, 118, new Padding(0, 1, 0, 3)),
                    (Theme.H2, 118, new Padding(0, 1, 0, 2)),
                    (Theme.H2, 80, new Padding(0, 0, 0, 1)),
                    (Theme.H2, 64, new Padding(0, 0, 0, 1)),
                    (Theme.Bold, 64, new Padding(0, 0, 0, 1)),
                };
                foreach (var (nav, logoH, pad) in steps)
                {
                    foreach (var item in _nav.Values) { item.Font = nav; item.Margin = pad; }
                    logo.Height = logoH;
                    if (Need() <= side.Height) break;
                }
            }
            finally { fittingSide = false; }
        }
        side.SizeChanged += (_, _) => FitSidebar();
        statusCard.SizeChanged += (_, _) => FitSidebar();

        // ================= right: pages + status line =================
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(4, 22, 22, 8) };
        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        var statusLine = new Panel { Dock = DockStyle.Bottom, Height = Theme.Body.Height + 14, BackColor = Color.Transparent, Padding = new Padding(4, 6, 0, 0) };
        _status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Text = L.T("Ready"), AutoEllipsis = true, BackColor = Color.Transparent, Font = Theme.Small };
        _progress = new ProgressBar { Dock = DockStyle.Right, Width = 220, Visible = false, Style = ProgressBarStyle.Continuous };
        statusLine.Controls.Add(_status);
        statusLine.Controls.Add(_progress);

        AddPage("Browse mods", BuildBrowsePage());
        _browser = new NexusBrowser();
        _browser.LinkCaught += async link => await HandleIncomingAsync(link, switchPage: false);
        _browser.LoginChanged += async () =>
        {
            await RefreshLoginAsync();
            if (_loggedIn && _current == "Nexus" && _browser.CameFromLogin) { _browser.CameFromLogin = false; ShowPage(_returnPage); } // logged in: back to the mods
        };
        _browser.BackRequested += () => ShowPage(_returnPage);
        _browser.DownloadCaught += async (path, page) =>
        {
            await RunExclusive(() => InstallFromBrowserDownloadAsync(path, page));
            _browser.ShowNotice(_status.Text, _lastStatusGood ? Theme.Good : Theme.Text);
            if (_lastStatusGood && _queue.Count > 0) { await RunExclusive(async () => await NextInQueueAsync()); return; } // next mod in the list
            if (_lastStatusGood) { _queueTotal = 0; ShowPage(_returnPage); } // installed: back to where you came from
        };
        // The Nexus page has no menu entry: it opens when you press Install or Log in, then goes away again.
        var nexusPanel = new GlassPanel { Padding = new Padding(14) };
        _browser.Dock = DockStyle.Fill;
        nexusPanel.Controls.Add(_browser);
        AddPage("Nexus", nexusPanel);
        AddPage("Installed", BuildInstalledPage());
        AddPage("Mod settings", BuildSettingsPage());
        AddPage("Profiles", BuildProfilesPage());
        AddPage("Saves", BuildSavesPage());
        AddPage("Problems", BuildProblemsPage());
        AddPage("Check BepInEx", BuildCheckerPage());
        AddPage("Activity", BuildActivityPage());

        main.Controls.Add(_pageHost);
        main.Controls.Add(BuildBanner());
        main.Controls.Add(statusLine);

        Controls.Add(main);
        Controls.Add(side);
        ShowPage("Browse mods");
    }

    private readonly Dictionary<string, Control> _pageControls = new();
    private string _current = "", _returnPage = "Browse mods";

    private void AddPage(string name, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pageHost.Controls.Add(page);
        _pageControls[name] = page;
    }

    private void ShowPage(string name)
    {
        if (name != "Nexus" && _pageControls.ContainsKey(name)) _returnPage = name;
        _current = name;
        foreach (var (key, page) in _pageControls) page.Visible = key == name;
        foreach (var (key, item) in _nav)
        {
            var on = key == name;
            item.ForeColor = on ? Theme.Accent : Theme.Text;
            item.Text = NavText(key);
        }
        if (name == "Installed") RefreshInstalled();
        if (name == "Mod settings") RefreshSettingsList();
        if (name == "Profiles") RefreshProfiles();
        if (name == "Saves") RefreshSaves();
        if (name == "Problems") RefreshProblems();
        if (name == "Check BepInEx") _ = RefreshChecksAsync(lookOnline: false);
        if (name == "Nexus") _ = _browser.OpenedAsync();
    }

    /// <summary>A search box with the game's rounded field look.</summary>
    private static Control Field(TextBox box, int width)
    {
        var frame = new GlassPanel { Fill = Theme.Field, Border = Theme.Edge, Radius = 11, Width = width, Height = box.PreferredHeight + 16, Padding = new Padding(12, 8, 12, 6), Margin = new Padding(0, 0, 10, 0) };
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Field;
        box.ForeColor = Theme.Text;
        box.Dock = DockStyle.Fill;
        frame.Controls.Add(box);
        return frame;
    }

    private Control BuildBrowsePage()
    {
        var page = new Panel { BackColor = Color.Transparent };

        // ---- right: the mod card ----
        var card = new GlassPanel { Dock = DockStyle.Right, Width = 400, Padding = new Padding(18) };
        page.SizeChanged += (_, _) => card.Width = Math.Clamp(page.Width * 36 / 100, 330, 420); // narrower windows: a narrower card
        var stack = new ScrollStack { Dock = DockStyle.Fill }; // the Browse card scrolls too
        const int W = 360;
        _picture = new PictureBox { Size = new Size(W, 200), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Field, Margin = new Padding(0, 0, 0, 14) };
        _picture.Resize += (_, _) =>
        {
            using var path = Theme.Rounded(new RectangleF(0, 0, _picture.Width, _picture.Height), 12);
            _picture.Region = new Region(path); // rounded corners like the game's panels
        };
        _modTitle = new Label { Text = L.T("Pick a mod"), Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, MaximumSize = new Size(W, 0), BackColor = Color.Transparent };
        _modMeta = new Label { Text = "", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 2, 0, 10), BackColor = Color.Transparent };
        _reqLabel = new Label { Text = "", ForeColor = Theme.Accent, Font = Theme.Small, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 10), BackColor = Color.Transparent, Visible = false };
        _modSummary = new Label { Text = L.T("Mods from the Nomad Drive section of Nexus Mods show up on the left."), ForeColor = Theme.Text, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 4), BackColor = Color.Transparent };
        var fileLabel = Theme.Label(L.T("Version"), Theme.Small, Theme.Muted);
        fileLabel.Margin = new Padding(0, 12, 0, 4);
        _files = Theme.Combo(W);
        _files.Margin = new Padding(0, 0, 0, 4);
        _files.SelectedIndexChanged += (_, _) =>
        {
            _fileInfo.Text = (_files.SelectedItem as ModFileInfo) is { } f
                ? L.F("Uploaded {0}", Fmt.Date(f.Date.ToLocalTime())) + (string.IsNullOrWhiteSpace(f.Description) ? "" : " — " + System.Net.WebUtility.HtmlDecode(StripTags(f.Description)))
                  + (f.Changelog.Count > 0 ? "\n" + Changelog(f) : "")
                : "";
            if (_byHandNote) _fileInfo.Text = ByHandNote + (_fileInfo.Text.Length > 0 ? "\n" + _fileInfo.Text : "");
            if (_selectedMod?.ModId == SelfModId) ShowSelfPageState();
        };
        _fileInfo = new Label { ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(W, 160), Margin = new Padding(0, 0, 0, 14), Font = Theme.Small, BackColor = Color.Transparent };
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false };
        _installButton = Theme.Button(L.T("Install"), true);
        _installButton.Enabled = false;
        _installButton.Click += async (_, _) => await InstallSelectedAsync();
        _nexusButton = Theme.Button(L.T("Open on Nexus"));
        _nexusButton.Enabled = false;
        _nexusButton.Click += async (_, _) =>
        {
            if (_selectedMod == null) return;
            if (!_settings.UseBuiltInBrowser || !await OpenInBuiltInBrowserAsync(_selectedMod.PageUrl)) Web.Open(_selectedMod.PageUrl);
        };
        _endorseButton = Theme.Button("👍 " + L.T("Endorse"));
        _endorseButton.Enabled = false;
        _endorseButton.Click += async (_, _) => await EndorseSelectedAsync();
        foreach (var b in new[] { _installButton, _nexusButton }) b.Margin = new Padding(0, 0, 8, 0);
        actions.Controls.AddRange(new Control[] { _installButton, _nexusButton, _endorseButton });
        _showOld = new CheckBox { Text = L.T("Show older versions"), AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = Theme.Small, Margin = new Padding(0, 0, 0, 4), Cursor = Cursors.Hand };
        _showOld.CheckedChanged += (_, _) => PopulateFiles();
        _translateLink = new LinkLabel { AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4), Visible = false };
        _translateLink.LinkClicked += async (_, _) => { _showingOriginal = !_showingOriginal; if (_selectedMod != null) await ShowSummaryAsync(_selectedMod); };
        stack.Controls.AddRange(new Control[] { _picture, _modTitle, _modMeta, _reqLabel, _modSummary, _translateLink, fileLabel, _files, _showOld, _fileInfo, actions });
        card.Controls.Add(stack);

        // ---- left: search + list ----
        var listPanel = new GlassPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 12) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 10) };
        _search = new TextBox { PlaceholderText = L.T("Search Nomad Drive mods"), Font = Theme.Body };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await LoadModsAsync(reset: true); };
        _sort = Theme.Combo(170);
        _sort.Margin = new Padding(0, 4, 10, 0);
        _sort.Items.AddRange(new object[] { L.T("Recently updated"), L.T("Most downloaded"), L.T("Most endorsed"), L.T("Newest") });
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += async (_, _) => await LoadModsAsync(reset: true);
        var refresh = Theme.Button(L.T("Refresh"));
        refresh.Margin = new Padding(0, 2, 0, 0);
        refresh.Click += async (_, _) => await LoadModsAsync(reset: true);
        toolbar.Controls.AddRange(new Control[] { Field(_search, 230), _sort, refresh });

        _modList = Theme.List();
        _modList.Columns.Add(L.T("Mod"), 210);
        _modList.Columns.Add(L.T("Author"), 110);
        _modList.Columns.Add(L.T("Version"), 85);
        _modList.Columns.Add(L.T("Downloads"), 85);
        _modList.Columns.Add(L.T("Endorsed"), 80).Tag = "optional";
        _modList.Columns.Add(L.T("Updated"), 90);
        _modList.SelectedIndexChanged += async (_, _) => await ShowModAsync();
        _modList.DoubleClick += async (_, _) => await InstallSelectedAsync();

        _loadMore = Theme.Button(L.T("Load more"));
        _loadMore.Visible = false;
        _loadMore.Click += async (_, _) => await LoadModsAsync(reset: false);
        var more = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        more.Controls.Add(_loadMore);

        listPanel.Controls.Add(_modList);
        listPanel.Controls.Add(more);
        listPanel.Controls.Add(toolbar);

        page.Controls.Add(listPanel);
        page.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 16, BackColor = Color.Transparent });
        page.Controls.Add(card);
        return page;
    }

    private Control BuildInstalledPage()
    {
        var page = new GlassPanel { Padding = new Padding(16, 16, 16, 12) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = true, Padding = new Padding(0, 0, 0, 10) };
        var check = Theme.Button(L.T("Check for updates"), true);
        check.Click += async (_, _) => await RunExclusive(() => CheckUpdatesAsync());
        _updateAllButton = Theme.Button(L.T("Update all"));
        _updateAllButton.Click += async (_, _) => await UpdateAllAsync();
        _updateButton = Theme.Button(L.T("Update"));
        _updateButton.Click += async (_, _) => await UpdateSelectedAsync();
        _toggleButton = Theme.Button(L.T("Disable"));
        _toggleButton.Click += (_, _) => ToggleSelected();
        _removeButton = Theme.Button(L.T("Uninstall"));
        _removeButton.Click += async (_, _) => await RunExclusive(UninstallSelectedAsync);
        var fromFile = Theme.Button(L.T("Install from file…"));
        fromFile.Click += async (_, _) => await RunExclusive(InstallFromFileAsync);
        var open = Theme.Button(L.T("Open plugins folder"));
        open.Click += (_, _) => { if (_game != null) { Directory.CreateDirectory(_game.PluginsDir); Web.Open(_game.PluginsDir); } };
        foreach (var b in new[] { check, _updateAllButton, _updateButton, _toggleButton, _removeButton, fromFile, open }) { b.Margin = new Padding(0, 0, 8, 6); toolbar.Controls.Add(b); }

        _installedList = Theme.List();
        _installedList.Columns.Add(L.T("Mod"), 260);
        _installedList.Columns.Add(L.T("Version"), 90);
        _installedList.Columns.Add(L.T("Status"), 160);
        _installedList.Columns.Add(L.T("Last game start"), 210);
        _installedList.Columns.Add(L.T("Installed to"), 260);
        _installedList.Columns.Add(L.T("Installed"), 110).Tag = "optional";
        _installedList.SelectedIndexChanged += (_, _) => { UpdateInstalledButtons(); ShowLoadDetail(); };

        var hint = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.Small.Height * 2 + 12, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.BottomLeft,
            Text = L.T("Disabling renames a mod's DLLs to .dll.disabled so BepInEx skips them. Uninstalling puts back any files the mod replaced. Grey rows were added outside this app.")
        };
        page.Controls.Add(_installedList);
        page.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Color.Transparent });
        page.Controls.Add(BuildLoadPanel());
        page.Controls.Add(hint);
        page.Controls.Add(toolbar);
        UpdateInstalledButtons();
        return page;
    }

    private Control BuildActivityPage()
    {
        var page = new GlassPanel { Padding = new Padding(18) };
        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Panel, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None, Font = Theme.Mono, WordWrap = true, Dock = DockStyle.Fill
        };
        _log.HandleCreated += (_, _) => Theme.DarkScrollbars(_log);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 12) };
        if (!StoreApp.IsPackaged) // the Store keeps its own build up to date
        {
            var check = Theme.Button(L.T("Check for app updates"));
            check.Click += async (_, _) => await RunExclusive(() => CheckForAppUpdateAsync(silent: false));
            bar.Controls.Add(check);
        }
        bar.Controls.Add(new Label { Text = L.F("Version {0}", Updater.Current.ToString(3)), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Margin = new Padding(8, 9, 0, 0) });
        page.Controls.Add(_log);
        page.Controls.Add(bar);
        return page;
    }

    // =====================================================================
    // Startup
    // =====================================================================

    private async Task StartupAsync()
    {
        StartLinkListener();
        EnableDragDrop();
        if (GameLauncher.RestoreIfPending()) Log(L.T("Turned mods back on after the last \"Play without mods\"."));

        _game = GameInstall.TryOpen(_settings.GamePath) ?? await Task.Run(() => GameLocator.Find());
        if (_game != null) UseGame(_game, announce: _settings.GamePath != _game.Root);
        else
        {
            RefreshGame();
            _ = DeepSearchAsync();
        }
        Activated += (_, _) => QuickFindOnFocus();

        RetireApiKeySetup();
        _ = LoadModsAsync(reset: true);
        await RefreshLoginAsync();

        if (_startupLink != null) await HandleIncomingAsync(_startupLink);

        await AutoUpdatesAsync();
        await StartupModUpdateCheckAsync();
        if (_game != null) { RefreshProblems(); _ = RefreshChecksAsync(lookOnline: false); }
    }

    /// <summary>Switches to a game folder: saves it, refreshes the status card and loads the game's menu art.</summary>
    private void UseGame(GameInstall g, bool announce)
    {
        _game = g;
        if (_settings.GamePath != g.Root) { _settings.GamePath = g.Root; _settings.Save(); }
        RefreshGame();
        RefreshInstalled();
        if (announce) Log(L.T("Found Nomad Drive at ") + g.Root);
        CheckGameVersion(g);
        _ = LoadGameArtAsync(g);
    }

    /// <summary>Not in any launcher's records: search the drives in the background.</summary>
    private async Task DeepSearchAsync()
    {
        _gameLabel.Text = L.T("Searching for Nomad Drive…");
        _gameLabel.ForeColor = Theme.Muted;
        var g = await Task.Run(() => GameLocator.DeepSearch(_closing.Token));
        if (IsDisposed || _game != null) return;
        if (g != null) { UseGame(g, announce: true); return; }
        RefreshGame();
        Log(L.T("Couldn't find Nomad Drive automatically. Use \"Change folder…\" to point at the game folder."));
    }

    /// <summary>If the game wasn't found, look again (cheaply) when the window comes back to the front — e.g. after installing it.</summary>
    private async void QuickFindOnFocus()
    {
        if (_game != null && Directory.Exists(_game.Root)) return;
        if (DateTime.UtcNow - _lastQuickFind < TimeSpan.FromSeconds(10)) return;
        _lastQuickFind = DateTime.UtcNow;
        var g = await Task.Run(() => GameLocator.Find());
        if (g != null && !IsDisposed && (_game == null || !Directory.Exists(_game.Root))) UseGame(g, announce: true);
    }

    private async Task LoadGameArtAsync(GameInstall g)
    {
        var (bgs, logoPath) = await Task.Run(() => GameArt.Load(g));
        if (IsDisposed || _game != g) return;
        var bg = bgs.Count > 0 ? GameArt.Open(bgs[Random.Shared.Next(bgs.Count)]) : null; // a random road scene, like the game's menu
        var logo = GameArt.Open(logoPath);
        if (bg != null) { _bgImage?.Dispose(); _bgImage = bg; _bgCache?.Dispose(); _bgCache = null; }
        if (logo != null) { _logo.Image?.Dispose(); _logo.Image = logo; }
        Invalidate(true);
    }

    private void RefreshGame()
    {
        if (_game == null)
        {
            _gameLabel.Text = L.T("Nomad Drive not found");
            _gameLabel.ForeColor = Theme.Bad;
            _bepLabel.Text = "—";
            _bepButton.Visible = false;
            return;
        }
        _gameLabel.Text = _game.LooksLikeNomadDrive ? _game.Root : _game.Root + "   " + L.T("(this doesn't look like Nomad Drive)");
        if (_game.LooksLikeNomadDrive) _game.RegisterWithModManager();
        _gameLabel.ForeColor = _game.LooksLikeNomadDrive ? Theme.Text : Theme.Warn;

        var s = BepInExManager.Check(_game);
        _bepLabel.Text = (s.Version != null ? "v" + s.Version + " — " : "") + s.Message;
        _bepLabel.ForeColor = s.State switch
        {
            BepInExState.Ready => Theme.Good,
            BepInExState.NeedsFirstRun => Theme.Warn,
            BepInExState.Disabled => Theme.Warn,
            _ => Theme.Bad
        };
        _bepButton.Visible = s.State is BepInExState.NotInstalled or BepInExState.Broken or BepInExState.Disabled;
        _bepButton.Text = s.State switch { BepInExState.Broken => L.T("Repair BepInEx"), BepInExState.Disabled => L.T("Turn BepInEx on"), _ => L.T("Install BepInEx") };
    }

    /// <summary>Shows whether you're signed in to Nexus in the app's Nexus tab.</summary>
    private async Task RefreshLoginAsync()
    {
        var state = await _browser.GetLoginStateAsync();
        switch (state)
        {
            case true:
                _loggedIn = true;
                _accountLabel.Text = L.T("Logged in to Nexus — Install opens the file, then click \"Manual download\"");
                _accountLabel.ForeColor = Theme.Good;
                _loginButton.Text = L.T("Log out");
                break;
            case false:
                _loggedIn = false;
                _accountLabel.Text = L.T("Not logged in — log in once to download (it's remembered). Browsing works without it.");
                _accountLabel.ForeColor = Theme.Warn;
                _loginButton.Text = L.T("Log in");
                break;
            default:
                _accountLabel.Text = L.T("Nexus login is unavailable (the WebView2 runtime is missing).");
                _accountLabel.ForeColor = Theme.Bad;
                _loginButton.Enabled = false;
                break;
        }
    }

    private async Task LoginOrLogoutAsync()
    {
        if (_loggedIn)
        {
            if (MessageBox.Show(this, L.T("Log out of Nexus in this app?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await _browser.LogOutAsync();
            Log(L.T("Logged out of Nexus."));
            await RefreshLoginAsync();
            return;
        }
        ShowPage("Nexus");
        await _browser.OpenLoginAsync();
        _browser.ShowNotice(L.T("Log in to Nexus here. You only need to do this once — the app remembers it."), Theme.Accent);
    }

    /// <summary>The app no longer uses an API key or nxm:// links: forget any saved key and give nxm:// back to whatever had it.</summary>
    private void RetireApiKeySetup()
    {
        if (_settings.ProtectedApiKey != null) { _settings.ProtectedApiKey = null; _settings.Save(); Log(L.T("Removed the saved Nexus API key (no longer used).")); }
        _api.ApiKey = null;
        try
        {
            if (ProtocolHandler.IsOurs())
            {
                ProtocolHandler.Unregister(_settings);
                Log(_settings.PreviousNxmExisted ? L.T("Handed \"Mod Manager Download\" links back to your previous mod manager.") : L.T("Stopped handling \"Mod Manager Download\" links."));
            }
        }
        catch { }
    }

    private void ChangeLanguage(L.Language lang, ComboBox picker)
    {
        if (lang.Code == L.Current.Code) return;
        _settings.Language = lang.Code;
        _settings.Save();
        // Ask in the NEW language, so whoever picked it can read the question.
        L.Init(lang.Code);
        var restart = MessageBox.Show(this, L.T("Restart the app now to switch language?"), L.T("Language"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        if (restart) { Application.Restart(); Environment.Exit(0); }
        else SetStatus(L.T("The new language is used next time the app starts."));
    }

    // =====================================================================
    // App updates (GitHub Releases)
    // =====================================================================

    /// <summary>On startup: update the app itself, then BepInEx, without asking.</summary>
    private async Task AutoUpdatesAsync()
    {
        if (!_appUpdateChecked) // the update screen already checked at startup
        {
            await RunExclusive(() => CheckForAppUpdateAsync(silent: true));
            if (IsDisposed || Disposing) return; // restarting into the new version
        }
        if (_settings.AutoUpdateBepInEx) await RunExclusive(AutoUpdateBepInExAsync);
    }

    private async Task AutoUpdateBepInExAsync()
    {
        if (_game == null) return;
        var plan = await BepInExManager.CheckForUpdateAsync(_http, _game, _closing.Token);
        if (plan == null) return;
        var have = BepInExManager.Check(_game).Version;
        if (_game.IsRunning())
        {
            Log(L.F("BepInEx {0} is available (you have {1}). It updates automatically next time the app starts with the game closed.", plan.Version, have ?? "?"));
            return;
        }
        SetStatus(L.F("Updating BepInEx to {0}…", plan.Version));
        var written = await BepInExManager.UpdateAsync(_http, _game, plan, Progress(), _closing.Token);
        HideProgress();
        Log(L.F("Updated BepInEx {0} → {1} ({2} files). Your plugins, settings and doorstop_config.ini were kept.", have ?? "?", plan.Version, written.Count));
        RefreshGame();
        SetStatus(L.F("BepInEx updated to {0}.", plan.Version), good: true);
    }

    /// <param name="silent">At startup: say nothing unless there's an update.</param>
    private async Task CheckForAppUpdateAsync(bool silent)
    {
        if (StoreApp.IsPackaged) return; // updated by the Microsoft Store
        var updater = new Updater(_http) { Token = _settings.GetGitHubToken() };
        if (!silent) SetStatus(L.T("Checking for app updates…"));
        var (result, info, error) = await updater.CheckAsync(_closing.Token);

        if (result == UpdateCheck.Failed)
        {
            Log(L.F("Update check failed: {0}", error ?? ""));
            if (!silent) SetStatus(L.F("Update check failed: {0}", error ?? ""));
            return;
        }
        if (result == UpdateCheck.UpToDate || info == null)
        {
            if (!silent) SetStatus(L.F("You have the latest version ({0}).", Updater.Current.ToString(3)), good: true);
            return;
        }

        // Automatic at startup; otherwise (button pressed, or automatic updates switched off) it asks first.
        if (!(silent && _settings.AutoUpdateApp))
        {
            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n" + (info.Notes.Length > 600 ? info.Notes[..600] + "…" : info.Notes);
            var ask = L.F("Version {0} is available (you have {1}).", info.Version.ToString(3), Updater.Current.ToString(3)) + notes + "\n\n" + L.T("Download and install it now? The app restarts when it's done.");
            if (MessageBox.Show(this, ask, L.T("App updates"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) { SetStatus(L.T("Ready")); return; }
        }

        Log(L.F("Updating the app to version {0}…", info.Version.ToString(3)));
        SetStatus(L.F("Downloading version {0}…", info.Version.ToString(3)));
        var path = await updater.DownloadAsync(info, Progress(), _closing.Token);
        HideProgress();
        Log(L.F("Downloaded version {0}. Restarting…", info.Version.ToString(3)));
        Updater.ApplyAndRestart(path);
        Close();
    }

    // =====================================================================
    // Game folder
    // =====================================================================

    private void PickGameFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = L.T("Select the Nomad Drive game folder (the one with the game's .exe)"), UseDescriptionForTitle = true, InitialDirectory = _game?.Root ?? "" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var g = GameLocator.OpenNear(dlg.SelectedPath);
        if (g == null)
        {
            MessageBox.Show(this, L.T("That folder doesn't contain a Unity game (.exe with a matching _Data folder)."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UseGame(g, announce: false);
        Log(L.T("Game folder set to ") + g.Root);
    }

    // =====================================================================
    // Single-instance link listener
    // =====================================================================

    private void StartLinkListener()
    {
        _ = Task.Run(async () =>
        {
            while (!_closing.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_closing.Token);
                    using var r = new StreamReader(server);
                    var line = await r.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                        BeginInvoke(async () => await HandleIncomingAsync(line));
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(500); }
            }
        });
    }

    /// <param name="switchPage">true for links arriving from outside (system browser); false when clicked in the Nexus tab.</param>
    private async Task HandleIncomingAsync(string line, bool switchPage = true)
    {
        if (switchPage) BringToFront2();
        if (line == "show") return;
        if (line.Equals(GameLink.CheckLink, StringComparison.OrdinalIgnoreCase)) { ShowPage("Check BepInEx"); return; }
        if (GameLink.TryParse(line, out var gameItems))
        {
            if (gameItems.Count == 1) await RunExclusive(() => InstallLatestFromGameAsync(gameItems[0].ModId, gameItems[0].FileId));
            else
            {
                Log(L.F("The game asked to install {0} mods.", gameItems.Count));
                await StartQueueAsync(gameItems.Select(i => (i.ModId, i.FileId, L.F("Mod {0}", i.ModId))));
            }
            return;
        }
        if (!NxmLink.TryParse(line, out var link) || link == null) { Log(L.T("Ignored an unrecognised link.")); return; }
        if (!link.IsNomadDrive)
        {
            if (!ProtocolHandler.ForwardToPrevious(_settings, line)) Log(L.F("Ignored a link for {0} (this app is Nomad Drive only).", link.GameDomain));
            return;
        }
        // "Mod Manager Download" needs an API key, which the app no longer uses.
        ShowPage("Nexus");
        _browser.ShowNotice(L.T("\"Mod Manager Download\" isn't used by this app — click \"Manual download\" instead (then \"Slow download\"). It installs automatically."), Theme.Warn);
        await Task.CompletedTask;
    }

    /// <summary>"Install" / "Update" pressed in the game's Mods menu: open the mod's main current file, ready to click.</summary>
    private async Task InstallLatestFromGameAsync(int modId, int? fileId = null)
    {
        if (RequireGame() is not { } game) return;
        SetStatus(L.T("Getting mod details…"));
        var files = await _api.GetModFilesAsync(modId, _closing.Token);
        if (modId == SelfModId)
        {
            // From the game, only the in-game menu's file makes sense: the build for this BepInEx.
            files = files.Where(IsInGameMenuFile).ToList();
            var major = BepInExManager.MajorOf(game) is 5 ? 5 : 6;
            var match = files.Where(f => f.IsCurrent && GameInstall.MenuFileMajor(f.Name) == major).ToList();
            if (match.Count > 0 && fileId == null) files = match;
        }
        var file = (fileId is int fid ? files.FirstOrDefault(f => f.FileId == fid) : null)
                   ?? files.FirstOrDefault(f => f.IsCurrent && f.Primary) ?? files.FirstOrDefault(f => f.IsCurrent) ?? files.FirstOrDefault();
        if (file == null) { SetStatus(L.T("That mod has no files to download.")); return; }
        Log(L.F("The game asked to install mod {0}.", modId));
        await InstallFromNexusAsync(modId, file.FileId);
    }

    private async Task<bool> OpenInBuiltInBrowserAsync(string url)
    {
        ShowPage("Nexus");
        if (await _browser.NavigateAsync(url)) return true;
        ShowPage("Browse mods");
        return false;
    }

    private void BringToFront2()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true; Activate(); TopMost = false;
    }

    // =====================================================================
    // Browse
    // =====================================================================

    private async Task LoadModsAsync(bool reset)
    {
        var req = ++_browseRequest;
        if (reset) _browseOffset = 0;
        SetStatus(L.T("Loading mods from Nexus…"));
        try
        {
            var (mods, total) = await _api.SearchModsAsync(_search.Text, (ModSort)_sort.SelectedIndex, _browseOffset, 25, _closing.Token);
            if (req != _browseRequest) return; // a newer search replaced this one
            if (reset) _browseHidden = 0;
            _browseTotal = total;
            _modList.BeginUpdate();
            if (reset) _modList.Items.Clear();
            var installed = _manifest.Mods.Select(m => m.ModId).ToHashSet();
            var byHand = HandInstalledKeys();
            foreach (var m in mods)
            {
                var key = NameKey(m.Name);
                if (key.Length >= 4) _knownByName.TryAdd(key, m);
                // Not mods the app can install: BepInEx itself, mods nobody has downloaded, and programs (.exe).
                if (IsLoaderPack(m) || m.Downloads == 0 || m.ExeOnly) { _browseHidden++; continue; }
                var have = installed.Contains(m.ModId) || m.ModId == SelfModId || byHand.Contains(key);
                var item = new ListViewItem(have ? "✓ " + m.Name : m.Name) { Tag = m };
                item.SubItems.Add(m.Author);
                item.SubItems.Add(m.Version);
                item.SubItems.Add(Fmt.Count(m.Downloads));
                item.SubItems.Add(Fmt.Count(m.Endorsements));
                item.SubItems.Add((m.UpdatedAt is { } up ? Fmt.ShortDate(up.ToLocalTime()) : null) ?? "");
                _modList.Items.Add(item);
            }
            _modList.EndUpdate();
            _browseOffset += mods.Count;
            _loadMore.Visible = _browseOffset < _browseTotal;
            var shown = Math.Max(0, _browseTotal - _browseHidden);
            var count = shown == 1 ? L.T("1 Nomad Drive mod") : L.F("{0} Nomad Drive mods", shown);
            SetStatus(string.IsNullOrWhiteSpace(_search.Text) ? count : L.F("{0} matching \"{1}\"", count, _search.Text.Trim()));
            if (reset && _modList.Items.Count > 0 && _modList.SelectedItems.Count == 0) _modList.Items[0].Selected = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (req == _browseRequest) SetStatus(L.T("Couldn't load mods: ") + ex.Message);
            Log(L.T("Browse failed: ") + ex.Message);
        }
    }

    /// <summary>Shows one mod's page in Browse (adding it to the list if the current search doesn't have it).</summary>
    private async Task OpenModPageAsync(int modId)
    {
        ShowPage("Browse mods");
        var item = _modList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is ModInfo m && m.ModId == modId);
        if (item == null)
        {
            ModInfo? mod = null;
            try { mod = await _api.GetModAsync(modId, _closing.Token); } catch { }
            if (mod == null) { SetStatus(L.T("Couldn't open that mod's page.")); return; }
            item = new ListViewItem(mod.Name) { Tag = mod };
            for (var i = 0; i < _modList.Columns.Count - 1; i++) item.SubItems.Add("");
            _modList.Items.Insert(0, item);
        }
        _modList.SelectedItems.Clear();
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
    }

    private async Task ShowModAsync()
    {
        if (_modList.SelectedItems.Count == 0 || _modList.SelectedItems[0].Tag is not ModInfo mod) return;
        _selectedMod = mod;
        _modTitle.Text = mod.Name;
        _modMeta.Text = $"by {mod.Author}" + (string.IsNullOrEmpty(mod.Category) ? "" : "  ·  " + mod.Category) + "  ·  v" + mod.Version + "  ·  " + $"{Fmt.Count(mod.Downloads)} downloads";
        _showingOriginal = false;
        _ = ShowSummaryAsync(mod);
        _nexusButton.Enabled = true;
        _endorseButton.Enabled = true;
        _installButton.Enabled = false;
        _ = ShowRequirementsAsync(mod);
        _files.Items.Clear();
        _allFiles = new();
        _byHandNote = false;
        _showOld.Visible = false;
        _fileInfo.Text = L.T("Loading files…");
        var oldImage = _picture.Image;
        _picture.Image = null;
        oldImage?.Dispose();
        _ = LoadPictureAsync(mod);

        try
        {
            var files = await _api.GetModFilesAsync(mod.ModId, _closing.Token);
            if (_selectedMod != mod) return;
            _allFiles = files;
            PopulateFiles();
            var already = _manifest.Mods.FirstOrDefault(m => m.ModId == mod.ModId);
            var byHand = already == null && HandInstalledKeys().Contains(NameKey(mod.Name));
            _installButton.Text = already != null || byHand ? L.T("Reinstall") : L.T("Install");
            if (byHand && !_byHandNote) { _byHandNote = true; _fileInfo.Text = ByHandNote + (_fileInfo.Text.Length > 0 ? "\n" + _fileInfo.Text : ""); }
            if (mod.ModId == SelfModId) ShowSelfPageState();
        }
        catch (Exception ex) { _fileInfo.Text = L.T("Couldn't load files: ") + ex.Message; }
    }

    /// <summary>Shows the summary in the app language (translated live), with a link to flip to the original.</summary>
    private async Task ShowSummaryAsync(ModInfo mod)
    {
        _modSummary.Text = mod.Summary;
        if (L.IsEnglish || !_settings.TranslateNexus || string.IsNullOrWhiteSpace(mod.Summary)) { _translateLink.Visible = false; return; }
        _translateLink.Visible = true;
        if (_showingOriginal) { _translateLink.Text = L.T("Show translation"); _translateLink.Links.Clear(); _translateLink.Links.Add(0, _translateLink.Text.Length); return; }

        if (!_translator.TryCached(mod.Summary, out var done))
        {
            if (_translator.LimitReached) { _translateLink.Text = L.T("Today's free translations are used up — showing the original."); _translateLink.Links.Clear(); return; }
            _translateLink.Text = L.T("Translating…");
            _translateLink.Links.Clear();
            try { done = await _translator.TranslateAsync(mod.Summary, _closing.Token) ?? ""; }
            catch (OperationCanceledException) { return; }
        }
        if (_selectedMod != mod) return;
        if (string.IsNullOrEmpty(done))
        {
            _translateLink.Text = _translator.LimitReached ? L.T("Today's free translations are used up — showing the original.") : L.T("Couldn't translate this right now — showing the original.");
            _translateLink.Links.Clear();
            return;
        }
        _modSummary.Text = done;
        var text = L.F("Translated by {0} · Show original", Translator.ServiceName);
        _translateLink.Text = text;
        _translateLink.Links.Clear();
        var cut = text.IndexOf('·');
        _translateLink.Links.Add(cut >= 0 ? cut + 1 : 0, cut >= 0 ? text.Length - cut - 1 : text.Length);
    }

    /// <summary>Fills the version picker: current files first, older versions (newest first) when ticked.</summary>
    private void PopulateFiles()
    {
        var keep = (_files.SelectedItem as ModFileInfo)?.FileId;
        _files.Items.Clear();
        var current = _allFiles.Where(f => f.IsCurrent).ToList();
        var old = _allFiles.Where(f => !f.IsCurrent).OrderByDescending(f => f.Date).ToList();
        foreach (var f in current) _files.Items.Add(f);
        if (_showOld.Checked || current.Count == 0) foreach (var f in old) _files.Items.Add(f);
        _showOld.Visible = old.Count > 0 && current.Count > 0;
        _showOld.Text = L.F("Show older versions ({0})", old.Count);

        var idx = keep == null ? -1 : _files.Items.Cast<ModFileInfo>().ToList().FindIndex(f => f.FileId == keep);
        // The in-game Mods menu comes in a BepInEx 5 and a BepInEx 6 build: pick the one for this game's BepInEx.
        if (idx < 0 && _selectedMod?.ModId == SelfModId && _game != null)
        {
            var major = BepInExManager.MajorOf(_game) is 5 ? 5 : 6;
            idx = _files.Items.Cast<ModFileInfo>().ToList().FindIndex(f => IsInGameMenuFile(f) && GameInstall.MenuFileMajor(f.Name) == major);
        }
        if (_files.Items.Count > 0) _files.SelectedIndex = idx >= 0 ? idx : 0;
        else _fileInfo.Text = L.T("This mod has no downloadable files.");
        _installButton.Enabled = _files.Items.Count > 0 && (_selectedMod?.ModId != SelfModId || IsInGameMenuFile(_files.SelectedItem as ModFileInfo));
    }

    /// <summary>
    /// This app's own Nexus page also holds the in-game Mods menu (Nomad Mod Manager). The app's file can't be
    /// installed from here (it updates itself), but the in-game menu's file installs like any mod.
    /// </summary>
    private static bool IsInGameMenuFile(ModFileInfo? f) =>
        f != null && (f.Name.Contains("in-game", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("in game", StringComparison.OrdinalIgnoreCase)
                      || f.Name.Contains("Mod Manager", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Mods menu", StringComparison.OrdinalIgnoreCase));

    private void ShowSelfPageState()
    {
        var f = _files.SelectedItem as ModFileInfo;
        if (IsInGameMenuFile(f))
        {
            var have = _manifest.Mods.FirstOrDefault(m => m.ModId == SelfModId && m.FileId == f!.FileId);
            _installButton.Text = have != null ? L.T("Reinstall") : L.T("Install");
            _installButton.Enabled = true;
            if (_fileInfo.Text.Length == 0) _fileInfo.Text = L.T("The in-game Mods menu: adds a Mods button to Nomad Drive's main menu.");
            return;
        }
        // The app itself: no install, just say which version you have.
        _installButton.Text = "✓ " + L.F("Installed (v{0})", Updater.Current.ToString(3));
        _installButton.Enabled = false;
        _fileInfo.Text = L.T("This is the app you're using. It updates itself when a new version comes out.");
    }

    /// <summary>
    /// Nexus's CDN serves mod pictures as WebP (even for ".png" links), which WinForms can't draw,
    /// so they're decoded with Windows' own WebP codec and handed over as a normal bitmap.
    /// </summary>
    private async Task LoadPictureAsync(ModInfo mod)
    {
        foreach (var url in new[] { mod.PictureUrl, mod.ThumbnailUrl }.Where(u => !string.IsNullOrEmpty(u)).Distinct())
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, _closing.Token);
                var copy = await Task.Run(() => DecodeImage(bytes));
                if (copy == null) continue;
                if (_selectedMod != mod) { copy.Dispose(); return; }
                _picture.Image = copy;
                return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* unsupported format or download failed: try the next one */ }
        }
    }

    private static Bitmap? DecodeImage(byte[] bytes)
    {
        // PNG/JPG/GIF/BMP: GDI+ handles these directly.
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms, false, true);
            return new Bitmap(img);
        }
        catch { }
        // WebP: decode with Windows' own image codecs (WIC, via WPF), shrunk to a sensible size, then re-encode as PNG for GDI+.
        try
        {
            using var src = new MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 800;
            bmp.StreamSource = src;
            bmp.EndInit();
            bmp.Freeze();
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var png = new MemoryStream();
            enc.Save(png);
            png.Position = 0;
            using var gdi = Image.FromStream(png);
            return new Bitmap(gdi);
        }
        catch { return null; } // no WebP codec on this PC: the card just shows no picture
    }

    private async Task InstallSelectedAsync()
    {
        if (_selectedMod == null || _files.SelectedItem is not ModFileInfo file) return;
        if (_selectedMod.ModId == SelfModId && !IsInGameMenuFile(file)) return; // the app updates itself
        var mod = _selectedMod;
        await RunExclusive(() => InstallFromNexusAsync(mod.ModId, file.FileId));
    }

    // =====================================================================
    // Install pipeline
    // =====================================================================

    /// <summary>
    /// Opens the mod's file in the Nexus tab. Nexus requires downloads to be started on its site, by you:
    /// click "Manual download" (then "Slow download") and the app catches the file and installs it.
    /// </summary>
    private async Task InstallFromNexusAsync(int modId, int fileId, bool checkRequirements = true)
    {
        var game = RequireGame();
        if (game == null) return;

        var mod = _selectedMod?.ModId == modId ? _selectedMod : await _api.GetModAsync(modId, _closing.Token) ?? new ModInfo { ModId = modId, Name = L.F("Mod {0}", modId) };

        if (checkRequirements)
        {
            _queue.Clear(); _queueTotal = 0; // a new install replaces any unfinished list
            var first = await RequirementsFirstAsync(mod);
            if (first == null) { SetStatus(L.T("Install cancelled.")); return; }
            if (first.Count > 0)
            {
                first.Add((modId, fileId, mod.Name));
                await StartQueueAsync(first);
                return;
            }
        }
        var page = $"{mod.PageUrl}?tab=files&file_id={fileId}";

        _browser.HighlightNextLoad(fileId);
        if (await OpenInBuiltInBrowserAsync(page))
        {
            var loggedIn = await _browser.GetLoginStateAsync() == true;
            var which = _allFiles.FirstOrDefault(f => f.FileId == fileId) is { } fi ? $" ({fi.Name} v{fi.Version})" : "";
            _browser.ShowNotice((loggedIn
                ? L.F("{0}{1}: click the highlighted download button. It installs automatically.", mod.Name, which)
                : L.F("{0}{1}: log in first (Log in button at the top of the app), then click the highlighted download button.", mod.Name, which)) + QueueNote(), Theme.Accent);
            Log(L.F("Opened {0} in the Nexus tab.", mod.Name));
            SetStatus(L.F("Waiting for you to click \"Manual download\" for {0}…", mod.Name));
            return;
        }

        // No built-in browser on this PC: download in your normal browser, then use "Install from file…".
        Web.Open(page);
        MessageBox.Show(this, L.F("{0} opened in your browser.\n\nDownload it with \"Manual download\", then come back and use \"Install from file…\" on the Installed tab to install it.", mod.Name),
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task InstallFromFileAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("Pick a Nomad Drive mod you downloaded from Nexus"),
            Filter = "Mod files (*.zip;*.7z;*.rar;*.dll)|*.zip;*.7z;*.rar;*.dll|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { } home && Directory.Exists(Path.Combine(home, "Downloads")) ? Path.Combine(home, "Downloads") : ""
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await InstallFromBrowserDownloadAsync(dlg.FileName, null, dropped: true);
    }

    /// <summary>A file you downloaded with "Manual download" in the Nexus tab. Works out which mod/file it is, then installs it.</summary>
    private async Task InstallFromBrowserDownloadAsync(string path, string? pageUrl, bool dropped = false)
    {
        var game = RequireGame();
        if (game == null) return;
        if (game.BlocksInstall()) { MessageBox.Show(this, L.T("Nomad Drive is running. Close the game, then press Install again (the file is already downloaded)."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var ctx = NexusDownloadContext.From(pageUrl, Path.GetFileName(path));
        if (ctx.ModId == null)
        {
            if (pageUrl == null)
            {
                // A file you picked or dragged in that isn't named like a Nexus download: install it as a local mod.
                var stem = Path.GetFileNameWithoutExtension(path);
                if (MessageBox.Show(this, L.F("\"{0}\" isn't named like a Nexus download, so the app can't check it for updates.\n\nInstall it anyway?", Path.GetFileName(path)),
                        L.T("Install from file"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await InstallDownloadedFileAsync(game, new ModInfo { ModId = 0, Name = stem }, new ModFileInfo { FileId = 0, Name = stem, Version = "", Date = File.GetLastWriteTimeUtc(path) }, path, deleteAfter: false);
                return;
            }
            Log(L.F("Downloaded {0}, but couldn't tell which Nomad Drive mod it belongs to — not installed.", Path.GetFileName(path)));
            SetStatus(L.T("Couldn't tell which mod that download was for."));
            return;
        }

        SetStatus(L.T("Getting mod details…"));
        var modId = ctx.ModId.Value;
        var mod = await _api.GetModAsync(modId, _closing.Token) ?? new ModInfo { ModId = modId, Name = L.F("Mod {0}", modId) };
        var files = await _api.GetModFilesAsync(modId, _closing.Token);
        var file = ctx.PickFile(files) ?? new ModFileInfo { FileId = 0, Name = Path.GetFileNameWithoutExtension(path), Version = mod.Version, Date = DateTime.UtcNow };
        Log(L.F("Manual download finished: {0} — {1} v{2}", mod.Name, file.Name, file.Version));
        await InstallDownloadedFileAsync(game, mod, file, path, deleteAfter: pageUrl != null && !dropped);
    }

    private async Task InstallDownloadedFileAsync(GameInstall game, ModInfo mod, ModFileInfo file, string path, bool deleteAfter = true)
    {
        var modId = mod.ModId;
        var fileId = file.FileId;

        // Work out where it goes
        var plan = ModInstaller.Analyze(path, mod.Name);
        if (plan.Kind == InstallKind.LoaderPack)
        {
            var s = BepInExManager.Check(game);
            var msg = s.State is BepInExState.Ready or BepInExState.NeedsFirstRun
                ? L.F("\"{0}\" is a BepInEx pack, and BepInEx (v{1}) is already installed.\n\nInstall it over the top anyway?", mod.Name, s.Version)
                : L.F("\"{0}\" is a BepInEx pack. It will be unpacked into the game folder:\n{1}\n\nInstall it?", mod.Name, game.Root);
            if (MessageBox.Show(this, msg, L.T("BepInEx pack"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { SetStatus(L.T("Install cancelled.")); return; }
        }
        else
        {
            if (!await EnsureBepInExAsync(askEvenIfInstalled: false)) { SetStatus(L.T("Install cancelled — the mod needs BepInEx.")); return; }
            if (plan.Kind == InstallKind.Unknown)
            {
                var choice = MessageBox.Show(this,
                    L.F("\"{0}\" has no plugin DLLs, so it's not clear where it belongs.\n\n", file.Name) +
                    L.F("Yes = put it in BepInEx\\plugins\\{0}\n", ModInstaller.ModFolderName(mod.Name)) +
                    L.T("No = unpack it into the game folder\nCancel = don't install"),
                    L.T("Where should this go?"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel) { SetStatus(L.T("Install cancelled.")); return; }
                plan = ModInstaller.Analyze(path, mod.Name, choice == DialogResult.Yes ? InstallKind.PluginFolder : InstallKind.GameRoot);
            }
        }

        BackupSavesIfOn(L.F("Before installing {0}", mod.Name));

        // Replace an older copy of the same file (updates / reinstalls)
        foreach (var old in _manifest.Mods.Where(m => m.ModId == modId && (m.FileId == fileId || m.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            Log(L.F("Removing previous {0} v{1} first.", old.Name, old.Version));
            ModInstaller.Uninstall(old, game, _manifest);
        }

        SetStatus(L.F("Installing {0}…", mod.Name));
        var meta = new InstalledMod
        {
            Id = modId > 0 ? $"{modId}:{fileId}" : "local:" + Fmt.SafeFileName(file.Name), ModId = modId, FileId = fileId, Name = mod.Name, FileName = file.Name, Author = mod.Author ?? "",
            Version = string.IsNullOrEmpty(file.Version) ? mod.Version : file.Version, FileDate = file.Date
        };
        try { ModInstaller.Install(plan, game, _manifest, meta); }
        catch { _manifest = Manifest.Load(); throw; }

        if (deleteAfter && !_settings.KeepDownloads) { try { File.Delete(path); } catch { } }

        Log(meta.Backups.Count > 0
            ? L.F("Installed {0} v{1} → {2} ({3} file(s), {4} replaced file(s) backed up)", mod.Name, meta.Version, plan.TargetDescription, meta.Files.Count, meta.Backups.Count)
            : L.F("Installed {0} v{1} → {2} ({3} file(s))", mod.Name, meta.Version, plan.TargetDescription, meta.Files.Count));
        if (meta.Replaced.Count > 0)
            Log(L.F("  Replaced your hand-installed copy (backed up): {0}", string.Join(", ", meta.Replaced.Select(r => r.Replace('/', '\\')))));
        if (meta.NotReplaced.Count > 0)
            Log(L.F("  Couldn't remove your hand-installed copy, so the mod is there twice (BepInEx runs the newest): {0}. Close the game and install again, or remove it on the Installed tab.", string.Join(", ", meta.NotReplaced.Select(r => r.Replace('/', '\\')))));
        if (plan.SkippedBepInEx) Log(L.T("  This download also includes BepInEx. Those files were left out: the app keeps your BepInEx set up and up to date."));
        if (plan.Skipped.Count > 0) Log(L.T("  Skipped (readmes/extra files): ") + string.Join(", ", plan.Skipped.Take(8)) + (plan.Skipped.Count > 8 ? " …" : ""));
        RefreshGame();
        RefreshInstalled();
        MarkInstalledInBrowse(modId);

        SetStatus(BepInExManager.Check(game).State == BepInExState.NeedsFirstRun
            ? L.F("Installed {0} v{1}. Launch the game once so BepInEx finishes setting up.", mod.Name, meta.Version)
            : game.IsRunning()
                ? L.F("Installed {0} v{1}. Check the Mods menu in the game: it starts there, or after a restart if it has to.", mod.Name, meta.Version)
                : L.F("Installed {0} v{1}.", mod.Name, meta.Version), good: true);
    }

    /// <summary>Makes sure BepInEx is there, asking the user before anything is installed or changed.</summary>
    private async Task<bool> EnsureBepInExAsync(bool askEvenIfInstalled)
    {
        var game = RequireGame();
        if (game == null) return false;
        var s = BepInExManager.Check(game);

        switch (s.State)
        {
            case BepInExState.Ready:
            case BepInExState.NeedsFirstRun:
                Directory.CreateDirectory(game.PluginsDir);
                return true;

            case BepInExState.Il2CppGame:
                MessageBox.Show(this, s.Message + L.T("\n\nInstall BepInEx 6 (IL2CPP) by hand, then come back."), "BepInEx", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;

            case BepInExState.Disabled:
            {
                var r = MessageBox.Show(this, L.T("BepInEx is installed but switched off (enabled = false in doorstop_config.ini), so mods won't load.\n\nTurn it back on?"),
                    L.T("BepInEx is off"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.Yes)
                {
                    var ini = Path.Combine(game.Root, "doorstop_config.ini");
                    File.WriteAllText(ini, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(ini), @"(?im)^(\s*enabled\s*=\s*)false", "${1}true"));
                    Log(L.T("Turned BepInEx back on in doorstop_config.ini."));
                    RefreshGame();
                }
                return true;
            }
        }

        // Not installed or broken: show exactly what will happen and wait for a yes.
        if (game.IsRunning()) { MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }

        var plan = await BepInExManager.PlanAsync(_http, game, _closing.Token);

        var why = s.State == BepInExState.Broken ? L.F("BepInEx looks broken: {0}", s.Message) : L.T("Nomad Drive mods need BepInEx, and it isn't installed yet.");
        var text = L.F("{0}\n\nInstall BepInEx {1} ({2}) now?\n\n", why, plan.Version, plan.Architecture) +
                   L.F("From:  {0}", plan.Url.Host + plan.Url.AbsolutePath) + (plan.SizeBytes > 0 ? $"  ({Fmt.Size(plan.SizeBytes)})" : "") + "\n" +
                   L.F("Into:  {0}\n\n", game.Root) +
                   L.T("It adds winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt and the BepInEx folder, plus support for mods built for BepInEx 5. Nothing outside the game folder is touched.\n\n") +
                   L.T("Afterwards, launch the game once so BepInEx finishes setting up.");
        if (MessageBox.Show(this, text, L.T("Install BepInEx?"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Log(L.T("BepInEx install declined."));
            return false;
        }

        SetStatus(L.F("Downloading BepInEx {0}…", plan.Version));
        var written = await BepInExManager.InstallAsync(_http, game, plan, Progress(), _closing.Token);
        HideProgress();
        Log(L.F("Installed BepInEx {0} ({1} files) into {2}", plan.Version, written.Count, game.Root));
        RefreshGame();
        SetStatus(L.F("BepInEx {0} installed.", plan.Version));
        return true;
    }

    // =====================================================================
    // Installed tab
    // =====================================================================

    private void RefreshInstalled()
    {
        if (_installedList == null) return;
        _manifest = Manifest.Load();
        if (_game != null && ModInstaller.Reconcile(_manifest, _game)) _manifest.Save();
        PrepareLoadChecks();
        _installedList.BeginUpdate();
        _installedList.Items.Clear();

        // Every row with the author it's grouped under.
        var rows = new List<(string Author, ListViewItem Item)>();
        foreach (var m in _manifest.Mods.OrderBy(m => m.Name))
        {
            var status = m.Update != null ? L.F("Update: v{0}", m.Update.Version) : m.Disabled ? L.T("Disabled") : L.T("Enabled");
            var item = new ListViewItem(m.Name) { Tag = m };
            item.SubItems.Add(m.Version);
            var st = item.SubItems.Add(status);
            st.ForeColor = m.Update != null ? Theme.Accent : m.Disabled ? Theme.Warn : Theme.Good;
            AddLoadCell(item, m.Files, m.Disabled);
            item.SubItems.Add(m.Location);
            item.SubItems.Add(Fmt.Date(m.InstalledAt));
            var author = m.Author.Length > 0 ? m.Author
                : _knownByName.Values.FirstOrDefault(k => k.ModId == m.ModId && m.ModId > 0)?.Author ?? "";
            rows.Add((author, item));
        }
        if (_game != null)
        {
            foreach (var (rel, disabled) in ModInstaller.FindUntracked(_game, _manifest))
            {
                if (rel.EndsWith("/" + BepInExCompat.PluginFolder, StringComparison.OrdinalIgnoreCase)) continue; // part of BepInEx, not a mod
                var folderName = Path.GetFileName(rel);
                var item = new ListViewItem(folderName) { Tag = new Untracked(rel, disabled), ForeColor = Theme.Muted };
                item.SubItems.Add("");
                var st = item.SubItems.Add(disabled ? L.T("Disabled (not from this app)") : L.T("Not from this app"));
                st.ForeColor = disabled ? Theme.Warn : Theme.Muted;
                AddLoadCell(item, new[] { rel }, disabled);
                item.SubItems.Add(rel.Replace('/', '\\'));
                item.SubItems.Add("");
                // Match by folder name, or by the plugin names inside it (e.g. folder "NomadHUD" holds "Retro RV HUD").
                var names = new List<string> { folderName, Path.GetFileNameWithoutExtension(folderName) };
                names.AddRange(UntrackedPluginNames(rel));
                var author = names.Select(n => _knownByName.TryGetValue(NameKey(n), out var k) ? k.Author : "").FirstOrDefault(a => a.Length > 0) ?? "";
                rows.Add((author, item));
            }
        }

        var unknown = L.T("Unknown author");
        foreach (var g in rows.GroupBy(r => r.Author.Length > 0 ? r.Author : unknown)
                              .OrderBy(g => g.Key == unknown ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            _installedList.Items.Add(new ListViewItem($"{g.Key}  ({g.Count()})") { Tag = new ListHeader($"{g.Key}  ({g.Count()})") });
            foreach (var r in g) _installedList.Items.Add(r.Item);
        }
        _installedList.EndUpdate();
        UpdateInstalledButtons();
        ShowLoadDetail();
        var notStarted = _loadDiag.Values.Count(d => d.State == LoadState.DidntStart);
        SetBadge("Installed", notStarted > 0 ? L.F("{0} not starting", notStarted) : _badges.TryGetValue("Installed", out var b) && b.Contains("update") ? b : null);
        _ = FillAuthorsAsync();
    }

    private List<string> UntrackedPluginNames(string rel)
    {
        var result = new List<string>();
        if (_game == null) return result;
        try
        {
            var full = ModInstaller.SafeJoin(_game.Root, rel);
            var dlls = Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase))
                : new[] { full, full + ".disabled" }.Where(File.Exists);
            foreach (var d in dlls) result.AddRange(PluginInspector.Read(d).Select(p => p.Name));
        }
        catch { }
        return result;
    }

    private bool _byHandNote;
    private static string ByHandNote => L.T("You already have this mod (added by hand). Installing it here replaces your copy, so it's tracked and updated.");
    private HashSet<string>? _handKeys;
    private DateTime _handKeysAt;

    /// <summary>Mods you added by hand, as name keys (cached briefly: it reads every hand-installed DLL).</summary>
    private HashSet<string> HandInstalledKeys()
    {
        if (_game == null) return new();
        if (_handKeys != null && DateTime.Now - _handKeysAt < TimeSpan.FromSeconds(10)) return _handKeys;
        try { _handKeys = ModInstaller.HandInstalledNameKeys(_game, _manifest); } catch { _handKeys = new(); }
        _handKeysAt = DateTime.Now;
        return _handKeys;
    }

    private static string NameKey(string name) => new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Mods installed before authors were recorded: look their authors up once, then redraw.</summary>
    private async Task FillAuthorsAsync()
    {
        if (_fillingAuthors) return;
        var missing = _manifest.Mods.Where(m => m.ModId > 0 && m.Author.Length == 0).ToList();
        if (missing.Count == 0) return;
        _fillingAuthors = true;
        var changed = false;
        try
        {
            foreach (var m in missing)
            {
                try
                {
                    var info = await _api.GetModAsync(m.ModId, _closing.Token);
                    if (info != null && info.Author.Length > 0) { m.Author = info.Author; changed = true; }
                }
                catch { }
            }
            if (changed) { _manifest.Save(); RefreshInstalled(); }
        }
        finally { _fillingAuthors = false; }
    }

    private void UpdateInstalledButtons()
    {
        if (_updateButton == null) return;
        var tag = _installedList.SelectedItems.Count > 0 ? _installedList.SelectedItems[0].Tag : null;
        _updateButton.Enabled = tag is InstalledMod { Update: not null };
        _removeButton.Enabled = tag is InstalledMod;
        _toggleButton.Enabled = tag is InstalledMod or Untracked;
        _toggleButton.Text = tag switch
        {
            InstalledMod m => m.Disabled ? L.T("Enable") : L.T("Disable"),
            Untracked { Disabled: true } => L.T("Enable"),
            _ => L.T("Disable")
        };
    }

    private void ToggleSelected()
    {
        if (_game == null || _installedList.SelectedItems.Count == 0) return;
        var tag = _installedList.SelectedItems[0].Tag;
        try
        {
            if (tag is InstalledMod m)
            {
                var mod = _manifest.Mods.First(x => x.Id == m.Id);
                ModInstaller.SetDisabled(mod, !mod.Disabled, _game, _manifest);
                Log(mod.Disabled ? L.F("Disabled {0}.", mod.Name) : L.F("Enabled {0}.", mod.Name));
            }
            else if (tag is Untracked u)
            {
                var disable = !u.Disabled;
                ModInstaller.SetUntrackedDisabled(_game, u.Rel, disable);
                Log(disable ? L.F("Disabled {0}.", u.Rel) : L.F("Enabled {0}.", u.Rel));
            }
        }
        catch (Exception ex) { ShowError(L.T("Couldn't change that mod"), ex); }
        RefreshInstalled();
    }

    private async Task UninstallSelectedAsync()
    {
        if (_game == null || _installedList.SelectedItems.Count == 0 || _installedList.SelectedItems[0].Tag is not InstalledMod m) return;
        var mod = _manifest.Mods.First(x => x.Id == m.Id);
        var msg = L.F("Uninstall {0} v{1}?\n\nThis removes the {2} file(s) it installed", mod.Name, mod.Version, mod.Files.Count) +
                  (mod.Backups.Count > 0 ? L.F(" and restores {0} file(s) it had replaced.", mod.Backups.Count) : ".") +
                  L.T("\nIts settings in BepInEx\\config are left alone.");
        if (MessageBox.Show(this, msg, L.T("Uninstall"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ModInstaller.Uninstall(mod, _game, _manifest);
        Log(L.F("Uninstalled {0}.", mod.Name));
        SetStatus(L.F("Uninstalled {0}.", mod.Name));
        RefreshInstalled();
        await Task.CompletedTask;
    }

    private async Task CheckUpdatesAsync(bool quiet = false)
    {
        _manifest = Manifest.Load();
        var tracked = _manifest.Mods.Where(m => m.ModId > 0).ToList();
        if (tracked.Count == 0) { if (!quiet) SetStatus(L.T("Nothing installed from Nexus yet.")); return; }
        var found = 0;
        var updates = new Dictionary<string, ModFileInfo>();
        foreach (var m in tracked)
        {
            if (!quiet) SetStatus(L.F("Checking {0}…", m.Name));
            try
            {
                var files = await _api.GetModFilesAsync(m.ModId, _closing.Token);
                var newer = files.Where(f => f.IsCurrent && f.FileId != m.FileId && f.Date > m.FileDate && f.Name.Equals(m.FileName, StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(f => f.Date).FirstOrDefault();
                var mine = files.FirstOrDefault(f => f.FileId == m.FileId);
                if (newer == null && (mine == null || !mine.IsCurrent))
                    newer = files.Where(f => f.IsCurrent && f.Category == "MAIN" && f.Date > m.FileDate).OrderByDescending(f => f.Primary).ThenByDescending(f => f.Date).FirstOrDefault();
                if (newer != null) { updates[m.Id] = newer; found++; }
            }
            catch (Exception ex) { Log(L.F("Update check failed for {0}: {1}", m.Name, ex.Message)); }
        }
        RefreshInstalled();
        foreach (var mm in _manifest.Mods) if (updates.TryGetValue(mm.Id, out var upd)) mm.Update = upd;
        SetBadge("Installed", found > 0 ? (found == 1 ? L.T("1 update") : L.F("{0} updates", found)) : null);
        foreach (ListViewItem item in _installedList.Items)
        {
            if (item.Tag is InstalledMod im && updates.TryGetValue(im.Id, out var u))
            {
                im.Update = u;
                item.SubItems[2].Text = L.F("Update: v{0}", u.Version);
                item.SubItems[2].ForeColor = Theme.Accent;
            }
        }
        _installedList.Invalidate();
        UpdateInstalledButtons();
        if (!quiet || found > 0) SetStatus(found == 0 ? L.T("Everything is up to date.") : L.F("{0} update(s) available.", found));
        Log(found == 0 ? L.T("Update check: all up to date.") : L.F("Update check: {0} update(s) available.", found));
    }

    private async Task UpdateSelectedAsync()
    {
        if (_installedList.SelectedItems.Count == 0 || _installedList.SelectedItems[0].Tag is not InstalledMod { Update: { } u } m) return;
        var log = Changelog(u);
        if (log.Length > 0 && MessageBox.Show(this, L.F("Update {0} from v{1} to v{2}?", m.Name, m.Version, u.Version) + "\n\n" + log, L.T("Update"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunExclusive(() => InstallFromNexusAsync(m.ModId, u.FileId));
    }

    private void MarkInstalledInBrowse(int modId)
    {
        foreach (ListViewItem item in _modList.Items)
            if (item.Tag is ModInfo mi && mi.ModId == modId && !item.Text.StartsWith("✓ ")) item.Text = "✓ " + item.Text;
        if (_selectedMod?.ModId == modId) _installButton.Text = L.T("Reinstall");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private GameInstall? RequireGame()
    {
        if (_game != null && Directory.Exists(_game.Root)) return _game;
        MessageBox.Show(this, L.T("Point the app at your Nomad Drive folder first (\"Change folder…\")."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        PickGameFolder();
        return _game;
    }

    private async Task RunExclusive(Func<Task> work)
    {
        if (!await _busy.WaitAsync(0))
        {
            SetStatus(L.T("Busy — wait for the current job to finish."));
            return;
        }
        UseWaitCursor = true;
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { HideProgress(); ShowError(L.T("Something went wrong"), ex); }
        finally { UseWaitCursor = false; _busy.Release(); }
    }

    private IProgress<double> Progress()
    {
        _progress.Value = 0;
        _progress.Visible = true;
        return new Progress<double>(p => _progress.Value = Math.Clamp((int)(p * 100), 0, 100));
    }

    private void HideProgress() => _progress.Visible = false;

    private void SetStatus(string text, bool good = false)
    {
        _lastStatusGood = good;
        _status.Text = string.IsNullOrEmpty(text) ? L.T("Ready") : text;
    }

    private void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        _log.AppendText(line + Environment.NewLine);
        try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}"); } catch { }
    }

    private void ShowError(string title, Exception ex)
    {
        Log($"{title}: {ex.Message}");
        SetStatus(ex.Message);
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string StripTags(string html) => System.Text.RegularExpressions.Regex.Replace(html.Replace("<br />", " ").Replace("<br>", " "), "<.*?>", "").Trim();
}
