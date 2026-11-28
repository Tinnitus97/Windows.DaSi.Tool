using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Export and import of the installed programs through winget:
/// - robustly resolves a working way to start winget
/// - registers the App Installer package itself if winget is not
///   registered for the current user
/// - if winget is missing altogether (or too old), App Installer is
///   installed from GitHub; if the package sources do not respond, they are
///   reset
/// - native "winget export" with add-on and runtime packages filtered out
/// - rescues known programs (Firefox de, Office ...) through a verified
///   list of known packages
/// - import: each package on its own with "winget install --exact --id"
/// </summary>
public sealed class WingetService
{
    private readonly ProcessRunner _runner;
    private readonly CancellationTokenState _cancel;
    private readonly Action<string> _log;

    private (string Exe, string Mode)? _launcher;

    /// <summary>Try installing from GitHub only once per program run.</summary>
    private bool _githubVersucht;

    /// <summary>Reset the package sources after a source error only once per program run.</summary>
    private bool _quellenZurueckgesetzt;

    public WingetService(ProcessRunner runner, CancellationTokenState cancel, Action<string> log)
    {
        _runner = runner;
        _cancel = cancel;
        _log = log;
    }

    /// <summary>
    /// For the tests: only try these programs as winget - a fake that answers
    /// like winget. That way every path of this service runs without installing
    /// anything on the test machine.
    /// </summary>
    internal IReadOnlyList<string>? FesteKandidaten { get; set; }

    /// <summary>
    /// For the tests: replaces installing App Installer from GitHub (about
    /// 300 MB).
    /// </summary>
    internal Func<Task<bool>>? Nachinstallation { get; set; }

    // ---------------------------------------------------------------- Launcher

    private IEnumerable<string> GetCandidatePaths()
    {
        if (FesteKandidaten is not null) return FesteKandidaten;
        var list = new List<string>();

        // 1) PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), "winget.exe");
            if (File.Exists(p)) list.Add(p);
        }

        // 2) App execution alias (deliberately without File.Exists: reparse point)
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        list.Add(Path.Combine(local, @"Microsoft\WindowsApps\winget.exe"));

        // 3) Installation folder of the App Installer package
        foreach (var loc in GetDesktopAppInstallerLocations())
        {
            var exe = Path.Combine(loc, "winget.exe");
            if (File.Exists(exe)) list.Add(exe);
        }

