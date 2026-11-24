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
    // the 새 압축 window's defaults (환경설정 > 압축)
    public string CompressFormat { get; private init; } = "zip";
    public int CompressLevel { get; private init; } = 6;
    public string CompressDir { get; private init; } = "";
    public string CompressSplit { get; private init; } = "";
    public bool CompressOpenFolder { get; private init; }

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
                CompressFormat = S("compress_format", "zip"),
                CompressLevel = r.TryGetProperty("compress_level", out var cl) && cl.TryGetInt32(out int lv)
                    ? Math.Clamp(lv, 0, 9) : 6,
                CompressDir = S("compress_dir", ""),
                CompressSplit = S("compress_split", ""),
                CompressOpenFolder = B("compress_open_folder"),
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                               "MxZip", "settings.json");

    /// <summary>Writes MxZip's "theme" setting ("dark" / "light"), keeping every other setting as it is.</summary>
    public static void SaveTheme(string theme)
    {
        var node = File.Exists(SettingsPath)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(SettingsPath)) as System.Text.Json.Nodes.JsonObject
            : null;
        node ??= new System.Text.Json.Nodes.JsonObject();
        node["theme"] = theme;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string tmp = SettingsPath + ".viewer.tmp";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // Korean stays readable
        };
        File.WriteAllText(tmp, node.ToJsonString(options), new System.Text.UTF8Encoding(false));
        File.Move(tmp, SettingsPath, overwrite: true);
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
