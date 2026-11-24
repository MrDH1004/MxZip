using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MxZip.Viewer;

/// <summary>
/// Text drawn by the viewer's own controls: GDI (TextRenderer) with the system's ClearType and hinting, as native
/// Windows controls draw, so owner-drawn text is as sharp and dark as the TextBoxes next to it. NoPadding keeps the
/// font's own spacing (GDI+ grey-scale anti-aliasing looked blurred at UI sizes).
/// </summary>
static class Typo
{
    static TextFormatFlags Flags(StringAlignment h, StringAlignment v, bool wrap, HotkeyPrefix prefix) =>
        TextFormatFlags.NoPadding |
        prefix switch { HotkeyPrefix.None => TextFormatFlags.NoPrefix, HotkeyPrefix.Hide => TextFormatFlags.HidePrefix, _ => 0 } |
        (wrap ? TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis) |
        h switch { StringAlignment.Center => TextFormatFlags.HorizontalCenter, StringAlignment.Far => TextFormatFlags.Right, _ => 0 } |
        v switch { StringAlignment.Center => TextFormatFlags.VerticalCenter, StringAlignment.Far => TextFormatFlags.Bottom, _ => 0 };

    public static void Draw(Graphics g, string text, Font font, RectangleF r, Color color,
                            StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center,
                            bool wrap = false, HotkeyPrefix prefix = HotkeyPrefix.None)
    {
        if (r.Width <= 0 || r.Height <= 0 || text.Length == 0)
            return;
        TextRenderer.DrawText(g, text, font, Rectangle.Round(r), color, Flags(h, v, wrap, prefix));
    }

    /// <summary>
    /// The text's size in pixels; maxWidth > 0 wraps it to that width. Measured at the process's DPI, as controls
    /// draw: on a monitor of another DPI, WinForms has already scaled the fonts.
    /// </summary>
    public static Size Measure(string text, Font font, int maxWidth = 0, HotkeyPrefix prefix = HotkeyPrefix.None)
    {
        var bounds = new Size(maxWidth > 0 ? maxWidth : int.MaxValue, int.MaxValue);
        var size = TextRenderer.MeasureText(text, font, bounds,
                                            Flags(StringAlignment.Near, StringAlignment.Near, maxWidth > 0, prefix));
        return new Size(size.Width + 1, Math.Max(size.Height, font.Height));
    }

    /// <summary>A rounded rectangle path.</summary>
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// The look shared by the toolbar's icon buttons and toggles: a quiet line icon on a square hit target, a filled
/// tile on hover / press and while a toggle is on, and a 2 px accent ring for keyboard focus. The label is the
/// control's Text and AccessibleName (screen readers, UI automation) and its tooltip; it is not drawn.
/// </summary>
sealed class ToolFace
{
    readonly Control owner;
    readonly Theme theme;
    readonly Icons.Painter painter;
    Bitmap? icon, iconOn, iconDisabled;
    public bool Hover, Down;
    public int Box = 32, IconSize = 18;  // at 96 dpi

    public ToolFace(Control owner, Theme theme, Icons.Painter painter)
    {
        this.owner = owner;
        this.theme = theme;
        this.painter = painter;
    }

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        owner.Size = new Size((int)(Box * s), (int)(Box * s));
        icon?.Dispose();
        iconOn?.Dispose();
        iconDisabled?.Dispose();
        int px = (int)(IconSize * s);
        icon = Icons.Render(painter, px, theme.Secondary, theme.Accent);
        iconOn = Icons.Render(painter, px, theme.Accent, theme.Accent);
        iconDisabled = Icons.Render(painter, px, theme.Disabled, theme.Disabled);
        owner.Invalidate();
    }

