using NomadModDownloader.Core;
using NomadModDownloader.UI;

namespace NomadModDownloader;

/// <summary>
/// Installed tab: "Last game start" for every mod, and for a mod that didn't start, why and how to fix it.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Dictionary<ListViewItem, LoadDiagnosis> _loadDiag = new();
    private Panel _loadPanel = null!;
    private Label _loadTitle = null!, _loadText = null!;
    private Button _loadFix = null!;
    private ModLoadDoctor.Context? _loadCtx;

    /// <summary>The "why isn't it loading" box under the Installed list.</summary>
    private Control BuildLoadPanel()
    {
        _loadPanel = new GlassPanel { Dock = DockStyle.Bottom, Height = Theme.Body.Height * 5 + 34, Fill = Theme.Field, Border = Theme.Edge, Radius = 12, Padding = new Padding(14, 10, 14, 10), Visible = false };
        _loadFix = Theme.Button(L.T("Fix"), true);
        _loadFix.Dock = DockStyle.Right;
        _loadFix.Click += async (_, _) => await RunExclusive(FixSelectedLoadAsync);
        var right = new Panel { Dock = DockStyle.Right, Width = 230, BackColor = Color.Transparent, Padding = new Padding(12, 6, 0, 6) };
        right.Controls.Add(_loadFix);
        _loadText = new Label { Dock = DockStyle.Fill, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Color.Transparent, AutoEllipsis = true };
        _loadTitle = new Label { Dock = DockStyle.Top, Height = Theme.Bold.Height + 6, Font = Theme.Bold, BackColor = Color.Transparent, AutoEllipsis = true };
        _loadPanel.Controls.Add(_loadText);
        _loadPanel.Controls.Add(right);
        _loadPanel.Controls.Add(_loadTitle);
        return _loadPanel;
    }

    /// <summary>Gathers the last session's facts once per refresh (it reads every mod's DLLs).</summary>
    private void PrepareLoadChecks()
    {
        _loadDiag.Clear();
        _loadCtx = null;
        if (_game == null) return;
        try { _loadCtx = ModLoadDoctor.Gather(_game, _manifest); }
        catch (Exception ex) { Log(L.F("Couldn't read the last game session: {0}", ex.Message)); }
    }

    /// <summary>Adds the "Last game start" cell to a row.</summary>
    private void AddLoadCell(ListViewItem item, IEnumerable<string> files, bool disabled)
    {
        if (_loadCtx == null) { item.SubItems.Add(""); return; }
        LoadDiagnosis d;
        try { d = ModLoadDoctor.Diagnose(_loadCtx, files, disabled); }
        catch { item.SubItems.Add(""); return; }
        _loadDiag[item] = d;
        var (mark, color) = d.State switch
        {
            LoadState.Started => ("✓ ", Theme.Good),
            LoadState.StartedWithErrors => ("! ", Theme.Warn),
            LoadState.DidntStart => ("✕ ", Theme.Bad),
            LoadState.InstalledSince or LoadState.NoSession => ("• ", Theme.Muted),
            _ => ("", Theme.Muted),
        };
        item.SubItems.Add(mark + d.Short).ForeColor = color;
    }

    private void ShowLoadDetail()
    {
        if (_loadPanel == null) return;
        var item = _installedList.SelectedItems.Count > 0 ? _installedList.SelectedItems[0] : null;
        if (item == null || !_loadDiag.TryGetValue(item, out var d) || d.State is LoadState.Started or LoadState.SupportFiles && d.Fix == LoadFix.None)
        {
            _loadPanel.Visible = item != null && _loadDiag.TryGetValue(item, out var ok) && ok.State == LoadState.Started;
            if (_loadPanel.Visible)
            {
                _loadTitle.Text = "✓  " + L.T("Started at the last game start");
                _loadTitle.ForeColor = Theme.Good;
                _loadText.Text = L.T("BepInEx loaded it without problems.");
                _loadFix.Visible = false;
            }
            return;
        }
        _loadPanel.Visible = true;
        _loadTitle.Text = d.State switch
        {
            LoadState.DidntStart => "✕  " + L.T("Didn't start: ") + d.Short,
            LoadState.StartedWithErrors => "!  " + d.Short,
            _ => d.Short,
        };
        _loadTitle.ForeColor = d.State == LoadState.DidntStart ? Theme.Bad : d.State == LoadState.StartedWithErrors ? Theme.Warn : Theme.Text;
        _loadText.Text = L.T("Why: ") + d.Why + (d.HowToFix.Length > 0 ? "\n" + L.T("How to fix: ") + d.HowToFix : "");
        var label = FixButtonLabel(d, item.Tag);
        _loadFix.Visible = label != null;
        if (label != null) _loadFix.Text = label;
    }

    private static string? FixButtonLabel(LoadDiagnosis d, object? tag) => d.Fix switch
    {
        LoadFix.EnableRequirement => L.F("Switch on {0}", d.FixTarget ?? ""),
        LoadFix.FindRequirement => L.T("Find it on Nexus"),
        LoadFix.DisableThis => L.T("Switch this mod off"),
        LoadFix.Unblock => L.T("Unblock"),
        LoadFix.Reinstall when tag is InstalledMod { ModId: > 0 } => L.T("Reinstall"),
        LoadFix.UpdateThis when tag is InstalledMod { ModId: > 0 } => L.T("Check for updates"),
        LoadFix.OpenChecker => L.T("Open Check BepInEx"),
        LoadFix.OpenProblems => L.T("Open Problems"),
        LoadFix.OpenLog => L.T("Open BepInEx log"),
        LoadFix.Play => L.T("Play"),
        _ => null,
    };

    private async Task FixSelectedLoadAsync()
    {
        var game = RequireGame();
        if (game == null || _installedList.SelectedItems.Count == 0) return;
        var item = _installedList.SelectedItems[0];
        if (!_loadDiag.TryGetValue(item, out var d)) return;
        bool NeedClosed()
        {
            if (!game.IsRunning() || game.HasModManager) return false;
            MessageBox.Show(this, L.T("Close Nomad Drive first, then try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        switch (d.Fix)
        {
            case LoadFix.EnableRequirement:
            {
                if (NeedClosed()) return;
                var rel = d.FixTargetRel?.Replace(".dll.disabled", ".dll");
                var owner = _manifest.Mods.FirstOrDefault(m => m.Files.Any(f => rel != null && (f.Equals(rel, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(f.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))));
                if (owner != null) { ModInstaller.SetDisabled(owner, false, game, _manifest); Log(L.F("Enabled {0}.", owner.Name)); }
                else if (rel != null)
                {
                    // Added by hand: switch on its top-level entry in BepInEx\plugins.
                    var parts = rel.Split('/');
                    var top = parts.Length > 3 ? string.Join("/", parts.Take(3)) : rel;
                    ModInstaller.SetUntrackedDisabled(game, top, false);
                    Log(L.F("Enabled {0}.", top));
                }
                SetStatus(L.F("Switched on {0}. Play the game to check.", d.FixTarget ?? ""), good: true);
                break;
            }
            case LoadFix.FindRequirement:
                ShowPage("Browse mods");
                _search.Text = d.FixTarget ?? "";
                SetStatus(L.F("Searching Nexus for {0}. If it isn't there, check the mod's page for its requirements.", d.FixTarget ?? ""));
                return;
            case LoadFix.DisableThis:
                if (NeedClosed()) return;
                ToggleSelected();
                SetStatus(L.T("Switched it off."), good: true);
                return;
            case LoadFix.Unblock:
            {
                var n = 0;
                foreach (var f in d.Files) { try { File.Delete(f + ":Zone.Identifier"); n++; } catch { } }
                Log(L.F("Unblocked {0} file(s).", n));
                SetStatus(L.T("Unblocked. Play the game to check."), good: true);
                break;
            }
            case LoadFix.Reinstall when item.Tag is InstalledMod { ModId: > 0 } m:
                await InstallFromNexusAsync(m.ModId, m.FileId);
                return;
            case LoadFix.UpdateThis when item.Tag is InstalledMod { ModId: > 0 }:
                await CheckUpdatesAsync();
                return;
            case LoadFix.OpenChecker: ShowPage("Check BepInEx"); return;
            case LoadFix.OpenProblems: ShowPage("Problems"); return;
            case LoadFix.OpenLog:
                if (File.Exists(LogAnalyzer.BepInExLog(game))) Web.Open(LogAnalyzer.BepInExLog(game));
                return;
            case LoadFix.Play: await PlayAsync(vanilla: false); return;
        }
        RefreshInstalled();
    }
}
