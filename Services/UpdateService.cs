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
/// The offered program version, as listed in update.json.
/// </summary>
public sealed class UpdateEntry
{
    /// <summary>Version number of the offered release, e.g. "1.2.0".</summary>
    public string Version { get; init; } = "";

    /// <summary>Release date - display only.</summary>
    public string Released { get; init; } = "";

    /// <summary>Address of the EXE in the release.</summary>
    public string Url { get; init; } = "";

    /// <summary>Expected SHA256 checksum, lower case.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Address of the release page for "What's new?".</summary>
    public string Notes { get; init; } = "";

    /// <summary>true if number and address are present.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Result of fetching update.json.</summary>
public sealed class UpdateManifest
{
    public UpdateEntry Program { get; init; } = new();

    public bool Success { get; init; }

    public string? Error { get; init; }

    public static UpdateManifest Failed(string error) => new() { Error = error };
}

/// <summary>
/// The self-update of Windows DaSi Tool.
///
/// Flow: at startup ONE file is fetched - update.json from the repository.
/// If it lists a higher version number than our own, the notice bar
/// appears. Only after explicit consent is the new EXE downloaded, its
/// checksum compared and the running file replaced.
///
/// TWO DECISIONS THAT DESERVE AN EXPLANATION:
///
/// 1. The check goes through raw.githubusercontent.com, NOT api.github.com.
///    Without sign-in, the API allows only 60 requests per hour and IP
///    address - in a company all computers sit behind the same address, so
///    it would stop after 60 program starts. An access token has no place
///    in a distributed EXE anyway.
///
/// 2. The address says "HEAD" instead of a fixed branch name. That way it
///    always points to the repository's default branch - "main" today. A
///    hard-coded name would be a silent trap: if the default branch were
///    later renamed or replaced, the check would come up empty, and in
///    every EXE already shipped. Those could not be fixed any more - they
///    fetch their fix through exactly this address.
///
/// Nothing is downloaded and nothing replaced without someone agreeing - and
/// only what matches the checksum in update.json is installed.
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// Default address. Can be replaced through <see cref="UrlOverrideFileName"/>
    /// next to the EXE - e.g. for an internal mirror.
    /// </summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Tinnitus97/Windows.DaSi.Tool/HEAD/update.json";

    /// <summary>Overview of the releases - target of "What's new?".</summary>
    public const string ReleasesPageUrl =
        "https://github.com/Tinnitus97/Windows.DaSi.Tool/releases";

    /// <summary>Name of the file holding a custom address.</summary>
    public const string UrlOverrideFileName = "Update-Url.txt";

    /// <summary>How long to wait for update.json.</summary>
    private static readonly TimeSpan AbfrageZeitgrenze = TimeSpan.FromSeconds(20);

