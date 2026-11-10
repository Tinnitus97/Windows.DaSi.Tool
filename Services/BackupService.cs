using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Portierung der Backup-/Restore-/Update-Funktionen aus dem PowerShell-Skript.
/// </summary>
public sealed class BackupService
{
    private readonly ProcessRunner _runner;
    private readonly CancellationTokenState _cancel;
    private readonly Action<string> _log;

    /// <summary>Optionaler Fortschritts-Callback: gemeldete Dateianzahl.</summary>
    public Action<int>? OnFilesProgress { get; set; }

    public string SourcePath { get; set; } = "";
    public string BackupPath { get; set; } = "";

    private static readonly string[] ChromiumCacheExcludes =
    {
        "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache",
        "Service Worker", "Crashpad", "Application Cache", "Media Cache",
        "DawnGraphiteCache", "DawnWebGPUCache"
    };

    public BackupService(ProcessRunner runner, CancellationTokenState cancel, Action<string> log)
    {
        _runner = runner;
        _cancel = cancel;
        _log = log;
    }

    // ------------------------------------------------------------ Benutzerprofil

    /// <summary>
    /// Soll der Ordner AppData mitgesichert werden? Vorgabe nein.
    /// </summary>
    public bool IncludeAppData { get; set; }

    /// <summary>
    /// Sollen die erkannten Cloud-Ordner mitgesichert werden? Vorgabe nein.
    /// </summary>
    public bool IncludeCloud { get; set; }

    /// <summary>
    /// Baut die Liste der vom Profil-Backup ausgeschlossenen Ordner (voll
    /// qualifizierte Pfade). Wird sowohl vom eigentlichen Backup als auch von
    /// der Speicher-Schaetzung verwendet, damit beide konsistent sind.
    ///
    /// Die Entscheidung selbst steht in <see cref="ProfileExcludes"/> - dort
    /// ohne Abhaengigkeiten und damit pruefbar.
    /// </summary>
    public static List<string> GetProfileExcludes(string userDir, bool includeAppData = false, bool includeCloud = false)
        => ProfileExcludes.Build(userDir, includeAppData, includeCloud);

