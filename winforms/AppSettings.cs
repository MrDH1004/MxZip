using System.Text.Json;
using Microsoft.Win32;

namespace MxZip.Viewer;

/// <summary>The few MxZip settings the viewer honours (settings.json, written by MxZip's settings dialog).</summary>
sealed class AppSettings
{
    public string Theme { get; private init; } = "system";
    public string FontFamily { get; private init; } = "";
    public float FontSize { get; private init; } = 9f;
    public bool ExtractOpenFolder { get; private init; }

    public static AppSettings Load()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "MxZip", "settings.json");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            float size = r.TryGetProperty("font_size", out var fs) && fs.TryGetDouble(out double d) ? (float)d : 10f;
            return new AppSettings
            {
                Theme = S("theme", "system"),
                FontFamily = S("font_family", ""),
                FontSize = Math.Clamp(size * 0.9f, 8f, 16f),  // Qt points at 96 dpi read slightly larger
                ExtractOpenFolder = B("extract_open_folder"),
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    const string Key = @"Software\MxZip";

    /// <summary>Whether the user closed the "make MxZip the ZIP app" notice.</summary>
    public static bool BannerDismissed
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("ViewerAssocBannerClosed") is int v && v == 1;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("ViewerAssocBannerClosed", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
