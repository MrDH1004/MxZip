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
    string? password;
    bool nameEdited, settingPath;
    int lastFormat;

    readonly TableLayoutPanel root = new();
    readonly TableLayoutPanel filesRow = new();
    readonly ThemedListView list = new();
    readonly Button addButton = new(), folderButton = new(), removeButton = new();
    readonly TableLayoutPanel settingsPanel = new();
    readonly TextBox pathBox = new();
    readonly Button browseButton = new();
    readonly ComboBox formatBox, splitBox, levelBox;
    readonly TextBox customSplit = new();
    readonly Button passwordButton = new();
    readonly Label passwordHint = new();
    readonly Button advancedButton = new();
    readonly TableLayoutPanel advancedPanel = new();
    readonly TextBox excludeBox = new();
    readonly CheckBox testCheck = new(), deleteCheck = new(), eachCheck = new(), detailsCheck = new();
    readonly Button startButton = new(), cancelButton = new();

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
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        AllowDrop = true;
        float s = DeviceDpi / 96f;

        root.Dock = DockStyle.Fill;
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Padding = new Padding((int)(16 * s), (int)(12 * s), (int)(16 * s), (int)(12 * s));

        var title = new Label
        {
            Text = "새 압축",
            AutoSize = true,
            Font = new Font(font.FontFamily, font.Size * 1.35f),
            Margin = new Padding(0, 0, 0, (int)(8 * s)),
        };

        // ----- 압축할 파일 목록 -----
        filesRow.Dock = DockStyle.Fill;
        filesRow.ColumnCount = 2;
        filesRow.RowCount = 1;
        filesRow.Margin = new Padding(0);
        filesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filesRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filesRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.SmallImageList = icons.List;
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
            Margin = new Padding((int)(8 * s), 0, 0, 0),
        };
        SetupButton(addButton, "추가(&A)...", s);
        addButton.Click += (_, _) => BeginInvoke(AddFiles);
        SetupButton(folderButton, "폴더 추가(&F)...", s);
        folderButton.Click += (_, _) => BeginInvoke(AddFolders);
        SetupButton(removeButton, "제거(&D)", s);
        removeButton.Click += (_, _) => RemoveSelected();
        fileButtons.Controls.AddRange(new Control[] { addButton, folderButton, removeButton });
        filesRow.Controls.Add(list, 0, 0);
        filesRow.Controls.Add(fileButtons, 1, 0);

        // ----- 압축 파일 설정 -----
        settingsPanel.Dock = DockStyle.Top;
        settingsPanel.AutoSize = true;
        settingsPanel.ColumnCount = 3;
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.Margin = new Padding(0, (int)(10 * s), 0, 0);
        int row = 0;
        var settingsTitle = Section("압축 파일 설정", s);
        settingsPanel.Controls.Add(settingsTitle, 0, row);
        settingsPanel.SetColumnSpan(settingsTitle, 3);
        row++;

        pathBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pathBox.AccessibleName = "파일 이름";
        pathBox.TextChanged += (_, _) =>
        {
            if (!settingPath)
                nameEdited = true;
        };
        SetupButton(browseButton, "...", s);
        browseButton.AutoSize = false;
        browseButton.MinimumSize = Size.Empty;
        browseButton.Padding = new Padding(0);
        browseButton.Size = new Size((int)(36 * s), pathBox.PreferredHeight + 2);
        browseButton.Margin = new Padding((int)(4 * s), (int)(3 * s), 0, (int)(3 * s));
        browseButton.AccessibleName = "저장 위치 찾아보기";
        browseButton.Click += (_, _) => BeginInvoke(Browse);
        AddRow(ref row, "파일 이름", pathBox, s, browseButton);

        formatBox = Combo(s);
        formatBox.AccessibleName = "압축 형식";
        formatBox.Width = (int)(220 * s);
        foreach (var f in ArchiveFormat.All)
            formatBox.Items.Add(f);
        AddRow(ref row, "압축 형식", formatBox, s);

        var passwordRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        SetupButton(passwordButton, "암호 설정(&P)...", s);
        passwordButton.Click += (_, _) => BeginInvoke(SetPassword);
        passwordHint.AutoSize = true;
        passwordHint.Anchor = AnchorStyles.Left;
        passwordHint.Margin = new Padding((int)(8 * s), (int)(7 * s), 0, 0);
        passwordRow.Controls.Add(passwordButton);
        passwordRow.Controls.Add(passwordHint);
        AddRow(ref row, "암호", passwordRow, s);

        var splitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        splitBox = Combo(s);
        splitBox.AccessibleName = "분할 압축";
        splitBox.Width = (int)(220 * s);
        splitBox.MaxDropDownItems = 12;
        foreach (var c in SplitChoices)
            splitBox.Items.Add(c);
        customSplit.Width = (int)(110 * s);
        customSplit.AccessibleName = "분할 크기 직접 입력 (예: 123MB, 1.5GB, 1440KB)";
        customSplit.PlaceholderText = "예: 123MB";
        customSplit.Margin = new Padding((int)(8 * s), 3, 0, 3);
        splitBox.SelectedIndexChanged += (_, _) =>
        {
            bool custom = splitBox.SelectedItem is SplitChoice { Bytes: Custom };
            customSplit.Visible = custom;
            if (custom && Visible)
                customSplit.Focus();
        };
        splitRow.Controls.Add(splitBox);
        splitRow.Controls.Add(customSplit);
        AddRow(ref row, "분할 압축", splitRow, s);

        levelBox = Combo(s);
        levelBox.AccessibleName = "압축 방법";
        levelBox.Width = (int)(220 * s);
        foreach (var l in LevelChoices)
            levelBox.Items.Add(l);
        AddRow(ref row, "압축 방법", levelBox, s);

        SetupButton(advancedButton, "고급 압축 설정 »", s);
        advancedButton.Click += (_, _) => ToggleAdvanced();
        settingsPanel.Controls.Add(advancedButton, 1, row++);

        advancedPanel.AutoSize = true;
        advancedPanel.ColumnCount = 2;
        advancedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advancedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        advancedPanel.Dock = DockStyle.Fill;
        advancedPanel.Margin = new Padding(0);
        excludeBox.Text = DefaultExclude;
        excludeBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        excludeBox.AccessibleName = "제외할 이름 끝 (쉼표로 구분)";
        advancedPanel.Controls.Add(FieldLabel("제외할 이름 끝", s), 0, 0);
        advancedPanel.Controls.Add(excludeBox, 1, 0);
        var excludeHint = new Label
        {
            Text = "이 이름으로 끝나는 파일은 넣지 않습니다. 쉼표로 구분합니다.",
            AutoSize = true,
            Tag = "muted",
            Margin = new Padding(3, 0, 3, 3),
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
            Margin = new Padding(0, (int)(4 * s), 0, 0),
        };
        SetupCheck(testCheck, "압축 후 테스트(&T)");
        SetupCheck(deleteCheck, "압축 후 원본 파일 삭제(&E)");
        SetupCheck(eachCheck, "각각 파일명/폴더명으로 압축하기");
        eachCheck.CheckedChanged += (_, _) => UpdateState();
        checks.Controls.AddRange(new Control[] { testCheck, deleteCheck, eachCheck });
        settingsPanel.Controls.Add(checks, 1, row++);

        // ----- bottom: 상세 설정 보기, 압축 시작, 취소 -----
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, (int)(12 * s), 0, 0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        SetupCheck(detailsCheck, "상세 설정 보기");
        detailsCheck.Checked = true;
        detailsCheck.Anchor = AnchorStyles.Left;
        detailsCheck.CheckedChanged += (_, _) => ShowPanel(settingsPanel, detailsCheck.Checked);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        SetupButton(startButton, "압축 시작", s);
        startButton.Tag = "primary";
        startButton.Click += (_, _) => Start();
        SetupButton(cancelButton, "취소", s);
        cancelButton.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(startButton);
        actions.Controls.Add(cancelButton);
        bottom.Controls.Add(detailsCheck, 0, 0);
        bottom.Controls.Add(actions, 1, 0);

        root.RowCount = 5;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(Section("압축할 파일 목록", s), 0, 1);
        root.Controls.Add(filesRow, 0, 2);
        root.Controls.Add(settingsPanel, 0, 3);
        root.Controls.Add(bottom, 0, 4);
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
        startButton.EnabledChanged += (_, _) => DialogStyle.Apply(theme, startButton);
        HandleCreated += (_, _) =>
        {
            theme.ApplyWindow(this);
            theme.ApplyList(list);
        };
        Load += (_, _) => FitHeight();
        FormClosed += (_, _) => sizing.Cancel();
        FormatChanged();
    }

    // ----- layout helpers -----

    static Label Section(string text, float s) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, (int)(6 * s)),
        Tag = "section",
    };

    static Label FieldLabel(string text, float s) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, (int)(4 * s), (int)(12 * s), (int)(4 * s)),
    };

    void AddRow(ref int row, string label, Control field, float s, Control? extra = null)
    {
        settingsPanel.Controls.Add(FieldLabel(label, s), 0, row);
        field.Margin = new Padding(field.Margin.Left, (int)(3 * s), field.Margin.Right, (int)(3 * s));
        settingsPanel.Controls.Add(field, 1, row);
        if (extra != null)
            settingsPanel.Controls.Add(extra, 2, row);
        row++;
    }

    static void SetupButton(Button b, string text, float s)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.MinimumSize = new Size((int)(88 * s), 0);
        b.Padding = new Padding((int)(8 * s), (int)(2 * s), (int)(8 * s), (int)(2 * s));
        b.Margin = new Padding(0, 0, (int)(6 * s), (int)(6 * s));
        b.AccessibleName = text.Replace("&", "", StringComparison.Ordinal).Replace("...", "", StringComparison.Ordinal);
    }

    static void SetupCheck(CheckBox c, string text)
    {
        c.Text = text;
        c.AutoSize = true;
        c.AccessibleName = text.Replace("&", "", StringComparison.Ordinal);
    }

    ComboBox Combo(float s)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = (int)(Font.Height + 6 * s),
        };
        c.DrawItem += DrawComboItem;
        return c;
    }

    /// <summary>Combo rows in the theme's colours; a format that needs exactly one file is greyed out otherwise.</summary>
    void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox c)
            return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var back = new SolidBrush(selected ? theme.Selection : theme.Panel))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Index < 0)
            return;
        bool enabled = c.Enabled && !(c.Items[e.Index] is ArchiveFormat f && !Allowed(f));
        var r = Rectangle.Inflate(e.Bounds, -(int)(4 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, r, enabled ? theme.Text : theme.Disabled,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                              TextFormatFlags.NoPrefix);
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var back = new SolidBrush(theme.Panel))
            e.Graphics.FillRectangle(back, e.Bounds);
        using (var line = new Pen(theme.Border))
        {
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        var r = Rectangle.Inflate(e.Bounds, -(int)(8 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", list.Font, r, theme.Muted, flags);
    }

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null)
            return;
        var g = e.Graphics;
        var row = e.Bounds;
        using (var back = new SolidBrush(e.Item.Selected ? theme.Selection : theme.Window))
            g.FillRectangle(back, row);
        int pad = (int)(6 * DeviceDpi / 96f);
        var textRect = Rectangle.Inflate(row, -pad, 0);
        if (e.ColumnIndex == 0 && list.SmallImageList != null && e.Item.ImageIndex >= 0)
        {
            var img = list.SmallImageList.Images[e.Item.ImageIndex];
            g.DrawImage(img, row.Left + pad, row.Top + (row.Height - img.Height) / 2, img.Width, img.Height);
            textRect = new Rectangle(row.Left + pad * 2 + img.Width, row.Top, row.Width - pad * 3 - img.Width, row.Height);
        }
        var align = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(g, e.SubItem.Text, list.Font, textRect, e.ColumnIndex == 0 ? theme.Text : theme.Muted,
                              align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                              (e.ColumnIndex == 2 ? TextFormatFlags.PathEllipsis : 0));
        if (list.Focused && e.Item.Focused)  // keyboard position: an accent line under the row
            using (var ring = new SolidBrush(theme.Accent))
                g.FillRectangle(ring, row.Left, row.Bottom - Math.Max(1, (int)(DeviceDpi / 96f)), row.Width,
                                Math.Max(1, (int)(DeviceDpi / 96f)));
    }

    /// <summary>The path column takes the rest of the width.</summary>
    void FitColumns()
    {
        int others = list.Columns[0].Width + list.Columns[1].Width;
        list.Columns[2].Width = Math.Max((int)(120 * DeviceDpi / 96f), list.ClientSize.Width - others);
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
        customSplit.Visible = splitBox.SelectedItem is SplitChoice { Bytes: Custom };
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
            Warn("파일 이름에 전체 경로를 적으세요. 예: C:\\자료\\새 압축.zip\n'...' 단추로 위치를 고를 수도 있습니다.");
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
                    Warn("분할 크기를 이해할 수 없습니다. 숫자와 단위로 적으세요. 예: 123MB, 1.5GB, 1440KB (최소 64KB)");
                    customSplit.Focus();
                    return;
                }
            }
        }

        string folder = Path.GetDirectoryName(output) ?? output;
        if (!CanWrite(folder))
        {
            Warn($"{folder}\n\n이 위치에는 관리자 권한이 있어야 파일을 만들 수 있습니다. 다른 위치를 고르세요.");
            return;
        }
        bool each = eachCheck.Checked;
        if (!each && paths.Any(p => string.Equals(p, output, StringComparison.OrdinalIgnoreCase)))
        {
            Warn("압축할 파일과 같은 이름으로는 만들 수 없습니다. 다른 파일 이름을 고르세요.");
            return;
        }
        if (!each && File.Exists(output) &&
            MessageBox.Show(this, $"{Path.GetFileName(output)} 파일이 이미 있습니다. 덮어쓸까요?", "새 압축",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (deleteCheck.Checked &&
            MessageBox.Show(this, $"압축이 끝나면 원본 {paths.Count:N0}개 항목을 휴지통으로 옮깁니다. 계속할까요?", "새 압축",
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
        }
        base.Dispose(disposing);
    }
}

/// <summary>The viewer's dark / light palette on standard dialog controls (the 새 압축 and 암호 설정 windows).</summary>
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
            case TextBox tb:
                tb.BackColor = t.Panel;
                tb.ForeColor = t.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = t.Panel;
                cb.ForeColor = t.Text;
                // Windows' dark combo style (no light frame); rows are owner drawn in the palette
                if (cb.IsHandleCreated)
                    SetWindowTheme(cb.Handle, t.Dark ? "DarkMode_CFD" : "Explorer", null);
                else
                    cb.HandleCreated += (_, _) => SetWindowTheme(cb.Handle, t.Dark ? "DarkMode_CFD" : "Explorer", null);
                break;
            case Button b:
                bool primary = b.Tag as string == "primary" && b.Enabled;
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = primary ? t.Accent : t.Panel;
                b.ForeColor = primary ? t.AccentText : t.Text;
                b.FlatAppearance.BorderColor = primary ? t.Accent : t.Border;
                b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Dark(t.Accent, 0.05f) : t.Hover;
                b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(t.Accent, 0.15f) : t.Pressed;
                break;
            case CheckBox cb:
                cb.BackColor = t.Window;
                cb.ForeColor = t.Text;
                cb.FlatStyle = FlatStyle.Flat;
                cb.FlatAppearance.BorderColor = t.Muted;
                cb.FlatAppearance.CheckedBackColor = t.Window;
                break;
            case Label l:
                l.BackColor = t.Window;
                l.ForeColor = (l.Tag as string) switch { "muted" => t.Muted, "accent" => t.Accent, _ => t.Text };
                if (l.Tag as string == "section" && !l.Font.Bold)
                    l.Font = new Font(l.Font, FontStyle.Bold);
                break;
            default:
                c.BackColor = t.Window;
                c.ForeColor = t.Text;
                break;
        }
    }
}
