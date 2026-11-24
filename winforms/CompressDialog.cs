using System.Globalization;
using System.Text.RegularExpressions;

namespace MxZip.Viewer;

/// <summary>What the 새 압축 window asks the engine to make (--create-json). Level 0: store only.</summary>
sealed record CompressRequest(List<string> Paths, string Out, string Format, int Level, long SplitBytes,
                              string? Password, string Exclude, bool Test, bool Each, bool DeleteSource);

/// <summary>
/// A format the engine can create: formats.FORMATS in formats.py, entry for entry (id, label, extension, password,
/// split, single). Single formats (gz / bz2 / xz / zst) hold exactly one file. tzst / zst need the engine's zstd
/// support; without it the engine refuses them with a message.
/// </summary>
sealed record ArchiveFormat(string Id, string Label, string Ext, bool Password = false, bool Split = false,
                            bool Single = false)
{
    public static readonly ArchiveFormat[] All =
    {
        new("zip", "ZIP", ".zip", Password: true, Split: true),
        new("7z", "7Z", ".7z", Password: true),
        new("tar", "TAR (압축 안 함)", ".tar"),
        new("tgz", "TAR.GZ", ".tar.gz"),
        new("tbz", "TAR.BZ2", ".tar.bz2"),
        new("txz", "TAR.XZ", ".tar.xz"),
        new("tzst", "TAR.ZST", ".tar.zst"),
        new("gz", "GZ (파일 1개)", ".gz", Single: true),
        new("bz2", "BZ2 (파일 1개)", ".bz2", Single: true),
        new("xz", "XZ (파일 1개)", ".xz", Single: true),
        new("zst", "ZST (파일 1개)", ".zst", Single: true),
    };

    public override string ToString() => Label;
}

/// <summary>
/// 새 압축: the files to compress (추가 / 폴더 추가 / 제거, or dropped on the list) and the archive settings (name,
/// format, password, split, method, excluded names, test / delete afterwards, one archive per item). The job itself
/// runs in the viewer (engine --create-json) with its progress and 취소 in the status bar.
/// </summary>
sealed class CompressDialog : Form
{
    sealed record SplitChoice(string Label, long Bytes)
    {
        public override string ToString() => Label;
    }

    sealed record LevelChoice(string Label, int Level)
    {
        public override string ToString() => Label;
    }

    const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
    const long Custom = -1;

    // the engine's own units (appcore.split_bytes: 1 MB = 1024 KB); FAT32 holds files up to 4 GB - 1 byte, a DVD
    // 4.7 billion bytes
    static readonly SplitChoice[] SplitChoices =
    {
        new("분할하지 않음", 0), new("5MB", 5 * MB), new("네이버 블로그(10MB)", 10 * MB), new("다음 블로그(10MB)", 10 * MB),
        new("다음 메일(25MB)", 25 * MB), new("티스토리(20MB)", 20 * MB), new("공직자통합메일(20MB)", 20 * MB),
        new("네이버 카페(50MB)", 50 * MB), new("25MB (Gmail)", 25 * MB), new("100MB", 100 * MB), new("300MB", 300 * MB),
        new("650MB (CD)", 650 * MB), new("700MB (CD)", 700 * MB), new("네이버 메일(1.95GB)", (long)(1.95 * GB)),
        new("다음 메일(4GB)", 4 * GB), new("카카오톡(300MB)", 300 * MB), new("1GB", GB), new("2GB", 2 * GB),
        new("4GB (FAT32)", 4 * GB - 1), new("4.7GB (DVD)", 4_700_000_000), new("직접 입력", Custom),
    };

    static readonly LevelChoice[] LevelChoices =
    {
        new("저장(압축 안 함)", 0), new("빠르게", 1), new("보통", 6), new("높게", 7), new("최고", 9),
    };

    const string DefaultExclude = ".tmp, Thumbs.db, .DS_Store";

    readonly Theme theme;
    readonly AppSettings settings;
    readonly ShellIcons icons = new();
    readonly CancellationTokenSource sizing = new();
    readonly HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
    readonly Font mono;
    string? password;
    bool nameEdited, settingPath;
    int lastFormat;

    readonly TableLayoutPanel root = new();
    readonly TableLayoutPanel filesRow = new();
    readonly Panel listFrame = new();
    readonly ThemedListView list = new();
    readonly ActionButton addButton, folderButton, removeButton;
    readonly TableLayoutPanel settingsPanel = new();
    readonly TextBox pathBox = new();
    readonly InputFrame pathFrame;
    readonly ActionButton browseButton;
    readonly ComboBox formatBox, splitBox, levelBox;
    readonly TextBox customSplit = new();
    readonly InputFrame customFrame;
    readonly ActionButton passwordButton;
    readonly TextLabel passwordHint = new();
    readonly ActionButton advancedButton;
    readonly TableLayoutPanel advancedPanel = new();
    readonly TextBox excludeBox = new();
    readonly InputFrame excludeFrame;
    readonly ThemedCheck testCheck, deleteCheck, eachCheck, detailsCheck;
    readonly ActionButton startButton, cancelButton;
    readonly List<TextLabel> fieldLabels = new();

