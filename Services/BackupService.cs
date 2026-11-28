using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Backup, restore and update actions of the tool - one method per item
/// of the UI, each one reporting what it did to the log.
/// </summary>
public sealed class BackupService
{
    private readonly ProcessRunner _runner;
    private readonly CancellationTokenState _cancel;
    private readonly Action<string> _log;

    /// <summary>Optional progress callback: number of files reported.</summary>
    public Action<int>? OnFilesProgress { get; set; }

    public string SourcePath { get; set; } = "";
    public string BackupPath { get; set; } = "";

    /// <summary>
    /// Installs programs (Firefox, Chrome ...) while restoring their profiles -
    /// through the same winget launch path and the same repair as the winget
    /// import. Without it: "winget" from the search path.
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

    // ------------------------------------------------------------ User profile

    /// <summary>
    /// Back up the AppData folder as well? Default: no.
    /// </summary>
    public bool IncludeAppData { get; set; }

    /// <summary>
    /// Back up the detected cloud folders as well? Default: no.
    /// </summary>
    public bool IncludeCloud { get; set; }

    /// <summary>
    /// Builds the list of folders excluded from the profile backup (fully
    /// qualified paths). Used by the backup itself and by the storage estimate,
    /// so both agree.
    ///
    /// The decision itself lives in <see cref="ProfileExcludes"/> - free of
    /// dependencies there and therefore testable.
    /// </summary>
    public static List<string> GetProfileExcludes(string userDir, bool includeAppData = false, bool includeCloud = false)
        => ProfileExcludes.Build(userDir, includeAppData, includeCloud);

