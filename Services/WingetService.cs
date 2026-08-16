using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Portierung der Export-/Import-Logik aus dem PowerShell-Skript:
/// - robustes Aufloesen eines lauffaehigen winget-Startwegs
/// - Selbstregistrierung des App-Installer-Pakets, falls winget fuer den
///   aktuellen Benutzer nicht registriert ist
/// - nativer "winget export" mit Filterung von Zusatz-/Laufzeitpaketen
/// - Rettung bekannter Programme (Firefox de, Office ...) ueber eine
///   verifizierte Bekannt-Liste
/// - Import: jedes Paket einzeln per "winget install --exact --id"
/// </summary>
public sealed class WingetService
{
    private readonly ProcessRunner _runner;
    private readonly CancellationTokenState _cancel;
    private readonly Action<string> _log;

    private (string Exe, string Mode)? _launcher;

    public WingetService(ProcessRunner runner, CancellationTokenState cancel, Action<string> log)
    {
        _runner = runner;
        _cancel = cancel;
        _log = log;
    }

    // ---------------------------------------------------------------- Launcher

    private IEnumerable<string> GetCandidatePaths()
    {
        var list = new List<string>();

        // 1) PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), "winget.exe");
            if (File.Exists(p)) list.Add(p);
        }

        // 2) App-Execution-Alias (bewusst ohne File.Exists: Reparse-Point)
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        list.Add(Path.Combine(local, @"Microsoft\WindowsApps\winget.exe"));

