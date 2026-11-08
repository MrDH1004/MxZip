using System.Drawing.Drawing2D;

namespace MxZip.Viewer;

/// <summary>
/// A toolbar button: icon above its label, flat, theme colours. A real Button underneath, so it keeps keyboard
/// activation and the accessibility Invoke action.
/// </summary>
sealed class ToolButton : Button
{
    readonly Theme theme;
    readonly Icons.Painter painter;
    Bitmap? icon, iconDisabled;
    bool hover, down;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Emphasis { get; set; }

    public ToolButton(Theme theme, string label, Icons.Painter painter)
    {
        this.theme = theme;
        this.painter = painter;
        Text = label;
        AccessibleName = label;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = true;
        Margin = new Padding(2, 4, 2, 4);
        Cursor = Cursors.Hand;
    }

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        Size = new Size((int)(68 * s), (int)(58 * s));
        icon?.Dispose();
        iconDisabled?.Dispose();
        int px = (int)(24 * s);
        icon = Icons.Render(painter, px, Emphasis ? theme.Accent : theme.Text, theme.Accent);
        iconDisabled = Icons.Render(painter, px, theme.Disabled, theme.Disabled);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Toolbar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Enabled && (hover || down || Focused && ShowFocusCues))
        {
            using var b = new SolidBrush(down ? theme.Pressed : theme.Hover);
            using var path = RoundRect(new RectangleF(1, 1, Width - 2, Height - 2), 6 * DeviceDpi / 96f);
            g.FillPath(b, path);
        }
        var img = Enabled ? icon : iconDisabled;
        if (img != null)
            g.DrawImage(img, (Width - img.Width) / 2, (int)(8 * DeviceDpi / 96f));
        var color = !Enabled ? theme.Disabled : Emphasis ? theme.Accent : theme.Text;
        var rect = new Rectangle(0, (int)(36 * DeviceDpi / 96f), Width, (int)(18 * DeviceDpi / 96f));
        TextRenderer.DrawText(g, Text, Font, rect, color,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>A large rounded tile of the empty window ("압축 파일 열기", "새로 압축하기").</summary>
sealed class CardButton : Button
{
    readonly Theme theme;
    readonly Icons.Painter painter;
    Bitmap? icon;
    bool hover;

    public CardButton(Theme theme, string label, Icons.Painter painter)
    {
        this.theme = theme;
        this.painter = painter;
        Text = label;
        AccessibleName = label;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        Size = new Size((int)(140 * s), (int)(110 * s));
        icon?.Dispose();
        icon = Icons.Render(painter, (int)(32 * s), theme.Accent, theme.Accent);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using (var b = new SolidBrush(hover || Focused && ShowFocusCues ? theme.CardHover : theme.Card))
        using (var pen = new Pen(theme.Border))
        using (var path = ToolButton.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * s))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        if (icon != null)
            g.DrawImage(icon, (Width - icon.Width) / 2, (int)(24 * s));
        var rect = new Rectangle(0, (int)(68 * s), Width, (int)(22 * s));
        TextRenderer.DrawText(g, Text, Font, rect, theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>A thin coloured notice under the toolbar: a message, one action and a close button.</summary>
sealed class Banner : Panel
{
    public readonly LinkLabel Action = new() { AutoSize = true };
    public readonly Button CloseButton;
    readonly Label message = new() { AutoSize = true };

    public Banner(Theme theme, string text, string action)
    {
        BackColor = theme.BannerBack;
        Dock = DockStyle.Top;
        Padding = new Padding(10, 0, 10, 0);
        message.Text = text;
        message.ForeColor = theme.BannerText;
        Action.Text = action;
        Action.LinkColor = Action.ActiveLinkColor = theme.BannerText;
        Action.LinkBehavior = LinkBehavior.AlwaysUnderline;
        Action.AccessibleName = action;
        CloseButton = new Button
        {
            FlatStyle = FlatStyle.Flat,
            AccessibleName = "알림 닫기",
            Cursor = Cursors.Hand,
            BackColor = theme.BannerBack,
        };
        CloseButton.FlatAppearance.BorderSize = 0;
        CloseButton.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(theme.BannerBack, 0.05f);
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = theme.BannerBack,
        };
        flow.Controls.Add(CloseButton);
        flow.Controls.Add(message);
        flow.Controls.Add(Action);
        Controls.Add(flow);
        Tag = theme;
    }

    public void SetMessage(string text) => message.Text = text;

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        Height = (int)(34 * s);
        var theme = (Theme)Tag!;
        CloseButton.Size = new Size((int)(24 * s), (int)(24 * s));
        CloseButton.Image = Icons.Render(Icons.Close, (int)(14 * s), theme.BannerText, theme.BannerText);
        int pad = (Height - CloseButton.Height) / 2;
        CloseButton.Margin = new Padding(0, pad, (int)(6 * s), 0);
        int textPad = (Height - message.PreferredHeight) / 2;
        message.Margin = new Padding(0, textPad, (int)(8 * s), 0);
        Action.Margin = new Padding(0, textPad, 0, 0);
    }
}