    public async Task BackupUserProfile()
    {
        var userDir = SourcePath;
        var backupTargetDir = Path.Combine(BackupPath, "Benutzerprofil");

        // This used to be a plain prefix comparison: profile "C:\Users\Max" and
        // target "C:\Users\Maximilian\Backup" counted as "target inside the
        // source folder", and the backup was refused.
        if (ProfileExcludes.IsInside(backupTargetDir, userDir))
        {
            _log(Loc.Tr("[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch.", "[ERROR] Target is inside the source directory. Aborting."));
            return;
        }

        var excluded = GetProfileExcludes(userDir, IncludeAppData, IncludeCloud);

        var args = ProfileBackupArgs(userDir, backupTargetDir);
        foreach (var ex in excluded) if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        // Record in the log what is included - otherwise nobody can tell later
        // why a backup became so large.
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

        // robocopy does not follow folders that actually point to a place outside
        // the profile ("Dokumente\Spiele" -> "D:\Spiele") (/XJ). Their content
        // would otherwise be missing from the backup without a word anywhere.
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
    /// Final step of a backup that includes AppData: summary to the log, the
    /// full list next to the backup (root of the backup folder, NOT inside
    /// "Benutzerprofil" - it would otherwise end up in the profile on restore).
    ///
    /// Returns true if robocopy did report a failure (code 8), but only for
    /// locked or vanished files in AppData. The backup then contains everything
    /// that could be backed up.
    /// </summary>
    private bool ReportAppDataSkips(RobocopySkips skips, int code)
    {
        // Restoring writes its own list - the one from the backup stays.
        var liste = Path.Combine(BackupPath, skips.Zurueck ? RobocopySkips.RestoreListFileName : RobocopySkips.ListFileName);
        if (skips.Count == 0)
        {
            // A list from earlier (same folder, "own folder" off) would now be wrong.
            try { if (File.Exists(liste)) File.Delete(liste); } catch { /* then it stays */ }
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

        // Bit 16 = robocopy itself failed; then nothing was "just skipped".
        return skips.AllExpected && skips.OtherErrors == 0 && (code & 16) == 0;
    }

    public async Task RestoreUserProfile()
    {
        var destDir = SourcePath;
        var backupSourceDir = Path.Combine(BackupPath, "Benutzerprofil");
        if (!Directory.Exists(backupSourceDir)) { _log(Loc.Tr("[FEHLER] Backup-Ordner nicht gefunden.", "[ERROR] Backup folder not found.")); return; }

        _log(Loc.Tr("[INFO] Starte Restore Benutzerprofil...", "[INFO] Starting user profile restore..."));
        var args = ProfileRestoreArgs(backupSourceDir, destDir);

        // The same two switches apply here - otherwise they would have no effect
        // on restore: robocopy copies the whole backup folder with /E. If the
        // backup contained AppData, it would land on disk without these excludes
        // even when the box is explicitly NOT checked - and the confirmation
        // before it would be an empty gesture.
        //
        // Excludes are relative to the BACKUP FOLDER, not to the profile: /XD
        // compares against the source.
        //
        // Cloud folders are ALWAYS left out of this run - even when checked.
        // When checked, they follow one by one afterwards, but not to their old
        // place.
        foreach (var ex in GetProfileExcludes(backupSourceDir, IncludeAppData, includeCloud: false))
            if (Directory.Exists(ex)) { args.Add("/XD"); args.Add(Quote(ex)); }

        RobocopySkips? skips = null;
        // If the tool runs from inside the target profile (usually from the
        // desktop), its own file is not overwritten - it is locked, and the
        // backup usually holds an older version.
        var selbst = ProfileExcludes.RunningProgramInBackup(destDir, backupSourceDir, Environment.ProcessPath);
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
            // Summarize locked files in AppData - robocopy names the path in the
            // backup, not in the profile.
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
    /// Restores the cloud folders from the backup - each one NEXT TO its old
    /// place: "OneDrive" goes to "OneDrive_wiederhergestellt". The reason is
    /// explained at <see cref="CloudProviders.RestoredFolderName"/>.
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

    // ------------------------------------------------------------ AppData / cloud without the profile
    //
    // AppData and cloud folders can also be backed up on their own, without
    // the rest of the profile. They land in THE SAME place as with the profile
    // backup (Benutzerprofil\AppData, Benutzerprofil\<cloud folder>) - so any
    // later restore finds them, no matter how they were backed up.

    /// <summary>Common robocopy switches for backing up from the profile.</summary>
    private List<string> ProfileBackupArgs(string quelle, string ziel)
    {
        var args = new List<string> { Quote(quelle), Quote(ziel) };
        args.AddRange(ProfileExcludes.BackupSwitches());
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        return args;
    }

    /// <summary>Common robocopy switches for restoring into the profile.</summary>
    private List<string> ProfileRestoreArgs(string quelle, string ziel)
    {
        var args = new List<string> { Quote(quelle), Quote(ziel) };
        args.AddRange(ProfileExcludes.RestoreSwitches());
        if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
        return args;
    }

    /// <summary>Backs up only AppData - the caches stay out as always.</summary>
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
        // The same list as for the profile backup, only the part inside AppData.
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

    /// <summary>Backs up only the detected cloud folders, each on its own.</summary>
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

    /// <summary>Restores only AppData - from the backup, however it was created.</summary>
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
        // The same excludes as for the profile: caches and the machine-bound
        // state of winget and the Windows shell - even if an older backup still
        // contains them.
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
    /// Before restoring AppData, closes the browsers and mail programs from
    /// the catalog that are running.
    ///
    /// After sign-in, Edge usually keeps running invisibly in the background
    /// ("startup boost"). Its files are locked then: part of the profile came
    /// from the backup, the rest stayed the old one - and every locked file
    /// took three lines in the log. The single browser items have always
    /// worked this way.
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
        await KillAndWait(TimeSpan.FromSeconds(20), geschlossen.Select(g => g.Prozess).ToArray());
    }

    /// <summary>Restores only the cloud folders - as always, next to the original.</summary>
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

    // ------------------------------------------------------------ Browser profiles

    /// <summary>Backs up the profile of a catalog program (Firefox, Edge, Chrome, Brave, Thunderbird).</summary>
    public async Task BackupAppProfile(AppEintrag app)
    {
        var quelle = AppCatalog.PathIn(SourcePath, app.ProfilRel);
        if (!Directory.Exists(quelle)) { _log(Loc.Tr($"[FEHLER] Pfad nicht gefunden: {quelle}", $"[ERROR] Path not found: {quelle}")); return; }

        if (!await CloseProgram(app.Name, false, app.Prozess))
            _log(Loc.Tr($"[WARNUNG] {app.Name} läuft noch - gesichert wird trotzdem, einzelne Dateien können aber unvollständig sein.",
                        $"[WARNING] {app.Name} is still running - backing up anyway, but individual files may be incomplete."));

        await MirrorAppProfile(app.Name, quelle, Path.Combine(BackupPath, app.BackupOrdner), app.Chromium ? ChromiumCacheExcludes : null);
        if (app.Punkt == Punkt.Thunderbird && !_cancel.IsCancelled) await BackupThunderbirdExternal(app.Name, quelle);
    }

    /// <summary>Restores the profile of a catalog program - installing the program first if needed.</summary>
    public async Task RestoreAppProfile(AppEintrag app)
    {
        var mozilla = MozillaProfile.ProgramOf(app);
        var frisch = await InstallApp(app.Name, app.Prozess + ".exe", app.WingetId,
            mozilla is { } m ? () => MozillaInstallationsHere(m.Exe, m.Ordner).Count > 0 : null);

        var quelle = Path.Combine(BackupPath, app.BackupOrdner);
        if (!Directory.Exists(quelle)) { _log(Loc.Tr($"[FEHLER] {app.Name} Backup nicht gefunden.", $"[ERROR] {app.Name} backup not found.")); return; }

        if (!await CloseProgram(app.Name, frisch, app.Prozess))
        {
            _log(Loc.Tr($"[FEHLER] {app.Name} läuft noch und ließ sich nicht beenden - das Profil wird NICHT zurückgeschrieben, sonst überschriebe {app.Name} es beim Beenden wieder. "
                        + $"Bitte {app.Name} vollständig schließen (auch im Infobereich) und den Punkt erneut ausführen.",
                        $"[ERROR] {app.Name} is still running and could not be closed - the profile is NOT restored, {app.Name} would overwrite it again on exit. "
                        + $"Please close {app.Name} completely (including the notification area) and run this item again."));
            return;
        }

        var ziel = AppCatalog.PathIn(SourcePath, app.ProfilRel);
        if (await RestoreAppDir(app.Name, quelle, ziel, app.Prozess) && MozillaProfile.ProgramOf(app) is { } moz)
        {
            RewriteMozillaPaths(app.Name, ziel);
            if (app.Punkt == Punkt.Thunderbird) await RestoreThunderbirdExternal(app.Name, ziel);
            SetMozillaDefaultProfile(app.Name, ziel, moz.Exe, moz.Ordner);
        }
    }

    // ------------------------------------------------------------ Firefox/Thunderbird: what ties a profile to the PC

    /// <summary>
    /// Rewrites the old user's paths in prefs.js (mail folders, download
    /// folder, pages from the profile) to the selected profile - like
    /// MozBackup, which adjusts the mail folders in prefs.js on restore.
    /// </summary>
    private void RewriteMozillaPaths(string name, string datenOrdner)
    {
        foreach (var profil in MozillaProfile.ProfileFolders(datenOrdner))
        {
            try
            {
                var alt = MozillaProfile.OldUserFolder(profil);
                if (alt is null || ProfileExcludes.SameFolder(alt, SourcePath)) continue;
                var n = MozillaProfile.RewritePaths(profil, alt, SourcePath);
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
    /// Will Firefox/Thunderbird reject the profile because it last ran with a
    /// newer major version? Then they create a new, empty profile ("You have
    /// launched an older version"). Typical causes: ESR is installed here
    /// instead of the regular edition, or winget delivers the new major version
    /// only days after the update on the old PC. Firefox only compares the
    /// major version (146.0.1 against 146.0 is fine - verified in CI with the
    /// real Firefox). Resetting does not help: the data may already be in a
    /// newer format, e.g. Firefox recreated the bookmarks. So: report it, with
    /// a remedy.
    /// </summary>
    private void CheckMozillaVersion(string name, string profilOrdner, IEnumerable<string> installationen)
    {
        if (MozillaProfile.ProfileVersion(profilOrdner) is not { } profil) return;
        var hier = installationen.Select(MozillaProfile.InstalledVersion).OfType<(string Version, string BuildId)>().ToList();
        if (hier.Count == 0 || hier.Any(i => MozillaProfile.Compare(profil, i) != MozillaProfile.Abstand.VielNeuer)) return;
        var fassungen = string.Join(", ", hier.Select(i => i.Version));
        _log(Loc.Tr($"[FEHLER] {name}: Das Profil lief zuletzt mit {profil.Version}, hier ist erst {fassungen} installiert. {name} lehnt ein Profil einer neueren Hauptversion ab und legt ein neues, leeres an. "
                    + $"Bitte {name} aktualisieren (Hilfe > Über {name}, oder winget upgrade) - danach nimmt es das Profil.",
                    $"[ERROR] {name}: the profile last ran with {profil.Version}, only {fassungen} is installed here. {name} refuses a profile of a newer major version and creates a new, empty one. "
                    + $"Please update {name} (Help > About {name}, or winget upgrade) - then it takes the profile."));
    }

    /// <summary>Thunderbird accounts whose mail lives outside the profile: in the backup and on restore.</summary>
    public const string ThunderbirdExternOrdner = "Thunderbird-Extern";

    private sealed class ExterneSicherung
    {
        public string Benutzerordner { get; set; } = "";
        public List<ExternesPostfach> Postfaecher { get; set; } = new();
    }

    private sealed class ExternesPostfach
    {
        /// <summary>Profile folder relative to the data folder ("Profiles\\abcd.default").</summary>
        public string Profil { get; set; } = "";
        public string Server { get; set; } = "";
        public string Ordner { get; set; } = "";
        /// <summary>Subfolder in Thunderbird-Extern.</summary>
        public string Sicherung { get; set; } = "";
    }

    /// <summary>
    /// Backs up the mail of accounts whose folder Thunderbird keeps outside
    /// the profile (Account Settings > Server Settings > Local Directory, e.g.
    /// D:\\Mail). Such mail used to be missing from the backup - MozBackup
    /// includes it. If it lives inside the user profile, the user profile item
    /// already backs it up.
    /// </summary>
    private async Task BackupThunderbirdExternal(string name, string datenOrdner)
    {
        var liste = new ExterneSicherung { Benutzerordner = SourcePath };
        var ablage = Path.Combine(BackupPath, ThunderbirdExternOrdner);
        foreach (var profil in MozillaProfile.ProfileFolders(datenOrdner))
        {
            foreach (var p in MozillaProfile.MailFoldersOutside(profil))
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
    /// Restores the mail of accounts outside the profile: to its old place
    /// (inside the old PC's user folder: into the selected profile's), or - if
    /// that drive does not exist here - into the profile folder; prefs.js is
    /// then adjusted.
    /// </summary>
    private async Task RestoreThunderbirdExternal(string name, string datenOrdner)
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
            if (verlegt && !MozillaProfile.MoveMailFolder(profil, p.Server, ziel))
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
    /// Firefox/Thunderbird: register the restored profile as the default of
    /// this installation. Otherwise a Firefox installed in a different folder
    /// than on the old PC (64 instead of 32 bit, for everyone instead of just
    /// the user) picks a new, empty profile.
    /// </summary>
    private void SetMozillaDefaultProfile(string name, string datenOrdner, string exe, string ordnerName)
    {
        var installationen = MozillaInstallationsHere(exe, ordnerName);
        MozillaProfile.Ergebnis erg;
        try { erg = MozillaProfile.Adjust(datenOrdner, installationen); }
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
        var alt = erg.AlteSchluessel.Select(k => MozillaProfile.KnownLocation(k, ordnerName, SourcePath) ?? k).ToList();
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
        if (erg.Verzeichnis is not null) CheckMozillaVersion(name, erg.Verzeichnis, erg.Neu.Select(n => n.Ordner));
    }

    /// <summary>Tests only: installation folders instead of the real search.</summary>
    internal List<string>? TestInstallationen { get; set; }

    /// <summary>The installation folders of Firefox/Thunderbird on this PC (fixed in the tests).</summary>
    private List<string> MozillaInstallationsHere(string exe, string ordnerName)
        => TestInstallationen ?? (OperatingSystem.IsWindows() ? MozillaProfile.Installations(exe, ordnerName, SourcePath) : new List<string>());

    // ------------------------------------------------------------ Opera (all editions)

    /// <summary>
    /// Backs up every Opera edition of the user (Opera, Opera GX, Beta ...) into
    /// its own subfolder of "Opera-Profil".
    /// </summary>
    public async Task BackupOpera()
    {
        var wurzel = AppCatalog.PathIn(SourcePath, AppCatalog.Opera.Rel);
        var varianten = AppCatalog.Opera.Variants(wurzel);
        if (varianten.Count == 0)
        {
            _log(Loc.Tr($"[FEHLER] Kein Opera-Profil gefunden unter: {wurzel}", $"[ERROR] No Opera profile found under: {wurzel}"));
            return;
        }
        var namen = string.Join(", ", varianten.Select(AppCatalog.Opera.DisplayName));
        _log(Loc.Tr($"[INFO] Gefundene Opera-Fassungen: {namen}", $"[INFO] Opera editions found: {namen}"));

        // All editions run as opera.exe - closing it once is enough.
        if (!await CloseProgram("Opera", false, OperaProzesse))
            _log(Loc.Tr("[WARNUNG] Opera läuft noch - gesichert wird trotzdem, einzelne Dateien können aber unvollständig sein.",
                        "[WARNING] Opera is still running - backing up anyway, but individual files may be incomplete."));

        foreach (var v in varianten)
        {
            if (_cancel.IsCancelled) return;
            await MirrorAppProfile(AppCatalog.Opera.DisplayName(v), Path.Combine(wurzel, v),
                                   Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner, v), ChromiumCacheExcludes);
        }
    }

    /// <summary>Restores every backed-up Opera edition, each to its own place.</summary>
    public async Task RestoreOpera()
    {
        var quelle = Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner);
        var varianten = AppCatalog.Opera.Variants(quelle);
        if (varianten.Count == 0)
        {
            _log(Loc.Tr("[FEHLER] Opera Backup nicht gefunden.", "[ERROR] Opera backup not found."));
            return;
        }
        var ziel = AppCatalog.PathIn(SourcePath, AppCatalog.Opera.Rel);
        foreach (var v in varianten)
        {
            if (_cancel.IsCancelled) return;
            var name = AppCatalog.Opera.DisplayName(v);
            var frisch = await InstallOpera(v);
            if (!await CloseProgram(name, frisch, OperaProzesse))
            {
                _log(Loc.Tr($"[FEHLER] {name} läuft noch und ließ sich nicht beenden - das Profil wird NICHT zurückgeschrieben. Bitte Opera vollständig schließen und den Punkt erneut ausführen.",
                            $"[ERROR] {name} is still running and could not be closed - the profile is NOT restored. Please close Opera completely and run this item again."));
                continue;
            }
            await RestoreAppDir(name, Path.Combine(quelle, v), Path.Combine(ziel, v), OperaProzesse);
        }
    }

    /// <summary>Opera itself and its helpers that keep files in the profile open.</summary>
    private static readonly string[] OperaProzesse = { AppCatalog.Opera.Prozess, "opera_autoupdate", "browser_assistant" };

    /// <summary>
    /// Is this Opera edition installed? Opera usually installs for the user
    /// only (AppData\Local\Programs\&lt;edition&gt;), less often for everyone
    /// (Program Files). Otherwise via winget - if the package is known.
    /// </summary>
    /// <returns>true if the edition was installed just now.</returns>
    private async Task<bool> InstallOpera(string variante)
    {
        if (_cancel.IsCancelled) return false;
        var name = AppCatalog.Opera.DisplayName(variante);
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

    // ------------------------------------------------------------ Program profiles: shared flow

    /// <summary>Mirrors a program profile into the backup.</summary>
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
    /// Restores a program profile. An existing profile is moved aside first
    /// ("..._alt_&lt;time&gt;"), not deleted.
    /// </summary>
    /// <param name="prozesse">
    /// The program's processes. If one of them is running again after copying,
    /// it is closed and the profile copied once more - otherwise it would
    /// overwrite the restored profile when it exits.
    /// </param>
    /// <returns>true if the profile is completely in place.</returns>
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
                // The existing profile used to be DELETED recursively here. That fails for
                // the same reason as moving it - a file that is still locked - but only
                // after part of it is already gone. What remained was a half-deleted
                // profile, and if the restore failed afterwards, there was none at all.
                // Aborting is the only answer here that cannot break anything.
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

        // If the program started while copying (installer, autostart, user), it
        // holds a fresh profile of its own open.
        if (prozesse.Length > 0 && OperatingSystem.IsWindows() && IsRunning(prozesse))
        {
            _log(Loc.Tr($"[WARNUNG] {name} wurde während des Kopierens gestartet - wird beendet, und das Profil wird noch einmal zurückgeschrieben.",
                        $"[WARNING] {name} was started while copying - it is closed and the profile is restored once more."));
            if (await KillAndWait(TimeSpan.FromSeconds(20), prozesse))
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

        // Are the files the profile depends on exactly as backed up?
        var abweichend = ProfileComparison.Differences(quelle, ziel);
        if (abweichend.Count > 0)
        {
            _log(Loc.Tr($"[FEHLER] {name}: Das Profil steht nicht vollständig im Ziel - abweichend: {string.Join(", ", abweichend.Take(8))}{(abweichend.Count > 8 ? ", ..." : "")}. "
                        + $"Meist lief {name} dabei. Bitte {name} vollständig schließen (auch im Infobereich) und den Punkt erneut ausführen.",
                        $"[ERROR] {name}: the profile is not complete in the target - differing: {string.Join(", ", abweichend.Take(8))}{(abweichend.Count > 8 ? ", ..." : "")}. "
                        + $"Usually {name} was running meanwhile. Please close {name} completely (including the notification area) and run this item again."));
            return false;
        }

        var profile = ProfileComparison.Profiles(ziel);
        _log(profile.Count > 0
            ? Loc.Tr($"[ERFOLG] {name} Profil wiederhergestellt ({string.Join(", ", profile)}).", $"[SUCCESS] {name} profile restored ({string.Join(", ", profile)}).")
            : Loc.Tr($"[ERFOLG] {name} Profil wiederhergestellt.", $"[SUCCESS] {name} profile restored."));
        return true;
    }

    // ------------------------------------------------------------ Folders under C:\

    /// <summary>
    /// Backs up the selected folders under C:\ to "C-Ordner\&lt;name&gt;" -
    /// mirrored like the program profiles. robocopy does not follow junctions
    /// (/XJ); if the backup target lies inside one of the folders, it is left
    /// out.
    /// </summary>
    public async Task BackupDriveFolders(IReadOnlyList<string> ordner)
    {
        if (ordner.Count == 0)
        {
            _log(Loc.Tr("[INFO] Keine Ordner gewählt - nichts zu sichern.", "[INFO] No folders selected - nothing to back up."));
            return;
        }
        var ziel = Path.Combine(BackupPath, DriveFolders.BackupOrdner);
        Directory.CreateDirectory(ziel);
        int fehler = 0;
        foreach (var quelle in ordner)
        {
            if (_cancel.IsCancelled) return;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(quelle));
            if (!Directory.Exists(quelle))
            {
                _log(Loc.Tr($"[WARNUNG] {quelle} gibt es nicht (mehr) - übersprungen.", $"[WARNING] {quelle} does not exist (any more) - skipped."));
                continue;
            }
            var args = new List<string> { Quote(quelle), Quote(Path.Combine(ziel, name)), "/MIR", "/XJ", "/R:1", "/W:1", "/MT:32" };
            if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
            if (DriveFolders.Contains(quelle, BackupPath)) { args.Add("/XD"); args.Add(Quote(BackupPath)); }

            _log(Loc.Tr($"[INFO] Sichere {quelle} ...", $"[INFO] Backing up {quelle} ..."));
            var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
            if (_cancel.IsCancelled) return;
            if (code < 8) _log(Loc.Tr($"[ERFOLG] {quelle} gesichert.", $"[SUCCESS] {quelle} backed up."));
            else { fehler++; _log(Loc.Tr($"[FEHLER] {quelle}: ExitCode {code}", $"[ERROR] {quelle}: ExitCode {code}")); }
        }
        if (fehler == 0 && ordner.Count > 1)
            _log(Loc.Tr($"[ERFOLG] {ordner.Count} Ordner von {DriveFolders.Wurzel} gesichert.", $"[SUCCESS] {ordner.Count} folders from {DriveFolders.Wurzel} backed up."));
    }

    /// <summary>
    /// Restores the selected folders to C:\&lt;name&gt;. Only adds (/E, not
    /// /MIR): whatever is already there stays - and if a file there is newer
    /// than in the backup, it stays as well (/XO).
    /// </summary>
    public async Task RestoreDriveFolders(IReadOnlyList<string> namen)
    {
        var quelle = Path.Combine(BackupPath, DriveFolders.BackupOrdner);
        if (!Directory.Exists(quelle))
        {
            _log(Loc.Tr($"[FEHLER] Keine Ordner von {DriveFolders.Wurzel} in der Sicherung gefunden.", $"[ERROR] No folders from {DriveFolders.Wurzel} found in the backup."));
            return;
        }
        if (namen.Count == 0)
        {
            _log(Loc.Tr("[INFO] Keine Ordner gewählt - nichts wiederherzustellen.", "[INFO] No folders selected - nothing to restore."));
            return;
        }
        foreach (var name in namen)
        {
            if (_cancel.IsCancelled) return;
            var von = Path.Combine(quelle, name);
            var nach = Path.Combine(DriveFolders.Wurzel, name);
            if (!Directory.Exists(von))
            {
                _log(Loc.Tr($"[WARNUNG] {name} liegt nicht in der Sicherung - übersprungen.", $"[WARNING] {name} is not in the backup - skipped."));
                continue;
            }
            _log(Directory.Exists(nach)
                ? Loc.Tr($"[INFO] {nach} gibt es schon - wird ergänzt; neuere Dateien dort bleiben.", $"[INFO] {nach} already exists - it is added to; newer files there are kept.")
                : Loc.Tr($"[INFO] Stelle {nach} wieder her ...", $"[INFO] Restoring {nach} ..."));
            var args = new List<string> { Quote(von), Quote(nach), "/E", "/XO", "/R:1", "/W:1", "/MT:32" };
            if (_cancel.FastMode) args.AddRange(new[] { "/NP", "/NDL" });
            var code = await _runner.RunRobocopyAsync(string.Join(" ", args), _log, OnFilesProgress);
            if (_cancel.IsCancelled) return;
            _log(code < 8 ? Loc.Tr($"[ERFOLG] {nach} wiederhergestellt.", $"[SUCCESS] {nach} restored.")
                          : Loc.Tr($"[FEHLER] {nach}: ExitCode {code}", $"[ERROR] {nach}: ExitCode {code}"));
        }
    }

    // ------------------------------------------------------------ Wi-Fi

    public async Task ExportWlan()
    {
        var destDir = Path.Combine(BackupPath, "WLAN-Profile");
        Directory.CreateDirectory(destDir);
        _log(Loc.Tr("[INFO] Exportiere WLAN Profile...", "[INFO] Exporting WiFi profiles..."));
        var r = await _runner.RunAsync("netsh", $"wlan export profile folder=\"{destDir}\" key=clear",
                                       SystemHelpers.ConsoleEncoding);

        // This used to say "[ERFOLG]" every time - even if netsh did not run at
        // all or refused. Typical on a PC without a Wi-Fi adapter: "The Wireless
        // AutoConfig Service (wlansvc) is not running."
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

    // ------------------------------------------------------------ Printers

    /// <summary>
    /// Finds the path to PrintBrm.exe (Print Backup Recovery Migration). It is
    /// Microsoft's official printer migration tool and saves queues, drivers,
    /// ports and settings into ONE file.
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
    /// The error PrintBrm names in its output - null if none.
    ///
    /// PrintBrm exits with code 0 even when it fails, e.g. on an unusable
    /// backup file: "The following error occurred: 0x80070057. / The parameter
    /// is incorrect." (verified on Windows). The error code (HRESULT 0x8...)
    /// gives it away - in every language.
    /// </summary>
    internal static string? PrintBrmError(string? ausgabe)
    {
        if (string.IsNullOrWhiteSpace(ausgabe)) return null;
        var zeilen = ausgabe.Split('\n').Select(z => z.Trim()).Where(z => z.Length > 0).ToList();
        var i = zeilen.FindIndex(z => System.Text.RegularExpressions.Regex.IsMatch(z, @"\b0x8[0-9A-Fa-f]{7}\b"));
        // The line with the code and the explanation after it.
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

        // PrintBrm does not reliably cope with paths containing special characters
        // (e.g. '#') or with network/removable drives (error 0x8007007b). So back
        // up to a short temp path first and copy to the target afterwards.
        var tmpDir = Path.GetTempPath();
        var tmpName = "WDST_Drucker.printerExport";
        var tmpFile = Path.Combine(tmpDir, tmpName);
        try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* ignore */ }

        _log(Loc.Tr("[INFO] Sichere Drucker, Treiber und Ports (das kann einen Moment dauern)...",
                    "[INFO] Backing up printers, drivers and ports (this may take a moment)..."));

        // PrintBrm is extremely picky about the path argument (error 0x8007007b).
        // What works reliably: run it with the temp folder as working directory
        // and pass only the BARE file name without a path to -F.
        var r = await _runner.RunAsync(brm, $"-B -F {tmpName}",
                                       SystemHelpers.ConsoleEncoding, 600000, tmpDir);

        if (!r.Started)
        {
            _log(Loc.Tr($"[FEHLER] PrintBrm konnte nicht gestartet werden: {r.StartError}",
                        $"[ERROR] PrintBrm could not be started: {r.StartError}"));
            return;
        }

        // Success is judged by the temp file existing (PrintBrm does not always
        // return a clean exit code).
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

        // PrintBrm copes badly with paths containing special characters: copy to temp first.
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

        // -R = restore, -O FORCE = overwrite. In the temp folder with the bare file name.
        var r = await _runner.RunAsync(brm, $"-R -F {tmpName} -O FORCE",
                                       SystemHelpers.ConsoleEncoding, 900000, tmpDir);

        try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* ignore */ }

        if (!r.Started)
        {
            _log(Loc.Tr($"[FEHLER] PrintBrm konnte nicht gestartet werden: {r.StartError}",
                        $"[ERROR] PrintBrm could not be started: {r.StartError}"));
            return;
        }

        // PrintBrm exits with 0 on some failures as well - e.g. with an unusable
        // backup file. This used to say "[ERFOLG]" then.
        var fehler = PrintBrmError(r.Output);
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

    // ------------------------------------------------------------ Wallpaper

    // Remembers the image's original path as well ...
    private const string WallpaperPathFile = "Hintergrundbild-Pfad.txt";
    // ... and the backed-up profile, so the path can be carried over when
    // restoring into a DIFFERENT profile (owner -> user).
    // A separate file, so older versions can still read the path file.
    private const string WallpaperProfileFile = "Hintergrundbild-Profil.txt";

    /// <summary>
    /// Backs up the wallpaper into the "Design" folder: an image (the file next
    /// to it) or a solid color. null if there is nothing fixed to back up
    /// (slideshow, Spotlight, image gone) - the log says so.
    /// </summary>
    private async Task<HintergrundEinstellung?> BackupWallpaper(string ordner, DesignZiel ziel)
    {
        var stand = UserWallpaper.Read(ziel.Wurzel);
        var src = stand.Bild;
        string? farbeText = stand.Farbe, stil = stand.Stil, kacheln = stand.Kacheln;
        var art = Wallpaper.Determine(src, src is not null && File.Exists(src), stand.Typ, farbeText);

        // The registered image was deleted, but Windows still shows it - from its
        // cache. Then back that up.
        if (art == HintergrundArt.Unbekannt && src is not null && !File.Exists(src)
            && Wallpaper.Cache(SourcePath) is { } zwischen && File.Exists(zwischen))
        {
            _log(Loc.Tr($"[INFO] Das eingetragene Hintergrundbild gibt es nicht mehr ({src}) - gesichert wird das Bild, das Windows gerade zeigt.",
                        $"[INFO] The configured wallpaper no longer exists ({src}) - the image Windows currently shows is backed up."));
            src = zwischen;
            art = HintergrundArt.Bild;
        }

        // Solid color: there is no image - the color is backed up.
        if (art == HintergrundArt.Volltonfarbe && Wallpaper.ReadColor(farbeText) is { } farbe)
        {
            _log(Loc.Tr($"[ERFOLG] Volltonfarbe als Hintergrund gesichert: RGB {farbe.R}, {farbe.G}, {farbe.B} ({Wallpaper.Hex(farbe)}).",
                        $"[SUCCESS] Solid color background backed up: RGB {farbe.R}, {farbe.G}, {farbe.B} ({Wallpaper.Hex(farbe)})."));
            return new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = Wallpaper.ColorText(farbe) };
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

        // The image under its original name next to Design.json - Windows' cache
        // ("TranscodedWallpaper", without extension) as "Hintergrundbild.jpg".
        var fileName = Wallpaper.BackupName(src, ReadHeader(src));
        if (Wallpaper.IsWindowsCache(src))
            _log(Loc.Tr($"[INFO] Der Hintergrund liegt nur in Windows' Zwischenspeicher ({src}) - gesichert als {fileName}.",
                        $"[INFO] The wallpaper only exists in Windows' cache ({src}) - backed up as {fileName}."));
        if (string.Equals(fileName, DesignSettings.DateiName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, DesignSettings.AlterDateiName, StringComparison.OrdinalIgnoreCase))
            fileName = "Hintergrund-" + fileName;
        try
        {
            Directory.CreateDirectory(ordner);
            await Task.Run(() => File.Copy(src, Path.Combine(ordner, fileName), overwrite: true));
            _log(Loc.Tr($"[ERFOLG] Hintergrundbild gesichert. Originalpfad: {src}",
                        $"[SUCCESS] Wallpaper backed up. Original path: {src}"));
            // Plus fit mode and color: with "Fit" or "Center" the color shows at the
            // edges.
            return new HintergrundEinstellung
            {
                Art = "Bild",
                Bild = fileName,
                BildPfad = src,
                Profil = SourcePath,
                Farbe = Wallpaper.ReadColor(farbeText) is { } rand ? Wallpaper.ColorText(rand) : null,
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
    /// Reads the wallpaper from a backup in the old format: folder
    /// "Hintergrundbild" with the image, Hintergrundbild-Pfad.txt,
    /// Hintergrundbild-Profil.txt and possibly Hintergrund.json (solid color,
    /// fit mode). It is applied the same way as the new format - through the
    /// Windows theme.
    /// </summary>
    /// <returns>The setting (kind, color, fit mode, original path, profile) and the backed-up image - (null, null) if nothing usable is there.</returns>
    private async Task<(HintergrundEinstellung? Einstellung, string? Bild)> ReadLegacyBackup()
    {
        var srcDir = Path.Combine(BackupPath, "Hintergrundbild");
        var pathFile = Path.Combine(srcDir, WallpaperPathFile);

        if (!Directory.Exists(srcDir))
        {
            _log(Loc.Tr("[FEHLER] Keine Hintergrundbild-Sicherung gefunden.",
                        "[ERROR] No wallpaper backup found."));
            return (null, null);
        }

        // Solid color instead of an image?
        var einstellung = Wallpaper.Load(srcDir);
        if (Wallpaper.IsSolidColor(einstellung)) return (einstellung, null);

        // Read the original path and the backed-up profile (if present).
        string? originalPath = null, originalProfile = null;
        try { if (File.Exists(pathFile)) originalPath = (await File.ReadAllTextAsync(pathFile)).Trim(); }
        catch { /* ignore */ }
        try
        {
            var profilDatei = Path.Combine(srcDir, WallpaperProfileFile);
            if (File.Exists(profilDatei)) originalProfile = (await File.ReadAllTextAsync(profilDatei)).Trim();
        }
        catch { /* older backup - then the rule for C:\Users\<name> applies */ }

        // Find the backed-up image in the backup folder. It is stored under its
        // original name - after several backups with changing wallpapers, several
        // images sit side by side. This spot used to take "the first file", i.e.
        // any one of them. Now it takes the image whose name matches the
        // remembered original path, otherwise the most recently backed-up one.
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

    /// <summary>Sets it as the wallpaper - registry, session, theme file - including fit mode and edge color.</summary>
    private void SetImageDirectly(DesignZiel ziel, string pfad, HintergrundEinstellung? einstellung)
    {
        try
        {
            var r = UserWallpaper.SetImage(ziel.Wurzel, ziel.EigenerBenutzer, pfad, einstellung, SourcePath);
            WallpaperResult(ziel, r, Loc.Tr("Hintergrundbild", "Wallpaper"));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Hintergrundbild konnte nicht gesetzt werden: {ex.Message}",
                        $"[ERROR] Wallpaper could not be set: {ex.Message}"));
        }
    }

    /// <summary>
    /// Puts the image from the backup into the selected profile. Returns the
    /// path - null if copying failed (the log says so).
    /// </summary>
    private async Task<string?> ProvideWallpaperImage(string backupImage, string? originalPath, string? originalProfile)
    {
        // Determine the target path - ALWAYS inside the selected target profile.
        //
        // The stored original path used to be used as is. On a computer with a
        // different user name (backed up: Besitzer, restored to: Anwender) that
        // created a folder "C:\Users\Besitzer" - a profile that does not exist
        // there. And without a stored path, the image went into the "Bilder"
        // folder of whoever runs the tool, not of the target profile.
        //
        // If it was in Windows' cache (Themes\TranscodedWallpaper, CachedFiles):
        // NOT back there. Windows manages those files itself - registered as the
        // wallpaper, it overwrites or deletes them, and after the next restart the
        // desktop is black.
        string targetPath;
        bool zwischenspeicher = Wallpaper.IsWindowsCache(originalPath);
        var imZielprofil = zwischenspeicher ? null : ProfilePaths.MapIntoProfile(originalPath, originalProfile, SourcePath);
        if (imZielprofil is not null)
        {
            targetPath = imZielprofil;
        }
        else if (!zwischenspeicher && !string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath)
                 && FilesAreEqual(backupImage, originalPath))
        {
            // A Windows wallpaper outside any profile (C:\Windows\Web\...) that is
            // already here exactly like that: just set it, copy nothing.
            targetPath = originalPath;
        }
        else
        {
            targetPath = Path.Combine(SourcePath, "Pictures", Wallpaper.BackupName(backupImage, ReadHeader(backupImage)));
        }
        if (!string.IsNullOrWhiteSpace(originalPath)
            && !string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            _log(Loc.Tr($"[INFO] Gesichert war das Bild unter {originalPath} - es kommt ins gewählte Profil: {targetPath}",
                        $"[INFO] The image was backed up from {originalPath} - it goes into the selected profile: {targetPath}"));

        try
        {
            // If the image is already at the target path: do NOT copy, just set it.
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
    /// Sets a solid color as the wallpaper - like Settings > Personalization >
    /// Background > Solid color: remove the image, set the color for the
    /// running session and permanently for the signed-in user.
    /// </summary>
    private void SetSolidColor(DesignZiel ziel, (byte R, byte G, byte B) farbe)
    {
        try
        {
            var r = UserWallpaper.SetColor(ziel.Wurzel, ziel.EigenerBenutzer, farbe, SourcePath);
            WallpaperResult(ziel, r, Loc.Tr($"Volltonfarbe RGB {farbe.R}, {farbe.G}, {farbe.B} ({Wallpaper.Hex(farbe)})",
                                                $"Solid color RGB {farbe.R}, {farbe.G}, {farbe.B} ({Wallpaper.Hex(farbe)})"));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Volltonfarbe konnte nicht gesetzt werden: {ex.Message}",
                        $"[ERROR] The solid color could not be set: {ex.Message}"));
        }
    }

    /// <summary>What setting the wallpaper resulted in - for the log.</summary>
    private void WallpaperResult(DesignZiel ziel, HintergrundGesetzt r, string was)
    {
        if (r.VirtuelleDesktops > 0)
            _log(Loc.Tr($"[INFO] {r.VirtuelleDesktops} virtuelle(r) Desktop(s) hatte(n) einen eigenen Hintergrund - angeglichen.",
                        $"[INFO] {r.VirtuelleDesktops} virtual desktop(s) had their own background - adjusted."));
        LogThemeFile(r.DesignDatei);
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

    /// <summary>What happened to Windows' theme file - for the log.</summary>
    private void LogThemeFile(DesignDateiErgebnis? d)
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

    /// <summary>The first bytes of a file - to tell the image type. Empty if it cannot be read.</summary>
    private static byte[] ReadHeader(string pfad)
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
    /// "Back up design": the wallpaper (image or solid color) and Windows'
    /// design settings - light/dark, accent color, transparency, mouse
    /// pointer, taskbar, Start. All in ONE folder "Design": Design.json and, for
    /// an image, the image file next to it.
    /// </summary>
    public async Task ExportDesign()
    {
        var ordner = Path.Combine(BackupPath, DesignSettings.Ordner);
        using var ziel = DesignKey(schreiben: false);
        if (ziel is null) return;
        var hintergrund = await BackupWallpaper(ordner, ziel);
        if (_cancel.IsCancelled) return;
        var werte = ReadDesignSettings(ziel);
        if (hintergrund is null && (werte is null || werte.Count == 0)) return;
        try
        {
            DesignSettings.Save(BackupPath, new DesignSicherung
            {
                Rechner = Environment.MachineName,
                Erstellt = DateTime.Now,
                Hintergrund = hintergrund,
                Werte = werte ?? new List<DesignEintrag>(),
            });
            CleanUpDesignFolder(ordner, hintergrund?.Bild);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] {DesignSettings.DateiName} konnte nicht geschrieben werden: {ex.Message}",
                        $"[ERROR] {DesignSettings.DateiName} could not be written: {ex.Message}"));
        }
    }

    /// <summary>
    /// When backing up into an existing backup folder again, an earlier image
    /// would otherwise sit next to the new one - and the interim version's file
    /// next to Design.json.
    /// </summary>
    private static void CleanUpDesignFolder(string ordner, string? bild)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ordner))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, DesignSettings.DateiName, StringComparison.OrdinalIgnoreCase)) continue;
                if (bild is not null && string.Equals(name, bild, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(f);
            }
        }
        catch { /* then it stays - only what Design.json names is read */ }
    }

