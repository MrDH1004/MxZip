using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MxZip.Viewer;

/// <summary>
/// MxZip's palette (appearance.py): teal accent #16776a, GitHub-like neutrals; dark as the Qt app's dark theme.
/// Light / dark follows the "theme" setting MxZip saves (system / light / dark).
/// </summary>
sealed class Theme
{
    public bool Dark { get; }
    public Color Window, Panel, Toolbar, Hover, Pressed, Border, Text, Muted, Disabled, Accent, AccentText,
                 Card, CardHover, BannerBack, BannerText, Selection;

    Theme(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Window = Hex("#20242b"); Panel = Hex("#252b33"); Toolbar = Hex("#1b1f25"); Hover = Hex("#303844");
            Pressed = Hex("#3d4755"); Border = Hex("#343c47"); Text = Hex("#e6edf3"); Muted = Hex("#9ba6b3");
            Disabled = Hex("#596575"); Accent = Hex("#74c8b8"); AccentText = Hex("#0f1a18");
            Card = Hex("#2a3039"); CardHover = Hex("#343c47"); BannerBack = Hex("#453b20"); BannerText = Hex("#e3b341");
            Selection = Hex("#203c36");
        }
        else
        {
            Window = Hex("#ffffff"); Panel = Hex("#f5f7fa"); Toolbar = Hex("#f4f6f7"); Hover = Hex("#e9edf2");
            Pressed = Hex("#dce3e6"); Border = Hex("#d0d7de"); Text = Hex("#1f2328"); Muted = Hex("#57606a");
            Disabled = Hex("#b0b7bf"); Accent = Hex("#16776a"); AccentText = Hex("#ffffff");
            Card = Hex("#f8fafb"); CardHover = Hex("#eaf4f1"); BannerBack = Hex("#fff8c5"); BannerText = Hex("#9a6700");
            Selection = Hex("#eaf4f1");
        }
    }

    static Color Hex(string s) => ColorTranslator.FromHtml(s);

    public static Theme Load(string theme)
    {
        bool dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemDark(),
        };
        return new Theme(dark);
    }

    static bool SystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Dark title bar, dark scroll bars and list header for a dark theme.</summary>
    public void ApplyWindow(Form form)
    {
        int on = Dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    public void ApplyList(ListView list)
    {
        list.BackColor = Window;
        list.ForeColor = Text;
        if (!Dark)
            return;
        // no DarkMode_* visual style on the list: both dark list styles draw column lines through the whole
        // list; rows and header are drawn by the viewer instead (only the scroll bars stay light)
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
