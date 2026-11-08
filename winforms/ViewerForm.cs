using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MxZip.Viewer;

/// <summary>
/// MxZip's archive window: a toolbar of the common actions, a folder-by-folder list of what the engine reported,
/// and an empty "drop files here" page when no archive is open. Every format operation is an engine call
/// (progress and cancel in the status bar); compressing new archives and settings open MxZip's own windows.
/// </summary>
sealed class ViewerForm : Form
{
    readonly AppSettings settings;
    readonly Theme theme;
    string? archive;
    string? password;
    List<Entry> entries = new();
    bool editable;
    string format = "";
    readonly Dictionary<string, List<Entry>> folders = new(StringComparer.OrdinalIgnoreCase);  // "a/b/" -> children
    string current = "";
    bool flat;
    int sortColumn;
    bool sortDesc;
    EngineJob? running;

    readonly FlowLayoutPanel toolbar = new();
    readonly ToolButton openButton, extractButton, newButton, addButton, deleteButton, testButton, infoButton,
                        flatButton, settingsButton;
    readonly Banner banner;
    readonly Panel pathBar = new();
    readonly Button upButton = new();
    readonly Label location = new();
    readonly Panel content = new();
    readonly ListView list = new();
    readonly Panel empty = new();
    readonly Panel status = new();
    readonly Label statusText = new();
    readonly ProgressBar progress = new();
    readonly LinkLabel cancelLink = new();
    readonly ShellIcons icons;
    readonly ContextMenuStrip itemMenu = new();