    /// <summary>
    /// "Restore design": first the settings (light/dark, accent color ...),
    /// then the wallpaper - if "accent color automatic" is on, Windows then
    /// derives it from the wallpaper, as on the old computer. Backups in the
    /// old format (folder "Hintergrundbild") are still read.
    /// </summary>
    public async Task ImportDesign()
    {
        var design = DesignSettings.Load(BackupPath);
        using var ziel = DesignKey(schreiben: true);
        if (ziel is null) return;
        if (_cancel.IsCancelled) return;

        // If a policy dictates the wallpaper, the policy wins - say so up front.
        if (OperatingSystem.IsWindows())
            foreach (var e in UserWallpaper.Influences(ziel.Wurzel, mitWechsel: false, Loc.Tr))
                _log("[WARNUNG] " + e + ".");

        // 1. Prepare the wallpaper: a color or an image (copied into the profile) -
        //    from Design.json or from a backup in the old format
        //    (folder "Hintergrundbild", up to version 1.2.6).
        var h = design?.Hintergrund;
        string? gesichert = h?.Bild is null ? null : Path.Combine(BackupPath, DesignSettings.Ordner, h.Bild);
        if (h is null && AppCatalog.HasWallpaper(BackupPath))
            (h, gesichert) = await ReadLegacyBackup();
        (byte R, byte G, byte B)? farbe = Wallpaper.IsSolidColor(h) ? Wallpaper.ReadColor(h!.Farbe) : null;
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
                bild = await ProvideWallpaperImage(gesichert, h.BildPfad, h.Profil);
            }
        }
        var werte = design?.Werte ?? new List<DesignEintrag>();

        // 2. Let Windows apply the theme itself - like double-clicking a .theme
        //    file. Otherwise the theme service writes its old theme back when
        //    Explorer restarts or the user signs out.
        if (OperatingSystem.IsWindows() && (farbe is not null || bild is not null || werte.Count > 0))
            await ApplyDesignViaWindows(ziel, farbe, bild, h, werte);
        if (_cancel.IsCancelled) return;

        // 3. Write the design settings explicitly - only NOW: if light/dark were
        //    already set before, Windows would see no change while applying and
        //    would not notify the taskbar and windows (found in CLInstall).
        //    Taskbar and Start are not part of the theme file.
        if (werte.Count > 0) ApplyDesignSettings(design!, ziel);
        else _log(Loc.Tr("[INFO] Die Sicherung enthält keine Design-Einstellungen (ältere Fassung des Tools) - nur der Hintergrund wird wiederhergestellt.",
                         "[INFO] The backup contains no design settings (older version of the tool) - only the wallpaper is restored."));
        if (_cancel.IsCancelled) return;

        // 4. The wallpaper - a solid color only here: the theme registers it, but
        //    the file's image is still what is shown.
        bool gesetzt = false;
        if (farbe is { } f)
        {
            SetSolidColor(ziel, f);
            gesetzt = true;
        }
        else if (bild is not null)
        {
            SetImageDirectly(ziel, bild, h);
            gesetzt = true;
        }
        else if (h is null && !AppCatalog.HasWallpaper(BackupPath))
        {
            _log(Loc.Tr("[INFO] Die Sicherung enthält keinen Hintergrund.", "[INFO] The backup contains no wallpaper."));
        }

        // Check a few seconds later whether Windows keeps it.
        if (gesetzt && OperatingSystem.IsWindows() && !_cancel.IsCancelled)
            await VerifyWallpaper(ziel, UserWallpaper.Read(ziel.Wurzel),
                                           Loc.Tr("Kontrolle nach dem Setzen", "Check after setting"));
    }

    /// <summary>The theme Windows applied in this run - for applying it again.</summary>
    private string? _angewandtesDesign;

    /// <summary>
    /// Builds a theme file from the user's current one with the values from the
    /// backup and lets Windows apply it as the signed-in user
    /// (<see cref="WindowsDesign"/>).
    /// </summary>
    private async Task ApplyDesignViaWindows(DesignZiel ziel, (byte R, byte G, byte B)? farbe, string? bild,
                                                HintergrundEinstellung? h, List<DesignEintrag> werte)
    {
        if (!OperatingSystem.IsWindows()) return;
        string? vorlage = null;
        try
        {
            if (UserWallpaper.ThemeFilePath(ziel.Wurzel, SourcePath) is { } aktuell && File.Exists(aktuell))
                vorlage = ThemeFile.ReadFile(aktuell).Inhalt;
        }
        catch { /* then without it */ }
        if (vorlage is null)
        {
            _log(Loc.Tr("[INFO] Kein aktuelles Windows-Design lesbar - Hintergrund und Einstellungen werden direkt eingetragen.",
                        "[INFO] No current Windows theme readable - background and settings are written directly."));
            return;
        }

        var inhalt = ThemeFile.ForRestore(vorlage, Loc.Tr("Design aus der Sicherung (DaSi)", "Theme from the backup (DaSi)"),
                                                       farbe, bild, h, werte);
        _log(Loc.Tr("[INFO] Windows wendet das Design aus der Sicherung an - wie ein Doppelklick auf die Design-Datei ...",
                    "[INFO] Windows applies the theme from the backup - like double-clicking the theme file ..."));
        if (await ApplyDesignAndWait(ziel, inhalt, farbe, bild) is { } datei)
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
    /// Lets Windows apply the theme (a new file under ...\Themes in the
    /// profile) and waits up to 20 s until it is taken over: the file is the
    /// current theme, and for a solid color the color is also in the registry.
    /// The caller sets an image directly afterwards anyway - if Windows already
    /// knows it, applying changes nothing about it.
    /// </summary>
    /// <returns>The applied file - null if Windows did not take it over.</returns>
    private async Task<string?> ApplyDesignAndWait(DesignZiel ziel, string inhalt, (byte R, byte G, byte B)? farbe, string? bild)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var ordner = Path.Combine(SourcePath, @"AppData\Local\Microsoft\Windows\Themes");
        bool IsCurrent(string datei)
            => string.Equals(UserWallpaper.ThemeFilePath(ziel.Wurzel, SourcePath), datei, StringComparison.OrdinalIgnoreCase);
        bool IsApplied(string datei)
            => IsCurrent(datei) && (farbe is not { } c || Wallpaper.ReadColor(UserWallpaper.Read(ziel.Wurzel).Farbe) == c);
        try
        {
            // The kind first: if it says "solid color", Windows takes only the color
            // from the theme, not the image.
            if (farbe is not null) UserWallpaper.WriteStyle(ziel.Wurzel, 1);
            else if (bild is not null) UserWallpaper.WriteStyle(ziel.Wurzel, 0);
            var r = await WindowsDesign.ApplyAndWait(ordner, inhalt, DesktopScreenshot.GetInteractiveUser()?.Name, IsApplied, IsCurrent,
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
    /// Waits a moment and checks whether the registry and the desktop still
    /// show what was set. If not: log what differs and what might cause it,
    /// and set it once more.
    /// </summary>
    private async Task VerifyWallpaper(DesignZiel ziel, HintergrundStand soll, string wann)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Delay(4000);
        var problem = UserWallpaper.Difference(soll, UserWallpaper.Read(ziel.Wurzel), Loc.Tr)
                      ?? UserWallpaper.Verify(soll, Loc.Tr);
        if (problem is null)
        {
            _log(Loc.Tr($"[INFO] {wann}: Der Desktop zeigt den gesetzten Hintergrund.", $"[INFO] {wann}: The desktop shows the background that was set."));
            return;
        }
        _log(Loc.Tr($"[WARNUNG] {wann}: {problem}. Der Hintergrund wird erneut gesetzt.", $"[WARNING] {wann}: {problem}. Setting the background again."));
        foreach (var e in UserWallpaper.Influences(ziel.Wurzel, mitWechsel: true, Loc.Tr))
            _log(Loc.Tr($"[INFO] Möglicher Grund: {e}.", $"[INFO] Possible reason: {e}."));
        try
        {
            // First through the Windows theme again - the one from this run, otherwise
            // the current one with the target wallpaper - then directly (for a solid
            // color the theme still shows its image). Without the theme, Explorer
            // would keep its old state and write it back at the next restart.
            var farbeSoll = soll.Bild is null ? Wallpaper.ReadColor(soll.Farbe) : null;
            var inhalt = _angewandtesDesign;
            if (inhalt is null && UserWallpaper.ThemeFilePath(ziel.Wurzel, SourcePath) is { } aktuell && File.Exists(aktuell))
                inhalt = ThemeFile.ReadFile(aktuell).Inhalt;
            if (inhalt is not null)
            {
                inhalt = ThemeFile.ForRestore(inhalt, Loc.Tr("Design aus der Sicherung (DaSi)", "Theme from the backup (DaSi)"),
                                                           farbeSoll, soll.Bild,
                                                           new HintergrundEinstellung { Darstellung = soll.Stil, Kacheln = soll.Kacheln },
                                                           Array.Empty<DesignEintrag>());
                if (soll.Bild is null && farbeSoll is null) inhalt = null;
            }
            if (inhalt is not null)
                await ApplyDesignAndWait(ziel, inhalt, farbeSoll, soll.Bild);
            UserWallpaper.Retry(ziel.Wurzel, ziel.EigenerBenutzer, soll, SourcePath);
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Der Hintergrund ließ sich nicht erneut setzen: {ex.Message}", $"[ERROR] The background could not be set again: {ex.Message}"));
            return;
        }
        await Task.Delay(3000);
        var nochmal = UserWallpaper.Difference(soll, UserWallpaper.Read(ziel.Wurzel), Loc.Tr)
                      ?? UserWallpaper.Verify(soll, Loc.Tr);
        _log(nochmal is null
            ? Loc.Tr("[ERFOLG] Hintergrund erneut gesetzt - jetzt zeigt der Desktop ihn.", "[SUCCESS] Background set again - the desktop now shows it.")
            : Loc.Tr($"[WARNUNG] Windows ändert den Hintergrund weiterhin: {nochmal}. Bitte unter Einstellungen > Personalisierung > Hintergrund nachsehen.",
                     $"[WARNING] Windows keeps changing the background: {nochmal}. Please check Settings > Personalization > Background."));
    }

    /// <summary>Where the wallpaper and the design settings belong.</summary>
    /// <param name="Wurzel">HKEY_USERS\&lt;SID&gt; of the signed-in user, otherwise HKEY_CURRENT_USER.</param>
    /// <param name="EigenerBenutzer">Does the tool run as exactly this user? Otherwise Windows calls that write to the
    /// process's registry only affect the running session.</param>
    /// <param name="Name">The signed-in user, if known.</param>
    private sealed record DesignZiel(RegistryKey Wurzel, bool Eigen, bool EigenerBenutzer, string? Name) : IDisposable
    {
        public void Dispose() { if (Eigen) Wurzel.Dispose(); }
    }

    /// <summary>
    /// The registry key for the wallpaper and the design settings: the one of
    /// the SIGNED-IN user (HKEY_USERS\&lt;SID&gt;) - the tool runs with admin
    /// rights, possibly under a different account. If the signed-in user does
    /// not own the selected profile, null: otherwise someone else's wallpaper
    /// and settings would be read or changed.
    /// </summary>
    private DesignZiel? DesignKey(bool schreiben)
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
        var (wurzel, eigen) = UserKey(schreiben);
        bool eigenerBenutzer = !eigen || UserWallpaper.IsProcessUser(angemeldet?.Sid);
        if (!eigenerBenutzer && angemeldet is not null)
            _log(Loc.Tr($"[INFO] Das Tool läuft unter dem Konto {Environment.UserName}, angemeldet ist {angemeldet.Name} - Hintergrund und Design werden bei {angemeldet.Name} gelesen bzw. eingetragen.",
                        $"[INFO] The tool runs under the account {Environment.UserName}, {angemeldet.Name} is signed in - background and design are read from / written to {angemeldet.Name}."));
        return new DesignZiel(wurzel, eigen, eigenerBenutzer, angemeldet?.Name);
    }

    /// <summary>
    /// HKEY_USERS\&lt;SID&gt; of the signed-in user - otherwise HKEY_CURRENT_USER.
    /// Whether it belongs to the selected profile is checked by the caller.
    /// </summary>
    private static (RegistryKey Wurzel, bool Eigen) UserKey(bool schreiben)
    {
        if (OperatingSystem.IsWindows() && DesktopScreenshot.GetInteractiveUser()?.Sid is { } sid)
        {
            try
            {
                var k = Registry.Users.OpenSubKey(sid, schreiben);
                if (k is not null) return (k, true);
            }
            catch { /* then HKEY_CURRENT_USER */ }
        }
        return (Registry.CurrentUser, false);
    }

    /// <summary>Reads the design settings; null if they are not read (other user, error).</summary>
    private List<DesignEintrag>? ReadDesignSettings(DesignZiel ziel)
    {
        try
        {
            var werte = DesignSettings.ReadAll((s, n) => DesignRegistry.Read(ziel.Wurzel, s, n));
            if (werte.Count == 0)
            {
                _log(Loc.Tr("[WARNUNG] Es wurden keine Design-Einstellungen gefunden.", "[WARNING] No design settings were found."));
                return werte;
            }
            var kurz = DesignSettings.Summary(werte, Loc.Tr);
            _log(Loc.Tr($"[ERFOLG] Design-Einstellungen gesichert ({werte.Count} Werte: {DesignSettings.Groups(werte)}){(kurz.Length > 0 ? " - " + kurz : "")}.",
                        $"[SUCCESS] Design settings backed up ({werte.Count} values: {DesignSettings.Groups(werte)}){(kurz.Length > 0 ? " - " + kurz : "")}."));
            return werte;
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die Design-Einstellungen konnten nicht gelesen werden: {ex.Message}",
                        $"[ERROR] The design settings could not be read: {ex.Message}"));
            return null;
        }
    }

    private void ApplyDesignSettings(DesignSicherung sicherung, DesignZiel ziel)
    {
        try
        {
            // Write and notify Windows. Windows loads the mouse pointers from the
            // PROCESS's registry - if the tool runs under another account, they only
            // take effect at the next sign-in.
            var ergebnis = DesignRegistry.Apply(ziel.Wurzel, sicherung.Werte, ziel.EigenerBenutzer);
            var (gesetzt, ohneDatei, nichtSchreibbar) = (ergebnis.Gesetzt, ergebnis.OhneDatei, ergebnis.NichtSchreibbar);
            bool zeiger = ergebnis.Zeiger;
            // Light/dark, accent color and pointers also go into the theme file -
            // Windows reads it at sign-in.
            if (OperatingSystem.IsWindows())
                LogThemeFile(UserWallpaper.AlignThemeFile(ziel.Wurzel, SourcePath, t => ThemeFile.ForDesign(t, gesetzt)));

            var kurz = DesignSettings.Summary(gesetzt, Loc.Tr);
            _log(Loc.Tr($"[ERFOLG] Design-Einstellungen übernommen ({gesetzt.Count} Werte: {DesignSettings.Groups(gesetzt)}){(kurz.Length > 0 ? " - " + kurz : "")}.",
                        $"[SUCCESS] Design settings applied ({gesetzt.Count} values: {DesignSettings.Groups(gesetzt)}){(kurz.Length > 0 ? " - " + kurz : "")}."));
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

    // ------------------------------------------------------------ Desktop screenshot

    /// <summary>
    /// Takes a picture of the desktop including its icons, so after a fresh
    /// install the user can look up what was where.
    /// </summary>
    public async Task CaptureDesktop()
    {
        // Whose desktop is visible? Often someone else's profile is backed up
        // while the technician is signed in - the picture would then show the
        // wrong desktop, and that would only come out on restore.
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

        // Move the windows out of the way - and remember exactly which ones, in
        // which order, and which was active. Only then do they all come back
        // exactly as they were.
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
            // Windows minimizes with a short animation.
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
            // Always bring them back - even if the capture fails.
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
                // Write completely to a temporary file first, then move it into place -
                // an abort while writing leaves no half image behind.
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

            // Images of an earlier backup into the same folder ("own folder" off) are
            // removed - only now that the new ones are safely written. Otherwise an
            // old image would remain if there are fewer monitors now.
            var neueNamen = plan.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var alt in DesktopScreenshot.FindImages(ordner)
                         .Append(Path.Combine(ordner, DesktopScreenshot.LegacyFileName)))
            {
                if (neueNamen.Contains(Path.GetFileName(alt))) continue;
                try { if (File.Exists(alt)) File.Delete(alt); } catch { /* then it stays */ }
            }
        }
        catch (Exception ex)
        {
            try { if (neu != null && File.Exists(neu)) File.Delete(neu); } catch { /* does not matter */ }
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
    /// Opens an image for the user. Replaceable for the tests - no image
    /// viewer should pop up there.
    /// </summary>
    internal Action<string> BildOeffnen { get; set; } = datei =>
    {
        // Through explorer.exe instead of directly: the tool runs with
        // administrator rights, and the Photos app does not start elevated.
        // explorer.exe hands the file to the user's running Explorer, which
        // opens it with normal rights.
        using var _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", Quote(datei)) { UseShellExecute = false });
    };

    /// <summary>
    /// Opens the backed-up desktop picture, so the icons can be rearranged by
    /// hand. Restoring the arrangement automatically from the picture is not
    /// reliable: Explorer overwrites it with its own state at the next sign-out.
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

        // Each image in its own window - so each one can be dragged to "its"
        // monitor and the icons arranged next to it.
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

    // ------------------------------------------------------------ Desktop icons (like DesktopOK)

    // ------------------------------------------------------------ Taskbar icons

    /// <summary>
    /// Backs up the icons pinned to the taskbar: the shortcuts from
    /// "User Pinned\TaskBar" and the Taskband registry key (order, also pinned
    /// Store apps without a shortcut).
    /// </summary>
    private void BackupTaskbar()
    {
        var pinOrdner = Taskbar.PinFolder(SourcePath);
        var verknuepfungen = Taskbar.Shortcuts(pinOrdner);
        var taskband = new List<DesignEintrag>();
        if (OperatingSystem.IsWindows())
        {
            var (wurzel, eigen) = UserKey(schreiben: false);
            try
            {
                using var k = wurzel.OpenSubKey(Taskbar.TaskbandSchluessel);
                foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                    if (DesignRegistry.Read(wurzel, Taskbar.TaskbandSchluessel, name) is { } v)
                        taskband.Add(new DesignEintrag { Gruppe = "Taskleiste", Schluessel = Taskbar.TaskbandSchluessel, Name = name, Typ = v.Typ, Wert = v.Wert });
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
            var ziel = Taskbar.BackupFolder(BackupPath);
            Directory.CreateDirectory(ziel);
            // Backed up into the same folder again: earlier shortcuts are removed.
            foreach (var alt in Directory.GetFiles(ziel, "*.lnk")) File.Delete(alt);
            foreach (var n in verknuepfungen) File.Copy(Path.Combine(pinOrdner, n), Path.Combine(ziel, n), overwrite: true);
            Taskbar.Save(BackupPath, new TaskleistenSicherung
            {
                Rechner = Environment.MachineName,
                Erstellt = DateTime.Now,
                Verknuepfungen = verknuepfungen,
                Taskband = taskband,
            });
            _log(verknuepfungen.Count > 0
                ? Loc.Tr($"[ERFOLG] Taskleiste gesichert: {verknuepfungen.Count} angeheftete Verknüpfungen ({Taskbar.Names(verknuepfungen)}) samt Reihenfolge und angehefteten Apps.",
                         $"[SUCCESS] Taskbar backed up: {verknuepfungen.Count} pinned shortcuts ({Taskbar.Names(verknuepfungen)}) including order and pinned apps.")
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
    /// Pins the backed-up icons to the taskbar again: restore the shortcuts,
    /// write Taskband, restart Explorer.
    /// </summary>
    /// <returns>true if Explorer was restarted for it.</returns>
    private async Task<bool> RestoreTaskbar(TaskleistenSicherung s)
    {
        var quelle = Taskbar.BackupFolder(BackupPath);
        var ziel = Taskbar.PinFolder(SourcePath);
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
        var ohneProgramm = OperatingSystem.IsWindows() ? CheckTaskbarTargets(ziel, zurueck) : new List<string>();

        if (!OperatingSystem.IsWindows() || s.Taskband.Count == 0)
        {
            _log(Loc.Tr($"[WARNUNG] Die Sicherung enthält nur die Verknüpfungen der Taskleiste, nicht ihre Reihenfolge - angeheftet werden sie dadurch nicht: {Taskbar.Names(zurueck)}",
                        $"[WARNING] The backup only contains the taskbar shortcuts, not their order - this does not pin them: {Taskbar.Names(zurueck)}"));
            return false;
        }

        // Write, restart Explorer (it reads the pinned icons only at startup) and
        // check whether Windows kept them. If it filled the taskbar on its own, a
        // second time - and then report honestly. This used to say "[ERFOLG]"
        // without checking; for one user the taskbar was empty afterwards.
        var taskband = OperatingSystem.IsWindows() ? AdjustAppIds(s.Taskband) : s.Taskband;
        // Windows only shows icons of programs that do not exist here yet once
        // the program is installed - they do not count in the check.
        var erwartet = Taskbar.PinnedNames(Taskbar.Favorites(taskband), zurueck.Except(ohneProgramm));
        List<string> nichtUebernommen = new();
        for (int versuch = 1; versuch <= 2; versuch++)
        {
            var (wurzel, eigen) = UserKey(schreiben: true);
            try
            {
                foreach (var e in taskband) DesignRegistry.Write(wurzel, e);
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
            var (ok, vonSelbst) = await ExplorerRestart.Restart(TimeSpan.FromSeconds(20));
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
            var jetzt = FavoritesNachlesen is { } nachlesen ? nachlesen() : ReadFavorites();
            nichtUebernommen = erwartet.Except(Taskbar.PinnedNames(jetzt, erwartet), StringComparer.OrdinalIgnoreCase).ToList();
            if (nichtUebernommen.Count == 0 || _cancel.IsCancelled) break;
            if (versuch == 1)
                _log(Loc.Tr($"[WARNUNG] Windows hat die Taskleiste nach dem Neustart selbst neu belegt - nicht übernommen: {Taskbar.Names(nichtUebernommen)}. Das Tool schreibt sie noch einmal.",
                            $"[WARNING] Windows re-populated the taskbar by itself after the restart - not taken over: {Taskbar.Names(nichtUebernommen)}. The tool writes them once more."));
        }
        if (nichtUebernommen.Count > 0)
        {
            _log(Loc.Tr($"[FEHLER] Windows übernimmt die gesicherten Taskleisten-Symbole nicht: {Taskbar.Names(nichtUebernommen)}. "
                        + "Die Verknüpfungen liegen im Ordner \"User Pinned\\TaskBar\" des Profils. Abhilfe: abmelden, wieder anmelden und \"Design & Desktop wiederherstellen\" (Symbole + Taskleiste) erneut ausführen - "
                        + "oder die Programme von Hand anheften.",
                        $"[ERROR] Windows does not take over the saved taskbar icons: {Taskbar.Names(nichtUebernommen)}. "
                        + "The shortcuts are in the profile's \"User Pinned\\TaskBar\" folder. Remedy: sign out, sign in again and run \"Restore design & desktop\" (icons + taskbar) again - "
                        + "or pin the programs by hand."));
            return true;
        }

        // Does Windows actually show the entries? No registry value says so - the
        // check looks at the taskbar itself, via the app IDs.
        if (!CheckTaskbarVisible(Taskbar.Entries(Taskbar.Favorites(taskband), zurueck), ohneProgramm)) return true;
        _log(Loc.Tr($"[ERFOLG] Taskleiste wiederhergestellt: {(zurueck.Count > 0 ? $"{zurueck.Count} Verknüpfungen ({Taskbar.Names(zurueck)}) und " : "")}die angehefteten Apps in der gesicherten Reihenfolge.",
                    $"[SUCCESS] Taskbar restored: {(zurueck.Count > 0 ? $"{zurueck.Count} shortcuts ({Taskbar.Names(zurueck)}) and " : "")}the pinned apps in the saved order."));
        if (fehlend.Count > 0)
            _log(Loc.Tr($"[WARNUNG] {fehlend.Count} Verknüpfungen fehlen in der Sicherung: {Taskbar.Names(fehlend)}",
                        $"[WARNING] {fehlend.Count} shortcuts are missing from the backup: {Taskbar.Names(fehlend)}"));
        return true;
    }

    /// <summary>Tests only: the app IDs the taskbar shows (null: not readable).</summary>
    internal Func<HashSet<string>?>? TaskleistenSicht { get; set; }

    /// <summary>
    /// Compares the pinned entries with what the taskbar shows. If the icon of
    /// a program installed here is missing, that is an error; if the program
    /// itself is still missing, a hint.
    /// </summary>
    /// <returns>false if an error was reported.</returns>
    private bool CheckTaskbarVisible(List<Taskbar.Eintrag> eintraege, List<string> ohneProgramm)
    {
        var sichtbar = TaskleistenSicht is { } sicht ? sicht() : OperatingSystem.IsWindows() ? TaskbarView.AppIds() : null;
        var mitKennung = eintraege.Where(e => e.Kennung is not null).ToList();
        if (mitKennung.Count == 0) return true;
        if (sichtbar is null)
        {
            _log(Loc.Tr("[INFO] Ob die Taskleiste die Symbole zeigt, ließ sich nicht nachsehen - bitte einen Blick darauf werfen.",
                        "[INFO] Whether the taskbar shows the icons could not be checked - please have a look."));
            return true;
        }
        static string Name(Taskbar.Eintrag e) => e.Verknuepfung is { } v ? Path.GetFileNameWithoutExtension(v) : e.Kennung!;
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

    /// <summary>How long to wait after the Explorer restart before checking.</summary>
    internal TimeSpan TaskleistenKontrolle { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Tests only: what Windows has in Taskband\Favorites after the restart.</summary>
    internal Func<byte[]?>? FavoritesNachlesen { get; set; }

    private byte[]? ReadFavorites()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var (wurzel, eigen) = UserKey(schreiben: false);
        try
        {
            var v = DesignRegistry.Read(wurzel, Taskbar.TaskbandSchluessel, "Favorites");
            return v is { Typ: "Binary" } b ? Convert.FromBase64String(b.Wert) : null;
        }
        catch { return null; }
        finally { if (eigen) wurzel.Dispose(); }
    }

    /// <summary>
    /// Do the restored taskbar shortcuts point to programs that exist here? If
    /// one is just somewhere else (32 instead of 64 bit, another location
    /// according to "App Paths"), the shortcut is redirected. If it is missing,
    /// the log names it - usually programs that still need to be installed.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    /// <returns>The shortcuts (file names) whose program does not exist here.</returns>
    internal List<string> CheckTaskbarTargets(string ordner, IEnumerable<string> verknuepfungen)
    {
        var fehlend = new List<string>();
        var fehlendeDateien = new List<string>();
        var programme = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programme86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var n in verknuepfungen)
        {
            var lnk = Path.Combine(ordner, n);
            var inhalt = Shortcut.Read(lnk);
            // Shell objects (File Explorer) and targets that exist: nothing to do.
            if (inhalt is null || inhalt.Ziel.Length == 0 || File.Exists(inhalt.Ziel) || Directory.Exists(inhalt.Ziel)) continue;
            var name = Path.GetFileNameWithoutExtension(n);
            var neu = Taskbar.NewTarget(inhalt.Ziel, File.Exists, LookUpAppPath, programme, programme86);
            if (neu is not null && Shortcut.SetTarget(lnk, neu))
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
                        + "Nach ihrer Installation \"Design & Desktop wiederherstellen\" (Symbole + Taskleiste) erneut ausführen.",
                        $"[WARNING] Taskbar: these pinned programs do not exist on this PC (yet): {string.Join(", ", fehlend)}. "
                        + "After installing them, run \"Restore design & desktop\" (icons + taskbar) again."));
        return fehlendeDateien;
    }

    /// <summary>
    /// Adjusts the app IDs in Taskband\Favorites to the programs of this PC.
    /// The taskbar uses the ID to map a pinned icon to an installed program;
    /// if it finds none, it does not show the icon. For one user (Windows 10 ->
    /// 11), Firefox carried the ID of the 32-bit Firefox ("E7CF176E110C211B"),
    /// while winget had installed the 64-bit Firefox ("308046B0AF4A39CB"). Only
    /// IDs of equal length are replaced.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal List<DesignEintrag> AdjustAppIds(List<DesignEintrag> taskband)
    {
        var kennungen = Taskbar.AppIds(Taskbar.Favorites(taskband)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kennungen.Count == 0) return taskband;

        // Firefox/Thunderbird: ID = hash of the installation folder.
        var mozilla = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in new[] { AppCatalog.Firefox, AppCatalog.Thunderbird })
        {
            var (exe, ordnerName) = MozillaProfile.ProgramOf(app)!.Value;
            var hier = MozillaInstallationsHere(exe, ordnerName);
            if (hier.Count == 0) continue;
            var schluesselHier = hier.Select(MozillaProfile.InstallHash).ToList();
            foreach (var ort in MozillaProfile.UsualLocations(ordnerName, SourcePath))
            {
                var alt = MozillaProfile.InstallHash(ort);
                if (!schluesselHier.Contains(alt, StringComparer.OrdinalIgnoreCase)) mozilla.TryAdd(alt, schluesselHier[0]);
            }
        }
        // Programs without an ID of their own: path from the Program Files folder on.
        var ordner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Taskbar.OrdnerProgramme86] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            [Taskbar.OrdnerProgramme64] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            [Taskbar.OrdnerProgramme] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        var ergebnis = taskband.Select(e => new DesignEintrag { Gruppe = e.Gruppe, Schluessel = e.Schluessel, Name = e.Name, Typ = e.Typ, Wert = e.Wert }).ToList();
        var ersetzt = new List<string>();
        foreach (var k in kennungen)
        {
            if (Taskbar.NewAppId(k, mozilla, ordner, File.Exists) is not { } neu) continue;
            foreach (var e in ergebnis.Where(e => e.Typ == "Binary" && e.Name.StartsWith("Favorites", StringComparison.Ordinal)))
            {
                try { e.Wert = Convert.ToBase64String(Taskbar.ReplaceAppId(Convert.FromBase64String(e.Wert), k, neu)); }
                catch { /* unreadable value - stays */ }
            }
            ersetzt.Add($"{k} -> {neu}");
        }
        if (ersetzt.Count > 0)
            _log(Loc.Tr($"[INFO] Taskleiste: Kennungen an die Programme dieses PCs angepasst, damit Windows die angehefteten Symbole zuordnet: {string.Join(", ", ersetzt)}.",
                        $"[INFO] Taskbar: app IDs adjusted to the programs on this PC so Windows matches the pinned icons: {string.Join(", ", ersetzt)}."));
        return ergebnis;
    }

    /// <summary>A program's location according to "App Paths" (for all users, 64 and 32 bit).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? LookUpAppPath(string datei)
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
            catch { /* next one */ }
        }
        return null;
    }

    /// <summary>
    /// Explorer shows the desktop of the signed-in user. If the selected
    /// profile belongs to someone else, the wrong desktop would be read or
    /// changed - better to do nothing at all then.
    /// </summary>
    private bool DesktopBelongsToProfile(string wasDe, string wasEn)
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
    /// "Icons + taskbar": remembers which desktop icon sits where and backs up
    /// the icons pinned to the taskbar.
    /// </summary>
    public async Task BackupDesktopIcons()
    {
        if (!DesktopBelongsToProfile("Keine Symbolpositionen gesichert", "No icon positions backed up")) return;
        await BackupIconPositions();
        if (_cancel.IsCancelled) return;
        BackupTaskbar();
    }

    private async Task BackupIconPositions()
    {

        DesktopLayout layout;
        try
        {
            var benutzer = Path.GetFileName(SourcePath.TrimEnd('\\', '/'));
            layout = await Task.Run(() => DesktopIcons.Read(Environment.MachineName, benutzer));
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
        try { datei = DesktopIcons.Save(Path.Combine(BackupPath, DesktopIcons.FolderName), layout); }
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
    /// Puts every backed-up desktop icon back in its place. Runs last - after
    /// profile, programs and wallpaper, so the icons exist by then.
    /// </summary>
    public async Task RestoreDesktopIcons()
    {
        var datei = DesktopIcons.FilePath(BackupPath);
        var layout = DesktopIcons.Load(datei);
        var taskleiste = Taskbar.Load(BackupPath);
        if (layout is null && taskleiste is null)
        {
            _log(Loc.Tr($"[FEHLER] In der Sicherung liegen weder Symbolpositionen noch Taskleisten-Symbole: {datei}",
                        $"[ERROR] The backup contains neither icon positions nor taskbar icons: {datei}"));
            return;
        }
        if (!DesktopBelongsToProfile("Symbolpositionen nicht gesetzt", "Icon positions not applied")) return;

        // The taskbar first: Explorer restarts for it - only then the desktop
        // icons go to their places, otherwise the restart might rearrange them.
        HintergrundStand? hgVorher = null;
        if (taskleiste is not null && OperatingSystem.IsWindows())
        {
            var (w, e) = UserKey(schreiben: false);
            try { hgVorher = UserWallpaper.Read(w); } finally { if (e) w.Dispose(); }
        }
        var neuGestartet = taskleiste is not null && await RestoreTaskbar(taskleiste);
        if (_cancel.IsCancelled) return;

        // On some Windows versions the Explorer restart resets the wallpaper -
        // check and set it again if needed.
        if (neuGestartet && hgVorher is not null && OperatingSystem.IsWindows())
        {
            var angemeldet = DesktopScreenshot.GetInteractiveUser();
            var (w, e) = UserKey(schreiben: true);
            using var ziel = new DesignZiel(w, e, !e || UserWallpaper.IsProcessUser(angemeldet?.Sid), angemeldet?.Name);
            await VerifyWallpaper(ziel, hgVorher, Loc.Tr("Hintergrund nach dem Explorer-Neustart", "Background after the Explorer restart"));
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
        // After an Explorer restart, the desktop and its icons appear only bit by
        // bit: wait longer and retry opening the desktop.
        var warten = TimeSpan.FromSeconds(neuGestartet ? 25 : 10);
        IconErgebnis? ergebnis = null;
        Exception? letzter = null;
        for (int versuch = 0; versuch < (neuGestartet ? 10 : 1) && ergebnis is null; versuch++)
        {
            try { ergebnis = await Task.Run(() => DesktopIcons.Restore(layout, warten, k => EnableSystemIcons(k, sid))); }
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
            static string Names(List<DesktopIcon> l) =>
                string.Join(", ", l.Take(10).Select(f => string.IsNullOrWhiteSpace(f.Anzeige) ? f.Name : f.Anzeige))
                + (l.Count > 10 ? ", ..." : "");
            var (imProfil, fremd) = DesktopIcons.ByOrigin(erg.Fehlend, Path.Combine(BackupPath, "Benutzerprofil", "Desktop"));
            if (imProfil.Count > 0)
                _log(Loc.Tr($"[WARNUNG] {imProfil.Count} von {erg.Gesamt} Symbolen gibt es auf diesem Desktop nicht: {Names(imProfil)}. "
                            + "Sie wurden ausgelassen - am besten zusammen mit dem Benutzerprofil und den Programmen wiederherstellen.",
                            $"[WARNING] {imProfil.Count} of {erg.Gesamt} icons do not exist on this desktop: {Names(imProfil)}. "
                            + "They were skipped - best restore them together with the user profile and the programs."));
            // Not from the profile: programs create these shortcuts when they are
            // installed - usually on the public desktop.
            if (fremd.Count > 0)
                _log(Loc.Tr($"[WARNUNG] {fremd.Count} von {erg.Gesamt} Symbolen fehlen hier und stammen nicht aus dem Benutzerprofil: {Names(fremd)}. "
                            + "Solche Verknüpfungen liegen meist auf dem öffentlichen Desktop (für alle Benutzer) und erscheinen, sobald ihr Programm installiert ist. "
                            + "Danach \"Design & Desktop wiederherstellen\" (Symbole + Taskleiste) noch einmal ausführen, dann kommen auch sie an ihre Stelle.",
                            $"[WARNING] {fremd.Count} of {erg.Gesamt} icons are missing here and do not come from the user profile: {Names(fremd)}. "
                            + "Such shortcuts usually live on the public desktop (for all users) and appear once their program is installed. "
                            + "Then run \"Restore design & desktop\" (icons + taskbar) again to put them in place as well."));
        }
        _log(erg.Gesetzt > 0
            ? Loc.Tr($"[ERFOLG] {erg.Gesetzt} von {erg.Gesamt} Desktop-Symbolen an ihre gesicherte Stelle gesetzt.",
                     $"[SUCCESS] {erg.Gesetzt} of {erg.Gesamt} desktop icons moved to their saved positions.")
            : Loc.Tr("[FEHLER] Kein gesichertes Symbol ließ sich auf diesem Desktop zuordnen.",
                     "[ERROR] None of the saved icons could be matched on this desktop."));
    }

    /// <summary>
    /// Turns on desktop system icons - the same setting as "Desktop icon
    /// settings" (0 = visible). It is written to the key of the SIGNED-IN user:
    /// the tool runs with administrator rights, possibly under another account
    /// - whose HKEY_CURRENT_USER would be the wrong one.
    /// </summary>
    private static bool EnableSystemIcons(IReadOnlyList<string> kennungen, string? sid)
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

        // Notify Explorer - just like the settings page does.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x1000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Compares two files by content (size, then bytes).</summary>
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

    // ------------------------------------------------------------ App install/update

    /// <returns>true if the program was installed just now.</returns>
    /// <param name="auchDa">Additional check in case "App Paths" knows nothing - e.g. the program folders.</param>
    private async Task<bool> InstallApp(string appName, string exeName, string? wingetId, Func<bool>? auchDa = null)
    {
        if (_cancel.IsCancelled) return false;
        _log(Loc.Tr($"[INFO] Prüfe, ob {appName} installiert ist...", $"[INFO] Checking whether {appName} is installed..."));
        // Installed for all users (HKLM) or - like Chrome without admin rights -
        // for the user only (HKCU).
        var appPathKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        bool da;
        using (var lm = Registry.LocalMachine.OpenSubKey(appPathKey))
        using (var cu = Registry.CurrentUser.OpenSubKey(appPathKey))
            da = lm != null || cu != null;
        // Parallel installations share "App Paths": if a PC had Firefox ESR
        // (32 bit) and the current release (64 bit), uninstalling one removes the
        // entry - the other one is still there. Without this check, winget would
        // needlessly install it once more.
        if (!da && auchDa is not null) { try { da = auchDa(); } catch { /* then winget */ } }
        if (da) { _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed.")); return false; }

        // Edge is part of Windows; there is no package for it.
        if (wingetId is null) return false;
        return await InstallViaWinget(appName, wingetId);
    }

    /// <summary>
    /// Closes a program before its profile is copied - and waits until it has
    /// really exited. If it was installed just now, it waits a moment first:
    /// some installers (Chrome) start the browser on their own a few seconds
    /// after installing. Running while the profile is copied, it would create
    /// a fresh, empty profile.
    /// </summary>
    /// <returns>false if it could not be closed.</returns>
    private async Task<bool> CloseProgram(string name, bool frischInstalliert, params string[] prozesse)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (frischInstalliert)
        {
            _log(Loc.Tr($"[INFO] {name} wurde gerade installiert - manche Installer starten das Programm danach von selbst. Kurz abwarten ...",
                        $"[INFO] {name} has just been installed - some installers start the program afterwards. Waiting briefly ..."));
            for (int i = 0; i < 12 && !IsRunning(prozesse) && !_cancel.IsCancelled; i++) await Task.Delay(1000);
        }
        _log(Loc.Tr($"[INFO] Beende {name}...", $"[INFO] Closing {name}..."));
        return await KillAndWait(TimeSpan.FromSeconds(20), prozesse);
    }

    /// <summary>Is one of these processes running?</summary>
    private static bool IsRunning(params string[] prozesse)
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
            catch { /* next one */ }
        }
        return false;
    }

    /// <summary>
    /// Kills the processes and waits until none is running - and it stays that
    /// way for a second (browsers start helper processes again).
    /// </summary>
    private async Task<bool> KillAndWait(TimeSpan hoechstens, params string[] prozesse)
    {
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            foreach (var name in prozesse) KillProcess(name);
            await Task.Delay(700);
            if (!IsRunning(prozesse))
            {
                await Task.Delay(1000);
                if (!IsRunning(prozesse)) return true;
            }
            if (uhr.Elapsed > hoechstens || _cancel.IsCancelled) return !IsRunning(prozesse);
        }
    }

    /// <returns>true if winget installed the program just now.</returns>
    private async Task<bool> InstallViaWinget(string appName, string wingetId)
    {
        _log(Loc.Tr($"[INFO] Versuche, {appName} via Winget zu installieren...", $"[INFO] Trying to install {appName} via winget..."));
        // This used to always run "winget" from the search path - bypassing the
        // check and the repair of the winget import. If winget failed on its
        // package source, the install failed here silently with a number.
        var r = Winget is not null
            ? await Winget.InstallAsync(wingetId)
            : await _runner.RunAsync("winget", $"install --id {wingetId} -e --accept-package-agreements --accept-source-agreements");

        // This used to always say "installation finished" - even if winget did
        // not start at all. Deliberately a WARNING, not an ERROR: the profile
        // itself is restored anyway, and that is what this action is about. The
        // user just needs to know that the program is still missing.
        if (!r.Started)
            _log(Loc.Tr($"[WARNUNG] winget konnte nicht gestartet werden ({r.StartError}). {appName} bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] winget could not be started ({r.StartError}). Please install {appName} manually - the profile is restored anyway."));
        else if (r.ExitCode is WingetErrors.SchonInstalliert or WingetErrors.SchonAktuell)
            _log(Loc.Tr($"[INFO] {appName} ist bereits installiert.", $"[INFO] {appName} is already installed."));
        else if (r.ExitCode != 0)
            _log(Loc.Tr($"[WARNUNG] {appName} ließ sich per winget nicht installieren ({WingetService.DescribeResult(r)}). Bitte von Hand installieren - das Profil wird trotzdem zurückgeschrieben.",
                        $"[WARNING] {appName} could not be installed via winget ({WingetService.DescribeResult(r)}). Please install it manually - the profile is restored anyway."));
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
