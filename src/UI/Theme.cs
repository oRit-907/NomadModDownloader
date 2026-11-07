using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using NomadModDownloader.Core;

namespace NomadModDownloader.UI;

/// <summary>
/// Nomad Drive's own look: the colours below are read from the game's main menu
/// (cream text, amber highlights, dark navy rounded panels) and the font is the
/// game's Noto Sans. The menu backgrounds and logo are read from the player's own game install (GameArt).
/// </summary>
/// <summary>A heading row in a list (e.g. an author's name above their mods). Not selectable.</summary>
internal sealed record ListHeader(string Text);

internal static class Theme
{
    // ---- colours (from the game's menu) ----
    public static readonly Color Text = Color.FromArgb(0xFF, 0xF9, 0xDF);     // cream
    public static readonly Color Accent = Color.FromArgb(0xFA, 0xC1, 0x6E);   // amber
    public static readonly Color AccentDark = Color.FromArgb(0xF2, 0xB2, 0x40);
    public static readonly Color Muted = Color.FromArgb(0xB9, 0xB4, 0xA3);    // labels
    public static readonly Color Bg = Color.FromArgb(0x16, 0x19, 0x24);       // behind everything
    public static readonly Color Panel = Color.FromArgb(0x1E, 0x22, 0x30);    // panel fill
    public static readonly Color Field = Color.FromArgb(0x23, 0x27, 0x34);    // inputs
    public static readonly Color Row = Color.FromArgb(0x26, 0x2B, 0x3A);      // alternate rows
    public static readonly Color Card = Color.FromArgb(0x2C, 0x32, 0x41);
    public static readonly Color Line = Color.FromArgb(0x3C, 0x41, 0x54);     // secondary buttons, selection
    public static readonly Color Edge = Color.FromArgb(0x54, 0x5C, 0x73);     // borders
    public static readonly Color Good = Color.FromArgb(0x34, 0xB5, 0x24);
    public static readonly Color Warn = Color.FromArgb(0xF2, 0xB2, 0x40);
    public static readonly Color Bad = Color.FromArgb(0xF0, 0x6A, 0x5A);
    public static readonly Color Ink = Color.FromArgb(0x1E, 0x22, 0x30);      // text on amber buttons

    // ---- fonts ----
    public static Font Body { get; private set; } = SystemFonts.DefaultFont;
    public static Font Small { get; private set; } = SystemFonts.DefaultFont;
    public static Font Bold { get; private set; } = SystemFonts.DefaultFont;
    public static Font Title { get; private set; } = SystemFonts.DefaultFont;
    public static Font H2 { get; private set; } = SystemFonts.DefaultFont;
    public static Font Nav { get; private set; } = SystemFonts.DefaultFont;
    public static Font Mono { get; private set; } = SystemFonts.DefaultFont;

    private static readonly PrivateFontCollection Fonts = new();

    /// <summary>Loads Noto Sans (the game's font). Chinese, Japanese and Korean use Windows' own UI font for those scripts.</summary>
    public static void Init()
    {
        FontFamily regular, bold;
        var cjk = L.Current.Code switch { "zh" => "Microsoft YaHei UI", "ja" => "Yu Gothic UI", "ko" => "Malgun Gothic", _ => null };
        if (cjk != null && IsInstalled(cjk))
        {
            regular = bold = new FontFamily(cjk);
        }
        else
        {
            regular = LoadFont("NotoSans-Regular.ttf") ?? new FontFamily("Segoe UI");
            bold = LoadFont("NotoSans-Bold.ttf") ?? regular;
        }
        Body = new Font(regular, 10f);
        Small = new Font(regular, 8.75f);
        Bold = new Font(bold, 10f, bold == regular ? FontStyle.Bold : FontStyle.Regular);
        Title = new Font(bold, 14f, bold == regular ? FontStyle.Bold : FontStyle.Regular);
        H2 = new Font(bold, 15f, bold == regular ? FontStyle.Bold : FontStyle.Regular);
        Nav = new Font(bold, 20f, bold == regular ? FontStyle.Bold : FontStyle.Regular);
        Mono = IsInstalled("Cascadia Mono") ? new Font("Cascadia Mono", 9f) : new Font("Consolas", 9.5f);
    }