        return list.Distinct();
    }

    private IEnumerable<string> GetDesktopAppInstallerLocations()
    {
        var results = new List<string>();
        try
        {
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var root = Path.Combine(progFiles, "WindowsApps");
            if (Directory.Exists(root))
            {
                // Newest version first - by version number, not by text (otherwise "1.9"
                // came before "1.26").
                results.AddRange(WingetFehler.NewestFirst(
                    Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")));
            }
        }
        catch { /* access to WindowsApps may be restricted */ }
        return results;
    }

    private async Task<ProcessResult> RunWingetRaw(string exe, string mode, string args, int timeoutMs = 0)
    {
        return mode == "Cmd"
            ? await _runner.RunViaCmdAsync(exe, args, timeoutMs)
            : await _runner.RunAsync(exe, args, outputEncoding: System.Text.Encoding.UTF8, timeoutMs: timeoutMs);
    }

    private async Task<(string Exe, string Mode)?> TestCandidates(List<string> attempts)
    {
        foreach (var exe in GetCandidatePaths())
        {
            foreach (var mode in new[] { "Direct", "Cmd" })
            {
                if (_cancel.IsCancelled) return null;
                var probe = await RunWingetRaw(exe, mode, "--version", 20000);
                if (probe.Started && probe.ExitCode == 0 && Regex.IsMatch(probe.Output, @"\d+\.\d+"))
                {
                    _log(Loc.Tr($"[INFO] Verwende winget {probe.Output.Trim()}: {exe} (Startmethode: {mode})", $"[INFO] Using winget {probe.Output.Trim()}: {exe} (launch method: {mode})"));
                    return (exe, mode);
                }
                var reason = probe.StartError ??
                             ("ExitCode " + probe.ExitCode +
                              (string.IsNullOrWhiteSpace(probe.Output) ? "" : ": " +
                               probe.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim()));
                attempts.Add($"{exe} [{mode}] -> {reason}");
            }
        }
        return null;
    }

    private async Task<bool> RegisterForCurrentUser()
    {
        _log(Loc.Tr("[INFO] winget scheint für diesen Benutzer nicht registriert zu sein. Versuche Registrierung des App-Installer-Pakets...", "[INFO] winget does not appear to be registered for this user. Attempting to register the App Installer package..."));

        // Add-AppxPackage is only reliably available in Windows PowerShell
        // (powershell.exe) -> call it through the process runner.
        async Task<bool> RunPs(string psCommand)
        {
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCommand.Replace("\"", "\\\"")}\"";
            var r = await _runner.RunAsync("powershell.exe", args, timeoutMs: 60000);
            return r.Started && r.ExitCode == 0;
        }

        // Option 1: RegisterByFamilyName (newer Windows builds)
        try
        {
            if (await RunPs("Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop"))
            {
                _log(Loc.Tr("[INFO] Registrierung per Paketfamilie erfolgreich.", "[INFO] Registration by package family succeeded."));
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!_cancel.FastMode) _log(Loc.Tr($"[INFO] Registrierung per Paketfamilie nicht möglich: {ex.Message}", $"[INFO] Registration by package family not possible: {ex.Message}"));
        }

        // Option 2: the classic way through the AppxManifest in the package folder
        foreach (var loc in GetDesktopAppInstallerLocations())
        {
            var manifest = Path.Combine(loc, "AppxManifest.xml");
            if (!File.Exists(manifest)) continue;
            try
            {
                if (await RunPs($"Add-AppxPackage -Register '{manifest}' -DisableDevelopmentMode -ErrorAction Stop"))
                {
                    _log(Loc.Tr($"[INFO] Registrierung über AppxManifest erfolgreich: {loc}", $"[INFO] Registration via AppxManifest succeeded: {loc}"));
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log(Loc.Tr($"[WARNUNG] Registrierung fehlgeschlagen ({loc}): {ex.Message}", $"[WARNING] Registration failed ({loc}): {ex.Message}"));
            }
        }
        return false;
    }

    private async Task<(string Exe, string Mode)?> ResolveLauncher()
    {
        if (_launcher is not null) return _launcher;

        var attempts = new List<string>();
        var found = await TestCandidates(attempts);
        if (found is not null) { _launcher = found; return _launcher; }
        if (_cancel.IsCancelled) return null;

        if (await RegisterForCurrentUser())
        {
            await Task.Delay(3000);
            found = await TestCandidates(attempts);
            if (found is not null) { _launcher = found; return _launcher; }
        }
        if (_cancel.IsCancelled) return null;

        // Cannot be registered: App Installer is missing altogether. Then from GitHub.
        if (await InstallFromGitHub())
        {
            await Task.Delay(3000);
            found = await TestCandidates(attempts);
            if (found is null && await RegisterForCurrentUser())
            {
                await Task.Delay(2000);
                found = await TestCandidates(attempts);
            }
            if (found is not null) { _launcher = found; return _launcher; }
        }
        if (_cancel.IsCancelled) return null;

        _log(Loc.Tr("[FEHLER] winget konnte mit keiner Methode gestartet werden. Versuchte Wege:", "[ERROR] winget could not be started by any method. Attempted paths:"));
        foreach (var a in attempts.Distinct()) _log("    " + a);
        _log(Loc.Tr("[INFO] Bitte einmal unter dem Benutzer, der dieses Tool startet, 'winget --version' in einer Konsole ausführen. Falls der Befehl unbekannt ist, den 'App-Installer' im Microsoft Store aktualisieren und danach erneut versuchen.", "[INFO] Please run 'winget --version' once in a console under the user that starts this tool. If the command is unknown, update the 'App Installer' from the Microsoft Store and try again."));
        return null;
    }

    /// <summary>
    /// Install App Installer from GitHub - at most once per run, because it is
    /// about 300 MB, and if it fails once (no internet, blocked by policy), it
    /// fails again right away.
    /// </summary>
    private async Task<bool> InstallFromGitHub()
    {
        if (_githubVersucht || !OperatingSystem.IsWindows()) return false;
        _githubVersucht = true;
        _log(Loc.Tr("[INFO] winget ist nicht installiert oder nicht nutzbar - versuche, den App-Installer von GitHub zu installieren.",
                    "[INFO] winget is not installed or not usable - trying to install the App Installer from GitHub."));
        return Nachinstallation is not null
            ? await Nachinstallation()
            : await WingetInstaller.InstallAsync(_log, () => _cancel.IsCancelled);
    }

    /// <summary>Reset and reload the package sources (needs admin rights - the tool has them).</summary>
    private async Task RepairSources()
    {
        _log(Loc.Tr("[INFO] Die Paketquellen von winget antworten nicht wie erwartet - setze sie zurück ('winget source reset --force') ...",
                    "[INFO] The winget package sources do not respond as expected - resetting them ('winget source reset --force') ..."));
        var reset = await InvokeWinget("source reset --force --disable-interactivity", 120000);
        if (!_cancel.FastMode || !reset.Started || reset.ExitCode != 0)
            _log(Loc.Tr($"    source reset: ExitCode {reset.ExitCode}", $"    source reset: exit code {reset.ExitCode}"));
        var update = await InvokeWinget("source update --disable-interactivity", 180000);
        if (!_cancel.FastMode || !update.Started || update.ExitCode != 0)
            _log(Loc.Tr($"    source update: ExitCode {update.ExitCode}", $"    source update: exit code {update.ExitCode}"));
    }

    /// <summary>Does the "winget" package source respond? null = yes, otherwise what is missing.</summary>
    private async Task<WingetReadiness?> CheckSources()
    {
        var quellen = await InvokeWinget("source list --disable-interactivity", 60000);
        if (_cancel.IsCancelled) return Cancelled();

        if (!quellen.Started || quellen.ExitCode != 0)
            return WingetReadiness.Fail(
                Loc.Tr("winget startet, kann aber seine Paketquellen nicht lesen.",
                       "winget starts but cannot read its package sources."),
                Loc.Tr("Ohne Paketquelle liefert ein Export eine leere Liste und ein Import findet nichts. "
                       + "Das Zurücksetzen der Quellen und eine Neuinstallation des App-Installers von GitHub "
                       + "haben nicht geholfen (Details im Protokoll). Führe in einer Konsole "
                       + "'winget source reset --force' und danach 'winget source update' aus.",
                       "Without a package source an export returns an empty list and an import finds nothing. "
                       + "Resetting the sources and reinstalling the App Installer from GitHub did not help "
                       + "(details in the log). Run 'winget source reset --force' and then 'winget source update' in a console."));

        if (!quellen.Output.Contains("winget", StringComparison.OrdinalIgnoreCase))
            return WingetReadiness.Fail(
                Loc.Tr("Die Paketquelle 'winget' ist nicht eingerichtet.",
                       "The 'winget' package source is not set up."),
                Loc.Tr("winget läuft, aber die offizielle Quelle fehlt - Programme lassen sich so weder "
                       + "zuverlässig erfassen noch installieren. Das Zurücksetzen der Quellen hat nicht "
                       + "geholfen (Details im Protokoll). Führe in einer Konsole 'winget source reset --force' aus.",
                       "winget runs, but the official source is missing - programs can neither be captured "
                       + "nor installed reliably. Resetting the sources did not help (details in the log). "
                       + "Run 'winget source reset --force' in a console."));
        return null;
    }

    private static WingetReadiness Cancelled() => WingetReadiness.Fail(
        Loc.Tr("Abgebrochen.", "Cancelled."),
        Loc.Tr("Die Prüfung wurde abgebrochen.", "The check was cancelled."));

    private async Task<ProcessResult> InvokeWinget(string args, int timeoutMs = 0)
    {
        var launcher = await ResolveLauncher();
        if (launcher is null)
            return new ProcessResult(-1, "", "winget ist in dieser Umgebung nicht ausführbar (Details siehe Log).");
        return await RunWingetRaw(launcher.Value.Exe, launcher.Value.Mode, args, timeoutMs);
    }

    // ------------------------------------------------------- Readiness

    /// <summary>
    /// Result of the readiness check. <paramref name="Problem"/> and
    /// <paramref name="Hint"/> are only set if it does not work - the text is
    /// meant for the user then, not for the log.
    /// </summary>
    public sealed record WingetReadiness(bool Ready, string? Version, string? Problem, string? Hint)
    {
        public static WingetReadiness Ok(string version) => new(true, version, null, null);
        public static WingetReadiness Fail(string problem, string hint) => new(false, null, problem, hint);
    }

    /// <summary>
    /// Checks whether winget is present, registered for this user AND usable -
    /// BEFORE an export or import starts.
    ///
    /// Why not just start: without the check, only the actual operation fails,
    /// in the middle of a queue that has already copied profiles. The user then
    /// sees an error at a point where nothing can be done any more. Here it
    /// shows up beforehand.
    ///
    /// The check has two stages, because "present" and "working" are two
    /// different things:
    ///   1. Can winget be started at all ("--version")? If that fails,
    ///      <see cref="ResolveLauncher"/> first tries to register the App
    ///      Installer package for this user.
    ///   2. Does the package source respond ("source list")? winget can start
    ///      while the source is missing or its terms were never accepted -
    ///      then an export returns an empty list without any error anywhere.
    /// </summary>
    public async Task<WingetReadiness> CheckReadinessAsync()
    {
        _log(Loc.Tr("[INFO] Prüfe, ob winget einsatzbereit ist...", "[INFO] Checking whether winget is ready..."));

        var launcher = await ResolveLauncher();
        if (_cancel.IsCancelled)
            return WingetReadiness.Fail(
                Loc.Tr("Abgebrochen.", "Cancelled."),
                Loc.Tr("Die Prüfung wurde abgebrochen.", "The check was cancelled."));

        if (launcher is null)
            return WingetReadiness.Fail(
                Loc.Tr("winget lässt sich auf diesem Rechner nicht starten.",
                       "winget cannot be started on this machine."),
                Loc.Tr("Das Tool hat versucht, den App-Installer für diesen Benutzer zu registrieren und ihn "
                       + "von GitHub (github.com/microsoft/winget-cli) zu installieren - ohne Erfolg. Häufige "
                       + "Gründe: keine Internetverbindung, App-Installationen per Richtlinie gesperrt.\n\n"
                       + "Installiere den 'App-Installer' aus dem Microsoft Store oder das MSIX-Paket von GitHub "
                       + "von Hand und starte das Tool neu. Die versuchten Wege stehen ausführlich im Protokoll.",
                       "The tool tried to register the App Installer for this user and to install it from GitHub "
                       + "(github.com/microsoft/winget-cli) - without success. Common reasons: no internet "
                       + "connection, app installations blocked by policy.\n\n"
                       + "Install the 'App Installer' from the Microsoft Store or the MSIX package from GitHub "
                       + "manually and restart the tool. The attempted paths are listed in detail in the log."));

        // Stage 2: does the package source respond? If not: reset it, and if that
        // does not help, winget is usually too old for today's sources - then
        // update App Installer from GitHub.
        var problem = await CheckSources();
        if (problem is not null && !_cancel.IsCancelled)
        {
            await RepairSources();
            problem = await CheckSources();
        }
        if (problem is not null && !_cancel.IsCancelled && await InstallFromGitHub())
        {
            _launcher = null;
            if (await ResolveLauncher() is not null)
            {
                problem = await CheckSources();
                if (problem is not null) { await RepairSources(); problem = await CheckSources(); }
            }
        }
        if (problem is not null) return problem;

        var version = await InvokeWinget("--version", 20000);
        var nummer = version.Started && version.ExitCode == 0 ? version.Output.Trim() : "?";
        _log(Loc.Tr($"[INFO] winget ist einsatzbereit ({nummer}).", $"[INFO] winget is ready ({nummer})."));
        return WingetReadiness.Ok(nummer);
    }

    private async Task<bool> PackageExists(string id)
    {
        var r = await InvokeWinget($"show --exact --id {Quote(id)} --source winget --accept-source-agreements --disable-interactivity");
        return r.Started && r.ExitCode == 0;
    }

    // ---------------------------------------------------------------- Export

    private static readonly string[] ExcludeIdPatterns =
    {
        @"\.MaintenanceService",
        @"^Microsoft\.VCRedist\.",
        @"^Microsoft\.VCLibs\.",
        @"^Microsoft\.DotNet\.(Desktop)?Runtime\.",
        @"^Microsoft\.DotNet\.AspNetCore\.",
        @"^Microsoft\.WindowsAppRuntime\.",
        @"^Microsoft\.UI\.Xaml\.",
        @"^Microsoft\.EdgeWebView2Runtime$",
        @"^Microsoft\.Edge$",
        @"^Microsoft\.AppInstaller$"
    };

    private static readonly string[] IgnoreUnresolvedPatterns =
    {
        @"Maintenance Service",
        @"^Windows Package Manager Source",
        @"^Microsoft \.NET Native",
        @"^Microsoft \.NET.*Runtime",
        @"^Microsoft Windows Desktop Runtime",
        @"^Microsoft ASP\.NET Core",
        @"^Microsoft Visual C\+\+",
        @"^Microsoft Teams Meeting Add-in",
        @"^Microsoft Edge(\s|$)",
        @"^Copilot",
        @"^Feedback[- ]?Hub"
    };

    private static IEnumerable<string> KnownCandidates(string displayName)
    {
        var ids = new List<string>();
        string? locale = null;
        var m = Regex.Match(displayName, @"\((x64\s+|x86\s+|arm64\s+)?(?<loc>[a-z]{2,3}(-[A-Za-z]{2,4})?)\)");
        if (m.Success) locale = m.Groups["loc"].Value;

        if (Regex.IsMatch(displayName, @"^Mozilla Firefox ESR"))
        {
            if (locale != null) ids.Add($"Mozilla.Firefox.ESR.{locale}");
            ids.Add("Mozilla.Firefox.ESR");
        }
        else if (Regex.IsMatch(displayName, @"^Mozilla Firefox"))
        {
            if (locale != null) ids.Add($"Mozilla.Firefox.{locale}");
            ids.Add("Mozilla.Firefox");
        }
        else if (Regex.IsMatch(displayName, @"^Mozilla Thunderbird"))
        {
            if (locale != null) ids.Add($"Mozilla.Thunderbird.{locale}");
            ids.Add("Mozilla.Thunderbird");
        }
        else if (Regex.IsMatch(displayName, @"^Microsoft 365|^Microsoft Office"))
        {
            ids.Add("Microsoft.Office");
        }
        return ids;
    }

    public async Task ExportAsync(string backupPath)
    {
        var wingetDir = Path.Combine(backupPath, "Winget");
        Directory.CreateDirectory(wingetDir);
        var exportFile = Path.Combine(wingetDir, "Export.json");
        var unresolvedFile = Path.Combine(wingetDir, "Nicht-zugeordnete-Programme.txt");

        // winget writes to a temporary file. The existing Export.json is only
        // replaced once the new one is complete and usable.
        //
        // The old list used to be DELETED here first - before it was even clear
        // whether winget would start. If the export failed, the old list was gone
        // and there was no new one. But the later reinstall of all programs
        // depends on exactly this file.
        var neuFile = exportFile + ".neu";
        SafeDelete(neuFile);
        try
        {
            await ExportInto(neuFile, exportFile, unresolvedFile);
        }
        finally
        {
            SafeDelete(neuFile);
        }
    }

    private async Task ExportInto(string neuFile, string exportFile, string unresolvedFile)
    {
        if (await ResolveLauncher() is null) return;
        _log(Loc.Tr("[INFO] Starte nativen Winget-Export (das kann einen Moment dauern)...", "[INFO] Starting native winget export (this may take a moment)..."));

        var result = await InvokeWinget(
            $"export -o {Quote(neuFile)} --source winget --accept-source-agreements --disable-interactivity");
        if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Export durch Benutzer abgebrochen.", "[CANCELLED] Winget export cancelled by user.")); return; }
        if (!result.Started) { _log(Loc.Tr($"[FEHLER] winget konnte nicht gestartet werden: {result.StartError}", $"[ERROR] winget could not be started: {result.StartError}")); return; }

        // Collect programs from the output that could not be matched (with an ignore list)
        var unresolvedNames = new List<string>();
        foreach (var line in result.Output.Split('\n'))
        {
            if (Regex.IsMatch(line, "(verf.{1,2}gbar|available)"))
            {
                var mm = Regex.Match(line, @":\s*(?<name>\S.*)$");
                if (!mm.Success) continue;
                var name = mm.Groups["name"].Value.Trim();
                if (IgnoreUnresolvedPatterns.Any(p => Regex.IsMatch(name, p)))
                {
                    if (!_cancel.FastMode) _log(Loc.Tr($"[INFO] Ignoriert (System-/Begleitkomponente): {name}", $"[INFO] Ignored (system/companion component): {name}"));
                }
                else unresolvedNames.Add(name);
            }
        }

        if (!File.Exists(neuFile))
        {
            _log(Loc.Tr($"[FEHLER] 'winget export' hat keine Datei erzeugt (ExitCode: {result.ExitCode}).", $"[ERROR] 'winget export' did not produce a file (ExitCode: {result.ExitCode})."));
            if (!string.IsNullOrWhiteSpace(result.Output)) _log(Loc.Tr("[INFO] Winget-Ausgabe: ", "[INFO] Winget output: ") + result.Output.Trim());
            return;
        }

        WingetExport? json;
        try
        {
            json = JsonSerializer.Deserialize<WingetExport>(await File.ReadAllTextAsync(neuFile));
        }
        catch (Exception ex)
        {
            _log(Loc.Tr($"[FEHLER] Die erzeugte Export.json konnte nicht gelesen werden: {ex.Message}", $"[ERROR] The generated Export.json could not be read: {ex.Message}"));
            return;
        }
        if (json is null) { _log(Loc.Tr("[FEHLER] Export.json ist leer.", "[ERROR] Export.json is empty.")); return; }

        var totalPackages = json.Sources.Sum(s => s.Packages.Count);
        if (totalPackages == 0)
        {
            _log(Loc.Tr($"[FEHLER] Der Winget-Export enthält keine Pakete (ExitCode: {result.ExitCode}).", $"[ERROR] The winget export contains no packages (ExitCode: {result.ExitCode})."));
            _log(Loc.Tr("[INFO] Mögliche Ursache: winget kann im erhöhten Kontext die Paketliste nicht lesen. Bitte einmal 'winget list' in einer Administrator-Konsole ausführen und die Quellvereinbarung bestätigen, danach erneut versuchen.", "[INFO] Possible cause: winget cannot read the package list in the elevated context. Please run 'winget list' once in an administrator console and accept the source agreement, then try again."));
            return;
        }

        // Filter: only real Vendor.Product IDs, no add-on or runtime packages
        int keptTotal = 0;
        var skipped = new List<string>();
        foreach (var src in json.Sources)
        {
            var kept = new List<WingetPackage>();
            foreach (var pkg in src.Packages)
            {
                var id = pkg.PackageIdentifier ?? "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                bool isProperId = Regex.IsMatch(id, @"^[^\s{}]+\.[^\s{}]+$");
                bool isExcluded = ExcludeIdPatterns.Any(p => Regex.IsMatch(id, p));
                if (isProperId && !isExcluded) { pkg.Version = null; kept.Add(pkg); }
                else skipped.Add(id);
            }
            src.Packages = kept;
            keptTotal += kept.Count;
        }
        json.Sources = json.Sources.Where(s => s.Packages.Count > 0).ToList();

        if (skipped.Count > 0 && !_cancel.FastMode)
            foreach (var id in skipped) _log(Loc.Tr($"[INFO] Übersprungen (Zusatz-/Laufzeitpaket): {id}", $"[INFO] Skipped (companion/runtime package): {id}"));

        // Known list: rescue programs that were not matched but are known
        if (json.Sources.Count == 0) json.Sources.Add(new WingetSource());
        var primary = json.Sources[0];
        var existing = new HashSet<string>(
            json.Sources.SelectMany(s => s.Packages).Select(p => p.PackageIdentifier.ToLowerInvariant()));

        var stillUnresolved = new List<string>();
        foreach (var name in unresolvedNames.Distinct().OrderBy(x => x))
        {
            if (_cancel.IsCancelled) break;
            string? rescued = null;
            foreach (var cand in KnownCandidates(name))
            {
                if (existing.Contains(cand.ToLowerInvariant())) { rescued = cand; break; }
                if (await PackageExists(cand)) { rescued = cand; break; }
            }
            if (rescued != null)
            {
                if (!existing.Contains(rescued.ToLowerInvariant()))
                {
                    primary.Packages.Add(new WingetPackage { PackageIdentifier = rescued });
                    existing.Add(rescued.ToLowerInvariant());
                    keptTotal++;
                }
                _log(Loc.Tr($"[INFO] Zuordnung über Bekannt-Liste: {name} -> {rescued}", $"[INFO] Mapped via known-list: {name} -> {rescued}"));
            }
            else stillUnresolved.Add(name);
        }
        if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Export durch Benutzer abgebrochen.", "[CANCELLED] Winget export cancelled by user.")); return; }

        if (keptTotal == 0)
        {
            _log(Loc.Tr("[FEHLER] Nach dem Herausfiltern der Zusatzpakete sind keine Programme übrig geblieben.", "[ERROR] After filtering out companion packages, no programs remain."));
            return;
        }

        json.CreationDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        // Human-readable: without the encoder, "Notepad++" would be in the file as
        // "Notepad++". Both would be valid - but whoever looks through
        // the list before a reinstall should be able to read it.
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        // Write the finished list completely to the temporary file first, then
        // move it over the old one in one step. If writing already fails (disk
        // full), the old Export.json stays untouched.
        await File.WriteAllTextAsync(neuFile, JsonSerializer.Serialize(json, opts));
        File.Move(neuFile, exportFile, overwrite: true);
        SafeDelete(unresolvedFile);   // belongs to the old state
        _log(Loc.Tr($"[ERFOLG] {keptTotal} Programme exportiert nach: {exportFile} ({skipped.Count} Zusatzpakete herausgefiltert)", $"[SUCCESS] {keptTotal} programs exported to: {exportFile} ({skipped.Count} companion packages filtered out)"));

        if (stillUnresolved.Count > 0)
        {
            await File.WriteAllLinesAsync(unresolvedFile, stillUnresolved.Distinct().OrderBy(x => x));
            _log(Loc.Tr($"[WARNUNG] {stillUnresolved.Count} installierte Programme sind nicht im Winget-Katalog verfügbar (z.B. Eigenentwicklungen, Firmensoftware, o.Ä.).", $"[WARNING] {stillUnresolved.Count} installed programs are not available in the winget catalog (e.g. in-house or company software)."));
            _log(Loc.Tr($"[INFO] Liste gespeichert unter: {unresolvedFile} - diese müssen bei Bedarf manuell installiert werden.", $"[INFO] List saved to: {unresolvedFile} - these must be installed manually if needed."));
        }
    }

    /// <summary>Reads the exported packages for the selection during import.</summary>
    public static List<WingetPackage> ReadExportedPackages(string backupPath, out string status)
    {
        var file = Path.Combine(backupPath, "Winget", "Export.json");
        if (!File.Exists(file)) file = Path.Combine(backupPath, "Export.json");
        if (!File.Exists(file)) { status = "NotFound"; return new(); }

        try
        {
            var json = JsonSerializer.Deserialize<WingetExport>(File.ReadAllText(file));
            var pkgs = json?.Sources.SelectMany(s => s.Packages)
                          .Where(p => !string.IsNullOrWhiteSpace(p.PackageIdentifier))
                          .ToList() ?? new List<WingetPackage>();
            if (pkgs.Count == 0) { status = "Empty"; return new(); }
            status = "OK";
            return pkgs;
        }
        catch { status = "ParseError"; return new(); }
    }

    // ---------------------------------------------------------------- Import

    /// <summary>
    /// The package that gets installed on the new PC: Firefox ESR comes there as
    /// the current release ("Mozilla.Firefox.ESR.de" -> "Mozilla.Firefox.de").
    /// New PCs get the current release - an extra ESR next to it (or instead of
    /// it) would mean two Firefox installations with two profiles. The current
    /// release takes over the ESR profile on restore.
    /// </summary>
    public static string TargetPackage(string id)
    {
        var m = Regex.Match(id, @"^Mozilla\.Firefox\.ESR(?<rest>\.[A-Za-z]{2,3}(-[A-Za-z]{2,4})?)?$", RegexOptions.IgnoreCase);
        return m.Success ? "Mozilla.Firefox" + m.Groups["rest"].Value : id;
    }

    public async Task ImportAsync(IReadOnlyList<WingetPackage> selected)
    {
        if (selected is null || selected.Count == 0)
        {
            _log(Loc.Tr("[INFO] Winget Import abgebrochen oder keine Programme ausgewählt.", "[INFO] Winget import cancelled or no programs selected."));
            return;
        }
        if (await ResolveLauncher() is null) return;

        // ESR -> current release; duplicates (ESR and current in the list) only once.
        var liste = new List<WingetPackage>();
        foreach (var p in selected)
        {
            var ziel = TargetPackage(p.PackageIdentifier);
            if (!ziel.Equals(p.PackageIdentifier, StringComparison.OrdinalIgnoreCase))
                _log(Loc.Tr($"[INFO] {p.PackageIdentifier} -> {ziel}: Firefox kommt auf diesem PC in der aktuellen Fassung statt ESR. Das Firefox-Profil übernimmt sie.",
                            $"[INFO] {p.PackageIdentifier} -> {ziel}: Firefox is installed in the current release instead of ESR on this PC. The Firefox profile carries over."));
            if (!liste.Any(x => x.PackageIdentifier.Equals(ziel, StringComparison.OrdinalIgnoreCase)))
                liste.Add(new WingetPackage { PackageIdentifier = ziel, Version = ziel == p.PackageIdentifier ? p.Version : null });
        }
        selected = liste;

        _log(Loc.Tr($"[INFO] Starte Winget-Import für {selected.Count} Programme...", $"[INFO] Starting winget import for {selected.Count} programs..."));
        int ok = 0, skip = 0, fail = 0, i = 0;

        foreach (var pkg in selected)
        {
            i++;
            if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Import durch Benutzer abgebrochen.", "[CANCELLED] Winget import cancelled by user.")); break; }
            var id = pkg.PackageIdentifier;
            if (string.IsNullOrWhiteSpace(id)) continue;

            _log(Loc.Tr($"[{i}/{selected.Count}] Installiere {id} ...", $"[{i}/{selected.Count}] Installing {id} ..."));
            var r = await InstallAsync(id);
            if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Import durch Benutzer abgebrochen.", "[CANCELLED] Winget import cancelled by user.")); break; }

            if (!r.Started) { fail++; _log(Loc.Tr($"    -> [FEHLER] winget konnte nicht gestartet werden: {r.StartError}", $"    -> [ERROR] winget could not be started: {r.StartError}")); continue; }

            switch (r.ExitCode)
            {
                case WingetFehler.Ok:               ok++;   _log(Loc.Tr("    -> Erfolgreich installiert.", "    -> Installed successfully.")); break;
                case WingetFehler.SchonAktuell:     skip++; _log(Loc.Tr("    -> Bereits installiert und aktuell.", "    -> Already installed and up to date.")); break;
                case WingetFehler.SchonInstalliert: skip++; _log(Loc.Tr("    -> Bereits installiert.", "    -> Already installed.")); break;
                case WingetFehler.NichtGefunden:    fail++; _log(Loc.Tr("    -> [FEHLER] Paket wurde im Winget-Katalog nicht gefunden.", "    -> [ERROR] Package was not found in the winget catalog.")); break;
                default:
                    fail++;
                    _log(Loc.Tr($"    -> [FEHLER] {DescribeResult(r)}", $"    -> [ERROR] {DescribeResult(r)}"));
                    break;
            }

            // If it still fails because of winget itself after the repair, every
            // further package fails the same way. The list used to run to the end
            // anyway - ten times the same message.
            if (r.ExitCode != WingetFehler.Ok && WingetFehler.SourceBroken(r.ExitCode, r.Output))
            {
                var rest = selected.Skip(i).Count(p => !string.IsNullOrWhiteSpace(p.PackageIdentifier));
                if (rest > 0)
                {
                    fail += rest;
                    _log(Loc.Tr($"[FEHLER] winget lässt sich auch nach der Reparatur nicht benutzen - die übrigen {rest} Programme werden nicht versucht: "
                                + string.Join(", ", selected.Skip(i).Select(p => p.PackageIdentifier).Where(x => !string.IsNullOrWhiteSpace(x))),
                                $"[ERROR] winget is still unusable after the repair - the remaining {rest} programs are not attempted: "
                                + string.Join(", ", selected.Skip(i).Select(p => p.PackageIdentifier).Where(x => !string.IsNullOrWhiteSpace(x)))));
                }
                _log(Loc.Tr("[INFO] Abhilfe: den 'App-Installer' aus dem Microsoft Store oder von github.com/microsoft/winget-cli aktualisieren, "
                            + "in einer Konsole 'winget source reset --force' ausführen und den Winget-Import erneut starten.",
                            "[INFO] Remedy: update the 'App Installer' from the Microsoft Store or github.com/microsoft/winget-cli, "
                            + "run 'winget source reset --force' in a console and start the winget import again."));
                break;
            }
        }

        // The summary line only says [ERFOLG] if nothing really failed. It used to
        // always say [ERFOLG] - even for "0 installed, 5 failed".
        var kennung = fail > 0 ? Loc.Tr("[WARNUNG]", "[WARNING]") : Loc.Tr("[ERFOLG]", "[SUCCESS]");
        _log(Loc.Tr($"{kennung} Winget-Import abgeschlossen: {ok} installiert, {skip} übersprungen (bereits vorhanden), {fail} fehlgeschlagen.", $"{kennung} Winget import finished: {ok} installed, {skip} skipped (already present), {fail} failed."));
    }

    /// <summary>
    /// Installs a package from the "winget" source.
    ///
    /// If it fails not because of the package but because of winget itself -
    /// package source or its database broken or from a newer winget version
    /// (0x8A150009) -, winget is repaired and the package tried once more:
    /// first reset the sources, then update App Installer from GitHub. Each
    /// stage at most once per run.
    /// </summary>
    public async Task<ProcessResult> InstallAsync(string id)
    {
        var r = await InstallOnce(id);
        if (!IsBroken(r) || _cancel.IsCancelled) return r;

        var erklaerung = WingetFehler.Explanation(r.ExitCode, Loc.Tr);
        _log(Loc.Tr($"[WARNUNG] winget meldet {WingetFehler.Hex(r.ExitCode)}{(erklaerung is null ? "" : ": " + erklaerung)} "
                    + "Das betrifft jedes Paket - winget wird repariert, danach geht es weiter.",
                    $"[WARNING] winget reports {WingetFehler.Hex(r.ExitCode)}{(erklaerung is null ? "" : ": " + erklaerung)} "
                    + "This affects every package - winget is being repaired, then it continues."));

        if (!_quellenZurueckgesetzt)
        {
            _quellenZurueckgesetzt = true;
            await RepairSources();
            if (_cancel.IsCancelled) return r;
            r = await InstallOnce(id);
            if (!IsBroken(r) || _cancel.IsCancelled) return r;
        }

        // If resetting does not help, winget is too old for its data - the latest
        // version copes with every database.
        if (await InstallFromGitHub())
        {
            _launcher = null;
            if (await ResolveLauncher() is not null && !_cancel.IsCancelled)
            {
                await RepairSources();
                if (!_cancel.IsCancelled) r = await InstallOnce(id);
            }
        }
        return r;
    }

    private Task<ProcessResult> InstallOnce(string id) => InvokeWinget(
        $"install --exact --id {Quote(id)} --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");

    private static bool IsBroken(ProcessResult r) => r.Started && r.ExitCode != 0 && WingetFehler.SourceBroken(r.ExitCode, r.Output);

    /// <summary>"ExitCode -1978335223 (0x8A150009): explanation - last message from winget".</summary>
    public static string DescribeResult(ProcessResult r)
    {
        var text = $"ExitCode {r.ExitCode} ({WingetFehler.Hex(r.ExitCode)})";
        var erklaerung = WingetFehler.Explanation(r.ExitCode, Loc.Tr);
        if (erklaerung is not null) text += ": " + erklaerung;
        var meldung = WingetFehler.LastMessage(r.Output);
        if (meldung.Length > 0) text += (erklaerung is null ? ": " : " - ") + meldung;
        return text;
    }

    // ---------------------------------------------------------------- Helpers

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
    private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
