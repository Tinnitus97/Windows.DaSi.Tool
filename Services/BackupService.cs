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

    /// <summary>
    /// Installiert Programme (Firefox, Chrome ...) beim Wiederherstellen ihrer
    /// Profile - ueber denselben winget-Startweg und dieselbe Reparatur wie der
    /// Winget-Import. Ohne: "winget" aus dem Suchpfad.
    /// </summary>
    public WingetService? Winget { get; set; }

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
        // Beim Zurueckschreiben eine eigene Liste - die vom Sichern bleibt stehen.
        var liste = Path.Combine(BackupPath, skips.Zurueck ? RobocopySkips.RestoreListFileName : RobocopySkips.ListFileName);
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
            _log(skips.Zurueck
                ? Loc.Tr($"[WARNUNG] Die Liste der nicht zurückgeschriebenen AppData-Dateien ließ sich nicht schreiben: {ex.Message}",
                         $"[WARNING] The list of AppData files not restored could not be written: {ex.Message}")
                : Loc.Tr($"[WARNUNG] Die Liste der nicht gesicherten AppData-Dateien ließ sich nicht schreiben: {ex.Message}",
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

        RobocopySkips? skips = null;
        // Laeuft das Tool aus dem Zielprofil heraus (meist vom Desktop), wird
        // seine Datei nicht ueberschrieben - sie ist gesperrt, und in der
        // Sicherung liegt meist eine aeltere Fassung.
        var selbst = ProfileExcludes.LaufendesProgrammInSicherung(destDir, backupSourceDir, Environment.ProcessPath);
        if (selbst is not null && File.Exists(selbst))
        {
            args.Add("/XF"); args.Add(Quote(selbst));
            _log(Loc.Tr($"[INFO] Das laufende Windows DaSi Tool wird nicht durch die Fassung aus der Sicherung ersetzt: {Path.GetRelativePath(backupSourceDir, selbst)}",
                        $"[INFO] The running Windows DaSi Tool is not replaced by the copy from the backup: {Path.GetRelativePath(backupSourceDir, selbst)}"));
        }

        if (IncludeAppData)
        {
            _log(Loc.Tr("[INFO] AppData wird mit wiederhergestellt.", "[INFO] AppData is included in the restore."));
            await CloseAppDataProgramsAsync();
            // Gesperrte Dateien in AppData zusammenfassen - robocopy nennt den
            // Pfad in der Sicherung, nicht im Profil.
            skips = new RobocopySkips(Path.Combine(backupSourceDir, "AppData"), zurueck: true);
        }

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress, skips);
        if (_cancel.IsCancelled) return;
        var nurUebersprungen = skips != null && ReportAppDataSkips(skips, code);
        if (code < 8)
            _log(Loc.Tr("[ERFOLG] Benutzerprofil wiederhergestellt.", "[SUCCESS] User profile restored."));
        else if (nurUebersprungen)
            _log(Loc.Tr("[ERFOLG] Benutzerprofil wiederhergestellt - bis auf die oben genannten gesperrten Dateien in AppData.",
                        "[SUCCESS] User profile restored - except for the locked files in AppData listed above."));
        else
            _log(Loc.Tr($"[FEHLER] Robocopy Fehlercode: {code}", $"[ERROR] Robocopy error code: {code}"));

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
        await CloseAppDataProgramsAsync();

        var args = ProfileRestoreArgs(quelle, ziel);
        // Dieselben Ausschluesse wie beim Profil: Zwischenspeicher und der
        // rechnergebundene Zustand von winget und Windows-Shell - auch dann,
        // wenn eine aeltere Sicherung sie noch enthaelt.
        foreach (var ex in ProfileExcludes.Build(Path.Combine(BackupPath, "Benutzerprofil"), includeAppData: true, includeCloud: true))
            if (ProfileExcludes.IsInside(ex, quelle) && Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        var skips = new RobocopySkips(quelle, zurueck: true);
        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress, skips);
        if (_cancel.IsCancelled) return;
        var nurUebersprungen = ReportAppDataSkips(skips, code);
        if (code < 8)
            _log(Loc.Tr("[ERFOLG] AppData wiederhergestellt.", "[SUCCESS] AppData restored."));
        else if (nurUebersprungen)
            _log(Loc.Tr("[ERFOLG] AppData wiederhergestellt - bis auf die oben genannten gesperrten Dateien.",
                        "[SUCCESS] AppData restored - except for the locked files listed above."));
        else
            _log(Loc.Tr($"[FEHLER] AppData: Robocopy Fehlercode {code}", $"[ERROR] AppData: Robocopy error code {code}"));
    }

    /// <summary>
    /// Schliesst vor dem Zurueckschreiben von AppData die Browser und
    /// Mailprogramme aus dem Katalog, soweit sie laufen.
    ///
    /// Edge laeuft nach der Anmeldung meist unsichtbar im Hintergrund weiter
    /// ("Startboost"). Seine Dateien sind dann gesperrt: Ein Teil des Profils
    /// kam aus der Sicherung, der Rest blieb der alte - und jede gesperrte
    /// Datei stand mit drei Zeilen im Protokoll. Genauso wird bei den
    /// einzelnen Browser-Kacheln schon immer verfahren.
    /// </summary>
    private async Task CloseAppDataProgramsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var programme = AppCatalog.Programme.Select(a => (a.Name, a.Prozess))
                                  .Append(("Opera", AppCatalog.Opera.Prozess));
        var geschlossen = new List<(string Name, string Prozess)>();
        foreach (var (name, prozess) in programme)
        {
            bool laeuft;
            try
            {
                var liste = System.Diagnostics.Process.GetProcessesByName(prozess);
                laeuft = liste.Length > 0;
                foreach (var pr in liste) pr.Dispose();
            }
            catch { continue; }
            if (!laeuft) continue;
            geschlossen.Add((name, prozess));
        }
        if (geschlossen.Count == 0) return;
        _log(Loc.Tr($"[INFO] Vor dem Zurückschreiben von AppData geschlossen: {string.Join(", ", geschlossen.Select(g => g.Name))} - sonst wären ihre Dateien gesperrt.",
                    $"[INFO] Closed before restoring AppData: {string.Join(", ", geschlossen.Select(g => g.Name))} - otherwise their files would be locked."));
        await BeendenUndWarten(TimeSpan.FromSeconds(20), geschlossen.Select(g => g.Prozess).ToArray());
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

        if (!await ProgrammSchliessen(app.Name, false, app.Prozess))
            _log(Loc.Tr($"[WARNUNG] {app.Name} läuft noch - gesichert wird trotzdem, einzelne Dateien können aber unvollständig sein.",
                        $"[WARNING] {app.Name} is still running - backing up anyway, but individual files may be incomplete."));

        await MirrorAppProfile(app.Name, quelle, Path.Combine(BackupPath, app.BackupOrdner), app.Chromium ? ChromiumCacheExcludes : null);
        if (app.Punkt == Punkt.Thunderbird && !_cancel.IsCancelled) await ThunderbirdExternSichern(app.Name, quelle);
    }

    /// <summary>Schreibt das Profil eines Programms aus dem Katalog zurueck - vorher wird es bei Bedarf installiert.</summary>
    public async Task RestoreAppProfile(AppEintrag app)
    {
        var frisch = await InstallApp(app.Name, app.Prozess + ".exe", app.WingetId);

        var quelle = Path.Combine(BackupPath, app.BackupOrdner);
        if (!Directory.Exists(quelle)) { _log(Loc.Tr($"[FEHLER] {app.Name} Backup nicht gefunden.", $"[ERROR] {app.Name} backup not found.")); return; }

        if (!await ProgrammSchliessen(app.Name, frisch, app.Prozess))
        {
            _log(Loc.Tr($"[FEHLER] {app.Name} läuft noch und ließ sich nicht beenden - das Profil wird NICHT zurückgeschrieben, sonst überschriebe {app.Name} es beim Beenden wieder. "
                        + $"Bitte {app.Name} vollständig schließen (auch im Infobereich) und den Punkt erneut ausführen.",
                        $"[ERROR] {app.Name} is still running and could not be closed - the profile is NOT restored, {app.Name} would overwrite it again on exit. "
                        + $"Please close {app.Name} completely (including the notification area) and run this item again."));
            return;
        }

        var ziel = AppCatalog.Pfad(SourcePath, app.ProfilRel);
        if (await RestoreAppDir(app.Name, quelle, ziel, app.Prozess) && MozillaProfile.Programm(app) is { } moz)
        {
            MozillaPfadeUmstellen(app.Name, ziel);
            if (app.Punkt == Punkt.Thunderbird) await ThunderbirdExternZurueck(app.Name, ziel);
            MozillaStandardprofil(app.Name, ziel, moz.Exe, moz.Ordner);
        }
    }

    // ------------------------------------------------------------ Firefox/Thunderbird: was ein Profil an den PC bindet

    /// <summary>
    /// Pfade des alten Benutzers in prefs.js (Mailordner, Download-Ordner,
    /// Seiten aus dem Profil) auf das gewaehlte Profil umstellen - wie
    /// MozBackup, das beim Zurueckschreiben die Mailordner in prefs.js anpasst.
    /// </summary>
    private void MozillaPfadeUmstellen(string name, string datenOrdner)
    {
        foreach (var profil in MozillaProfile.Profilordner(datenOrdner))
        {
            try
            {
                var alt = MozillaProfile.AlterBenutzerordner(profil);
                if (alt is null || ProfileExcludes.SameFolder(alt, SourcePath)) continue;
                var n = MozillaProfile.PfadeUmstellen(profil, alt, SourcePath);
                if (n > 0)
                    _log(Loc.Tr($"[INFO] {name}: {n} Einstellungen in {Path.GetFileName(profil)}\\prefs.js zeigten auf den Benutzerordner des alten PCs ({alt}) - umgestellt auf {SourcePath}.",
                                $"[INFO] {name}: {n} settings in {Path.GetFileName(profil)}\\prefs.js pointed to the old PC's user folder ({alt}) - changed to {SourcePath}."));
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[WARNUNG] {name}: Pfade in {Path.GetFileName(profil)}\\prefs.js ließen sich nicht umstellen ({ex.Message}).",
                            $"[WARNING] {name}: paths in {Path.GetFileName(profil)}\\prefs.js could not be changed ({ex.Message})."));
            }
        }
    }

    /// <summary>
    /// Lehnt Firefox/Thunderbird das Profil ab, weil es zuletzt mit einer
    /// neueren Fassung lief? Dann legen sie ein neues, leeres Profil an
    /// ("Sie haben eine aeltere Version gestartet"). Typisch: winget liefert
    /// die neueste Fassung erst einen Tag nach dem Update auf dem alten PC,
    /// oder hier liegt ESR statt der normalen Fassung. Bei gleicher
    /// Hauptversion ist das harmlos - die Datenformate aendern sich nur mit ihr -,
    /// dann wird die Pruefung zurueckgesetzt. Bei neuerer Hauptversion nicht:
    /// Firefox legte sonst z. B. Lesezeichen neu an.
    /// </summary>
    private void MozillaFassungPruefen(string name, string profilOrdner, IEnumerable<string> installationen)
    {
        if (MozillaProfile.ProfilFassung(profilOrdner) is not { } profil) return;
        var hier = installationen.Select(i => (Ordner: i, Fassung: MozillaProfile.InstallierteFassung(i)))
                                 .Where(i => i.Fassung is not null).Select(i => (i.Ordner, Fassung: i.Fassung!.Value)).ToList();
        if (hier.Count == 0) return;
        var abstaende = hier.Select(i => MozillaProfile.Vergleichen(profil, i.Fassung)).ToList();
        if (abstaende.Contains(MozillaProfile.Abstand.Passt)) return;
        var fassungen = string.Join(", ", hier.Select(i => i.Fassung.Version));
        if (abstaende.Contains(MozillaProfile.Abstand.Neuer))
        {
            try
            {
                File.Delete(Path.Combine(profilOrdner, "compatibility.ini"));
                _log(Loc.Tr($"[INFO] {name}: Das Profil lief zuletzt mit {profil.Version}, hier ist {fassungen} installiert - etwas älter. {name} hätte es deshalb abgelehnt und ein neues, leeres angelegt; die Versionsprüfung ist zurückgesetzt.",
                            $"[INFO] {name}: the profile last ran with {profil.Version}, {fassungen} is installed here - slightly older. {name} would have refused it and created a new, empty one; the version check has been reset."));
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[FEHLER] {name}: Das Profil lief zuletzt mit {profil.Version}, hier ist {fassungen} installiert - {name} lehnt es ab. Die Versionsprüfung ließ sich nicht zurücksetzen ({ex.Message}). Bitte {name} aktualisieren.",
                            $"[ERROR] {name}: the profile last ran with {profil.Version}, {fassungen} is installed here - {name} refuses it. The version check could not be reset ({ex.Message}). Please update {name}."));
            }
            return;
        }
        _log(Loc.Tr($"[FEHLER] {name}: Das Profil lief zuletzt mit {profil.Version}, hier ist erst {fassungen} installiert. {name} lehnt ein Profil einer neueren Hauptversion ab und legt ein neues, leeres an. "
                    + $"Bitte {name} aktualisieren (Hilfe > Über {name}, oder winget upgrade) und den Punkt erneut ausführen.",
                    $"[ERROR] {name}: the profile last ran with {profil.Version}, only {fassungen} is installed here. {name} refuses a profile of a newer major version and creates a new, empty one. "
                    + $"Please update {name} (Help > About {name}, or winget upgrade) and run this item again."));
    }

    /// <summary>Thunderbird-Konten, deren Mails ausserhalb des Profils liegen: in der Sicherung und beim Zurueckschreiben.</summary>
    public const string ThunderbirdExternOrdner = "Thunderbird-Extern";

    private sealed class ExterneSicherung
    {
        public string Benutzerordner { get; set; } = "";
        public List<ExternesPostfach> Postfaecher { get; set; } = new();
    }

    private sealed class ExternesPostfach
    {
        /// <summary>Profilordner relativ zum Datenordner ("Profiles\\abcd.default").</summary>
        public string Profil { get; set; } = "";
        public string Server { get; set; } = "";
        public string Ordner { get; set; } = "";
        /// <summary>Unterordner in Thunderbird-Extern.</summary>
        public string Sicherung { get; set; } = "";
    }

    /// <summary>
    /// Sichert die Mails von Konten, deren Ordner Thunderbird ausserhalb des
    /// Profils fuehrt (Konten-Einstellungen > Server-Einstellungen > Lokaler
    /// Ordner, etwa D:\\Mail). Bisher fehlten sie in der Sicherung - MozBackup
    /// sichert sie mit. Liegen sie im Benutzerprofil, sichert sie schon der
    /// Punkt Benutzerprofil.
    /// </summary>
    private async Task ThunderbirdExternSichern(string name, string datenOrdner)
    {
        var liste = new ExterneSicherung { Benutzerordner = SourcePath };
        var ablage = Path.Combine(BackupPath, ThunderbirdExternOrdner);
        foreach (var profil in MozillaProfile.Profilordner(datenOrdner))
        {
            foreach (var p in MozillaProfile.PostfaecherAusserhalb(profil))
            {
                if (_cancel.IsCancelled) return;
                if (ProfileExcludes.IsInside(p.Ordner, SourcePath))
                {
                    _log(Loc.Tr($"[INFO] {name}: Die Mails von {p.Server} liegen außerhalb des Thunderbird-Profils, aber im Benutzerprofil ({p.Ordner}) - sie kommen mit dem Punkt \"Windows Benutzerprofil\" in die Sicherung.",
                                $"[INFO] {name}: the mail of {p.Server} is outside the Thunderbird profile but inside the user profile ({p.Ordner}) - it is backed up with \"Windows user profile\"."));
                    continue;
                }
                if (!Directory.Exists(p.Ordner))
                {
                    _log(Loc.Tr($"[WARNUNG] {name}: Der Mailordner von {p.Server} fehlt: {p.Ordner}",
                                $"[WARNING] {name}: the mail folder of {p.Server} is missing: {p.Ordner}"));
                    continue;
                }
                var unter = $"{liste.Postfaecher.Count + 1}_{p.Server}";
                var args = new List<string> { Quote(p.Ordner), Quote(Path.Combine(ablage, unter)), "/MIR", "/R:1", "/W:1", "/MT:32" };
                if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
                var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
                if (code >= 8)
                {
                    _log(Loc.Tr($"[FEHLER] {name}: Die Mails von {p.Server} ({p.Ordner}) ließen sich nicht sichern: ExitCode {code}",
                                $"[ERROR] {name}: the mail of {p.Server} ({p.Ordner}) could not be backed up: ExitCode {code}"));
                    continue;
                }
                liste.Postfaecher.Add(new ExternesPostfach { Profil = Path.GetRelativePath(datenOrdner, profil), Server = p.Server, Ordner = p.Ordner, Sicherung = unter });
                _log(Loc.Tr($"[ERFOLG] {name}: Mails von {p.Server} außerhalb des Profils gesichert ({p.Ordner}).",
                            $"[SUCCESS] {name}: mail of {p.Server} outside the profile backed up ({p.Ordner})."));
            }
        }
        if (liste.Postfaecher.Count > 0)
        {
            Directory.CreateDirectory(ablage);
            File.WriteAllText(Path.Combine(ablage, "Postfaecher.json"), System.Text.Json.JsonSerializer.Serialize(liste, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Schreibt die Mails von Konten ausserhalb des Profils zurueck: an ihren
    /// alten Ort (im Benutzerordner des alten PCs: in den des gewaehlten
    /// Profils), oder - gibt es das Laufwerk hier nicht - in den Profilordner;
    /// prefs.js wird dann angepasst.
    /// </summary>
    private async Task ThunderbirdExternZurueck(string name, string datenOrdner)
    {
        var datei = Path.Combine(BackupPath, ThunderbirdExternOrdner, "Postfaecher.json");
        if (!File.Exists(datei)) return;
        ExterneSicherung? liste;
        try { liste = System.Text.Json.JsonSerializer.Deserialize<ExterneSicherung>(File.ReadAllText(datei)); }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] {name}: Die Liste der Mailordner außerhalb des Profils ist unlesbar ({ex.Message}).",
                        $"[ERROR] {name}: the list of mail folders outside the profile is unreadable ({ex.Message})."));
            return;
        }
        foreach (var p in liste?.Postfaecher ?? new List<ExternesPostfach>())
        {
            if (_cancel.IsCancelled) return;
            var profil = Path.Combine(datenOrdner, p.Profil);
            var ziel = p.Ordner;
            var root = Path.GetPathRoot(ziel);
            bool verlegt = string.IsNullOrEmpty(root) || !Directory.Exists(root);
            if (verlegt) ziel = Path.Combine(profil, "Mail", "Extern-" + p.Server);
            var args = new List<string> { Quote(Path.Combine(BackupPath, ThunderbirdExternOrdner, p.Sicherung)), Quote(ziel), "/E", "/R:1", "/W:1", "/MT:32" };
            if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
            var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
            if (code >= 8)
            {
                _log(Loc.Tr($"[FEHLER] {name}: Die Mails von {p.Server} ließen sich nicht nach {ziel} zurückschreiben: ExitCode {code}",
                            $"[ERROR] {name}: the mail of {p.Server} could not be restored to {ziel}: ExitCode {code}"));
                continue;
            }
            if (verlegt && !MozillaProfile.PostfachVerlegen(profil, p.Server, ziel))
            {
                _log(Loc.Tr($"[FEHLER] {name}: Die Mails von {p.Server} liegen jetzt unter {ziel}, prefs.js ließ sich aber nicht anpassen.",
                            $"[ERROR] {name}: the mail of {p.Server} is now at {ziel}, but prefs.js could not be updated."));
                continue;
            }
            _log(verlegt
                ? Loc.Tr($"[ERFOLG] {name}: Mails von {p.Server} zurückgeschrieben - das Laufwerk von {p.Ordner} gibt es hier nicht, sie liegen jetzt im Profil ({ziel}).",
                         $"[SUCCESS] {name}: mail of {p.Server} restored - the drive of {p.Ordner} does not exist here, it is now inside the profile ({ziel}).")
                : Loc.Tr($"[ERFOLG] {name}: Mails von {p.Server} an ihren Ort außerhalb des Profils zurückgeschrieben ({ziel}).",
                         $"[SUCCESS] {name}: mail of {p.Server} restored to its place outside the profile ({ziel})."));
        }
    }

    /// <summary>
    /// Firefox/Thunderbird: das zurueckgeschriebene Profil als Standard dieser
    /// Installation eintragen. Sonst nimmt ein Firefox, der hier in einem
    /// anderen Ordner liegt als auf dem alten PC (64 statt 32 Bit, fuer alle
    /// statt nur fuer den Benutzer), ein neues, leeres Profil.
    /// </summary>
    private void MozillaStandardprofil(string name, string datenOrdner, string exe, string ordnerName)
    {
        var installationen = TestInstallationen
            ?? (OperatingSystem.IsWindows() ? MozillaProfile.Installationen(exe, ordnerName, SourcePath) : new List<string>());
        MozillaProfile.Ergebnis erg;
        try { erg = MozillaProfile.Anpassen(datenOrdner, installationen); }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] {name}: Das Profil ließ sich nicht als Standard eintragen ({ex.Message}). {name} legt beim ersten Start womöglich ein neues, leeres Profil an - dann mit \"{exe} -P\" das zurückgeschriebene wählen.",
                        $"[ERROR] {name}: the profile could not be set as default ({ex.Message}). {name} may create a new, empty profile on first start - then pick the restored one with \"{exe} -P\"."));
            return;
        }
        if (erg.Profil is null)
        {
            _log(Loc.Tr($"[WARNUNG] {name}: In der Sicherung steht kein Profil in profiles.ini - {name} wählt selbst.",
                        $"[WARNING] {name}: the backup lists no profile in profiles.ini - {name} chooses by itself."));
            return;
        }

        var profilName = string.IsNullOrEmpty(erg.Name) ? erg.Profil : $"\"{erg.Name}\" ({erg.Profil})";
        var alt = erg.AlteSchluessel.Select(k => MozillaProfile.BekannterOrt(k, ordnerName, SourcePath) ?? k).ToList();
        if (erg.Neu.Count == 0)
        {
            _log(Loc.Tr($"[WARNUNG] {name} ist auf diesem PC nicht installiert. Das Profil {profilName} ist als Standard eingetragen - {name} übernimmt es beim ersten Start nach der Installation.",
                        $"[WARNING] {name} is not installed on this PC. The profile {profilName} is set as default - {name} takes it over on the first start after installation."));
            return;
        }
        var hier = string.Join(", ", erg.Neu.Select(n => n.Ordner));
        if (erg.SchonPassend)
            _log(Loc.Tr($"[INFO] {name}: Profil {profilName} ist Standard für {hier} - wie auf dem alten PC.",
                        $"[INFO] {name}: profile {profilName} is the default for {hier} - as on the old PC."));
        else
            _log(Loc.Tr($"[INFO] {name}: Profil {profilName} ist jetzt Standard für {hier}. Auf dem alten PC lief {name} aus {(alt.Count > 0 ? string.Join(", ", alt) : "einem unbekannten Ordner")} - ohne diese Anpassung legte {name} beim ersten Start ein neues, leeres Profil an.",
                        $"[INFO] {name}: profile {profilName} is now the default for {hier}. On the old PC {name} ran from {(alt.Count > 0 ? string.Join(", ", alt) : "an unknown folder")} - without this adjustment {name} would have created a new, empty profile on first start."));
        if (erg.Verzeichnis is not null) MozillaFassungPruefen(name, erg.Verzeichnis, erg.Neu.Select(n => n.Ordner));
    }

    /// <summary>Nur fuer die Pruefung: Installationsordner statt der echten Suche.</summary>
    internal List<string>? TestInstallationen { get; set; }

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
        if (!await ProgrammSchliessen("Opera", false, OperaProzesse))
            _log(Loc.Tr("[WARNUNG] Opera läuft noch - gesichert wird trotzdem, einzelne Dateien können aber unvollständig sein.",
                        "[WARNING] Opera is still running - backing up anyway, but individual files may be incomplete."));

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
            var frisch = await InstallOpera(v);
            if (!await ProgrammSchliessen(name, frisch, OperaProzesse))
            {
                _log(Loc.Tr($"[FEHLER] {name} läuft noch und ließ sich nicht beenden - das Profil wird NICHT zurückgeschrieben. Bitte Opera vollständig schließen und den Punkt erneut ausführen.",
                            $"[ERROR] {name} is still running and could not be closed - the profile is NOT restored. Please close Opera completely and run this item again."));
                continue;
            }
            await RestoreAppDir(name, Path.Combine(quelle, v), Path.Combine(ziel, v), OperaProzesse);
        }
    }

    /// <summary>Opera selbst und seine Begleiter, die Dateien im Profil offen halten.</summary>
    private static readonly string[] OperaProzesse = { AppCatalog.Opera.Prozess, "opera_autoupdate", "browser_assistant" };

    /// <summary>
    /// Ist diese Opera-Fassung installiert? Opera installiert sich meist nur
    /// fuer den Benutzer (AppData\Local\Programs\&lt;Fassung&gt;), seltener fuer alle
    /// (Programme). Sonst per winget - soweit das Paket bekannt ist.
    /// </summary>
    /// <returns>true, wenn die Fassung gerade eben installiert wurde.</returns>
    private async Task<bool> InstallOpera(string variante)
    {
        if (_cancel.IsCancelled) return false;
        var name = AppCatalog.Opera.Anzeigename(variante);
        var orte = new[]
        {
            Path.Combine(SourcePath, "AppData", "Local", "Programs", name, "opera.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name, "opera.exe"),
        };
        if (orte.Any(File.Exists))
        {
            _log(Loc.Tr($"[INFO] {name} ist bereits installiert.", $"[INFO] {name} is already installed."));
            return false;
        }
        var id = AppCatalog.Opera.WingetId(variante);
        if (id is null)
        {
            _log(Loc.Tr($"[WARNUNG] {name} ist nicht installiert und lässt sich nicht automatisch nachinstallieren. Bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] {name} is not installed and cannot be installed automatically. Please install it manually - the profile is restored anyway."));
            return false;
        }
        return await InstallViaWinget(name, id);
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
    /// <param name="prozesse">
    /// Die Prozesse des Programms. Laeuft eins davon nach dem Kopieren wieder,
    /// wird es beendet und noch einmal kopiert - sonst ueberschriebe es beim
    /// Beenden das zurueckgeschriebene Profil.
    /// </param>
    /// <returns>true, wenn das Profil vollstaendig im Ziel steht.</returns>
    private async Task<bool> RestoreAppDir(string name, string quelle, string ziel, params string[] prozesse)
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
                return false;
            }
        }
        var parent = Path.GetDirectoryName(ziel);
        if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

        var args = new List<string> { Quote(quelle), Quote(ziel), "/MIR", "/R:1", "/W:1", "/MT:32" };
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });

        var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
        if (_cancel.IsCancelled) return false;

        // Hat sich das Programm waehrend des Kopierens gestartet (Installer,
        // Autostart, Benutzer), haelt es ein eigenes, frisches Profil offen.
        if (prozesse.Length > 0 && OperatingSystem.IsWindows() && Laeuft(prozesse))
        {
            _log(Loc.Tr($"[WARNUNG] {name} wurde während des Kopierens gestartet - wird beendet, und das Profil wird noch einmal zurückgeschrieben.",
                        $"[WARNING] {name} was started while copying - it is closed and the profile is restored once more."));
            if (await BeendenUndWarten(TimeSpan.FromSeconds(20), prozesse))
            {
                code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
                if (_cancel.IsCancelled) return false;
            }
        }

        if (code >= 8)
        {
            _log(Loc.Tr($"[FEHLER] {name}: ExitCode {code}", $"[ERROR] {name}: ExitCode {code}"));
            return false;
        }

        // Stehen die Dateien, an denen das Profil haengt, so da wie gesichert?
        var abweichend = ProfilAbgleich.Abweichungen(quelle, ziel);
        if (abweichend.Count > 0)
        {
            _log(Loc.Tr($"[FEHLER] {name}: Das Profil steht nicht vollständig im Ziel - abweichend: {string.Join(", ", abweichend.Take(8))}{(abweichend.Count > 8 ? ", ..." : "")}. "
                        + $"Meist lief {name} dabei. Bitte {name} vollständig schließen (auch im Infobereich) und den Punkt erneut ausführen.",
                        $"[ERROR] {name}: the profile is not complete in the target - differing: {string.Join(", ", abweichend.Take(8))}{(abweichend.Count > 8 ? ", ..." : "")}. "
                        + $"Usually {name} was running meanwhile. Please close {name} completely (including the notification area) and run this item again."));
            return false;
        }

        var profile = ProfilAbgleich.Profile(ziel);
        _log(profile.Count > 0
            ? Loc.Tr($"[ERFOLG] {name} Profil wiederhergestellt ({string.Join(", ", profile)}).", $"[SUCCESS] {name} profile restored ({string.Join(", ", profile)}).")
            : Loc.Tr($"[ERFOLG] {name} Profil wiederhergestellt.", $"[SUCCESS] {name} profile restored."));
        return true;
    }

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

    /// <summary>
    /// Der Fehler, den PrintBrm in seiner Ausgabe nennt - null, wenn keiner.
    ///
    /// PrintBrm endet auch dann mit Exit-Code 0, wenn es scheitert, etwa an
    /// einer unbrauchbaren Sicherungsdatei: "The following error occurred:
    /// 0x80070057. / The parameter is incorrect." (unter Windows geprueft).
    /// Erkennbar ist das am Fehlercode (HRESULT 0x8...) - in jeder Sprache.
    /// </summary>
    internal static string? PrintBrmFehler(string? ausgabe)
    {
        if (string.IsNullOrWhiteSpace(ausgabe)) return null;
        var zeilen = ausgabe.Split('\n').Select(z => z.Trim()).Where(z => z.Length > 0).ToList();
        var i = zeilen.FindIndex(z => System.Text.RegularExpressions.Regex.IsMatch(z, @"\b0x8[0-9A-Fa-f]{7}\b"));
        // Die Zeile mit dem Code und die Erklaerung dahinter.
        return i < 0 ? null : string.Join(" ", zeilen.Skip(i).Take(2));
    }

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

        // PrintBrm endet auch bei manchem Fehlschlag mit 0 - etwa bei einer
        // unbrauchbaren Sicherungsdatei. Frueher stand dann "[ERFOLG]" da.
        var fehler = PrintBrmFehler(r.Output);
        if (r.ExitCode == 0 && fehler is null)
        {
            _log(Loc.Tr("[ERFOLG] Drucker wiederhergestellt.", "[SUCCESS] Printers restored."));
            _log(Loc.Tr("[INFO] Hinweis: Netzwerkdrucker sind nur erreichbar, wenn der Druckserver verfügbar ist.",
                        "[INFO] Note: network printers are only reachable if the print server is available."));
        }
        else
        {
            _log(Loc.Tr($"[FEHLER] Druckerwiederherstellung fehlgeschlagen (ExitCode: {r.ExitCode}{(fehler is null ? "" : ", " + fehler)}).",
                        $"[ERROR] Printer restore failed (ExitCode: {r.ExitCode}{(fehler is null ? "" : ", " + fehler)})."));
            if (!string.IsNullOrWhiteSpace(r.Output))
                _log(Loc.Tr("[INFO] PrintBrm-Ausgabe: ", "[INFO] PrintBrm output: ") + r.Output.Trim());
        }
    }

    // ------------------------------------------------------------ Hintergrundbild

    // Merkt sich neben dem Bild auch dessen Originalpfad ...
    private const string WallpaperPathFile = "Hintergrundbild-Pfad.txt";
    // ... und das gesicherte Profil, damit sich der Pfad beim Zurueckschreiben
    // in ein ANDERES Profil uebertragen laesst (Besitzer -> Anwender).
    // Eigene Datei, damit aeltere Fassungen die Pfad-Datei weiter lesen koennen.
    private const string WallpaperProfileFile = "Hintergrundbild-Profil.txt";

    /// <summary>
    /// Sichert den Hintergrund in den Ordner "Design": ein Bild (die Datei
    /// daneben) oder eine Volltonfarbe. null, wenn es nichts Festes zu sichern
    /// gibt (Diashow, Blickpunkt, Bild verschwunden) - das steht dann im
    /// Protokoll.
    /// </summary>
    private async Task<HintergrundEinstellung?> HintergrundSichern(string ordner, DesignZiel ziel)
    {
        var stand = DesktopHintergrund.Lesen(ziel.Wurzel);
        var src = stand.Bild;
        string? farbeText = stand.Farbe, stil = stand.Stil, kacheln = stand.Kacheln;
        var art = Hintergrund.Bestimmen(src, src is not null && File.Exists(src), stand.Typ, farbeText);

        // Das eingetragene Bild ist geloescht, Windows zeigt es aber weiter -
        // aus seinem Zwischenspeicher. Dann eben das sichern.
        if (art == HintergrundArt.Unbekannt && src is not null && !File.Exists(src)
            && Hintergrund.Zwischenspeicher(SourcePath) is { } zwischen && File.Exists(zwischen))
        {
            _log(Loc.Tr($"[INFO] Das eingetragene Hintergrundbild gibt es nicht mehr ({src}) - gesichert wird das Bild, das Windows gerade zeigt.",
                        $"[INFO] The configured wallpaper no longer exists ({src}) - the image Windows currently shows is backed up."));
            src = zwischen;
            art = HintergrundArt.Bild;
        }

        // Volltonfarbe: Es gibt kein Bild - gesichert wird die Farbe.
        if (art == HintergrundArt.Volltonfarbe && Hintergrund.FarbeLesen(farbeText) is { } farbe)
        {
            _log(Loc.Tr($"[ERFOLG] Volltonfarbe als Hintergrund gesichert: RGB {farbe.R}, {farbe.G}, {farbe.B} ({Hintergrund.Hex(farbe)}).",
                        $"[SUCCESS] Solid color background backed up: RGB {farbe.R}, {farbe.G}, {farbe.B} ({Hintergrund.Hex(farbe)})."));
            return new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = Hintergrund.FarbeText(farbe) };
        }
        if (art == HintergrundArt.Wechselnd)
        {
            _log(Loc.Tr("[WARNUNG] Als Hintergrund läuft eine Diashow oder Windows-Blickpunkt - wechselnde Bilder, es gibt kein festes Bild zu sichern.",
                        "[WARNING] The background is a slideshow or Windows Spotlight - changing images, there is no fixed image to back up."));
            return null;
        }
        if (art != HintergrundArt.Bild || src is null)
        {
            _log(Loc.Tr(src is null
                            ? "[WARNUNG] Es konnte kein aktuelles Hintergrundbild und keine Hintergrundfarbe gefunden werden."
                            : $"[WARNUNG] Das eingestellte Hintergrundbild gibt es nicht mehr: {src}",
                        src is null
                            ? "[WARNING] Neither a current wallpaper nor a background color could be found."
                            : $"[WARNING] The configured wallpaper no longer exists: {src}"));
            return null;
        }

        // Bild unter seinem Originalnamen neben Design.json - Windows'
        // Zwischenspeicher ("TranscodedWallpaper", ohne Endung) als
        // "Hintergrundbild.jpg".
        var fileName = Hintergrund.SicherungsName(src, KopfLesen(src));
        if (Hintergrund.IstWindowsZwischenspeicher(src))
            _log(Loc.Tr($"[INFO] Der Hintergrund liegt nur in Windows' Zwischenspeicher ({src}) - gesichert als {fileName}.",
                        $"[INFO] The wallpaper only exists in Windows' cache ({src}) - backed up as {fileName}."));
        if (string.Equals(fileName, DesignEinstellungen.DateiName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, DesignEinstellungen.AlterDateiName, StringComparison.OrdinalIgnoreCase))
            fileName = "Hintergrund-" + fileName;
        try
        {
            Directory.CreateDirectory(ordner);
            await Task.Run(() => File.Copy(src, Path.Combine(ordner, fileName), overwrite: true));
            _log(Loc.Tr($"[ERFOLG] Hintergrundbild gesichert. Originalpfad: {src}",
                        $"[SUCCESS] Wallpaper backed up. Original path: {src}"));
            // Darstellung und Farbe dazu: Bei "Anpassen" oder "Zentriert" sieht
            // man die Farbe am Rand.
            return new HintergrundEinstellung
            {
                Art = "Bild",
                Bild = fileName,
                BildPfad = src,
                Profil = SourcePath,
                Farbe = Hintergrund.FarbeLesen(farbeText) is { } rand ? Hintergrund.FarbeText(rand) : null,
                Darstellung = stil,
                Kacheln = kacheln,
            };
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht gesichert werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be backed up: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Hintergrund aus einer Sicherung im alten Format lesen: Ordner
    /// "Hintergrundbild" mit dem Bild, Hintergrundbild-Pfad.txt,
    /// Hintergrundbild-Profil.txt und ggf. Hintergrund.json (Volltonfarbe,
    /// Darstellung). Gesetzt wird er wie beim neuen Format - ueber das
    /// Windows-Design.
    /// </summary>
    /// <returns>Die Einstellung (Art, Farbe, Darstellung, Originalpfad, Profil) und das gesicherte Bild - (null, null), wenn nichts Brauchbares da ist.</returns>
    private async Task<(HintergrundEinstellung? Einstellung, string? Bild)> AlteSicherungLesen()
    {
        var srcDir = Path.Combine(BackupPath, "Hintergrundbild");
        var pathFile = Path.Combine(srcDir, WallpaperPathFile);

        if (!Directory.Exists(srcDir))
        {
            _log(Loc.Tr("[FEHLER] Keine Hintergrundbild-Sicherung gefunden.",
                        "[ERROR] No wallpaper backup found."));
            return (null, null);
        }

        // Volltonfarbe statt Bild?
        var einstellung = Hintergrund.Laden(srcDir);
        if (Hintergrund.IstVolltonfarbe(einstellung)) return (einstellung, null);

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
                            && !f.EndsWith(WallpaperProfileFile, StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(HintergrundEinstellung.DateiName, StringComparison.OrdinalIgnoreCase))
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
            return (null, null);
        }

        var e = einstellung ?? new HintergrundEinstellung();
        e.Art = "Bild";
        e.BildPfad = originalPath;
        e.Profil = originalProfile;
        return (e, backupImage);
    }

    /// <summary>Als Hintergrund setzen - Registry, Sitzung, Design-Datei - samt Darstellung und Randfarbe.</summary>
    private void BildDirektSetzen(DesignZiel ziel, string pfad, HintergrundEinstellung? einstellung)
    {
        try
        {
            var r = DesktopHintergrund.BildSetzen(ziel.Wurzel, ziel.EigenerBenutzer, pfad, einstellung, SourcePath);
            HintergrundErgebnis(ziel, r, Loc.Tr("Hintergrundbild", "Wallpaper"));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht gesetzt werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be set: {ex.Message}"));
        }
    }

    /// <summary>
    /// Legt das Bild aus der Sicherung ins gewaehlte Profil. Liefert den Pfad -
    /// null, wenn das Kopieren scheiterte (steht dann im Protokoll).
    /// </summary>
    private async Task<string?> HintergrundbildBereitstellen(string backupImage, string? originalPath, string? originalProfile)
    {
        // Zielpfad bestimmen - IMMER im gewaehlten Zielprofil.
        //
        // Frueher wurde der gespeicherte Originalpfad unveraendert benutzt. Auf
        // einem Rechner mit anderem Benutzernamen (gesichert: Besitzer,
        // zurueck zu: Anwender) entstand so ein Ordner "C:\Users\Besitzer" -
        // ein Profil, das es dort nicht gibt. Und ohne gespeicherten Pfad ging
        // das Bild in "Bilder" dessen, der das Tool ausfuehrt, nicht des
        // Zielprofils.
        //
        // Lag es in Windows' Zwischenspeicher (Themes\TranscodedWallpaper,
        // CachedFiles): NICHT dorthin zurueck. Die Dateien verwaltet Windows
        // selbst - als Hintergrund eingetragen, ueberschreibt bzw. loescht es
        // sie, und nach dem naechsten Neustart ist der Desktop schwarz.
        string targetPath;
        bool zwischenspeicher = Hintergrund.IstWindowsZwischenspeicher(originalPath);
        var imZielprofil = zwischenspeicher ? null : ProfilePaths.MapIntoProfile(originalPath, originalProfile, SourcePath);
        if (imZielprofil is not null)
        {
            targetPath = imZielprofil;
        }
        else if (!zwischenspeicher && !string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath)
                 && FilesAreEqual(backupImage, originalPath))
        {
            // Ein Windows-Hintergrund ausserhalb jedes Profils (C:\Windows\Web\...),
            // der hier schon genau so liegt: nur setzen, nichts kopieren.
            targetPath = originalPath;
        }
        else
        {
            targetPath = Path.Combine(SourcePath, "Pictures", Hintergrund.SicherungsName(backupImage, KopfLesen(backupImage)));
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
            return targetPath;
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht wiederhergestellt werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be restored: {ex.Message}"));
            return null;
        }
    }


    /// <summary>
    /// Setzt eine Volltonfarbe als Hintergrund - wie unter Einstellungen >
    /// Personalisierung > Hintergrund > Volltonfarbe: Bild entfernen, Farbe
    /// fuer die laufende Sitzung und dauerhaft beim angemeldeten Benutzer.
    /// </summary>
    private void VolltonfarbeSetzen(DesignZiel ziel, (byte R, byte G, byte B) farbe)
    {
        try
        {
            var r = DesktopHintergrund.FarbeSetzen(ziel.Wurzel, ziel.EigenerBenutzer, farbe, SourcePath);
            HintergrundErgebnis(ziel, r, Loc.Tr($"Volltonfarbe RGB {farbe.R}, {farbe.G}, {farbe.B} ({Hintergrund.Hex(farbe)})",
                                                $"Solid color RGB {farbe.R}, {farbe.G}, {farbe.B} ({Hintergrund.Hex(farbe)})"));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Volltonfarbe konnte nicht gesetzt werden: {ex.Message}",
                        $"[ERROR] The solid color could not be set: {ex.Message}"));
        }
    }

    /// <summary>Was beim Setzen des Hintergrunds herauskam - ins Protokoll.</summary>
    private void HintergrundErgebnis(DesignZiel ziel, HintergrundGesetzt r, string was)
    {
        if (r.VirtuelleDesktops > 0)
            _log(Loc.Tr($"[INFO] {r.VirtuelleDesktops} virtuelle(r) Desktop(s) hatte(n) einen eigenen Hintergrund - angeglichen.",
                        $"[INFO] {r.VirtuelleDesktops} virtual desktop(s) had their own background - adjusted."));
        DesignDateiProtokoll(r.DesignDatei);
        _log(r.ExplorerFehler is null
            ? Loc.Tr("[INFO] Gesetzt über den Explorer (IDesktopWallpaper), wie die Einstellungen-App.", "[INFO] Set via Explorer (IDesktopWallpaper), like the Settings app.")
            : Loc.Tr($"[INFO] Gesetzt über SystemParametersInfo - der Weg über den Explorer ging nicht: {r.ExplorerFehler}",
                     $"[INFO] Set via SystemParametersInfo - the Explorer route failed: {r.ExplorerFehler}"));
        var fuer = ziel.EigenerBenutzer || ziel.Name is null ? "" : Loc.Tr($" (eingetragen für {ziel.Name})", $" (set for {ziel.Name})");
        if (r.Eingetragen && r.Angezeigt)
            _log(Loc.Tr($"[ERFOLG] Hintergrund gesetzt: {was}{fuer}.", $"[SUCCESS] Background set: {was}{fuer}."));
        else if (r.Eingetragen)
            _log(Loc.Tr($"[WARNUNG] Hintergrund eingetragen: {was}{fuer} - Windows zeigt ihn erst nach der nächsten Anmeldung.",
                        $"[WARNING] Background configured: {was}{fuer} - Windows only shows it after the next sign-in."));
        else
            _log(Loc.Tr($"[FEHLER] Der Hintergrund ließ sich nicht dauerhaft eintragen ({was}) - nach einem Neustart wäre er wieder weg.",
                        $"[ERROR] The background could not be saved permanently ({was}) - it would be gone after a restart."));
    }

    /// <summary>Was mit der Design-Datei von Windows geschah - ins Protokoll.</summary>
    private void DesignDateiProtokoll(DesignDateiErgebnis? d)
    {
        if (d is null) return;
        var name = d.Datei is null ? "" : Path.GetFileName(d.Datei);
        if (d.Geaendert)
            _log(Loc.Tr($"[INFO] Auch in der Design-Datei von Windows eingetragen ({name}) - sonst setzt Windows beim nächsten Anmelden den alten Stand zurück.",
                        $"[INFO] Also written to the Windows theme file ({name}) - otherwise Windows restores the old state at the next sign-in."));
        else if (d.Grund is not null)
            _log(Loc.Tr($"[INFO] Design-Datei von Windows nicht angepasst ({(d.Datei ?? "-")}): {d.Grund}.",
                        $"[INFO] Windows theme file not adjusted ({(d.Datei ?? "-")}): {d.Grund}."));
    }

    /// <summary>Die ersten Bytes einer Datei - fuer die Bildart. Leer, wenn sie sich nicht lesen laesst.</summary>
    private static byte[] KopfLesen(string pfad)
    {
        try
        {
            using var f = File.OpenRead(pfad);
            var kopf = new byte[16];
            int n = f.Read(kopf, 0, kopf.Length);
            return kopf[..n];
        }
        catch { return Array.Empty<byte>(); }
    }

    // ------------------------------------------------------------ Design

    /// <summary>
    /// "Design sichern": der Hintergrund (Bild oder Volltonfarbe) und die
    /// Design-Einstellungen von Windows - Hell/Dunkel, Akzentfarbe,
    /// Transparenz, Mauszeiger, Taskleiste, Start. Alles in EINEM Ordner
    /// "Design": Design.json und, bei einem Bild, die Bilddatei daneben.
    /// </summary>
    public async Task ExportDesign()
    {
        var ordner = Path.Combine(BackupPath, DesignEinstellungen.Ordner);
        using var ziel = DesignZweig(schreiben: false);
        if (ziel is null) return;
        var hintergrund = await HintergrundSichern(ordner, ziel);
        if (_cancel.IsCancelled) return;
        var werte = DesignEinstellungenLesen(ziel);
        if (hintergrund is null && (werte is null || werte.Count == 0)) return;
        try
        {
            DesignEinstellungen.Speichern(BackupPath, new DesignSicherung
            {
                Rechner = Environment.MachineName,
                Erstellt = DateTime.Now,
                Hintergrund = hintergrund,
                Werte = werte ?? new List<DesignEintrag>(),
            });
            DesignOrdnerAufraeumen(ordner, hintergrund?.Bild);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] {DesignEinstellungen.DateiName} konnte nicht geschrieben werden: {ex.Message}",
                        $"[ERROR] {DesignEinstellungen.DateiName} could not be written: {ex.Message}"));
        }
    }

    /// <summary>
    /// Wird in einen vorhandenen Sicherungsordner erneut gesichert, laege
    /// sonst ein frueheres Bild neben dem neuen - und die Datei der
    /// Zwischenfassung neben Design.json.
    /// </summary>
    private static void DesignOrdnerAufraeumen(string ordner, string? bild)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ordner))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, DesignEinstellungen.DateiName, StringComparison.OrdinalIgnoreCase)) continue;
                if (bild is not null && string.Equals(name, bild, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(f);
            }
        }
        catch { /* bleibt eben liegen - gelesen wird nur, was Design.json nennt */ }
    }

    /// <summary>
    /// "Design wiederherstellen": erst die Einstellungen (Hell/Dunkel,
    /// Akzentfarbe ...), dann der Hintergrund - ist "Akzentfarbe automatisch"
    /// an, leitet Windows sie danach aus dem Hintergrund ab, wie auf dem alten
    /// Rechner. Sicherungen im alten Format (Ordner "Hintergrundbild") werden
    /// weiter gelesen.
    /// </summary>
    public async Task ImportDesign()
    {
        var design = DesignEinstellungen.Laden(BackupPath);
        using var ziel = DesignZweig(schreiben: true);
        if (ziel is null) return;
        if (_cancel.IsCancelled) return;

        // Legt eine Richtlinie den Hintergrund fest, gewinnt sie - vorher sagen.
        if (OperatingSystem.IsWindows())
            foreach (var e in DesktopHintergrund.Einfluesse(ziel.Wurzel, mitWechsel: false, Loc.Tr))
                _log("[WARNUNG] " + e + ".");

        // 1. Den Hintergrund bereitlegen: Farbe oder Bild (ins Profil kopiert) -
        //    aus Design.json oder aus einer Sicherung im alten Format
        //    (Ordner "Hintergrundbild", bis Version 1.2.6).
        var h = design?.Hintergrund;
        string? gesichert = h?.Bild is null ? null : Path.Combine(BackupPath, DesignEinstellungen.Ordner, h.Bild);
        if (h is null && AppCatalog.HatHintergrund(BackupPath))
            (h, gesichert) = await AlteSicherungLesen();
        (byte R, byte G, byte B)? farbe = Hintergrund.IstVolltonfarbe(h) ? Hintergrund.FarbeLesen(h!.Farbe) : null;
        string? bild = null;
        if (h is not null && farbe is null)
        {
            if (gesichert is null || !File.Exists(gesichert))
            {
                _log(Loc.Tr($"[FEHLER] Das Hintergrundbild fehlt in der Sicherung: {gesichert ?? "(kein Name)"}",
                            $"[ERROR] The wallpaper is missing from the backup: {gesichert ?? "(no name)"}"));
            }
            else
            {
                bild = await HintergrundbildBereitstellen(gesichert, h.BildPfad, h.Profil);
            }
        }
        var werte = design?.Werte ?? new List<DesignEintrag>();

        // 2. Windows das Design selbst anwenden lassen - wie ein Doppelklick
        //    auf eine .theme-Datei. Sonst schreibt der Designdienst beim
        //    Neustart des Explorers bzw. beim Abmelden sein altes Design zurueck.
        if (OperatingSystem.IsWindows() && (farbe is not null || bild is not null || werte.Count > 0))
            await DesignVonWindowsAnwenden(ziel, farbe, bild, h, werte);
        if (_cancel.IsCancelled) return;

        // 3. Die Design-Einstellungen ausdruecklich eintragen - erst JETZT:
        //    Stuenden Hell/Dunkel vorher schon da, saehe Windows beim Anwenden
        //    keinen Wechsel und sagte Taskleiste und Fenstern nicht Bescheid
        //    (CLInstall). Taskleiste und Start stehen nicht in der Design-Datei.
        if (werte.Count > 0) DesignEinstellungenSetzen(design!, ziel);
        else _log(Loc.Tr("[INFO] Die Sicherung enthält keine Design-Einstellungen (ältere Fassung des Tools) - nur der Hintergrund wird wiederhergestellt.",
                         "[INFO] The backup contains no design settings (older version of the tool) - only the wallpaper is restored."));
        if (_cancel.IsCancelled) return;

        // 4. Der Hintergrund - bei einer Volltonfarbe erst hier: Das Design
        //    traegt sie ein, gezeigt wird aber noch das Bild der Datei.
        bool gesetzt = false;
        if (farbe is { } f)
        {
            VolltonfarbeSetzen(ziel, f);
            gesetzt = true;
        }
        else if (bild is not null)
        {
            BildDirektSetzen(ziel, bild, h);
            gesetzt = true;
        }
        else if (h is null && !AppCatalog.HatHintergrund(BackupPath))
        {
            _log(Loc.Tr("[INFO] Die Sicherung enthält keinen Hintergrund.", "[INFO] The backup contains no wallpaper."));
        }

        // Ein paar Sekunden spaeter nachsehen, ob Windows ihn stehen laesst.
        if (gesetzt && OperatingSystem.IsWindows() && !_cancel.IsCancelled)
            await HintergrundKontrollieren(ziel, DesktopHintergrund.Lesen(ziel.Wurzel),
                                           Loc.Tr("Kontrolle nach dem Setzen", "Check after setting"));
    }

    /// <summary>Das Design, das Windows in diesem Lauf angewendet hat - fuer ein erneutes Anwenden.</summary>
    private string? _angewandtesDesign;

    /// <summary>
    /// Baut aus der aktuellen Design-Datei des Benutzers eine mit den Werten
    /// der Sicherung und laesst Windows sie als der angemeldete Benutzer
    /// anwenden (<see cref="WindowsDesign"/>).
    /// </summary>
    private async Task DesignVonWindowsAnwenden(DesignZiel ziel, (byte R, byte G, byte B)? farbe, string? bild,
                                                HintergrundEinstellung? h, List<DesignEintrag> werte)
    {
        if (!OperatingSystem.IsWindows()) return;
        string? vorlage = null;
        try
        {
            if (DesktopHintergrund.DesignDateiPfad(ziel.Wurzel, SourcePath) is { } aktuell && File.Exists(aktuell))
                vorlage = DesignDatei.DateiLesen(aktuell).Inhalt;
        }
        catch { /* dann ohne */ }
        if (vorlage is null)
        {
            _log(Loc.Tr("[INFO] Kein aktuelles Windows-Design lesbar - Hintergrund und Einstellungen werden direkt eingetragen.",
                        "[INFO] No current Windows theme readable - background and settings are written directly."));
            return;
        }

        var inhalt = DesignDatei.FuerWiederherstellung(vorlage, Loc.Tr("Design aus der Sicherung (DaSi)", "Theme from the backup (DaSi)"),
                                                       farbe, bild, h, werte);
        _log(Loc.Tr("[INFO] Windows wendet das Design aus der Sicherung an - wie ein Doppelklick auf die Design-Datei ...",
                    "[INFO] Windows applies the theme from the backup - like double-clicking the theme file ..."));
        if (await DesignAnwendenUndWarten(ziel, inhalt, farbe, bild) is { } datei)
        {
            _angewandtesDesign = inhalt;
            _log(Loc.Tr($"[ERFOLG] Windows hat das Design angewendet ({Path.GetFileName(datei)}, unter Personalisierung > Designs auswählbar).",
                        $"[SUCCESS] Windows applied the theme ({Path.GetFileName(datei)}, selectable under Personalization > Themes)."));
        }
        else
        {
            _log(Loc.Tr("[WARNUNG] Windows hat das Design nicht (rechtzeitig) übernommen - Hintergrund und Einstellungen werden direkt eingetragen. Nach einem Neustart kann der alte Hintergrund zurückkommen.",
                        "[WARNING] Windows did not apply the theme (in time) - background and settings are written directly. The old background may return after a restart."));
        }
    }

    /// <summary>
    /// Laesst Windows das Design anwenden (neue Datei unter ...\Themes im
    /// Profil) und wartet bis 20 s, bis es uebernommen ist: Die Datei ist das
    /// aktuelle Design, bei einer Volltonfarbe steht zudem die Farbe in der
    /// Registry. Ein Bild setzt der Aufrufer danach ohnehin noch direkt - kennt
    /// Windows es schon, aendert das Anwenden daran nichts.
    /// </summary>
    /// <returns>Die angewendete Datei - null, wenn Windows es nicht uebernommen hat.</returns>
    private async Task<string?> DesignAnwendenUndWarten(DesignZiel ziel, string inhalt, (byte R, byte G, byte B)? farbe, string? bild)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var ordner = Path.Combine(SourcePath, @"AppData\Local\Microsoft\Windows\Themes");
        bool Aktuell(string datei)
            => string.Equals(DesktopHintergrund.DesignDateiPfad(ziel.Wurzel, SourcePath), datei, StringComparison.OrdinalIgnoreCase);
        bool Da(string datei)
            => Aktuell(datei) && (farbe is not { } c || Hintergrund.FarbeLesen(DesktopHintergrund.Lesen(ziel.Wurzel).Farbe) == c);
        try
        {
            // Erst die Art: Steht "Volltonfarbe" darin, nimmt Windows aus dem
            // Design nur die Farbe, nicht das Bild.
            if (farbe is not null) DesktopHintergrund.ArtEintragen(ziel.Wurzel, 1);
            else if (bild is not null) DesktopHintergrund.ArtEintragen(ziel.Wurzel, 0);
            var r = await WindowsDesign.AnwendenUndWarten(ordner, inhalt, DesktopScreenshot.GetInteractiveUser()?.Name, Da, Aktuell,
                                                          TimeSpan.FromSeconds(20));
            if (!r.Gestartet)
                _log(Loc.Tr($"[WARNUNG] Windows ließ das Design nicht anwenden: {r.Fehler}", $"[WARNING] Windows did not apply the theme: {r.Fehler}"));
            return r.Uebernommen ? r.Datei : null;
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[WARNUNG] Die Design-Datei ließ sich nicht anlegen oder anwenden: {ex.Message}",
                        $"[WARNING] The theme file could not be created or applied: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Wartet kurz und sieht nach, ob Registry und Desktop noch zeigen, was
    /// gesetzt wurde. Wenn nicht: ins Protokoll, was anders ist und was es
    /// sein koennte, und einmal erneut setzen.
    /// </summary>
    private async Task HintergrundKontrollieren(DesignZiel ziel, HintergrundStand soll, string wann)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Delay(4000);
        var problem = DesktopHintergrund.Unterschied(soll, DesktopHintergrund.Lesen(ziel.Wurzel), Loc.Tr)
                      ?? DesktopHintergrund.Kontrolle(soll, Loc.Tr);
        if (problem is null)
        {
            _log(Loc.Tr($"[INFO] {wann}: Der Desktop zeigt den gesetzten Hintergrund.", $"[INFO] {wann}: The desktop shows the background that was set."));
            return;
        }
        _log(Loc.Tr($"[WARNUNG] {wann}: {problem}. Der Hintergrund wird erneut gesetzt.", $"[WARNING] {wann}: {problem}. Setting the background again."));
        foreach (var e in DesktopHintergrund.Einfluesse(ziel.Wurzel, mitWechsel: true, Loc.Tr))
            _log(Loc.Tr($"[INFO] Möglicher Grund: {e}.", $"[INFO] Possible reason: {e}."));
        try
        {
            // Erst wieder ueber das Windows-Design - das aus diesem Lauf, sonst
            // das aktuelle mit dem Soll-Hintergrund -, dann direkt (bei einer
            // Volltonfarbe zeigt das Design noch sein Bild). Ohne das Design
            // hielte der Explorer weiter seinen alten Stand und schriebe ihn
            // beim naechsten Neustart zurueck.
            var farbeSoll = soll.Bild is null ? Hintergrund.FarbeLesen(soll.Farbe) : null;
            var inhalt = _angewandtesDesign;
            if (inhalt is null && DesktopHintergrund.DesignDateiPfad(ziel.Wurzel, SourcePath) is { } aktuell && File.Exists(aktuell))
                inhalt = DesignDatei.DateiLesen(aktuell).Inhalt;
            if (inhalt is not null)
            {
                inhalt = DesignDatei.FuerWiederherstellung(inhalt, Loc.Tr("Design aus der Sicherung (DaSi)", "Theme from the backup (DaSi)"),
                                                           farbeSoll, soll.Bild,
                                                           new HintergrundEinstellung { Darstellung = soll.Stil, Kacheln = soll.Kacheln },
                                                           Array.Empty<DesignEintrag>());
                if (soll.Bild is null && farbeSoll is null) inhalt = null;
            }
            if (inhalt is not null)
                await DesignAnwendenUndWarten(ziel, inhalt, farbeSoll, soll.Bild);
            DesktopHintergrund.Erneut(ziel.Wurzel, ziel.EigenerBenutzer, soll, SourcePath);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Der Hintergrund ließ sich nicht erneut setzen: {ex.Message}", $"[ERROR] The background could not be set again: {ex.Message}"));
            return;
        }
        await Task.Delay(3000);
        var nochmal = DesktopHintergrund.Unterschied(soll, DesktopHintergrund.Lesen(ziel.Wurzel), Loc.Tr)
                      ?? DesktopHintergrund.Kontrolle(soll, Loc.Tr);
        _log(nochmal is null
            ? Loc.Tr("[ERFOLG] Hintergrund erneut gesetzt - jetzt zeigt der Desktop ihn.", "[SUCCESS] Background set again - the desktop now shows it.")
            : Loc.Tr($"[WARNUNG] Windows ändert den Hintergrund weiterhin: {nochmal}. Bitte unter Einstellungen > Personalisierung > Hintergrund nachsehen.",
                     $"[WARNING] Windows keeps changing the background: {nochmal}. Please check Settings > Personalization > Background."));
    }

    /// <summary>Wohin Hintergrund und Design-Einstellungen gehoeren.</summary>
    /// <param name="Wurzel">HKEY_USERS\&lt;SID&gt; des angemeldeten Benutzers, sonst HKEY_CURRENT_USER.</param>
    /// <param name="EigenerBenutzer">Laeuft das Tool unter genau diesem Benutzer? Sonst gilt Windows-Aufrufe, die in die
    /// Registry des Prozesses schreiben, nur fuer die laufende Sitzung.</param>
    /// <param name="Name">Der angemeldete Benutzer, falls bekannt.</param>
    private sealed record DesignZiel(RegistryKey Wurzel, bool Eigen, bool EigenerBenutzer, string? Name) : IDisposable
    {
        public void Dispose() { if (Eigen) Wurzel.Dispose(); }
    }

    /// <summary>
    /// Der Registry-Zweig fuer Hintergrund und Design-Einstellungen: der des
    /// ANGEMELDETEN Benutzers (HKEY_USERS\&lt;SID&gt;) - das Tool laeuft mit
    /// Adminrechten, womoeglich unter einem anderen Konto. Gehoert der
    /// angemeldete Benutzer nicht zum gewaehlten Profil, null: Dann wuerden
    /// Hintergrund und Einstellungen eines anderen gelesen bzw. veraendert.
    /// </summary>
    private DesignZiel? DesignZweig(bool schreiben)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var angemeldet = DesktopScreenshot.GetInteractiveUser();
        if (angemeldet?.ProfilePath is { } eigenesProfil && !ProfileExcludes.SameFolder(eigenesProfil, SourcePath))
        {
            _log(Loc.Tr($"[FEHLER] Design: Angemeldet ist {angemeldet.Name} ({eigenesProfil}), gewählt ist aber {SourcePath}. "
                        + "Hintergrund, Hell/Dunkel, Akzentfarbe & Co. gehören dem angemeldeten Benutzer - bitte als dieser Benutzer anmelden und das Tool dort starten.",
                        $"[ERROR] Design: {angemeldet.Name} ({eigenesProfil}) is signed in, but {SourcePath} is selected. "
                        + "Background, light/dark mode, accent color etc. belong to the signed-in user - please sign in as that user and run the tool there."));
            return null;
        }
        var (wurzel, eigen) = BenutzerZweig(schreiben);
        bool eigenerBenutzer = !eigen || DesktopHintergrund.IstProzessBenutzer(angemeldet?.Sid);
        if (!eigenerBenutzer && angemeldet is not null)
            _log(Loc.Tr($"[INFO] Das Tool läuft unter dem Konto {Environment.UserName}, angemeldet ist {angemeldet.Name} - Hintergrund und Design werden bei {angemeldet.Name} gelesen bzw. eingetragen.",
                        $"[INFO] The tool runs under the account {Environment.UserName}, {angemeldet.Name} is signed in - background and design are read from / written to {angemeldet.Name}."));
        return new DesignZiel(wurzel, eigen, eigenerBenutzer, angemeldet?.Name);
    }

    /// <summary>
    /// HKEY_USERS\&lt;SID&gt; des angemeldeten Benutzers - sonst HKEY_CURRENT_USER.
    /// Ob er zum gewaehlten Profil gehoert, prueft der Aufrufer.
    /// </summary>
    private static (RegistryKey Wurzel, bool Eigen) BenutzerZweig(bool schreiben)
    {
        if (OperatingSystem.IsWindows() && DesktopScreenshot.GetInteractiveUser()?.Sid is { } sid)
        {
            try
            {
                var k = Registry.Users.OpenSubKey(sid, schreiben);
                if (k is not null) return (k, true);
            }
            catch { /* dann HKEY_CURRENT_USER */ }
        }
        return (Registry.CurrentUser, false);
    }

    /// <summary>Liest die Design-Einstellungen; null, wenn sie nicht gelesen werden (anderer Benutzer, Fehler).</summary>
    private List<DesignEintrag>? DesignEinstellungenLesen(DesignZiel ziel)
    {
        try
        {
            var werte = DesignEinstellungen.Auslesen((s, n) => DesignRegistry.Lesen(ziel.Wurzel, s, n));
            if (werte.Count == 0)
            {
                _log(Loc.Tr("[WARNUNG] Es wurden keine Design-Einstellungen gefunden.", "[WARNING] No design settings were found."));
                return werte;
            }
            var kurz = DesignEinstellungen.Zusammenfassung(werte, Loc.Tr);
            _log(Loc.Tr($"[ERFOLG] Design-Einstellungen gesichert ({werte.Count} Werte: {DesignEinstellungen.Gruppen(werte)}){(kurz.Length > 0 ? " - " + kurz : "")}.",
                        $"[SUCCESS] Design settings backed up ({werte.Count} values: {DesignEinstellungen.Gruppen(werte)}){(kurz.Length > 0 ? " - " + kurz : "")}."));
            return werte;
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Design-Einstellungen konnten nicht gelesen werden: {ex.Message}",
                        $"[ERROR] The design settings could not be read: {ex.Message}"));
            return null;
        }
    }

    private void DesignEinstellungenSetzen(DesignSicherung sicherung, DesignZiel ziel)
    {
        try
        {
            // Schreiben und Windows Bescheid geben. Die Mauszeiger laedt Windows
            // dabei aus der Registry des PROZESSES - laeuft das Tool unter einem
            // anderen Konto, gelten sie erst ab der naechsten Anmeldung.
            var ergebnis = DesignRegistry.Uebernehmen(ziel.Wurzel, sicherung.Werte, ziel.EigenerBenutzer);
            var (gesetzt, ohneDatei, nichtSchreibbar) = (ergebnis.Gesetzt, ergebnis.OhneDatei, ergebnis.NichtSchreibbar);
            bool zeiger = ergebnis.Zeiger;
            // Hell/Dunkel, Akzentfarbe und Zeiger auch in der Design-Datei - die
            // liest Windows beim Anmelden.
            if (OperatingSystem.IsWindows())
                DesignDateiProtokoll(DesktopHintergrund.DesignDateiAngleichen(ziel.Wurzel, SourcePath, t => DesignDatei.FuerDesign(t, gesetzt)));

            var kurz = DesignEinstellungen.Zusammenfassung(gesetzt, Loc.Tr);
            _log(Loc.Tr($"[ERFOLG] Design-Einstellungen übernommen ({gesetzt.Count} Werte: {DesignEinstellungen.Gruppen(gesetzt)}){(kurz.Length > 0 ? " - " + kurz : "")}.",
                        $"[SUCCESS] Design settings applied ({gesetzt.Count} values: {DesignEinstellungen.Gruppen(gesetzt)}){(kurz.Length > 0 ? " - " + kurz : "")}."));
            if (ohneDatei.Count > 0)
                _log(Loc.Tr($"[INFO] {ohneDatei.Count} Mauszeiger aus einem eigenen Schema gibt es auf diesem Rechner nicht - dort bleibt der Standardzeiger: {string.Join(", ", ohneDatei)}.",
                            $"[INFO] {ohneDatei.Count} pointers from a custom scheme do not exist on this machine - the default pointer remains: {string.Join(", ", ohneDatei)}."));
            if (nichtSchreibbar.Count > 0)
                _log(Loc.Tr($"[WARNUNG] {nichtSchreibbar.Count} Werte ließen sich nicht setzen: {string.Join(", ", nichtSchreibbar)}.",
                            $"[WARNING] {nichtSchreibbar.Count} values could not be set: {string.Join(", ", nichtSchreibbar)}."));
            _log(Loc.Tr("[INFO] Hell/Dunkel und Transparenz gelten sofort. Die Akzentfarbe auf Titelleisten und Taskleiste übernimmt Windows teils erst nach dem nächsten Anmelden.",
                        "[INFO] Light/dark mode and transparency apply right away. Windows partly applies the accent color on title bars and the taskbar only after the next sign-in."));
            if (zeiger && !ziel.EigenerBenutzer)
                _log(Loc.Tr("[INFO] Die Mauszeiger übernimmt Windows erst nach der nächsten Anmeldung - das Tool läuft unter einem anderen Konto.",
                            "[INFO] Windows only applies the mouse pointers after the next sign-in - the tool runs under another account."));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Design-Einstellungen konnten nicht übernommen werden: {ex.Message}",
                        $"[ERROR] The design settings could not be applied: {ex.Message}"));
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
    /// Oeffnet ein Bild fuer den Benutzer. Fuer die Pruefungen ersetzbar - dort
    /// soll keine Bildanzeige aufgehen.
    /// </summary>
    internal Action<string> BildOeffnen { get; set; } = datei =>
    {
        // Ueber explorer.exe statt direkt: Das Tool laeuft mit
        // Administratorrechten, und die Fotos-App startet nicht mit
        // erhoehten Rechten. explorer.exe gibt die Datei an den laufenden
        // Explorer des Benutzers weiter, der sie mit normalen Rechten oeffnet.
        using var _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", Quote(datei)) { UseShellExecute = false });
    };

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
                BildOeffnen(datei);
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

    // ------------------------------------------------------------ Taskleisten-Symbole

    /// <summary>
    /// Sichert die an die Taskleiste angehefteten Symbole: die Verknuepfungen
    /// aus "User Pinned\TaskBar" und den Registry-Schluessel Taskband
    /// (Reihenfolge, auch angeheftete Store-Apps ohne Verknuepfung).
    /// </summary>
    private void TaskleisteSichern()
    {
        var pinOrdner = Taskleiste.PinOrdner(SourcePath);
        var verknuepfungen = Taskleiste.Verknuepfungen(pinOrdner);
        var taskband = new List<DesignEintrag>();
        if (OperatingSystem.IsWindows())
        {
            var (wurzel, eigen) = BenutzerZweig(schreiben: false);
            try
            {
                using var k = wurzel.OpenSubKey(Taskleiste.TaskbandSchluessel);
                foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                    if (DesignRegistry.Lesen(wurzel, Taskleiste.TaskbandSchluessel, name) is { } v)
                        taskband.Add(new DesignEintrag { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = name, Typ = v.Typ, Wert = v.Wert });
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[WARNUNG] Die Reihenfolge der Taskleiste ließ sich nicht lesen: {ex.Message}",
                            $"[WARNING] The taskbar order could not be read: {ex.Message}"));
            }
            finally { if (eigen) wurzel.Dispose(); }
        }
        if (verknuepfungen.Count == 0 && taskband.Count == 0)
        {
            _log(Loc.Tr("[WARNUNG] An die Taskleiste ist nichts angeheftet - es gibt keine Taskleisten-Symbole zu sichern.",
                        "[WARNING] Nothing is pinned to the taskbar - there are no taskbar icons to back up."));
            return;
        }

        try
        {
            var ziel = Taskleiste.SicherungsOrdner(BackupPath);
            Directory.CreateDirectory(ziel);
            // Erneut in denselben Ordner gesichert: fruehere Verknuepfungen weg.
            foreach (var alt in Directory.GetFiles(ziel, "*.lnk")) File.Delete(alt);
            foreach (var n in verknuepfungen) File.Copy(Path.Combine(pinOrdner, n), Path.Combine(ziel, n), overwrite: true);
            Taskleiste.Speichern(BackupPath, new TaskleistenSicherung
            {
                Rechner = Environment.MachineName,
                Erstellt = DateTime.Now,
                Verknuepfungen = verknuepfungen,
                Taskband = taskband,
            });
            _log(verknuepfungen.Count > 0
                ? Loc.Tr($"[ERFOLG] Taskleiste gesichert: {verknuepfungen.Count} angeheftete Verknüpfungen ({Taskleiste.Namen(verknuepfungen)}) samt Reihenfolge und angehefteten Apps.",
                         $"[SUCCESS] Taskbar backed up: {verknuepfungen.Count} pinned shortcuts ({Taskleiste.Namen(verknuepfungen)}) including order and pinned apps.")
                : Loc.Tr("[ERFOLG] Taskleiste gesichert: Reihenfolge und angeheftete Apps.",
                         "[SUCCESS] Taskbar backed up: order and pinned apps."));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Taskleisten-Symbole konnten nicht gesichert werden: {ex.Message}",
                        $"[ERROR] The taskbar icons could not be backed up: {ex.Message}"));
        }
    }

    /// <summary>
    /// Heftet die gesicherten Symbole wieder an die Taskleiste: Verknuepfungen
    /// zurueck, Taskband schreiben, Explorer neu starten.
    /// </summary>
    /// <returns>true, wenn der Explorer dafuer neu gestartet wurde.</returns>
    private async Task<bool> TaskleisteZurueckschreiben(TaskleistenSicherung s)
    {
        var quelle = Taskleiste.SicherungsOrdner(BackupPath);
        var ziel = Taskleiste.PinOrdner(SourcePath);
        var zurueck = new List<string>();
        var fehlend = new List<string>();
        try
        {
            Directory.CreateDirectory(ziel);
            foreach (var n in s.Verknuepfungen)
            {
                var q = Path.Combine(quelle, n);
                if (File.Exists(q)) { File.Copy(q, Path.Combine(ziel, n), overwrite: true); zurueck.Add(n); }
                else fehlend.Add(n);
            }
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Taskleisten-Verknüpfungen konnten nicht zurückgeschrieben werden: {ex.Message}",
                        $"[ERROR] The taskbar shortcuts could not be restored: {ex.Message}"));
            return false;
        }
        var ohneProgramm = OperatingSystem.IsWindows() ? TaskleistenZielePruefen(ziel, zurueck) : new List<string>();

        if (!OperatingSystem.IsWindows() || s.Taskband.Count == 0)
        {
            _log(Loc.Tr($"[WARNUNG] Die Sicherung enthält nur die Verknüpfungen der Taskleiste, nicht ihre Reihenfolge - angeheftet werden sie dadurch nicht: {Taskleiste.Namen(zurueck)}",
                        $"[WARNING] The backup only contains the taskbar shortcuts, not their order - this does not pin them: {Taskleiste.Namen(zurueck)}"));
            return false;
        }

        // Schreiben, den Explorer neu starten (er liest die angehefteten Symbole
        // nur beim Start) und nachsehen, ob Windows sie behalten hat. Hat es die
        // Taskleiste selbst neu belegt, ein zweites Mal - danach ehrlich melden.
        // Frueher stand hier ungeprueft "[ERFOLG]"; bei einem Anwender war die
        // Taskleiste danach leer.
        var taskband = OperatingSystem.IsWindows() ? KennungenAnpassen(s.Taskband) : s.Taskband;
        // Symbole von Programmen, die es hier noch nicht gibt, zeigt Windows erst
        // mit dem Programm - sie zaehlen bei der Kontrolle nicht mit.
        var erwartet = Taskleiste.AngehefteteNamen(Taskleiste.Favorites(taskband), zurueck.Except(ohneProgramm));
        List<string> nichtUebernommen = new();
        for (int versuch = 1; versuch <= 2; versuch++)
        {
            var (wurzel, eigen) = BenutzerZweig(schreiben: true);
            try
            {
                foreach (var e in taskband) DesignRegistry.Schreiben(wurzel, e);
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[FEHLER] Die Reihenfolge der Taskleiste ließ sich nicht schreiben: {ex.Message}",
                            $"[ERROR] The taskbar order could not be written: {ex.Message}"));
                return false;
            }
            finally { if (eigen) wurzel.Dispose(); }

            if (versuch == 1)
                _log(Loc.Tr("[INFO] Der Explorer startet kurz neu, damit die Taskleiste die gesicherten Symbole übernimmt - offene Explorer-Fenster schließen sich dabei.",
                            "[INFO] Explorer restarts briefly so the taskbar picks up the saved icons - open Explorer windows close in the process."));
            var (ok, vonSelbst) = await ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(20));
            if (!ok)
            {
                _log(Loc.Tr("[WARNUNG] Der Explorer kam nicht zurück. Die Taskleiste zeigt die Symbole nach dem nächsten Anmelden. Notfalls im Task-Manager \"Neuen Task ausführen\" > explorer.exe.",
                            "[WARNING] Explorer did not come back. The taskbar shows the icons after the next sign-in. If needed: Task Manager > \"Run new task\" > explorer.exe."));
                return true;
            }
            if (!vonSelbst && versuch == 1)
                _log(Loc.Tr("[INFO] Windows hat den Explorer nicht selbst neu gestartet - das Tool hat ihn gestartet.",
                            "[INFO] Windows did not restart Explorer by itself - the tool started it."));

            await Task.Delay(TaskleistenKontrolle);
            var jetzt = FavoritesNachlesen is { } nachlesen ? nachlesen() : FavoritesLesen();
            nichtUebernommen = erwartet.Except(Taskleiste.AngehefteteNamen(jetzt, erwartet), StringComparer.OrdinalIgnoreCase).ToList();
            if (nichtUebernommen.Count == 0 || _cancel.IsCancelled) break;
            if (versuch == 1)
                _log(Loc.Tr($"[WARNUNG] Windows hat die Taskleiste nach dem Neustart selbst neu belegt - nicht übernommen: {Taskleiste.Namen(nichtUebernommen)}. Das Tool schreibt sie noch einmal.",
                            $"[WARNING] Windows re-populated the taskbar by itself after the restart - not taken over: {Taskleiste.Namen(nichtUebernommen)}. The tool writes them once more."));
        }
        if (nichtUebernommen.Count > 0)
        {
            _log(Loc.Tr($"[FEHLER] Windows übernimmt die gesicherten Taskleisten-Symbole nicht: {Taskleiste.Namen(nichtUebernommen)}. "
                        + "Die Verknüpfungen liegen im Ordner \"User Pinned\\TaskBar\" des Profils. Abhilfe: abmelden, wieder anmelden und \"Desktop-Layout wiederherstellen\" erneut ausführen - "
                        + "oder die Programme von Hand anheften.",
                        $"[ERROR] Windows does not take over the saved taskbar icons: {Taskleiste.Namen(nichtUebernommen)}. "
                        + "The shortcuts are in the profile's \"User Pinned\\TaskBar\" folder. Remedy: sign out, sign in again and run \"Restore desktop layout\" again - "
                        + "or pin the programs by hand."));
            return true;
        }

        // Zeigt Windows die Eintraege auch? Das steht in keinem Registry-Wert -
        // nachgesehen wird auf der Taskleiste selbst, ueber die App-Kennungen.
        if (!TaskleisteSichtbar(Taskleiste.Eintraege(Taskleiste.Favorites(taskband), zurueck), ohneProgramm)) return true;
        _log(Loc.Tr($"[ERFOLG] Taskleiste wiederhergestellt: {(zurueck.Count > 0 ? $"{zurueck.Count} Verknüpfungen ({Taskleiste.Namen(zurueck)}) und " : "")}die angehefteten Apps in der gesicherten Reihenfolge.",
                    $"[SUCCESS] Taskbar restored: {(zurueck.Count > 0 ? $"{zurueck.Count} shortcuts ({Taskleiste.Namen(zurueck)}) and " : "")}the pinned apps in the saved order."));
        if (fehlend.Count > 0)
            _log(Loc.Tr($"[WARNUNG] {fehlend.Count} Verknüpfungen fehlen in der Sicherung: {Taskleiste.Namen(fehlend)}",
                        $"[WARNING] {fehlend.Count} shortcuts are missing from the backup: {Taskleiste.Namen(fehlend)}"));
        return true;
    }

    /// <summary>Nur fuer die Pruefung: die App-Kennungen, die die Taskleiste zeigt (null: nicht lesbar).</summary>
    internal Func<HashSet<string>?>? TaskleistenSicht { get; set; }

    /// <summary>
    /// Vergleicht die angehefteten Eintraege mit dem, was die Taskleiste zeigt.
    /// Fehlt das Symbol eines Programms, das hier installiert ist, ist das ein
    /// Fehler; fehlt das Programm selbst noch, ein Hinweis.
    /// </summary>
    /// <returns>false, wenn ein Fehler gemeldet wurde.</returns>
    private bool TaskleisteSichtbar(List<Taskleiste.Eintrag> eintraege, List<string> ohneProgramm)
    {
        var sichtbar = TaskleistenSicht is { } sicht ? sicht() : OperatingSystem.IsWindows() ? TaskleistenAnsicht.Kennungen() : null;
        var mitKennung = eintraege.Where(e => e.Kennung is not null).ToList();
        if (mitKennung.Count == 0) return true;
        if (sichtbar is null)
        {
            _log(Loc.Tr("[INFO] Ob die Taskleiste die Symbole zeigt, ließ sich nicht nachsehen - bitte einen Blick darauf werfen.",
                        "[INFO] Whether the taskbar shows the icons could not be checked - please have a look."));
            return true;
        }
        static string Name(Taskleiste.Eintrag e) => e.Verknuepfung is { } v ? Path.GetFileNameWithoutExtension(v) : e.Kennung!;
        var unsichtbar = mitKennung.Where(e => !sichtbar.Contains(e.Kennung!)).ToList();
        var fehltProgramm = unsichtbar.Where(e => e.Verknuepfung is null || ohneProgramm.Contains(e.Verknuepfung, StringComparer.OrdinalIgnoreCase)).ToList();
        var trotzdem = unsichtbar.Except(fehltProgramm).ToList();
        if (fehltProgramm.Count > 0)
            _log(Loc.Tr($"[WARNUNG] Die Taskleiste zeigt diese Symbole nicht - das Programm fehlt hier (noch): {string.Join(", ", fehltProgramm.Select(Name))}. Nach der Installation den Punkt erneut ausführen.",
                        $"[WARNING] The taskbar does not show these icons - the program is missing here (yet): {string.Join(", ", fehltProgramm.Select(Name))}. Run this item again after installing."));
        if (trotzdem.Count > 0)
        {
            _log(Loc.Tr($"[FEHLER] Die Taskleiste zeigt diese angehefteten Symbole nicht, obwohl das Programm da ist: {string.Join(", ", trotzdem.Select(e => $"{Name(e)} ({e.Kennung})"))}. "
                        + "Abhilfe: im Startmenü per Rechtsklick > \"An Taskleiste anheften\".",
                        $"[ERROR] The taskbar does not show these pinned icons although the program is present: {string.Join(", ", trotzdem.Select(e => $"{Name(e)} ({e.Kennung})"))}. "
                        + "Remedy: in the Start menu, right-click > \"Pin to taskbar\"."));
            return false;
        }
        _log(Loc.Tr($"[INFO] Die Taskleiste zeigt {mitKennung.Count - unsichtbar.Count} von {mitKennung.Count} gesicherten Symbolen.",
                    $"[INFO] The taskbar shows {mitKennung.Count - unsichtbar.Count} of {mitKennung.Count} saved icons."));
        return true;
    }

    /// <summary>Wie lange nach dem Explorer-Neustart gewartet wird, bevor nachgesehen wird.</summary>
    internal TimeSpan TaskleistenKontrolle { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Nur fuer die Pruefung: was Windows nach dem Neustart in Taskband\Favorites stehen hat.</summary>
    internal Func<byte[]?>? FavoritesNachlesen { get; set; }

    private byte[]? FavoritesLesen()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var (wurzel, eigen) = BenutzerZweig(schreiben: false);
        try
        {
            var v = DesignRegistry.Lesen(wurzel, Taskleiste.TaskbandSchluessel, "Favorites");
            return v is { Typ: "Binary" } b ? Convert.FromBase64String(b.Wert) : null;
        }
        catch { return null; }
        finally { if (eigen) wurzel.Dispose(); }
    }

    /// <summary>
    /// Zeigen die zurueckgeschriebenen Taskleisten-Verknuepfungen auf Programme,
    /// die es hier gibt? Liegt eins nur woanders (32 statt 64 Bit, anderer Ort
    /// laut "App Paths"), wird die Verknuepfung umgestellt. Fehlt es, sagt das
    /// Protokoll, welche - meist Programme, die erst noch installiert werden.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    /// <returns>Die Verknuepfungen (Dateinamen), deren Programm es hier nicht gibt.</returns>
    internal List<string> TaskleistenZielePruefen(string ordner, IEnumerable<string> verknuepfungen)
    {
        var fehlend = new List<string>();
        var fehlendeDateien = new List<string>();
        var programme = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programme86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var n in verknuepfungen)
        {
            var lnk = Path.Combine(ordner, n);
            var inhalt = Verknuepfung.Lesen(lnk);
            // Shell-Objekte (Datei-Explorer) und Ziele, die es gibt: nichts zu tun.
            if (inhalt is null || inhalt.Ziel.Length == 0 || File.Exists(inhalt.Ziel) || Directory.Exists(inhalt.Ziel)) continue;
            var name = Path.GetFileNameWithoutExtension(n);
            var neu = Taskleiste.NeuesZiel(inhalt.Ziel, File.Exists, AppPathNachschlagen, programme, programme86);
            if (neu is not null && Verknuepfung.ZielSetzen(lnk, neu))
                _log(Loc.Tr($"[INFO] Taskleiste: {name} zeigte auf {inhalt.Ziel} - hier liegt das Programm unter {neu}, die Verknüpfung ist umgestellt.",
                            $"[INFO] Taskbar: {name} pointed to {inhalt.Ziel} - here the program is at {neu}, the shortcut has been updated."));
            else
            {
                fehlend.Add($"{name} ({inhalt.Ziel})");
                fehlendeDateien.Add(n);
            }
        }
        if (fehlend.Count > 0)
            _log(Loc.Tr($"[WARNUNG] Taskleiste: Diese angehefteten Programme gibt es auf diesem PC (noch) nicht: {string.Join(", ", fehlend)}. "
                        + "Nach ihrer Installation \"Desktop-Layout wiederherstellen\" erneut ausführen.",
                        $"[WARNING] Taskbar: these pinned programs do not exist on this PC (yet): {string.Join(", ", fehlend)}. "
                        + "After installing them, run \"Restore desktop layout\" again."));
        return fehlendeDateien;
    }

    /// <summary>
    /// Passt die App-Kennungen in Taskband\Favorites an die Programme dieses PCs
    /// an. Ueber die Kennung ordnet die Taskleiste ein angeheftetes Symbol einem
    /// installierten Programm zu; findet sie keins, zeigt sie es nicht. Bei
    /// einem Anwender (Windows 10 -> 11) trug Firefox die Kennung des
    /// 32-Bit-Firefox ("E7CF176E110C211B"), winget hatte das 64-Bit-Firefox
    /// installiert ("308046B0AF4A39CB"). Ersetzt werden nur gleich lange Kennungen.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal List<DesignEintrag> KennungenAnpassen(List<DesignEintrag> taskband)
    {
        var kennungen = Taskleiste.Kennungen(Taskleiste.Favorites(taskband)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kennungen.Count == 0) return taskband;

        // Firefox/Thunderbird: Kennung = Schluessel des Installationsordners.
        var mozilla = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in new[] { AppCatalog.Firefox, AppCatalog.Thunderbird })
        {
            var (exe, ordnerName) = MozillaProfile.Programm(app)!.Value;
            var hier = TestInstallationen ?? MozillaProfile.Installationen(exe, ordnerName, SourcePath);
            if (hier.Count == 0) continue;
            var schluesselHier = hier.Select(MozillaProfile.Schluessel).ToList();
            foreach (var ort in MozillaProfile.UeblicheOrte(ordnerName, SourcePath))
            {
                var alt = MozillaProfile.Schluessel(ort);
                if (!schluesselHier.Contains(alt, StringComparer.OrdinalIgnoreCase)) mozilla.TryAdd(alt, schluesselHier[0]);
            }
        }
        // Programme ohne eigene Kennung: Pfad ab dem Programme-Ordner.
        var ordner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Taskleiste.OrdnerProgramme86] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            [Taskleiste.OrdnerProgramme64] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            [Taskleiste.OrdnerProgramme] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        var ergebnis = taskband.Select(e => new DesignEintrag { Gruppe = e.Gruppe, Schluessel = e.Schluessel, Name = e.Name, Typ = e.Typ, Wert = e.Wert }).ToList();
        var ersetzt = new List<string>();
        foreach (var k in kennungen)
        {
            if (Taskleiste.NeueKennung(k, mozilla, ordner, File.Exists) is not { } neu) continue;
            foreach (var e in ergebnis.Where(e => e.Typ == "Binary" && e.Name.StartsWith("Favorites", StringComparison.Ordinal)))
            {
                try { e.Wert = Convert.ToBase64String(Taskleiste.KennungErsetzen(Convert.FromBase64String(e.Wert), k, neu)); }
                catch { /* unlesbarer Wert - bleibt */ }
            }
            ersetzt.Add($"{k} -> {neu}");
        }
        if (ersetzt.Count > 0)
            _log(Loc.Tr($"[INFO] Taskleiste: Kennungen an die Programme dieses PCs angepasst, damit Windows die angehefteten Symbole zuordnet: {string.Join(", ", ersetzt)}.",
                        $"[INFO] Taskbar: app IDs adjusted to the programs on this PC so Windows matches the pinned icons: {string.Join(", ", ersetzt)}."));
        return ergebnis;
    }

    /// <summary>Der Ort eines Programms laut "App Paths" (fuer alle Benutzer, 64 und 32 Bit).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? AppPathNachschlagen(string datei)
    {
        foreach (var ansicht in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var basis = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, ansicht);
                using var k = basis.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{datei}");
                if (k?.GetValue("") is string p && p.Trim().Trim('"') is { Length: > 0 } pfad)
                    return Environment.ExpandEnvironmentVariables(pfad);
            }
            catch { /* naechste */ }
        }
        return null;
    }

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

    /// <summary>
    /// "Symbolpositionen": merkt sich, welches Desktop-Symbol wo liegt, und
    /// sichert die an die Taskleiste angehefteten Symbole.
    /// </summary>
    public async Task BackupDesktopIcons()
    {
        if (!DesktopGehoertZumProfil("Keine Symbolpositionen gesichert", "No icon positions backed up")) return;
        await DesktopSymbolpositionenSichern();
        if (_cancel.IsCancelled) return;
        TaskleisteSichern();
    }

    private async Task DesktopSymbolpositionenSichern()
    {

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
        var taskleiste = Taskleiste.Laden(BackupPath);
        if (layout is null && taskleiste is null)
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegen weder Symbolpositionen noch Taskleisten-Symbole: {datei}",
                        $"[ERROR] The backup contains neither icon positions nor taskbar icons: {datei}"));
            return;
        }
        if (!DesktopGehoertZumProfil("Symbolpositionen nicht gesetzt", "Icon positions not applied")) return;

        // Erst die Taskleiste: Dafuer startet der Explorer neu - danach erst
        // die Desktop-Symbole an ihre Stelle, sonst ordnete der Neustart sie
        // womoeglich neu.
        HintergrundStand? hgVorher = null;
        if (taskleiste is not null && OperatingSystem.IsWindows())
        {
            var (w, e) = BenutzerZweig(schreiben: false);
            try { hgVorher = DesktopHintergrund.Lesen(w); } finally { if (e) w.Dispose(); }
        }
        var neuGestartet = taskleiste is not null && await TaskleisteZurueckschreiben(taskleiste);
        if (_cancel.IsCancelled) return;

        // Unter manchem Windows setzt der Neustart des Explorers den
        // Hintergrund zurueck - nachsehen und notfalls erneut setzen.
        if (neuGestartet && hgVorher is not null && OperatingSystem.IsWindows())
        {
            var angemeldet = DesktopScreenshot.GetInteractiveUser();
            var (w, e) = BenutzerZweig(schreiben: true);
            using var ziel = new DesignZiel(w, e, !e || DesktopHintergrund.IstProzessBenutzer(angemeldet?.Sid), angemeldet?.Name);
            await HintergrundKontrollieren(ziel, hgVorher, Loc.Tr("Hintergrund nach dem Explorer-Neustart", "Background after the Explorer restart"));
        }
        if (layout is null)
        {
            _log(Loc.Tr("[INFO] Die Sicherung enthält keine Positionen von Desktop-Symbolen.", "[INFO] The backup contains no desktop icon positions."));
            return;
        }

        _log(Loc.Tr($"[INFO] Setze {layout.Symbole.Count} Desktop-Symbole an ihre gesicherte Stelle (gesichert am {layout.Erstellt:dd.MM.yyyy} auf {layout.Rechner})...",
                    $"[INFO] Moving {layout.Symbole.Count} desktop icons to their saved positions (saved on {layout.Erstellt:yyyy-MM-dd} on {layout.Rechner})..."));
        IconErgebnis erg;
        var sid = OperatingSystem.IsWindows() ? DesktopScreenshot.GetInteractiveUser()?.Sid : null;
        // Nach einem Neustart des Explorers erscheinen Desktop und Symbole erst
        // nach und nach: laenger warten, und das Oeffnen des Desktops wiederholen.
        var warten = TimeSpan.FromSeconds(neuGestartet ? 25 : 10);
        IconErgebnis? ergebnis = null;
        Exception? letzter = null;
        for (int versuch = 0; versuch < (neuGestartet ? 10 : 1) && ergebnis is null; versuch++)
        {
            try { ergebnis = await Task.Run(() => DesktopIcons.Wiederherstellen(layout, warten, k => SystemsymboleEinschalten(k, sid))); }
            catch (Exception ex) { letzter = ex; if (neuGestartet) await Task.Delay(1500); }
        }
        if (ergebnis is null)
        {
            _log(Loc.Tr($"[FEHLER] Die Symbolpositionen ließen sich nicht setzen: {letzter?.Message}",
                        $"[ERROR] The icon positions could not be applied: {letzter?.Message}"));
            return;
        }
        erg = ergebnis;

        if (erg.Eingeschaltet is { Count: > 0 } an)
        {
            var namen = string.Join(", ", an.Select(k => DesktopIcons.Systemsymbole.TryGetValue(k, out var n) ? Loc.Tr(n.De, n.En) : k));
            _log(Loc.Tr($"[INFO] Systemsymbole eingeschaltet wie auf dem alten Desktop: {namen}.",
                        $"[INFO] System icons switched on as on the old desktop: {namen}."));
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
            static string Namen(List<DesktopIcon> l) =>
                string.Join(", ", l.Take(10).Select(f => string.IsNullOrWhiteSpace(f.Anzeige) ? f.Name : f.Anzeige))
                + (l.Count > 10 ? ", ..." : "");
            var (imProfil, fremd) = DesktopIcons.NachHerkunft(erg.Fehlend, Path.Combine(BackupPath, "Benutzerprofil", "Desktop"));
            if (imProfil.Count > 0)
                _log(Loc.Tr($"[WARNUNG] {imProfil.Count} von {erg.Gesamt} Symbolen gibt es auf diesem Desktop nicht: {Namen(imProfil)}. "
                            + "Sie wurden ausgelassen - am besten zusammen mit dem Benutzerprofil und den Programmen wiederherstellen.",
                            $"[WARNING] {imProfil.Count} of {erg.Gesamt} icons do not exist on this desktop: {Namen(imProfil)}. "
                            + "They were skipped - best restore them together with the user profile and the programs."));
            // Nicht aus dem Profil: Diese Verknuepfungen legen Programme bei der
            // Installation an - meist auf dem oeffentlichen Desktop.
            if (fremd.Count > 0)
                _log(Loc.Tr($"[WARNUNG] {fremd.Count} von {erg.Gesamt} Symbolen fehlen hier und stammen nicht aus dem Benutzerprofil: {Namen(fremd)}. "
                            + "Solche Verknüpfungen liegen meist auf dem öffentlichen Desktop (für alle Benutzer) und erscheinen, sobald ihr Programm installiert ist. "
                            + "Danach \"Desktop-Layout wiederherstellen\" noch einmal ausführen, dann kommen auch sie an ihre Stelle.",
                            $"[WARNING] {fremd.Count} of {erg.Gesamt} icons are missing here and do not come from the user profile: {Namen(fremd)}. "
                            + "Such shortcuts usually live on the public desktop (for all users) and appear once their program is installed. "
                            + "Then run \"Restore desktop layout\" again to put them in place as well."));
        }
        _log(erg.Gesetzt > 0
            ? Loc.Tr($"[ERFOLG] {erg.Gesetzt} von {erg.Gesamt} Desktop-Symbolen an ihre gesicherte Stelle gesetzt.",
                     $"[SUCCESS] {erg.Gesetzt} of {erg.Gesamt} desktop icons moved to their saved positions.")
            : Loc.Tr("[FEHLER] Kein gesichertes Symbol ließ sich auf diesem Desktop zuordnen.",
                     "[ERROR] None of the saved icons could be matched on this desktop."));
    }

    /// <summary>
    /// Schaltet Systemsymbole des Desktops ein - dieselbe Einstellung wie
    /// "Desktopsymboleinstellungen" (0 = sichtbar). Geschrieben wird in den
    /// Zweig des ANGEMELDETEN Benutzers: Das Tool laeuft mit
    /// Administratorrechten, womoeglich unter einem anderen Konto - dessen
    /// HKEY_CURRENT_USER waere der falsche.
    /// </summary>
    private static bool SystemsymboleEinschalten(IReadOnlyList<string> kennungen, string? sid)
    {
        if (!OperatingSystem.IsWindows() || kennungen.Count == 0) return false;
        const string pfad = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        RegistryKey? zweig = null;
        try
        {
            if (sid is not null) zweig = Registry.Users.OpenSubKey(sid, writable: true);
            using var key = (zweig ?? Registry.CurrentUser).CreateSubKey(pfad, writable: true);
            foreach (var k in kennungen) key.SetValue(k, 0, RegistryValueKind.DWord);
        }
        catch { return false; }
        finally { zweig?.Dispose(); }

        // Dem Explorer Bescheid geben - wie es auch die Einstellungsseite tut.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x1000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

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

    /// <returns>true, wenn das Programm gerade eben installiert wurde.</returns>
    private async Task<bool> InstallApp(string appName, string exeName, string? wingetId)
    {
        if (_cancel.IsCancelled) return false;
        _log(Loc.Tr($"[INFO] Prüfe, ob {appName} installiert ist...", $"[INFO] Checking whether {appName} is installed..."));
        // Fuer alle Benutzer (HKLM) oder - wie Chrome ohne Adminrechte - nur
        // fuer den Benutzer (HKCU) installiert.
        var appPathKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        bool da;
        using (var lm = Registry.LocalMachine.OpenSubKey(appPathKey))
        using (var cu = Registry.CurrentUser.OpenSubKey(appPathKey))
            da = lm != null || cu != null;
        if (da) { _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed.")); return false; }

        // Edge gehoert zu Windows, fuer ihn gibt es kein Paket.
        if (wingetId is null) return false;
        return await InstallViaWinget(appName, wingetId);
    }

    /// <summary>
    /// Schliesst ein Programm vor dem Kopieren seines Profils - und wartet, bis
    /// es wirklich beendet ist. Wurde es gerade erst installiert, wird vorher
    /// kurz abgewartet: Manche Installer (Chrome) starten den Browser nach der
    /// Installation von selbst, ein paar Sekunden spaeter. Lief er dann waehrend
    /// des Kopierens, legte er ein frisches, leeres Profil an.
    /// </summary>
    /// <returns>false, wenn es sich nicht beenden liess.</returns>
    private async Task<bool> ProgrammSchliessen(string name, bool frischInstalliert, params string[] prozesse)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (frischInstalliert)
        {
            _log(Loc.Tr($"[INFO] {name} wurde gerade installiert - manche Installer starten das Programm danach von selbst. Kurz abwarten ...",
                        $"[INFO] {name} has just been installed - some installers start the program afterwards. Waiting briefly ..."));
            for (int i = 0; i < 12 && !Laeuft(prozesse) && !_cancel.IsCancelled; i++) await Task.Delay(1000);
        }
        _log(Loc.Tr($"[INFO] Beende {name}...", $"[INFO] Closing {name}..."));
        return await BeendenUndWarten(TimeSpan.FromSeconds(20), prozesse);
    }

    /// <summary>Laeuft einer dieser Prozesse?</summary>
    private static bool Laeuft(params string[] prozesse)
    {
        foreach (var name in prozesse)
        {
            try
            {
                var liste = System.Diagnostics.Process.GetProcessesByName(name);
                var n = liste.Length;
                foreach (var pr in liste) pr.Dispose();
                if (n > 0) return true;
            }
            catch { /* naechster */ }
        }
        return false;
    }

    /// <summary>
    /// Beendet die Prozesse und wartet, bis keiner mehr laeuft - und es auch
    /// eine Sekunde lang so bleibt (Browser starten Hilfsprozesse nach).
    /// </summary>
    private async Task<bool> BeendenUndWarten(TimeSpan hoechstens, params string[] prozesse)
    {
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            foreach (var name in prozesse) KillProcess(name);
            await Task.Delay(700);
            if (!Laeuft(prozesse))
            {
                await Task.Delay(1000);
                if (!Laeuft(prozesse)) return true;
            }
            if (uhr.Elapsed > hoechstens || _cancel.IsCancelled) return !Laeuft(prozesse);
        }
    }

    /// <returns>true, wenn winget das Programm gerade installiert hat.</returns>
    private async Task<bool> InstallViaWinget(string appName, string wingetId)
    {
        _log(Loc.Tr($"[INFO] Versuche, {appName} via Winget zu installieren...", $"[INFO] Trying to install {appName} via winget..."));
        // Frueher immer "winget" aus dem Suchpfad - an der Pruefung und der
        // Reparatur des Winget-Imports vorbei. Scheiterte winget an seiner
        // Paketquelle, scheiterte die Installation hier stumm mit einer Zahl.
        var r = Winget is not null
            ? await Winget.InstallAsync(wingetId)
            : await _runner.RunAsync("winget", $"install --id {wingetId} -e --accept-package-agreements --accept-source-agreements");

        // Frueher stand hier immer "Installation abgeschlossen" - auch wenn
        // winget gar nicht startete. Bewusst eine WARNUNG und kein FEHLER: Das
        // Profil selbst wird trotzdem zurueckgeschrieben, und darum geht es
        // bei dieser Aktion. Nur muss der Benutzer wissen, dass das Programm
        // noch fehlt.
        if (!r.Started)
            _log(Loc.Tr($"[WARNUNG] winget konnte nicht gestartet werden ({r.StartError}). {appName} bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] winget could not be started ({r.StartError}). Please install {appName} manually - the profile is restored anyway."));
        else if (r.ExitCode is WingetFehler.SchonInstalliert or WingetFehler.SchonAktuell)
            _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed."));
        else if (r.ExitCode != 0)
            _log(Loc.Tr($"[WARNUNG] {appName} ließ sich per winget nicht installieren ({WingetService.Beschreibung(r)}). Bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] {appName} could not be installed via winget ({WingetService.Beschreibung(r)}). Please install it manually - the profile is restored anyway."));
        else
        {
            _log(Loc.Tr($"[INFO] {appName} wurde per winget installiert.", $"[INFO] {appName} was installed via winget."));
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------ Helpers

    private static void KillProcess(string processName)
    {
        try { foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName)) p.Kill(true); } catch { }
    }

    private static string Quote(string s) => $"\"{s.TrimEnd('\\')}\"";
}