    private static bool IsInstalled(string family)
    {
        try { using var f = new Font(family, 10f); return f.Name.Equals(family, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static FontFamily? LoadFont(string file)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Art." + file);
            if (s == null) return null;
            var data = new byte[s.Length];
            s.ReadExactly(data);
            var ptr = Marshal.AllocCoTaskMem(data.Length);   // kept for the app's lifetime (GDI+ needs it)
            Marshal.Copy(data, 0, ptr, data.Length);
            uint n = 0;
            AddFontMemResourceEx(ptr, (uint)data.Length, IntPtr.Zero, ref n); // so GDI text (TextRenderer) can use it too
            var before = Fonts.Families.Length;
            Fonts.AddMemoryFont(ptr, data.Length);
            return Fonts.Families.Length > before ? Fonts.Families[^1] : Fonts.Families.LastOrDefault();
        }
        catch { return null; }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    // ---- game art ----
    public static Image? Art(string file)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Art." + file);
            if (s == null) return null;
            using var img = Image.FromStream(s);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- control factories ----

    public static Button Button(string text, bool primary = false) => new GameButton { Text = text, Primary = primary };

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 6, 8, 0)
    };

    public static ComboBox Combo(int width)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = width, BackColor = Field, ForeColor = Text, FlatStyle = FlatStyle.Flat,
            Font = Body, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Body.Height + 10, Cursor = Cursors.Hand
        };
        c.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using var bg = new SolidBrush(sel ? Line : Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), Body, Rectangle.Inflate(e.Bounds, -6, 0), sel ? Accent : Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        c.HandleCreated += (_, _) => DarkScrollbars(c);
        return c;
    }

    public static ListView List()
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackColor = Panel,
            ForeColor = Text,
            Font = Body,
            OwnerDraw = true,
            Dock = DockStyle.Fill,
            Scrollable = true
        };
        // Column labels: bold sand-coloured text on a slightly lighter strip, so they read clearly.
        lv.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(Edge)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Bold, Rectangle.Inflate(e.Bounds, -8, 0), Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        // Whole-row rounded background, like the game's list rows.
        lv.DrawItem += (_, e) =>
        {
            using (var bg = new SolidBrush(Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Item!.Tag is ListHeader h)
            {
                var tr = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 6, e.Bounds.Width - 8, e.Bounds.Height - 6);
                TextRenderer.DrawText(e.Graphics, h.Text, Bold, tr, Accent, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using var line = new Pen(Line);
                e.Graphics.DrawLine(line, e.Bounds.X + 4, e.Bounds.Bottom - 1, e.Bounds.Right - 4, e.Bounds.Bottom - 1);
                return;
            }
            var fill = e.Item!.Selected ? Line : (e.ItemIndex % 2 == 1 ? Row : Panel);
            if (fill != Panel)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(e.Bounds.X + 0.5f, e.Bounds.Y + 1.5f, e.Bounds.Width - 1, e.Bounds.Height - 3);
                using var path = Rounded(r, 9);
                using var b = new SolidBrush(fill);
                e.Graphics.FillPath(b, path);
            }
        };
        lv.DrawSubItem += (_, e) =>
        {
            if (e.Item!.Tag is ListHeader) return; // drawn whole by DrawItem
            var selected = e.Item!.Selected;
            static bool Ours(Color c) => c == Good || c == Warn || c == Bad || c == Accent || c == Muted;
            var color = e.ColumnIndex == 0
                ? (selected ? Accent : Ours(e.Item.ForeColor) ? e.Item.ForeColor : Text)
                : (Ours(e.SubItem!.ForeColor) ? e.SubItem.ForeColor : Muted);
            TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, e.ColumnIndex == 0 ? Bold : Body, Rectangle.Inflate(e.Bounds, -8, 0), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        // Heading rows can't be selected.
        lv.ItemSelectionChanged += (_, e) => { if (e.IsSelected && e.Item?.Tag is ListHeader) e.Item.Selected = false; };
        // Taller rows
        lv.SmallImageList = new ImageList { ImageSize = new Size(1, Body.Height + 18) };

        // Columns share the list's width (their starting widths are the proportions), so there's
        // never a sideways scrollbar or an empty white strip in the header.
        var fitting = false;
        int[]? weights = null;
        var lastWidth = -1;
        void FillLast()
        {
            if (fitting || lv.Columns.Count == 0 || !lv.IsHandleCreated) return;
            fitting = true;
            try
            {
                weights ??= lv.Columns.Cast<ColumnHeader>().Select(c => Math.Max(20, c.Width)).ToArray();
                var width = lv.ClientSize.Width - 2;
                if (width <= 0) return;
                // Columns tagged "optional" give their room to the others when the list is narrow.
                var narrow = width < weights.Sum() * 0.85;
                bool Shown(int i) => !(narrow && lv.Columns[i].Tag as string == "optional");
                var total = Enumerable.Range(0, weights.Length).Where(Shown).Sum(i => weights[i]);
                var last = Enumerable.Range(0, weights.Length).Last(Shown);
                lastWidth = lv.ClientSize.Width;
                var used = 0;
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    var w = !Shown(i) ? 0 : i == last ? width - used : (int)(width * (weights[i] / (double)total));
                    if (lv.Columns[i].Width != w) lv.Columns[i].Width = w;
                    used += w;
                }
            }
            finally { fitting = false; }
        }
        lv.HandleCreated += (_, _) =>
        {
            DarkScrollbars(lv);
            lv.BeginInvoke(FillLast);
        };
        lv.SizeChanged += (_, _) => FillLast();
        // The vertical scrollbar appearing (as rows are added) narrows the list without a resize event: catch it on paint.
        lv.DrawItem += (_, _) => { if (lv.ClientSize.Width != lastWidth && !fitting) lv.BeginInvoke(FillLast); };
        lv.ClientSizeChanged += (_, _) => FillLast();
        return lv;
    }

    /// <summary>Dark title bar on Windows 10 20H1+ / 11.</summary>
    public static void DarkTitleBar(Form f)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
            // Windows 11: title bar in the panel colour
            int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, sizeof(int));
        }
        catch { }
    }

    public static void DarkScrollbars(Control c)
    {
        try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}

