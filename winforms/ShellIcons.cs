using System.Runtime.InteropServices;

namespace MxZip.Viewer;

/// <summary>Explorer's small icons by extension (no file needs to exist), cached per extension.</summary>
sealed class ShellIcons
{
    public ImageList List { get; } = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    public int Folder { get; }
    readonly Dictionary<string, int> byExt = new(StringComparer.OrdinalIgnoreCase);

    public ShellIcons()
    {
        int scale = Math.Max(1, (int)Math.Round(GetDpiForSystem() / 96.0 * 16)) ;
        List.ImageSize = new Size(scale, scale);
        Folder = Add("folder", FILE_ATTRIBUTE_DIRECTORY);
    }

    public int ForFile(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Length == 0)
            ext = ".";
        if (!byExt.TryGetValue(ext, out int i))
            byExt[ext] = i = Add("x" + ext, FILE_ATTRIBUTE_NORMAL);
        return i;
    }

    int Add(string fakeName, uint attrs)
    {
        var info = new SHFILEINFOW();
        IntPtr ok = SHGetFileInfoW(fakeName, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(),
                                   SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            List.Images.Add(new Bitmap(List.ImageSize.Width, List.ImageSize.Height));
            return List.Images.Count - 1;
        }
        using (var icon = Icon.FromHandle(info.hIcon))
            List.Images.Add(icon.ToBitmap());
        DestroyIcon(info.hIcon);
        return List.Images.Count - 1;
    }

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();
}
