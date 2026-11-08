namespace MxZip.Viewer;

/// <summary>
/// The archive list. In the dark theme it uses Windows' dark Explorer style for its scroll bars; that style also
/// draws column lines through the empty area below the last row, so that area is painted over in the window
/// colour after each paint (the rows themselves are drawn by the viewer).
/// </summary>
sealed class ThemedListView : ListView
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color EmptyAreaColor { get; set; } = Color.Empty;

    public ThemedListView()
    {
        DoubleBuffered = true;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || EmptyAreaColor.IsEmpty || View != View.Details)  // WM_PAINT
            return;
        int top = 0;
        if (Items.Count > 0)
        {
            var last = GetItemRect(Items.Count - 1);
            top = last.Bottom;
        }
        else
        {
            top = HeaderHeight();
        }
        if (top >= ClientSize.Height)
            return;
        using var g = Graphics.FromHwnd(Handle);
        using var b = new SolidBrush(EmptyAreaColor);
        g.FillRectangle(b, 0, Math.Max(0, top), ClientSize.Width, ClientSize.Height - Math.Max(0, top));
    }

    int HeaderHeight()
    {
        IntPtr header = SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);  // LVM_GETHEADER
        return header != IntPtr.Zero && GetWindowRect(header, out var r) ? r.Bottom - r.Top : 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