/// <summary>A rounded, see-through panel (the game's dark navy panels). Children with transparent backgrounds show it through.</summary>
internal class GlassPanel : Panel
{
    public Color Fill { get; set; } = Color.FromArgb(236, Theme.Panel);
    public Color Border { get; set; } = Color.Empty;
    public int Radius { get; set; } = 16;

    public GlassPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e); // what's behind (the form's background picture)
        if (Fill.A == 0 && Border.IsEmpty) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        if (Fill.A > 0) { using var b = new SolidBrush(Fill); e.Graphics.FillPath(b, path); }
        if (!Border.IsEmpty) { using var p = new Pen(Border, 1.2f); e.Graphics.DrawPath(p, path); }
    }
}

/// <summary>Rounded button: amber with dark text (primary) or slate with cream text, like the game's menus.</summary>
internal sealed class GameButton : Button
{
    private bool _hover, _down;
    public bool Primary { get; set; }

    public GameButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 8, 0);
        Padding = new Padding(16, 7, 16, 7);
        UseVisualStyleBackColor = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return new Size(t.Width + Padding.Horizontal, t.Height + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override bool ShowFocusCues => false;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, text;
        if (!Enabled) { fill = Color.FromArgb(150, Theme.Line); text = Color.FromArgb(150, Theme.Muted); }
        else if (Primary)
        {
            fill = _down ? Theme.AccentDark : _hover ? Color.FromArgb(0xFF, 0xD0, 0x8A) : Theme.Accent;
            text = Theme.Ink;
        }
        else
        {
            fill = _down ? Theme.Card : _hover ? Theme.Edge : Theme.Line;
            text = Theme.Text;
        }
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 11))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

