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
    /// Baut die Liste der vom Profil-Backup ausgeschlossenen Ordner (voll
    /// qualifizierte Pfade). Wird sowohl vom eigentlichen Backup als auch von
    /// der Speicher-Schaetzung verwendet, damit beide konsistent sind.
    /// </summary>
    public static List<string> GetProfileExcludes(string userDir)
    {
        var excluded = new List<string>
        {
            "AppData", "Anwendungsdaten", "Application Data", "Cookies", "Links", "Favorites",
            "Local Settings", "My Documents", "NetHood", "PrintHood", "Recent", "Templates",
            "Start Menu", "Druckumgebung", "Netzwerkumgebung", "SendTo", "Vorlagen",
            "Lokale Einstellungen", "Eigene Dateien", "Dropbox", "HiDrive", "Google Drive", "iCloudDrive"
        }.Select(d => Path.Combine(userDir, d)).ToList();

        excluded.Add(Path.Combine(userDir, @"AppData\Local\Temp"));
        excluded.Add(Path.Combine(userDir, @"AppData\Local\Microsoft\Windows\INetCache"));
        excluded.Add(Path.Combine(userDir, @"AppData\Local\Google\Chrome\User Data\Default\Cache"));
        excluded.Add(Path.Combine(userDir, @"AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cache"));

        try
        {
            foreach (var od in Directory.GetDirectories(userDir, "OneDrive*"))
                excluded.Add(od);
        }
        catch { }

        return excluded;
    }

    public async Task BackupUserProfile()
    {
        var userDir = SourcePath;
        var backupTargetDir = Path.Combine(BackupPath, "Benutzerprofil");

        if (backupTargetDir.StartsWith(userDir, StringComparison.OrdinalIgnoreCase))
        {
            _log(Loc.Tr("[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch.", "[ERROR] Target is inside the source directory. Aborting."));
            return;
        }

        var excluded = GetProfileExcludes(userDir);

        var args = new List<string> { Quote(userDir), Quote(backupTargetDir), "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        foreach (var ex in excluded) if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

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

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr("[ERFOLG] Benutzerprofil wiederhergestellt.", "[SUCCESS] User profile restored.") : Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));
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
            try { Directory.Move(targetAppProfileDir, oldDir); _log(Loc.Tr($"[INFO] Bisheriges {appName}-Profil gesichert nach: {oldDir}", $"[INFO] Previous {appName} profile moved to: {oldDir}")); }
            catch { try { Directory.Delete(targetAppProfileDir, true); } catch { } }
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
        await _runner.RunAsync("netsh", $"wlan export profile folder=\"{destDir}\" key=clear");
        _log(Loc.Tr("[ERFOLG] WLAN Profile exportiert.", "[SUCCESS] WiFi profiles exported."));
    }

    public async Task ImportWlan()
    {
        var srcDir = Path.Combine(BackupPath, "WLAN-Profile");
        if (!Directory.Exists(srcDir)) { _log(Loc.Tr("[FEHLER] WLAN Ordner nicht gefunden.", "[ERROR] WiFi folder not found.")); return; }
        _log(Loc.Tr("[INFO] Importiere WLAN Profile...", "[INFO] Importing WiFi profiles..."));
        foreach (var xml in Directory.GetFiles(srcDir, "*.xml"))
            await _runner.RunAsync("netsh", $"wlan add profile filename=\"{xml}\"");
        _log(Loc.Tr("[ERFOLG] WLAN Profile importiert.", "[SUCCESS] WiFi profiles imported."));
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

        // Das gesicherte Bild im Backup-Ordner finden (erste Nicht-Textdatei).
        string? backupImage = null;
        try
        {
            backupImage = Directory.EnumerateFiles(srcDir)
                .FirstOrDefault(f => !f.EndsWith(WallpaperPathFile, StringComparison.OrdinalIgnoreCase));
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
        await _runner.RunAsync("winget", $"install --id {wingetId} -e --accept-package-agreements --accept-source-agreements");
        _log(Loc.Tr("[INFO] Winget-Installation abgeschlossen.", "[INFO] Winget installation finished."));
    }

    // ------------------------------------------------------------ Helpers

    private static void KillProcess(string processName)
    {
        try { foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName)) p.Kill(true); } catch { }
    }

    private static string Quote(string s) => $"\"{s.TrimEnd('\\')}\"";
}
