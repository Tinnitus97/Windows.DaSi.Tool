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

        var args = ProfileBackupArgs(userDir, backupTargetDir);
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

        // Ordner, die in Wahrheit auf einen Ort ausserhalb des Profils zeigen
        // ("Dokumente\Spiele" -> "D:\Spiele"), folgt robocopy nicht (/XJ). Ihr
        // Inhalt fehlte sonst in der Sicherung, ohne dass es irgendwo stuende.
        var (verweise, abgebrochen) = await Task.Run(() =>
        {
            var l = ProfileLinks.FindOutsideLinks(userDir, excluded, out var t);
            return (l, t);
        });
        foreach (var v in verweise)
        {
            var rel = Path.GetRelativePath(userDir, v.Path);
            _log(Loc.Tr($"[WARNUNG] \"{rel}\" ist eine Verknüpfung auf \"{v.Target}\" außerhalb des Profils. Ihr Inhalt wird NICHT mitgesichert - bei Bedarf diesen Ordner selbst sichern.",
                        $"[WARNING] \"{rel}\" is a link to \"{v.Target}\" outside the profile. Its content is NOT backed up - back up that folder separately if needed."));
        }
        if (abgebrochen)
            _log(Loc.Tr($"[INFO] Die Suche nach Verknüpfungen wurde nach {ProfileLinks.MaxFolders:N0} Ordnern beendet.",
                        $"[INFO] The search for links stopped after {ProfileLinks.MaxFolders:N0} folders."));

        _log(Loc.Tr("[INFO] Starte Backup Benutzerprofil...", "[INFO] Starting user profile backup..."));
        var skips = IncludeAppData ? new RobocopySkips(Path.Combine(userDir, "AppData")) : null;
        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress, skips);
        if (_cancel.IsCancelled) return;
        var nurUebersprungen = skips != null && ReportAppDataSkips(skips, code);
        if (code < 8)
            _log(Loc.Tr("[ERFOLG] Benutzerprofil gesichert.", "[SUCCESS] User profile backed up."));
        else if (nurUebersprungen)
            _log(Loc.Tr("[ERFOLG] Benutzerprofil gesichert - bis auf die oben genannten gesperrten bzw. verschwundenen Dateien in AppData.",
                        "[SUCCESS] User profile backed up - except for the locked or vanished files in AppData listed above."));
        else
            _log(Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));
    }

    /// <summary>
    /// Schluss einer Sicherung mit AppData: Zusammenfassung ins Protokoll,
    /// vollstaendige Liste neben die Sicherung (Wurzel des Sicherungsordners,
    /// NICHT in "Benutzerprofil" - sonst landete sie beim Wiederherstellen im
    /// Profil).
    ///
    /// Rueckgabe true: robocopy meldete zwar einen Fehlschlag (Code 8), aber
    /// ausschliesslich gesperrte oder verschwundene Dateien in AppData. Die
    /// Sicherung hat dann alles gesichert, was sich sichern liess.
    /// </summary>
    private bool ReportAppDataSkips(RobocopySkips skips, int code)
    {
        var liste = Path.Combine(BackupPath, RobocopySkips.ListFileName);
        if (skips.Count == 0)
        {
            // Eine Liste von frueher (gleicher Ordner, "Eigener Ordner" aus) waere jetzt falsch.
            try { if (File.Exists(liste)) File.Delete(liste); } catch { /* bleibt eben liegen */ }
            return false;
        }

        string? geschrieben = liste;
        try
        {
            File.WriteAllText(liste, skips.ListText("AppData", DateTime.Now, Loc.Tr), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            geschrieben = null;
            _log(Loc.Tr($"[WARNUNG] Die Liste der nicht gesicherten AppData-Dateien ließ sich nicht schreiben: {ex.Message}",
                        $"[WARNING] The list of AppData files not backed up could not be written: {ex.Message}"));
        }
        _log(skips.Summary("AppData", geschrieben, Loc.Tr));

        // Bit 16 = robocopy selbst ist gescheitert; dann ist nichts "nur uebersprungen".
        return skips.AllExpected && skips.OtherErrors == 0 && (code & 16) == 0;
    }

    public async Task RestoreUserProfile()
    {
        var destDir = SourcePath;
        var backupSourceDir = Path.Combine(BackupPath, "Benutzerprofil");
        if (!Directory.Exists(backupSourceDir)) { _log(Loc.Tr("[FEHLER] Backup-Ordner nicht gefunden.", "[ERROR] Backup folder not found.")); return; }

        _log(Loc.Tr("[INFO] Starte Restore Benutzerprofil...", "[INFO] Starting user profile restore..."));
        var args = ProfileRestoreArgs(backupSourceDir, destDir);

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

            var args = ProfileRestoreArgs(cloud.Path, ziel);

            var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
            if (_cancel.IsCancelled) return;
            _log(code < 8
                ? Loc.Tr($"[ERFOLG] {cloud.Provider} wiederhergestellt nach: {ziel}", $"[SUCCESS] {cloud.Provider} restored to: {ziel}")
                : Loc.Tr($"[FEHLER] {cloud.Provider}: Robocopy Fehlercode {code}", $"[ERROR] {cloud.Provider}: Robocopy error code {code}"));
        }
    }

    // ------------------------------------------------------------ AppData / Cloud ohne Profil
    //
    // AppData und Cloud-Ordner lassen sich auch einzeln sichern, ohne den Rest
    // des Profils. Sie landen an DERSELBEN Stelle wie bei der Profilsicherung
    // (Benutzerprofil\AppData, Benutzerprofil\<Cloud-Ordner>) - so findet jede
    // spaetere Wiederherstellung sie, egal auf welchem Weg gesichert wurde.

    /// <summary>Gemeinsame robocopy-Schalter fuer das Sichern aus dem Profil.</summary>
    private List<string> ProfileBackupArgs(string quelle, string ziel)
    {
        var args = new List<string> { Quote(quelle), Quote(ziel) };
        args.AddRange(ProfileExcludes.BackupSwitches());
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        return args;
    }

    /// <summary>Gemeinsame robocopy-Schalter fuer das Zurueckschreiben ins Profil.</summary>
    private List<string> ProfileRestoreArgs(string quelle, string ziel)
    {
        var args = new List<string> { Quote(quelle), Quote(ziel) };
        args.AddRange(ProfileExcludes.RestoreSwitches());
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        return args;
    }

    /// <summary>Sichert nur AppData - die Zwischenspeicher bleiben wie immer draussen.</summary>
    public async Task BackupAppDataOnly()
    {
        var quelle = Path.Combine(SourcePath, "AppData");
        if (!Directory.Exists(quelle))
        {
            _log(Loc.Tr($"[FEHLER] AppData nicht gefunden: {quelle}", $"[ERROR] AppData not found: {quelle}"));
            return;
        }
        var ziel = Path.Combine(BackupPath, "Benutzerprofil", "AppData");
        if (ProfileExcludes.IsInside(ziel, quelle))
        {
            _log(Loc.Tr("[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch.", "[ERROR] Target is inside the source directory. Aborting."));
            return;
        }

        var args = ProfileBackupArgs(quelle, ziel);
        // Dieselbe Liste wie bei der Profilsicherung, davon nur, was in AppData liegt.
        foreach (var ex in ProfileExcludes.Build(SourcePath, includeAppData: true, includeCloud: true))
            if (ProfileExcludes.IsInside(ex, quelle) && Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        _log(Loc.Tr("[INFO] Sichere AppData (ohne den Rest des Profils, Zwischenspeicher bleiben ausgenommen)...",
                    "[INFO] Backing up AppData (without the rest of the profile, caches remain excluded)..."));
        var skips = new RobocopySkips(quelle);
        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress, skips);
        if (_cancel.IsCancelled) return;
        var nurUebersprungen = ReportAppDataSkips(skips, code);
        if (code < 8)
            _log(Loc.Tr("[ERFOLG] AppData gesichert.", "[SUCCESS] AppData backed up."));
        else if (nurUebersprungen)
            _log(Loc.Tr("[ERFOLG] AppData gesichert - bis auf die oben genannten gesperrten bzw. verschwundenen Dateien.",
                        "[SUCCESS] AppData backed up - except for the locked or vanished files listed above."));
        else
            _log(Loc.Tr($"[FEHLER] AppData: Robocopy Fehlercode {code}", $"[ERROR] AppData: Robocopy error code {code}"));
    }

    /// <summary>Sichert nur die erkannten Cloud-Ordner, jeden fuer sich.</summary>
    public async Task BackupCloudOnly()
    {
        var ordner = CloudProviders.Detect(SourcePath);
        if (ordner.Count == 0)
        {
            _log(Loc.Tr("[WARNUNG] Im Profil wurde kein Cloud-Ordner gefunden - nichts zu sichern.",
                        "[WARNING] No cloud folder was found in the profile - nothing to back up."));
            return;
        }
        _log(Loc.Tr($"[INFO] Sichere Cloud-Ordner (ohne den Rest des Profils): {CloudProviders.Summarize(ordner)}",
                    $"[INFO] Backing up cloud folders (without the rest of the profile): {CloudProviders.Summarize(ordner)}"));

        foreach (var cloud in ordner)
        {
            if (_cancel.IsCancelled) return;
            var name = Path.GetFileName(cloud.Path);
            var ziel = Path.Combine(BackupPath, "Benutzerprofil", name);
            if (ProfileExcludes.IsInside(ziel, cloud.Path))
            {
                _log(Loc.Tr($"[FEHLER] {cloud.Provider}: Ziel liegt im Cloud-Ordner selbst. Übersprungen.",
                            $"[ERROR] {cloud.Provider}: target is inside the cloud folder itself. Skipped."));
                continue;
            }
            var code = await _runner.RunRobocopyAsync(string.Join(" ", ProfileBackupArgs(cloud.Path, ziel)), _log, OnFilesProgress);
            if (_cancel.IsCancelled) return;
            _log(code < 8
                ? Loc.Tr($"[ERFOLG] {cloud.Provider} gesichert: \"{name}\"", $"[SUCCESS] {cloud.Provider} backed up: \"{name}\"")
                : Loc.Tr($"[FEHLER] {cloud.Provider}: Robocopy Fehlercode {code}", $"[ERROR] {cloud.Provider}: Robocopy error code {code}"));
        }
    }

    /// <summary>Schreibt nur AppData zurueck - aus der Sicherung, wie auch immer sie entstand.</summary>
    public async Task RestoreAppDataOnly()
    {
        var quelle = Path.Combine(BackupPath, "Benutzerprofil", "AppData");
        if (!Directory.Exists(quelle))
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegt kein AppData: {quelle}", $"[ERROR] The backup contains no AppData: {quelle}"));
            return;
        }
        var ziel = Path.Combine(SourcePath, "AppData");
        _log(Loc.Tr("[INFO] Schreibe AppData zurück (ohne den Rest des Profils)...",
                    "[INFO] Restoring AppData (without the rest of the profile)..."));
        var code = await _runner.RunRobocopyAsync(string.Join(" ", ProfileRestoreArgs(quelle, ziel)), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr("[ERFOLG] AppData wiederhergestellt.", "[SUCCESS] AppData restored.")
                      : Loc.Tr($"[FEHLER] AppData: Robocopy Fehlercode {code}", $"[ERROR] AppData: Robocopy error code {code}"));
    }

    /// <summary>Schreibt nur die Cloud-Ordner zurueck - wie immer neben das Original.</summary>
    public async Task RestoreCloudOnly()
    {
        var sicherung = Path.Combine(BackupPath, "Benutzerprofil");
        if (!Directory.Exists(sicherung))
        {
            _log(Loc.Tr("[FEHLER] Backup-Ordner nicht gefunden.", "[ERROR] Backup folder not found."));
            return;
        }
        await RestoreCloudFolders(sicherung, SourcePath);
    }

    // ------------------------------------------------------------ Browser-Profile

    /// <summary>Sichert das Profil eines Programms aus dem Katalog (Firefox, Edge, Chrome, Brave, Thunderbird).</summary>
    public async Task BackupAppProfile(AppEintrag app)
    {
        var quelle = AppCatalog.Pfad(SourcePath, app.ProfilRel);
        if (!Directory.Exists(quelle)) { _log(Loc.Tr($"[FEHLER] Pfad nicht gefunden: {quelle}", $"[ERROR] Path not found: {quelle}")); return; }

        _log(Loc.Tr($"[INFO] Beende {app.Name}...", $"[INFO] Closing {app.Name}..."));
        KillProcess(app.Prozess);
        await Task.Delay(2000);

        await MirrorAppProfile(app.Name, quelle, Path.Combine(BackupPath, app.BackupOrdner), app.Chromium ? ChromiumCacheExcludes : null);
    }

    /// <summary>Schreibt das Profil eines Programms aus dem Katalog zurueck - vorher wird es bei Bedarf installiert.</summary>
    public async Task RestoreAppProfile(AppEintrag app)
    {
        await InstallApp(app.Name, app.Prozess + ".exe", app.WingetId);

        var quelle = Path.Combine(BackupPath, app.BackupOrdner);
        if (!Directory.Exists(quelle)) { _log(Loc.Tr($"[FEHLER] {app.Name} Backup nicht gefunden.", $"[ERROR] {app.Name} backup not found.")); return; }

        _log(Loc.Tr($"[INFO] Beende {app.Name}...", $"[INFO] Closing {app.Name}..."));
        KillProcess(app.Prozess);
        await Task.Delay(2000);

        await RestoreAppDir(app.Name, quelle, AppCatalog.Pfad(SourcePath, app.ProfilRel));
    }

    // ------------------------------------------------------------ Opera (alle Fassungen)

    /// <summary>
    /// Sichert jede Opera-Fassung des Benutzers (Opera, Opera GX, Beta ...) in
    /// einen eigenen Unterordner von "Opera-Profil".
    /// </summary>
    public async Task BackupOpera()
    {
        var wurzel = AppCatalog.Pfad(SourcePath, AppCatalog.Opera.Rel);
        var varianten = AppCatalog.Opera.Varianten(wurzel);
        if (varianten.Count == 0)
        {
            _log(Loc.Tr($"[FEHLER] Kein Opera-Profil gefunden unter: {wurzel}", $"[ERROR] No Opera profile found under: {wurzel}"));
            return;
        }
        var namen = string.Join(", ", varianten.Select(AppCatalog.Opera.Anzeigename));
        _log(Loc.Tr($"[INFO] Gefundene Opera-Fassungen: {namen}", $"[INFO] Opera editions found: {namen}"));

        // Alle Fassungen laufen als opera.exe - einmal beenden genuegt.
        _log(Loc.Tr("[INFO] Beende Opera...", "[INFO] Closing Opera..."));
        KillOpera();
        await Task.Delay(2000);

        foreach (var v in varianten)
        {
            if (_cancel.IsCancelled) return;
            await MirrorAppProfile(AppCatalog.Opera.Anzeigename(v), Path.Combine(wurzel, v),
                                   Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner, v), ChromiumCacheExcludes);
        }
    }

    /// <summary>Schreibt jede gesicherte Opera-Fassung zurueck, jede an ihren Platz.</summary>
    public async Task RestoreOpera()
    {
        var quelle = Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner);
        var varianten = AppCatalog.Opera.Varianten(quelle);
        if (varianten.Count == 0)
        {
            _log(Loc.Tr("[FEHLER] Opera Backup nicht gefunden.", "[ERROR] Opera backup not found."));
            return;
        }
        var ziel = AppCatalog.Pfad(SourcePath, AppCatalog.Opera.Rel);
        foreach (var v in varianten)
        {
            if (_cancel.IsCancelled) return;
            var name = AppCatalog.Opera.Anzeigename(v);
            await InstallOpera(v);
            _log(Loc.Tr($"[INFO] Beende {name}...", $"[INFO] Closing {name}..."));
            KillOpera();
            await Task.Delay(2000);
            await RestoreAppDir(name, Path.Combine(quelle, v), Path.Combine(ziel, v));
        }
    }

    /// <summary>Opera selbst und seine Begleiter, die Dateien im Profil offen halten.</summary>
    private static void KillOpera()
    {
        foreach (var name in new[] { AppCatalog.Opera.Prozess, "opera_autoupdate", "browser_assistant" })
            KillProcess(name);
    }

    /// <summary>
    /// Ist diese Opera-Fassung installiert? Opera installiert sich meist nur
    /// fuer den Benutzer (AppData\Local\Programs\&lt;Fassung&gt;), seltener fuer alle
    /// (Programme). Sonst per winget - soweit das Paket bekannt ist.
    /// </summary>
    private async Task InstallOpera(string variante)
    {
        if (_cancel.IsCancelled) return;
        var name = AppCatalog.Opera.Anzeigename(variante);
        var orte = new[]
        {
            Path.Combine(SourcePath, "AppData", "Local", "Programs", name, "opera.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name, "opera.exe"),
        };
        if (orte.Any(File.Exists))
        {
            _log(Loc.Tr($"[INFO] {name} ist bereits installiert.", $"[INFO] {name} is already installed."));
            return;
        }
        var id = AppCatalog.Opera.WingetId(variante);
        if (id is null)
        {
            _log(Loc.Tr($"[WARNUNG] {name} ist nicht installiert und lässt sich nicht automatisch nachinstallieren. Bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] {name} is not installed and cannot be installed automatically. Please install it manually - the profile is restored anyway."));
            return;
        }
        await InstallViaWinget(name, id);
    }

    // ------------------------------------------------------------ Programmprofile: gemeinsamer Ablauf

    /// <summary>Spiegelt ein Programmprofil in die Sicherung.</summary>
    private async Task MirrorAppProfile(string name, string quelle, string ziel, string[]? excludeDirs)
    {
        var args = new List<string> { Quote(quelle), Quote(ziel), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        foreach (var ex in excludeDirs ?? Array.Empty<string>()) { args.Add("/XD"); args.Add(Quote(ex)); }

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr($"[ERFOLG] {name} Profil gesichert.", $"[SUCCESS] {name} profile backed up.")
                      : Loc.Tr($"[FEHLER] {name}: ExitCode {code}", $"[ERROR] {name}: ExitCode {code}"));
    }

    /// <summary>
    /// Schreibt ein Programmprofil zurueck. Ein vorhandenes Profil wird vorher
    /// beiseitegelegt ("..._alt_&lt;Zeit&gt;"), nicht geloescht.
    /// </summary>
    private async Task RestoreAppDir(string name, string quelle, string ziel)
    {
        if (Directory.Exists(ziel))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var oldDir = $"{ziel}_alt_{stamp}";
            try
            {
                Directory.Move(ziel, oldDir);
                _log(Loc.Tr($"[INFO] Bisheriges {name}-Profil gesichert nach: {oldDir}", $"[INFO] Previous {name} profile moved to: {oldDir}"));
            }
            catch (Exception ex)
            {
                // Frueher wurde das bestehende Profil hier rekursiv GELOESCHT. Das
                // scheitert aus demselben Grund wie das Verschieben - eine noch
                // gesperrte Datei - aber erst, nachdem schon ein Teil weg ist.
                // Uebrig blieb ein halb geloeschtes Profil, und schlug danach das
                // Zurueckschreiben fehl, war gar keins mehr da. Abbrechen ist
                // hier die einzige Antwort, die nichts kaputt machen kann.
                _log(Loc.Tr($"[FEHLER] Das bisherige {name}-Profil ließ sich nicht beiseitelegen ({ex.Message}). "
                            + $"Wiederherstellung abgebrochen - am bestehenden Profil wurde nichts verändert. "
                            + $"Bitte {name} vollständig schließen und erneut versuchen.",
                            $"[ERROR] The existing {name} profile could not be moved aside ({ex.Message}). "
                            + $"Restore aborted - the existing profile was left untouched. "
                            + $"Please close {name} completely and try again."));
                return;
            }
        }
        var parent = Path.GetDirectoryName(ziel);
        if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

        var args = new List<string> { Quote(quelle), Quote(ziel), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return;
        _log(code < 8 ? Loc.Tr($"[ERFOLG] {name} Profil wiederhergestellt.", $"[SUCCESS] {name} profile restored.")
                      : Loc.Tr($"[FEHLER] {name}: ExitCode {code}", $"[ERROR] {name}: ExitCode {code}"));
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

    // Merkt sich neben dem Bild auch dessen Originalpfad ...
    private const string WallpaperPathFile = "Hintergrundbild-Pfad.txt";
    // ... und das gesicherte Profil, damit sich der Pfad beim Zurueckschreiben
    // in ein ANDERES Profil uebertragen laesst (Besitzer -> Anwender).
    // Eigene Datei, damit aeltere Fassungen die Pfad-Datei weiter lesen koennen.
    private const string WallpaperProfileFile = "Hintergrundbild-Profil.txt";

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
            await File.WriteAllTextAsync(Path.Combine(destDir, WallpaperProfileFile), SourcePath);

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

        // Originalpfad und gesichertes Profil einlesen (falls vorhanden).
        string? originalPath = null, originalProfile = null;
        try { if (File.Exists(pathFile)) originalPath = (await File.ReadAllTextAsync(pathFile)).Trim(); }
        catch { /* ignore */ }
        try
        {
            var profilDatei = Path.Combine(srcDir, WallpaperProfileFile);
            if (File.Exists(profilDatei)) originalProfile = (await File.ReadAllTextAsync(profilDatei)).Trim();
        }
        catch { /* aeltere Sicherung - dann greift die Regel fuer C:\Users\<Name> */ }

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
                .Where(f => !f.EndsWith(WallpaperPathFile, StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(WallpaperProfileFile, StringComparison.OrdinalIgnoreCase))
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

        // Zielpfad bestimmen - IMMER im gewaehlten Zielprofil.
        //
        // Frueher wurde der gespeicherte Originalpfad unveraendert benutzt. Auf
        // einem Rechner mit anderem Benutzernamen (gesichert: Besitzer,
        // zurueck zu: Anwender) entstand so ein Ordner "C:\Users\Besitzer" -
        // ein Profil, das es dort nicht gibt. Und ohne gespeicherten Pfad ging
        // das Bild in "Bilder" dessen, der das Tool ausfuehrt, nicht des
        // Zielprofils.
        string targetPath;
        var imZielprofil = ProfilePaths.MapIntoProfile(originalPath, originalProfile, SourcePath);
        if (imZielprofil is not null)
        {
            targetPath = imZielprofil;
        }
        else if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath)
                 && FilesAreEqual(backupImage, originalPath))
        {
            // Ein Windows-Hintergrund ausserhalb jedes Profils (C:\Windows\Web\...),
            // der hier schon genau so liegt: nur setzen, nichts kopieren.
            targetPath = originalPath;
        }
        else
        {
            targetPath = Path.Combine(SourcePath, "Pictures", Path.GetFileName(backupImage));
        }
        if (!string.IsNullOrWhiteSpace(originalPath)
            && !string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            _log(Loc.Tr($"[INFO] Gesichert war das Bild unter {originalPath} - es kommt ins gewählte Profil: {targetPath}",
                        $"[INFO] The image was backed up from {originalPath} - it goes into the selected profile: {targetPath}"));

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

        // Fenster beiseitelegen - und sich genau merken, welche, in welcher
        // Reihenfolge und welches aktiv war. Nur so kommen sie danach alle
        // wieder so zurueck, wie sie waren.
        DesktopScreenshot.HiddenWindows? beiseite = null;
        try
        {
            beiseite = DesktopScreenshot.HideWindows();
            _log(Loc.Tr($"[INFO] {beiseite.TopToBottom.Count} Fenster werden für einen Moment beiseitegelegt, damit die Desktop-Symbole frei liegen...",
                        $"[INFO] {beiseite.TopToBottom.Count} windows are moved aside for a moment so the desktop icons are visible..."));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[WARNUNG] Die Fenster ließen sich nicht ausblenden ({ex.Message}) - offene Fenster verdecken auf dem Foto evtl. Symbole.",
                        $"[WARNING] The windows could not be hidden ({ex.Message}) - open windows may cover icons in the picture."));
        }

        ScreenCapture bild;
        try
        {
            // Windows minimiert mit einer kurzen Animation.
            await Task.Delay(beiseite is null ? 300 : 1200);
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
            // In jedem Fall zurueckholen - auch wenn die Aufnahme scheitert.
            if (beiseite is not null)
            {
                try
                {
                    await Task.Run(() => DesktopScreenshot.RestoreWindows(beiseite));
                    _log(Loc.Tr($"[INFO] {beiseite.TopToBottom.Count} Fenster wieder an ihrem Platz, in der alten Reihenfolge.",
                                $"[INFO] {beiseite.TopToBottom.Count} windows back in place, in their previous order."));
                }
                catch (Exception ex)
                {
                    _log(Loc.Tr($"[WARNUNG] Nicht alle Fenster ließen sich zurückholen ({ex.Message}) - sie liegen minimiert in der Taskleiste.",
                                $"[WARNING] Not all windows could be brought back ({ex.Message}) - they are minimized in the taskbar."));
                }
            }
        }

        if (DesktopScreenshot.IsBlank(bild.Bgra))
            _log(Loc.Tr("[WARNUNG] Das Foto ist schwarz - war der Bildschirm gesperrt oder eine UAC-Abfrage offen? Es wird trotzdem gespeichert.",
                        "[WARNING] The picture is black - was the screen locked or a UAC prompt open? It is saved anyway."));

        var ordner = Path.Combine(BackupPath, DesktopScreenshot.FolderName);
        var plan = DesktopScreenshot.PlanImages(bild.Areas);
        var gespeichert = new List<string>();
        string? neu = null;
        try
        {
            Directory.CreateDirectory(ordner);
            foreach (var m in plan)
            {
                var ziel = Path.Combine(ordner, m.FileName);
                // Erst vollstaendig in eine Zwischendatei, dann an ihren Platz -
                // ein Abbruch beim Schreiben hinterlaesst kein halbes Bild.
                neu = ziel + ".neu";
                var pfad = neu;
                await Task.Run(() =>
                {
                    var teil = DesktopScreenshot.Crop(bild.Bgra, bild.Width, bild.Height,
                        m.Area.Left - bild.Left, m.Area.Top - bild.Top, m.Area.Width, m.Area.Height,
                        out var w, out var h);
                    using (var datei = File.Create(pfad)) PngWriter.WriteBgra(datei, w, h, teil);
                    File.Move(pfad, ziel, overwrite: true);
                });
                neu = null;
                gespeichert.Add(Loc.Tr(
                    $"    {m.FileName} ({m.Area.Width} × {m.Area.Height} Pixel{(m.Area.Primary && plan.Count > 1 ? ", Hauptbildschirm" : "")})",
                    $"    {m.FileName} ({m.Area.Width} × {m.Area.Height} pixels{(m.Area.Primary && plan.Count > 1 ? ", main screen" : "")})"));
            }

            // Bilder einer frueheren Sicherung in denselben Ordner ("Eigener
            // Ordner" aus) gehen weg - erst jetzt, da die neuen sicher liegen.
            // Sonst laege bei inzwischen weniger Monitoren ein altes Bild daneben.
            var neueNamen = plan.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var alt in DesktopScreenshot.FindImages(ordner)
                         .Append(Path.Combine(ordner, DesktopScreenshot.LegacyFileName)))
            {
                if (neueNamen.Contains(Path.GetFileName(alt))) continue;
                try { if (File.Exists(alt)) File.Delete(alt); } catch { /* bleibt eben liegen */ }
            }
        }
        catch (Exception ex)
        {
            try { if (neu != null && File.Exists(neu)) File.Delete(neu); } catch { /* egal */ }
            _log(Loc.Tr($"[FEHLER] Desktop-Screenshot konnte nicht gespeichert werden: {ex.Message}",
                        $"[ERROR] Desktop screenshot could not be saved: {ex.Message}"));
            return;
        }

        var kopf = plan.Count == 1
            ? Loc.Tr($"[ERFOLG] Desktop-Screenshot gespeichert in {ordner}:", $"[SUCCESS] Desktop screenshot saved in {ordner}:")
            : Loc.Tr($"[ERFOLG] Desktop-Screenshot gespeichert, ein Bild pro Bildschirm ({plan.Count}) in {ordner}:",
                     $"[SUCCESS] Desktop screenshot saved, one picture per screen ({plan.Count}) in {ordner}:");
        _log(kopf + "\n" + string.Join("\n", gespeichert));
    }

    /// <summary>
    /// Oeffnet das gesicherte Desktop-Foto, damit sich die Symbole danach von
    /// Hand wieder anordnen lassen. Automatisch zurueckstellen laesst sich die
    /// Anordnung nicht verlaesslich: Der Explorer ueberschreibt sie beim
    /// naechsten Abmelden mit seinem eigenen Stand.
    /// </summary>
    public Task ShowDesktopScreenshot()
    {
        var ordner = Path.Combine(BackupPath, DesktopScreenshot.FolderName);
        var bilder = DesktopScreenshot.FindImages(ordner);
        if (bilder.Count == 0)
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegt kein Desktop-Screenshot: {ordner}",
                        $"[ERROR] The backup contains no desktop screenshot: {ordner}"));
            return Task.CompletedTask;
        }

        // Jedes Bild in seinem eigenen Fenster - so laesst sich jedes auf
        // "seinen" Monitor ziehen und die Symbole daneben anordnen.
        int offen = 0;
        foreach (var datei in bilder)
        {
            try
            {
                // Ueber explorer.exe statt direkt: Das Tool laeuft mit
                // Administratorrechten, und die Fotos-App startet nicht mit
                // erhoehten Rechten. explorer.exe gibt die Datei an den laufenden
                // Explorer des Benutzers weiter, der sie mit normalen Rechten oeffnet.
                using var _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", Quote(datei)) { UseShellExecute = false });
                offen++;
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[FEHLER] Desktop-Screenshot konnte nicht geöffnet werden ({ex.Message}). Er liegt hier: {datei}",
                            $"[ERROR] Desktop screenshot could not be opened ({ex.Message}). It is located here: {datei}"));
            }
        }
        if (offen > 0)
            _log(Loc.Tr($"[ERFOLG] Desktop-Screenshot geöffnet ({offen} {(offen == 1 ? "Bild" : "Bilder")}): {ordner} - daran lassen sich die Symbole wieder anordnen.",
                        $"[SUCCESS] Desktop screenshot opened ({offen} {(offen == 1 ? "picture" : "pictures")}): {ordner} - use it to arrange the icons again."));
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ Desktop-Symbole (wie DesktopOK)

    /// <summary>
    /// Der Explorer zeigt den Desktop des angemeldeten Benutzers. Gehoert das
    /// gewaehlte Profil jemand anderem, wuerde der falsche Desktop gelesen bzw.
    /// umgestellt - dann lieber gar nichts tun.
    /// </summary>
    private bool DesktopGehoertZumProfil(string wasDe, string wasEn)
    {
        var angemeldet = DesktopScreenshot.GetInteractiveUser();
        if (angemeldet?.ProfilePath is { } eigenesProfil && !ProfileExcludes.SameFolder(eigenesProfil, SourcePath))
        {
            _log(Loc.Tr($"[FEHLER] {wasDe}: Zu sehen ist der Desktop von {angemeldet.Name} ({eigenesProfil}), gewählt ist aber {SourcePath}. "
                        + "Bitte als dieser Benutzer anmelden und das Tool dort starten.",
                        $"[ERROR] {wasEn}: the visible desktop belongs to {angemeldet.Name} ({eigenesProfil}), but {SourcePath} is selected. "
                        + "Please sign in as that user and run the tool there."));
            return false;
        }
        if (angemeldet is null)
            _log(Loc.Tr("[INFO] Der angemeldete Benutzer ließ sich nicht ermitteln - verwendet wird der Desktop, der gerade zu sehen ist.",
                        "[INFO] The signed-in user could not be determined - the desktop currently on screen is used."));
        return true;
    }

    /// <summary>Merkt sich, welches Desktop-Symbol wo liegt.</summary>
    public async Task BackupDesktopIcons()
    {
        if (!DesktopGehoertZumProfil("Keine Symbolpositionen gesichert", "No icon positions backed up")) return;

        DesktopLayout layout;
        try
        {
            var benutzer = Path.GetFileName(SourcePath.TrimEnd('\\', '/'));
            layout = await Task.Run(() => DesktopIcons.Lesen(Environment.MachineName, benutzer));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Desktop-Symbole ließen sich nicht lesen: {ex.Message}",
                        $"[ERROR] The desktop icons could not be read: {ex.Message}"));
            return;
        }
        if (layout.Symbole.Count == 0)
        {
            _log(Loc.Tr("[WARNUNG] Auf dem Desktop liegen keine Symbole - es gibt keine Positionen zu sichern.",
                        "[WARNING] There are no icons on the desktop - there are no positions to back up."));
            return;
        }

        string datei;
        try { datei = DesktopIcons.Speichern(Path.Combine(BackupPath, DesktopIcons.FolderName), layout); }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Symbolpositionen konnten nicht gespeichert werden: {ex.Message}",
                        $"[ERROR] Icon positions could not be saved: {ex.Message}"));
            return;
        }
        if (layout.AutomatischAnordnen)
            _log(Loc.Tr("[INFO] \"Symbole automatisch anordnen\" ist an - Windows reiht die Symbole selbst. Das wird mit vermerkt.",
                        "[INFO] \"Auto arrange icons\" is on - Windows lines up the icons itself. This is noted as well."));
        _log(Loc.Tr($"[ERFOLG] Positionen von {layout.Symbole.Count} Desktop-Symbolen gesichert (Desktop {layout.Breite} x {layout.Hoehe}): {datei}",
                    $"[SUCCESS] Positions of {layout.Symbole.Count} desktop icons backed up (desktop {layout.Breite} x {layout.Hoehe}): {datei}"));
    }

    /// <summary>
    /// Setzt jedes gesicherte Desktop-Symbol wieder an seine Stelle. Laeuft
    /// zuletzt - nach Profil, Programmen und Hintergrundbild, damit die Symbole
    /// dann auch da sind.
    /// </summary>
    public async Task RestoreDesktopIcons()
    {
        var datei = DesktopIcons.Datei(BackupPath);
        var layout = DesktopIcons.Laden(datei);
        if (layout is null)
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegen keine Symbolpositionen: {datei}",
                        $"[ERROR] The backup contains no icon positions: {datei}"));
            return;
        }
        if (!DesktopGehoertZumProfil("Symbolpositionen nicht gesetzt", "Icon positions not applied")) return;

        _log(Loc.Tr($"[INFO] Setze {layout.Symbole.Count} Desktop-Symbole an ihre gesicherte Stelle (gesichert am {layout.Erstellt:dd.MM.yyyy} auf {layout.Rechner})...",
                    $"[INFO] Moving {layout.Symbole.Count} desktop icons to their saved positions (saved on {layout.Erstellt:yyyy-MM-dd} on {layout.Rechner})..."));
        IconErgebnis erg;
        try { erg = await Task.Run(() => DesktopIcons.Wiederherstellen(layout, TimeSpan.FromSeconds(10))); }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Symbolpositionen ließen sich nicht setzen: {ex.Message}",
                        $"[ERROR] The icon positions could not be applied: {ex.Message}"));
            return;
        }

        if (erg.AnordnenAusgeschaltet)
            _log(Loc.Tr("[INFO] \"Symbole automatisch anordnen\" war an und ist jetzt aus - sonst reihte Windows die Symbole sofort wieder neu.",
                        "[INFO] \"Auto arrange icons\" was on and is now off - otherwise Windows would line the icons up again right away."));
        if (erg.AnordnenWiederAn)
            _log(Loc.Tr("[INFO] Beim Sichern war \"Symbole automatisch anordnen\" an - so ist es jetzt auch hier.",
                        "[INFO] \"Auto arrange icons\" was on when backing up - it is now on here as well."));
        if (erg.RasterUmgestellt)
            _log(Loc.Tr($"[INFO] \"Symbole am Raster ausrichten\" wie beim Sichern: {(layout.AmRasterAusrichten ? "an" : "aus")}.",
                        $"[INFO] \"Align icons to grid\" as when backing up: {(layout.AmRasterAusrichten ? "on" : "off")}."));
        if (erg.Skaliert)
            _log(Loc.Tr($"[INFO] Der Desktop ist hier {erg.BreiteJetzt} x {erg.HoeheJetzt} groß statt {layout.Breite} x {layout.Hoehe} - die Positionen wurden im selben Verhältnis umgerechnet.",
                        $"[INFO] The desktop here is {erg.BreiteJetzt} x {erg.HoeheJetzt} instead of {layout.Breite} x {layout.Hoehe} - the positions were scaled accordingly."));
        if (erg.Fehlend.Count > 0)
        {
            var namen = string.Join(", ", erg.Fehlend.Take(10).Select(f => string.IsNullOrWhiteSpace(f.Anzeige) ? f.Name : f.Anzeige))
                        + (erg.Fehlend.Count > 10 ? ", ..." : "");
            _log(Loc.Tr($"[WARNUNG] {erg.Fehlend.Count} von {erg.Gesamt} Symbolen gibt es auf diesem Desktop nicht: {namen}. "
                        + "Sie wurden ausgelassen - am besten zusammen mit dem Benutzerprofil und den Programmen wiederherstellen.",
                        $"[WARNING] {erg.Fehlend.Count} of {erg.Gesamt} icons do not exist on this desktop: {namen}. "
                        + "They were skipped - best restore them together with the user profile and the programs."));
        }
        _log(erg.Gesetzt > 0
            ? Loc.Tr($"[ERFOLG] {erg.Gesetzt} von {erg.Gesamt} Desktop-Symbolen an ihre gesicherte Stelle gesetzt.",
                     $"[SUCCESS] {erg.Gesetzt} of {erg.Gesamt} desktop icons moved to their saved positions.")
            : Loc.Tr("[FEHLER] Kein gesichertes Symbol ließ sich auf diesem Desktop zuordnen.",
                     "[ERROR] None of the saved icons could be matched on this desktop."));
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

    private async Task InstallApp(string appName, string exeName, string? wingetId)
    {
        if (_cancel.IsCancelled) return;
        _log(Loc.Tr($"[INFO] Prüfe, ob {appName} installiert ist...", $"[INFO] Checking whether {appName} is installed..."));
        var appPathKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        using var key = Registry.LocalMachine.OpenSubKey(appPathKey);
        if (key != null) { _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed.")); return; }

        // Edge gehoert zu Windows, fuer ihn gibt es kein Paket.
        if (wingetId is null) return;
        await InstallViaWinget(appName, wingetId);
    }

    private async Task InstallViaWinget(string appName, string wingetId)
    {
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