/// <summary>
/// A scrolling stack that redraws cleanly. A plain FlowLayoutPanel with see-through children
/// smears when scrolled (Windows shifts the old pixels); this one has a solid fill, is double
/// buffered and composited, and repaints fully on every scroll.
/// </summary>
public sealed class ScrollStack : FlowLayoutPanel
{
    public ScrollStack()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = Theme.Panel;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED: children paint in one pass, no tearing
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkScrollbars(this); }

    private bool _fitting;
    private int _fittedWidth = -1;

    /// <summary>Keeps every child inside the visible width, so there's never a sideways scrollbar or cut-off text.</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!_fitting && ClientSize.Width > 0 && ClientSize.Width != _fittedWidth)
        {
            _fitting = true;
            try
            {
                _fittedWidth = ClientSize.Width;
                foreach (Control c in Controls)
                {
                    var w = Math.Max(120, ClientSize.Width - Padding.Horizontal - c.Margin.Horizontal - 2);
                    switch (c)
                    {
                        case Label { AutoSize: true } l: l.MaximumSize = new Size(w, l.MaximumSize.Height); break;
                        case FlowLayoutPanel { AutoSize: true } f: f.WrapContents = true; f.MaximumSize = new Size(w, 0); break;
                        case TableLayoutPanel { AutoSize: true } t: t.MinimumSize = new Size(w, 0); t.MaximumSize = new Size(w, 0); break;
                        default: if (!c.AutoSize) c.Width = w; break;
                    }
                }
            }
            finally { _fitting = false; }
        }
        base.OnLayout(levent);
        HorizontalScroll.Visible = false;
    }

    protected override void OnControlAdded(ControlEventArgs e) { _fittedWidth = -1; base.OnControlAdded(e); } // new rows get fitted too
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(true); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(true); }
}

/// <summary>
/// A list drawn like the in-game Mods menu: author headings in amber, and each mod as a rounded card with its name
/// and version on top, a status line under it, and its buttons on the right. Still a ListView underneath, so
/// selection, keyboard and controller navigation work as before.
/// Text comes from the item: line 1 = Text + SubItems[VersionColumn], line 2 = the SubItems in LineColumns (their colours).
/// </summary>
internal sealed class CardList : ListView
{
    public int VersionColumn { get; set; } = 1;
    public int[] LineColumns { get; set; } = Array.Empty<int>();
    /// <summary>The buttons for a row (left to right).</summary>
    public Func<ListViewItem, IReadOnlyList<string>> ButtonsFor { get; set; } = _ => Array.Empty<string>();
    /// <summary>A row's button was pressed (the row is selected first).</summary>
    public event Action<ListViewItem, string>? ButtonClicked;

    private const int RowH = 76, HeadH = 76;
    private Point _mouse = new(-1, -1);

    public CardList()
    {
        View = View.Details;
        HeaderStyle = ColumnHeaderStyle.None;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        OwnerDraw = true;
        Dock = DockStyle.Fill;
        Columns.Add("", 400);
        SmallImageList = new ImageList { ImageSize = new Size(1, RowH) };
        DoubleBuffered = true;
        DrawColumnHeader += (_, e) => e.DrawDefault = false;
        DrawSubItem += (_, _) => { }; // the whole card is drawn in DrawItem
        DrawItem += (_, e) => Draw(e);
        ItemSelectionChanged += (_, e) => { if (e.IsSelected && e.Item?.Tag is ListHeader) e.Item.Selected = false; };
        HandleCreated += (_, _) => { Theme.DarkScrollbars(this); BeginInvoke(Fit); };
        ClientSizeChanged += (_, _) => Fit();
        SizeChanged += (_, _) => Fit();
    }

    private void Fit()
    {
        if (Columns.Count > 0 && ClientSize.Width > 0 && Columns[0].Width != ClientSize.Width) Columns[0].Width = ClientSize.Width;
    }

    private static readonly TextFormatFlags One = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private List<(string Label, Rectangle Rect)> ButtonRects(ListViewItem item, Rectangle card)
    {
        var result = new List<(string, Rectangle)>();
        var labels = item.Tag is ListHeader ? Array.Empty<string>() : ButtonsFor(item);
        var x = card.Right - 14;
        foreach (var label in labels.Reverse())
        {
            var w = TextRenderer.MeasureText(label, Theme.Bold).Width + 30;
            x -= w;
            result.Insert(0, (label, new Rectangle(x, card.Y + (card.Height - 38) / 2, w, 38)));
            x -= 8;
        }
        return result;
    }

    private static Rectangle CardRect(Rectangle row) => new(row.X + 6, row.Y + 4, row.Width - 12, row.Height - 8);