    /// <summary>How long the download of the EXE may take.</summary>
    private static readonly TimeSpan DownloadZeitgrenze = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Working folder for the downloaded EXE and the swap script.
    ///
    /// Deliberately a folder of its own next to the program and not inside it:
    /// the script is still running while the program has already exited.
    /// </summary>
    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "WindowsDaSiTool-Update");

    // ------------------------------------------------------------ Address

    /// <summary>
    /// Reads a custom address from <see cref="UrlOverrideFileName"/> next to
    /// the EXE. Expected is one line with a full address; everything else
    /// (e.g. lines starting with "#") is skipped.
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
            // An unreadable file must not prevent the check.
            return DefaultManifestUrl;
        }
    }

    // ------------------------------------------------------------ Check

    /// <summary>Fetches update.json and evaluates it.</summary>
    public static async Task<UpdateManifest> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = AbfrageZeitgrenze };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDaSiTool");

            // Without this suffix a computer may report the cached old version for
            // hours.
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
    /// Evaluates the content of update.json. Public, so parsing can be tested
    /// without network access.
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

    /// <summary>Reads a value and accepts a number as well as text.</summary>
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
    /// true if the offered number is higher than our own.
    ///
    /// If either of them cannot be read as a version number, that explicitly
    /// does NOT count as an update: better to offer nothing than the wrong
    /// thing.
    /// </summary>
    public static bool IsProgramNewer(string? installed, string? offered)
        => Version.TryParse(installed, out var hier)
           && Version.TryParse(offered, out var dort)
           && dort > hier;

    // ------------------------------------------------------------ Download

    /// <summary>Result of a download including the checksum comparison.</summary>
    public sealed record DownloadResult(bool Success, string File, string? Error, long Bytes);

    /// <summary>
    /// Downloads a file and compares its SHA256 checksum with the one from
    /// update.json. If it does not match, the file is deleted again: a
    /// half-transferred or tampered file must not take the place of the running
    /// program.
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
                        $"Prüfsumme stimmt nicht (erwartet {Short(expectedSha256)}, erhalten {Short(tatsaechlich)})",
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

    /// <summary>Computes a file's SHA256 checksum in lower case.</summary>
    public static string ComputeSha256(string file)
    {
        using var strom = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(strom)).ToLowerInvariant();
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* already gone */ }
    }

    // ------------------------------------------------------------ Swap

    /// <summary>
    /// How often the script attempts the swap, about one second apart. Enough
    /// for the program to exit in peace.
    /// </summary>
    public const int MaxReplaceAttempts = 60;

    /// <summary>Name of the explanation that appears when the swap fails.</summary>
    public const string FailureNoteFileName = "update-fehler.txt";

    /// <summary>
    /// Writes the script that replaces the running EXE.
    ///
    /// Why a script at all: on Windows a running EXE cannot overwrite itself -
    /// the file is locked as long as the process runs. So the script waits for
    /// it to end, moves the new version to the old place and starts it.
    ///
    /// THE PATHS ARE NOT IN THE FILE; they come as arguments (%~1, %~2). They
    /// used to be in the script as "set NEU=...", and the script was written as
    /// UTF-8. But cmd.exe reads a batch file in the OEM code page (850): every
    /// umlaut turned into garbage when read, the path no longer matched, and
    /// move failed. The script lives in %TEMP%, i.e. under C:\Users\[name] -
    /// so self-update was broken for every user with an umlaut in their name.
    /// Arguments, on the other hand, are passed through by cmd as Unicode. The
    /// file itself now contains only ASCII and is written without a BOM.
    ///
    /// THE WAIT USES THE FILE LOCK, not the process ID. As long as the old
    /// version runs, Windows holds its file, and move fails - so move is simply
    /// repeated until it works. This used to be a for loop that asked for the
    /// process ID via "tasklist | find" and was meant to exit via goto. On real
    /// Windows it still ran all 30 rounds (in the test: 60 s, for a process ID
    /// that did not even exist). Every update stood still for about a minute -
    /// and whoever restarted the program in that time locked the file. The loop
    /// here needs no for, no tasklist and no find; "ping" instead of "timeout"
    /// waits even when there is no console to read from.
    ///
    /// If the move fails - missing write permissions, or the program runs on
    /// another workstation from the same folder -, an explanation opens in the
    /// editor and the previous version starts again. This used to be "pause":
    /// in a window that never appeared because of CreateNoWindow. The program
    /// was then simply gone, and an invisible cmd waited forever.
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
            rem Sich selbst loeschen: "(goto)" ohne Sprungziel beendet den Batch
            rem sofort, der Rest dieser bereits eingelesenen Zeile laeuft danach
            rem ohne Rueckgriff auf die Datei. Mit "del" und darunter "exit /b"
            rem suchte cmd die gerade geloeschte Datei und meldete "The batch
            rem file cannot be found." (Exit-Code 1) - so gesehen unter Windows.
            (goto) 2>nul & del "%~f0" >nul 2>&1 & exit /b 0

            :fehler
            rem Kein "pause": Dieses Skript laeuft ohne sichtbares Fenster.
            start "" notepad.exe "%~dp0{FailureNoteFileName}"
            start "" "%~2"
            exit /b 1
            """;

        // ASCII only, without a BOM - a BOM turned "@echo off" into an unknown
        // command.
        File.WriteAllText(skript, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // The explanation for the failure case: with a BOM, so the editor shows
        // the umlauts in the paths correctly. Bilingual, because the script does
        // not know the program's language setting.
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
    /// Builds the command line for the swap script. A method of its own, so the
    /// Windows test can run exactly this command.
    ///
    /// cmd removes the outer quotes after /c, leaving "script" "new" "target" -
    /// the usual, robust form for paths with spaces.
    /// </summary>
    public static ProcessStartInfo BuildUpdateStart(string script, string newExe, string currentExe)
        => new("cmd.exe", $"/c \"\"{script}\" \"{newExe}\" \"{currentExe}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkFolder
        };

    /// <summary>
    /// Starts the swap script. The program has to exit afterwards - the script
    /// waits as long as it runs.
    /// </summary>
    public static void StartUpdateScript(string script, string newExe, string currentExe)
    {
        using var _ = Process.Start(BuildUpdateStart(script, newExe, currentExe));
    }

    /// <summary>
    /// Cleans up the working folder and retries a few times - a virus scanner
    /// sometimes keeps a freshly written file open for a moment.
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
                    try { File.SetAttributes(datei, FileAttributes.Normal); } catch { /* does not matter */ }
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
                // Try again at the next start.
                return;
            }
        }
    }
}
