using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Laesst Windows selbst ein Design anwenden - wie ein Doppelklick auf die
/// .theme-Datei.
///
/// Warum nicht nur Registry und SystemParametersInfo: Unter Windows 11 merkt
/// sich der Explorer den Hintergrund zusaetzlich in einem eigenen Speicher und
/// schreibt ihn beim Neustart zurueck. Ein Anwender sah die wiederhergestellte
/// Volltonfarbe nach dem Neustart des Explorers bzw. von Windows wieder
/// schwarz - obwohl Registry und Custom.theme stimmten. Ueber die
/// Einstellungen-App gewaehlt, blieb sie. Dasselbe hat CLInstall im Betrieb
/// gelernt: Sofort und dauerhaft wirkt ein Design nur, wenn Windows es selbst
/// anwendet.
///
/// Und zwar ALS das Konto am Bildschirm: Das Tool laeuft erhoeht, womoeglich
/// unter einem anderen Konto (Administratorschutz von Windows 11:
/// "ADMIN_Anwender", oder fremde Anmeldedaten bei der UAC-Abfrage). Wie in
/// CLInstall startet deshalb eine kurzlebige geplante Aufgabe fuer den
/// angemeldeten Benutzer explorer.exe mit der Design-Datei - ohne Kennwort,
/// weil er angemeldet ist, und ohne Erhoehung.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDesign
{
    /// <summary>So heisst die Datei unter ...\Themes im Profil.</summary>
    public const string DateiName = "DaSi-Wiederherstellung.theme";

    /// <summary>
    /// Wendet die Design-Datei an. Mit bekanntem Benutzer ("DOMAENE\Name")
    /// ueber eine geplante Aufgabe in seiner Sitzung, sonst direkt.
    /// </summary>
    /// <returns>Ob es sich starten liess, und was dabei schiefging.</returns>
    public static async Task<(bool Ok, string? Fehler)> Anwenden(string datei, string? benutzer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(benutzer))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("explorer.exe", Anfuehrung(datei)) { UseShellExecute = false });
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        var skript = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            $"$design = {PsText(datei)}",
            $"$benutzer = {PsText(benutzer)}",
            "$aufgabe = 'DaSi-Design-' + [guid]::NewGuid().ToString('N')",
            "$aktion = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'explorer.exe') -Argument ('\"' + $design + '\"')",
            "$wer = New-ScheduledTaskPrincipal -UserId $benutzer -LogonType Interactive -RunLevel Limited",
            "Register-ScheduledTask -TaskName $aufgabe -Action $aktion -Principal $wer -Force | Out-Null",
            "try {",
            "  Start-ScheduledTask -TaskName $aufgabe",
            "  $bis = (Get-Date).AddSeconds(20)",
            "  do { Start-Sleep -Milliseconds 500 } while ((Get-ScheduledTask -TaskName $aufgabe).State -eq 'Running' -and (Get-Date) -lt $bis)",
            "  Write-Output 'GESTARTET'",
            "} finally {",
            "  Unregister-ScheduledTask -TaskName $aufgabe -Confirm:$false -ErrorAction SilentlyContinue",
            "}");
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + WingetInstaller.Encode(skript))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe ließ sich nicht starten.");
            var aus = p.StandardOutput.ReadToEndAsync(ct);
            var err = p.StandardError.ReadToEndAsync(ct);
            using var zeit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            zeit.CancelAfter(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(zeit.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* weg */ }
                return (false, "Zeitlimit überschritten");
            }
            var text = (await aus) + (await err);
            return text.Contains("GESTARTET", StringComparison.Ordinal)
                ? (true, null)
                : (false, ErsteZeile(text) ?? $"PowerShell endete mit {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Ein Doppelklick auf eine Design-Datei oeffnet ausserdem die
    /// Einstellungen - das Fenster, wenn es seit <paramref name="seit"/>
    /// aufging, wieder schliessen.
    /// </summary>
    public static void EinstellungenSchliessen(DateTime seit)
    {
        foreach (var p in Process.GetProcessesByName("SystemSettings"))
        {
            try { if (p.StartTime >= seit.AddSeconds(-1)) p.Kill(); }
            catch { /* naechster */ }
            finally { p.Dispose(); }
        }
    }

    private static string Anfuehrung(string s) => "\"" + s + "\"";

    /// <summary>PowerShell-Zeichenkette in einfachen Anfuehrungszeichen.</summary>
    private static string PsText(string s) => "'" + s.Replace("'", "''") + "'";

    private static string? ErsteZeile(string text)
        => text.Split('\n').Select(z => z.Trim()).FirstOrDefault(z => z.Length > 0);
}
