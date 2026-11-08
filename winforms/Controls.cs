using System.Drawing.Drawing2D;

namespace MxZip.Viewer;

/// <summary>
/// The look shared by toolbar buttons and toggles: icon above its label, flat, theme colours, a filled tile while
/// a toggle is on, and a 2 px accent ring for keyboard focus (at least 3:1 against the toolbar in both themes).
/// </summary>
sealed class ToolFace
{
    readonly Control owner;
    readonly Theme theme;
    readonly Icons.Painter painter;
    Bitmap? icon, iconOn, iconDisabled;
    public bool Hover, Down, Emphasis;

    public ToolFace(Control owner, Theme theme, Icons.Painter painter)
    {
        this.owner = owner;
        this.theme = theme;
        this.painter = painter;
    }

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        owner.Size = new Size((int)(68 * s), (int)(58 * s));
        icon?.Dispose();
        iconOn?.Dispose();
        iconDisabled?.Dispose();
        int px = (int)(24 * s);
        icon = Icons.Render(painter, px, Emphasis ? theme.Accent : theme.Text, theme.Accent);
        iconOn = Icons.Render(painter, px, theme.Accent, theme.Accent);
        iconDisabled = Icons.Render(painter, px, theme.Disabled, theme.Disabled);
        owner.Invalidate();
    }

    public void Paint(Graphics g, bool on, bool focusCue)
    {
        g.Clear(owner.Parent?.BackColor ?? theme.Toolbar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = owner.DeviceDpi / 96f;
        var tile = new RectangleF(1, 1, owner.Width - 2, owner.Height - 2);
        bool enabled = owner.Enabled;
        if (enabled && (Hover || Down || on))  // a toggle that is on keeps a filled tile: a shape, not only a colour
        {
            using var b = new SolidBrush(Down ? theme.Pressed : Hover ? theme.Hover : theme.Selection);
            using var path = ToolButton.RoundRect(tile, 6 * s);
            g.FillPath(b, path);
        }
        if (enabled && focusCue)
        {
            using var ring = new Pen(theme.Accent, Math.Max(2f, 2 * s));
            using var path = ToolButton.RoundRect(RectangleF.Inflate(tile, -ring.Width / 2, -ring.Width / 2), 6 * s);
            g.DrawPath(ring, path);
        }
        var img = enabled ? (on ? iconOn : icon) : iconDisabled;
        if (img != null)
            g.DrawImage(img, (owner.Width - img.Width) / 2, (int)(8 * s));
        var color = !enabled ? theme.Disabled : Emphasis || on ? theme.Accent : theme.Text;
        var rect = new Rectangle(0, (int)(36 * s), owner.Width, (int)(18 * s));
        TextRenderer.DrawText(g, owner.Text, owner.Font, rect, color,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public static void Setup(ButtonBase b, string label)
    {
        b.Text = label;
        b.AccessibleName = label;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.TabStop = true;
        b.Margin = new Padding(2, 4, 2, 4);
        b.Cursor = Cursors.Hand;
    }
}

/// <summary>A toolbar button. A real Button underneath: keyboard activation and the accessibility Invoke action.</summary>
sealed class ToolButton : Button
{
    readonly ToolFace face;

    /// <summary>The accent label and icon of the view's primary action (새로 압축).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Emphasis { get => face.Emphasis; set => face.Emphasis = value; }

    public ToolButton(Theme theme, string label, Icons.Painter painter)
    {
        face = new ToolFace(this, theme, painter);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ToolFace.Setup(this, label);
    }

    public void Fit(int dpi) => face.Fit(dpi);

    protected override void OnMouseEnter(EventArgs e) { face.Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { face.Hover = face.Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { face.Down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { face.Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) => face.Paint(e.Graphics, false, Focused && ShowFocusCues);

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

/// <summary>
/// A toolbar toggle (전체 보기, 다크 모드). A real CheckBox underneath: Space toggles it and assistive technology
/// gets a check box with its on / off state and the Toggle action. The click handler decides the new state
/// (AutoCheck off), so the state always follows what the window really shows.
/// </summary>
sealed class ToolToggle : CheckBox
{
    readonly ToolFace face;

    public ToolToggle(Theme theme, string label, Icons.Painter painter)
    {
        face = new ToolFace(this, theme, painter);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Appearance = Appearance.Button;
        AutoCheck = false;
        ToolFace.Setup(this, label);
    }

    public void Fit(int dpi) => face.Fit(dpi);

    protected override void OnMouseEnter(EventArgs e) { face.Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { face.Hover = face.Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { face.Down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { face.Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnPaint(PaintEventArgs e) => face.Paint(e.Graphics, Checked, Focused && ShowFocusCues);
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
        bool focusRing = Focused && ShowFocusCues;  // keyboard focus: a 2 px accent ring instead of the border
        float w = focusRing ? Math.Max(2f, 2 * s) : 1f;
        using (var b = new SolidBrush(hover ? theme.CardHover : theme.Card))
        using (var pen = new Pen(focusRing ? theme.Accent : theme.Border, w))
        using (var path = ToolButton.RoundRect(new RectangleF(w / 2, w / 2, Width - w - 1, Height - w - 1), 8 * s))
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

    /// <summary>Re-colours the notice after a dark / light switch (the shared Theme already holds the new palette).</summary>
    public void ApplyTheme()
    {
        var theme = (Theme)Tag!;
        BackColor = theme.BannerBack;
        foreach (Control c in Controls)
            c.BackColor = theme.BannerBack;
        message.ForeColor = theme.BannerText;
        Action.LinkColor = Action.ActiveLinkColor = theme.BannerText;
        CloseButton.BackColor = theme.BannerBack;
        CloseButton.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(theme.BannerBack, 0.05f);
        Fit(DeviceDpi);  // the close icon is drawn in the banner's text colour
    }

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
