using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Laesst Windows selbst ein Design anwenden - wie ein Doppelklick auf die
/// .theme-Datei.
///
/// Warum nicht nur Registry, Custom.theme und SystemParametersInfo bzw.
/// IDesktopWallpaper: Der Designdienst von Windows haelt das aktuelle Design
/// im Speicher und schreibt es beim Neustart des Explorers und beim Abmelden
/// zurueck - in die Registry und in Custom.theme. Ein Anwender sah die
/// wiederhergestellte Volltonfarbe danach jedes Mal wieder schwarz, obwohl
/// das Tool genau dieselben Werte geschrieben hatte wie die Einstellungen-App
/// (per Vergleich der Registry vorher/nachher belegt). Die Einstellungen-App
/// aendert das Design im Dienst, das Tool nur die Dateien. Dasselbe hat
/// CLInstall im Betrieb gelernt: Dauerhaft wirkt ein Design nur, wenn Windows
/// es selbst anwendet.
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
    /// <summary>So beginnen die Dateien unter ...\Themes im Profil.</summary>
    public const string DateiPraefix = "DaSi-Wiederherstellung";

    /// <summary>
    /// Ein neuer Name fuer jedes Anwenden: Eine Design-Datei, die schon die
    /// aktuelle ist, wendet Windows nicht noch einmal an (unter Windows 11
    /// geprueft) - ein zweites Wiederherstellen haette sonst nichts bewirkt.
    /// </summary>
    public static string NeuerDateiName() => $"{DateiPraefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.theme";

    /// <summary>Was <see cref="AnwendenUndWarten"/> ergab.</summary>
    /// <param name="Gestartet">Liess sich das Anwenden starten?</param>
    /// <param name="Uebernommen">Hat Windows es innerhalb der Wartezeit uebernommen?</param>
    public sealed record Ergebnis(bool Gestartet, bool Uebernommen, string Datei, string? Fehler);

    /// <summary>
    /// Schreibt <paramref name="inhalt"/> als neue Design-Datei in
    /// <paramref name="ordner"/>, laesst Windows sie anwenden und wartet, bis
    /// <paramref name="uebernommen"/> (bekommt den Pfad der neuen Datei) zutrifft
    /// (hoechstens <paramref name="hoechstens"/>).
    /// Schliesst das dabei geoeffnete Einstellungen-Fenster und raeumt danach
    /// fruehere DaSi-Designs weg - bzw. die neue Datei, wenn Windows sie nicht
    /// uebernommen hat.
    /// </summary>
    public static async Task<Ergebnis> AnwendenUndWarten(string ordner, string inhalt, string? benutzer,
                                                         Func<string, bool> uebernommen, TimeSpan hoechstens)
    {
        Directory.CreateDirectory(ordner);
        var datei = Path.Combine(ordner, NeuerDateiName());
        DesignDatei.DateiSchreiben(datei, inhalt, new System.Text.UnicodeEncoding(false, true));
        var start = DateTime.Now;
        var (ok, fehler) = await Anwenden(datei, benutzer);
        bool da = false;
        if (ok)
        {
            var uhr = Stopwatch.StartNew();
            while (!(da = uebernommen(datei)) && uhr.Elapsed < hoechstens) await Task.Delay(500);
            await Task.Delay(1500);
            EinstellungenSchliessen(start);
        }
        Aufraeumen(ordner, da ? datei : null, verwerfen: da ? null : datei);
        return new Ergebnis(ok, da, datei, fehler);
    }

    /// <summary>Fruehere DaSi-Designs loeschen - ausser <paramref name="behalten"/>; <paramref name="verwerfen"/> in jedem Fall.</summary>
    private static void Aufraeumen(string ordner, string? behalten, string? verwerfen)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ordner, DateiPraefix + "*.theme"))
            {
                bool weg = string.Equals(f, verwerfen, StringComparison.OrdinalIgnoreCase)
                           || (behalten is not null && !string.Equals(f, behalten, StringComparison.OrdinalIgnoreCase));
                if (weg) try { File.Delete(f); } catch { /* naechste */ }
            }
        }
        catch { /* bleibt eben liegen */ }
    }

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
