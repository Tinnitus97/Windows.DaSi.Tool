using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    public async Task BackupUserProfile()
    {
        var userDir = SourcePath;
        var backupTargetDir = Path.Combine(BackupPath, "Benutzerprofil");

        if (backupTargetDir.StartsWith(userDir, StringComparison.OrdinalIgnoreCase))
        {
            _log(Loc.Tr("[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch.", "[ERROR] Target is inside the source directory. Aborting."));
            return;
        }

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

        var args = new List<string> { Quote(userDir), Quote(backupTargetDir), "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NFL", "/NDL" });
        foreach (var ex in excluded) if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        _log(Loc.Tr("[INFO] Starte Backup Benutzerprofil...", "[INFO] Starting user profile backup..."));
        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log);
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
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NFL", "/NDL" });

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr("[ERFOLG] Benutzerprofil wiederhergestellt.", "[SUCCESS] User profile restored.") : Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));
    }

    // ------------------------------------------------------------ Browser-Profile

    public async Task BackupAppProfile(string appName, string profileRel, string processName, string[]? excludeDirs = null)
    {
        if ((appName is "Firefox" or "Thunderbird") && _cancel.AutoUpdate)
            await CheckAndUpdateMozilla(appName, processName + ".exe");

        var appProfilePath = Path.Combine(SourcePath, profileRel);
        if (!Directory.Exists(appProfilePath)) { _log(Loc.Tr($"[FEHLER] Pfad nicht gefunden: {appProfilePath}", $"[ERROR] Path not found: {appProfilePath}")); return; }

        _log(Loc.Tr($"[INFO] Beende {appName}...", $"[INFO] Closing {appName}..."));
        KillProcess(processName);
        await Task.Delay(2000);

        var targetBackupDir = Path.Combine(BackupPath, $"{appName}-Profil");
        var args = new List<string> { Quote(appProfilePath), Quote(targetBackupDir), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NFL", "/NDL" });
        foreach (var ex in excludeDirs ?? Array.Empty<string>()) { args.Add("/XD"); args.Add(Quote(ex)); }

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr($"[ERFOLG] {appName} Profil gesichert.", $"[SUCCESS] {appName} profile backed up.") : Loc.Tr($"[FEHLER] ExitCode {code}", $"[ERROR] ExitCode {code}"));
    }

    public async Task RestoreAppProfile(string appName, string profileRel, string processName)
    {
        await InstallApp(appName, processName + ".exe");
        if ((appName is "Firefox" or "Thunderbird") && _cancel.AutoUpdate)
            await CheckAndUpdateMozilla(appName, processName + ".exe");

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
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NFL", "/NDL" });

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log);
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

    // ------------------------------------------------------------ App-Install/Update

    private async Task InstallApp(string appName, string exeName)
    {
        if (_cancel.IsCancelled) return;
        _log(Loc.Tr($"[INFO] Pruefe, ob {appName} installiert ist...", $"[INFO] Checking whether {appName} is installed..."));
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

    private async Task CheckAndUpdateMozilla(string appName, string exeName)
    {
        if (_cancel.IsCancelled) return;
        _log(Loc.Tr($"[INFO] Pruefe auf Updates fuer {appName}...", $"[INFO] Checking for updates for {appName}..."));

        string? exePath = null;
        Version? installed = null;
        using (var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"))
        {
            if (key?.GetValue(null) is string p && File.Exists(p))
            {
                exePath = p;
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(p);
                if (Version.TryParse((vi.ProductVersion ?? "").Split(' ')[0], out var v)) installed = v;
                _log(Loc.Tr($"[INFO] Installierte {appName} Version: {installed}", $"[INFO] Installed {appName} version: {installed}"));
            }
            else
            {
                _log(Loc.Tr($"[WARNUNG] {appName} scheint nicht installiert zu sein. Update-Pruefung uebersprungen.", $"[WARNING] {appName} does not appear to be installed. Update check skipped."));
                return;
            }
        }

        Version? latest = null;
        var product = appName.ToLowerInvariant();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var apiUrl = $"https://product-details.mozilla.org/1.0/{product}_versions.json";
            var jsonText = await http.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(jsonText);
            var field = product == "firefox" ? "LATEST_FIREFOX_VERSION" : "LATEST_THUNDERBIRD_VERSION";
            if (doc.RootElement.TryGetProperty(field, out var el) && Version.TryParse(el.GetString(), out var v))
            {
                latest = v;
                _log(Loc.Tr($"[INFO] Neueste verfuegbare {appName} Version: {latest}", $"[INFO] Latest available {appName} version: {latest}"));
            }
        }
        catch { return; }

        if (latest != null && installed != null && latest > installed)
        {
            _log(Loc.Tr($"[AKTION] Update fuer {appName} wird durchgefuehrt (via Winget).", $"[ACTION] Updating {appName} (via winget)."));
            var id = product == "firefox" ? "Mozilla.Firefox.de" : "Mozilla.Thunderbird.de";
            KillProcess(exeName.Replace(".exe", ""));
            await Task.Delay(2000);
            await _runner.RunAsync("winget", $"upgrade --id {id} -e --accept-package-agreements --accept-source-agreements");
            _log(Loc.Tr($"[ERFOLG] {appName} aktualisiert (sofern ein Update verfuegbar war).", $"[SUCCESS] {appName} updated (if an update was available)."));
        }
        else _log(Loc.Tr($"[INFO] {appName} ist bereits aktuell.", $"[INFO] {appName} is already up to date."));
    }

    // ------------------------------------------------------------ Helpers

    private static void KillProcess(string processName)
    {
        try { foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName)) p.Kill(true); } catch { }
    }

    private static string Quote(string s) => $"\"{s.TrimEnd('\\')}\"";
}