    public void Paint(Graphics g, bool on, bool focusCue)
    {
        g.Clear(owner.Parent?.BackColor ?? theme.Toolbar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = owner.DeviceDpi / 96f;
        var tile = new RectangleF(0, 0, owner.Width - 1, owner.Height - 1);
        bool enabled = owner.Enabled;
        if (enabled && (Hover || Down || on))  // a toggle that is on keeps a filled tile: a shape, not only a colour
        {
            using var b = new SolidBrush(Down ? theme.Pressed : on && !Hover ? theme.Selection : theme.Hover);
            using var path = Typo.RoundRect(tile, 6 * s);
            g.FillPath(b, path);
        }
        if (enabled && focusCue)
        {
            using var ring = new Pen(theme.Accent, Math.Max(2f, 2 * s));
            using var path = Typo.RoundRect(RectangleF.Inflate(tile, -ring.Width / 2, -ring.Width / 2), 6 * s);
            g.DrawPath(ring, path);
        }
        var img = enabled ? (on ? iconOn : icon) : iconDisabled;
        if (img != null)
            g.DrawImage(img, (owner.Width - img.Width) / 2, (owner.Height - img.Height) / 2, img.Width, img.Height);
    }

    public static void Setup(ButtonBase b, string label)
    {
        b.Text = label;
        b.AccessibleName = label;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.TabStop = true;
        b.Margin = new Padding(0);
        b.Cursor = Cursors.Hand;
    }
}

/// <summary>An icon-only toolbar button. A real Button underneath: keyboard activation and the accessibility Invoke action.</summary>
sealed class ToolButton : Button
{
    readonly ToolFace face;

    public ToolButton(Theme theme, string label, Icons.Painter painter, int box = 32, int iconSize = 18)
    {
        face = new ToolFace(this, theme, painter) { Box = box, IconSize = iconSize };
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
}

/// <summary>
/// An icon-only toolbar toggle (전체 보기, 다크 모드). A real CheckBox underneath: Space toggles it and assistive
/// technology gets a check box with its on / off state and the Toggle action. The click handler decides the new
/// state (AutoCheck off), so the state always follows what the window really shows.
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

enum ButtonKind { Secondary, Primary, Link }

/// <summary>
/// The text button of the toolbar and the dialogs. Primary: accent fill (one per view: 풀기, 압축 시작, 확인);
/// Secondary: card fill with a hairline; Link: accent text only (the notice strip). Radius 6, 32 px tall, 16 px
/// side padding, hover / press shades, a 2 px focus ring, a disabled look. A real Button underneath: AcceptButton,
/// DialogResult, Alt mnemonics (&amp;) and the accessibility Invoke action work as usual.
/// </summary>
sealed class ActionButton : Button
{
    readonly Theme theme;
    readonly Icons.Painter? painter;
    Bitmap? icon;
    Color iconColor;
    bool hover, down;
    Size? textSize;  // measured once per text / font

    public ButtonKind Kind { get; }

    /// <summary>A small chevron after the text: the button opens a menu (풀기).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DropDown { get; init; }

    /// <summary>The narrowest width at 96 dpi (equal-width button columns).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MinWidth { get; set; }

