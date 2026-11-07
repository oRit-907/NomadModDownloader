using System.Diagnostics;
using NomadModDownloader.Core;
using NomadModDownloader.UI;

namespace NomadModDownloader;

/// <summary>
/// Play buttons, the install queue (requirements, Update all, shared lists), the game-update banner,
/// drag-and-drop, and the Mod settings / Profiles / Saves / Problems pages.
/// </summary>
internal sealed partial class MainForm
{
    // =====================================================================
    // Nav badges ("Installed · 2 updates", "Problems · 3")
    // =====================================================================

    private readonly Dictionary<string, string> _badges = new();

    private void SetBadge(string page, string? text)
    {
        if (string.IsNullOrEmpty(text)) _badges.Remove(page); else _badges[page] = text;
        if (_nav.TryGetValue(page, out var item)) item.Text = NavText(page);
    }

    private string NavText(string page) =>
        L.T(page) + (page == "Installed" && _installedCount > 0 ? $" ({_installedCount})" : "") + (_badges.TryGetValue(page, out var b) ? "  · " + b : "");

    // =====================================================================
    // Play
    // =====================================================================

    private const string DiscordUrl = "https://discord.com/invite/nomaddrive";
    private Button _playButton = null!;
    private LinkLabel _playVanilla = null!;
    private bool _watchingGame;

