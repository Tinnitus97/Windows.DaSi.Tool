using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Der angebotene Programmstand, wie er in update.json steht.
/// </summary>
public sealed class UpdateEntry
{
    /// <summary>Versionsnummer der angebotenen Fassung, z.B. "1.2.0".</summary>
    public string Version { get; init; } = "";

    /// <summary>Tag der Veroeffentlichung - reine Anzeige.</summary>
    public string Released { get; init; } = "";

    /// <summary>Adresse der EXE an der Veroeffentlichung.</summary>
    public string Url { get; init; } = "";

    /// <summary>Erwartete SHA256-Summe, klein geschrieben.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Adresse der Veroeffentlichungsseite fuer "Was ist neu?".</summary>
    public string Notes { get; init; } = "";

    /// <summary>true, wenn Nummer und Adresse vorhanden sind.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Ergebnis der Abfrage von update.json.</summary>
public sealed class UpdateManifest
{
    public UpdateEntry Program { get; init; } = new();

    public bool Success { get; init; }

    public string? Error { get; init; }

    public static UpdateManifest Failed(string error) => new() { Error = error };
}

/// <summary>
/// Die Selbstaktualisierung des Windows DaSi Tools.
///
/// Ablauf: Beim Start wird EINE Datei geholt - update.json aus dem
/// Repository. Steht dort eine hoehere Versionsnummer als die eigene,
/// erscheint der Hinweisstreifen. Erst auf ausdrueckliche Zustimmung wird die
/// neue EXE geladen, ihre Pruefsumme verglichen und die laufende Datei
/// ersetzt.
///
/// ZWEI ENTSCHEIDUNGEN, DIE EINE ERKLAERUNG VERDIENEN:
///
/// 1. Abgefragt wird ueber raw.githubusercontent.com, NICHT ueber
///    api.github.com. Die Schnittstelle laesst ohne Anmeldung nur 60 Abrufe je
///    Stunde und IP-Adresse zu - in einer Firma sitzen alle Rechner hinter
///    derselben Adresse, nach 60 Programmstarts waere Schluss. Ein
///    Zugangstoken hat in einer verteilten EXE ohnehin nichts zu suchen.
///
/// 2. In der Adresse steht "HEAD" statt eines festen Zweignamens. Damit zeigt
///    sie immer auf den Standardzweig des Repositories - heute "main". Ein
///    fest eingetragener Name waere eine stille Falle: Wird der Standardzweig
///    spaeter umbenannt oder ausgetauscht, liefe die Abfrage ins Leere, und
///    zwar in jeder bereits ausgelieferten EXE. Die laesst sich dann nicht
///    mehr nachbessern - sie holt ihre Nachbesserung ja ueber genau diese
///    Adresse.
///
/// Es wird nichts heruntergeladen und nichts ersetzt, ohne dass jemand
/// zugestimmt hat - und ausschliesslich das eingespielt, dessen Pruefsumme zu
/// der Angabe in update.json passt.
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// Vorgabeadresse. Laesst sich ueber <see cref="UrlOverrideFileName"/>
    /// neben der EXE ersetzen - etwa fuer ein internes Spiegelverzeichnis.
    /// </summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Tinnitus97/Windows.DaSi.Tool/HEAD/update.json";

    /// <summary>Uebersicht der Veroeffentlichungen - Ziel von "Was ist neu?".</summary>
    public const string ReleasesPageUrl =
        "https://github.com/Tinnitus97/Windows.DaSi.Tool/releases";

    /// <summary>Name der Datei mit einer abweichenden Adresse.</summary>
    public const string UrlOverrideFileName = "Update-Url.txt";

    /// <summary>Wie lange auf update.json gewartet wird.</summary>
    private static readonly TimeSpan AbfrageZeitgrenze = TimeSpan.FromSeconds(20);

