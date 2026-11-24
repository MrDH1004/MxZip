using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MxZip.Viewer;

/// <summary>
/// MxZip's archive window: a toolbar of the common actions, a folder-by-folder list of what the engine reported,
/// and an empty "drop files here" page when no archive is open. Every format operation is an engine call
/// (progress and cancel in the status bar); new archives are set up in the 새 압축 window, settings open MxZip's own.
/// </summary>
sealed class ViewerForm : Form
{
    AppSettings settings;
    FileSystemWatcher? settingsWatcher;
    System.Windows.Forms.Timer? settingsDebounce;
    readonly Theme theme;
    string? archive;
    string? password;
    List<Entry> entries = new();
    List<string> damaged = new();  // a ZIP cut short: members left out of the list (shown in the status line)
    bool editable;
    string format = "";
    readonly Dictionary<string, List<Entry>> folders = new(StringComparer.OrdinalIgnoreCase);  // "a/b/" -> children
    string current = "";
    bool flat;
    int sortColumn;
    bool sortDesc;
    EngineJob? running;

    // toolbar: the three main actions as text buttons on the left, the archive's quieter actions as icons after
    // them, 다크 모드 and 환경설정 as icons at the right end
    readonly TableLayoutPanel toolbar = new();
    readonly FlowLayoutPanel toolLeft = new(), toolRight = new();
    readonly ActionButton openButton, extractButton, newButton;
    readonly ToolButton addButton, deleteButton, testButton, infoButton, settingsButton;
    readonly ToolToggle flatButton, themeButton;
    readonly ToolTip tips = new();
    readonly Banner banner;
    readonly Banner updateBanner;
    string updateVersion = "";
    readonly SummaryCard summary;
    readonly ToolButton upButton;
    readonly TextLabel location = new();
    readonly Panel content = new();
    readonly Panel summaryGap = new();
    readonly Panel listFrame = new();
    readonly ThemedListView list = new();
    ImageList? rowHeight;
    ListViewItem? hoverItem;
    readonly Panel empty = new();
    readonly Panel status = new();
    readonly TextLabel statusText = new();
    readonly ThinProgress progress;
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
        Font = new Font(settings.FontFamily.Length > 0 ? settings.FontFamily : Theme.DefaultFamily, settings.FontSize);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        AllowDrop = true;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // toolbar
        openButton = TextTool("열기", Icons.Open, ButtonKind.Secondary, OpenArchiveDialog, "Ctrl+O");
        extractButton = TextTool("풀기", Icons.Extract, ButtonKind.Primary, ShowExtractMenu, "Ctrl+E", popup: true);
        newButton = TextTool("새로 압축", Icons.NewArchive, ButtonKind.Secondary, NewArchiveDialog, "Ctrl+N");
        addButton = Tool("파일 추가", Icons.AddFile, AddFilesDialog);
        deleteButton = Tool("파일 삭제", Icons.DeleteFile, DeleteSelected, "Delete");
        testButton = Tool("오류 검사", Icons.Test, TestArchive);
        infoButton = Tool("정보", Icons.Info, ShowInfo);
        flatButton = ToggleTool("전체 보기", Icons.FlatList, ToggleFlat);
        themeButton = ToggleTool("다크 모드", Icons.Moon, ToggleTheme);
        themeButton.Checked = theme.Dark;
        settingsButton = Tool("환경설정", Icons.Settings, () => Engine.Forward("--settings"));
        foreach (var flow in new[] { toolLeft, toolRight })
        {
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.WrapContents = false;
            flow.Margin = new Padding(0);
            flow.Padding = new Padding(0);
        }
        toolLeft.Anchor = AnchorStyles.Left;
        toolRight.Anchor = AnchorStyles.Right;
        toolLeft.Controls.AddRange(new Control[] { openButton, extractButton, newButton, addButton, deleteButton,
                                                   testButton, infoButton, flatButton });
        toolRight.Controls.AddRange(new Control[] { themeButton, settingsButton });
        toolbar.Dock = DockStyle.Top;
        toolbar.ColumnCount = 3;
        toolbar.RowCount = 1;
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.Controls.Add(toolLeft, 0, 0);
        toolbar.Controls.Add(toolRight, 2, 0);
        toolbar.Paint += (_, e) =>  // a hairline under the toolbar
        {
            using var pen = new Pen(theme.Border);
            e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };

        banner = new Banner(theme, "ZIP 파일을 열 기본 앱을 MxZip으로 바꾸시겠습니까?", "기본 앱 설정 열기");
        banner.Action.Click += (_, _) => BeginInvoke(OpenDefaultApps);
        banner.CloseButton.Click += (_, _) =>
        {
            banner.Visible = false;
            try { AppSettings.BannerDismissed = true; } catch { }
        };
        banner.Visible = false;
        tips.SetToolTip(banner.CloseButton, "알림 닫기");

        // the automatic update check MxZip's old home window made: a notice with 지금 업데이트
        updateBanner = new Banner(theme, "새 버전이 있습니다.", "지금 업데이트");
        updateBanner.Action.Click += (_, _) => BeginInvoke(StartUpdate);
        updateBanner.CloseButton.Click += (_, _) => updateBanner.Visible = false;
        updateBanner.Visible = false;
        tips.SetToolTip(updateBanner.CloseButton, "알림 닫기");

        // the archive's summary card; inside it the up button and where in the archive the list is
        summary = new SummaryCard(theme) { Dock = DockStyle.Top, Visible = false };
        upButton = new ToolButton(theme, "위로", Icons.Up, box: 28, iconSize: 16);
        upButton.Click += (_, _) => GoUp();
        tips.SetToolTip(upButton, "위로 (Backspace)");
        location.AutoSize = false;
        location.TextAlign = ContentAlignment.MiddleLeft;
        location.AutoEllipsis = true;
        location.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        summary.Crumbs.Controls.Add(upButton);
        summary.Crumbs.Controls.Add(location);
        summary.Crumbs.Resize += (_, _) => PlaceCrumbs();
        summaryGap.Dock = DockStyle.Top;
        summaryGap.Visible = false;

        // list, framed as a card
        icons = new ShellIcons();
        listFrame.Dock = DockStyle.Fill;
        listFrame.Visible = false;
        listFrame.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float k = DeviceDpi / 96f;
            using var path = Typo.RoundRect(new RectangleF(0.5f, 0.5f, listFrame.Width - 1.5f, listFrame.Height - 1.5f), 8 * k);
            using var fill = new SolidBrush(theme.Card);
            using var pen = new Pen(theme.Border);
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
        list.GotFocus += (_, _) =>
        {
            if (list.FocusedItem == null && list.Items.Count > 0)
                list.Items[0].Focused = true;  // a keyboard user lands on a visible row
            list.Invalidate();
        };
        list.LostFocus += (_, _) => list.Invalidate();
        list.MouseMove += (_, e) => SetHover(list.GetItemAt(e.X, e.Y));
        list.MouseLeave += (_, _) => SetHover(null);
        list.ItemActivate += (_, _) => OpenItem(list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null);
        list.ColumnClick += (_, e) => Sort(e.Column);
        list.SelectedIndexChanged += (_, _) =>
        {
            UpdateButtons();
            QueueSelection();
        };
        list.ContextMenuStrip = itemMenu;
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
        // docking starts from the back of the z-order (the last added): summary, the gap under it, then the list
        content.Controls.Add(listFrame);
        content.Controls.Add(summaryGap);
        content.Controls.Add(summary);
        content.Controls.Add(empty);

        // status bar: message, and while working a progress bar with 취소
        status.Dock = DockStyle.Bottom;
        status.BackColor = theme.Window;
        status.Paint += (_, e) =>  // a hairline above the status bar
        {
            using var pen = new Pen(theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, status.Width, 0);
        };
        statusText.Dock = DockStyle.Fill;
        statusText.AutoSize = false;
        statusText.TextAlign = ContentAlignment.MiddleLeft;
        statusText.ForeColor = theme.Muted;
        statusText.AutoEllipsis = true;
        progress = new ThinProgress(theme) { Dock = DockStyle.Right };
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

        // docking starts from the back of the z-order (the last added): status, toolbar, notices, content
        Controls.Add(content);
        Controls.Add(updateBanner);
        Controls.Add(banner);
        Controls.Add(toolbar);
        Controls.Add(status);

