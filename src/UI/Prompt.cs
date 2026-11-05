namespace NomadModDownloader.UI;

/// <summary>A small dark dialog that asks for one line (or a block) of text.</summary>
internal static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string question, string initial = "", bool multiline = false, bool readOnly = false, string okText = "OK")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Body,
            ClientSize = new Size(500, multiline ? 300 : 170), Padding = new Padding(18), AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi
        };
        f.HandleCreated += (_, _) => Theme.DarkTitleBar(f);
        var label = new Label { Text = question, Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(464, 0), ForeColor = Theme.Text, Padding = new Padding(0, 0, 0, 10) };
        var box = new TextBox
        {
            Text = initial, Dock = DockStyle.Fill, Multiline = multiline, ReadOnly = readOnly, BackColor = Theme.Field, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, Font = multiline ? Theme.Mono : Theme.Body, WordWrap = true
        };
        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 12), BackColor = Color.Transparent };
        holder.Controls.Add(box);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
        var ok = Theme.Button(okText, true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.Button(Core.L.T("Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.Add(ok);
        if (!readOnly) buttons.Controls.Add(cancel);
        f.Controls.Add(holder);
        f.Controls.Add(label);
        f.Controls.Add(buttons);
        f.AcceptButton = multiline ? null : ok;
        f.CancelButton = readOnly ? ok : cancel;
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
