using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Installiert winget nach, wenn es fehlt: den App-Installer als MSIX-Bundle
/// und seine Abhaengigkeiten (VCLibs, Windows App Runtime) aus dem neuesten
/// Release von github.com/microsoft/winget-cli - derselbe Weg, den Microsoft
/// fuer Rechner ohne Store beschreibt.
///
/// Typische Faelle: frisch installiertes Windows, dessen App-Installer noch
/// nicht da oder zu alt ist; LTSC- und Server-Ausgaben ohne Store; ein
/// App-Installer, dessen winget mit den heutigen Paketquellen nicht mehr
/// zurechtkommt.
/// </summary>
public static class WingetInstaller
{
    /// <summary>"latest/download" leitet immer auf die neueste Fassung um.</summary>
    public const string ReleaseBase = "https://github.com/microsoft/winget-cli/releases/latest/download/";
    public const string BundleFile = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    public const string DependenciesFile = "DesktopAppInstaller_Dependencies.zip";

    private static readonly TimeSpan DownloadZeitgrenze = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan InstallZeitgrenze = TimeSpan.FromMinutes(10);

    /// <summary>Ordner im Abhaengigkeits-Paket fuer diese Windows-Architektur.</summary>
    public static string ArchFolder(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>
    /// Die Abhaengigkeiten fuer die Architektur aus dem entpackten Paket
    /// ("x64\Microsoft.VCLibs...appx" ...), nach Namen sortiert - die VCLibs
    /// vor der Windows App Runtime.
    /// </summary>
    public static List<string> Dependencies(string entpackt, string arch)
    {
        try
        {
            var ordner = Directory.Exists(entpackt)
                ? Directory.GetDirectories(entpackt).FirstOrDefault(d => string.Equals(Path.GetFileName(d), arch, StringComparison.OrdinalIgnoreCase))
                : null;
            if (ordner is null) return new List<string>();
            return Directory.GetFiles(ordner)
                .Where(f => f.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Text in einfachen Anfuehrungszeichen fuer PowerShell ("O'Brien" -> 'O''Brien').</summary>
    public static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Das PowerShell-Skript: erst jede Abhaengigkeit einzeln - ist eine schon
    /// in neuerer Fassung da, ist das kein Fehler -, dann der App-Installer.
    /// </summary>
    public static string InstallScript(string bundle, IReadOnlyList<string> abhaengigkeiten)
    {
        var liste = string.Join(", ", abhaengigkeiten.Select(PsQuote));
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "$ProgressPreference = 'SilentlyContinue'",
            $"foreach ($d in @({liste})) {{ try {{ Add-AppxPackage -Path $d }} catch {{ Write-Output ('ABHAENGIGKEIT ' + (Split-Path $d -Leaf) + ': ' + $_.Exception.Message) }} }}",
            $"try {{ Add-AppxPackage -Path {PsQuote(bundle)} -ForceApplicationShutdown; Write-Output 'INSTALLIERT' }} catch {{ Write-Output ('FEHLER: ' + $_.Exception.Message); exit 1 }}");
    }

    /// <summary>Fuer "powershell -EncodedCommand": UTF-16LE, Base64 - ganz ohne Anfuehrungszeichen-Probleme.</summary>
    public static string Encode(string skript) => Convert.ToBase64String(Encoding.Unicode.GetBytes(skript));

    /// <summary>
    /// Laedt App-Installer und Abhaengigkeiten herunter und installiert sie
    /// fuer den Benutzer, unter dem das Tool laeuft. true, wenn die
    /// Installation geklappt hat.
    /// </summary>
    public static async Task<bool> InstallAsync(Action<string> log, Func<bool> abgebrochen, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var ordner = Path.Combine(Path.GetTempPath(), "DaSi-winget-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(ordner);
            log(Loc.Tr("[INFO] Lade den App-Installer (winget) von GitHub herunter - neueste Fassung von github.com/microsoft/winget-cli ...",
                       "[INFO] Downloading the App Installer (winget) from GitHub - latest release of github.com/microsoft/winget-cli ..."));

            var zip = Path.Combine(ordner, DependenciesFile);
            var bundle = Path.Combine(ordner, BundleFile);
            using (var client = new HttpClient { Timeout = DownloadZeitgrenze })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDaSiTool");
                if (!await Laden(client, ReleaseBase + DependenciesFile, zip, Loc.Tr("Abhängigkeiten", "dependencies"), log, abgebrochen, ct)) return false;
                if (!await Laden(client, ReleaseBase + BundleFile, bundle, "App-Installer", log, abgebrochen, ct)) return false;
            }

            var entpackt = Path.Combine(ordner, "abhaengigkeiten");
            ZipFile.ExtractToDirectory(zip, entpackt);
            var arch = ArchFolder(RuntimeInformation.OSArchitecture);
            var deps = Dependencies(entpackt, arch);
            log(Loc.Tr($"[INFO] Installiere den App-Installer samt {deps.Count} Abhängigkeiten ({arch}) ...",
                       $"[INFO] Installing the App Installer with {deps.Count} dependencies ({arch}) ..."));

            var (code, ausgabe) = await PowerShell(InstallScript(bundle, deps), ct);
            foreach (var zeile in ausgabe.Split('\n').Select(z => z.Trim()).Where(z => z.StartsWith("ABHAENGIGKEIT ")))
                log(Loc.Tr($"[INFO] {zeile["ABHAENGIGKEIT ".Length..]} (meist schon in neuerer Fassung vorhanden)",
                           $"[INFO] {zeile["ABHAENGIGKEIT ".Length..]} (usually already present in a newer version)"));
            if (code == 0 && ausgabe.Contains("INSTALLIERT"))
            {
                log(Loc.Tr("[ERFOLG] App-Installer (winget) installiert.", "[SUCCESS] App Installer (winget) installed."));
                return true;
            }
            var grund = ausgabe.Split('\n').Select(z => z.Trim()).LastOrDefault(z => z.StartsWith("FEHLER: "))?["FEHLER: ".Length..]
                        ?? ausgabe.Trim();
            log(Loc.Tr($"[WARNUNG] Der App-Installer ließ sich nicht installieren: {grund}",
                       $"[WARNING] The App Installer could not be installed: {grund}"));
            return false;
        }
        catch (Exception ex)
        {
            log(Loc.Tr($"[WARNUNG] winget ließ sich nicht von GitHub nachinstallieren: {ex.Message}",
                       $"[WARNING] winget could not be installed from GitHub: {ex.Message}"));
            return false;
        }
        finally
        {
            try { Directory.Delete(ordner, true); } catch { /* Temp - egal */ }
        }
    }

    private static async Task<bool> Laden(HttpClient client, string url, string ziel, string was,
                                          Action<string> log, Func<bool> abgebrochen, CancellationToken ct)
    {
        using var antwort = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        antwort.EnsureSuccessStatusCode();
        var gesamt = antwort.Content.Headers.ContentLength;
        await using var quelle = await antwort.Content.ReadAsStreamAsync(ct);
        await using var datei = File.Create(ziel);
        var puffer = new byte[1 << 16];
        long gelesen = 0;
        int naechsteMarke = 25;
        int n;
        while ((n = await quelle.ReadAsync(puffer, ct)) > 0)
        {
            if (abgebrochen()) return false;
            await datei.WriteAsync(puffer.AsMemory(0, n), ct);
            gelesen += n;
            if (gesamt is > 0 && gelesen * 100 / gesamt.Value >= naechsteMarke && naechsteMarke < 100)
            {
                log(Loc.Tr($"    {was}: {naechsteMarke} % von {gesamt.Value / 1048576} MB", $"    {was}: {naechsteMarke} % of {gesamt.Value / 1048576} MB"));
                naechsteMarke += 25;
            }
        }
        log(Loc.Tr($"    {was}: geladen ({gelesen / 1048576} MB)", $"    {was}: downloaded ({gelesen / 1048576} MB)"));
        return true;
    }

    /// <summary>Windows PowerShell 5.1 - dort ist Add-AppxPackage verlaesslich vorhanden (anders als in pwsh 7).</summary>
    private static async Task<(int Code, string Ausgabe)> PowerShell(string skript, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encode(skript))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe ließ sich nicht starten.");
        var aus = p.StandardOutput.ReadToEndAsync(ct);
        var err = p.StandardError.ReadToEndAsync(ct);
        using var zeit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        zeit.CancelAfter(InstallZeitgrenze);
        try { await p.WaitForExitAsync(zeit.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "Zeitlimit überschritten");
        }
        return (p.ExitCode, (await aus) + "\n" + (await err));
    }
}
