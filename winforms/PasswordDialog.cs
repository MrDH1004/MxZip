namespace MxZip.Viewer;

/// <summary>Asks for the archive's password; null when cancelled.</summary>
static class PasswordDialog
{
    static Form NewForm(Form owner, string caption, float s) => new()
    {
        Text = caption,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterParent,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowInTaskbar = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        AutoScaleMode = AutoScaleMode.Dpi,
        Padding = new Padding((int)(20 * s), (int)(16 * s), (int)(20 * s), (int)(16 * s)),
        Font = owner.Font,
    };

    /// <summary>The bottom-right button row: the primary button first, then 취소.</summary>
    static FlowLayoutPanel ButtonRow(float s) => new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.RightToLeft,
        Dock = DockStyle.Fill,
        WrapContents = false,
        Margin = new Padding(0, (int)(20 * s), 0, 0),
        Padding = new Padding(0),
    };

    public static string? Ask(Form owner, Theme theme, string archiveName, bool retry)
    {
        float s = owner.DeviceDpi / 96f;
        using var form = NewForm(owner, "암호 입력 - MxZip", s);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        layout.Controls.Add(new TextLabel
        {
            AutoSize = true,
            MaximumSize = new Size((int)(360 * s), 0),
            Text = (retry ? "암호가 틀렸습니다. 다시 입력하세요.\n" : "") + $"'{archiveName}'의 암호를 입력하세요.",
            Margin = new Padding(0, 0, 0, (int)(12 * s)),
        });
        var box = new TextBox { UseSystemPasswordChar = true, AccessibleName = "암호" };
        var frame = new InputFrame(theme, box) { Width = (int)(360 * s), Margin = new Padding(0) };
        frame.Fit(owner.DeviceDpi);
        layout.Controls.Add(frame);
        var buttons = ButtonRow(s);
        var cancel = new ActionButton(theme, "취소") { DialogResult = DialogResult.Cancel, MinWidth = 80 };
        var ok = new ActionButton(theme, "확인", ButtonKind.Primary) { DialogResult = DialogResult.OK, MinWidth = 80, Margin = new Padding(0, 0, (int)(8 * s), 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        DialogStyle.Apply(theme, form);
        form.HandleCreated += (_, _) => theme.ApplyWindow(form);
        form.Shown += (_, _) => box.Focus();
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Length > 0 ? box.Text : null;
    }

    /// <summary>
    /// 새 압축's 암호 설정: the new password twice, 암호 표시, in the viewer's theme. Returns the password, "" when the
    /// set one is removed (암호 지우기), null when cancelled.
    /// </summary>
    public static string? AskNew(Form owner, Theme theme, string hint, bool hasPassword)
    {
        float s = owner.DeviceDpi / 96f;
        using var form = NewForm(owner, "암호 설정 - MxZip", s);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        TextLabel Field(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, (int)(4 * s), (int)(16 * s), (int)(4 * s)),
        };
        InputFrame Input(TextBox box)
        {
            var f = new InputFrame(theme, box) { Width = (int)(260 * s), Margin = new Padding(0, (int)(4 * s), 0, (int)(4 * s)) };
            f.Fit(owner.DeviceDpi);
            return f;
        }
        var box = new TextBox { UseSystemPasswordChar = true, AccessibleName = "암호" };
        var again = new TextBox { UseSystemPasswordChar = true, AccessibleName = "암호 확인" };
        var show = new ThemedCheck(theme, "암호 표시") { Margin = new Padding(0, (int)(4 * s), 0, 0) };
        show.CheckedChanged += (_, _) => box.UseSystemPasswordChar = again.UseSystemPasswordChar = !show.Checked;
        layout.Controls.Add(Field("암호"), 0, 0);
        layout.Controls.Add(Input(box), 1, 0);
        layout.Controls.Add(Field("암호 확인"), 0, 1);
        layout.Controls.Add(Input(again), 1, 1);
        layout.Controls.Add(show, 1, 2);
        layout.Controls.Add(new TextLabel
        {
            Text = hint,
            AutoSize = true,
            MaximumSize = new Size((int)(260 * s), 0),
            Tag = "muted",
            Margin = new Padding(0, (int)(4 * s), 0, 0),
        }, 1, 3);
        var buttons = ButtonRow(s);
        var cancel = new ActionButton(theme, "취소") { DialogResult = DialogResult.Cancel, MinWidth = 80 };
        var ok = new ActionButton(theme, "확인", ButtonKind.Primary) { MinWidth = 80, Margin = new Padding(0, 0, (int)(8 * s), 0) };
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
                MessageBox.Show(form, "암호가 서로 다릅니다.", "암호 설정", MessageBoxButtons.OK,
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
            var clear = new ActionButton(theme, "암호 지우기") { Margin = new Padding(0, 0, (int)(8 * s), 0) };
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
        form.Shown += (_, _) => box.Focus();
        return form.ShowDialog(owner) == DialogResult.OK ? answer : null;
    }
}
