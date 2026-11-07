using System.Drawing.Drawing2D;
using NomadModDownloader.Core;

namespace NomadModDownloader.UI;

/// <summary>
/// Browse mods as a grid of cards, like the in-game Mods menu: the mod's picture on the left, its name, category,
/// author and version, a few lines of its summary, and Install (or "Installed") and Details at the bottom.
/// A ListView in tile view underneath, so selection, keyboard and controller navigation still work.
/// Rows: Tag = ModInfo, Text = the name ("✓ " in front when it's installed).
/// </summary>
internal sealed class ModGrid : ListView
{
    public Func<ModInfo, Image?> ImageFor { get; set; } = _ => null;
    public event Action<ListViewItem, string>? ButtonClicked;
    public string InstallText { get; set; } = "Install";
    public string InstalledText { get; set; } = "Installed";
    public string DetailsText { get; set; } = "Details";

    private const int TileH = 192, Gap = 12;
    private Point _mouse = new(-1, -1);

    public ModGrid()
    {
        View = View.Tile;
        OwnerDraw = true;
        MultiSelect = false;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Columns.Add("");
        DrawItem += (_, e) => Draw(e);
        HandleCreated += (_, _) => { Theme.DarkScrollbars(this); BeginInvoke(Fit); };
        ClientSizeChanged += (_, _) => Fit();
        SizeChanged += (_, _) => Fit();
    }

    public static bool IsInstalled(ListViewItem item) => item.Text.StartsWith("✓ ");
    public static string NameOf(ListViewItem item) => IsInstalled(item) ? item.Text[2..] : item.Text;

    private void Fit()
    {
        var w = ClientSize.Width - 4;
        if (w <= 0) return;
        var cols = w > 1250 ? 3 : w > 760 ? 2 : 1;
        var size = new Size(w / cols - 4, TileH);
        if (TileSize != size) TileSize = size;
    }

    private static Rectangle CardRect(Rectangle tile) => new(tile.X + Gap / 2, tile.Y + Gap / 2, tile.Width - Gap, tile.Height - Gap);

    private (Rectangle Image, Rectangle Text, Rectangle Install, Rectangle Details) Layout(Rectangle card)
    {
        var imgW = Math.Min(190, card.Width * 38 / 100);
        var image = new Rectangle(card.X + 10, card.Y + 10, imgW, card.Height - 20);
        var text = Rectangle.FromLTRB(image.Right + 16, card.Y + 10, card.Right - 12, card.Bottom - 10);
        var bh = 40;
        var dw = TextRenderer.MeasureText(DetailsText, Theme.Bold).Width + 34;
        var iw = Math.Max(TextRenderer.MeasureText(InstallText, Theme.Bold).Width, TextRenderer.MeasureText(InstalledText, Theme.Bold).Width) + 40;
        var details = new Rectangle(text.Right - dw, text.Bottom - bh, dw, bh);
        var install = new Rectangle(text.X, text.Bottom - bh, Math.Min(iw, details.X - 10 - text.X), bh);
        return (image, text, install, details);
    }

    private static readonly TextFormatFlags One = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private void Draw(DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var bg = new SolidBrush(Theme.Panel)) g.FillRectangle(bg, e.Bounds);
        var item = e.Item!;
        if (item.Tag is not ModInfo mod) return;
        var card = CardRect(e.Bounds);
        var hover = card.Contains(_mouse);
        using (var path = Theme.Rounded(card, 12))
        using (var b = new SolidBrush(item.Selected ? Theme.Line : hover ? Theme.Card : Theme.Row))
            g.FillPath(b, path);
        if (item.Selected) using (var path = Theme.Rounded(card, 12)) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawPath(p, path);

