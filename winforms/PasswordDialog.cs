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

    /// <summary>
    /// 새 압축's 암호 설정: the new password twice, 암호 표시, in the viewer's theme. Returns the password, "" when the
    /// set one is removed (암호 지우기), null when cancelled.
    /// </summary>
    public static string? AskNew(Form owner, Theme theme, string hint, bool hasPassword)
    {
        float s = owner.DeviceDpi / 96f;
        using var form = new Form
        {
            Text = "암호 설정 - MxZip",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi,
            Padding = new Padding((int)(12 * s)),
            Font = owner.Font,
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        Label Field(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, (int)(4 * s), (int)(10 * s), (int)(4 * s)),
        };
        var box = new TextBox { UseSystemPasswordChar = true, Width = (int)(260 * s), AccessibleName = "암호" };
        var again = new TextBox { UseSystemPasswordChar = true, Width = (int)(260 * s), AccessibleName = "암호 확인" };
        var show = new CheckBox { Text = "암호 표시", AutoSize = true, AccessibleName = "암호 표시" };
        show.CheckedChanged += (_, _) => box.UseSystemPasswordChar = again.UseSystemPasswordChar = !show.Checked;
        layout.Controls.Add(Field("암호"), 0, 0);
        layout.Controls.Add(box, 1, 0);
        layout.Controls.Add(Field("암호 확인"), 0, 1);
        layout.Controls.Add(again, 1, 1);
        layout.Controls.Add(show, 1, 2);
        layout.Controls.Add(new Label
        {
            Text = hint,
            AutoSize = true,
            MaximumSize = new Size((int)(260 * s), 0),
            Tag = "muted",
            Margin = new Padding(3, (int)(2 * s), 3, (int)(8 * s)),
        }, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "확인", AutoSize = true, Tag = "primary" };
        string? answer = null;
        ok.Click += (_, _) =>
        {
            if (box.Text.Length == 0)
            {
                MessageBox.Show(form, "암호를 입력하세요.", "암호 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                box.Focus();
                return;
            }
            if (!string.Equals(box.Text, again.Text, StringComparison.Ordinal))
            {
                MessageBox.Show(form, "암호와 암호 확인이 서로 다릅니다. 다시 입력하세요.", "암호 설정", MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                again.Focus();
                again.SelectAll();
                return;
            }
            answer = box.Text;
            form.DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        if (hasPassword)
        {
            var clear = new Button { Text = "암호 지우기", AutoSize = true };
            clear.Click += (_, _) =>
            {
                answer = "";
                form.DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(clear);
        }
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        DialogStyle.Apply(theme, form);
        form.HandleCreated += (_, _) => theme.ApplyWindow(form);
        return form.ShowDialog(owner) == DialogResult.OK ? answer : null;
    }
}