    private Control BuildPlayArea()
    {
        var area = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 14, 0, 0) };
        _playButton = Theme.Button("▶  " + L.T("PLAY"), true);
        _playButton.Font = Theme.H2;
        _playButton.Width = 204;
        _playButton.Height = Theme.H2.Height + 22;
        _playButton.Click += async (_, _) => await PlayAsync(vanilla: false);
        _playVanilla = new LinkLabel { Text = L.T("Play without mods"), AutoSize = true, Font = Theme.Small, LinkColor = Theme.Muted, ActiveLinkColor = Theme.Accent, BackColor = Color.Transparent, Margin = new Padding(2, 6, 0, 0) };
        _playVanilla.LinkClicked += async (_, _) => await PlayAsync(vanilla: true);

        // The game's Discord community: a square chat button beside PLAY.
        var discord = new ChatButton { Size = new Size(_playButton.Height, _playButton.Height), Margin = new Padding(10, 0, 0, 0) };
        discord.Click += (_, _) => Web.Open(DiscordUrl);
        // Same height as PLAY (which can change with display scaling and font size).
        void Match() => discord.Size = new Size(_playButton.Height, _playButton.Height);
        _playButton.SizeChanged += (_, _) => Match();
        Match();
        new ToolTip().SetToolTip(discord, L.T("Nomad Drive Discord"));
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _playButton.Margin = new Padding(0);
        row.Controls.Add(_playButton);
        row.Controls.Add(discord);
        area.Controls.Add(row);
        area.Controls.Add(_playVanilla);
        return area;
    }

    private async Task PlayAsync(bool vanilla)
    {
        var game = RequireGame();
        if (game == null) return;
        if (game.IsRunning()) { SetStatus(L.T("Nomad Drive is already running.")); return; }
        try
        {
            if (vanilla)
            {
                SetStatus(L.T("Starting Nomad Drive without mods (mods are switched back on once it's running)…"));
                Log(L.T("Started Nomad Drive without mods."));
                _ = WatchSessionAsync(game, vanilla: true);
                await GameLauncher.LaunchVanillaAsync(game, _closing.Token);
                RefreshGame();
            }
            else
            {
                if (BepInExManager.Check(game).State is BepInExState.NotInstalled or BepInExState.Broken or BepInExState.Disabled && _manifest.Mods.Count > 0)
                    if (!await EnsureBepInExAsync(askEvenIfInstalled: false)) return;
                GameLauncher.Launch(game);
                SetStatus(L.T("Starting Nomad Drive…"));
                Log(L.T("Started Nomad Drive."));
                _ = WatchSessionAsync(game, vanilla: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(L.T("Couldn't start the game"), ex); }
    }

    /// <summary>Waits for the game to start and close again, then reads its logs for problems.</summary>
    private async Task WatchSessionAsync(GameInstall game, bool vanilla)
    {
        if (_watchingGame) return;
        _watchingGame = true;
        try
        {
            var until = DateTime.UtcNow.AddMinutes(3);
            while (!game.IsRunning() && DateTime.UtcNow < until) await Task.Delay(1500, _closing.Token);
            if (!game.IsRunning()) return;
            _playButton.Enabled = false;
            _playButton.Text = L.T("Game running");
            while (game.IsRunning()) await Task.Delay(3000, _closing.Token);
            if (IsDisposed) return;
            _playButton.Enabled = true;
            _playButton.Text = "▶  " + L.T("PLAY");
            if (vanilla) { SetStatus(L.T("Ready")); return; }
            var count = RefreshProblems();
            SetStatus(count == 0 ? L.T("The game closed. No mod problems found in that session.") : L.F("The game closed. {0} problem(s) found — see Problems.", count), good: count == 0);
        }
        catch (OperationCanceledException) { }
        finally { _watchingGame = false; if (!IsDisposed) { _playButton.Enabled = true; _playButton.Text = "▶  " + L.T("PLAY"); } }
    }

    // =====================================================================
    // Install queue: requirements, Update all, shared lists, missing profile mods
    // =====================================================================

    private readonly Queue<(int ModId, int? FileId, string Name)> _queue = new();
    private int _queueTotal;

    private string QueueNote() => _queueTotal > 1 ? "  " + L.F("({0} of {1})", _queueTotal - _queue.Count, _queueTotal) : "";

    /// <summary>Starts installing a list of mods one after another. Nexus needs a click for each download.</summary>
    private async Task StartQueueAsync(IEnumerable<(int ModId, int? FileId, string Name)> items)
    {
        _queue.Clear();
        foreach (var i in items) _queue.Enqueue(i);
        _queueTotal = _queue.Count;
        if (_queueTotal == 0) return;
        Log(L.F("Installing {0} mod(s) one after another: {1}", _queueTotal, string.Join(", ", _queue.Select(q => q.Name))));
        await NextInQueueAsync();
    }

    /// <returns>true if another mod was opened.</returns>
    private async Task<bool> NextInQueueAsync()
    {
        while (_queue.Count > 0)
        {
            var (modId, fileId, name) = _queue.Dequeue();
            if (modId == SelfModId && (fileId is null or 0)) continue; // the app itself: it updates on its own
            var fid = fileId;
            if (fid is null or 0)
            {
                try
                {
                    var files = await _api.GetModFilesAsync(modId, _closing.Token);
                    fid = files.Where(f => f.IsCurrent).OrderByDescending(f => f.Primary).ThenByDescending(f => f.Category == "MAIN").ThenByDescending(f => f.Date).FirstOrDefault()?.FileId;
                }
                catch (Exception ex) { Log(L.F("Couldn't get the files for {0}: {1}", name, ex.Message)); }
            }
            if (fid is null or 0) { Log(L.F("Skipped {0}: it has no file to download.", name)); continue; }
            await InstallFromNexusAsync(modId, fid.Value, checkRequirements: false);
            return true;
        }
        _queueTotal = 0;
        return false;
    }

    /// <summary>Asks about a mod's missing requirements. Returns the mods to install first, or null to cancel.</summary>
    private async Task<List<(int, int?, string)>?> RequirementsFirstAsync(ModInfo mod)
    {
        List<ModRequirement> reqs;
        try { reqs = await _api.GetRequirementsAsync(mod.ModId, _closing.Token); }
        catch { return new(); } // can't tell: just install the mod
        var installed = _manifest.Mods.Select(m => m.ModId).ToHashSet();
        var missing = reqs.Where(r => !r.IsBepInExPack && !r.External && r.ModId > 0 && !installed.Contains(r.ModId) && r.ModId != mod.ModId && r.ModId != SelfModId).ToList();
        var outside = reqs.Where(r => r.External).ToList();
        foreach (var o in outside) Log(L.F("{0} also needs something from outside Nexus: {1} {2}", mod.Name, o.Name, o.Url));
        var required = missing.Where(r => !r.Optional).ToList();
        var optional = missing.Where(r => r.Optional).ToList();
        if (required.Count == 0 && optional.Count == 0) return new();

        var text = L.F("{0} works with other mods that aren't installed:", mod.Name) + "\n\n";
        if (required.Count > 0) text += L.T("Needs:") + "\n" + string.Join("\n", required.Select(r => "  • " + r.Name + (r.Notes.Length > 0 && r.Notes.Length < 60 ? $" ({r.Notes})" : ""))) + "\n\n";
        if (optional.Count > 0) text += L.T("Optional:") + "\n" + string.Join("\n", optional.Select(r => "  • " + r.Name)) + "\n\n";
        text += required.Count > 0
            ? L.T("Yes = install what it needs first, then this mod\nNo = install only this mod\nCancel = don't install")
            : L.T("Yes = install the optional ones too\nNo = install only this mod\nCancel = don't install");
        var answer = MessageBox.Show(this, text, L.T("Mod requirements"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return null;
        if (answer == DialogResult.No) return new();
        return (required.Count > 0 ? required : optional).Select(r => (r.ModId, (int?)null, r.Name)).ToList();
    }

    // ---- Browse card: requirements line + endorse

    private Label _reqLabel = null!;
    private Button _endorseButton = null!;

    private async Task ShowRequirementsAsync(ModInfo mod)
    {
        _reqLabel.Text = "";
        _reqLabel.Visible = false;
        try
        {
            var reqs = await _api.GetRequirementsAsync(mod.ModId, _closing.Token);
            if (_selectedMod != mod) return;
            var shown = reqs.Where(r => !r.IsBepInExPack).ToList();
            if (shown.Count == 0) return;
            var installed = _manifest.Mods.Select(m => m.ModId).ToHashSet();
            string Item(ModRequirement r) => ((installed.Contains(r.ModId) || r.ModId == SelfModId) ? "✓ " : "") + r.Name;
            var need = shown.Where(r => !r.Optional).ToList();
            var opt = shown.Where(r => r.Optional).ToList();
            var parts = new List<string>();
            if (need.Count > 0) parts.Add(L.T("Needs:") + " " + string.Join(", ", need.Select(Item)));
            if (opt.Count > 0) parts.Add(L.T("Optional:") + " " + string.Join(", ", opt.Select(Item)));
            _reqLabel.Text = string.Join("\n", parts);
            _reqLabel.Visible = true;
        }
        catch { }
    }

    private async Task EndorseSelectedAsync()
    {
        if (_selectedMod == null) return;
        _browser.HighlightEndorseNextLoad();
        if (await OpenInBuiltInBrowserAsync(_selectedMod.PageUrl))
            _browser.ShowNotice(L.F("Click the highlighted Endorse button to endorse {0} (you need to be logged in).", _selectedMod.Name), Theme.Accent);
        else Web.Open(_selectedMod.PageUrl);
    }

    // ---- Installed tab: update all, changelogs

    private Button _updateAllButton = null!;

    private async Task UpdateAllAsync()
    {
        var updates = _manifest.Mods.Where(m => m.Update != null).ToList();
        if (updates.Count == 0) { await RunExclusive(() => CheckUpdatesAsync()); updates = _manifest.Mods.Where(m => m.Update != null).ToList(); }
        if (updates.Count == 0) return;
        var list = string.Join("\n", updates.Select(m => $"  • {m.Name}  v{m.Version} → v{m.Update!.Version}"));
        if (MessageBox.Show(this, L.F("Update {0} mod(s)?\n\n{1}\n\nEach one opens on Nexus with its download button highlighted: click it, and the next one opens after it installs.", updates.Count, list),
                L.T("Update all"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunExclusive(() => StartQueueAsync(updates.Select(m => (m.ModId, (int?)m.Update!.FileId, m.Name))));
    }

    private static string Changelog(ModFileInfo f) =>
        f.Changelog.Count == 0 ? "" : L.T("What's new:") + "\n" + string.Join("\n", f.Changelog.Take(12).Select(c => "  • " + StripTags(c))) + (f.Changelog.Count > 12 ? "\n  …" : "");

    /// <summary>Quietly checks installed mods for updates at startup and shows the count on the menu.</summary>
    private async Task StartupModUpdateCheckAsync()
    {
        if (!_settings.CheckModUpdatesOnStart || _manifest.Mods.All(m => m.ModId <= 0)) return;
        await RunExclusive(() => CheckUpdatesAsync(quiet: true));
    }

    // =====================================================================
    // Game update banner
    // =====================================================================

    private GlassPanel _banner = null!;
    private Label _bannerText = null!;

    private Control BuildBanner()
    {
        _banner = new GlassPanel { Dock = DockStyle.Top, Height = 10, Padding = new Padding(16, 12, 16, 12), Visible = false, Border = Theme.Accent };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _bannerText = new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 6, 16, 6) };
        var check = Theme.Button(L.T("Check mod updates"), true);
        check.Click += async (_, _) => { _banner.Visible = false; ShowPage("Installed"); await RunExclusive(() => CheckUpdatesAsync()); };
        var off = Theme.Button(L.T("Turn all mods off"));
        off.Click += (_, _) => TurnAllModsOff();
        var dismiss = Theme.Button(L.T("Dismiss"));
        dismiss.Click += (_, _) => _banner.Visible = false;
        foreach (var b in new[] { check, off, dismiss }) b.Margin = new Padding(0, 0, 8, 0);
        row.Controls.AddRange(new Control[] { _bannerText, check, off, dismiss });
        _banner.Controls.Add(row);
        row.SizeChanged += (_, _) => _banner.Height = row.Height + _banner.Padding.Vertical;
        var holder = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 12) };
        holder.Controls.Add(_banner);
        return holder;
    }

    private void CheckGameVersion(GameInstall g)
    {
        var fp = GameVersion.Fingerprint(g);
        if (fp.Length == 0) return;
        var before = _settings.LastGameFingerprint;
        if (before != fp) { _settings.LastGameFingerprint = fp; _settings.Save(); }
        if (before == null || before == fp || _manifest.Mods.Count == 0) return;
        _bannerText.Text = L.T("Nomad Drive has been updated. Some mods may stop working until their authors update them.");
        _banner.Visible = true;
        Log(L.T("Nomad Drive has been updated since the last time the app ran."));
    }

    /// <summary>Switches every mod off, saving what was on as a profile so it's one click to switch back.</summary>
    private void TurnAllModsOff()
    {
        if (_game == null) return;
        if (_game.IsRunning()) { MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var store = ProfileStore.Load();
        var name = L.T("Before turning mods off");
        store.Capture(name, _manifest, _game);
        ProfileStore.Apply(new ModProfile { Name = L.T("No mods") }, _manifest, _game);
        store.Active = L.T("No mods");
        store.Save();
        Log(L.F("Turned all mods off. Your previous set is saved as the profile \"{0}\".", name));
        SetStatus(L.F("All mods are off. Switch back with the \"{0}\" profile.", name), good: true);
        _banner.Visible = false;
        RefreshInstalled();
        RefreshProfiles();
    }

    // =====================================================================
    // Drag and drop
    // =====================================================================

    private static readonly string[] DropTypes = { ".zip", ".7z", ".rar", ".dll" };

    private void EnableDragDrop()
    {
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
            e.Effect = files != null && files.Any(f => DropTypes.Contains(Path.GetExtension(f).ToLowerInvariant())) ? DragDropEffects.Copy : DragDropEffects.None;
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files.Where(f => DropTypes.Contains(Path.GetExtension(f).ToLowerInvariant())))
                await RunExclusive(() => InstallFromBrowserDownloadAsync(f, null, dropped: true));
        };
    }

    // =====================================================================
    // Mod settings page (BepInEx\config\*.cfg)
    // =====================================================================

    private ListView _cfgList = null!;
    private FlowLayoutPanel _cfgEditor = null!;
    private Label _cfgTitle = null!, _cfgHint = null!;
    private ConfigFile? _cfg;
    private readonly Dictionary<ConfigEntry, Func<string>> _cfgInputs = new();
    private Button _cfgSave = null!, _cfgRevert = null!;

    private Control BuildSettingsPage()
    {
        var page = new Panel { BackColor = Color.Transparent };

        var left = new GlassPanel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(14) };
        _cfgList = Theme.List();
        _cfgList.Columns.Add(L.T("Mod"), 260);
        _cfgList.SelectedIndexChanged += (_, _) => { if (_cfgList.SelectedItems.Count > 0 && _cfgList.SelectedItems[0].Tag is string p) LoadConfig(p); };
        var leftHint = new Label { Dock = DockStyle.Bottom, Height = Theme.Small.Height * 3 + 10, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Text = L.T("A mod's settings file appears after the game has run once with that mod.") };
        left.Controls.Add(_cfgList);
        left.Controls.Add(leftHint);

        var right = new GlassPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 12) };
        _cfgTitle = new Label { Dock = DockStyle.Top, Height = Theme.H2.Height + 8, Font = Theme.H2, ForeColor = Theme.Text, BackColor = Color.Transparent, Text = L.T("Pick a mod") };
        _cfgHint = new Label { Dock = DockStyle.Top, Height = Theme.Small.Height + 10, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent };
        _cfgEditor = new ScrollStack { Dock = DockStyle.Fill };
        _cfgEditor.HandleCreated += (_, _) => Theme.DarkScrollbars(_cfgEditor);
        _cfgEditor.SizeChanged += (_, _) => { foreach (Control c in _cfgEditor.Controls) c.Width = Math.Max(300, _cfgEditor.ClientSize.Width - 24); };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        _cfgSave = Theme.Button(L.T("Save"), true);
        _cfgSave.Click += (_, _) => SaveConfig();
        _cfgRevert = Theme.Button(L.T("Undo changes"));
        _cfgRevert.Click += (_, _) => { if (_cfg != null) LoadConfig(_cfg.Path); };
        var openFile = Theme.Button(L.T("Open file"));
        openFile.Click += (_, _) => { if (_cfg != null) Web.Open(_cfg.Path); };
        var folder = Theme.Button(L.T("Open config folder"));
        folder.Click += (_, _) => { if (_game != null) { var d = Path.Combine(_game.BepInExDir, "config"); Directory.CreateDirectory(d); Web.Open(d); } };
        foreach (var b in new[] { _cfgSave, _cfgRevert, openFile, folder }) { b.Margin = new Padding(0, 0, 8, 0); bar.Controls.Add(b); }
        _cfgSave.Enabled = _cfgRevert.Enabled = false;
        right.Controls.Add(_cfgEditor);
        right.Controls.Add(_cfgHint);
        right.Controls.Add(_cfgTitle);
        right.Controls.Add(bar);

        page.Controls.Add(right);
        page.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 16, BackColor = Color.Transparent });
        page.Controls.Add(left);
        return page;
    }

    private void RefreshSettingsList()
    {
        if (_cfgList == null) return;
        var keep = _cfg?.Path;
        _cfgList.BeginUpdate();
        _cfgList.Items.Clear();
        if (_game != null)
        {
            foreach (var (path, plugin) in ConfigFile.Find(_game).OrderBy(x => x.Plugin == null && Path.GetFileNameWithoutExtension(x.Path) == "BepInEx").ThenBy(x => x.Plugin?.Name ?? Path.GetFileNameWithoutExtension(x.Path)))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var name = plugin?.Name ?? (stem == "BepInEx" ? L.T("BepInEx (the mod loader)") : stem);
                _cfgList.Items.Add(new ListViewItem(name) { Tag = path, ForeColor = plugin == null && stem != "BepInEx" ? Theme.Muted : Theme.Text, Selected = path == keep });
            }
        }
        _cfgList.EndUpdate();
        if (_cfgList.Items.Count == 0) { _cfgTitle.Text = L.T("No mod settings yet"); _cfgHint.Text = L.T("Run the game once with your mods installed, then come back."); _cfgEditor.Controls.Clear(); }
        else if (_cfgList.SelectedItems.Count == 0) _cfgList.Items[0].Selected = true;
    }

    private void LoadConfig(string path)
    {
        try { _cfg = ConfigFile.Load(path); }
        catch (Exception ex) { ShowError(L.T("Couldn't read that settings file"), ex); return; }
        _cfgInputs.Clear();
        _cfgEditor.SuspendLayout();
        foreach (Control c in _cfgEditor.Controls) c.Dispose();
        _cfgEditor.Controls.Clear();
        _cfgTitle.Text = _cfgList.SelectedItems.Count > 0 ? _cfgList.SelectedItems[0].Text : Path.GetFileName(path);
        _cfgHint.Text = L.T("Changes apply the next time the game starts.") + "   " + Path.GetFileName(path);
        var width = Math.Max(300, _cfgEditor.ClientSize.Width - 24);
        string? section = null;
        foreach (var e in _cfg.Entries)
        {
            if (e.Section != section)
            {
                section = e.Section;
                _cfgEditor.Controls.Add(new Label { Text = section, Font = Theme.Bold, ForeColor = Theme.Accent, BackColor = Color.Transparent, AutoSize = false, Width = width, Height = Theme.Bold.Height + 14, TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, 8, 0, 4) });
            }
            _cfgEditor.Controls.Add(SettingRow(e, width));
        }
        if (_cfg.Entries.Count == 0) _cfgEditor.Controls.Add(Theme.Label(L.T("This file has no settings."), Theme.Body, Theme.Muted));
        _cfgEditor.ResumeLayout();
        _cfgSave.Enabled = _cfgRevert.Enabled = _cfg.Entries.Count > 0;
    }

    private Control SettingRow(ConfigEntry e, int width)
    {
        var row = new TableLayoutPanel { Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = new Padding(0) };
        text.Controls.Add(new Label { Text = e.Key, Font = Theme.Bold, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) });
        var desc = e.Description + (e.Default != null ? (e.Description.Length > 0 ? "  " : "") + L.F("(default: {0})", e.Default) : "");
        if (desc.Length > 0) text.Controls.Add(new Label { Text = desc, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size((int)(width * 0.55) - 10, 0), BackColor = Color.Transparent });

        Control input;
        switch (e.Kind)
        {
            case ConfigKind.Bool:
            {
                var cb = new CheckBox { Checked = e.Value.Equals("true", StringComparison.OrdinalIgnoreCase), Text = e.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? L.T("On") : L.T("Off"), AutoSize = true, ForeColor = Theme.Text, BackColor = Color.Transparent, Cursor = Cursors.Hand };
                cb.CheckedChanged += (_, _) => cb.Text = cb.Checked ? L.T("On") : L.T("Off");
                _cfgInputs[e] = () => cb.Checked ? "true" : "false";
                input = cb;
                break;
            }
            case ConfigKind.Choice:
            {
                var combo = Theme.Combo(220);
                foreach (var o in e.Options) combo.Items.Add(o);
                combo.SelectedItem = e.Options.Contains(e.Value) ? e.Value : null;
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
                _cfgInputs[e] = () => combo.SelectedItem as string ?? e.Value;
                input = combo;
                break;
            }
            default:
            {
                var box = new TextBox { Text = e.Value, Font = Theme.Body, PlaceholderText = e.Kind == ConfigKind.Key ? L.T("e.g. F8 or LeftControl + K") : "" };
                _cfgInputs[e] = () => box.Text;
                input = Field(box, 220);
                break;
            }
        }
        input.Margin = new Padding(8, 2, 0, 0);
        row.Controls.Add(text, 0, 0);
        row.Controls.Add(input, 1, 0);
        return row;
    }

    private void SaveConfig()
    {
        if (_cfg == null) return;
        var errors = new List<string>();
        var changed = 0;
        foreach (var (entry, read) in _cfgInputs)
        {
            var v = read().Trim();
            if (v == entry.Value) continue;
            if (_cfg.Set(entry, v) is { } err) errors.Add($"{entry.Key}: {err}");
            else changed++;
        }
        if (errors.Count > 0)
        {
            MessageBox.Show(this, L.T("Some values weren't saved:") + "\n\n" + string.Join("\n", errors), L.T("Mod settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (changed == 0) return;
        }
        if (changed == 0) { SetStatus(L.T("Nothing changed.")); return; }
        try
        {
            File.Copy(_cfg.Path, Path.Combine(AppPaths.Backups, Path.GetFileName(_cfg.Path) + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak"), true);
            _cfg.Save();
            Log(L.F("Saved {0} setting(s) in {1}.", changed, Path.GetFileName(_cfg.Path)));
            SetStatus(_game?.IsRunning() == true ? L.T("Saved. Restart the game to use the new settings.") : L.T("Saved. The game uses them next time it starts."), good: true);
        }
        catch (Exception ex) { ShowError(L.T("Couldn't save the settings"), ex); }
    }

    // =====================================================================
    // Profiles page (+ share codes)
    // =====================================================================

    private ListView _profileList = null!;

    private Control BuildProfilesPage()
    {
        var page = new GlassPanel { Padding = new Padding(16, 16, 16, 12) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = true, Padding = new Padding(0, 0, 0, 10) };
        var save = Theme.Button(L.T("Save current mods as…"), true);
        save.Click += (_, _) => SaveProfile();
        var use = Theme.Button(L.T("Switch to this profile"));
        use.Click += async (_, _) => await RunExclusive(ApplySelectedProfileAsync);
        var del = Theme.Button(L.T("Delete"));
        del.Click += (_, _) => DeleteSelectedProfile();
        var share = Theme.Button(L.T("Share my mods"));
        share.Click += (_, _) => ShareMods(null);
        var shareSel = Theme.Button(L.T("Share this profile"));
        shareSel.Click += (_, _) => { if (SelectedProfile() is { } p) ShareMods(p); };
        var import = Theme.Button(L.T("Install from a code…"));
        import.Click += async (_, _) => await ImportCodeAsync();
        foreach (var b in new[] { save, use, del, share, shareSel, import }) { b.Margin = new Padding(0, 0, 8, 6); toolbar.Controls.Add(b); }

        _profileList = Theme.List();
        _profileList.Columns.Add(L.T("Profile"), 260);
        _profileList.Columns.Add(L.T("Mods on"), 110);
        _profileList.Columns.Add(L.T("Mods"), 520);
        _profileList.DoubleClick += async (_, _) => await RunExclusive(ApplySelectedProfileAsync);

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = Theme.Small.Height * 2 + 12, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.BottomLeft,
            Text = L.T("A profile remembers which mods are on. Switching turns the others off (nothing is uninstalled). A share code lists your Nexus mods so a friend can install the same set.")
        };
        page.Controls.Add(_profileList);
        page.Controls.Add(hint);
        page.Controls.Add(toolbar);
        return page;
    }

    private ModProfile? SelectedProfile() =>
        _profileList.SelectedItems.Count > 0 && _profileList.SelectedItems[0].Tag is string name ? ProfileStore.Load().Profiles.FirstOrDefault(p => p.Name == name) : null;

    private void RefreshProfiles()
    {
        if (_profileList == null) return;
        var store = ProfileStore.Load();
        var names = _manifest.Mods.ToDictionary(m => m.Id, m => m.Name);
        _profileList.BeginUpdate();
        _profileList.Items.Clear();
        foreach (var p in store.Profiles.OrderBy(p => p.Name))
        {
            var active = p.Name == store.Active;
            var item = new ListViewItem((active ? "› " : "") + p.Name) { Tag = p.Name, ForeColor = active ? Theme.Accent : Theme.Text };
            item.SubItems.Add((p.Enabled.Count + p.EnabledOther.Count).ToString());
            item.SubItems.Add(string.Join(", ", p.Enabled.Select(id => names.TryGetValue(id, out var n) ? n : "?").Concat(p.EnabledOther.Select(Path.GetFileName)).Take(12)));
            _profileList.Items.Add(item);
        }
        _profileList.EndUpdate();
    }

    private void SaveProfile()
    {
        var game = RequireGame();
        if (game == null) return;
        var name = Prompt.Ask(this, L.T("Save profile"), L.T("Name for this set of mods (e.g. \"Vanilla+\", \"Cheats\", \"Full RV\"):"), ProfileStore.Load().Active ?? "", okText: L.T("Save"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var store = ProfileStore.Load();
        var p = store.Capture(name.Trim(), Manifest.Load(), game);
        store.Save();
        Log(L.F("Saved profile \"{0}\" ({1} mod(s) on).", p.Name, p.Enabled.Count + p.EnabledOther.Count));
        RefreshProfiles();
    }

    private async Task ApplySelectedProfileAsync()
    {
        var game = RequireGame();
        if (game == null || SelectedProfile() is not { } profile) return;
        if (game.IsRunning()) { MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        BackupSavesIfOn(L.F("Before switching to profile \"{0}\"", profile.Name));
        _manifest = Manifest.Load();
        var missing = ProfileStore.Apply(profile, _manifest, game);
        var store = ProfileStore.Load();
        store.Active = profile.Name;
        store.Save();
        Log(L.F("Switched to profile \"{0}\".", profile.Name));
        SetStatus(L.F("Switched to \"{0}\".", profile.Name), good: true);
        RefreshInstalled();
        RefreshProfiles();
        if (missing.Count > 0 && MessageBox.Show(this, L.F("{0} mod(s) in this profile aren't installed any more. Install them now?", missing.Count), L.T("Profiles"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await StartQueueAsync(missing.Select(m => (m.ModId, (int?)m.FileId, L.F("Mod {0}", m.ModId))));
    }

    private void DeleteSelectedProfile()
    {
        if (SelectedProfile() is not { } p) return;
        if (MessageBox.Show(this, L.F("Delete the profile \"{0}\"? Your mods stay as they are.", p.Name), L.T("Profiles"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var store = ProfileStore.Load();
        store.Profiles.RemoveAll(x => x.Name == p.Name);
        if (store.Active == p.Name) store.Active = null;
        store.Save();
        RefreshProfiles();
    }

    private void ShareMods(ModProfile? profile)
    {
        var mods = profile?.Mods ?? _manifest.Mods.Where(m => !m.Disabled && m.ModId > 0).Select(m => new SharedMod(m.ModId, m.FileId)).ToList();
        if (mods.Count == 0) { SetStatus(L.T("There are no Nexus mods to share.")); return; }
        var code = ShareCode.Encode(mods);
        try { Clipboard.SetText(code); } catch { }
        Prompt.Ask(this, L.T("Share code"), L.F("Share code for {0} mod(s), copied to the clipboard. Your friend pastes it into \"Install from a code…\".", mods.Count), code, multiline: true, readOnly: true);
    }

    private async Task ImportCodeAsync()
    {
        var game = RequireGame();
        if (game == null) return;
        string initial = "";
        try { if (Clipboard.ContainsText() && Clipboard.GetText().Trim().StartsWith(ShareCode.Prefix, StringComparison.OrdinalIgnoreCase)) initial = Clipboard.GetText().Trim(); } catch { }
        var text = Prompt.Ask(this, L.T("Install from a code"), L.T("Paste a share code (starts with NMD1-):"), initial, multiline: true, okText: L.T("Next"));
        if (text == null) return;
        var mods = ShareCode.Decode(text);
        if (mods == null) { MessageBox.Show(this, L.T("That isn't a valid share code."), L.T("Install from a code"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var installed = _manifest.Mods.Select(m => m.ModId).ToHashSet();
        var todo = mods.Where(m => !installed.Contains(m.ModId)).ToList();
        SetStatus(L.T("Getting mod details…"));
        var names = new List<(int, int?, string)>();
        foreach (var m in todo)
        {
            string name;
            try { name = (await _api.GetModAsync(m.ModId, _closing.Token))?.Name ?? L.F("Mod {0}", m.ModId); } catch { name = L.F("Mod {0}", m.ModId); }
            names.Add((m.ModId, m.FileId, name));
        }
        SetStatus(L.T("Ready"));
        if (names.Count == 0) { MessageBox.Show(this, L.F("You already have all {0} mod(s) in that code.", mods.Count), L.T("Install from a code"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var list = string.Join("\n", names.Select(n => "  • " + n.Item3));
        if (MessageBox.Show(this, L.F("Install {0} mod(s)?\n\n{1}\n\nEach one opens on Nexus with its download button highlighted: click it, and the next one opens after it installs.", names.Count, list),
                L.T("Install from a code"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunExclusive(() => StartQueueAsync(names));
    }

    // =====================================================================
    // Saves page
    // =====================================================================

    private ListView _saveList = null!;
    private Label _saveFolderLabel = null!;

    private Control BuildSavesPage()
    {
        var page = new GlassPanel { Padding = new Padding(16, 16, 16, 12) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
        var now = Theme.Button(L.T("Back up now"), true);
        now.Click += (_, _) => BackupNow();
        var restore = Theme.Button(L.T("Restore"));
        restore.Click += (_, _) => RestoreSelected();
        var del = Theme.Button(L.T("Delete"));
        del.Click += (_, _) => DeleteSelectedBackup();
        var openSaves = Theme.Button(L.T("Open saves folder"));
        openSaves.Click += (_, _) => { if (SaveBackups.FindSaveFolder() is { } d) Web.Open(d); else SetStatus(L.T("No Nomad Drive saves found yet.")); };
        var openBackups = Theme.Button(L.T("Open backups folder"));
        openBackups.Click += (_, _) => Web.Open(SaveBackups.BackupDir);
        foreach (var b in new[] { now, restore, del, openSaves, openBackups }) { b.Margin = new Padding(0, 0, 8, 6); toolbar.Controls.Add(b); }

        var auto = new CheckBox { Text = L.T("Back up saves automatically before installing, updating or switching mods"), Checked = _settings.BackupSavesBeforeChanges, AutoSize = true, ForeColor = Theme.Text, BackColor = Color.Transparent, Cursor = Cursors.Hand, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
        auto.CheckedChanged += (_, _) => { _settings.BackupSavesBeforeChanges = auto.Checked; _settings.Save(); };
        _saveFolderLabel = new Label { Dock = DockStyle.Top, Height = Theme.Small.Height + 12, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent };

        _saveList = Theme.List();
        _saveList.Columns.Add(L.T("Backed up"), 200);
        _saveList.Columns.Add(L.T("Why"), 420);
        _saveList.Columns.Add(L.T("Size"), 100);
        _saveList.DoubleClick += (_, _) => RestoreSelected();

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = Theme.Small.Height * 2 + 12, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.BottomLeft,
            Text = L.F("Automatic backups keep the newest {0}. Restoring backs up your current saves first, so it can be undone.", SaveBackups.KeepAutomatic)
        };
        page.Controls.Add(_saveList);
        page.Controls.Add(hint);
        page.Controls.Add(_saveFolderLabel);
        page.Controls.Add(auto);
        page.Controls.Add(toolbar);
        return page;
    }

    private void RefreshSaves()
    {
        if (_saveList == null) return;
        var folder = SaveBackups.FindSaveFolder();
        _saveFolderLabel.Text = folder != null ? L.T("Saves: ") + folder : L.T("No Nomad Drive saves found yet. Play the game once first.");
        _saveList.BeginUpdate();
        _saveList.Items.Clear();
        foreach (var b in SaveBackups.List())
        {
            var item = new ListViewItem(Fmt.Date(b.Time) + "  " + b.Time.ToString("HH:mm")) { Tag = b };
            item.SubItems.Add(b.Reason);
            item.SubItems.Add(Fmt.Size(b.Size));
            _saveList.Items.Add(item);
        }
        _saveList.EndUpdate();
    }

    /// <summary>Automatic backup before a mod change (when switched on). Never stops the change if it fails.</summary>
    private void BackupSavesIfOn(string reason)
    {
        if (!_settings.BackupSavesBeforeChanges) return;
        try { if (SaveBackups.Create(reason, automatic: true) is { } b) Log(L.F("Backed up your saves ({0}).", Fmt.Size(b.Size))); }
        catch (Exception ex) { Log(L.F("Couldn't back up your saves: {0}", ex.Message)); }
    }

    private void BackupNow()
    {
        try
        {
            var b = SaveBackups.Create(L.T("Backed up by hand"), automatic: false);
            if (b == null) { SetStatus(L.T("No Nomad Drive saves found yet.")); return; }
            SetStatus(L.F("Saves backed up ({0}).", Fmt.Size(b.Size)), good: true);
            RefreshSaves();
        }
        catch (Exception ex) { ShowError(L.T("Couldn't back up your saves"), ex); }
    }

    private void RestoreSelected()
    {
        if (_saveList.SelectedItems.Count == 0 || _saveList.SelectedItems[0].Tag is not SaveBackup b) return;
        if (_game?.IsRunning() == true) { MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show(this, L.F("Restore the saves from {0}?\n\nYour current saves are backed up first, so you can undo this.", Fmt.Date(b.Time) + " " + b.Time.ToString("HH:mm")),
                L.T("Restore saves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            SaveBackups.Restore(b);
            Log(L.F("Restored saves from {0}.", Path.GetFileName(b.Path)));
            SetStatus(L.T("Saves restored."), good: true);
            RefreshSaves();
        }
        catch (Exception ex) { ShowError(L.T("Couldn't restore the saves"), ex); }
    }

    private void DeleteSelectedBackup()
    {
        if (_saveList.SelectedItems.Count == 0 || _saveList.SelectedItems[0].Tag is not SaveBackup b) return;
        if (MessageBox.Show(this, L.T("Delete this backup?"), L.T("Saves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SaveBackups.Delete(b);
        RefreshSaves();
    }

    // =====================================================================
    // Problems page (last session's errors + conflicts)
    // =====================================================================

    private ListView _problemList = null!;
    private TextBox _problemDetail = null!;
    private Label _problemHeader = null!;
    private Button _problemDisable = null!;

    private sealed record ProblemRow(Problem? Problem, Conflict? Conflict);

    private Control BuildProblemsPage()
    {
        var page = new GlassPanel { Padding = new Padding(16, 16, 16, 12) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
        var again = Theme.Button(L.T("Check again"), true);
        again.Click += (_, _) => { var n = RefreshProblems(); SetStatus(n == 0 ? L.T("No problems found.") : L.F("{0} problem(s) found.", n), good: n == 0); };
        _problemDisable = Theme.Button(L.T("Disable this mod"));
        _problemDisable.Enabled = false;
        _problemDisable.Click += (_, _) => DisableProblemMod();
        var log = Theme.Button(L.T("Open BepInEx log"));
        log.Click += (_, _) => { if (_game != null && File.Exists(LogAnalyzer.BepInExLog(_game))) Web.Open(LogAnalyzer.BepInExLog(_game)); else SetStatus(L.T("There's no BepInEx log yet. Play the game once first.")); };
        var copy = Theme.Button(L.T("Copy for a bug report"));
        copy.Click += (_, _) => CopyProblemReport();
        foreach (var b in new[] { again, _problemDisable, log, copy }) { b.Margin = new Padding(0, 0, 8, 6); toolbar.Controls.Add(b); }

        _problemHeader = new Label { Dock = DockStyle.Top, Height = Theme.Small.Height + 14, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent };
        _problemList = Theme.List();
        _problemList.Columns.Add(L.T("What"), 110);
        _problemList.Columns.Add(L.T("Mod"), 200);
        _problemList.Columns.Add(L.T("Problem"), 520);
        _problemList.Columns.Add(L.T("Times"), 70);
        _problemList.SelectedIndexChanged += (_, _) => ShowProblemDetail();
        _problemDetail = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Mono, WordWrap = true };
        _problemDetail.HandleCreated += (_, _) => Theme.DarkScrollbars(_problemDetail);
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Color.Transparent };
        page.Controls.Add(_problemList);
        page.Controls.Add(gap);
        page.Controls.Add(_problemDetail);
        page.Controls.Add(_problemHeader);
        page.Controls.Add(toolbar);
        return page;
    }

    /// <returns>How many problems (errors, crashes and conflicts) were found.</returns>
    private int RefreshProblems()
    {
        if (_problemList == null || _game == null) { SetBadge("Problems", null); return 0; }
        _manifest = Manifest.Load();
        SessionReport report;
        List<Conflict> conflicts;
        try { report = LogAnalyzer.Analyze(_game, _manifest); conflicts = ConflictChecker.Check(_game, _manifest); }
        catch (Exception ex) { Log(L.F("Couldn't read the game's logs: {0}", ex.Message)); return 0; }

        _problemHeader.Text = !report.BepInExRan
            ? L.T("No game session yet: play the game once with mods, then check here.")
            : L.F("Last game session: {0}  ·  {1} mod(s) loaded", Fmt.Date(report.LogTime ?? DateTime.Now) + " " + (report.LogTime ?? DateTime.Now).ToString("HH:mm"), report.LoadedPlugins.Count);
        _problemList.BeginUpdate();
        _problemList.Items.Clear();
        foreach (var p in report.Problems.OrderByDescending(p => p.Level))
        {
            var what = p.Level switch { ProblemLevel.Crash => L.T("Crash"), ProblemLevel.Error => L.T("Error"), ProblemLevel.Warning => L.T("Warning"), _ => L.T("Note") };
            var item = new ListViewItem(what) { Tag = new ProblemRow(p, null) };
            item.SubItems.Add(p.ModName ?? p.Plugin ?? "—");
            item.SubItems.Add(p.Title);
            item.SubItems.Add(p.Count > 1 ? "×" + p.Count : "");
            item.ForeColor = p.Level >= ProblemLevel.Error ? Theme.Bad : Theme.Warn;
            _problemList.Items.Add(item);
        }
        foreach (var c in conflicts)
        {
            var item = new ListViewItem(L.T("Conflict")) { Tag = new ProblemRow(null, c), ForeColor = Theme.Warn };
            item.SubItems.Add(string.Join(", ", c.Mods));
            item.SubItems.Add(c.Message.Split('\n')[0]);
            item.SubItems.Add("");
            _problemList.Items.Add(item);
        }
        if (_problemList.Items.Count == 0)
            _problemList.Items.Add(new ListViewItem("✓") { ForeColor = Theme.Good, SubItems = { "", report.BepInExRan ? L.T("No problems found in the last session.") : L.T("Nothing to check yet."), "" } });
        _problemList.EndUpdate();
        _problemDetail.Text = "";
        _problemDisable.Enabled = false;

        var count = report.Problems.Count(p => p.Level >= ProblemLevel.Error) + conflicts.Count;
        SetBadge("Problems", count > 0 ? count.ToString() : null);
        return count;
    }

    private void ShowProblemDetail()
    {
        if (_problemList.SelectedItems.Count == 0 || _problemList.SelectedItems[0].Tag is not ProblemRow row) { _problemDetail.Text = ""; _problemDisable.Enabled = false; return; }
        _problemDetail.Text = (row.Problem != null ? row.Problem.Title + "\r\n\r\n" + row.Problem.Detail : row.Conflict!.Message).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        var name = row.Problem?.ModName ?? row.Conflict?.Mods.FirstOrDefault();
        _problemDisable.Enabled = name != null && _manifest.Mods.Any(m => m.Name == name && !m.Disabled);
    }

    private void DisableProblemMod()
    {
        if (_game == null || _problemList.SelectedItems.Count == 0 || _problemList.SelectedItems[0].Tag is not ProblemRow row) return;
        var name = row.Problem?.ModName ?? row.Conflict?.Mods.FirstOrDefault();
        var mod = _manifest.Mods.FirstOrDefault(m => m.Name == name);
        if (mod == null) return;
        if (_game.IsRunning()) { MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        ModInstaller.SetDisabled(mod, true, _game, _manifest);
        Log(L.F("Disabled {0}.", mod.Name));
        SetStatus(L.F("Disabled {0}. Turn it back on from the Installed tab.", mod.Name), good: true);
        _problemDisable.Enabled = false;
        RefreshInstalled();
    }

    private void CopyProblemReport()
    {
        if (_game == null) return;
        var lines = new List<string> { $"Nomad Mod Downloader {Updater.Current.ToString(3)} · BepInEx {BepInExManager.Check(_game).Version ?? "?"} · game {GameVersion.Fingerprint(_game)}", "" };
        lines.Add("Installed: " + string.Join(", ", _manifest.Mods.Select(m => $"{m.Name} {m.Version}{(m.Disabled ? " (off)" : "")}")));
        lines.Add("");
        foreach (ListViewItem item in _problemList.Items)
        {
            if (item.Tag is not ProblemRow row) continue;
            lines.Add(row.Problem != null ? $"[{item.Text}] {item.SubItems[1].Text}: {row.Problem.Title}{(row.Problem.Count > 1 ? $" (x{row.Problem.Count})" : "")}\n{row.Problem.Detail}" : $"[{item.Text}] {row.Conflict!.Message}");
            lines.Add("");
        }
        try { Clipboard.SetText(string.Join("\r\n", lines).Replace("\r\n", "\n").Replace("\n", "\r\n")); SetStatus(L.T("Copied. Paste it into a bug report or the mod's Nexus page."), good: true); }
        catch { }
    }
}

/// <summary>The link to the game's Discord server: Discord's app icon, slightly brighter on hover.</summary>
internal sealed class ChatButton : Control
{
    private static readonly Image? Icon = Theme.Art("discord.png");
    private bool _hover, _down;

    public ChatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AccessibleName = "Discord";
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        var inset = _down ? 2 : 0;
        var r = new Rectangle(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        if (Icon != null) g.DrawImage(Icon, r);
        if (_hover && !_down)
        {
            using var path = Theme.Rounded(new RectangleF(r.X, r.Y, r.Width, r.Height), r.Width * 0.2f);
            using var glow = new SolidBrush(Color.FromArgb(40, Color.White));
            g.FillPath(glow, path);
        }
    }
}
