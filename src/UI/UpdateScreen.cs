using NomadModDownloader.Core;

namespace NomadModDownloader.UI;

/// <summary>
/// Shown for a moment before the main window: checks GitHub for a new version of the app and,
/// if there is one, downloads it with a progress bar and restarts into it. Never blocks startup
/// for long: offline or slow, it gives up after a few seconds and the app opens as normal.
/// </summary>
internal sealed class UpdateScreen : Form
{
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly Button _skip;
    private readonly CancellationTokenSource _cts = new();
    private readonly Image? _bg;

    /// <summary>true when the new version is being installed: don't open the main window.</summary>
    public bool Restarting { get; private set; }
    /// <summary>true when the check finished (either way), so the main window needn't check again.</summary>
    public bool Checked { get; private set; }

    public UpdateScreen()
    {
        Text = L.T("Nomad Mod Downloader");
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 250);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;
        ShowInTaskbar = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // The game's menu art, if it has been read from the player's install before.
        _bg = GameArt.Open(Path.Combine(AppPaths.Root, "art", "bg1.jpg"));

        var title = new Label { Text = "NOMAD DRIVE", Font = new Font(Theme.Bold.FontFamily, 26f, FontStyle.Bold, GraphicsUnit.Pixel), ForeColor = Theme.Accent, BackColor = Color.Transparent, AutoSize = true, Location = new Point(28, 34) };
        var sub = new Label { Text = L.T("MOD DOWNLOADER"), Font = Theme.Bold, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = true, Location = new Point(31, 74) };
        _status = new Label { Text = L.T("Checking for updates…"), ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = false, Location = new Point(30, 150), Size = new Size(460, 24) };
        _bar = new ProgressBar { Location = new Point(30, 180), Size = new Size(460, 10), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25 };
        _skip = Theme.Button(L.T("Skip"));
        _skip.Location = new Point(410, 200);
        _skip.Click += (_, _) => { _cts.Cancel(); Close(); };
        var version = new Label { Text = L.F("Version {0}", Updater.Current.ToString(3)), ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, AutoSize = true, Location = new Point(30, 210) };
        Controls.AddRange(new Control[] { title, sub, _status, _bar, _skip, version });

        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += async (_, _) => await RunAsync();
        FormClosed += (_, _) => { _cts.Cancel(); _bg?.Dispose(); };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        if (_bg != null)
        {
            var scale = Math.Max(ClientSize.Width / (float)_bg.Width, ClientSize.Height / (float)_bg.Height);
            g.DrawImage(_bg, (ClientSize.Width - _bg.Width * scale) / 2, (ClientSize.Height - _bg.Height * scale) / 2, _bg.Width * scale, _bg.Height * scale);
        }
        using var shade = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, Color.FromArgb(235, 18, 20, 30), Color.FromArgb(150, 18, 20, 30), 0f);
        g.FillRectangle(shade, ClientRectangle);
        using var edge = new Pen(Theme.Edge);
        g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private async Task RunAsync()
    {
        var settings = AppSettings.Load();
        if (!settings.AutoUpdateApp) { Close(); return; } // "ask first" mode: the main window asks instead
        try
        {
            using var http = NexusApi.CreateHttpClient();
            var updater = new Updater(http) { Token = settings.GetGitHubToken() };
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            quick.CancelAfter(TimeSpan.FromSeconds(6)); // don't hold up startup when GitHub is slow or offline
            var (result, info, _) = await updater.CheckAsync(quick.Token);
            Checked = result != UpdateCheck.Failed;
            if (result != UpdateCheck.Available || info == null)
            {
                _status.Text = result == UpdateCheck.Failed ? L.T("Couldn't check for updates. Starting…") : L.T("You're up to date. Starting…");
                await Task.Delay(350);
                Close();
                return;
            }

            _status.Text = L.F("Downloading version {0}…", info.Version.ToString(3));
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 0;
            var progress = new Progress<double>(p => { _bar.Value = Math.Clamp((int)(p * 100), 0, 100); _status.Text = L.F("Downloading version {0}… {1}%", info.Version.ToString(3), (int)(p * 100)); });
            var path = await updater.DownloadAsync(info, progress, _cts.Token);
            _status.Text = L.F("Installing version {0}…", info.Version.ToString(3));
            _skip.Enabled = false;
            await Task.Delay(300);
            Updater.ApplyAndRestart(path);
            Restarting = true;
            Close();
        }
        catch (OperationCanceledException) { if (!IsDisposed) Close(); }
        catch (Exception ex)
        {
            try { File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:u} Update screen: {ex.Message}{Environment.NewLine}"); } catch { }
            _status.Text = L.T("Couldn't update right now. Starting…");
            await Task.Delay(800);
            if (!IsDisposed) Close();
        }
    }
}