    private void Draw(DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Panel)) g.FillRectangle(bg, e.Bounds);
        var item = e.Item!;
        if (item.Tag is ListHeader h)
        {
            var tr = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height - 10);
            TextRenderer.DrawText(g, h.Text, Theme.H2, tr, Theme.Accent, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        var card = CardRect(e.Bounds);
        var hover = card.Contains(_mouse);
        using (var path = Theme.Rounded(card, 10))
        using (var b = new SolidBrush(item.Selected ? Theme.Line : hover ? Theme.Card : Theme.Row))
            g.FillPath(b, path);
        if (item.Selected) using (var path = Theme.Rounded(card, 10)) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawPath(p, path);

        var buttons = ButtonRects(item, card);
        var textRight = (buttons.Count > 0 ? buttons[0].Rect.X : card.Right) - 12;
        var x = card.X + 18;
        // line 1: name, then the version in grey
        var nameColor = item.ForeColor == Theme.Muted ? Theme.Muted : Theme.Text;
        var nameSize = TextRenderer.MeasureText(g, item.Text, Theme.Title, Size.Empty, One);
        var nameRect = new Rectangle(x, card.Y + 9, Math.Min(nameSize.Width, textRight - x), nameSize.Height);
        TextRenderer.DrawText(g, item.Text, Theme.Title, nameRect, nameColor, One);
        var ver = VersionColumn < item.SubItems.Count ? item.SubItems[VersionColumn].Text : "";
        if (ver.Length > 0 && nameRect.Right + 10 < textRight)
        {
            var v = ver.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? ver : "v" + ver;
            TextRenderer.DrawText(g, v, Theme.Small, new Rectangle(nameRect.Right + 8, card.Y + 15, textRight - nameRect.Right - 8, 22), Theme.Muted, One);
        }
        // line 2: status pieces, each in its own colour, joined by dots
        var lx = x;
        var ly = card.Y + card.Height - 30;
        var first = true;
        foreach (var c in LineColumns)
        {
            if (c >= item.SubItems.Count) continue;
            var s = item.SubItems[c].Text;
            if (string.IsNullOrWhiteSpace(s) || lx >= textRight) continue;
            if (!first)
            {
                TextRenderer.DrawText(g, "  ·  ", Theme.Small, new Rectangle(lx, ly, 40, 22), Theme.Muted, One);
                lx += TextRenderer.MeasureText(g, "  ·  ", Theme.Small, Size.Empty, One).Width;
            }
            var color = item.SubItems[c].ForeColor;
            if (color == ForeColor || color == Theme.Text || color.IsEmpty) color = Theme.Muted;
            var font = first ? Theme.Bold : Theme.Small;
            var w = Math.Min(TextRenderer.MeasureText(g, s, font, Size.Empty, One).Width, textRight - lx);
            TextRenderer.DrawText(g, s, font, new Rectangle(lx, ly - (first ? 1 : 0), w, 22), color, One);
            lx += w;
            first = false;
        }
        // buttons
        foreach (var (label, r) in buttons)
        {
            var over = r.Contains(_mouse);
            using var path = Theme.Rounded(r, 9);
            using var b = new SolidBrush(over ? Theme.Edge : Theme.Line);
            g.FillPath(b, path);
            TextRenderer.DrawText(g, label, Theme.Bold, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var old = GetItemAt(_mouse.X, _mouse.Y);
        _mouse = e.Location;
        var now = GetItemAt(e.X, e.Y);
        if (old != null) Invalidate(old.Bounds);
        if (now != null && now != old) Invalidate(now.Bounds);
        else if (now != null) Invalidate(now.Bounds);
        Cursor = now != null && ButtonRects(now, CardRect(now.Bounds)).Any(b => b.Rect.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var old = GetItemAt(_mouse.X, _mouse.Y);
        _mouse = new Point(-1, -1);
        if (old != null) Invalidate(old.Bounds);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var item = GetItemAt(e.X, e.Y);
        if (item == null || item.Tag is ListHeader) return;
        foreach (var (label, r) in ButtonRects(item, CardRect(item.Bounds)))
            if (r.Contains(e.Location))
            {
                item.Selected = true;
                item.Focused = true;
                ButtonClicked?.Invoke(item, label);
                return;
            }
    }

    /// <summary>Presses the selected row's first button (controller A / Enter on a row).</summary>
    public IReadOnlyList<string> SelectedButtons => SelectedItems.Count > 0 ? ButtonsFor(SelectedItems[0]) : Array.Empty<string>();
}
