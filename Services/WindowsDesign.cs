using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Lets Windows apply a theme itself - like double-clicking the .theme file.
///
/// Why not just the registry, Custom.theme and SystemParametersInfo or
/// IDesktopWallpaper: Windows' theme service keeps the current theme in
/// memory and writes it back when Explorer restarts and at sign-out - into
/// the registry and into Custom.theme. One user saw the restored solid
/// color turn black again every time, although the tool had written exactly
/// the same values as the Settings app (proven by comparing the registry
/// before and after). The Settings app changes the theme inside the
/// service; the tool only changed the files. CLInstall learned the same in
/// practice: a theme only sticks if Windows applies it itself.
///
/// And it has to happen AS the account at the screen: the tool runs
/// elevated, possibly under another account (Windows 11 administrator
/// protection: "ADMIN_Anwender", or other credentials in the UAC prompt).
/// As in CLInstall, a short-lived scheduled task for the signed-in user
/// therefore starts explorer.exe with the theme file - without a password,
/// because the user is signed in, and without elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDesign
{
    /// <summary>The names of the files under ...\Themes in the profile start like this.</summary>
    public const string DateiPraefix = "DaSi-Wiederherstellung";

    /// <summary>
    /// A new name for every apply: Windows does not apply a theme file again
    /// that already is the current one (verified on Windows 11) - a second
    /// restore would otherwise have had no effect.
    /// </summary>
    public static string NewFileName() => $"{DateiPraefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.theme";

    /// <summary>What <see cref="ApplyAndWait"/> resulted in.</summary>
    /// <param name="Gestartet">Could applying be started?</param>
    /// <param name="Uebernommen">Did Windows take it over within the waiting time?</param>
    public sealed record Ergebnis(bool Gestartet, bool Uebernommen, string Datei, string? Fehler);

    /// <summary>
    /// Writes <paramref name="inhalt"/> as a new theme file into
    /// <paramref name="ordner"/>, lets Windows apply it and waits until
    /// <paramref name="uebernommen"/> (given the new file's path) is true (at
    /// most <paramref name="hoechstens"/>).
    /// Closes the Settings window that opens along the way. If the new file is
    /// the current theme afterwards (<paramref name="istAktuell"/>), earlier
    /// DaSi themes are deleted; otherwise the new file is. The current theme is
    /// never deleted.
    /// </summary>
    public static async Task<Ergebnis> ApplyAndWait(string ordner, string inhalt, string? benutzer,
                                                         Func<string, bool> uebernommen, Func<string, bool> istAktuell,
                                                         TimeSpan hoechstens)
    {
        Directory.CreateDirectory(ordner);
        var datei = Path.Combine(ordner, NewFileName());
        ThemeFile.WriteFile(datei, inhalt, new System.Text.UnicodeEncoding(false, true));
        var start = DateTime.Now;
        var (ok, fehler) = await Apply(datei, benutzer);
        bool da = false;
        if (ok)
        {
            var uhr = Stopwatch.StartNew();
            while (!(da = uebernommen(datei)) && uhr.Elapsed < hoechstens) await Task.Delay(500);
            await Task.Delay(1500);
            CloseSettings(start);
        }
        bool aktuell = istAktuell(datei);
        CleanUp(ordner, aktuell ? datei : null, verwerfen: aktuell ? null : datei);
        return new Ergebnis(ok, da, datei, fehler);
    }

    /// <summary>Delete earlier DaSi themes - except <paramref name="behalten"/>; <paramref name="verwerfen"/> in any case.</summary>
    private static void CleanUp(string ordner, string? behalten, string? verwerfen)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ordner, DateiPraefix + "*.theme"))
            {
                bool weg = string.Equals(f, verwerfen, StringComparison.OrdinalIgnoreCase)
                           || (behalten is not null && !string.Equals(f, behalten, StringComparison.OrdinalIgnoreCase));
                if (weg) try { File.Delete(f); } catch { /* next one */ }
            }
        }
        catch { /* then it stays */ }
    }

    /// <summary>
    /// Applies the theme file. With a known user ("DOMAIN\name") through a
    /// scheduled task in that user's session, otherwise directly.
    /// </summary>
    /// <returns>Whether it could be started, and what went wrong.</returns>
    public static async Task<(bool Ok, string? Fehler)> Apply(string datei, string? benutzer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(benutzer))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("explorer.exe", Quote(datei)) { UseShellExecute = false });
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        var skript = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            $"$design = {PsLiteral(datei)}",
            $"$benutzer = {PsLiteral(benutzer)}",
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
                try { p.Kill(true); } catch { /* gone */ }
                return (false, "Zeitlimit überschritten");
            }
            var text = (await aus) + (await err);
            return text.Contains("GESTARTET", StringComparison.Ordinal)
                ? (true, null)
                : (false, FirstLine(text) ?? $"PowerShell endete mit {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Double-clicking a theme file also opens Settings - close that window
    /// again if it opened since <paramref name="seit"/>.
    /// </summary>
    public static void CloseSettings(DateTime seit)
    {
        foreach (var p in Process.GetProcessesByName("SystemSettings"))
        {
            try { if (p.StartTime >= seit.AddSeconds(-1)) p.Kill(); }
            catch { /* next one */ }
            finally { p.Dispose(); }
        }
    }

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>A PowerShell string in single quotes.</summary>
    private static string PsLiteral(string s) => "'" + s.Replace("'", "''") + "'";

    private static string? FirstLine(string text)
        => text.Split('\n').Select(z => z.Trim()).FirstOrDefault(z => z.Length > 0);
}
