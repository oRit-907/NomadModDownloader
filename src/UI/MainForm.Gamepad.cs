using System.Diagnostics;
using System.Runtime.InteropServices;
using NomadModDownloader.Core;
using NomadModDownloader.UI;

namespace NomadModDownloader;

/// <summary>
/// Controller support. Only while this app is the window in front, so a controller used in the game doesn't
/// drive the app behind it. D-pad / left stick move an amber highlight between buttons, lists and the menu;
/// A presses, B goes back, LB / RB switch pages, the right stick scrolls, Start plays, Y searches.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Gamepad _pad = new();
    private System.Windows.Forms.Timer? _padTimer;
    private Control? _padFocus;
    private readonly Panel[] _ring = new Panel[4];
    private bool _padHinted;
    private float _scrollCarry;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    private const int LVM_SCROLL = 0x1014;

    private static readonly string[] PadPages = { "Browse mods", "Installed", "Mod settings", "Profiles", "Saves", "Problems", "Check BepInEx", "Activity" };

    private void StartGamepad()
    {
        if (!_settings.ControllerSupport) return;
        for (var i = 0; i < 4; i++)
        {
            _ring[i] = new Panel { BackColor = Theme.Accent, Visible = false, Enabled = false };
            Controls.Add(_ring[i]);
        }
        _padTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _padTimer.Tick += (_, _) => { try { PadTick(); } catch (Exception ex) { Debug.WriteLine(ex); } };
        _padTimer.Start();
    }

    private void PadTick()
    {
        var (pressed, move) = _pad.Poll();
        if (!_pad.Connected) { HideRing(); return; }
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        if (pid != (uint)Environment.ProcessId) { HideRing(); return; } // another app (the game) is in front

        var any = pressed != Gamepad.Buttons.None || move != Gamepad.Dir.None || Math.Abs(_pad.RightY) > 0;
        if (!any) return;

        // A dialog of ours is in front (message box, prompt): drive it with keys.
        if (fg != Handle)
        {
            HideRing();
            if (move is Gamepad.Dir.Left or Gamepad.Dir.Up) SendKeys.SendWait("+{TAB}");
            if (move is Gamepad.Dir.Right or Gamepad.Dir.Down) SendKeys.SendWait("{TAB}");
            if (pressed.HasFlag(Gamepad.Buttons.A)) SendKeys.SendWait(" ");
            if (pressed.HasFlag(Gamepad.Buttons.B)) SendKeys.SendWait("{ESC}");
            return;
        }

        if (!_padHinted)
        {
            _padHinted = true;
            SetStatus(L.T("Controller: A select · B back · LB / RB pages · right stick scrolls · Y search · Start play"));
        }

        // The Nexus page is a web page: keys do the work there.
        if (_current == "Nexus" && IsInWeb(ActiveControl))
        {
            HideRing();
            if (move == Gamepad.Dir.Up) SendKeys.SendWait("{UP}");
            if (move == Gamepad.Dir.Down) SendKeys.SendWait("{DOWN}");
            if (move == Gamepad.Dir.Left) SendKeys.SendWait("+{TAB}");
            if (move == Gamepad.Dir.Right) SendKeys.SendWait("{TAB}");
            if (pressed.HasFlag(Gamepad.Buttons.A)) SendKeys.SendWait("{ENTER}");
            if (Math.Abs(_pad.RightY) > 0) SendKeys.SendWait(_pad.RightY > 0 ? "{UP}" : "{DOWN}");
            if (pressed.HasFlag(Gamepad.Buttons.B)) ShowPage(_returnPage);
            return;
        }

        if (pressed.HasFlag(Gamepad.Buttons.LB)) { CyclePage(-1); return; }
        if (pressed.HasFlag(Gamepad.Buttons.RB)) { CyclePage(1); return; }
        if (pressed.HasFlag(Gamepad.Buttons.B)) { PadBack(); return; }
        if (pressed.HasFlag(Gamepad.Buttons.Start)) { _playButton.PerformClick(); return; }
        if (pressed.HasFlag(Gamepad.Buttons.Y)) { ShowPage("Browse mods"); SetPadFocus(_search); OpenTouchKeyboard(); return; }

        if (_padFocus == null || !Usable(_padFocus)) SetPadFocus(FirstOnPage());
        if (move != Gamepad.Dir.None) PadMove(move);
        if (pressed.HasFlag(Gamepad.Buttons.A) && _padFocus != null) PadActivate(_padFocus);
        if (Math.Abs(_pad.RightY) > 0) PadScroll(_pad.RightY);
        ShowRing();
    }

    private void CyclePage(int d)
    {
        var i = Array.IndexOf(PadPages, _current == "Nexus" ? _returnPage : _current);
        ShowPage(PadPages[((i < 0 ? 0 : i) + d + PadPages.Length) % PadPages.Length]);
        SetPadFocus(FirstOnPage());
    }

    private void PadBack()
    {
        if (_current == "Nexus") ShowPage(_returnPage);
        else if (_padFocus is not Label l || !_nav.ContainsValue(l)) SetPadFocus(_nav.TryGetValue(_current, out var n) ? n : null); // first back to the menu
        else ShowPage("Browse mods");
    }

    // ------------------------------------------------------------------

    /// <summary>Inside a list, combo or slider, up/down (or left/right) change it; at its edge the highlight moves on.</summary>
    private void PadMove(Gamepad.Dir d)
    {
        var f = _padFocus;
        switch (f)
        {
            case ListView lv when d is Gamepad.Dir.Up or Gamepad.Dir.Down && lv.Items.Count > 0:
            {
                var i = lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1;
                var next = i + (d == Gamepad.Dir.Up ? -1 : 1);
                if (next >= 0 && next < lv.Items.Count)
                {
                    lv.SelectedItems.Clear();
                    lv.Items[next].Selected = true;
                    lv.Items[next].Focused = true;
                    lv.EnsureVisible(next);
                    return;
                }
                break;
            }
            case ListBox lb when d is Gamepad.Dir.Up or Gamepad.Dir.Down && lb.Items.Count > 0:
            {
                var next = lb.SelectedIndex + (d == Gamepad.Dir.Up ? -1 : 1);
                if (next >= 0 && next < lb.Items.Count) { lb.SelectedIndex = next; return; }
                break;
            }
            case ComboBox cb when d is Gamepad.Dir.Up or Gamepad.Dir.Down && cb.Items.Count > 0 && cb.DropDownStyle == ComboBoxStyle.DropDownList:
            {
                var next = cb.SelectedIndex + (d == Gamepad.Dir.Up ? -1 : 1);
                if (next >= 0 && next < cb.Items.Count) { cb.SelectedIndex = next; return; }
                break;
            }
            case TrackBar tb when d is Gamepad.Dir.Left or Gamepad.Dir.Right:
            {
                var next = tb.Value + (d == Gamepad.Dir.Left ? -tb.SmallChange : tb.SmallChange);
                tb.Value = Math.Clamp(next, tb.Minimum, tb.Maximum);
                return;
            }
            case NumericUpDown nu when d is Gamepad.Dir.Up or Gamepad.Dir.Down:
            {
                if (d == Gamepad.Dir.Up) nu.UpButton(); else nu.DownButton();
                return;
            }
        }

        var from = f != null && Usable(f) ? f.RectangleToScreen(f.ClientRectangle) : new Rectangle(0, 0, 0, 0);
        var c = new PointF(from.X + from.Width / 2f, from.Y + from.Height / 2f);
        Control? best = null;
        var bestScore = float.MaxValue;
        foreach (var t in Targets())
        {
            if (t == f) continue;
            var r = t.RectangleToScreen(t.ClientRectangle);
            var dx = r.X + r.Width / 2f - c.X;
            var dy = r.Y + r.Height / 2f - c.Y;
            float along, across;
            switch (d)
            {
                case Gamepad.Dir.Up: along = -dy; across = Math.Abs(dx); break;
                case Gamepad.Dir.Down: along = dy; across = Math.Abs(dx); break;
                case Gamepad.Dir.Left: along = -dx; across = Math.Abs(dy); break;
                default: along = dx; across = Math.Abs(dy); break;
            }
            // Must be clearly that way: past the edge of the current one.
            var edge = d is Gamepad.Dir.Up or Gamepad.Dir.Down ? from.Height / 2f : from.Width / 2f;
            if (along < Math.Max(4, edge * 0.5f)) continue;
            var score = along + across * 2.5f;
            if (score < bestScore) { bestScore = score; best = t; }
        }
        if (best != null) SetPadFocus(best);
    }

    private void PadActivate(Control c)
    {
        switch (c)
        {
            case Label l when l.Tag is string page && _nav.ContainsKey(page): ShowPage(page); SetPadFocus(FirstOnPage()); break;
            case CheckBox cb: cb.Checked = !cb.Checked; break;
            case RadioButton rb: rb.Checked = true; break;
            case Button b: b.PerformClick(); break;
            case TextBox tb: tb.Focus(); OpenTouchKeyboard(); break;
            case ListView lv when lv.SelectedItems.Count == 0 && lv.Items.Count > 0: lv.Items[0].Selected = true; lv.Items[0].Focused = true; break;
            case ListView: SendKeys.SendWait("{ENTER}"); break;
            case LinkLabel ll when ll.Links.Count > 0:
                typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(ll, new object[] { new LinkLabelLinkClickedEventArgs(ll.Links[0]) });
                break;
            default:
                // Custom controls (e.g. the Discord button) react to a click.
                typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(c, new object[] { EventArgs.Empty });
                break;
        }
    }

    private void PadScroll(float y)
    {
        _scrollCarry += y * 0.6f;
        var steps = (int)_scrollCarry;
        if (steps == 0) return;
        _scrollCarry -= steps;
        var lv = _padFocus as ListView ?? Ancestors(_padFocus).OfType<ListView>().FirstOrDefault();
        if (lv != null && lv.IsHandleCreated) { SendMessage(lv.Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)(-steps * 40)); return; }
        var sc = Ancestors(_padFocus).OfType<ScrollableControl>().FirstOrDefault(s => s.AutoScroll && s.VerticalScroll.Visible)
                 ?? (_pageControls.TryGetValue(_current, out var page) ? Descendants(page).OfType<ScrollableControl>().FirstOrDefault(s => s.AutoScroll && s.VerticalScroll.Visible) : null);
        if (sc == null) return;
        var v = Math.Clamp(sc.VerticalScroll.Value - steps * 60, sc.VerticalScroll.Minimum, Math.Max(sc.VerticalScroll.Minimum, sc.VerticalScroll.Maximum - sc.VerticalScroll.LargeChange + 1));
        sc.AutoScrollPosition = new Point(0, v);
        sc.Invalidate(true);
    }

    // ------------------------------------------------------------------

    private void SetPadFocus(Control? c)
    {
        _padFocus = c;
        if (c == null) { HideRing(); return; }
        if (c.CanFocus && c is not Label) c.Focus();
        // Keep it on screen inside scrolling panels.
        foreach (var sc in Ancestors(c).OfType<ScrollableControl>().Where(s => s.AutoScroll)) { sc.ScrollControlIntoView(c); break; }
        ShowRing();
    }

    /// <summary>Everything the highlight can go to: the side menu, the top bar and the visible page.</summary>
    private IEnumerable<Control> Targets()
    {
        foreach (var l in _nav.Values) if (Usable(l)) yield return l;
        foreach (var c in Descendants(this))
        {
            if (c is Label and not LinkLabel || c is Panel || c is GroupBox || c is SplitContainer || c is TabControl || c is ToolStrip) continue;
            if (c == _ring[0] || c == _ring[1] || c == _ring[2] || c == _ring[3]) continue;
            if (c is not (ButtonBase or ComboBox or ListView or ListBox or TextBoxBase or TrackBar or NumericUpDown or LinkLabel) && c.GetType().Name != "ChatButton") continue;
            if (IsInWeb(c)) continue;
            if (Usable(c)) yield return c;
        }
    }

    private Control? FirstOnPage()
    {
        if (!_pageControls.TryGetValue(_current, out var page)) return _nav.Values.FirstOrDefault();
        var all = Targets().Where(t => Ancestors(t).Contains(page)).ToList();
        return all.OrderBy(t => t.RectangleToScreen(t.ClientRectangle).Y / 20).ThenBy(t => t.RectangleToScreen(t.ClientRectangle).X).FirstOrDefault()
               ?? (_nav.TryGetValue(_current, out var n) ? n : null);
    }

    private static bool Usable(Control c)
    {
        if (!c.Enabled || c.IsDisposed || !c.IsHandleCreated) return false;
        for (var p = c; p != null; p = p.Parent) if (!p.Visible) return false;
        var r = c.RectangleToScreen(c.ClientRectangle);
        return r.Width > 4 && r.Height > 4;
    }

    private static bool IsInWeb(Control? c)
    {
        for (var p = c; p != null; p = p.Parent) if (p.GetType().Name.Contains("WebView")) return true;
        return false;
    }

    private static IEnumerable<Control> Ancestors(Control? c)
    {
        for (var p = c?.Parent; p != null; p = p.Parent) yield return p;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // ------------------------------------------------------------------

    private void ShowRing()
    {
        var c = _padFocus;
        if (c == null || !Usable(c)) { HideRing(); return; }
        var r = RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
        const int t = 3;
        r.Inflate(t + 1, t + 1);
        var parts = new[]
        {
            new Rectangle(r.X, r.Y, r.Width, t), new Rectangle(r.X, r.Bottom - t, r.Width, t),
            new Rectangle(r.X, r.Y, t, r.Height), new Rectangle(r.Right - t, r.Y, t, r.Height),
        };
        for (var i = 0; i < 4; i++)
        {
            if (_ring[i] == null) continue;
            _ring[i].Bounds = parts[i];
            _ring[i].Visible = true;
            _ring[i].BringToFront();
        }
    }

    private void HideRing()
    {
        foreach (var p in _ring) if (p != null && p.Visible) p.Visible = false;
    }

    /// <summary>Windows' on-screen keyboard for typing with a controller (the touch keyboard, else the classic one).</summary>
    private static void OpenTouchKeyboard()
    {
        try
        {
            var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
            if (File.Exists(tabTip)) { Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true }); return; }
            Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
        }
        catch { }
    }
}