    public ViewerForm(string? archive, Task<EngineResult>? listing)
    {
        settings = AppSettings.Load();
        theme = Theme.Load(Environment.GetEnvironmentVariable("MXZIP_THEME") ?? settings.Theme);  // env: tests
        Text = "MxZip";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(settings.FontFamily.Length > 0 ? settings.FontFamily : "맑은 고딕", settings.FontSize);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        AllowDrop = true;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // toolbar
        openButton = Tool("열기", Icons.Open, OpenArchiveDialog);
        extractButton = Tool("풀기", Icons.Extract, ShowExtractMenu, popup: true);
        newButton = Tool("새로 압축", Icons.NewArchive, NewArchiveDialog);
        newButton.Emphasis = true;
        addButton = Tool("파일 추가", Icons.AddFile, AddFilesDialog);
        deleteButton = Tool("파일 삭제", Icons.DeleteFile, DeleteSelected);
        testButton = Tool("오류 검사", Icons.Test, TestArchive);
        infoButton = Tool("정보", Icons.Info, ShowInfo);
        flatButton = Tool("전체 보기", Icons.FlatList, ToggleFlat);
        settingsButton = Tool("환경설정", Icons.Settings, () => Engine.Forward("--settings"));
        toolbar.Dock = DockStyle.Top;
        toolbar.BackColor = theme.Toolbar;
        toolbar.WrapContents = false;
        toolbar.Padding = new Padding(6, 2, 6, 2);
        toolbar.Controls.AddRange(new Control[] { openButton, extractButton, newButton, Separator(), addButton,
                                                  deleteButton, testButton, infoButton, Separator(), flatButton });
        toolbar.Controls.Add(settingsButton);
        toolbar.Resize += (_, _) => PlaceSettings();

        banner = new Banner(theme, "ZIP 파일을 열 기본 앱을 MxZip으로 바꾸시겠습니까?", "기본 앱 설정 열기");
        banner.Action.LinkClicked += (_, _) => BeginInvoke(OpenDefaultApps);
        banner.CloseButton.Click += (_, _) =>
        {
            banner.Visible = false;
            try { AppSettings.BannerDismissed = true; } catch { }
        };
        banner.Visible = false;

        // path bar: up button and where in the archive the list is
        pathBar.Dock = DockStyle.Top;
        pathBar.BackColor = theme.Panel;
        upButton.FlatStyle = FlatStyle.Flat;
        upButton.FlatAppearance.BorderSize = 0;
        upButton.FlatAppearance.MouseOverBackColor = theme.Hover;
        upButton.AccessibleName = "위로";
        upButton.Dock = DockStyle.Left;
        upButton.Click += (_, _) => GoUp();
        location.Dock = DockStyle.Fill;
        location.TextAlign = ContentAlignment.MiddleLeft;
        location.ForeColor = theme.Muted;
        location.AutoEllipsis = true;
        pathBar.Controls.Add(location);
        pathBar.Controls.Add(upButton);
        pathBar.Visible = false;

        // list
        icons = new ShellIcons();
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;
        list.SmallImageList = icons.List;
        list.AllowDrop = true;
        list.Columns.Add("이름", 380);
        list.Columns.Add("크기", 110, HorizontalAlignment.Right);
        list.Columns.Add("압축 크기", 110, HorizontalAlignment.Right);
        list.Columns.Add("수정한 날짜", 140);
        list.OwnerDraw = true;  // header and rows in the theme's colours
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += DrawCell;
        list.DrawColumnHeader += DrawHeader;
        list.Resize += (_, _) => FitColumns();
        list.ItemActivate += (_, _) => OpenItem(list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null);
        list.ColumnClick += (_, e) => Sort(e.Column);
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.ContextMenuStrip = itemMenu;
        list.Visible = false;
        itemMenu.Items.Add("열기", null, (_, _) => OpenItem(list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null));
        itemMenu.Items.Add("선택 항목 풀기...", null, (_, _) => ExtractChosen(SelectedEntries(), choose: true));
        itemMenu.Items.Add("삭제", null, (_, _) => DeleteSelected());
        itemMenu.Opening += (_, e) =>
        {
            bool any = SelectedEntries().Count > 0;
            itemMenu.Items[0].Enabled = list.SelectedItems.Count == 1;
            itemMenu.Items[1].Enabled = any;
            itemMenu.Items[2].Enabled = any && editable;
            e.Cancel = list.SelectedItems.Count == 0;
        };

        BuildEmptyPage();
        content.Dock = DockStyle.Fill;
        content.BackColor = theme.Window;
        content.Controls.Add(list);
        content.Controls.Add(empty);

        // status bar: message, and while working a progress bar with 취소
        status.Dock = DockStyle.Bottom;
        status.BackColor = theme.Panel;
        statusText.Dock = DockStyle.Fill;
        statusText.TextAlign = ContentAlignment.MiddleLeft;
        statusText.ForeColor = theme.Muted;
        statusText.AutoEllipsis = true;
        progress.Dock = DockStyle.Right;
        progress.Visible = false;
        cancelLink.Text = "취소";
        cancelLink.Dock = DockStyle.Right;
        cancelLink.TextAlign = ContentAlignment.MiddleCenter;
        cancelLink.LinkColor = cancelLink.ActiveLinkColor = theme.Accent;
        cancelLink.Visible = false;
        cancelLink.LinkClicked += (_, _) => running?.Cancel();
        status.Controls.Add(statusText);
        status.Controls.Add(progress);
        status.Controls.Add(cancelLink);

        // docking runs from the last added control: toolbar on top, then banner, path bar; status at the bottom
        // docking starts from the back of the z-order (the last added): status, toolbar, path bar, notice, list
        Controls.Add(content);
        Controls.Add(banner);
        Controls.Add(pathBar);
        Controls.Add(toolbar);
        Controls.Add(status);

        foreach (Control c in new Control[] { this, list, empty, content })
        {
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }
        KeyDown += OnKey;
        DpiChanged += (_, _) => FitToDpi();
        HandleCreated += (_, _) =>
        {
            theme.ApplyWindow(this);
            theme.ApplyList(list);
        };
        FitToDpi();
        float scale = DeviceDpi / 96f;
        Size = new Size((int)(960 * scale), (int)(640 * scale));
        MinimumSize = new Size((int)(560 * scale), (int)(380 * scale));
        UpdateButtons();
        Shown += async (_, _) =>
        {
            banner.Visible = !AppSettings.BannerDismissed && !ZipOpensWithMxZip();
            if (archive != null && listing != null)
                await LoadArchive(archive, listing);
            else
                ShowEmpty();
        };
    }

    // ----- layout -----

    ToolButton Tool(string label, Icons.Painter painter, Action action, bool popup = false)
    {
        var b = new ToolButton(theme, label, painter);
        // a dialog runs after the click returns: opened inside the handler it would hold an accessibility Invoke
        // (screen readers, UI automation) until it closed. A popup menu is not modal and must open at once: shown
        // later it closes again as the click finishes.
        b.Click += (_, _) =>
        {
            if (popup)
                action();
            else
                BeginInvoke(action);
        };
        return b;
    }

    Control Separator() => new Panel { Width = 1, BackColor = theme.Border, Margin = new Padding(6, 14, 6, 14) };

