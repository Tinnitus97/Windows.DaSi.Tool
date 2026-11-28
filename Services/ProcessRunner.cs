using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

public sealed record ProcessResult(int ExitCode, string Output, string? StartError)
{
    public bool Started => StartError is null;
}

/// <summary>
/// Central process launcher: starts programs, captures their output and
/// tracks them so Cancel can end them.
/// </summary>
public sealed class ProcessRunner
{
    private readonly CancellationTokenState _cancel;

    public ProcessRunner(CancellationTokenState cancel) => _cancel = cancel;

    /// <summary>
    /// Runs an EXE and captures stdout and stderr.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string fileName, string arguments,
        Encoding? outputEncoding = null, int timeoutMs = 0,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return new ProcessResult(-1, "", "Prozess konnte nicht gestartet werden.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }

        _cancel.Register(proc.Id);
        var sw = Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            while (!proc.HasExited)
            {
                if (_cancel.IsCancelled)
                {
                    TryKill(proc);
                    break;
                }
                if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                {
                    timedOut = true;
                    TryKill(proc);
                    break;
                }
                await Task.Delay(150);
            }

            proc.WaitForExit();
            var stdout = await outTask;
            var stderr = await errTask;
            var combined = string.Join("\r\n",
                new[] { stdout, stderr }.Where(s => !string.IsNullOrEmpty(s)));

            return new ProcessResult(proc.ExitCode, combined, timedOut ? "Zeitüberschreitung" : null);
        }
        finally
        {
            _cancel.Unregister(proc.Id);
        }
    }

    /// <summary>
    /// Runs a command through "cmd /c ... > tempfile 2>&1" and reads the output
    /// from the temp file. Needed where starting winget's app execution alias
    /// directly fails with "Access denied".
    /// </summary>
    public async Task<ProcessResult> RunViaCmdAsync(string exe, string arguments, int timeoutMs = 0)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"winget_out_{Guid.NewGuid():N}.txt");
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var args = $"/d /c \"\"{exe}\" {arguments} > \"{tmp}\" 2>&1\"";

        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return new ProcessResult(-1, "", "cmd konnte nicht gestartet werden.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }

        _cancel.Register(proc.Id);
        var sw = Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            while (!proc.HasExited)
            {
                if (_cancel.IsCancelled) { TryKill(proc); break; }
                if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs) { timedOut = true; TryKill(proc); break; }
                await Task.Delay(150);
            }
            proc.WaitForExit();
        }
        finally
        {
            _cancel.Unregister(proc.Id);
        }

        string output = "";
        try
        {
            if (File.Exists(tmp)) output = await File.ReadAllTextAsync(tmp, Encoding.UTF8);
        }
        catch { /* ignore */ }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        return new ProcessResult(proc.ExitCode, output, timedOut ? "Zeitüberschreitung" : null);
    }

    /// <summary>
    /// Recognizes a file line printed by robocopy (for the counter). File lines
    /// are indented with tabs/whitespace and contain a path. Header, option and
    /// summary lines are excluded.
    /// </summary>
    private static bool IsRobocopyFileLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        // Must be indented (robocopy indents file and folder lines).
        if (line[0] != '\t' && line[0] != ' ') return false;
        var t = line.Trim();
        if (t.Length == 0) return false;
        // Exclude summary and header lines.
        if (t.StartsWith("Insgesamt") || t.StartsWith("Total") ||
            t.StartsWith("Kopiert") || t.StartsWith("Copied") ||
            t.StartsWith("Verzeich") || t.StartsWith("Dirs") ||
            t.StartsWith("Dateien") || t.StartsWith("Files") ||
            t.StartsWith("Bytes") || t.StartsWith("Zeiten") || t.StartsWith("Times") ||
            t.StartsWith("Beschleunigt") || t.StartsWith("Speed") ||
            t.StartsWith("Optionen") || t.StartsWith("Options") ||
            t.StartsWith("Quelle") || t.StartsWith("Source") ||
            t.StartsWith("Ziel") || t.StartsWith("Dest") ||
            t.StartsWith("---") || t.StartsWith("===") ||
            t.StartsWith("ROBOCOPY") || t.StartsWith("Gestartet") || t.StartsWith("Started"))
            return false;
        return true;
    }

    /// <summary>
    /// A robocopy run with continuous output to the log.
    /// Returns robocopy's exit code (>= 8 = error).
    /// </summary>
    /// <param name="skips">
    /// Optional: error messages about files in this folder are not written to
    /// the log but collected there (see <see cref="RobocopySkips"/>).
    /// </param>
    public async Task<int> RunRobocopyAsync(string arguments, Action<string> onLine, Action<int>? onFileCounted = null,
                                            RobocopySkips? skips = null)
    {
        if (_cancel.IsCancelled) return 999;

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = SystemHelpers.ConsoleEncoding
        };

        // In fast mode, file lines are only counted (for the progress) but NOT
        // written to the log - the log stays tidy.
        bool countOnly = _cancel.FastMode && onFileCounted != null;
        int fileCount = 0;

        using var proc = new Process { StartInfo = psi };
        var queue = new ConcurrentQueue<string>();
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // The lines arrive in order (one event after another), as RobocopySkips
            // needs for its three-line messages.
            if (skips != null && skips.Swallow(e.Data)) return;
            if (countOnly && IsRobocopyFileLine(e.Data))
            {
                // Count only, no log. Display throttled (every 25 files).
                int c = System.Threading.Interlocked.Increment(ref fileCount);
                if (c % 25 == 0) onFileCounted!(c);
                return;
            }
            queue.Enqueue(e.Data);
        };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) queue.Enqueue("[STDERR] " + e.Data); };

        try { proc.Start(); }
        catch (Exception ex)
        {
            onLine(Loc.Tr($"[FEHLER] Robocopy konnte nicht gestartet werden: {ex.Message}",
                          $"[ERROR] Robocopy could not be started: {ex.Message}"));
            return 999;
        }

        // Give the robocopy process a higher priority. It is the process doing the
        // actual copying - unlike the app itself. This mainly helps when the CPU
        // (not the disk) is the bottleneck, e.g. with very many small files.
        // Errors here are not critical.
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch { /* missing rights or the like - ignore */ }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _cancel.Register(proc.Id);

        var sw = Stopwatch.StartNew();
        try
        {
            while (!proc.HasExited)
            {
                if (_cancel.IsCancelled) { TryKill(proc); break; }
                if (queue.Count > 0 && sw.ElapsedMilliseconds > 400)
                {
                    var sb = new StringBuilder();
                    while (queue.TryDequeue(out var line)) sb.AppendLine(line);
                    if (sb.Length > 0) onLine(sb.ToString().TrimEnd());
                    sw.Restart();
                }
                await Task.Delay(100);
            }
            proc.WaitForExit();
            await Task.Delay(200);
            var rest = new StringBuilder();
            while (queue.TryDequeue(out var line)) rest.AppendLine(line);
            if (rest.Length > 0) onLine(rest.ToString().TrimEnd());
            // Report the final count.
            if (countOnly && fileCount > 0) onFileCounted!(fileCount);
        }
        finally
        {
            _cancel.Unregister(proc.Id);
        }

        return proc.ExitCode;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