    public async Task BackupUserProfile()
    {
        var userDir = SourcePath;
        var backupTargetDir = Path.Combine(BackupPath, "Benutzerprofil");

        // Frueher ein reiner Praefixvergleich: Profil "C:\Users\Max" und Ziel
        // "C:\Users\Maximilian\Backup" galten als "Ziel im Quellverzeichnis",
        // und das Backup wurde verweigert.
        if (ProfileExcludes.IsInside(backupTargetDir, userDir))
        {
            _log(Loc.Tr("[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch.", "[ERROR] Target is inside the source directory. Aborting."));
            return;
        }

        var excluded = GetProfileExcludes(userDir, IncludeAppData, IncludeCloud);

        var args = new List<string> { Quote(userDir), Quote(backupTargetDir), "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        foreach (var ex in excluded) if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        // Im Protokoll festhalten, was mitlaeuft - sonst laesst sich hinterher
        // nicht mehr nachvollziehen, warum ein Backup so gross wurde.
        if (IncludeAppData)
            _log(Loc.Tr("[INFO] AppData wird mitgesichert (Zwischenspeicher bleiben ausgenommen).",
                        "[INFO] AppData is included (caches remain excluded)."));
        if (IncludeCloud)
        {
            var clouds = CloudProviders.Detect(userDir);
            _log(clouds.Count > 0
                ? Loc.Tr($"[INFO] Cloud-Ordner werden mitgesichert: {CloudProviders.Summarize(clouds)}.",
                         $"[INFO] Cloud folders are included: {CloudProviders.Summarize(clouds)}.")
                : Loc.Tr("[INFO] Cloud-Ordner sollen mitgesichert werden - es wurde aber keiner gefunden.",
                         "[INFO] Cloud folders should be included - but none were found."));
        }

        _log(Loc.Tr("[INFO] Starte Backup Benutzerprofil...", "[INFO] Starting user profile backup..."));
        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr("[ERFOLG] Benutzerprofil gesichert.", "[SUCCESS] User profile backed up.") : Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));
    }

    public async Task RestoreUserProfile()
    {
        var destDir = SourcePath;
        var backupSourceDir = Path.Combine(BackupPath, "Benutzerprofil");
        if (!Directory.Exists(backupSourceDir)) { _log(Loc.Tr("[FEHLER] Backup-Ordner nicht gefunden.", "[ERROR] Backup folder not found.")); return; }

        _log(Loc.Tr("[INFO] Starte Restore Benutzerprofil...", "[INFO] Starting user profile restore..."));
        var args = new List<string> { Quote(backupSourceDir), Quote(destDir), "/E", "/ZB", "/COPYALL", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });

        // Dieselben zwei Schalter wirken auch hier - sonst waeren sie beim
        // Wiederherstellen wirkungslos: robocopy kopiert mit /E den ganzen
        // Sicherungsordner. Enthielt die Sicherung AppData, landete es ohne
        // diese Ausschluesse auch dann auf der Platte, wenn der Haken
        // ausdruecklich NICHT gesetzt ist - und die Rueckfrage davor waere
        // eine leere Geste.
        //
        // Ausgeschlossen wird relativ zum SICHERUNGSORDNER, nicht zum Profil:
        // /XD vergleicht gegen die Quelle.
        //
        // Cloud-Ordner bleiben in diesem Lauf IMMER draussen - auch mit Haken.
        // Mit Haken folgen sie danach einzeln, aber nicht an ihren alten Platz.
        foreach (var ex in GetProfileExcludes(backupSourceDir, IncludeAppData, includeCloud: false))
            if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        if (IncludeAppData)
            _log(Loc.Tr("[INFO] AppData wird mit wiederhergestellt.", "[INFO] AppData is included in the restore."));

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr("[ERFOLG] Benutzerprofil wiederhergestellt.", "[SUCCESS] User profile restored.") : Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));

        if (IncludeCloud) await RestoreCloudFolders(backupSourceDir, destDir);
    }

    /// <summary>
    /// Schreibt die Cloud-Ordner der Sicherung zurueck - jeden NEBEN seinen
    /// alten Platz: "OneDrive" landet in "OneDrive_wiederhergestellt".
    /// Warum, steht bei <see cref="CloudProviders.RestoredFolderName"/>.
    /// </summary>
    private async Task RestoreCloudFolders(string backupSourceDir, string destDir)
    {
        var ordner = CloudProviders.Detect(backupSourceDir);
        if (ordner.Count == 0)
        {
            _log(Loc.Tr("[INFO] Die Sicherung enthält keine Cloud-Ordner.", "[INFO] The backup contains no cloud folders."));
            return;
        }

        foreach (var cloud in ordner)
        {
            if (_cancel.IsCancelled) return;
            var name = Path.GetFileName(cloud.Path);
            var ziel = Path.Combine(destDir, CloudProviders.RestoredFolderName(name));

            _log(Loc.Tr($"[INFO] {cloud.Provider}: \"{name}\" wird nach \"{ziel}\" zurückgeschrieben - nicht in den laufenden Cloud-Ordner, damit der Cloud-Client nichts doppelt hochlädt.",
                        $"[INFO] {cloud.Provider}: \"{name}\" is restored to \"{ziel}\" - not into the live cloud folder, so the cloud client does not upload anything twice."));
            if (Directory.Exists(ziel))
                _log(Loc.Tr("[INFO] Der Ordner besteht schon (frühere Wiederherstellung) und wird ergänzt.",
                            "[INFO] The folder already exists (earlier restore) and is updated."));

            var args = new List<string> { Quote(cloud.Path), Quote(ziel), "/E", "/ZB", "/COPYALL", "/R:1", "/W:1", "/MT:32" };
            if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });

            var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
            if (_cancel.IsCancelled) return;
            _log(code < 8
                ? Loc.Tr($"[ERFOLG] {cloud.Provider} wiederhergestellt nach: {ziel}", $"[SUCCESS] {cloud.Provider} restored to: {ziel}")
                : Loc.Tr($"[FEHLER] {cloud.Provider}: Robocopy Fehlercode {code}", $"[ERROR] {cloud.Provider}: Robocopy error code {code}"));
        }
    }

    // ------------------------------------------------------------ Browser-Profile

    public async Task BackupAppProfile(string appName, string profileRel, string processName, string[]? excludeDirs = null)
    {
        var appProfilePath = Path.Combine(SourcePath, profileRel);
        if (!Directory.Exists(appProfilePath)) { _log(Loc.Tr($"[FEHLER] Pfad nicht gefunden: {appProfilePath}", $"[ERROR] Path not found: {appProfilePath}")); return; }

        _log(Loc.Tr($"[INFO] Beende {appName}...", $"[INFO] Closing {appName}..."));
        KillProcess(processName);
        await Task.Delay(2000);

        var targetBackupDir = Path.Combine(BackupPath, $"{appName}-Profil");
        var args = new List<string> { Quote(appProfilePath), Quote(targetBackupDir), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        foreach (var ex in excludeDirs ?? Array.Empty<string>()) { args.Add("/XD"); args.Add(Quote(ex)); }

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr($"[ERFOLG] {appName} Profil gesichert.", $"[SUCCESS] {appName} profile backed up.") : Loc.Tr($"[FEHLER] ExitCode {code}", $"[ERROR] ExitCode {code}"));
    }

    public async Task RestoreAppProfile(string appName, string profileRel, string processName)
    {
        await InstallApp(appName, processName + ".exe");

        var backupSourceDir = Path.Combine(BackupPath, $"{appName}-Profil");
        if (!Directory.Exists(backupSourceDir)) { _log(Loc.Tr($"[FEHLER] {appName} Backup nicht gefunden.", $"[ERROR] {appName} backup not found.")); return; }
        var targetAppProfileDir = Path.Combine(SourcePath, profileRel);

        _log(Loc.Tr($"[INFO] Beende {appName}...", $"[INFO] Closing {appName}..."));
        KillProcess(processName);
        await Task.Delay(2000);

        if (Directory.Exists(targetAppProfileDir))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var oldDir = $"{targetAppProfileDir}_alt_{stamp}";
            try
            {
                Directory.Move(targetAppProfileDir, oldDir);
                _log(Loc.Tr($"[INFO] Bisheriges {appName}-Profil gesichert nach: {oldDir}", $"[INFO] Previous {appName} profile moved to: {oldDir}"));
            }
            catch (Exception ex)
            {
                // Frueher wurde das bestehende Profil hier rekursiv GELOESCHT. Das
                // scheitert aus demselben Grund wie das Verschieben - eine noch
                // gesperrte Datei - aber erst, nachdem schon ein Teil weg ist.
                // Uebrig blieb ein halb geloeschtes Profil, und schlug danach das
                // Zurueckschreiben fehl, war gar keins mehr da. Abbrechen ist
                // hier die einzige Antwort, die nichts kaputt machen kann.
                _log(Loc.Tr($"[FEHLER] Das bisherige {appName}-Profil ließ sich nicht beiseitelegen ({ex.Message}). "
                            + $"Wiederherstellung abgebrochen - am bestehenden Profil wurde nichts verändert. "
                            + $"Bitte {appName} vollständig schließen und erneut versuchen.",
                            $"[ERROR] The existing {appName} profile could not be moved aside ({ex.Message}). "
                            + $"Restore aborted - the existing profile was left untouched. "
                            + $"Please close {appName} completely and try again."));
                return;
            }
        }
        var parent = Path.GetDirectoryName(targetAppProfileDir);
        if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

        var args = new List<string> { Quote(backupSourceDir), Quote(targetAppProfileDir), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr($"[ERFOLG] {appName} Profil wiederhergestellt.", $"[SUCCESS] {appName} profile restored.") : Loc.Tr($"[FEHLER] ExitCode {code}", $"[ERROR] ExitCode {code}"));
    }

    public string[] ChromiumExcludes => ChromiumCacheExcludes;

    // ------------------------------------------------------------ WLAN

    public async Task ExportWlan()
    {
        var destDir = Path.Combine(BackupPath, "WLAN-Profile");
        Directory.CreateDirectory(destDir);
        _log(Loc.Tr("[INFO] Exportiere WLAN Profile...", "[INFO] Exporting WiFi profiles..."));
        var r = await _runner.RunAsync("netsh", $"wlan export profile folder=\"{destDir}\" key=clear",
                                       SystemHelpers.ConsoleEncoding);

        // Frueher stand hier immer "[ERFOLG]" - auch wenn netsh gar nicht lief
        // oder ablehnte. Typisch auf einem PC ohne WLAN-Adapter: "Der Dienst
        // 'WLAN-AutoKonfig' wird nicht ausgefuehrt."
        if (!r.Started)
        {
            _log(Loc.Tr($"[FEHLER] netsh konnte nicht gestartet werden: {r.StartError}",
                        $"[ERROR] netsh could not be started: {r.StartError}"));
            return;
        }
        var anzahl = Directory.GetFiles(destDir, "*.xml").Length;
        if (r.ExitCode != 0)
        {
            _log(Loc.Tr($"[FEHLER] WLAN-Export fehlgeschlagen (ExitCode {r.ExitCode}): {r.Output.Trim()}",
                        $"[ERROR] WiFi export failed (ExitCode {r.ExitCode}): {r.Output.Trim()}"));
            return;
        }
        _log(anzahl > 0
            ? Loc.Tr($"[ERFOLG] {anzahl} WLAN-Profile exportiert. Sie enthalten die Schlüssel im Klartext - den Sicherungsordner entsprechend schützen.",
                     $"[SUCCESS] {anzahl} WiFi profiles exported. They contain the keys in clear text - protect the backup folder accordingly.")
            : Loc.Tr("[WARNUNG] Auf diesem Rechner sind keine WLAN-Profile gespeichert.",
                     "[WARNING] No WiFi profiles are stored on this computer."));
    }

    public async Task ImportWlan()
    {
        var srcDir = Path.Combine(BackupPath, "WLAN-Profile");
        if (!Directory.Exists(srcDir)) { _log(Loc.Tr("[FEHLER] WLAN Ordner nicht gefunden.", "[ERROR] WiFi folder not found.")); return; }
        _log(Loc.Tr("[INFO] Importiere WLAN Profile...", "[INFO] Importing WiFi profiles..."));
        int ok = 0, fehler = 0;
        foreach (var xml in Directory.GetFiles(srcDir, "*.xml"))
        {
            var r = await _runner.RunAsync("netsh", $"wlan add profile filename=\"{xml}\"",
                                           SystemHelpers.ConsoleEncoding);
            if (r.Started && r.ExitCode == 0) { ok++; continue; }
            fehler++;
            _log(Loc.Tr($"    -> [FEHLER] {Path.GetFileNameWithoutExtension(xml)}: {(r.StartError ?? r.Output).Trim()}",
                        $"    -> [ERROR] {Path.GetFileNameWithoutExtension(xml)}: {(r.StartError ?? r.Output).Trim()}"));
        }
        _log(fehler == 0
            ? Loc.Tr($"[ERFOLG] {ok} WLAN-Profile importiert.", $"[SUCCESS] {ok} WiFi profiles imported.")
            : Loc.Tr($"[FEHLER] WLAN-Import: {ok} importiert, {fehler} fehlgeschlagen.",
                     $"[ERROR] WiFi import: {ok} imported, {fehler} failed."));
    }

    // ------------------------------------------------------------ Drucker

    /// <summary>
    /// Ermittelt den Pfad zu PrintBrm.exe (Print Backup Recovery Migration).
    /// Das ist Microsofts offizielles Werkzeug fuer Druckermigration und
    /// sichert Warteschlangen, Treiber, Ports und Einstellungen in EINE Datei.
    /// </summary>
    private static string? FindPrintBrm()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "spool", "tools", "PrintBrm.exe");
            if (File.Exists(path)) return path;
        }
        catch { /* ignore */ }
        return null;
    }

    private const string PrinterFileName = "Drucker.printerExport";

    public async Task ExportPrinters()
    {
        var brm = FindPrintBrm();
        if (brm is null)
        {
            _log(Loc.Tr("[FEHLER] PrintBrm.exe wurde nicht gefunden. Druckersicherung nicht möglich.",
                        "[ERROR] PrintBrm.exe was not found. Printer backup is not possible."));
            return;
        }

        var destDir = Path.Combine(BackupPath, "Drucker");
        Directory.CreateDirectory(destDir);
        var finalFile = Path.Combine(destDir, PrinterFileName);

        // PrintBrm vertraegt Pfade mit Sonderzeichen (z.B. '#') und Netz-/
        // Wechseldatentraeger nicht zuverlaessig (Fehler 0x8007007b). Deshalb
        // zuerst in einen kurzen Temp-Pfad sichern und danach ans Ziel kopieren.
        var tmpDir = Path.GetTempPath();
        var tmpName = "WDST_Drucker.printerExport";
        var tmpFile = Path.Combine(tmpDir, tmpName);
        try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* ignore */ }

        _log(Loc.Tr("[INFO] Sichere Drucker, Treiber und Ports (das kann einen Moment dauern)...",
                    "[INFO] Backing up printers, drivers and ports (this may take a moment)..."));

        // PrintBrm ist bei der Pfad-Uebergabe extrem eigen (Fehler 0x8007007b).
        // Zuverlaessig ist: im Temp-Ordner als Arbeitsverzeichnis laufen lassen
        // und -F nur den BLOSSEN Dateinamen ohne Pfad uebergeben.
        var r = await _runner.RunAsync(brm, $"-B -F {tmpName}",
                                       SystemHelpers.ConsoleEncoding, 600000, tmpDir);

        if (!r.Started)
        {
            _log(Loc.Tr($"[FEHLER] PrintBrm konnte nicht gestartet werden: {r.StartError}",
                        $"[ERROR] PrintBrm could not be started: {r.StartError}"));
            return;
        }

        // Erfolg wird am Vorhandensein der Temp-Datei festgemacht (PrintBrm liefert
        // nicht immer einen sauberen ExitCode).
        if (File.Exists(tmpFile) && new FileInfo(tmpFile).Length > 0)
        {
            try
            {
                try { if (File.Exists(finalFile)) File.Delete(finalFile); } catch { /* ignore */ }
                File.Move(tmpFile, finalFile);
                var mb = Math.Round(new FileInfo(finalFile).Length / 1048576.0, 1);
                _log(Loc.Tr($"[ERFOLG] Drucker gesichert nach: {finalFile} ({mb} MB)",
                            $"[SUCCESS] Printers backed up to: {finalFile} ({mb} MB)"));
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[FEHLER] Konnte die Sicherung nicht ans Ziel kopieren: {ex.Message}",
                            $"[ERROR] Could not copy the backup to the target: {ex.Message}"));
            }
        }
        else
        {
            _log(Loc.Tr("[FEHLER] Druckersicherung fehlgeschlagen - es wurde keine Datei erzeugt.",
                        "[ERROR] Printer backup failed - no file was created."));
            if (!string.IsNullOrWhiteSpace(r.Output))
                _log(Loc.Tr("[INFO] PrintBrm-Ausgabe: ", "[INFO] PrintBrm output: ") + r.Output.Trim());
            _log(Loc.Tr("[INFO] Mögliche Ursache: Der Druckwarteschlangen-Dienst (Spooler) läuft nicht.",
                        "[INFO] Possible cause: the Print Spooler service is not running."));
        }
    }

    public async Task ImportPrinters()
    {
        var brm = FindPrintBrm();
        if (brm is null)
        {
            _log(Loc.Tr("[FEHLER] PrintBrm.exe wurde nicht gefunden. Wiederherstellung nicht möglich.",
                        "[ERROR] PrintBrm.exe was not found. Restore is not possible."));
            return;
        }

        var file = Path.Combine(BackupPath, "Drucker", PrinterFileName);
        if (!File.Exists(file))
        {
            _log(Loc.Tr("[FEHLER] Keine Druckersicherung gefunden.", "[ERROR] No printer backup found."));
            return;
        }

        _log(Loc.Tr("[INFO] Stelle Drucker, Treiber und Ports wieder her (das kann einen Moment dauern)...",
                    "[INFO] Restoring printers, drivers and ports (this may take a moment)..."));

        // PrintBrm vertraegt Sonderzeichen-Pfade schlecht: erst in Temp kopieren.
        var tmpDir = Path.GetTempPath();
        var tmpName = "WDST_Drucker.printerExport";
        var tmpFile = Path.Combine(tmpDir, tmpName);
        try
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
            File.Copy(file, tmpFile);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Konnte die Sicherung nicht vorbereiten: {ex.Message}",
                        $"[ERROR] Could not prepare the backup: {ex.Message}"));
            return;
        }

        // -R = Restore, -O FORCE = ueberschreiben. Im Temp-Ordner mit blossem Dateinamen.
        var r = await _runner.RunAsync(brm, $"-R -F {tmpName} -O FORCE",
                                       SystemHelpers.ConsoleEncoding, 900000, tmpDir);

        try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* ignore */ }

        if (!r.Started)
        {
            _log(Loc.Tr($"[FEHLER] PrintBrm konnte nicht gestartet werden: {r.StartError}",
                        $"[ERROR] PrintBrm could not be started: {r.StartError}"));
            return;
        }

        if (r.ExitCode == 0)
        {
            _log(Loc.Tr("[ERFOLG] Drucker wiederhergestellt.", "[SUCCESS] Printers restored."));
            _log(Loc.Tr("[INFO] Hinweis: Netzwerkdrucker sind nur erreichbar, wenn der Druckserver verfügbar ist.",
                        "[INFO] Note: network printers are only reachable if the print server is available."));
        }
        else
        {
            _log(Loc.Tr($"[FEHLER] Druckerwiederherstellung fehlgeschlagen (ExitCode: {r.ExitCode}).",
                        $"[ERROR] Printer restore failed (ExitCode: {r.ExitCode})."));
            if (!string.IsNullOrWhiteSpace(r.Output))
                _log(Loc.Tr("[INFO] PrintBrm-Ausgabe: ", "[INFO] PrintBrm output: ") + r.Output.Trim());
        }
    }

    // ------------------------------------------------------------ Hintergrundbild

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uAction, uint uParam, string lpvParam, uint fuWinIni);

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE   = 0x01;
    private const uint SPIF_SENDCHANGE       = 0x02;

    // Merkt sich neben dem Bild auch dessen Originalpfad.
    private const string WallpaperPathFile = "Hintergrundbild-Pfad.txt";

    /// <summary>
    /// Liest den Pfad des aktuellen Desktop-Hintergrundbilds aus der Registry.
    /// </summary>
    private static string? GetCurrentWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var p = key?.GetValue("WallPaper") as string;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch { return null; }
    }

    public async Task ExportWallpaper()
    {
        var src = GetCurrentWallpaperPath();
        if (src is null || !File.Exists(src))
        {
            _log(Loc.Tr("[WARNUNG] Es konnte kein aktuelles Hintergrundbild gefunden werden (evtl. eine Volltonfarbe oder ein Diashow-Hintergrund).",
                        "[WARNING] No current wallpaper file could be found (perhaps a solid color or a slideshow background)."));
            return;
        }

        var destDir = Path.Combine(BackupPath, "Hintergrundbild");
        Directory.CreateDirectory(destDir);

        // Bild unter seinem Originalnamen sichern.
        var fileName = Path.GetFileName(src);
        var dest = Path.Combine(destDir, fileName);

        try
        {
            await Task.Run(() => File.Copy(src, dest, overwrite: true));
            // Originalpfad merken, damit beim Wiederherstellen die gleiche Stelle
            // verwendet werden kann.
            await File.WriteAllTextAsync(Path.Combine(destDir, WallpaperPathFile), src);

            _log(Loc.Tr($"[ERFOLG] Hintergrundbild gesichert. Originalpfad: {src}",
                        $"[SUCCESS] Wallpaper backed up. Original path: {src}"));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht gesichert werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be backed up: {ex.Message}"));
        }
    }

    public async Task ImportWallpaper()
    {
        var srcDir = Path.Combine(BackupPath, "Hintergrundbild");
        var pathFile = Path.Combine(srcDir, WallpaperPathFile);

        if (!Directory.Exists(srcDir))
        {
            _log(Loc.Tr("[FEHLER] Keine Hintergrundbild-Sicherung gefunden.",
                        "[ERROR] No wallpaper backup found."));
            return;
        }

        // Originalpfad einlesen (falls vorhanden).
        string? originalPath = null;
        try { if (File.Exists(pathFile)) originalPath = (await File.ReadAllTextAsync(pathFile)).Trim(); }
        catch { /* ignore */ }

        // Das gesicherte Bild im Backup-Ordner finden. Es wird unter seinem
        // Originalnamen abgelegt - nach mehreren Sicherungen mit wechselnden
        // Hintergruenden liegen deshalb mehrere Bilder nebeneinander. Frueher
        // nahm diese Stelle "die erste Datei", also ein beliebiges davon. Jetzt
        // das Bild, dessen Name zum gemerkten Originalpfad passt, und sonst das
        // zuletzt gesicherte.
        string? backupImage = null;
        try
        {
            var bilder = Directory.EnumerateFiles(srcDir)
                .Where(f => !f.EndsWith(WallpaperPathFile, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var originalName = string.IsNullOrWhiteSpace(originalPath) ? null : Path.GetFileName(originalPath);
            backupImage = bilder.FirstOrDefault(f => originalName is not null
                              && string.Equals(Path.GetFileName(f), originalName, StringComparison.OrdinalIgnoreCase))
                          ?? bilder.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch { /* ignore */ }

        if (backupImage is null)
        {
            _log(Loc.Tr("[FEHLER] In der Sicherung wurde kein Bild gefunden.",
                        "[ERROR] No image was found in the backup."));
            return;
        }

        // Zielpfad bestimmen: bevorzugt der Originalpfad, sonst ein Standardordner.
        string targetPath;
        if (!string.IsNullOrWhiteSpace(originalPath))
            targetPath = originalPath;
        else
            targetPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Path.GetFileName(backupImage));

        try
        {
            // Wenn das Bild bereits am Zielpfad liegt: NICHT kopieren, nur setzen.
            bool alreadyThere = File.Exists(targetPath) &&
                                FilesAreEqual(backupImage, targetPath);

            if (alreadyThere)
            {
                _log(Loc.Tr($"[INFO] Bild liegt bereits am Zielpfad, es wird nur als Hintergrund gesetzt: {targetPath}",
                            $"[INFO] Image is already at the target path, it will only be set as wallpaper: {targetPath}"));
            }
            else
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await Task.Run(() => File.Copy(backupImage, targetPath, overwrite: true));
                _log(Loc.Tr($"[INFO] Hintergrundbild wiederhergestellt nach: {targetPath}",
                            $"[INFO] Wallpaper restored to: {targetPath}"));
            }

            // Als Desktop-Hintergrund setzen.
            bool ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, targetPath,
                                           SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (ok)
                _log(Loc.Tr("[ERFOLG] Hintergrundbild gesetzt.", "[SUCCESS] Wallpaper set."));
            else
                _log(Loc.Tr("[WARNUNG] Das Bild wurde bereitgestellt, konnte aber nicht automatisch gesetzt werden.",
                            "[WARNING] The image was placed but could not be set automatically."));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht wiederhergestellt werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be restored: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------ Desktop-Screenshot

    /// <summary>
    /// Fotografiert den Desktop samt Symbolen, damit sich nach einer
    /// Neuinstallation nachsehen laesst, was wo lag.
    /// </summary>
    public async Task CaptureDesktop()
    {
        // Wessen Desktop ist zu sehen? Gesichert wird oft ein fremdes Profil,
        // waehrend der Techniker selbst angemeldet ist - dann zeigte das Foto
        // den falschen Desktop, und das fiele erst beim Wiederherstellen auf.
        var angemeldet = DesktopScreenshot.GetInteractiveUser();
        if (angemeldet?.ProfilePath is { } eigenesProfil && !ProfileExcludes.SameFolder(eigenesProfil, SourcePath))
        {
            _log(Loc.Tr($"[FEHLER] Kein Desktop-Screenshot: Zu sehen ist der Desktop von {angemeldet.Name} ({eigenesProfil}), "
                        + $"gesichert wird aber {SourcePath}. Das Foto zeigte den falschen Desktop - bitte als dieser Benutzer anmelden und das Tool dort starten.",
                        $"[ERROR] No desktop screenshot: the visible desktop belongs to {angemeldet.Name} ({eigenesProfil}), "
                        + $"but {SourcePath} is being backed up. The picture would show the wrong desktop - please sign in as that user and run the tool there."));
            return;
        }
        if (angemeldet is null)
            _log(Loc.Tr("[INFO] Der angemeldete Benutzer ließ sich nicht ermitteln - aufgenommen wird der Desktop, der gerade zu sehen ist.",
                        "[INFO] The signed-in user could not be determined - the desktop currently on screen is captured."));

        _log(Loc.Tr("[INFO] Alle Fenster werden für einen Moment ausgeblendet, damit die Desktop-Symbole frei liegen...",
                    "[INFO] All windows are hidden for a moment so the desktop icons are visible..."));
        bool ausgeblendet = DesktopScreenshot.ToggleDesktop();
        if (!ausgeblendet)
            _log(Loc.Tr("[WARNUNG] Die Fenster ließen sich nicht ausblenden - offene Fenster verdecken auf dem Foto evtl. Symbole.",
                        "[WARNING] The windows could not be hidden - open windows may cover icons in the picture."));

        ScreenCapture bild;
        try
        {
            // Windows blendet die Fenster mit einer kurzen Animation aus.
            await Task.Delay(ausgeblendet ? 1500 : 300);
            bild = await Task.Run(DesktopScreenshot.CaptureVirtualScreen);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Der Bildschirm ließ sich nicht aufnehmen: {ex.Message}",
                        $"[ERROR] The screen could not be captured: {ex.Message}"));
            return;
        }
        finally
        {
            // Fenster in jedem Fall zurueckholen - auch wenn die Aufnahme scheitert.
            if (ausgeblendet) { DesktopScreenshot.ToggleDesktop(); await Task.Delay(400); }
        }

        if (DesktopScreenshot.IsBlank(bild.Bgra))
            _log(Loc.Tr("[WARNUNG] Das Foto ist schwarz - war der Bildschirm gesperrt oder eine UAC-Abfrage offen? Es wird trotzdem gespeichert.",
                        "[WARNING] The picture is black - was the screen locked or a UAC prompt open? It is saved anyway."));

        var ordner = Path.Combine(BackupPath, DesktopScreenshot.FolderName);
        var ziel = Path.Combine(ordner, DesktopScreenshot.FileName);
        // Erst vollstaendig in eine Zwischendatei, dann an die Stelle des alten
        // Fotos - ein Abbruch beim Schreiben hinterlaesst kein halbes Bild.
        var neu = ziel + ".neu";
        try
        {
            Directory.CreateDirectory(ordner);
            await Task.Run(() =>
            {
                using (var datei = File.Create(neu)) PngWriter.WriteBgra(datei, bild.Width, bild.Height, bild.Bgra);
                File.Move(neu, ziel, overwrite: true);
            });
        }
        catch (Exception ex)
        {
            try { if (File.Exists(neu)) File.Delete(neu); } catch { /* egal */ }
            _log(Loc.Tr($"[FEHLER] Desktop-Screenshot konnte nicht gespeichert werden: {ex.Message}",
                        $"[ERROR] Desktop screenshot could not be saved: {ex.Message}"));
            return;
        }

        var monitore = bild.Monitors == 1 ? Loc.Tr("1 Bildschirm", "1 screen") : Loc.Tr($"{bild.Monitors} Bildschirme", $"{bild.Monitors} screens");
        _log(Loc.Tr($"[ERFOLG] Desktop-Screenshot gespeichert: {ziel} ({bild.Width} × {bild.Height} Pixel, {monitore}).",
                    $"[SUCCESS] Desktop screenshot saved: {ziel} ({bild.Width} × {bild.Height} pixels, {monitore})."));
    }

    /// <summary>
    /// Oeffnet das gesicherte Desktop-Foto, damit sich die Symbole danach von
    /// Hand wieder anordnen lassen. Automatisch zurueckstellen laesst sich die
    /// Anordnung nicht verlaesslich: Der Explorer ueberschreibt sie beim
    /// naechsten Abmelden mit seinem eigenen Stand.
    /// </summary>
    public Task ShowDesktopScreenshot()
    {
        var datei = Path.Combine(BackupPath, DesktopScreenshot.FolderName, DesktopScreenshot.FileName);
        if (!File.Exists(datei))
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegt kein Desktop-Screenshot: {datei}",
                        $"[ERROR] The backup contains no desktop screenshot: {datei}"));
            return Task.CompletedTask;
        }

        try
        {
            // Ueber explorer.exe statt direkt: Das Tool laeuft mit
            // Administratorrechten, und die Fotos-App startet nicht mit
            // erhoehten Rechten. explorer.exe gibt die Datei an den laufenden
            // Explorer des Benutzers weiter, der sie mit normalen Rechten oeffnet.
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", Quote(datei)) { UseShellExecute = false });
            _log(Loc.Tr($"[ERFOLG] Desktop-Screenshot geöffnet: {datei} - daran lassen sich die Symbole wieder anordnen.",
                        $"[SUCCESS] Desktop screenshot opened: {datei} - use it to arrange the icons again."));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Desktop-Screenshot konnte nicht geöffnet werden ({ex.Message}). Er liegt hier: {datei}",
                        $"[ERROR] Desktop screenshot could not be opened ({ex.Message}). It is located here: {datei}"));
        }
        return Task.CompletedTask;
    }

    /// <summary>Vergleicht zwei Dateien inhaltlich (Groesse, dann Bytes).</summary>
    private static bool FilesAreEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            int ba, bb;
            do { ba = sa.ReadByte(); bb = sb.ReadByte(); if (ba != bb) return false; }
            while (ba != -1);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ App-Install/Update

    private async Task InstallApp(string appName, string exeName)
    {
        if (_cancel.IsCancelled) return;
        _log(Loc.Tr($"[INFO] Prüfe, ob {appName} installiert ist...", $"[INFO] Checking whether {appName} is installed..."));
        var appPathKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        using var key = Registry.LocalMachine.OpenSubKey(appPathKey);
        if (key != null) { _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed.")); return; }

        if (appName == "Edge") return;
        var wingetId = appName switch
        {
            "Firefox" => "Mozilla.Firefox.de",
            "Thunderbird" => "Mozilla.Thunderbird.de",
            "Chrome" => "Google.Chrome",
            "Brave" => "Brave.Brave",
            _ => null
        };
        if (wingetId is null) return;

        _log(Loc.Tr($"[INFO] Versuche, {appName} via Winget zu installieren...", $"[INFO] Trying to install {appName} via winget..."));
        var r = await _runner.RunAsync("winget", $"install --id {wingetId} -e --accept-package-agreements --accept-source-agreements");

        // Frueher stand hier immer "Installation abgeschlossen" - auch wenn
        // winget gar nicht startete. Bewusst eine WARNUNG und kein FEHLER: Das
        // Profil selbst wird trotzdem zurueckgeschrieben, und darum geht es
        // bei dieser Aktion. Nur muss der Benutzer wissen, dass das Programm
        // noch fehlt.
        if (!r.Started)
            _log(Loc.Tr($"[WARNUNG] winget konnte nicht gestartet werden ({r.StartError}). {appName} bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] winget could not be started ({r.StartError}). Please install {appName} manually - the profile is restored anyway."));
        else if (r.ExitCode != 0)
            _log(Loc.Tr($"[WARNUNG] {appName} ließ sich per winget nicht installieren (ExitCode {r.ExitCode}). Bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] {appName} could not be installed via winget (ExitCode {r.ExitCode}). Please install it manually - the profile is restored anyway."));
        else
            _log(Loc.Tr($"[INFO] {appName} wurde per winget installiert.", $"[INFO] {appName} was installed via winget."));
    }

    // ------------------------------------------------------------ Helpers

    private static void KillProcess(string processName)
    {
        try { foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName)) p.Kill(true); } catch { }
    }

    private static string Quote(string s) => $"\"{s.TrimEnd('\\')}\"";
}