        foreach (Control c in new Control[] { content, summary, listFrame })
            c.AllowDrop = true;
        ApplyColors();
        foreach (Control c in new Control[] { this, list, empty, content, summary, listFrame })
        {
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }
        KeyDown += OnKey;
        DpiChanged += (_, _) => FitToDpi();
        HandleCreated += (_, _) =>
        {
            // every child gets its window now, in collection order: a hidden control whose window is made later
            // (when shown) is moved in the collection to its new window's z-order, which reverses toolbar buttons
            // and the docking order of the content. Only the controls that exist now are covered: one added later
            // to a toolbar row or the content must get its window before it is hidden.
            foreach (Control c in Descendants(this).ToList())
                _ = c.Handle;
            theme.ApplyWindow(this);
            theme.ApplyList(list);
        };
        FitToDpi();
        float scale = DeviceDpi / 96f;
        Size = new Size((int)(960 * scale), (int)(640 * scale));
        UpdateButtons();
        Shown += async (_, _) =>
        {
            banner.Visible = !AppSettings.BannerDismissed && !ZipOpensWithMxZip();
            _ = CheckForUpdate();
            WatchSettings();
            if (archive != null && listing != null)
                await LoadArchive(archive, listing);
            else
            {
                ShowEmpty();
                // MxZip.exe --updated (the installer's relaunch) hands over "MxZip x.y.z(으)로 업데이트했습니다."
                string? notice = Environment.GetEnvironmentVariable("MXZIP_VIEWER_NOTICE");
                if (!string.IsNullOrEmpty(notice))
                    statusText.Text = notice;
            }
        };
    }

    // ----- layout -----

    /// <summary>An icon-only toolbar button; its label is the tooltip and the accessible name.</summary>
    ToolButton Tool(string label, Icons.Painter painter, Action action, string? keys = null)
    {
        var b = new ToolButton(theme, label, painter);
        tips.SetToolTip(b, keys != null ? $"{label} ({keys})" : label);
        // a dialog runs after the click returns: opened inside the handler it would hold an accessibility Invoke
        // (screen readers, UI automation) until it closed
        b.Click += (_, _) => BeginInvoke(action);
        return b;
    }

    /// <summary>
    /// One of the three main toolbar actions as a text button. A popup menu is not modal and must open at once
    /// (shown later it closes again as the click finishes); a dialog runs after the click returns.
    /// </summary>
    ActionButton TextTool(string label, Icons.Painter painter, ButtonKind kind, Action action, string keys, bool popup = false)
    {
        var b = new ActionButton(theme, label, kind, painter) { DropDown = popup };
        tips.SetToolTip(b, $"{label} ({keys})");
        b.Click += (_, _) =>
        {
            if (popup)
                action();
            else
                BeginInvoke(action);
        };
        return b;
    }

    /// <summary>A toolbar toggle; like the buttons, its action runs after the click returns.</summary>
    ToolToggle ToggleTool(string label, Icons.Painter painter, Action action)
    {
        var t = new ToolToggle(theme, label, painter);
        tips.SetToolTip(t, label);
        t.Click += (_, _) => BeginInvoke(action);
        return t;
    }

    void FitToDpi()
    {
        int dpi = DeviceDpi;
        float s = dpi / 96f;
        int gap = (int)(8 * s), group = (int)(16 * s), iconGap = (int)(4 * s);
        foreach (Control c in Descendants(toolbar))
        {
            if (c is ToolButton tb)
            {
                tb.Fit(dpi);
                tb.Margin = new Padding(0, 0, iconGap, 0);
            }
            else if (c is ToolToggle tt)
            {
                tt.Fit(dpi);
                tt.Margin = new Padding(0, 0, iconGap, 0);
            }
            else if (c is ActionButton ab)
            {
                ab.Fit();
                ab.Margin = new Padding(0, 0, gap, 0);
            }
        }
        flatButton.Margin = settingsButton.Margin = new Padding(0);
        // 52 px, taller when a large font makes the buttons taller
        toolbar.Height = Math.Max((int)(52 * s), openButton.Height + (int)(20 * s));
        toolbar.Padding = new Padding((int)(16 * s), 0, (int)(12 * s), 1);
        banner.Fit(dpi);
        updateBanner.Fit(dpi);
        summary.Fit(dpi);
        upButton.Fit(dpi);
        summaryGap.Height = (int)(12 * s);
        content.Padding = new Padding((int)(16 * s), (int)(16 * s), (int)(16 * s), (int)(12 * s));
        int inset = Math.Max(3, (int)(3 * s));  // keeps the list's square corners inside the frame's rounded ones
        listFrame.Padding = new Padding(inset);
        // the row height: a list takes the tallest of its image lists, so an empty state list sets 32 px rows
        var rows = new ImageList { ImageSize = new Size(1, Math.Max(16, (int)(32 * s))) };
        list.StateImageList = rows;
        rowHeight?.Dispose();
        rowHeight = rows;
        status.Height = Math.Max((int)(28 * s), Typo.Measure("가", statusText.Font).Height + (int)(8 * s));
        progress.Width = (int)(160 * s);
        cancelLink.Width = (int)(48 * s);
        statusText.Padding = new Padding((int)(16 * s), 0, 0, 0);
        PlaceCrumbs();
        UpdateButtons();  // which archive buttons show, and the 16 px before the icon group
        foreach (var b in Descendants(empty).OfType<ActionButton>())
            b.Fit();
        emptyPicture.Image?.Dispose();
        emptyPicture.Image = Icons.StorageBox((int)(112 * s), theme.Muted, theme.Card, theme.Panel, theme.Track,
                                              theme.Accent, theme.AccentText);
        // never narrower than the toolbar with every button shown (and the 16 px group gap)
        int W(Control c) => c.Width + c.Margin.Horizontal;
        int toolbarWidth = toolLeft.Controls.OfType<Control>().Sum(W) + toolRight.Controls.OfType<Control>().Sum(W) +
                           toolbar.Padding.Horizontal + group + (int)(24 * s);
        int frame = Width - ClientSize.Width;
        MinimumSize = new Size(Math.Max((int)(560 * s), toolbarWidth + frame), (int)(420 * s));
    }

    /// <summary>The 위로 button at the left of the summary's bottom row, the folder path after it.</summary>
    void PlaceCrumbs()
    {
        float s = DeviceDpi / 96f;
        var row = summary.Crumbs;
        upButton.Location = new Point((int)(8 * s), (row.Height - upButton.Height) / 2);
        int x = upButton.Right + (int)(6 * s);
        int h = Typo.Measure("가", location.Font).Height + (int)(4 * s);
        location.Bounds = new Rectangle(x, (row.Height - h) / 2, Math.Max(0, row.Width - x - (int)(12 * s)), h);
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var back = new SolidBrush(theme.Card))
            e.Graphics.FillRectangle(back, e.Bounds);
        using (var line = new Pen(theme.Border))
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        bool right = e.Header?.TextAlign == HorizontalAlignment.Right;
        string text = (e.Header?.Text ?? "") + (e.ColumnIndex == sortColumn ? (sortDesc ? "  ▼" : "  ▲") : "");
        var r = Rectangle.Inflate(e.Bounds, -(int)(12 * DeviceDpi / 96f), 0);
        Typo.Draw(e.Graphics, text, list.Font, r, e.ColumnIndex == sortColumn ? theme.Secondary : theme.Muted,
                  right ? StringAlignment.Far : StringAlignment.Near);
    }

    const string LockMark = "  🔒";

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null)
            return;
        bool selected = e.Item.Selected;
        var g = e.Graphics;
        var row = e.Bounds;
        float s = DeviceDpi / 96f;
        using (var back = new SolidBrush(selected ? theme.Selection : e.Item == hoverItem ? theme.Hover : theme.Card))
            g.FillRectangle(back, row);
        int pad = (int)(12 * s);
        var textRect = Rectangle.Inflate(row, -pad, 0);
        if (e.ColumnIndex == 0 && list.SmallImageList != null && e.Item.ImageIndex >= 0)
        {
            var img = list.SmallImageList.Images[e.Item.ImageIndex];
            g.DrawImage(img, row.Left + pad, row.Top + (row.Height - img.Height) / 2, img.Width, img.Height);
            int left = row.Left + pad + img.Width + (int)(8 * s);
            textRect = new Rectangle(left, row.Top, row.Right - left - (int)(8 * s), row.Height);
        }
        bool right = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
        var color = e.ColumnIndex == 0 ? theme.Text : e.ColumnIndex == 3 ? theme.Muted : theme.Secondary;
        string text = e.SubItem.Text;
        // the 🔒 of an encrypted entry is drawn as a line icon (GDI+ has no colour-emoji fallback)
        bool locked = e.ColumnIndex == 0 && text.EndsWith(LockMark, StringComparison.Ordinal);
        if (locked)
        {
            text = text[..^LockMark.Length];
            int iconPx = (int)(14 * s);
            int w = Math.Min(Typo.Measure(text, list.Font).Width, Math.Max(0, textRect.Width - iconPx - (int)(6 * s)));
            Typo.Draw(g, text, list.Font, new Rectangle(textRect.Left, textRect.Top, w, textRect.Height), color);
            if (textRect.Width >= iconPx)  // a column too narrow for the name shows no lock either
                g.DrawImage(LockIcon(iconPx), textRect.Left + w + (int)(6 * s), row.Top + (row.Height - iconPx) / 2, iconPx, iconPx);
        }
        else
            Typo.Draw(g, text, list.Font, textRect, color, right ? StringAlignment.Far : StringAlignment.Near);
        if (list.Focused && e.Item.Focused)  // keyboard position: an accent outline around the whole row
        {
            int w = Math.Max(1, (int)s);
            using var ring = new SolidBrush(theme.Accent);
            g.FillRectangle(ring, row.Left, row.Top, row.Width, w);
            g.FillRectangle(ring, row.Left, row.Bottom - w, row.Width, w);
            if (e.ColumnIndex == 0)
                g.FillRectangle(ring, row.Left, row.Top, w, row.Height);
            if (e.ColumnIndex == list.Columns.Count - 1)
                g.FillRectangle(ring, row.Right - w, row.Top, w, row.Height);
        }
    }

    Bitmap? lockIcon;
    Color lockColor;

    /// <summary>The lock of encrypted rows, drawn once per size and palette.</summary>
    Bitmap LockIcon(int px)
    {
        if (lockIcon == null || lockIcon.Width != px || lockColor != theme.Muted)
        {
            lockIcon?.Dispose();
            lockIcon = Icons.Render(Icons.Lock, px, theme.Muted, theme.Muted);
            lockColor = theme.Muted;
        }
        return lockIcon;
    }

    /// <summary>A subtle tile under the row the mouse is over.</summary>
    void SetHover(ListViewItem? item)
    {
        if (item == hoverItem)
            return;
        var old = hoverItem;
        hoverItem = item;
        if (old != null && old.ListView == list)
            list.Invalidate(old.Bounds);
        if (item != null)
            list.Invalidate(item.Bounds);
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
        emptyPicture.Margin = new Padding(0, 0, 0, 12);
        var title = new TextLabel
        {
            Text = "파일을 끌어 놓으세요",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = theme.Text,
            Tag = "text",
            Font = new Font(Font.FontFamily, Font.Size * 1.5f),
            Margin = new Padding(0, 0, 0, 6),
        };
        var sub = new TextLabel
        {
            Text = "압축 파일은 열고, 일반 파일과 폴더는 새로 압축합니다.",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = theme.Secondary,
            Tag = "secondary",
            Margin = new Padding(0, 0, 0, 24),
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            WrapContents = false,
            BackColor = theme.Window,
            Margin = new Padding(0),
        };
        var openNow = new ActionButton(theme, "압축 파일 열기", ButtonKind.Primary) { Margin = new Padding(0, 0, 8, 0) };
        openNow.Click += (_, _) => BeginInvoke(OpenArchiveDialog);
        var newNow = new ActionButton(theme, "새로 압축하기", ButtonKind.Secondary) { Margin = new Padding(0) };
        newNow.Click += (_, _) => BeginInvoke(NewArchiveDialog);
        buttons.Controls.Add(openNow);
        buttons.Controls.Add(newNow);
        foreach (Control c in new Control[] { emptyPicture, title, sub, buttons })
            table.Controls.Add(c);
        empty.Controls.Add(table);
        void Center() => table.Location = new Point(Math.Max(0, (empty.Width - table.Width) / 2),
                                                    Math.Max(0, (empty.Height - table.Height) / 2 - (int)(16 * DeviceDpi / 96f)));
        empty.Resize += (_, _) => Center();
        table.SizeChanged += (_, _) => Center();
    }

    void ShowEmpty()
    {
        archive = null;
        password = null;
        entries = new();
        damaged = new();
        Text = "MxZip";
        listFrame.Visible = summary.Visible = summaryGap.Visible = false;
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
        // archive actions are hidden, not greyed, while no archive is open; adding and deleting also on a
        // read-only archive
        bool shown = archive != null;
        extractButton.Visible = testButton.Visible = infoButton.Visible = flatButton.Visible = shown;
        addButton.Visible = deleteButton.Visible = shown && editable;
        extractButton.Enabled = open && entries.Count > 0;
        addButton.Enabled = open && editable;
        deleteButton.Enabled = open && editable && selected;
        testButton.Enabled = open;
        infoButton.Enabled = open;
        flatButton.Enabled = open;
        flatButton.Checked = flat;
        // the last visible icon has no trailing gap; the main actions keep 16 px before the icons
        newButton.Margin = new Padding(0, 0, shown ? (int)(16 * DeviceDpi / 96f) : 0, 0);
        upButton.Enabled = !flat && current.Length > 0;
    }

    // ----- loading and showing an archive -----

    async Task LoadArchive(string path, Task<EngineResult> listing)
    {
        archive = path;
        editable = false;  // until the listing says so: adding stays hidden for a read-only or cancelled archive
        Text = Path.GetFileName(path) + " - MxZip";
        empty.Visible = false;
        listFrame.Visible = summary.Visible = summaryGap.Visible = true;
        list.Items.Clear();
        location.Text = Path.GetFileName(path);
        summary.Show(Path.GetFileName(path), "", "", -1, "", "");
        statusText.Text = "목록을 읽는 중...";
        UpdateButtons();
        var r = await listing;
        while (!r.Ok && r.Error == "password")
        {
            string? pw = PasswordDialog.Ask(this, theme, Path.GetFileName(path), password != null);
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
        damaged = r.Bad;
        editable = r.Editable;
        format = r.Format;
        BuildFolders();
        string keep = folders.ContainsKey(current) ? current : "";
        ShowFolder(keep);
        if (r.Bad.Count > 0)  // a ZIP cut short: named once on opening, counted in the summary afterwards
            statusText.Text = $"손상된 파일 {r.Bad.Count:N0}개 제외: {string.Join(", ", r.Bad.Take(2))}" +
                              (r.Bad.Count > 2 ? " 외" : "");
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
        long packed = entries.Where(e => !e.IsDir).Sum(e => e.CSize);
        var notes = new List<string>();
        if (password != null)
            notes.Add("암호 사용");
        if (damaged.Count > 0)
            notes.Add($"손상 {damaged.Count:N0}개 제외");
        if (damaged.Count > 0 || !editable)
            notes.Add("읽기 전용");
        string tag = format.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        summary.Show(Path.GetFileName(archive) ?? "", tag.ToUpperInvariant(),
                     $"파일 {files:N0}개 · 원본 {FormatSize(total)} · 압축 후 {FormatSize(packed)}",
                     total > 0 ? (double)packed / total : -1, total > 0 ? $"원본의 {100.0 * packed / total:N1}%" : "",
                     string.Join(" · ", notes));
        ShowSelection();
    }

    /// <summary>
    /// The status bar while an archive is open and idle: what is selected ("3개 선택 · 1.2 MB"), else nothing (the
    /// archive's own figures are in the summary card). Progress, results and messages replace it as before.
    /// </summary>
    void ShowSelection()
    {
        if (archive == null || running != null)
            return;
        var chosen = SelectedEntries();
        if (chosen.Count == 0)
        {
            statusText.Text = "";
            return;
        }
        var members = new HashSet<string>(MembersOf(chosen), StringComparer.Ordinal);
        long size = entries.Where(e => !e.IsDir && members.Contains(e.Name)).Sum(e => e.Size);
        statusText.Text = $"{chosen.Count:N0}개 선택 · {FormatSize(size)}";
    }

    bool selectionQueued;

    /// <summary>Selecting many rows fires once per row: the status is worked out once, after them.</summary>
    void QueueSelection()
    {
        if (selectionQueued || !IsHandleCreated)
            return;
        selectionQueued = true;
        BeginInvoke(() =>
        {
            selectionQueued = false;
            ShowSelection();
        });
    }

    internal static string FormatSize(long n) => n < 1024 ? $"{n:N0} B" :
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
        flatButton.Checked = flat;
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

    /// <summary>askPassword: a "password" answer asks for the open archive's password and runs the job again.</summary>
    async Task<EngineResult?> RunJob(string what, Func<string?, IProgress<(int, string)>, EngineJob> start,
                                     bool askPassword = true)
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
            if (r.Ok || r.Error != "password" || !askPassword)
            {
                if (r.Error == "cancel")
                    statusText.Text = "취소했습니다.";
                else if (archive != null)
                    ShowSummary();
                return r;
            }
            // ZIPs list their names without a password: it is asked for when the data is needed
            string? pw = PasswordDialog.Ask(this, theme, Path.GetFileName(archive!), asked || password != null);
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
        if (r.Bad.Count > 0)
        {
            statusText.Text = $"풀었습니다 (손상 {r.Bad.Count:N0}개 건너뜀): {dest}";
            Warn(SkippedText(r.Bad));
        }
        if (settings.ExtractOpenFolder || members.Count > 0)
            Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>After extracting a ZIP cut short: which members were left out, and what to do about it.</summary>
    static string SkippedText(List<string> names) =>
        $"손상된 파일 {names.Count:N0}개를 건너뛰었습니다.\n\n" + Names(names, 5) + "\n\n원본을 다시 받으면 모두 풀립니다.";

    /// <summary>The first few names, one per line, and how many more there are.</summary>
    static string Names(List<string> names, int max) =>
        string.Join("\n", names.Take(max)) + (names.Count > max ? $"\n외 {names.Count - max:N0}개" : "");

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
            Warn($"손상된 항목 {r.Bad.Count:N0}개:\n" + Names(r.Bad, 10));
        else
            Info("이상 없음.");
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
        // the native task dialog: verb buttons ("삭제" / "취소") and the consequence stated up front
        var delete = new TaskDialogButton("삭제");
        var page = new TaskDialogPage
        {
            Caption = "MxZip",
            Heading = $"{chosen.Count}개 항목 삭제",
            Text = $"선택한 항목({members.Count:N0}개 파일)을 '{Path.GetFileName(archive)}'에서 지웁니다. 되돌릴 수 없습니다.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { delete, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
        };
        if (TaskDialog.ShowDialog(this, page) != delete)
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
        string ratio = size > 0 ? $"원본의 {100.0 * packed / size:N1}%" : "-";
        var text = new StringBuilder()
            .AppendLine($"파일: {fi.Name}")
            .AppendLine($"위치: {fi.DirectoryName}")
            .AppendLine($"형식: {format}")
            .AppendLine($"압축 파일 크기: {FormatSize(fi.Exists ? fi.Length : 0)}")
            .AppendLine($"파일 {files:N0}개, 폴더 {dirs:N0}개")
            .AppendLine($"원본 크기: {FormatSize(size)}")
            .AppendLine($"압축 후 크기: {ratio}")
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

    /// <summary>새로 압축: the 새 압축 window, empty; files are added there.</summary>
    void NewArchiveDialog() => Compress(Array.Empty<string>());

    /// <summary>
    /// The 새 압축 window (list, format, password, split, method) with these files and folders in its list; the job
    /// runs here with progress and 취소 in the status bar. (MxZip.exe --compress-dialog-list stays for the shell menu.)
    /// </summary>
    void Compress(IEnumerable<string> paths)
    {
        if (running != null)
            return;
        var request = CompressDialog.Ask(this, theme, paths);
        if (request != null)
            _ = CreateArchive(request);
    }

    async Task CreateArchive(CompressRequest request)
    {
        string name = request.Each ? $"{request.Paths.Count:N0}개 압축 파일" : Path.GetFileName(request.Out);
        var r = await RunJob($"압축하는 중... {name}", (_, p) => Engine.Create(request, p), askPassword: false);
        if (r == null || r.Error == "cancel")
            return;
        if (!r.Ok)
        {
            Warn(r.Message);
            return;
        }
        statusText.Text = "압축했습니다: " + string.Join(", ", r.Outputs.Take(3).Select(Path.GetFileName)) +
                          (r.Outputs.Count > 3 ? $" 외 {r.Outputs.Count - 3}개" : "");
        string? folder = Path.GetDirectoryName(r.Outputs.FirstOrDefault() ?? request.Out);
        if (settings.CompressOpenFolder && folder != null)
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        if (r.Outputs.Count == 0)
            return;
        if (!request.Each || r.Outputs.Count == 1)
            OpenArchive(r.Outputs[0]);  // a split ZIP opens from its first volume (.zip.001)
        else
            Info($"압축 파일 {r.Outputs.Count:N0}개를 만들었습니다.\n\n" + Names(r.Outputs.Select(o => Path.GetFileName(o)).ToList(), 10));
    }

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

    // ----- following MxZip's settings -----

    /// <summary>
    /// 환경설정 > 테마 (시스템 설정 / 밝게 / 어둡게) and the other options take effect in this window as soon as the
    /// settings file changes; with 시스템 설정 the window also follows Windows' own dark / light switch.
    /// </summary>
    void WatchSettings()
    {
        settingsDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        settingsDebounce.Tick += (_, _) =>
        {
            settingsDebounce.Stop();
            ReloadSettings();
        };
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxZip");
            Directory.CreateDirectory(dir);
            settingsWatcher = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // MxZip saves through a temp file and a rename: both events mean "read it again"
            settingsWatcher.Changed += (_, _) => BeginInvoke(RestartDebounce);
            settingsWatcher.Renamed += (_, _) => BeginInvoke(RestartDebounce);
            settingsWatcher.Created += (_, _) => BeginInvoke(RestartDebounce);
        }
        catch (Exception)
        {
            settingsWatcher = null;  // the window still works; it just won't pick up later changes
        }
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    void RestartDebounce()
    {
        if (IsDisposed || settingsDebounce == null)
            return;
        settingsDebounce.Stop();
        settingsDebounce.Start();
    }

    void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color
            && !IsDisposed)
            BeginInvoke(RestartDebounce);  // 시스템 설정: Windows switched between dark and light
    }

    void ReloadSettings()
    {
        settings = AppSettings.Load();
        bool dark = Theme.IsDark(Environment.GetEnvironmentVariable("MXZIP_THEME") ?? settings.Theme);
        if (dark == theme.Dark)
            return;
        theme.Use(dark);
        themeButton.Checked = dark;
        ApplyTheme();
    }

    // ----- dark / light -----

    /// <summary>다크 모드: switches at once and saves it as MxZip's theme setting (the Qt windows follow it too).</summary>
    void ToggleTheme()
    {
        theme.Use(!theme.Dark);
        themeButton.Checked = theme.Dark;
        try
        {
            AppSettings.SaveTheme(theme.Dark ? "dark" : "light");
        }
        catch (Exception e)
        {
            statusText.Text = "테마 설정을 저장하지 못했습니다: " + e.Message;
        }
        ApplyTheme();
    }

    /// <summary>Re-colours every surface from the current palette (colours set in the constructor, icons, list).</summary>
    void ApplyTheme()
    {
        SuspendLayout();
        ApplyColors();
        theme.ApplyList(list);
        theme.ApplyWindow(this);
        FitToDpi();  // icons are rendered in the palette's colours
        ResumeLayout(true);
        foreach (Control c in Descendants(this))
            c.Invalidate();
        RedrawFrame();
    }

    /// <summary>The surfaces' colours from the palette (the owner-drawn parts read it as they paint).</summary>
    void ApplyColors()
    {
        BackColor = content.BackColor = empty.BackColor = listFrame.BackColor = summaryGap.BackColor = theme.Window;
        ForeColor = theme.Text;
        toolbar.BackColor = toolLeft.BackColor = toolRight.BackColor = theme.Toolbar;
        status.BackColor = statusText.BackColor = theme.Window;
        summary.BackColor = theme.Window;
        location.BackColor = theme.Card;
        location.ForeColor = theme.Secondary;
        statusText.ForeColor = theme.Muted;
        cancelLink.LinkColor = cancelLink.ActiveLinkColor = theme.Accent;
        summary.ApplyTheme();
        banner.ApplyTheme();
        updateBanner.ApplyTheme();
        foreach (Control c in Descendants(empty))
        {
            if (c is Label label)
            {
                label.BackColor = theme.Window;
                label.ForeColor = (label.Tag as string) == "secondary" ? theme.Secondary : theme.Text;
            }
            else if (c is not ActionButton)
                c.BackColor = theme.Window;
        }
    }

    /// <summary>The title bar takes the new dark / light mode only when the frame is redrawn.</summary>
    void RedrawFrame() => SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ----- update notice -----

    /// <summary>MxZip's automatic update check (when enabled in its settings), in the background.</summary>
    async Task CheckForUpdate()
    {
        var r = await Engine.Run(new[] { "--update-check" }, Array.Empty<string>(), null, null).Result;
        if (!r.Ok || r.UpdateVersion.Length == 0 || IsDisposed)
            return;
        updateVersion = r.UpdateVersion;
        updateBanner.SetMessage($"새 버전 MxZip {updateVersion}이(가) 나왔습니다.");
        updateBanner.Visible = true;
    }

    /// <summary>The unattended update: download, check, run the installer (it closes MxZip and starts it again).</summary>
    void StartUpdate()
    {
        Engine.Forward("--update-now");
        updateBanner.SetMessage($"MxZip {updateVersion} 내려받는 중... 설치가 끝나면 MxZip이 다시 시작됩니다.");
        updateBanner.HideAction();
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
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        settingsWatcher?.Dispose();
        settingsDebounce?.Dispose();
        running?.Cancel();
        tips.Dispose();
        list.StateImageList = null;
        rowHeight?.Dispose();
        lockIcon?.Dispose();
        base.OnFormClosing(e);
    }
}
