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
/// Zentraler Prozess-Starter. Kapselt das Muster aus dem PowerShell-Skript
/// (ProcessStartInfo, Ausgabe abfangen, ActiveProcesses-Tracking, Cancel).
/// </summary>
public sealed class ProcessRunner
{
    private readonly CancellationTokenState _cancel;

    public ProcessRunner(CancellationTokenState cancel) => _cancel = cancel;

    /// <summary>
    /// Fuehrt eine EXE aus und faengt stdout+stderr ab.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string fileName, string arguments,
        Encoding? outputEncoding = null, int timeoutMs = 0)
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

        var proc = new Process { StartInfo = psi };
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

            return new ProcessResult(proc.ExitCode, combined, timedOut ? "Zeitueberschreitung" : null);
        }
        finally
        {
            _cancel.Unregister(proc.Id);
        }
    }

    /// <summary>
    /// Startet einen Befehl ueber "cmd /c ... > tempfile 2>&1" und liest die
    /// Ausgabe aus der Temp-Datei. Noetig, wo der Direktstart des winget-
    /// App-Execution-Alias mit "Zugriff verweigert" scheitert.
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

        var proc = new Process { StartInfo = psi };
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

        return new ProcessResult(proc.ExitCode, output, timedOut ? "Zeitueberschreitung" : null);
    }

    /// <summary>
    /// Robocopy-Lauf mit fortlaufender Ausgabe ins Log.
    /// Rueckgabe ist der Robocopy-ExitCode (>=8 = Fehler).
    /// </summary>
    public async Task<int> RunRobocopyAsync(string arguments, Action<string> onLine)
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
            StandardOutputEncoding = Encoding.GetEncoding(850)
        };

        var proc = new Process { StartInfo = psi };
        var queue = new ConcurrentQueue<string>();
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) queue.Enqueue(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) queue.Enqueue("[STDERR] " + e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { onLine($"[FEHLER] Robocopy konnte nicht gestartet werden: {ex.Message}"); return 999; }

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
