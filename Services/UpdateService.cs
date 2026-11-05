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
    /// Wie oft das Skript den Austausch versucht, etwa eine Sekunde Abstand.
    /// Reicht, damit sich das Programm in Ruhe beendet.
    /// </summary>
    public const int MaxReplaceAttempts = 60;

    /// <summary>Name der Erklaerung, die bei einem gescheiterten Austausch erscheint.</summary>
    public const string FailureNoteFileName = "update-fehler.txt";

    /// <summary>
    /// Schreibt das Skript, das die laufende EXE ersetzt.
    ///
    /// Warum ueberhaupt ein Skript: Eine laufende EXE kann sich unter Windows
    /// nicht selbst ueberschreiben - die Datei ist gesperrt, solange der
    /// Vorgang laeuft. Das Skript wartet deshalb auf dessen Ende, verschiebt
    /// die neue Fassung an die alte Stelle und startet sie.
    ///
    /// DIE PFADE STEHEN NICHT IN DER DATEI, sie kommen als Argumente (%~1,
    /// %~2). Frueher standen sie als "set NEU=..." im Skript, und das Skript
    /// wurde als UTF-8 geschrieben. cmd.exe liest eine Batchdatei aber in der
    /// OEM-Codepage (850): Aus jedem Umlaut wurde beim Lesen Zeichensalat,
    /// der Pfad stimmte nicht mehr, move scheiterte. Das Skript liegt in
    /// %TEMP%, also unter C:\Users\[Name] - fuer jeden Benutzer mit Umlaut
    /// im Namen war die Selbstaktualisierung damit kaputt. Argumente reicht
    /// cmd dagegen als Unicode durch. Die Datei selbst enthaelt nur noch
    /// ASCII und wird ohne BOM geschrieben.
    ///
    /// GEWARTET WIRD UEBER DIE DATEISPERRE, nicht ueber die Prozessnummer.
    /// Solange die alte Fassung laeuft, haelt Windows ihre Datei fest, und
    /// move scheitert - also wird move einfach wiederholt, bis es klappt.
    /// Frueher fragte eine for-Schleife per "tasklist | find" nach der
    /// Prozessnummer und sollte per goto aussteigen. Auf einem echten
    /// Windows lief sie trotzdem alle 30 Durchgaenge durch (im Test: 60 s,
    /// fuer eine Prozessnummer, die es gar nicht gab). Jedes Update stand so
    /// rund eine Minute still - und wer das Programm in der Zeit erneut
    /// startete, sperrte die Datei. Die Schleife hier kommt ohne for, ohne
    /// tasklist und ohne find aus; "ping" statt "timeout" wartet auch dann,
    /// wenn keine Konsole zum Lesen da ist.
    ///
    /// Schlaegt das Verschieben fehl - fehlende Schreibrechte, oder das
    /// Programm laeuft an einem anderen Arbeitsplatz aus demselben Ordner -,
    /// oeffnet sich eine Erklaerung im Editor, und die bisherige Fassung
    /// startet wieder. Frueher stand dort "pause": in einem Fenster, das
    /// wegen CreateNoWindow nie erschien. Das Programm war dann einfach weg,
    /// und ein unsichtbares cmd wartete fuer immer.
    /// </summary>
    public static string WriteUpdateScript(string newExe, string currentExe)
    {
        Directory.CreateDirectory(WorkFolder);
        var skript = Path.Combine(WorkFolder, "austausch.cmd");

        var text = $"""
            @echo off
            rem Ersetzt das Windows DaSi Tool durch die neue Fassung.
            rem Aufruf: austausch.cmd "<neue Fassung>" "<Ziel>"
            rem Die Pfade kommen bewusst als Argumente und stehen NICHT in dieser
            rem Datei: cmd liest Batchdateien in der OEM-Codepage, Argumente aber
            rem als Unicode. Nur so bleibt ein Pfad mit Umlaut heil.
            setlocal
            set /a versuch=0

            :ersetzen
            move /y "%~1" "%~2" >nul 2>&1
            if not errorlevel 1 goto fertig
            rem Die alte Fassung laeuft noch und haelt ihre Datei fest.
            set /a versuch+=1
            if %versuch% geq {MaxReplaceAttempts} goto fehler
            ping -n 2 127.0.0.1 >nul
            goto ersetzen

            :fertig
            start "" "%~2"
            rem Loeschen und Beenden in EINER Zeile: cmd liest die Zeile vorab
            rem ganz ein. Stuende exit darunter, faende cmd die eigene, gerade
            rem geloeschte Datei nicht mehr und endete mit Fehlercode 1.
            del "%~f0" >nul 2>&1 & exit /b 0

            :fehler
            rem Kein "pause": Dieses Skript laeuft ohne sichtbares Fenster.
            start "" notepad.exe "%~dp0{FailureNoteFileName}"
            start "" "%~2"
            exit /b 1
            """;

        // Nur ASCII, ohne BOM - ein BOM machte aus "@echo off" einen
        // unbekannten Befehl.
        File.WriteAllText(skript, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Die Erklaerung fuer den Fehlerfall: mit BOM, damit der Editor die
        // Umlaute in den Pfaden richtig zeigt. Zweisprachig, weil das Skript
        // die Spracheinstellung des Programms nicht kennt.
        var notiz = $"""
            Windows DaSi Tool - Die Aktualisierung konnte nicht abgeschlossen werden
            =========================================================================

            Die bisherige Fassung wurde nicht verändert und wieder gestartet.

            Neue Fassung (heruntergeladen und geprüft):
              {newExe}

            Sollte ersetzt werden:
              {currentExe}

            Mögliche Gründe:
              - Es fehlen Schreibrechte im Programmordner.
              - Das Programm läuft an einem anderen Arbeitsplatz aus demselben Ordner.

            Abhilfe: Das Programm überall schließen und die neue Fassung von Hand an
            die Stelle der alten kopieren.

            -------------------------------------------------------------------------
            The update could not be completed. The previous version was left
            unchanged and restarted. New version: see first path above; it was meant
            to replace the second. Possible causes: no write permission in the
            program folder, or the program is running elsewhere from the same folder.
            """;
        File.WriteAllText(Path.Combine(WorkFolder, FailureNoteFileName), notiz,
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return skript;
    }

    /// <summary>
    /// Baut den Aufruf des Austauschskripts. Eigene Methode, damit der Test
    /// unter Windows genau diesen Aufruf ausfuehren kann.
    ///
    /// Die aeusseren Anfuehrungszeichen nach /c entfernt cmd, uebrig bleibt
    /// "skript" "neu" "ziel" - die uebliche, robuste Form fuer Pfade mit
    /// Leerzeichen.
    /// </summary>
    public static ProcessStartInfo BuildUpdateStart(string script, string newExe, string currentExe)
        => new("cmd.exe", $"/c \"\"{script}\" \"{newExe}\" \"{currentExe}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkFolder
        };

    /// <summary>
    /// Startet das Austauschskript. Danach muss sich das Programm beenden -
    /// solange es laeuft, wartet das Skript.
    /// </summary>
    public static void StartUpdateScript(string script, string newExe, string currentExe)
    {
        using var _ = Process.Start(BuildUpdateStart(script, newExe, currentExe));
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
