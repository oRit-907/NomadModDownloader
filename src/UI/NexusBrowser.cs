using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NomadModDownloader.Core;

namespace NomadModDownloader.UI;

/// <summary>
/// Nexus Mods inside the app. You sign in once (it's remembered) and click
/// "Mod Manager Download" yourself; the nxm:// link that produces is caught here
/// and handed to the installer instead of leaving the app.
/// </summary>
internal sealed class NexusBrowser : Panel
{
    public const string Home = "https://www.nexusmods.com/nomaddrive";

    private readonly WebView2 _web;
    private readonly Label _address;
    private readonly Label _notice;
    private readonly Button _back, _forward;
    private Task<bool>? _init;

    /// <summary>Raised with the full nxm:// link when you click "Mod Manager Download".</summary>
    public event Action<string>? LinkCaught;

    /// <summary>False if the WebView2 runtime isn't on this PC.</summary>
    public bool Available { get; private set; } = true;

    /// <summary>"Back to mods" pressed: the page has no menu entry, so this is how you leave it.</summary>
    public event Action? BackRequested;

    /// <summary>True while the login page was opened from the app's Log in button.</summary>
    public bool CameFromLogin { get; set; }

    public NexusBrowser()
    {
        BackColor = Color.Transparent;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 10) };
        var leave = Theme.Button(L.T("‹ Back to mods"), true);
        leave.Click += (_, _) => { CameFromLogin = false; BackRequested?.Invoke(); };
        _back = Theme.Button(L.T("‹ Back"));
        _forward = Theme.Button(L.T("Forward ›"));
        var reload = Theme.Button(L.T("Reload"));
        var home = Theme.Button(L.T("Nomad Drive mods"));
        var external = Theme.Button(L.T("Open in my browser"));
        _back.Click += (_, _) => { if (_web?.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); };
        _forward.Click += (_, _) => { if (_web?.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward(); };
        reload.Click += (_, _) => _web?.CoreWebView2?.Reload();
        home.Click += async (_, _) => await NavigateAsync(Home);
        external.Click += (_, _) => Web.Open(_web?.Source?.ToString() ?? Home);
        _address = new Label { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Margin = new Padding(8, 9, 0, 0) };
        leave.Margin = new Padding(0, 0, 18, 0);
        bar.Controls.AddRange(new Control[] { leave, _back, _forward, reload, home, external, _address });

        _notice = new Label
        {
            Dock = DockStyle.Top, Height = Theme.Small.Height + 20, BackColor = Theme.Card, ForeColor = Theme.Text, Padding = new Padding(12, 0, 12, 0),
            TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Small,
            Text = L.T("Log in to Nexus here once (it's remembered). On a mod's Files tab, click \"Manual download\" then \"Slow download\" — the mod installs automatically.")
        };

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Panel };

        Controls.Add(_web);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent });
        Controls.Add(_notice);
        Controls.Add(bar);
        UpdateNav();
    }

    public void ShowNotice(string text, Color? color = null)
    {
        _notice.Text = text;
        _notice.ForeColor = color ?? Theme.Text;
    }

    public Task<bool> EnsureReadyAsync() => _init ??= InitAsync();

    // ---------------- login ----------------

    public const string LoginUrl = "https://users.nexusmods.com/auth/sign_in";

    /// <summary>Raised when you log in or out in this tab.</summary>
    public event Action? LoginChanged;

    private bool? _lastLogin;
    private bool _cameFromLogin;

    /// <summary>true = logged in, false = not, null = built-in browser unavailable.</summary>
    public async Task<bool?> GetLoginStateAsync()
    {
        if (!await EnsureReadyAsync()) return null;
        try
        {
            // Nexus keeps your website login in its "nexusmods_session" cookie.
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.nexusmods.com");
            return cookies.Any(c => c.Name.StartsWith("nexusmods_session", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Value));
        }
        catch { return false; }
    }

    public async Task OpenLoginAsync()
    {
        if (!await EnsureReadyAsync()) return;
        _navigated = true;
        _cameFromLogin = true;
        CameFromLogin = true;
        _web.CoreWebView2.Navigate(LoginUrl);
    }

    /// <summary>Clears this app's Nexus cookies (your normal browser isn't affected).</summary>
    public async Task LogOutAsync()
    {
        if (!await EnsureReadyAsync()) return;
        _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _lastLogin = false;
        _navigated = true;
        _web.CoreWebView2.Navigate(Home);
    }

    // ---------------- download-button highlight ----------------

    private int? _highlightFileId;
    private int _highlightLoads;

    /// <summary>On the next page loads (Files tab, then the download page), scroll to and highlight the download button. It never clicks it — you do.</summary>
    public void HighlightNextLoad(int fileId) { _highlightFileId = fileId; _highlightLoads = 2; _highlightEndorse = false; }

    /// <summary>Outlines the mod page's Endorse button (you click it yourself).</summary>
    public void HighlightEndorseNextLoad() { _highlightEndorse = true; _highlightFileId = null; _highlightLoads = 2; }
    private bool _highlightEndorse;

    private async Task HighlightEndorseAsync()
    {
        if (!_highlightEndorse) return;
        if (--_highlightLoads <= 0) _highlightEndorse = false;
        const string script = """
            (function(){
              const glow = 'outline:3px solid #f5a524 !important;outline-offset:3px;box-shadow:0 0 0 7px rgba(245,165,36,.35) !important;border-radius:4px;';
              const txt = e => (e.innerText || e.textContent || '').trim().toLowerCase();
              let tries = 0;
              const t = setInterval(() => {
                tries++;
                const all = [...document.querySelectorAll('a,button')];
                const el = all.find(e => /^endorse(d)?\b/.test(txt(e)) || (e.id || '').toLowerCase().includes('endorse') || (e.getAttribute('aria-label') || '').toLowerCase().startsWith('endorse'));
                if (el) { clearInterval(t); el.setAttribute('style', (el.getAttribute('style') || '') + glow); el.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
                if (tries > 40) clearInterval(t);
              }, 400);
            })();
            """;
        try { await _web.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    private async Task HighlightAsync()
    {
        await HighlightEndorseAsync();
        if (_highlightFileId is not int fid) return;
        if (--_highlightLoads <= 0) _highlightFileId = null;
        const string script = """
            (function(fid){
              const glow = 'outline:3px solid #f5a524 !important;outline-offset:3px;box-shadow:0 0 0 7px rgba(245,165,36,.35) !important;border-radius:4px;';
              const txt = e => (e.innerText || e.textContent || '').trim().toLowerCase();
              let tries = 0;
              const t = setInterval(() => {
                tries++;
                const all = [...document.querySelectorAll('a,button')];
                // The download page for the file: "Slow download".
                let el = all.find(e => txt(e).startsWith('slow download'));
                // The Files tab: that file's "Manual download".
                if (!el) {
                  const scopes = [...document.querySelectorAll('[id*="' + fid + '"],[data-id="' + fid + '"],[data-file-id="' + fid + '"]')];
                  for (const sc of scopes) { el = [...sc.querySelectorAll('a,button')].find(e => txt(e).startsWith('manual download')); if (el) break; }
                }
                if (!el) el = all.find(e => txt(e).startsWith('manual download') && (e.href || '').includes('file_id=' + fid));
                if (el) { clearInterval(t); el.setAttribute('style', (el.getAttribute('style') || '') + glow); el.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
                if (tries > 40) clearInterval(t);
              }, 400);
            })(FID);
            """;
        try { await _web.CoreWebView2.ExecuteScriptAsync(script.Replace("FID", fid.ToString())); } catch { }
    }

    private async Task CheckLoginChangedAsync()
    {
        await HighlightAsync();
        var now = await GetLoginStateAsync();
        if (now == _lastLogin) return;
        var wasKnown = _lastLogin != null;
        _lastLogin = now;
        if (now == true && _cameFromLogin)
        {
            _cameFromLogin = false;
            ShowNotice(L.T("Logged in. Pick a mod on the Browse tab and press Install, or browse Nomad Drive mods here."), Theme.Good);
            if (!(_web.CoreWebView2.Source ?? "").Contains("nomaddrive", StringComparison.OrdinalIgnoreCase)) _web.CoreWebView2.Navigate(Home);
        }
        if (wasKnown || now == true) LoginChanged?.Invoke();
    }

    private async Task<bool> InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NomadModDownloader", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            _ = _web.Handle; // the tab may be hidden (e.g. checking login at startup): make sure the control exists
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;

            // Older pages navigate to nxm:// directly...
            core.NavigationStarting += (_, e) =>
            {
                if (IsNxm(e.Uri)) { e.Cancel = true; Raise(e.Uri); }
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                if (IsNxm(e.Uri)) { e.Cancel = true; Raise(e.Uri); }
            };
            // ...newer runtimes route custom schemes through this event instead of asking Windows.
            core.LaunchingExternalUriScheme += (_, e) =>
            {
                e.Cancel = true;
                if (IsNxm(e.Uri)) Raise(e.Uri);
                else Web.Open(e.Uri);
            };
            // Links that open a new window: Nexus pages stay here, anything else goes to your normal browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsNxm(e.Uri)) Raise(e.Uri);
                else if (IsNexus(e.Uri)) core.Navigate(e.Uri);
                else Web.Open(e.Uri);
            };
            // "Manual download" → "Slow download": catch the file instead of saving it to your Downloads folder.
            core.DownloadStarting += (_, e) => CatchDownload(core, e);
            core.SourceChanged += (_, _) => UpdateNav();
            core.NavigationCompleted += async (_, _) => await CheckLoginChangedAsync();
            core.HistoryChanged += (_, _) => UpdateNav();
            core.DocumentTitleChanged += (_, _) => UpdateNav();
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Available = false;
            ShowNotice(L.T("The Microsoft Edge WebView2 Runtime isn't installed, so Nexus will open in your normal browser instead. (Get it from Microsoft: \"WebView2 Runtime\".)"), Theme.Warn);
            return false;
        }
        catch (Exception ex)
        {
            Available = false;
            ShowNotice(L.T("Built-in browser couldn't start: ") + ex.Message + L.T(" — Nexus will open in your normal browser instead."), Theme.Warn);
            return false;
        }
    }

    /// <summary>Opening the tab directly loads the Nomad Drive page the first time.</summary>
    public async Task OpenedAsync()
    {
        if (!await EnsureReadyAsync() || _navigated) return;
        _navigated = true;
        _web.CoreWebView2.Navigate(Home);
    }

    private bool _navigated;

    public async Task<bool> NavigateAsync(string url)
    {
        _navigated = true;
        if (!await EnsureReadyAsync()) return false;
        _web.CoreWebView2.Navigate(url);
        return true;
    }

    /// <summary>Raised with (saved file path, Nexus page it was started from) when a Manual download finishes.</summary>
    public event Action<string, string?>? DownloadCaught;

    private static readonly HashSet<string> ModExt = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".dll" };

    private void CatchDownload(CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        var page = core.Source;
        var name = Path.GetFileName(e.ResultFilePath);
        // Only Nomad Drive mod files started from a Nomad Drive page; anything else downloads normally.
        if (!NexusDownloadContext.IsNomadDrivePage(page) || !ModExt.Contains(Path.GetExtension(name))) return;

        var folder = Path.Combine(AppPaths.Downloads, "browser");
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, name);
        if (File.Exists(dest)) { try { File.Delete(dest); } catch { dest = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:HHmmss}{Path.GetExtension(name)}"); } }
        e.ResultFilePath = dest;
        e.Handled = true; // no Edge download bubble

        var op = e.DownloadOperation;
        ShowNotice(L.F("Downloading {0}…", name), Theme.Accent);
        op.BytesReceivedChanged += (_, _) =>
        {
            var total = (long?)op.TotalBytesToReceive ?? 0;
            ShowNotice(total > 0 ? L.F("Downloading {0}… {1}%", name, op.BytesReceived * 100 / total) : L.F("Downloading {0}… {1}", name, Fmt.Size(op.BytesReceived)), Theme.Accent);
        };
        op.StateChanged += (_, _) =>
        {
            switch (op.State)
            {
                case CoreWebView2DownloadState.Completed:
                    ShowNotice(L.F("Downloaded {0} — installing…", name), Theme.Good);
                    BeginInvoke(() => DownloadCaught?.Invoke(op.ResultFilePath, page));
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    ShowNotice(L.F("Download of {0} stopped ({1}). Try the button again.", name, op.InterruptReason), Theme.Bad);
                    break;
            }
        };
    }

    private void Raise(string uri)
    {
        if (!NxmLink.TryParse(uri, out var link) || link == null) return;
        if (!link.IsNomadDrive)
        {
            ShowNotice(L.F("That link is for {0}, not Nomad Drive — ignored.", link.GameDomain), Theme.Warn);
            return;
        }
        ShowNotice(L.T("Got it — installing…"), Theme.Good);
        // Let the page finish its click handler before we pop dialogs.
        BeginInvoke(() => LinkCaught?.Invoke(uri));
    }

    private void UpdateNav()
    {
        var core = _web.CoreWebView2;
        _back.Enabled = core?.CanGoBack == true;
        _forward.Enabled = core?.CanGoForward == true;
        var src = core?.Source;
        _address.Text = string.IsNullOrEmpty(src) ? "" : (Uri.TryCreate(src, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath : src);
    }

    private static bool IsNxm(string? uri) => uri != null && uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase);

    private static bool IsNexus(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        (u.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase)
         || u.Host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase)); // download servers: keep in-app so the file gets caught
}
