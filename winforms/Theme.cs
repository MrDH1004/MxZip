using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MxZip.Viewer;

/// <summary>
/// MxZip's palette: graphite neutrals and the logo's blue as a rare accent (primary button, selection, ratio bar,
/// focus ring). Light / dark follows the "theme" setting MxZip saves (system / light / dark).
/// Contrast (WCAG): light accent #2f5bd3 under white text 5.9:1; dark accent #7c9cf5 under #0f0f10 text 7.2:1;
/// muted text #737373 on #fafafa 4.5:1, #85858b on #19191b 4.8:1.
/// </summary>
sealed class Theme
{
    /// <summary>맑은 고딕 by its family name, which resolves on every Windows language.</summary>
    public const string DefaultFamily = "Malgun Gothic";

    public bool Dark { get; private set; }

    /// <summary>
    /// Window: the page behind everything. Card: raised surfaces (toolbar, list, summary, dialogs). Panel: inputs and
    /// quiet strips. Border: the hairline (text at about 8 %). Track: the empty part of the ratio bar.
    /// Secondary / Muted: the two steps of quieter text.
    /// </summary>
    public Color Window, Card, Panel, Toolbar, Hover, Pressed, Border, Text, Secondary, Muted, Disabled, Accent,
                 AccentText, AccentHover, AccentPressed, BannerBack, BannerText, Selection, Track;

    Theme(bool dark) => Use(dark);

    /// <summary>Switches the palette in place (the window re-applies it: 다크 모드 button).</summary>
    public void Use(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Window = Hex("#141415"); Card = Hex("#19191b"); Panel = Hex("#232325"); Toolbar = Card;
            Hover = Hex("#262628"); Pressed = Hex("#303033"); Border = Hex("#2c2c2f");
            Text = Hex("#ededed"); Secondary = Hex("#a1a1a6"); Muted = Hex("#85858b"); Disabled = Hex("#5c5c62");
            Accent = Hex("#7c9cf5"); AccentText = Hex("#0f0f10"); AccentHover = Hex("#93aef7"); AccentPressed = Hex("#6a8ae6");
            Selection = Hex("#2b3245"); Track = Hex("#2e2e32");
        }
        else
        {
            Window = Hex("#fafafa"); Card = Hex("#ffffff"); Panel = Hex("#f4f4f5"); Toolbar = Card;
            Hover = Hex("#f0f0f1"); Pressed = Hex("#e4e4e7"); Border = Hex("#e6e6e6");
            Text = Hex("#171717"); Secondary = Hex("#525252"); Muted = Hex("#737373"); Disabled = Hex("#a3a3a3");
            Accent = Hex("#2f5bd3"); AccentText = Hex("#ffffff"); AccentHover = Hex("#284fbb"); AccentPressed = Hex("#2244a3");
            Selection = Hex("#e6ebfa"); Track = Hex("#e8e8ea");
        }
        BannerBack = Panel;
        BannerText = Secondary;
    }

    static Color Hex(string s) => ColorTranslator.FromHtml(s);

    public static Theme Load(string theme) => new(IsDark(theme));

    /// <summary>Whether MxZip's theme setting ("system" / "light" / "dark") means dark right now.</summary>
    public static bool IsDark(string theme) => theme switch
    {
        "dark" => true,
        "light" => false,
        _ => SystemDark(),
    };

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
        list.BackColor = Card;
        list.ForeColor = Text;
        // dark: Windows' dark Explorer style for dark scroll bars; the column lines it draws below the rows are
        // painted over by ThemedListView (rows and header are drawn by the viewer)
        SetWindowTheme(list.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
        if (list is ThemedListView themed)
            themed.EmptyAreaColor = Card;  // both Explorer styles draw column lines below the rows
        list.Invalidate();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
}