    /// <summary>Wie lange der Download der EXE dauern darf.</summary>
    private static readonly TimeSpan DownloadZeitgrenze = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Arbeitsordner fuer die heruntergeladene EXE und das Austauschskript.
    ///
    /// Bewusst ein eigener Ordner neben dem Programm und nicht darin: Das
    /// Skript laeuft noch, waehrend sich das Programm schon beendet hat.
    /// </summary>
    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "WindowsDaSiTool-Update");

    // ------------------------------------------------------------ Adresse

    /// <summary>
    /// Liest eine abweichende Adresse aus <see cref="UrlOverrideFileName"/>
    /// neben der EXE. Erwartet wird eine Zeile mit vollstaendiger Adresse;
    /// alles andere (z.B. Zeilen mit "#") wird uebergangen.
    /// </summary>
    public static string ResolveManifestUrl(string programFolder)
    {
        try
        {
            var datei = Path.Combine(programFolder, UrlOverrideFileName);
            if (!File.Exists(datei)) return DefaultManifestUrl;

            var zeile = File.ReadAllLines(datei)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(zeile) ? DefaultManifestUrl : zeile;
        }
        catch
        {
            // Eine unlesbare Datei darf die Abfrage nicht verhindern.
            return DefaultManifestUrl;
        }
    }

    // ------------------------------------------------------------ Abfragen

    /// <summary>Holt update.json und wertet sie aus.</summary>
    public static async Task<UpdateManifest> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = AbfrageZeitgrenze };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDaSiTool");

            // Ohne diesen Zusatz meldet ein Rechner unter Umstaenden
            // stundenlang den zwischengespeicherten alten Stand.
            client.DefaultRequestHeaders.CacheControl =
                new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

            return Parse(await client.GetStringAsync(url, ct));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return UpdateManifest.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Wertet den Inhalt von update.json aus. Oeffentlich, damit sich das
    /// Einlesen ohne Netzzugriff pruefen laesst.
    /// </summary>
    public static UpdateManifest Parse(string json)
    {
        try
        {
            using var dokument = JsonDocument.Parse(json);
            var wurzel = dokument.RootElement;

            if (!wurzel.TryGetProperty("program", out var teil) || teil.ValueKind != JsonValueKind.Object)
                return new UpdateManifest { Success = true };

            return new UpdateManifest
            {
                Success = true,
                Program = new UpdateEntry
                {
                    Version = Text(teil, "version"),
                    Released = Text(teil, "released"),
                    Url = Text(teil, "url"),
                    Sha256 = Text(teil, "sha256").ToLowerInvariant(),
                    Notes = Text(teil, "notes")
                }
            };
        }
        catch (Exception ex)
        {
            return UpdateManifest.Failed(ex.Message);
        }
    }

    /// <summary>Liest einen Wert und nimmt eine Zahl wie Text entgegen.</summary>
    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var wert)
            ? wert.ValueKind switch
            {
                JsonValueKind.String => wert.GetString() ?? "",
                JsonValueKind.Number => wert.ToString(),
                _ => ""
            }
            : "";

    /// <summary>
    /// true, wenn die angebotene Nummer hoeher ist als die eigene.
    ///
    /// Laesst sich eine der beiden nicht als Versionsnummer lesen, gilt das
    /// ausdruecklich NICHT als Update: lieber nichts anbieten als das Falsche.
    /// </summary>
    public static bool IsProgramNewer(string? installed, string? offered)
        => Version.TryParse(installed, out var hier)
           && Version.TryParse(offered, out var dort)
           && dort > hier;

    // ------------------------------------------------------------ Herunterladen

    /// <summary>Ergebnis eines Downloads samt Pruefsummenvergleich.</summary>
    public sealed record DownloadResult(bool Success, string File, string? Error, long Bytes);

    /// <summary>
    /// Laedt eine Datei und vergleicht ihre SHA256-Summe mit der Angabe aus
    /// update.json. Stimmt sie nicht, wird die Datei wieder geloescht: Eine
    /// halb uebertragene oder ausgetauschte Datei darf nicht an die Stelle des
    /// laufenden Programms treten.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        string url, string expectedSha256, string targetFile, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);

            using (var client = new HttpClient { Timeout = DownloadZeitgrenze })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDaSiTool");

                using var antwort = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                antwort.EnsureSuccessStatusCode();

                await using var quelle = await antwort.Content.ReadAsStreamAsync(ct);
                await using var ziel = File.Create(targetFile);
                await quelle.CopyToAsync(ziel, ct);
            }

            var groesse = new FileInfo(targetFile).Length;

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var tatsaechlich = ComputeSha256(targetFile);
                if (!string.Equals(tatsaechlich, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(targetFile);
                    return new DownloadResult(false, targetFile,
                        $"Prüfsumme stimmt nicht (erwartet {Kurz(expectedSha256)}, erhalten {Kurz(tatsaechlich)})",
                        groesse);
                }
            }

            return new DownloadResult(true, targetFile, null, groesse);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TryDelete(targetFile);
            return new DownloadResult(false, targetFile, ex.Message, 0);
        }
    }

    /// <summary>Bildet die SHA256-Summe einer Datei als Kleinbuchstaben.</summary>
    public static string ComputeSha256(string file)
    {
        using var strom = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(strom)).ToLowerInvariant();
    }

    private static string Kurz(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* schon weg */ }
    }

    // ------------------------------------------------------------ Austauschen

    /// <summary>
    /// Schreibt das Skript, das die laufende EXE ersetzt.
    ///
    /// Warum ueberhaupt ein Skript: Eine laufende EXE kann sich unter Windows
    /// nicht selbst ueberschreiben - die Datei ist gesperrt, solange der
    /// Vorgang laeuft. Das Skript wartet deshalb auf dessen Ende, verschiebt
    /// die neue Fassung an die alte Stelle und startet sie.
    ///
    /// Schlaegt das Verschieben fehl - fehlende Schreibrechte, oder das
    /// Programm laeuft an einem anderen Arbeitsplatz aus demselben Ordner -,
    /// bleibt ein Fenster mit dem Grund und dem Pfad zur neuen Fassung stehen,
    /// statt kommentarlos nichts zu tun.
    /// </summary>
    public static string WriteUpdateScript(string newExe, string currentExe, int processId)
    {
        Directory.CreateDirectory(WorkFolder);
        var skript = Path.Combine(WorkFolder, "austausch.cmd");

        var text = $"""
            @echo off
            rem Wartet auf das Ende des Windows DaSi Tools und ersetzt dann die Datei.
            setlocal
            set "NEU={newExe}"
            set "ZIEL={currentExe}"

            for /l %%i in (1,1,30) do (
              tasklist /fi "PID eq {processId}" 2>nul | find "{processId}" >nul || goto ersetzen
              timeout /t 1 /nobreak >nul
            )

            :ersetzen
            move /y "%NEU%" "%ZIEL%" >nul
            if errorlevel 1 (
              echo.
              echo Die Datei konnte nicht ersetzt werden.
              echo Neue Fassung liegt hier: %NEU%
              echo Ziel: %ZIEL%
              echo.
              echo Moegliche Gruende: fehlende Schreibrechte, oder das Programm
              echo laeuft an einem anderen Arbeitsplatz aus demselben Ordner.
              echo.
              pause
              exit /b 1
            )

            start "" "%ZIEL%"
            del "%~f0" >nul 2>&1
            """;

        File.WriteAllText(skript, text, Encoding.UTF8);
        return skript;
    }

    /// <summary>
    /// Startet das Austauschskript. Danach muss sich das Programm beenden -
    /// solange es laeuft, wartet das Skript.
    /// </summary>
    public static void StartUpdateScript(string script)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkFolder
        });
    }

    /// <summary>
    /// Raeumt den Arbeitsordner auf und fasst dabei mehrfach nach - ein
    /// Virenscanner haelt eine frisch geschriebene Datei manchmal noch kurz
    /// offen.
    /// </summary>
    public static void CleanUp()
    {
        if (!Directory.Exists(WorkFolder)) return;

        for (var versuch = 1; versuch <= 3; versuch++)
        {
            try
            {
                foreach (var datei in Directory.EnumerateFiles(WorkFolder, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(datei, FileAttributes.Normal); } catch { /* egal */ }
                }

                Directory.Delete(WorkFolder, recursive: true);
                return;
            }
            catch when (versuch < 3)
            {
                Thread.Sleep(300);
            }
            catch
            {
                // Beim naechsten Start erneut versuchen.
                return;
            }
        }
    }
}