        // 3) Installationsordner des App-Installer-Pakets
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
                var dirs = Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                    .OrderByDescending(d => d);
                results.AddRange(dirs);
            }
        }
        catch { /* Zugriff auf WindowsApps evtl. eingeschraenkt */ }
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

        // Add-AppxPackage steht nur ueber Windows PowerShell (powershell.exe)
        // zuverlaessig zur Verfuegung -> ueber den Prozess-Runner aufrufen.
        async Task<bool> RunPs(string psCommand)
        {
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCommand.Replace("\"", "\\\"")}\"";
            var r = await _runner.RunAsync("powershell.exe", args, timeoutMs: 60000);
            return r.Started && r.ExitCode == 0;
        }

        // Variante 1: RegisterByFamilyName (neuere Windows-Builds)
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

        // Variante 2: klassisch ueber das AppxManifest im Paketordner
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

        _log(Loc.Tr("[FEHLER] winget konnte mit keiner Methode gestartet werden. Versuchte Wege:", "[ERROR] winget could not be started by any method. Attempted paths:"));
        foreach (var a in attempts.Distinct()) _log("    " + a);
        _log(Loc.Tr("[INFO] Bitte einmal unter dem Benutzer, der dieses Tool startet, 'winget --version' in einer Konsole ausführen. Falls der Befehl unbekannt ist, den 'App-Installer' im Microsoft Store aktualisieren und danach erneut versuchen.", "[INFO] Please run 'winget --version' once in a console under the user that starts this tool. If the command is unknown, update the 'App Installer' from the Microsoft Store and try again."));
        return null;
    }

    private async Task<ProcessResult> InvokeWinget(string args, int timeoutMs = 0)
    {
        var launcher = await ResolveLauncher();
        if (launcher is null)
            return new ProcessResult(-1, "", "winget ist in dieser Umgebung nicht ausfuehrbar (Details siehe Log).");
        return await RunWingetRaw(launcher.Value.Exe, launcher.Value.Mode, args, timeoutMs);
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
        SafeDelete(exportFile);
        SafeDelete(unresolvedFile);

        if (await ResolveLauncher() is null) return;
        _log(Loc.Tr("[INFO] Starte nativen Winget-Export (das kann einen Moment dauern)...", "[INFO] Starting native winget export (this may take a moment)..."));

        var result = await InvokeWinget(
            $"export -o {Quote(exportFile)} --source winget --accept-source-agreements --disable-interactivity");
        if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Export durch Benutzer abgebrochen.", "[CANCELLED] Winget export cancelled by user.")); return; }
        if (!result.Started) { _log(Loc.Tr($"[FEHLER] winget konnte nicht gestartet werden: {result.StartError}", $"[ERROR] winget could not be started: {result.StartError}")); return; }

        // Nicht zuordenbare Programme aus der Ausgabe einsammeln (mit Ignorier-Liste)
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

        if (!File.Exists(exportFile))
        {
            _log(Loc.Tr($"[FEHLER] 'winget export' hat keine Datei erzeugt (ExitCode: {result.ExitCode}).", $"[ERROR] 'winget export' did not produce a file (ExitCode: {result.ExitCode})."));
            if (!string.IsNullOrWhiteSpace(result.Output)) _log(Loc.Tr("[INFO] Winget-Ausgabe: ", "[INFO] Winget output: ") + result.Output.Trim());
            return;
        }

        WingetExport? json;
        try
        {
            json = JsonSerializer.Deserialize<WingetExport>(await File.ReadAllTextAsync(exportFile));
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
            SafeDelete(exportFile);
            return;
        }

        // Filter: nur echte Hersteller.Produkt-IDs, keine Zusatz-/Laufzeitpakete
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

        // Bekannt-Liste: nicht zugeordnete, aber bekannte Programme retten
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
            SafeDelete(exportFile);
            return;
        }

        json.CreationDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var opts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        await File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(json, opts));
        _log(Loc.Tr($"[ERFOLG] {keptTotal} Programme exportiert nach: {exportFile} ({skipped.Count} Zusatzpakete herausgefiltert)", $"[SUCCESS] {keptTotal} programs exported to: {exportFile} ({skipped.Count} companion packages filtered out)"));

        if (stillUnresolved.Count > 0)
        {
            await File.WriteAllLinesAsync(unresolvedFile, stillUnresolved.Distinct().OrderBy(x => x));
            _log(Loc.Tr($"[WARNUNG] {stillUnresolved.Count} installierte Programme sind nicht im Winget-Katalog verfügbar (z.B. Eigenentwicklungen, Firmensoftware, o.Ä.).", $"[WARNING] {stillUnresolved.Count} installed programs are not available in the winget catalog (e.g. in-house or company software)."));
            _log(Loc.Tr($"[INFO] Liste gespeichert unter: {unresolvedFile} - diese müssen bei Bedarf manuell installiert werden.", $"[INFO] List saved to: {unresolvedFile} - these must be installed manually if needed."));
        }
    }

    /// <summary>Liest die exportierten Pakete fuer die Auswahl beim Import.</summary>
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

    public async Task ImportAsync(IReadOnlyList<WingetPackage> selected)
    {
        if (selected is null || selected.Count == 0)
        {
            _log(Loc.Tr("[INFO] Winget Import abgebrochen oder keine Programme ausgewählt.", "[INFO] Winget import cancelled or no programs selected."));
            return;
        }
        if (await ResolveLauncher() is null) return;

        _log(Loc.Tr($"[INFO] Starte Winget-Import für {selected.Count} Programme...", $"[INFO] Starting winget import for {selected.Count} programs..."));
        int ok = 0, skip = 0, fail = 0, i = 0;

        foreach (var pkg in selected)
        {
            i++;
            if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Import durch Benutzer abgebrochen.", "[CANCELLED] Winget import cancelled by user.")); break; }
            var id = pkg.PackageIdentifier;
            if (string.IsNullOrWhiteSpace(id)) continue;

            _log(Loc.Tr($"[{i}/{selected.Count}] Installiere {id} ...", $"[{i}/{selected.Count}] Installing {id} ..."));
            var r = await InvokeWinget(
                $"install --exact --id {Quote(id)} --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
            if (_cancel.IsCancelled) { _log(Loc.Tr("[ABBRUCH] Winget-Import durch Benutzer abgebrochen.", "[CANCELLED] Winget import cancelled by user.")); break; }

            if (!r.Started) { fail++; _log(Loc.Tr($"    -> [FEHLER] winget konnte nicht gestartet werden: {r.StartError}", $"    -> [ERROR] winget could not be started: {r.StartError}")); continue; }

            switch (r.ExitCode)
            {
                case 0:            ok++;   _log(Loc.Tr("    -> Erfolgreich installiert.", "    -> Installed successfully.")); break;
                case -1978335189:  skip++; _log(Loc.Tr("    -> Bereits installiert und aktuell.", "    -> Already installed and up to date.")); break;
                case -1978335135:  skip++; _log(Loc.Tr("    -> Bereits installiert.", "    -> Already installed.")); break;
                case -1978335212:  fail++; _log(Loc.Tr("    -> [FEHLER] Paket wurde im Winget-Katalog nicht gefunden.", "    -> [ERROR] Package was not found in the winget catalog.")); break;
                default:
                    fail++;
                    var tail = string.Join(" | ",
                        r.Output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(2).Select(l => l.Trim()));
                    _log(Loc.Tr($"    -> [FEHLER] ExitCode {r.ExitCode}. {tail}", $"    -> [ERROR] ExitCode {r.ExitCode}. {tail}"));
                    break;
            }
        }

        _log(Loc.Tr($"[ERFOLG] Winget-Import abgeschlossen: {ok} installiert, {skip} übersprungen (bereits vorhanden), {fail} fehlgeschlagen.", $"[SUCCESS] Winget import finished: {ok} installed, {skip} skipped (already present), {fail} failed."));
    }

    // ---------------------------------------------------------------- Helpers

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
    private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