    public ActionButton(Theme theme, string text, ButtonKind kind = ButtonKind.Secondary, Icons.Painter? painter = null)
    {
        this.theme = theme;
        this.painter = painter;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Text = text;
        AccessibleName = text.Replace("&", "", StringComparison.Ordinal).Replace("...", "", StringComparison.Ordinal);
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    float S => DeviceDpi / 96f;

    Size TextSize => textSize ??= Typo.Measure(Text, Font, 0, HotkeyPrefix.Hide);

    public override Size GetPreferredSize(Size proposedSize)
    {
        float s = S;
        var text = TextSize;
        int pad = (int)((Kind == ButtonKind.Link ? 4 : 16) * s);
        int w = pad * 2 + text.Width;
        if (painter != null)
            w += (int)(24 * s);
        if (DropDown)
            w += (int)(16 * s);
        int h = Math.Max((int)((Kind == ButtonKind.Link ? 24 : 32) * s), text.Height + (int)((Kind == ButtonKind.Link ? 4 : 12) * s));
        return new Size(Math.Max(w, (int)(MinWidth * s)), h);
    }

    /// <summary>Sizes the button for this dpi (the parent calls it from its own DPI fitting).</summary>
    public void Fit() => Size = GetPreferredSize(Size.Empty);

    protected override void OnTextChanged(EventArgs e) { textSize = null; base.OnTextChanged(e); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { textSize = null; base.OnFontChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = S;
        bool enabled = Enabled;
        bool focus = Focused && ShowFocusCues;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = 6 * s;
        Color fore;
        if (Kind == ButtonKind.Primary)
        {
            Color fill = !enabled ? theme.Panel : down ? theme.AccentPressed : hover ? theme.AccentHover : theme.Accent;
            using (var b = new SolidBrush(fill))
            using (var path = Typo.RoundRect(r, radius))
                g.FillPath(b, path);
            fore = enabled ? theme.AccentText : theme.Disabled;
        }
        else if (Kind == ButtonKind.Secondary)
        {
            Color fill = !enabled ? theme.Card : down ? theme.Pressed : hover ? theme.Hover : theme.Card;
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(enabled ? theme.Pressed : theme.Border))
            using (var path = Typo.RoundRect(r, radius))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            fore = enabled ? theme.Text : theme.Disabled;
        }
        else
        {
            fore = enabled ? (down ? theme.AccentPressed : theme.Accent) : theme.Disabled;
        }
        if (focus && enabled)
        {
            float w = Math.Max(2f, 2 * s);
            using var ring = new Pen(theme.Accent, w);
            var fr = RectangleF.Inflate(new RectangleF(0, 0, Width, Height), -w / 2, -w / 2);
            using (var path = Typo.RoundRect(fr, radius))
                g.DrawPath(ring, path);
            if (Kind == ButtonKind.Primary)  // a light inner line keeps the ring visible against the accent fill
            {
                using var inner = new Pen(theme.AccentText, Math.Max(1f, s));
                using var ip = Typo.RoundRect(RectangleF.Inflate(fr, -w, -w), Math.Max(1, radius - w));
                g.DrawPath(inner, ip);
            }
        }

        var text = TextSize;
        int iconW = painter != null ? (int)(24 * s) : 0, chevronW = DropDown ? (int)(16 * s) : 0;
        int content = iconW + text.Width + chevronW;
        int x = Math.Max((int)(4 * s), (Width - content) / 2);
        if (painter != null)
        {
            if (icon == null || iconColor != fore || icon.Width != (int)(16 * s))
            {
                icon?.Dispose();
                icon = Icons.Render(painter, (int)(16 * s), fore, fore);
                iconColor = fore;
            }
            g.DrawImage(icon, x, (Height - icon.Height) / 2, icon.Width, icon.Height);
            x += iconW;
        }
        var prefix = ShowKeyboardCues ? HotkeyPrefix.Show : HotkeyPrefix.Hide;
        var tr = new RectangleF(x, 0, Math.Min(text.Width + 2, Width - x), Height);
        Typo.Draw(g, Text, Font, tr, fore, StringAlignment.Near, StringAlignment.Center, false, prefix);
        if (Kind == ButtonKind.Link && enabled && (hover || focus))
        {
            using var line = new Pen(fore, Math.Max(1f, s));
            float y = Height / 2f + text.Height / 2f - s;
            g.DrawLine(line, x, y, x + text.Width - 1, y);
        }
        if (DropDown)
        {
            float cx = x + text.Width + 10 * s, cy = Height / 2f + s;
            using var pen = new Pen(fore, Math.Max(1.4f, 1.5f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new PointF[] { new(cx - 3.5f * s, cy - 2 * s), new(cx, cy + 1.5f * s), new(cx + 3.5f * s, cy - 2 * s) });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            icon?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A label whose text is drawn by <see cref="Typo"/> (natural Hangul spacing). AutoSize measures the same way;
/// MaximumSize.Width wraps it.
/// </summary>
sealed class TextLabel : Label
{
    public TextLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        UseMnemonic = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int max = MaximumSize.Width > 0 ? MaximumSize.Width - Padding.Horizontal : 0;
        var size = Typo.Measure(Text, Font, max);
        return new Size(size.Width + Padding.Horizontal, size.Height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new RectangleF(Padding.Left, Padding.Top, ClientSize.Width - Padding.Horizontal, ClientSize.Height - Padding.Vertical);
        var a = TextAlign;
        var h = a is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? StringAlignment.Far
              : a is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? StringAlignment.Center
              : StringAlignment.Near;
        var v = a is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? StringAlignment.Center
              : a is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? StringAlignment.Far
              : StringAlignment.Near;
        bool wrap = !AutoEllipsis && (!AutoSize || MaximumSize.Width > 0);
        Typo.Draw(e.Graphics, Text, Font, r, Enabled ? ForeColor : SystemColors.GrayText, h, v, wrap);
    }
}

/// <summary>A check box drawn in the palette: a rounded box (accent with a check mark when on) and Typo text.</summary>
sealed class ThemedCheck : CheckBox
{
    readonly Theme theme;
    bool hover;

    public ThemedCheck(Theme theme, string text)
    {
        this.theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = text;
        AutoSize = true;
        AccessibleName = text.Replace("&", "", StringComparison.Ordinal);
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float s = DeviceDpi / 96f;
        var t = Typo.Measure(Text, Font, 0, HotkeyPrefix.Hide);
        return new Size((int)(24 * s) + t.Width + (int)(4 * s), Math.Max((int)(28 * s), t.Height + (int)(6 * s)));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float box = 16 * s;
        var r = new RectangleF(1 * s, (Height - box) / 2f, box, box);
        using (var path = Typo.RoundRect(r, 4 * s))
        {
            if (Checked && Enabled)
            {
                using var b = new SolidBrush(hover ? theme.AccentHover : theme.Accent);
                g.FillPath(b, path);
                using var pen = new Pen(theme.AccentText, Math.Max(1.5f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new PointF[] { new(r.X + 4 * s, r.Y + 8.2f * s), new(r.X + 7 * s, r.Y + 11 * s), new(r.X + 12 * s, r.Y + 5 * s) });
            }
            else
            {
                using var b = new SolidBrush(hover && Enabled ? theme.Hover : theme.Card);
                g.FillPath(b, path);
                using var pen = new Pen(Enabled ? theme.Muted : theme.Disabled, Math.Max(1f, s));
                g.DrawPath(pen, path);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(theme.Accent, Math.Max(2f, 2 * s));
            using var fp = Typo.RoundRect(RectangleF.Inflate(r, 2.5f * s, 2.5f * s), 6 * s);
            g.DrawPath(ring, fp);
        }
        var tr = new RectangleF(24 * s, 0, Width - 24 * s, Height);
        Typo.Draw(g, Text, Font, tr, Enabled ? theme.Text : theme.Disabled, StringAlignment.Near, StringAlignment.Center, false,
                  ShowKeyboardCues ? HotkeyPrefix.Show : HotkeyPrefix.Hide);
    }
}

/// <summary>
/// A flat text field: the TextBox has no border of its own and sits in this frame, which draws a rounded hairline
/// (accent while the box has focus) in the palette. 32 px tall like the buttons.
/// </summary>
sealed class InputFrame : Panel
{
    readonly Theme theme;
    public TextBox Box { get; }

    public InputFrame(Theme theme, TextBox box)
    {
        this.theme = theme;
        Box = box;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        box.BorderStyle = BorderStyle.None;
        box.Dock = DockStyle.Fill;
        box.Enter += (_, _) => Invalidate();
        box.Leave += (_, _) => Invalidate();
        Controls.Add(box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => box.Focus();
        box.FontChanged += (_, _) => Fit(DeviceDpi);
    }

    /// <summary>
    /// 32 px tall (taller when a large font needs it), the box centred. Runs again whenever the font reaches the
    /// box (once the frame is in its dialog, the dialog's font) and on a DPI change.
    /// </summary>
    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        int box = Box.PreferredHeight;
        Height = Math.Max((int)(32 * s), box + (int)(8 * s));
        int top = Math.Max(1, (Height - box) / 2);
        Padding = new Padding((int)(8 * s), top, (int)(8 * s), 1);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Fit(DeviceDpi);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        Fit(DeviceDpi);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Fit(DeviceDpi);
    }

    public void ApplyTheme()
    {
        BackColor = theme.Card;
        Box.BackColor = theme.Card;
        Box.ForeColor = theme.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        bool focus = Box.Focused;
        float w = focus ? Math.Max(1.5f, 1.5f * s) : 1f;
        var r = RectangleF.Inflate(new RectangleF(0, 0, Width, Height), -w / 2 - 0.5f, -w / 2 - 0.5f);
        using var path = Typo.RoundRect(r, 6 * s);
        using (var b = new SolidBrush(theme.Card))
            g.FillPath(b, path);
        using var pen = new Pen(focus ? theme.Accent : (Enabled ? theme.Pressed : theme.Border), w);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// The format tag, MxZip's signature: a small rounded label with the format in a monospaced face ("ZIP", "7Z").
/// Used only in the archive summary and for the format choice of 새 압축.
/// </summary>
static class FormatTag
{
    static string? family;

    /// <summary>Cascadia Mono where installed, else Consolas.</summary>
    public static Font MonoFont(float size)
    {
        if (family == null)
        {
            using var probe = new Font("Cascadia Mono", 9f);
            family = string.Equals(probe.Name, "Cascadia Mono", StringComparison.OrdinalIgnoreCase) ? "Cascadia Mono" : "Consolas";
        }
        return new Font(family, size);
    }

    public static Size Measure(string text, Font mono, int dpi)
    {
        float s = dpi / 96f;
        var t = Typo.Measure(text, mono);
        return new Size(t.Width + (int)(12 * s), (int)(20 * s));
    }

    /// <summary>Paints the tag with its top-left corner at x, y; returns its size.</summary>
    public static Size Paint(Graphics g, string text, Font mono, Theme theme, int x, int y, int dpi, bool selected = false,
                             bool enabled = true)
    {
        float s = dpi / 96f;
        var size = Measure(text, mono, dpi);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(x + 0.5f, y + 0.5f, size.Width - 1, size.Height - 1);
        using (var path = Typo.RoundRect(r, 4 * s))
        using (var b = new SolidBrush(selected ? theme.Card : theme.Panel))
        using (var pen = new Pen(theme.Pressed))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = mode;
        Typo.Draw(g, text, mono, new RectangleF(x, y, size.Width, size.Height), enabled ? theme.Text : theme.Disabled,
                  StringAlignment.Center);
        return size;
    }
}

/// <summary>
/// The open archive's header: the file name with its format tag, "파일 N개 · 원본 X · 압축 후 Y", a thin bar of the
/// packed share of the original size, quiet notes (password, damaged, read-only), and below a hairline the
/// folder navigation (Crumbs: the 위로 button and where in the archive the list is).
/// </summary>
sealed class SummaryCard : Panel
{
    readonly Theme theme;
    Font? nameFont, mono;
    public readonly Panel Crumbs = new() { Dock = DockStyle.Bottom };
    string fileName = "", format = "", stats = "", share = "", notes = "";
    double ratio = -1;

    public SummaryCard(Theme theme)
    {
        this.theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(Crumbs);
    }

    /// <summary>ratio: packed / original (0..1), or below 0 when unknown; share: its words ("원본의 38.0%").</summary>
    public void Show(string fileName, string format, string stats, double ratio, string share, string notes)
    {
        this.fileName = fileName;
        this.format = format;
        this.stats = stats;
        this.ratio = ratio;
        this.share = share;
        this.notes = notes;
        AccessibleName = fileName;
        AccessibleDescription = string.Join(" · ", new[] { format, stats, share, notes }.Where(t => t.Length > 0));
        Invalidate();
    }

    float S => DeviceDpi / 96f;

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        nameFont?.Dispose();
        mono?.Dispose();
        nameFont = new Font(Font.FontFamily, Font.Size * 1.4f);
        mono = FormatTag.MonoFont(Math.Max(7.5f, Font.Size * 0.9f));
        Crumbs.Height = (int)(36 * s);
        Padding = new Padding((int)(4 * s), 0, (int)(4 * s), (int)(4 * s));
        Height = HeaderHeight + 1 + Crumbs.Height + Padding.Bottom;
        Invalidate();
    }

    int LineHeight => Typo.Measure("가", Font).Height;

    int HeaderHeight
    {
        get
        {
            float s = S;
            int name = nameFont != null ? Typo.Measure("가", nameFont).Height : (int)(26 * s);
            return (int)(16 * s) + Math.Max(name, (int)(22 * s)) + (int)(6 * s) + LineHeight + (int)(10 * s) +
                   Math.Max(LineHeight, (int)(8 * s)) + (int)(12 * s);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Fit(DeviceDpi);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = S;
        var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Typo.RoundRect(card, 8 * s))
        using (var b = new SolidBrush(theme.Card))
        using (var pen = new Pen(theme.Border))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        if (nameFont == null || mono == null)
            return;
        int pad = (int)(16 * s);
        int y = (int)(16 * s);
        int nameH = Math.Max(Typo.Measure("가", nameFont).Height, (int)(22 * s));
        int x = pad;
        if (format.Length > 0)
        {
            var tag = FormatTag.Measure(format, mono, DeviceDpi);
            FormatTag.Paint(g, format, mono, theme, x, y + (nameH - tag.Height) / 2, DeviceDpi);
            x += tag.Width + (int)(10 * s);
        }
        Typo.Draw(g, fileName, nameFont, new RectangleF(x, y, Width - x - pad, nameH), theme.Text);
        y += nameH + (int)(6 * s);
        int line = LineHeight;
        Typo.Draw(g, stats, Font, new RectangleF(pad, y, Width - pad * 2, line), theme.Secondary);
        y += line + (int)(10 * s);
        int rowH = Math.Max(line, (int)(8 * s));
        x = pad;
        if (ratio >= 0)
        {
            int barW = (int)(200 * s), barH = Math.Max(4, (int)(4 * s));
            float by = y + (rowH - barH) / 2f;
            using (var track = new SolidBrush(theme.Track))
            using (var tp = Typo.RoundRect(new RectangleF(x, by, barW, barH), barH / 2f))
                g.FillPath(track, tp);
            float fill = (float)Math.Clamp(ratio, 0, 1) * barW;
            if (fill >= 1)
                using (var accent = new SolidBrush(theme.Accent))
                using (var fp = Typo.RoundRect(new RectangleF(x, by, Math.Max(fill, barH), barH), barH / 2f))
                    g.FillPath(accent, fp);
            x += barW + (int)(10 * s);
        }
        string tail = string.Join("  ·  ", new[] { share, notes }.Where(t => t.Length > 0));
        Typo.Draw(g, tail, Font, new RectangleF(x, y, Width - x - pad, rowH), theme.Muted);
        y = Height - Padding.Bottom - Crumbs.Height - 1;  // the hairline just above the navigation row
        using (var hair = new Pen(theme.Border))
            g.DrawLine(hair, 1, y, Width - 2, y);
    }

    public void ApplyTheme()
    {
        Crumbs.BackColor = theme.Card;
        foreach (Control c in Crumbs.Controls)
            c.BackColor = theme.Card;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            nameFont?.Dispose();
            mono?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A quiet notice strip under the toolbar: a message, one link action and a small close button.</summary>
sealed class Banner : Panel
{
    public readonly ActionButton Action;
    public readonly ToolButton CloseButton;
    readonly TextLabel message = new() { AutoSize = false, AutoEllipsis = true };
    readonly Theme theme;

    public Banner(Theme theme, string text, string action)
    {
        this.theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        message.Text = text;
        Action = new ActionButton(theme, action, ButtonKind.Link);
        CloseButton = new ToolButton(theme, "알림 닫기", Icons.Close, box: 24, iconSize: 14);
        Controls.Add(message);
        Controls.Add(Action);
        Controls.Add(CloseButton);
        ApplyTheme();
    }

    bool actionShown = true;

    public void SetMessage(string text)
    {
        message.Text = text;
        PerformLayout();
    }

    /// <summary>Takes the action away (its message now says what is happening).</summary>
    public void HideAction()
    {
        actionShown = false;
        Action.Visible = false;
        PerformLayout();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        PerformLayout();
    }

    /// <summary>Re-colours the notice after a dark / light switch (the shared Theme already holds the new palette).</summary>
    public void ApplyTheme()
    {
        BackColor = message.BackColor = theme.BannerBack;
        message.ForeColor = theme.BannerText;
        Fit(DeviceDpi);  // the close icon is drawn in the palette
        Invalidate(true);
    }

    public void Fit(int dpi)
    {
        float s = dpi / 96f;
        Height = Math.Max((int)(36 * s), message.PreferredSize.Height + (int)(12 * s));
        CloseButton.Fit(dpi);
        Action.Fit();
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        float s = DeviceDpi / 96f;
        int pad = (int)(16 * s);
        int gap = (int)(8 * s);
        CloseButton.Location = new Point(Width - pad + (int)(6 * s) - CloseButton.Width, (Height - CloseButton.Height) / 2);
        // a long message is cut with an ellipsis: the action and the close button always stay in view
        int room = CloseButton.Left - gap - pad - (actionShown ? Action.Width + gap : 0);
        var m = message.PreferredSize;
        message.Bounds = new Rectangle(pad, (Height - m.Height) / 2, Math.Max(0, Math.Min(m.Width, room)), m.Height);
        Action.Location = new Point(message.Right + gap, (Height - Action.Height) / 2);
        CloseButton.BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(theme.BannerBack);
        using var pen = new Pen(theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>The status bar's progress: a thin rounded bar, the accent share on a neutral track.</summary>
sealed class ThinProgress : Control
{
    readonly Theme theme;
    int value;

    public ThinProgress(Theme theme)
    {
        this.theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "진행률";
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ProgressAccessible(this);

    /// <summary>Screen readers read the bar's value ("42%").</summary>
    sealed class ProgressAccessible : ControlAccessibleObject
    {
        readonly ThinProgress owner;

        public ProgressAccessible(ThinProgress owner) : base(owner) => this.owner = owner;

        public override AccessibleRole Role => AccessibleRole.ProgressBar;

        public override string? Value
        {
            get => $"{owner.value}%";
            set { }
        }
    }

    /// <summary>0 to 100.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (v == this.value)
                return;
            this.value = v;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Math.Max(4f, 4 * DeviceDpi / 96f);
        var track = new RectangleF(0, (Height - h) / 2f, Width - 1, h);
        using (var b = new SolidBrush(theme.Track))
        using (var p = Typo.RoundRect(track, h / 2))
            g.FillPath(b, p);
        if (value <= 0)
            return;
        using (var b = new SolidBrush(theme.Accent))
        using (var p = Typo.RoundRect(new RectangleF(track.X, track.Y, Math.Max(h, track.Width * value / 100f), h), h / 2))
            g.FillPath(b, p);
    }
}
