using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Startet den Explorer (Desktop und Taskleiste) neu - noetig, damit er die
/// zurueckgeschriebenen Taskleisten-Symbole liest: Er liest sie nur beim Start.
///
/// Beendet wird nur der Explorer dieser Sitzung. Windows startet die Shell
/// danach von selbst neu (AutoRestartShell) - als angemeldeter Benutzer, ohne
/// Adminrechte. Kommt sie nicht zurueck, startet das Tool sie selbst.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerNeustart
{
    /// <summary>Ist die Taskleiste da?</summary>
    public static bool TaskleisteDa() => FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;

    /// <returns>Ok: die Taskleiste ist wieder da. VonSelbst: Windows hat die Shell selbst neu gestartet.</returns>
    public static async Task<(bool Ok, bool VonSelbst)> NeuStarten(TimeSpan warten)
    {
        int sitzung;
        using (var ich = Process.GetCurrentProcess()) sitzung = ich.SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { if (p.SessionId == sitzung) p.Kill(); }
            catch { /* naechster */ }
            finally { p.Dispose(); }
        }

        // Erst weg sein lassen - sonst gilt die alte Taskleiste als "wieder da".
        for (int i = 0; i < 40 && TaskleisteDa(); i++) await Task.Delay(250);

        if (await WarteAufTaskleiste(warten)) return (true, true);

        try
        {
            using var neu = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { return (false, false); }
        return (await WarteAufTaskleiste(warten), false);
    }

    private static async Task<bool> WarteAufTaskleiste(TimeSpan warten)
    {
        var uhr = Stopwatch.StartNew();
        while (uhr.Elapsed < warten)
        {
            await Task.Delay(500);
            if (TaskleisteDa())
            {
                // Desktop und Symbole brauchen nach der Taskleiste noch einen Moment.
                await Task.Delay(2500);
                return true;
            }
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
}