        var (imgR, textR, installR, detailsR) = Layout(card);
        // picture, cropped to fill its box
        using (var clip = Theme.Rounded(imgR, 9))
        {
            using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, clip);
            if (ImageFor(mod) is { } img && img.Width > 0 && img.Height > 0)
            {
                var state = g.Save();
                g.SetClip(clip);
                var scale = Math.Max(imgR.Width / (float)img.Width, imgR.Height / (float)img.Height);
                var w = img.Width * scale; var h = img.Height * scale;
                g.DrawImage(img, imgR.X + (imgR.Width - w) / 2, imgR.Y + (imgR.Height - h) / 2, w, h);
                g.Restore(state);
            }
        }

        // name, then category · author · version, then the summary
        var y = textR.Y;
        TextRenderer.DrawText(g, NameOf(item), Theme.Title, new Rectangle(textR.X, y, textR.Width, Theme.Title.Height + 4), Theme.Text, One);
        y += Theme.Title.Height + 4;
        var meta = string.Join("  ·  ", new[] { mod.Category, mod.Author.Length > 0 ? L.F("by {0}", mod.Author) : "", mod.Version.Length > 0 ? "v" + mod.Version.TrimStart('v', 'V') : "" }.Where(s => s.Length > 0));
        TextRenderer.DrawText(g, meta, Theme.Small, new Rectangle(textR.X, y, textR.Width, Theme.Small.Height + 4), Theme.Muted, One);
        y += Theme.Small.Height + 8;
        var sumH = installR.Y - 8 - y;
        if (sumH > 10 && mod.Summary.Length > 0)
            TextRenderer.DrawText(g, System.Net.WebUtility.HtmlDecode(mod.Summary), Theme.Small, new Rectangle(textR.X, y, textR.Width, sumH), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);

        // Install (amber) or "● Installed" (green), and Details
        if (IsInstalled(item))
        {
            var dot = new Rectangle(installR.X + 2, installR.Y + installR.Height / 2 - 5, 10, 10);
            using (var b = new SolidBrush(Theme.Good)) g.FillEllipse(b, dot);
            TextRenderer.DrawText(g, InstalledText, Theme.Bold, Rectangle.FromLTRB(dot.Right + 8, installR.Y, installR.Right, installR.Bottom), Theme.Good,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        else Pill(g, installR, InstallText, true, installR.Contains(_mouse));
        Pill(g, detailsR, DetailsText, false, detailsR.Contains(_mouse));
    }

    private static void Pill(Graphics g, Rectangle r, string text, bool primary, bool over)
    {
        using var path = Theme.Rounded(r, 9);
        using var b = new SolidBrush(primary ? (over ? Color.FromArgb(0xFF, 0xD0, 0x8A) : Theme.Accent) : over ? Theme.Edge : Theme.Line);
        g.FillPath(b, path);
        TextRenderer.DrawText(g, text, Theme.Bold, r, primary ? Theme.Ink : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var old = GetItemAt(_mouse.X, _mouse.Y);
        _mouse = e.Location;
        var now = GetItemAt(e.X, e.Y);
        if (old != null) Invalidate(old.Bounds);
        if (now != null) Invalidate(now.Bounds);
        var onButton = false;
        if (now != null)
        {
            var (_, _, ins, det) = Layout(CardRect(now.Bounds));
            onButton = det.Contains(e.Location) || (!IsInstalled(now) && ins.Contains(e.Location));
        }
        Cursor = onButton ? Cursors.Hand : Cursors.Default;
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
        if (item == null) return;
        var (_, _, ins, det) = Layout(CardRect(item.Bounds));
        string? which = det.Contains(e.Location) ? "Details" : !IsInstalled(item) && ins.Contains(e.Location) ? "Install" : null;
        if (which == null) return;
        item.Selected = true;
        item.Focused = true;
        ButtonClicked?.Invoke(item, which);
    }

    /// <summary>Redraws the card of one mod (its picture just arrived).</summary>
    public void Refresh(int modId)
    {
        foreach (ListViewItem i in Items)
            if (i.Tag is ModInfo m && m.ModId == modId) { Invalidate(i.Bounds); return; }
    }
}
