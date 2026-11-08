using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MxZip.Viewer;

/// <summary>One archive member as the engine lists it.</summary>
sealed record Entry(string Name, bool IsDir, long Size, long CSize, string Modified, bool Encrypted);

/// <summary>The engine's answer, or why it failed (Error: "password", "missing", "cancel", "message").</summary>
sealed record EngineResult(bool Ok, string Error, string Message)
{
    public List<Entry> Entries { get; init; } = new();
    public string Format { get; init; } = "";
    public bool Editable { get; init; }
    public List<string> Bad { get; init; } = new();
}

/// <summary>A running engine call: its answer, a way to stop it, and its progress (percent, current name).</summary>
sealed class EngineJob
{
    public required Task<EngineResult> Result { get; init; }
    public required Action Cancel { get; init; }
}

/// <summary>
/// The Python engine (MxZip.exe, or mxzip_main.py while developing) does every format; the viewer only asks.
/// MxZip.exe is a windowed exe without stdout, so it writes its answer to a file this class names; a password
/// goes on stdin, never on the command line. Progress comes through RESULT.progress, cancel through RESULT.cancel.
/// </summary>
static class Engine
{
    static readonly string Dir = AppContext.BaseDirectory;

    /// <summary>
    /// A start info for MxZip.exe next to the viewer with args (ArgumentList: Windows quoting done by .NET, so a
    /// path ending in a backslash survives); MXZIP_ENGINE="python.exe|C:\...\mxzip_main.py" while developing.
    /// </summary>
    static ProcessStartInfo Start(IEnumerable<string> args)
    {
        string? dev = Environment.GetEnvironmentVariable("MXZIP_ENGINE");
        ProcessStartInfo psi;
        if (!string.IsNullOrEmpty(dev) && dev.Contains('|'))
        {
            var parts = dev.Split('|', 2);
            psi = new ProcessStartInfo(parts[0]);
            psi.ArgumentList.Add(parts[1]);
        }
        else
            psi = new ProcessStartInfo(Path.Combine(Dir, "MxZip.exe"));
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Dir;
        return psi;
    }

    /// <summary>
    /// Starts command(head..., RESULT, options..., "--", operands...) without waiting. head: the command and its
    /// leading operands (archive, and for --extract-to the destination).
    /// </summary>
    public static EngineJob Run(string[] head, IEnumerable<string> options, IEnumerable<string>? operands,
                                string? password, IProgress<(int percent, string name)>? progress = null)
    {
        string result = Path.Combine(Path.GetTempPath(), $"mxzip-viewer-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var args = new List<string>(head) { result };
        if (password != null)
            args.Add("--password-stdin");
        args.AddRange(options);
        if (operands != null)
        {
            args.Add("--");
            args.AddRange(operands);
        }
        var psi = Start(args);
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Exception e)
        {
            return new EngineJob
            {
                Result = Task.FromResult(new EngineResult(false, "message", $"MxZip 엔진을 시작하지 못했습니다: {e.Message}")),
                Cancel = () => { },
            };
        }
        using (var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            if (password != null)
                stdin.Write(password);
        }
        var task = Task.Run(async () =>
        {
            string progressFile = result + ".progress";
            while (!p.WaitForExit(100))
            {
                if (progress == null || !File.Exists(progressFile))
                    continue;
                try
                {
                    var parts = File.ReadAllText(progressFile, Encoding.UTF8).Split('\t', 2);
                    if (int.TryParse(parts[0], out int pct))
                        progress.Report((pct, parts.Length > 1 ? parts[1] : ""));
                }
                catch (IOException) { }
            }
            await Task.CompletedTask;
            p.Dispose();
            return Read(result);
        });
        return new EngineJob
        {
            Result = task,
            Cancel = () => { try { File.WriteAllText(result + ".cancel", ""); } catch { } },
        };
    }

    public static Task<EngineResult> List(string archive, string? password) =>
        Run(new[] { "--list-json", archive }, Array.Empty<string>(), null, password).Result;

    static EngineResult Read(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
                return new EngineResult(false, root.GetProperty("error").GetString() ?? "message",
                                        root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
            var entries = new List<Entry>();
            if (root.TryGetProperty("entries", out var rows))
                foreach (var r in rows.EnumerateArray())
                    entries.Add(new Entry(r[0].GetString() ?? "", r[1].GetBoolean(), r[2].GetInt64(), r[3].GetInt64(),
                                          r[4].GetString() ?? "", r[5].GetBoolean()));
            var bad = new List<string>();
            if (root.TryGetProperty("bad", out var b))
                foreach (var x in b.EnumerateArray())
                    bad.Add(x.GetString() ?? "");
            return new EngineResult(true, "", "")
            {
                Entries = entries,
                Format = root.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "",
                Editable = root.TryGetProperty("editable", out var ed) && ed.GetBoolean(),
                Bad = bad,
            };
        }
        catch (Exception e)
        {
            return new EngineResult(false, "message", "MxZip 엔진이 결과를 남기지 않았습니다. " + e.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Hands work the viewer does not do itself to MxZip.exe (compress window, settings, menus).</summary>
    public static void Forward(params string[] args) => Process.Start(Start(args))?.Dispose();

    /// <summary>The UTF-8 list file the --extract-list / --compress-dialog-list commands read (and delete).</summary>
    public static string ListFile(IEnumerable<string> paths)
    {
        string f = Path.Combine(Path.GetTempPath(), $"mxzip-list-{Guid.NewGuid():N}.txt");
        File.WriteAllText(f, string.Join("\n", paths), new UTF8Encoding(false));
        return f;
    }
}