    public CompressRequest? Request { get; private set; }

    /// <summary>The job to run, or null when cancelled. paths: already in the list (files dropped on the viewer).</summary>
    public static CompressRequest? Ask(Form owner, Theme theme, IEnumerable<string> paths)
    {
        using var dlg = new CompressDialog(theme, AppSettings.Load(), owner.Font, paths);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Request : null;
    }

    CompressDialog(Theme theme, AppSettings settings, Font font, IEnumerable<string> paths)
    {
        this.theme = theme;
        this.settings = settings;
        Text = "새 압축";
        Font = font;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;  // resizable for a longer list; a dialog: no maximize / minimize
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        AllowDrop = true;
        float s = DeviceDpi / 96f;
        mono = FormatTag.MonoFont(Math.Max(7.5f, font.Size * 0.9f));
        int fieldWidth = (int)(240 * s);

        root.Dock = DockStyle.Fill;
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Padding = new Padding((int)(20 * s), (int)(16 * s), (int)(20 * s), (int)(16 * s));

        // ----- 압축할 파일 목록 -----
        filesRow.Dock = DockStyle.Fill;
        filesRow.ColumnCount = 2;
        filesRow.RowCount = 1;
        filesRow.Margin = new Padding(0);
        filesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filesRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filesRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listFrame.Dock = DockStyle.Fill;
        listFrame.Margin = new Padding(0);
        listFrame.Padding = new Padding(Math.Max(3, (int)(3 * s)));
        listFrame.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Typo.RoundRect(new RectangleF(0.5f, 0.5f, listFrame.Width - 1.5f, listFrame.Height - 1.5f), 6 * DeviceDpi / 96f);
            using var fill = new SolidBrush(theme.Card);
            using var pen = new Pen(list.Focused ? theme.Accent : theme.Pressed);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        };
        listFrame.Resize += (_, _) => listFrame.Invalidate();
        listFrame.Controls.Add(list);
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;
        list.SmallImageList = icons.List;
        list.StateImageList = new ImageList { ImageSize = new Size(1, Math.Max(16, (int)(28 * s))) };  // 28 px rows
        list.AllowDrop = true;
        list.AccessibleName = "압축할 파일 목록";
        list.Columns.Add("이름", (int)(200 * s));
        list.Columns.Add("파일 크기", (int)(90 * s), HorizontalAlignment.Right);
        list.Columns.Add("경로", (int)(260 * s));
        list.OwnerDraw = true;
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += DrawCell;
        list.DrawColumnHeader += DrawHeader;
        list.Resize += (_, _) => FitColumns();
        list.SelectedIndexChanged += (_, _) => UpdateState();
        list.GotFocus += (_, _) => listFrame.Invalidate();
        list.LostFocus += (_, _) => listFrame.Invalidate();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        var fileButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding((int)(12 * s), 0, 0, 0),
            Padding = new Padding(0),
        };
        addButton = NewButton("추가(&A)...", s);
        addButton.Click += (_, _) => BeginInvoke(AddFiles);
        folderButton = NewButton("폴더 추가(&F)...", s);
        folderButton.Click += (_, _) => BeginInvoke(AddFolders);
        removeButton = NewButton("제거(&D)", s);
        removeButton.Click += (_, _) => RemoveSelected();
        var side = new[] { addButton, folderButton, removeButton };
        foreach (var b in side)
            b.Margin = new Padding(0, 0, 0, (int)(8 * s));
        fileButtons.Controls.AddRange(side);
        filesRow.Controls.Add(listFrame, 0, 0);
        filesRow.Controls.Add(fileButtons, 1, 0);

        // ----- 압축 파일 설정 -----
        settingsPanel.Dock = DockStyle.Top;
        settingsPanel.AutoSize = true;
        settingsPanel.ColumnCount = 3;
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));  // fixed below to the widest label
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.Margin = new Padding(0, (int)(24 * s), 0, 0);
        int row = 0;
        var settingsTitle = Section("압축 파일 설정", s);
        settingsPanel.Controls.Add(settingsTitle, 0, row);
        settingsPanel.SetColumnSpan(settingsTitle, 3);
        row++;

        pathBox.AccessibleName = "파일 이름";
        pathBox.TextChanged += (_, _) =>
        {
            if (!settingPath)
                nameEdited = true;
        };
        pathFrame = new InputFrame(theme, pathBox) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        pathFrame.Fit(DeviceDpi);
        browseButton = new ActionButton(theme, "...") { MinWidth = 40 };
        browseButton.Margin = new Padding((int)(8 * s), (int)(4 * s), 0, (int)(4 * s));
        browseButton.AccessibleName = "저장 위치 찾아보기";
        browseButton.Click += (_, _) => BeginInvoke(Browse);
        AddRow(ref row, "파일 이름", pathFrame, s, browseButton);

        formatBox = Combo(s);
        formatBox.AccessibleName = "압축 형식";
        formatBox.Width = fieldWidth;
        foreach (var f in ArchiveFormat.All)
            formatBox.Items.Add(f);
        AddRow(ref row, "압축 형식", formatBox, s);

        var passwordRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        passwordButton = NewButton("암호 설정(&P)...", s);
        passwordButton.Click += (_, _) => BeginInvoke(SetPassword);
        passwordHint.AutoSize = true;
        passwordHint.Anchor = AnchorStyles.Left;
        passwordHint.Margin = new Padding((int)(12 * s), 0, 0, 0);
        passwordRow.Controls.Add(passwordButton);
        passwordRow.Controls.Add(passwordHint);
        passwordRow.Layout += (_, _) =>  // the hint centred on the button
            passwordHint.Top = Math.Max(0, (passwordButton.Height - passwordHint.Height) / 2);
        AddRow(ref row, "암호", passwordRow, s);

        var splitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        splitBox = Combo(s);
        splitBox.AccessibleName = "분할 압축";
        splitBox.Width = fieldWidth;
        splitBox.MaxDropDownItems = 12;
        splitBox.Margin = new Padding(0);
        foreach (var c in SplitChoices)
            splitBox.Items.Add(c);
        customSplit.AccessibleName = "분할 크기 직접 입력 (예: 123MB, 1.5GB, 1440KB)";
        customSplit.PlaceholderText = "예: 123MB";
        customFrame = new InputFrame(theme, customSplit) { Width = (int)(120 * s), Margin = new Padding((int)(8 * s), 0, 0, 0) };
        customFrame.Fit(DeviceDpi);
        splitBox.SelectedIndexChanged += (_, _) =>
        {
            bool custom = splitBox.SelectedItem is SplitChoice { Bytes: Custom };
            customFrame.Visible = custom;
            if (custom && Visible)
                customSplit.Focus();
        };
        splitRow.Controls.Add(splitBox);
        splitRow.Controls.Add(customFrame);
        AddRow(ref row, "분할 압축", splitRow, s);

        levelBox = Combo(s);
        levelBox.AccessibleName = "압축 방법";
        levelBox.Width = fieldWidth;
        foreach (var l in LevelChoices)
            levelBox.Items.Add(l);
        AddRow(ref row, "압축 방법", levelBox, s);

        advancedButton = new ActionButton(theme, "고급 압축 설정 »", ButtonKind.Link);
        advancedButton.Margin = new Padding(0, (int)(4 * s), 0, 0);
        advancedButton.Anchor = AnchorStyles.Left;
        advancedButton.Click += (_, _) => ToggleAdvanced();
        settingsPanel.Controls.Add(advancedButton, 1, row++);

        advancedPanel.AutoSize = true;
        advancedPanel.ColumnCount = 2;
        advancedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advancedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        advancedPanel.Dock = DockStyle.Fill;
        advancedPanel.Margin = new Padding(0);
        excludeBox.Text = DefaultExclude;
        excludeBox.AccessibleName = "제외할 이름 끝 (쉼표로 구분)";
        excludeFrame = new InputFrame(theme, excludeBox) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        excludeFrame.Fit(DeviceDpi);
        excludeFrame.Margin = new Padding(0, (int)(4 * s), 0, (int)(4 * s));
        advancedPanel.Controls.Add(FieldLabel("제외할 이름 끝", s), 0, 0);
        advancedPanel.Controls.Add(excludeFrame, 1, 0);
        var excludeHint = new TextLabel
        {
            Text = "이 이름으로 끝나는 파일은 넣지 않습니다. 쉼표로 구분합니다.",
            AutoSize = true,
            Tag = "muted",
            Margin = new Padding(0, 0, 0, (int)(4 * s)),
        };
        advancedPanel.Controls.Add(excludeHint, 1, 1);
        advancedPanel.Visible = false;
        settingsPanel.Controls.Add(advancedPanel, 0, row);
        settingsPanel.SetColumnSpan(advancedPanel, 3);
        row++;

        var checks = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, (int)(8 * s), 0, 0),
            Padding = new Padding(0),
        };
        testCheck = NewCheck("압축 후 테스트(&T)");
        deleteCheck = NewCheck("압축 후 원본 파일 삭제(&E)");
        eachCheck = NewCheck("각각 파일명/폴더명으로 압축하기");
        eachCheck.CheckedChanged += (_, _) => UpdateState();
        checks.Controls.AddRange(new Control[] { testCheck, deleteCheck, eachCheck });
        settingsPanel.Controls.Add(checks, 1, row++);

        // ----- bottom: 상세 설정 보기, 압축 시작, 취소 -----
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, (int)(24 * s), 0, 0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailsCheck = NewCheck("상세 설정 보기");
        detailsCheck.Checked = true;
        detailsCheck.Anchor = AnchorStyles.Left;
        detailsCheck.CheckedChanged += (_, _) => ShowPanel(settingsPanel, detailsCheck.Checked);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        startButton = new ActionButton(theme, "압축 시작", ButtonKind.Primary) { MinWidth = 96 };
        startButton.Margin = new Padding(0, 0, (int)(8 * s), 0);
        startButton.Click += (_, _) => Start();
        cancelButton = new ActionButton(theme, "취소") { MinWidth = 80 };
        cancelButton.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(startButton);
        actions.Controls.Add(cancelButton);
        bottom.Controls.Add(detailsCheck, 0, 0);
        bottom.Controls.Add(actions, 1, 0);

        root.RowCount = 4;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Section("압축할 파일 목록", s), 0, 0);
        root.Controls.Add(filesRow, 0, 1);
        root.Controls.Add(settingsPanel, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);
        AcceptButton = startButton;
        CancelButton = cancelButton;

        foreach (Control c in new Control[] { this, list })
        {
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
                    AddPaths(dropped);
            };
        }

        // the dropped files first: a saved GZ / XZ default holds only when exactly one file is there
        AddPaths(paths);
        // defaults from 환경설정 > 압축
        int fmt = Array.FindIndex(ArchiveFormat.All, f => string.Equals(f.Id, settings.CompressFormat, StringComparison.Ordinal));
        lastFormat = fmt < 0 ? 0 : fmt;
        formatBox.SelectedIndex = lastFormat;
        formatBox.SelectedIndexChanged += (_, _) => FormatChanged();
        levelBox.SelectedIndex = NearestLevel(settings.CompressLevel);
        SelectSplit(settings.CompressSplit);
        SetPath(AutoPath());

        DialogStyle.Apply(theme, this);
        HandleCreated += (_, _) =>
        {
            // windows for the hidden rows too, in order (made later, a shown control moves to the front of its row)
            foreach (Control c in Descendants(this).ToList())
                _ = c.Handle;
            theme.ApplyWindow(this);
            theme.ApplyList(list);
        };
        Load += (_, _) =>
        {
            FitWidths();
            FitHeight();
        };
        FormClosed += (_, _) => sizing.Cancel();
        FormatChanged();
    }

    // ----- layout helpers -----

    static IEnumerable<Control> Descendants(Control c) =>
        new[] { c }.Concat(c.Controls.OfType<Control>().SelectMany(Descendants));

    static TextLabel Section(string text, float s) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, (int)(8 * s)),
        Tag = "section",
    };

    TextLabel FieldLabel(string text, float s)
    {
        var l = new TextLabel
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, (int)(4 * s), (int)(16 * s), (int)(4 * s)),
        };
        fieldLabels.Add(l);
        return l;
    }

    void AddRow(ref int row, string label, Control field, float s, Control? extra = null)
    {
        settingsPanel.Controls.Add(FieldLabel(label, s), 0, row);
        field.Margin = new Padding(0, (int)(4 * s), 0, (int)(4 * s));  // every field starts on the same line
        settingsPanel.Controls.Add(field, 1, row);
        if (extra != null)
            settingsPanel.Controls.Add(extra, 2, row);
        row++;
    }

    ActionButton NewButton(string text, float s) => new(theme, text) { Margin = new Padding(0, 0, (int)(8 * s), 0) };

    ThemedCheck NewCheck(string text) => new(theme, text) { Margin = new Padding(0) };

    ComboBox Combo(float s)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(Font.Height + 4, (int)(32 * s) - 6),  // 32 px tall like the buttons and fields
        };
        c.DrawItem += DrawComboItem;
        return c;
    }

    /// <summary>
    /// Combo rows in the theme's colours. Formats show their tag, the rest of their label and the extension; a
    /// format that needs exactly one file is greyed out otherwise.
    /// </summary>
    void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox c)
            return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var back = new SolidBrush(selected ? theme.Selection : theme.Card))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Index < 0)
            return;
        float s = DeviceDpi / 96f;
        var item = c.Items[e.Index];
        bool enabled = c.Enabled && !(item is ArchiveFormat f0 && !Allowed(f0));
        var r = Rectangle.Inflate(e.Bounds, -(int)(6 * s), 0);
        if (item is ArchiveFormat f)
        {
            string label = f.Label;
            int space = label.IndexOf(' ', StringComparison.Ordinal);
            string tag = space < 0 ? label : label[..space];
            string rest = space < 0 ? "" : label[(space + 1)..].Trim('(', ')', ' ');
            var size = FormatTag.Measure(tag, mono, DeviceDpi);
            FormatTag.Paint(e.Graphics, tag, mono, theme, r.Left, r.Top + (r.Height - size.Height) / 2, DeviceDpi, selected, enabled);
            int x = r.Left + size.Width + (int)(8 * s);
            int extW = Typo.Measure(f.Ext, c.Font).Width;
            Typo.Draw(e.Graphics, f.Ext, c.Font, new Rectangle(r.Right - extW, r.Top, extW, r.Height), enabled ? theme.Muted : theme.Disabled);
            if (rest.Length > 0)
                Typo.Draw(e.Graphics, rest, c.Font, new Rectangle(x, r.Top, Math.Max(0, r.Right - extW - x - (int)(8 * s)), r.Height),
                          enabled ? theme.Secondary : theme.Disabled);
            return;
        }
        Typo.Draw(e.Graphics, c.GetItemText(item) ?? "", c.Font, r, enabled ? theme.Text : theme.Disabled);
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var back = new SolidBrush(theme.Card))
            e.Graphics.FillRectangle(back, e.Bounds);
        using (var line = new Pen(theme.Border))
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        bool right = e.Header?.TextAlign == HorizontalAlignment.Right;
        var r = Rectangle.Inflate(e.Bounds, -(int)(10 * DeviceDpi / 96f), 0);
        Typo.Draw(e.Graphics, e.Header?.Text ?? "", list.Font, r, theme.Muted, right ? StringAlignment.Far : StringAlignment.Near);
    }

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null)
            return;
        var g = e.Graphics;
        var row = e.Bounds;
        float s = DeviceDpi / 96f;
        using (var back = new SolidBrush(e.Item.Selected ? theme.Selection : theme.Card))
            g.FillRectangle(back, row);
        int pad = (int)(10 * s);
        var textRect = Rectangle.Inflate(row, -pad, 0);
        if (e.ColumnIndex == 0 && list.SmallImageList != null && e.Item.ImageIndex >= 0)
        {
            var img = list.SmallImageList.Images[e.Item.ImageIndex];
            g.DrawImage(img, row.Left + pad, row.Top + (row.Height - img.Height) / 2, img.Width, img.Height);
            int left = row.Left + pad + img.Width + (int)(8 * s);
            textRect = new Rectangle(left, row.Top, row.Right - left - (int)(6 * s), row.Height);
        }
        bool right = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
        string text = e.SubItem.Text;
        if (e.ColumnIndex == 2)  // a long folder: its start and end stay readable
            text = TrimPath(text, list.Font, textRect.Width);
        Typo.Draw(g, text, list.Font, textRect, e.ColumnIndex == 0 ? theme.Text : e.ColumnIndex == 2 ? theme.Muted : theme.Secondary,
                  right ? StringAlignment.Far : StringAlignment.Near);
        if (list.Focused && e.Item.Focused)  // keyboard position: an accent line under the row
            using (var ring = new SolidBrush(theme.Accent))
                g.FillRectangle(ring, row.Left, row.Bottom - Math.Max(1, (int)s), row.Width, Math.Max(1, (int)s));
    }

    /// <summary>C:\a\b\c\d -> C:\a\…\d when it does not fit (as TextRenderer's path ellipsis).</summary>
    string TrimPath(string path, Font font, int width)
    {
        if (width <= 0 || Typo.Measure(path, font).Width <= width)
            return path;
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 3)
            return path;
        for (int keep = parts.Length - 2; keep >= 1; keep--)
        {
            string t = string.Join(Path.DirectorySeparatorChar, parts.Take(keep)) + Path.DirectorySeparatorChar + "…" +
                       Path.DirectorySeparatorChar + parts[^1];
            if (Typo.Measure(t, font).Width <= width)
                return t;
        }
        return parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + parts[^1];
    }

    /// <summary>The path column takes the rest of the width.</summary>
    void FitColumns()
    {
        int others = list.Columns[0].Width + list.Columns[1].Width;
        list.Columns[2].Width = Math.Max((int)(120 * DeviceDpi / 96f), list.ClientSize.Width - others);
    }

    /// <summary>
    /// Widths that depend on the text: measured once every control is in the window and has the window's font
    /// (환경설정's font size), not the default font it had when it was made.
    /// </summary>
    void FitWidths()
    {
        float s = DeviceDpi / 96f;
        // one width for the column of buttons
        var side = new[] { addButton, folderButton, removeButton };
        foreach (var b in side)
            b.MinWidth = 0;
        int sideWidth = side.Max(b => b.GetPreferredSize(Size.Empty).Width);
        foreach (var b in side)
            b.MinWidth = (int)Math.Ceiling(sideWidth / s);
        // one label column for the settings and the advanced rows, as wide as the widest label
        int labelWidth = fieldLabels.Max(l => l.PreferredSize.Width + l.Margin.Horizontal);
        settingsPanel.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, labelWidth);
        advancedPanel.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, labelWidth);
        // the size column fits its header at any font size
        list.Columns[1].Width = Math.Max((int)(90 * s), Typo.Measure(list.Columns[1].Text, list.Font).Width + (int)(28 * s));
    }

    /// <summary>A height that shows the settings and about seven list rows.</summary>
    void FitHeight()
    {
        float s = DeviceDpi / 96f;
        int width = (int)(660 * s);
        int fixedHeight = root.Padding.Vertical;
        foreach (Control c in root.Controls)
            if (c != filesRow && c.Visible)
                fixedHeight += c.GetPreferredSize(new Size(width - root.Padding.Horizontal, 0)).Height + c.Margin.Vertical;
        ClientSize = new Size(width, fixedHeight + (int)(190 * s));
        MinimumSize = new Size((int)(560 * s), Height - (int)(90 * s));
    }

    /// <summary>Shows or hides a section; the window grows or shrinks by its height, so the list keeps its size.</summary>
    void ShowPanel(Control panel, bool show)
    {
        if (panel.Visible == show)
            return;
        int h = show ? panel.GetPreferredSize(new Size(panel.Parent?.ClientSize.Width ?? Width, 0)).Height + panel.Margin.Vertical
                     : panel.Height + panel.Margin.Vertical;
        panel.Visible = show;
        if (WindowState != FormWindowState.Normal)
            return;
        float s = DeviceDpi / 96f;
        int min = MinimumSize.Height;
        MinimumSize = new Size(MinimumSize.Width, Math.Max((int)(240 * s), min + (show ? h : -h)));
        Height = Math.Max(MinimumSize.Height, Height + (show ? h : -h));
    }

    void ToggleAdvanced()
    {
        bool show = !advancedPanel.Visible;
        ShowPanel(advancedPanel, show);
        advancedButton.Text = show ? "고급 압축 설정 «" : "고급 압축 설정 »";
    }

    // ----- the list -----

    void AddFiles()
    {
        using var dlg = new OpenFileDialog { Title = "압축할 파일 추가", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPaths(dlg.FileNames);
    }

    void AddFolders()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "압축할 폴더 추가",
            UseDescriptionForTitle = true,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPaths(dlg.SelectedPaths);
    }

    void AddPaths(IEnumerable<string> paths)
    {
        list.BeginUpdate();
        foreach (string p in paths)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            bool dir = Directory.Exists(full);
            if (!dir && !File.Exists(full) || !added.Add(full))
                continue;
            string name = Path.GetFileName(full);
            if (name.Length == 0)
                name = full;  // a drive: D:\
            long size = 0;
            if (!dir)
                try { size = new FileInfo(full).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var item = new ListViewItem(new[] { name, dir ? "…" : ViewerForm.FormatSize(size), Path.GetDirectoryName(full) ?? full },
                                        dir ? icons.Folder : icons.ForFile(name)) { Tag = full };
            list.Items.Add(item);
            if (dir)
                _ = FillFolderSize(item, full);
        }
        list.EndUpdate();
        ListChanged();
    }

    /// <summary>A folder's size, counted in the background: a large tree must not hold up the window.</summary>
    async Task FillFolderSize(ListViewItem item, string dir)
    {
        var token = sizing.Token;
        long size = await Task.Run(() => FolderSize(dir, token));
        if (token.IsCancellationRequested || IsDisposed || item.ListView == null)
            return;
        item.SubItems[1].Text = ViewerForm.FormatSize(size);
    }

    static long FolderSize(string dir, CancellationToken token)
    {
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                if (token.IsCancellationRequested)
                    break;
                total += f.Length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    void RemoveSelected()
    {
        if (list.SelectedItems.Count == 0)
            return;
        int first = list.SelectedIndices[0];
        list.BeginUpdate();
        foreach (ListViewItem it in list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            if (it.Tag is string p)
                added.Remove(p);
            list.Items.Remove(it);
        }
        list.EndUpdate();
        if (list.Items.Count > 0)
        {
            var next = list.Items[Math.Min(first, list.Items.Count - 1)];
            next.Selected = next.Focused = true;
        }
        ListChanged();
    }

    List<string> Paths() => list.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<string>().ToList();

    /// <summary>The single file in the list (gz / bz2 / xz / zst need exactly one), else null.</summary>
    string? OneFile()
    {
        var paths = Paths();
        return paths.Count == 1 && File.Exists(paths[0]) ? paths[0] : null;
    }

    bool Allowed(ArchiveFormat f) => !f.Single || OneFile() != null;

    void ListChanged()
    {
        if (!Allowed(Format))
            formatBox.SelectedIndex = 0;  // a second file: back to ZIP
        if (!nameEdited)
            SetPath(AutoPath());
        formatBox.Invalidate();
        UpdateState();
    }

    void UpdateState()
    {
        removeButton.Enabled = list.SelectedItems.Count > 0;
        startButton.Enabled = list.Items.Count > 0;
    }

    // ----- the archive name -----

    ArchiveFormat Format => formatBox.SelectedItem as ArchiveFormat ?? ArchiveFormat.All[0];

    void SetPath(string path)
    {
        settingPath = true;
        pathBox.Text = path;
        settingPath = false;
    }

    /// <summary>
    /// MxZip's default name (as its compress window): one item -> its name, several -> their folder's name, next to
    /// them (or in 환경설정's 압축 폴더); nothing yet -> 문서\새 압축. Never an existing file.
    /// </summary>
    string AutoPath()
    {
        var paths = Paths();
        var fmt = Format;
        if (paths.Count == 0)
        {
            string folder0 = settings.CompressDir.Length > 0 ? settings.CompressDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Unique(Path.Combine(folder0, "새 압축"), fmt.Ext);
        }
        string first = paths[0];
        string dir = Path.GetDirectoryName(first) ?? first;
        string name;
        if (paths.Count > 1)
            name = Path.GetFileName(dir);
        else if (Directory.Exists(first))
            name = Path.GetFileName(first);  // folder "v1.2" stays "v1.2"
        else
            name = fmt.Single ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        if (name.Length == 0)
            name = "archive";
        string folder = settings.CompressDir.Length > 0 ? settings.CompressDir : dir;
        return Unique(Path.Combine(folder, name), fmt.Ext);
    }

    static string Unique(string stem, string ext)
    {
        string p = stem + ext;
        for (int i = 2; File.Exists(p) || Directory.Exists(p) || File.Exists(p + ".001"); i++)
            p = $"{stem} ({i}){ext}";
        return p;
    }

    /// <summary>The name without a format's extension: a.tar.gz -> a (the longest extension first).</summary>
    static string StripExt(string name)
    {
        foreach (var ext in ArchiveFormat.All.Select(f => f.Ext).OrderByDescending(e => e.Length))
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return name;
    }

    void FormatChanged()
    {
        var f = Format;
        if (!Allowed(f))
        {
            formatBox.SelectedIndex = lastFormat < formatBox.Items.Count && Allowed(ArchiveFormat.All[lastFormat]) ? lastFormat : 0;
            return;  // runs again for the restored choice
        }
        lastFormat = formatBox.SelectedIndex;
        // the extension follows the format; a stream keeps the file's own name inside: b.bin -> b.bin.xz
        string cur = pathBox.Text.Trim();
        if (!nameEdited || cur.Length == 0)
            SetPath(AutoPath());
        else
        {
            string dir = Path.GetDirectoryName(cur) ?? "";
            string name = StripExt(Path.GetFileName(cur));
            string? one = OneFile();
            if (f.Single && one != null)
                name = Path.GetFileName(one);
            else if (one != null && string.Equals(name, Path.GetFileName(one), StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);  // back from b.bin.xz to an archive: b.zip
            string next = Path.Combine(dir, name) + f.Ext;
            if (!string.Equals(next, cur, StringComparison.OrdinalIgnoreCase))
                next = Unique(Path.Combine(dir, name), f.Ext);
            SetPath(next);
            nameEdited = true;
        }
        passwordButton.Enabled = f.Password;
        if (!f.Password)
            password = null;
        ShowPasswordHint();
        splitBox.Enabled = f.Split;
        if (!f.Split)
            splitBox.SelectedIndex = 0;
        levelBox.Enabled = f.Id != "tar";
    }

    void ShowPasswordHint()
    {
        var f = Format;
        if (!f.Password)
        {
            passwordHint.Text = $"{f.Label.Split(' ')[0]} 형식은 암호를 지원하지 않습니다.";
            passwordHint.Tag = "muted";
        }
        else if (password != null)
        {
            passwordHint.Text = f.Id == "7z" ? "암호 설정됨 (AES-256, 파일 이름도 숨김)" : "암호 설정됨 (AES-256)";
            passwordHint.Tag = "accent";
        }
        else
        {
            passwordHint.Text = "암호 없음";
            passwordHint.Tag = "muted";
        }
        DialogStyle.Apply(theme, passwordHint);
    }

    void Browse()
    {
        var f = Format;
        string cur = pathBox.Text.Trim();
        using var dlg = new SaveFileDialog
        {
            Title = "압축 파일 저장 위치",
            Filter = $"{f.Label} (*{f.Ext})|*{f.Ext}|모든 파일|*.*",
            OverwritePrompt = false,  // 압축 시작 asks
            AddExtension = true,
            DefaultExt = f.Ext.TrimStart('.'),
        };
        try
        {
            string? dir = Path.GetDirectoryName(cur);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
            dlg.FileName = Path.GetFileName(cur);
        }
        catch (ArgumentException) { }
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        string chosen = dlg.FileName;
        if (!chosen.EndsWith(f.Ext, StringComparison.OrdinalIgnoreCase))
            chosen += f.Ext;
        SetPath(chosen);
        nameEdited = true;
    }

    // ----- password, split, level -----

    void SetPassword()
    {
        var f = Format;
        string hint = f.Id == "7z" ? "AES-256으로 암호화되고 파일 이름도 숨겨집니다." : "AES-256으로 암호화됩니다.";
        string? pw = PasswordDialog.AskNew(this, theme, hint, password != null);
        if (pw == null)
            return;
        password = pw.Length > 0 ? pw : null;
        ShowPasswordHint();
    }

    static int NearestLevel(int level)
    {
        int best = 0;
        for (int i = 1; i < LevelChoices.Length; i++)
            if (Math.Abs(LevelChoices[i].Level - level) < Math.Abs(LevelChoices[best].Level - level))
                best = i;
        return best;
    }

    /// <summary>"123MB", "1.5GB", "1440KB" (as appcore.split_bytes reads it); -1 when it cannot be read.</summary>
    internal static long ParseSize(string text)
    {
        var m = Regex.Match(text, @"^\s*(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            return -1;
        string unit = m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : "B";
        int power = Array.IndexOf(new[] { "B", "KB", "MB", "GB", "TB" }, unit);
        double bytes = n * Math.Pow(1024, power);
        return bytes > long.MaxValue / 2 ? -1 : (long)bytes;
    }

    /// <summary>환경설정's 분할 크기 ("분할 안 함", "100MB", ...): the preset of that size, else 직접 입력.</summary>
    void SelectSplit(string saved)
    {
        long bytes = saved.Length == 0 || saved == "분할 안 함" ? 0 : ParseSize(saved);
        int i = bytes < 0 ? -1 : Array.FindIndex(SplitChoices, c => c.Bytes == bytes);
        if (i >= 0)
            splitBox.SelectedIndex = i;
        else
        {
            splitBox.SelectedIndex = SplitChoices.Length - 1;
            customSplit.Text = saved;
        }
        customFrame.Visible = splitBox.SelectedItem is SplitChoice { Bytes: Custom };
    }

    // ----- 압축 시작 -----

    void Start()
    {
        var paths = Paths();
        if (paths.Count == 0)
            return;
        var f = Format;
        string output = pathBox.Text.Trim().Trim('"');
        try
        {
            if (output.Length == 0 || !Path.IsPathFullyQualified(output) || Path.GetFileName(output).Length == 0)
                throw new ArgumentException("not a full path");
            output = Path.GetFullPath(output);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Warn("파일 이름에 전체 경로를 적으세요. 예: C:\\자료\\새 압축.zip");
            pathBox.Focus();
            return;
        }
        if (!output.EndsWith(f.Ext, StringComparison.OrdinalIgnoreCase))
            output += f.Ext;

        long split = 0;
        if (f.Split && splitBox.SelectedItem is SplitChoice choice)
        {
            split = choice.Bytes;
            if (split == Custom)
            {
                split = ParseSize(customSplit.Text);
                if (split < 64 * KB)
                {
                    Warn("분할 크기 예: 100MB, 1.5GB (최소 64KB)");
                    customSplit.Focus();
                    return;
                }
            }
        }

        string folder = Path.GetDirectoryName(output) ?? output;
        if (!CanWrite(folder))
        {
            Warn($"쓸 수 없는 위치입니다. 다른 위치를 고르세요.\n{folder}");
            return;
        }
        bool each = eachCheck.Checked;
        if (!each && paths.Any(p => string.Equals(p, output, StringComparison.OrdinalIgnoreCase)))
        {
            Warn("원본과 같은 이름은 쓸 수 없습니다.");
            return;
        }
        if (!each && File.Exists(output) &&
            MessageBox.Show(this, $"{Path.GetFileName(output)} 파일이 이미 있습니다. 덮어쓸까요?", "새 압축",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (deleteCheck.Checked &&
            MessageBox.Show(this, $"압축 후 원본 {paths.Count:N0}개를 휴지통으로 옮깁니다. 계속할까요?", "새 압축",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        int level = f.Id == "tar" ? 6 : (levelBox.SelectedItem as LevelChoice)?.Level ?? 6;
        Request = new CompressRequest(paths, output, f.Id, level, split, f.Password ? password : null,
                                      excludeBox.Text.Trim(), testCheck.Checked, each, deleteCheck.Checked);
        DialogResult = DialogResult.OK;
    }

    /// <summary>False only when Windows refuses to create a file there (C:\, Program Files): appcore.can_write.</summary>
    static bool CanWrite(string folder)
    {
        string d = folder;
        while (!Directory.Exists(d) && Path.GetDirectoryName(d) is string up)
            d = up;
        string probe = Path.Combine(d, $"~MxZip{Environment.ProcessId}_{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;  // some other problem: let the job run and report it
        }
    }

    void Warn(string message) => MessageBox.Show(this, message, "새 압축", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sizing.Cancel();
            sizing.Dispose();
            icons.List.Dispose();
            list.StateImageList?.Dispose();
            mono.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The viewer's dark / light palette on the dialog controls (the 새 압축, 암호 입력 and 암호 설정 windows).</summary>
static class DialogStyle
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void Apply(Theme t, Control root)
    {
        Style(t, root);
        foreach (Control c in root.Controls)
            Apply(t, c);
    }

    static void Style(Theme t, Control c)
    {
        switch (c)
        {
            case ListView:
                break;  // ThemedListView: Theme.ApplyList and owner drawing
            case ActionButton or ThemedCheck:
                c.BackColor = c.Parent?.BackColor ?? t.Window;  // they paint themselves from the palette
                break;
            case InputFrame frame:
                frame.ApplyTheme();
                break;
            case TextBox tb:
                tb.BackColor = t.Card;
                tb.ForeColor = t.Text;
                if (tb.Parent is not InputFrame)
                    tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = t.Card;
                cb.ForeColor = t.Text;
                // Windows' dark combo style (no light frame); rows are owner drawn in the palette
                if (cb.IsHandleCreated)
                    SetWindowTheme(cb.Handle, t.Dark ? "DarkMode_CFD" : "Explorer", null);
                else
                    cb.HandleCreated += (_, _) => SetWindowTheme(cb.Handle, t.Dark ? "DarkMode_CFD" : "Explorer", null);
                break;
            case Label l:
                l.BackColor = t.Window;
                // section labels: small and muted; hints muted; a set password in the accent
                l.ForeColor = (l.Tag as string) switch { "muted" or "section" => t.Muted, "accent" => t.Accent, _ => t.Text };
                break;
            default:
                c.BackColor = t.Window;
                c.ForeColor = t.Text;
                break;
        }
    }
}