    void FitToDpi()
    {
        int dpi = DeviceDpi;
        float s = dpi / 96f;
        foreach (Control c in toolbar.Controls)
        {
            if (c is ToolButton tb)
                tb.Fit(dpi);
            else
                c.Height = (int)(34 * s);
        }
        toolbar.Height = (int)(70 * s);
        banner.Fit(dpi);
        pathBar.Height = (int)(30 * s);
        upButton.Width = (int)(32 * s);
        upButton.Image = Icons.Render(Icons.Up, (int)(16 * s), upButton.Enabled ? theme.Text : theme.Disabled, theme.Accent);
        status.Height = (int)(26 * s);
        progress.Width = (int)(160 * s);
        cancelLink.Width = (int)(48 * s);
        statusText.Padding = new Padding((int)(8 * s), 0, 0, 0);
        location.Padding = new Padding((int)(4 * s), 0, 0, 0);
        foreach (CardButton card in empty.Controls.OfType<Control>().SelectMany(Descendants).OfType<CardButton>())
            card.Fit(dpi);
        emptyPicture.Image = Icons.Render(Icons.ArchiveBox, (int)(64 * s), theme.Muted, theme.Accent);
        PlaceSettings();
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
        string text = (e.Header?.Text ?? "") + (e.ColumnIndex == sortColumn ? (sortDesc ? "  ▼" : "  ▲") : "");
        var r = Rectangle.Inflate(e.Bounds, -(int)(8 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, text, list.Font, r, theme.Muted, flags);
    }

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null)
            return;
        bool selected = e.Item.Selected;
        var g = e.Graphics;
        var row = e.Bounds;
        using (var back = new SolidBrush(selected ? theme.Selection : theme.Window))
            g.FillRectangle(back, row);
        if (selected && e.ColumnIndex == 0)  // an accent bar marks the selected row
            using (var bar = new SolidBrush(theme.Accent))
                g.FillRectangle(bar, row.Left, row.Top, Math.Max(2, (int)(2 * DeviceDpi / 96f)), row.Height);
        int pad = (int)(6 * DeviceDpi / 96f);
        var textRect = Rectangle.Inflate(row, -pad, 0);
        if (e.ColumnIndex == 0 && list.SmallImageList != null && e.Item.ImageIndex >= 0)
        {
            var img = list.SmallImageList.Images[e.Item.ImageIndex];
            g.DrawImage(img, row.Left + pad, row.Top + (row.Height - img.Height) / 2, img.Width, img.Height);
            textRect = new Rectangle(row.Left + pad * 2 + img.Width, row.Top, row.Width - pad * 3 - img.Width, row.Height);
        }
        var align = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        var color = e.ColumnIndex == 0 ? theme.Text : theme.Muted;
        TextRenderer.DrawText(g, e.SubItem.Text, list.Font, textRect, color,
                              align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>The last column takes the rest of the width, so the header has no blank end.</summary>
    void FitColumns()
    {
        if (list.Columns.Count < 4)
            return;
        int others = list.Columns[0].Width + list.Columns[1].Width + list.Columns[2].Width;
        list.Columns[3].Width = Math.Max((int)(130 * DeviceDpi / 96f), list.ClientSize.Width - others);
    }

    static IEnumerable<Control> Descendants(Control c) =>
        new[] { c }.Concat(c.Controls.OfType<Control>().SelectMany(Descendants));

    /// <summary>환경설정 sits at the right end of the toolbar.</summary>
    void PlaceSettings()
    {
        int used = toolbar.Controls.OfType<Control>().Where(c => c != settingsButton)
                          .Sum(c => c.Width + c.Margin.Horizontal) + toolbar.Padding.Horizontal;
        int gap = toolbar.ClientSize.Width - used - settingsButton.Width - settingsButton.Margin.Horizontal;
        settingsButton.Margin = new Padding(Math.Max(2, gap), settingsButton.Margin.Top, 2, settingsButton.Margin.Bottom);
    }

    readonly PictureBox emptyPicture = new() { SizeMode = PictureBoxSizeMode.AutoSize, Anchor = AnchorStyles.None };

    void BuildEmptyPage()
    {
        empty.Dock = DockStyle.Fill;
        empty.BackColor = theme.Window;
        empty.AllowDrop = true;
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Anchor = AnchorStyles.None,
            BackColor = theme.Window,
        };
        var title = new Label
        {
            Text = "파일을 끌어 놓으세요",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = theme.Text,
            Font = new Font(Font.FontFamily, Font.Size * 1.35f),
            Margin = new Padding(0, 14, 0, 6),
        };
        var sub = new Label
        {
            Text = "압축 파일은 열고, 일반 파일과 폴더는 새로 압축합니다.",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = theme.Muted,
            Margin = new Padding(0, 0, 0, 22),
        };
        var cards = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, BackColor = theme.Window };
        var openCard = new CardButton(theme, "압축 파일 열기", Icons.Open) { Margin = new Padding(0, 0, 12, 0) };
        openCard.Click += (_, _) => BeginInvoke(OpenArchiveDialog);
        var newCard = new CardButton(theme, "새로 압축하기", Icons.NewArchive) { Margin = new Padding(0) };
        newCard.Click += (_, _) => BeginInvoke(NewArchiveDialog);
        cards.Controls.Add(openCard);
        cards.Controls.Add(newCard);
        foreach (Control c in new Control[] { emptyPicture, title, sub, cards })
            table.Controls.Add(c);
        empty.Controls.Add(table);
        empty.Resize += (_, _) => table.Location = new Point(Math.Max(0, (empty.Width - table.Width) / 2),
                                                             Math.Max(0, (empty.Height - table.Height) / 2 - 20));
        table.SizeChanged += (_, _) => table.Location = new Point(Math.Max(0, (empty.Width - table.Width) / 2),
                                                                  Math.Max(0, (empty.Height - table.Height) / 2 - 20));
    }

    void ShowEmpty()
    {
        archive = null;
        password = null;
        entries = new();
        Text = "MxZip";
        list.Visible = false;
        pathBar.Visible = false;
        empty.Visible = true;
        statusText.Text = "";
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool open = archive != null && running == null;
        bool selected = SelectedEntries().Count > 0;
        openButton.Enabled = running == null;
        newButton.Enabled = running == null;
        extractButton.Enabled = open && entries.Count > 0;
        addButton.Enabled = open && editable;
        deleteButton.Enabled = open && editable && selected;
        testButton.Enabled = open;
        infoButton.Enabled = open;
        flatButton.Enabled = open;
        flatButton.Emphasis = flat;
        bool up = !flat && current.Length > 0;
        if (upButton.Enabled != up || upButton.Image == null)
        {
            upButton.Enabled = up;
            float s = DeviceDpi / 96f;
            upButton.Image = Icons.Render(Icons.Up, (int)(16 * s), up ? theme.Text : theme.Disabled, theme.Accent);
        }
    }

    // ----- loading and showing an archive -----

    async Task LoadArchive(string path, Task<EngineResult> listing)
    {
        archive = path;
        Text = Path.GetFileName(path) + " - MxZip";
        empty.Visible = false;
        list.Visible = true;
        pathBar.Visible = true;
        list.Items.Clear();
        location.Text = Path.GetFileName(path);
        statusText.Text = "목록을 읽는 중...";
        UpdateButtons();
        var r = await listing;
        while (!r.Ok && r.Error == "password")
        {
            string? pw = PasswordDialog.Ask(this, Path.GetFileName(path), password != null);
            if (pw == null)
            {
                statusText.Text = "암호가 필요합니다.";
                return;
            }
            password = pw;
            statusText.Text = "목록을 읽는 중...";
            r = await Engine.List(path, password);
        }
        if (!r.Ok)
        {
            Warn(r.Message);
            ShowEmpty();
            return;
        }
        entries = r.Entries;
        editable = r.Editable;
        format = r.Format;
        BuildFolders();
        string keep = folders.ContainsKey(current) ? current : "";
        ShowFolder(keep);
    }

    void Reload() => _ = LoadArchive(archive!, Engine.List(archive!, password));

    void BuildFolders()
    {
        folders.Clear();
        folders[""] = new();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            string name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.Length == 0)
                continue;
            int slash = -1;  // every parent folder, also those the archive lists only through their files
            while ((slash = name.IndexOf('/', slash + 1)) >= 0)
            {
                string dir = name[..(slash + 1)];
                if (seenDirs.Add(dir))
                    AddChild(dir, new Entry(dir, true, 0, 0, "", false));
            }
            if (e.IsDir)
            {
                string dir = name + "/";
                if (seenDirs.Add(dir))
                    AddChild(dir, e with { Name = dir });
                else
                    Replace(dir, e with { Name = dir });  // the listed folder carries the date
            }
            else
                AddChild(name, e with { Name = name });
        }
    }

    static string ParentOf(string path)
    {
        string p = path.TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..(i + 1)];
    }

    void AddChild(string path, Entry e)
    {
        string parent = ParentOf(path);
        if (!folders.TryGetValue(parent, out var kids))
            folders[parent] = kids = new();
        kids.Add(e);
        if (e.IsDir && !folders.ContainsKey(path))
            folders[path] = new();
    }

    void Replace(string path, Entry e)
    {
        var kids = folders[ParentOf(path)];
        int i = kids.FindIndex(k => string.Equals(k.Name, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            kids[i] = e;
    }

    static string Leaf(string path)
    {
        string p = path.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    void ShowFolder(string folder)
    {
        current = folder;
        IEnumerable<Entry> kids = flat
            ? folders.Values.SelectMany(v => v).Where(e => !e.IsDir)
            : folders.TryGetValue(folder, out var k) ? k : new List<Entry>();
        Func<Entry, string> label = flat ? e => e.Name : e => Leaf(e.Name);
        IEnumerable<Entry> ordered = sortColumn switch
        {
            1 => kids.OrderBy(e => e.Size),
            2 => kids.OrderBy(e => e.CSize),
            3 => kids.OrderBy(e => e.Modified, StringComparer.Ordinal),
            _ => kids.OrderBy(label, StringComparer.CurrentCultureIgnoreCase),
        };
        if (sortDesc)
            ordered = ordered.Reverse();
        ordered = ordered.OrderBy(e => e.IsDir ? 0 : 1);  // folders first; OrderBy is stable
        list.BeginUpdate();
        list.Items.Clear();
        if (!flat && folder.Length > 0)
            list.Items.Add(new ListViewItem(new[] { "..", "", "", "" }, icons.Folder) { Tag = null });
        foreach (var e in ordered)
        {
            string name = label(e);
            list.Items.Add(new ListViewItem(new[]
            {
                name + (e.Encrypted ? "  🔒" : ""),
                e.IsDir ? "" : FormatSize(e.Size),
                e.IsDir ? "" : FormatSize(e.CSize),
                e.Modified,
            }, e.IsDir ? icons.Folder : icons.ForFile(name)) { Tag = e });
        }
        list.EndUpdate();
        string where = flat ? "모든 파일" : string.Join("  ›  ", folder.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        location.Text = Path.GetFileName(archive) + (where.Length > 0 ? "  ›  " + where : "");
        ShowSummary();
        UpdateButtons();
    }

    void ShowSummary()
    {
        long files = entries.Count(e => !e.IsDir), total = entries.Where(e => !e.IsDir).Sum(e => e.Size);
        statusText.Text = $"{format} · 파일 {files:N0}개 · {FormatSize(total)}" + (password != null ? " · 암호 사용" : "") +
                          (editable ? "" : " · 읽기 전용(파일 추가·삭제는 ZIP만)");
    }

    static string FormatSize(long n) => n < 1024 ? $"{n:N0} B" :
        n < 1024 * 1024 ? $"{n / 1024.0:N1} KB" :
        n < 1024L * 1024 * 1024 ? $"{n / (1024.0 * 1024):N1} MB" : $"{n / (1024.0 * 1024 * 1024):N2} GB";

    void Sort(int column)
    {
        sortDesc = column == sortColumn && !sortDesc;
        sortColumn = column;
        ShowFolder(current);
    }

    void GoUp()
    {
        if (flat || current.Length == 0)
            return;
        string from = current;
        ShowFolder(ParentOf(current));
        foreach (ListViewItem it in list.Items)
            if (it.Tag is Entry e && string.Equals(e.Name, from, StringComparison.OrdinalIgnoreCase))
            {
                it.Selected = it.Focused = true;
                it.EnsureVisible();
            }
    }

    void ToggleFlat()
    {
        flat = !flat;
        flatButton.Emphasis = flat;
        flatButton.Fit(DeviceDpi);
        ShowFolder(flat ? current : (folders.ContainsKey(current) ? current : ""));
    }

    List<Entry> SelectedEntries() => list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<Entry>().ToList();

    /// <summary>The member names a selection stands for: a folder means every file under it.</summary>
    List<string> MembersOf(IEnumerable<Entry> chosen)
    {
        var members = new List<string>();
        foreach (var c in chosen)
        {
            if (!c.IsDir)
                members.Add(OriginalName(c));
            else
                members.AddRange(entries.Where(x => x.Name.Replace('\\', '/').StartsWith(c.Name, StringComparison.Ordinal))
                                        .Select(x => x.Name));
        }
        return members.Distinct().ToList();
    }

    string OriginalName(Entry e) =>
        entries.FirstOrDefault(x => string.Equals(x.Name.Replace('\\', '/').TrimEnd('/'), e.Name.TrimEnd('/'),
                                                  StringComparison.Ordinal))?.Name ?? e.Name;

    // ----- engine jobs: one at a time, progress and 취소 in the status bar -----

    async Task<EngineResult?> RunJob(string what, Func<string?, IProgress<(int, string)>, EngineJob> start)
    {
        bool asked = false;
        while (true)
        {
            var report = new Progress<(int percent, string name)>(p =>
            {
                progress.Value = Math.Clamp(p.percent, 0, 100);
                statusText.Text = $"{what} {p.percent}%  {p.name}";
            });
            running = start(password, report);
            progress.Value = 0;
            progress.Visible = cancelLink.Visible = true;
            statusText.Text = what;
            UseWaitCursor = true;
            UpdateButtons();
            EngineResult r;
            try
            {
                r = await running.Result;
            }
            finally
            {
                running = null;
                UseWaitCursor = false;
                progress.Visible = cancelLink.Visible = false;
                UpdateButtons();
            }
            if (r.Ok || r.Error != "password")
            {
                if (r.Error == "cancel")
                    statusText.Text = "취소했습니다.";
                else if (archive != null)
                    ShowSummary();
                return r;
            }
            // ZIPs list their names without a password: it is asked for when the data is needed
            string? pw = PasswordDialog.Ask(this, Path.GetFileName(archive!), asked || password != null);
            if (pw == null)
                return null;
            password = pw;
            asked = true;
        }
    }

    async void OpenItem(ListViewItem? item)
    {
        if (item == null || running != null)
            return;
        if (item.Tag is not Entry e)
        {
            GoUp();
            return;
        }
        if (e.IsDir)
        {
            if (flat)
                ToggleFlat();
            ShowFolder(e.Name);
            return;
        }
        // one file: extract it into a private temp folder, then let Windows open it
        string dir = Path.Combine(Path.GetTempPath(), "MxZip", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string arc = archive!;
        var r = await RunJob($"{Leaf(e.Name)} 여는 중...", (pw, p) =>
            Engine.Run(new[] { "--extract-to", arc, dir }, Array.Empty<string>(), new[] { OriginalName(e) }, pw, p));
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
        {
            Warn(r.Message);
            return;
        }
        string file = Path.Combine(dir, e.Name.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    void ShowExtractMenu()
    {
        if (archive == null)
            return;
        var menu = new ContextMenuStrip();
        var chosen = SelectedEntries();
        string stem = Stem(archive);
        if (chosen.Count > 0)
        {
            menu.Items.Add($"선택한 {chosen.Count}개 항목 풀기...", null, (_, _) => ExtractChosen(chosen, choose: true));
            menu.Items.Add(new ToolStripSeparator());
        }
        // the shell menu's four, in its order
        menu.Items.Add("여기에 풀기(&Z)", null, (_, _) => ExtractAll(Where.Here));
        menu.Items.Add("알아서 풀기(&Z)", null, (_, _) => ExtractAll(Where.Smart));
        menu.Items.Add($"{stem}\\ 에 풀기(&E)", null, (_, _) => ExtractAll(Where.Folder));
        menu.Items.Add("위치를 골라서 풀기(&B)...", null, (_, _) => ExtractAll(Where.Choose));
        menu.Show(extractButton, new Point(0, extractButton.Height));
    }

    enum Where { Choose, Folder, Here, Smart }

    /// <summary>알아서 풀기: whether the archive holds a single top-level item (then it goes right here).</summary>
    bool SingleTop() =>
        entries.Select(e => e.Name.Replace('\\', '/').TrimStart('/').Split('/')[0])
               .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;

    static string Stem(string path)
    {
        string name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    static string UniqueDir(string path)
    {
        string p = path;
        for (int i = 2; Directory.Exists(p) || File.Exists(p); i++)
            p = $"{path} ({i})";
        return p;
    }

    void ExtractAll(Where where)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(archive!))!;
        string? dest = where switch
        {
            Where.Here => parent,
            Where.Smart => SingleTop() ? parent : UniqueDir(Path.Combine(parent, Stem(archive!))),
            Where.Folder => UniqueDir(Path.Combine(parent, Stem(archive!))),
            _ => ChooseFolder("풀 위치", parent) is string c ? UniqueDir(Path.Combine(c, Stem(archive!))) : null,
        };
        if (dest != null)
            _ = Extract(Array.Empty<string>(), dest, rename: dest == parent);  // never overwrite next to the archive
    }

    void ExtractChosen(List<Entry> chosen, bool choose)
    {
        if (chosen.Count == 0)
        {
            Info("풀 항목을 고르세요.");
            return;
        }
        string parent = Path.GetDirectoryName(Path.GetFullPath(archive!))!;
        string? dest = choose ? ChooseFolder("선택 항목을 풀 위치", parent) : parent;
        if (dest != null)
            _ = Extract(MembersOf(chosen), dest, rename: true);
    }

    /// <summary>rename: files already there are kept and the new ones get "name (2)"; a fresh folder is just filled.</summary>
    async Task Extract(IReadOnlyCollection<string> members, string dest, bool rename)
    {
        string arc = archive!;
        var r = await RunJob(members.Count > 0 ? $"{members.Count:N0}개 항목 푸는 중..." : "푸는 중...", (pw, p) =>
            Engine.Run(new[] { "--extract-to", arc, dest }, rename ? new[] { "--rename" } : Array.Empty<string>(),
                       members.Count > 0 ? members : null, pw, p));
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
        {
            Warn(r.Message);
            return;
        }
        statusText.Text = $"풀었습니다: {dest}";
        if (settings.ExtractOpenFolder || members.Count > 0)
            Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true })?.Dispose();
    }

    string? ChooseFolder(string title, string start)
    {
        using var dlg = new FolderBrowserDialog { Description = title, InitialDirectory = start, UseDescriptionForTitle = true };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    async void TestArchive()
    {
        if (archive == null)
            return;
        string arc = archive;
        var r = await RunJob("오류 검사 중...", (pw, p) =>
            Engine.Run(new[] { "--test-json", arc }, Array.Empty<string>(), null, pw, p));
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
            Warn(r.Message);
        else if (r.Bad.Count > 0)
            Warn("손상된 항목이 있습니다:\n" + string.Join("\n", r.Bad.Take(30)) + (r.Bad.Count > 30 ? $"\n외 {r.Bad.Count - 30}개" : ""));
        else
            Info("이상 없음: 모든 항목이 정상입니다.");
    }

    void AddFilesDialog()
    {
        if (archive == null || !editable)
            return;
        using var dlg = new OpenFileDialog { Title = "추가할 파일", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _ = AddPaths(dlg.FileNames);
    }

    async Task AddPaths(IReadOnlyCollection<string> paths)
    {
        string arc = archive!;
        string into = flat ? "" : current;
        var r = await RunJob("추가하는 중...", (pw, p) =>
            Engine.Run(new[] { "--add-json", arc }, into.Length > 0 ? new[] { "--into", into } : Array.Empty<string>(),
                       paths, pw, p));
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
            Warn(r.Message);
        Reload();
    }

    async void DeleteSelected()
    {
        var chosen = SelectedEntries();
        if (archive == null || !editable || chosen.Count == 0 || running != null)
            return;
        var members = MembersOf(chosen);
        if (MessageBox.Show(this, $"{chosen.Count}개 항목을 압축 파일에서 삭제할까요?", "MxZip", MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        string arc = archive;
        var r = await RunJob("삭제하는 중...", (pw, p) =>
            Engine.Run(new[] { "--delete-json", arc }, Array.Empty<string>(), members, pw, p));
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
            Warn(r.Message);
        Reload();
    }

    void ShowInfo()
    {
        if (archive == null)
            return;
        var fi = new FileInfo(archive);
        long files = entries.Count(e => !e.IsDir), dirs = folders.Count - 1;
        long size = entries.Where(e => !e.IsDir).Sum(e => e.Size), packed = entries.Where(e => !e.IsDir).Sum(e => e.CSize);
        int encrypted = entries.Count(e => e.Encrypted);
        string ratio = size > 0 ? $"{100.0 * packed / size:N1}%" : "-";
        var text = new StringBuilder()
            .AppendLine($"파일: {fi.Name}")
            .AppendLine($"위치: {fi.DirectoryName}")
            .AppendLine($"형식: {format}")
            .AppendLine($"압축 파일 크기: {FormatSize(fi.Exists ? fi.Length : 0)}")
            .AppendLine($"파일 {files:N0}개, 폴더 {dirs:N0}개")
            .AppendLine($"원본 크기: {FormatSize(size)}")
            .AppendLine($"압축률: {ratio}")
            .AppendLine($"암호: {(encrypted > 0 ? $"{encrypted:N0}개 항목" : "없음")}")
            .Append($"파일 추가·삭제: {(editable ? "가능" : "불가 (ZIP만)")}");
        MessageBox.Show(this, text.ToString(), "압축 파일 정보 - MxZip", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ----- opening and creating -----

    const string ArchiveFilter = "압축 파일|*.zip;*.zipx;*.7z;*.rar;*.tar;*.tgz;*.gz;*.bz2;*.xz;*.zst;*.egg;*.alz;*.lzh;*.cab;" +
                                 "*.iso;*.img;*.wim;*.arj;*.lzma;*.zpaq;*.tbz;*.tbz2;*.txz;*.tzst;*.001|모든 파일|*.*";

    void OpenArchiveDialog()
    {
        using var dlg = new OpenFileDialog { Title = "압축 파일 열기", Filter = ArchiveFilter };
        if (archive != null)
            dlg.InitialDirectory = Path.GetDirectoryName(archive);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenArchive(dlg.FileName);
    }

    void OpenArchive(string path)
    {
        if (running != null)
            return;
        password = null;
        current = "";
        flat = false;
        _ = LoadArchive(Path.GetFullPath(path), Engine.List(Path.GetFullPath(path), null));
    }

    void NewArchiveDialog()
    {
        using var dlg = new OpenFileDialog { Title = "압축할 파일", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            Compress(dlg.FileNames);
    }

    /// <summary>MxZip's compress window (format, level, password, split) for these files and folders.</summary>
    static void Compress(IEnumerable<string> paths) =>
        Engine.Forward("--compress-dialog-list", Engine.ListFile(paths));

    static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".7z", ".rar", ".tar", ".tgz", ".gz", ".bz2", ".xz", ".zst", ".egg", ".alz", ".lzh", ".lha",
        ".cab", ".iso", ".img", ".wim", ".arj", ".lzma", ".zpaq", ".tbz", ".tbz2", ".txz", ".tzst", ".001", ".udf",
        ".msi", ".vhd", ".vhdx", ".dmg", ".cpio", ".rpm", ".deb", ".z", ".taz", ".br", ".lz", ".lz4",
    };

    static bool IsArchive(string path) => File.Exists(path) && ArchiveExts.Contains(Path.GetExtension(path));

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && running == null ? DragDropEffects.Copy
                                                                                           : DragDropEffects.None;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;
        if (paths.Length == 1 && IsArchive(paths[0]) && (archive == null || !editable))
            OpenArchive(paths[0]);  // an archive dropped on the window opens
        else if (archive != null && editable)
            _ = AddPaths(paths);  // onto an open ZIP: added to the folder shown
        else
            Compress(paths);  // anything else: a new archive
    }

    // ----- default app notice -----

    static bool ZipOpensWithMxZip()
    {
        var sb = new StringBuilder(1024);
        uint n = (uint)sb.Capacity;
        if (AssocQueryString(0, 2, ".zip", "open", sb, ref n) != 0)  // ASSOCSTR_EXECUTABLE
            return false;
        string dir = Path.GetDirectoryName(sb.ToString()) ?? "";
        return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'),
                             StringComparison.OrdinalIgnoreCase);
    }

    void OpenDefaultApps()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=MxZip") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? outBuf, ref uint outLen);

    // ----- keys and messages -----

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.O)
            OpenArchiveDialog();
        else if (e.Control && e.KeyCode == Keys.N)
            NewArchiveDialog();
        else if (archive == null)
            return;
        else if (e.KeyCode == Keys.Back && list.Focused)
            GoUp();
        else if (e.KeyCode == Keys.Delete && list.Focused)
            DeleteSelected();
        else if (e.KeyCode == Keys.F5)
            Reload();
        else if (e.Control && e.KeyCode == Keys.A && list.Focused)
        {
            foreach (ListViewItem it in list.Items)
                it.Selected = it.Tag != null;
        }
        else if (e.Control && e.KeyCode == Keys.E)
            ShowExtractMenu();
        else
            return;
        e.Handled = e.SuppressKeyPress = true;
    }

    void Warn(string message) => MessageBox.Show(this, message, "MxZip", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    void Info(string message) => MessageBox.Show(this, message, "MxZip", MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running?.Cancel();
        base.OnFormClosing(e);
    }
}
