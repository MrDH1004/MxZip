namespace MxZip.Viewer;

/// <summary>
/// MxZipViewer.exe [ARCHIVE]: MxZip's archive window ("Open with" / double-click, or empty to drop files on).
/// The listing starts in the engine before the window exists, so both run at once. --flags (shell menu,
/// installer, updater commands) are handed unchanged to MxZip.exe, which keeps those entry points.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1 || args.Length == 1 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            try
            {
                Engine.Forward(args);
            }
            catch (Exception e)
            {
                MessageBox.Show("MxZip을 시작하지 못했습니다: " + e.Message, "MxZip", MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
        string? archive = args.Length == 1 && File.Exists(args[0]) ? Path.GetFullPath(args[0]) : null;
        var listing = archive != null ? Engine.List(archive, null) : null;  // first: the engine starts during the window build
        ApplicationConfiguration.Initialize();
        Application.Run(new ViewerForm(archive, listing));
        return 0;
    }
}
