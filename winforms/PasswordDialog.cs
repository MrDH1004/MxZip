namespace MxZip.Viewer;

/// <summary>Asks for the archive's password; null when cancelled.</summary>
static class PasswordDialog
{
    public static string? Ask(IWin32Window owner, string archiveName, bool retry)
    {
        using var form = new Form
        {
            Text = "암호 입력 - MxZip",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Font = new Font("맑은 고딕", 9F),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = (retry ? "암호가 틀렸습니다. 다시 입력하세요.\n" : "") + $"'{archiveName}'의 암호를 입력하세요.",
        });
        var box = new TextBox { UseSystemPasswordChar = true, Width = 360, AccessibleName = "암호" };
        layout.Controls.Add(box);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Length > 0 ? box.Text : null;
    }
}
