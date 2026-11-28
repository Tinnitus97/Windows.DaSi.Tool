using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Restarts Explorer (desktop and taskbar) - needed so it reads the restored
/// taskbar icons: it reads them only at startup.
///
/// Only this session's Explorer is ended. Windows then restarts the shell by
/// itself (AutoRestartShell) - as the signed-in user, without admin rights.
/// If it does not come back, the tool starts it itself.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerRestart
{
    /// <summary>Is the taskbar there?</summary>
    public static bool TaskbarPresent() => FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;

    /// <returns>Ok: the taskbar is back. VonSelbst: Windows restarted the shell by itself.</returns>
    public static async Task<(bool Ok, bool VonSelbst)> Restart(TimeSpan warten)
    {
        int sitzung;
        using (var ich = Process.GetCurrentProcess()) sitzung = ich.SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { if (p.SessionId == sitzung) p.Kill(); }
            catch { /* next one */ }
            finally { p.Dispose(); }
        }

        // Let it disappear first - otherwise the old taskbar would count as "back".
        for (int i = 0; i < 40 && TaskbarPresent(); i++) await Task.Delay(250);

        if (await WaitForTaskbar(warten)) return (true, true);

        try
        {
            using var neu = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { return (false, false); }
        return (await WaitForTaskbar(warten), false);
    }

    private static async Task<bool> WaitForTaskbar(TimeSpan warten)
    {
        var uhr = Stopwatch.StartNew();
        while (uhr.Elapsed < warten)
        {
            await Task.Delay(500);
            if (TaskbarPresent())
            {
                // The desktop and its icons need another moment after the taskbar.
                await Task.Delay(2500);
                return true;
            }
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
}
