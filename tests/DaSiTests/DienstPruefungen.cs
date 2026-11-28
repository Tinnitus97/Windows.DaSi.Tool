using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Tests for the services that had none of their own at first: language,
/// system helpers, cancellation and performance mode, process start, update
/// over HTTP and installing winget.
/// </summary>
internal static partial class Program
{
    /// <summary>Collects a service's log lines - like the program's window does.</summary>
    private sealed class Protokoll
    {
        private readonly List<string> _zeilen = new();
        public void Write(string z) { lock (_zeilen) _zeilen.Add(z); }
        public List<string> Zeilen { get { lock (_zeilen) return _zeilen.ToList(); } }
        public bool Has(string teil) => Zeilen.Any(z => z.Contains(teil, StringComparison.OrdinalIgnoreCase));
        public int Count(string teil) => Zeilen.Count(z => z.Contains(teil, StringComparison.OrdinalIgnoreCase));
        public string Text => string.Join(" | ", Zeilen).Replace("\r", "").Replace("\n", " / ");
        public void Clear() { lock (_zeilen) _zeilen.Clear(); }
    }

    private static void LocalizerCheck()
    {
        Console.WriteLine("\n== Sprache (Localizer) ==");
        var vorher = Localizer.I.Lang;
        int meldungen = 0;
        Action zaehlen = () => meldungen++;
        Localizer.I.LanguageChanged += zaehlen;
        try
        {
            Localizer.I.Lang = AppLang.En;
            Check("Englisch: Tr liefert den englischen Text", Loc.Tr("Ja", "Yes") == "Yes" && Localizer.I.Tr("a", "b") == "b");
            Localizer.I.Lang = AppLang.En;   // no change - no event
            Localizer.I.Lang = AppLang.De;
            Check("Deutsch: Tr liefert den deutschen Text", Loc.Tr("Ja", "Yes") == "Ja");
            Check("jeder echte Sprachwechsel meldet sich genau einmal", meldungen == 2, $"{meldungen} Meldungen");
        }
        finally
        {
            Localizer.I.LanguageChanged -= zaehlen;
            Localizer.I.Lang = vorher;
        }
    }

    private static void SystemHelpersCheck(string root)
    {
        Console.WriteLine("\n== Systemhelfer ==");
        var enc = SystemHelpers.ConsoleEncoding;
        Check("Konsolen-Kodierung wird ermittelt und gemerkt", enc is not null && ReferenceEquals(enc, SystemHelpers.ConsoleEncoding));
        if (OperatingSystem.IsWindows())
            Check("Konsolen-Kodierung ist die OEM-Codepage (nicht der UTF-8-Rueckfall)", enc!.CodePage != 65001, $"CodePage {enc!.CodePage}");

        var appDir = SystemHelpers.GetApplicationDirectory();
        Check("Programmordner = Ordner der laufenden EXE", Directory.Exists(appDir)
            && string.Equals(Path.GetFullPath(appDir).TrimEnd('\\', '/'),
                             Path.GetFullPath(Path.GetDirectoryName(Environment.ProcessPath)!).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
            appDir);

        bool hell = SystemHelpers.IsWindowsLightTheme();
        if (OperatingSystem.IsWindows())
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var erwartet = k?.GetValue("AppsUseLightTheme") is int v && v != 0;
            Check("Hell/Dunkel wie in der Registry (AppsUseLightTheme)", hell == erwartet, $"{hell} / {erwartet}");
        }
        else Check("ohne Registry: dunkel", !hell);

        // The test project runs with the invariant culture - which is neither de
        // nor en, so the fallback applies. The UI tests check de/en.
        Check("Sprache: unbekannte Kultur -> Deutsch", SystemHelpers.GetWindowsLanguage() == "de");

        var profile = SystemHelpers.GetUserProfiles();
        Check("Profilliste: keine System- oder Dienstprofile",
            !profile.Any(p => new[] { "Default", "Public", "All Users", "Default User" }.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)),
            string.Join(", ", profile));
        if (OperatingSystem.IsWindows())
        {
            var eigenes = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check("Profilliste enthaelt das eigene Profil", profile.Contains(eigenes, StringComparer.OrdinalIgnoreCase), string.Join(", ", profile));
            Check("Profilliste ist nach Namen sortiert",
                profile.SequenceEqual(profile.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)));
        }

        bool akku = SystemHelpers.IsOnBattery(out var prozent);
        Check("Akkustand: -1 (unbekannt) oder 0-100", prozent >= -1 && prozent <= 100, $"{akku}, {prozent} %");
        if (OperatingSystem.IsWindows()) Check("Pruefrechner ohne Akku gilt nicht als Akkubetrieb", !akku);

        Check("freier Platz im Temp-Ordner > 0", SystemHelpers.GetFreeSpace(Path.GetTempPath()) > 0);
        Check("freier Platz: leerer Pfad -> -1", SystemHelpers.GetFreeSpace("") == -1);
        if (OperatingSystem.IsWindows())
        {
            var frei = "QRSTUVWXYZ".Select(c => c + @":\").FirstOrDefault(l => !DriveInfo.GetDrives().Any(d => string.Equals(d.Name, l, StringComparison.OrdinalIgnoreCase)));
            if (frei is not null) Check($"freier Platz: Laufwerk {frei} fehlt -> -1", SystemHelpers.GetFreeSpace(frei + "x") == -1);
        }

        // Folder size with exclusion and cancellation.
        var baum = Path.Combine(root, "groesse");
        Directory.CreateDirectory(Path.Combine(baum, "a", "tief"));
        Directory.CreateDirectory(Path.Combine(baum, "weg"));
        File.WriteAllBytes(Path.Combine(baum, "eins.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(baum, "a", "zwei.bin"), new byte[234]);
        File.WriteAllBytes(Path.Combine(baum, "a", "tief", "drei.bin"), new byte[66]);
        File.WriteAllBytes(Path.Combine(baum, "weg", "gross.bin"), new byte[50_000]);
        Check("Ordnergroesse: alles", SystemHelpers.GetDirectorySize(baum) == 51_300, SystemHelpers.GetDirectorySize(baum).ToString());
        Check("Ordnergroesse: ausgeschlossener Ordner zaehlt nicht",
            SystemHelpers.GetDirectorySize(baum, new[] { Path.Combine(baum, "weg") }) == 1_300);
        Check("Ordnergroesse: fehlender Ordner -> 0", SystemHelpers.GetDirectorySize(Path.Combine(root, "gibt-es-nicht")) == 0);
        using (var abbruch = new CancellationTokenSource())
        {
            abbruch.Cancel();
            bool geworfen = false;
            try { SystemHelpers.GetDirectorySize(baum, ct: abbruch.Token); }
            catch (OperationCanceledException) { geworfen = true; }
            Check("Ordnergroesse: Abbruch wird befolgt", geworfen);
        }
        if (OperatingSystem.IsWindows())
        {
            // A link to the folder itself must not cause an endless loop.
            var schleife = Path.Combine(baum, "a", "schleife");
            try
            {
                Directory.CreateSymbolicLink(schleife, baum);
                Check("Ordnergroesse: Verknuepfungen werden ausgelassen", SystemHelpers.GetDirectorySize(baum) == 51_300);
            }
            catch (Exception ex) { Console.WriteLine($"         (Verknuepfung nicht anlegbar: {ex.Message})"); }
        }

        Check("Groesse lesbar: -1 -> ?", SystemHelpers.FormatBytes(-1) == "?");
        Check("Groesse lesbar: 0 B", SystemHelpers.FormatBytes(0) == "0 B");
        Check("Groesse lesbar: 1536 -> 1.5 KB", SystemHelpers.FormatBytes(1536) == "1.5 KB", SystemHelpers.FormatBytes(1536));
        Check("Groesse lesbar: 5 GB", SystemHelpers.FormatBytes(5L * 1024 * 1024 * 1024) == "5.0 GB");

        Check("NTFS: leerer Pfad -> nein", !SystemHelpers.IsNtfsDrive(""));
        if (OperatingSystem.IsWindows())
        {
            Check("NTFS: Systemlaufwerk", SystemHelpers.IsNtfsDrive(Environment.SystemDirectory));
            Check("NTFS: Netzwerkpfad gilt als geeignet", SystemHelpers.IsNtfsDrive(@"\\server\freigabe\ordner"));
            Check("Oeffnen: eine Datei, die es nicht gibt -> false (nichts geht auf)",
                !SystemHelpers.Open(Path.Combine(root, "gibt-es-nicht.dasi-unbekannt")));
        }
    }

    private static void CancellationAndPerformanceCheck()
    {
        Console.WriteLine("\n== Abbruch und Leistungsmodus ==");
        var c = new CancellationTokenState();
        Check("Abbruch: anfangs nicht abgebrochen, schneller Modus an", !c.IsCancelled && c.FastMode);
        c.RequestCancel();
        Check("Abbruch: angefordert", c.IsCancelled);
        c.Reset();
        Check("Abbruch: zurueckgesetzt", !c.IsCancelled);

        // KillAll ends exactly the registered processes.
        Process LongRunner() => OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!
            : Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        using var angemeldet = LongRunner();
        using var abgemeldet = LongRunner();
        c.Register(angemeldet.Id);
        c.Register(abgemeldet.Id);
        c.Unregister(abgemeldet.Id);
        c.KillAll();
        Check("KillAll beendet den angemeldeten Prozess", angemeldet.WaitForExit(10_000));
        Check("KillAll laesst abgemeldete Prozesse in Ruhe", !abgemeldet.HasExited);
        try { abgemeldet.Kill(true); } catch { }

        var log = new Protokoll();
        var vorher = Process.GetCurrentProcess().PriorityClass;
        using (var perf = new PerformanceMode(log.Write))
        {
            perf.Enable();
            if (OperatingSystem.IsWindows())
            {
                Check("Leistungsmodus: Standby unterdrueckt (Protokoll)", log.Has("Leistungsmodus aktiv"), log.Text);
                Check("Leistungsmodus: Prioritaet angehoben", Process.GetCurrentProcess().PriorityClass == ProcessPriorityClass.AboveNormal);
            }
            perf.Dispose();
            perf.Dispose();   // calling it twice must not do anything twice
        }
        Check("Leistungsmodus: Prioritaet wie vorher", Process.GetCurrentProcess().PriorityClass == vorher);
        Check("Leistungsmodus: genau einmal beendet", log.Count("Leistungsmodus beendet") == 1, log.Text);
    }

    private static void ProcessesCheck(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Prozesse starten (ProcessRunner) ==");
        var c = new CancellationTokenState();
        var r = new ProcessRunner(c);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var a = r.RunAsync(cmd, "/c echo hallo-aus & echo hallo-fehler 1>&2 & exit 3").GetAwaiter().GetResult();
        Check("Ausgabe und Fehlerausgabe werden gesammelt, Exit-Code stimmt",
            a.Started && a.ExitCode == 3 && a.Output.Contains("hallo-aus") && a.Output.Contains("hallo-fehler"), $"{a.ExitCode}: {a.Output}");

        var wd = Path.Combine(root, "arbeitsordner");
        Directory.CreateDirectory(wd);
        var b = r.RunAsync(cmd, "/c cd", workingDirectory: wd).GetAwaiter().GetResult();
        Check("Arbeitsordner wird gesetzt", b.Output.Trim().Equals(wd, StringComparison.OrdinalIgnoreCase), b.Output.Trim());

        var fehlt = r.RunAsync(Path.Combine(root, "gibt-es-nicht.exe"), "").GetAwaiter().GetResult();
        Check("fehlendes Programm: nicht gestartet, mit Grund", !fehlt.Started && fehlt.ExitCode == -1 && !string.IsNullOrEmpty(fehlt.StartError));

        var uhr = Stopwatch.StartNew();
        var zeit = r.RunAsync("ping.exe", "-n 30 127.0.0.1", timeoutMs: 700).GetAwaiter().GetResult();
        Check("Zeitgrenze: Prozess wird beendet und gemeldet", zeit.StartError == "Zeitüberschreitung" && uhr.Elapsed.TotalSeconds < 15,
            $"{zeit.StartError}, {uhr.Elapsed.TotalSeconds:F1} s");

        uhr.Restart();
        var lauf = r.RunAsync("ping.exe", "-n 30 127.0.0.1");
        Thread.Sleep(600);
        c.RequestCancel();
        var abgebrochen = lauf.GetAwaiter().GetResult();
        Check("Abbruch beendet den laufenden Prozess", uhr.Elapsed.TotalSeconds < 15, $"{uhr.Elapsed.TotalSeconds:F1} s");
        c.Reset();

        var viaCmd = r.RunViaCmdAsync(cmd, "/c echo ueber-cmd").GetAwaiter().GetResult();
        Check("ueber cmd: Ausgabe kommt aus der Zwischendatei", viaCmd.Started && viaCmd.ExitCode == 0 && viaCmd.Output.Contains("ueber-cmd"), viaCmd.Output);
        var viaZeit = r.RunViaCmdAsync("ping.exe", "-n 30 127.0.0.1", timeoutMs: 700).GetAwaiter().GetResult();
        Check("ueber cmd: Zeitgrenze", viaZeit.StartError == "Zeitüberschreitung");
        var viaAbbruch = r.RunViaCmdAsync("ping.exe", "-n 30 127.0.0.1");
        Thread.Sleep(600);
        c.RequestCancel();
        uhr.Restart();
        viaAbbruch.GetAwaiter().GetResult();
        Check("ueber cmd: Abbruch", uhr.Elapsed.TotalSeconds < 15);
        c.Reset();

        // robocopy: in fast mode only count, otherwise every line goes to the log.
        var quelle = Path.Combine(root, "robo-zaehlen", "quelle");
        Directory.CreateDirectory(quelle);
        for (int i = 0; i < 60; i++) File.WriteAllText(Path.Combine(quelle, $"datei{i:00}.txt"), "x" + i);
        var log = new Protokoll();
        var zaehler = new List<int>();
        int code = r.RunRobocopyAsync($"\"{quelle}\" \"{Path.Combine(root, "robo-zaehlen", "ziel1")}\" /E /NP /NJH",
                                      log.Write, n => { lock (zaehler) zaehler.Add(n); }).GetAwaiter().GetResult();
        Check("robocopy: Exit-Code < 8", code is > 0 and < 8, code.ToString());
        // robocopy's indented lines are counted - files and folders. This is about
        // progress, not an exact summary.
        Check("robocopy schnell: gezaehlt, alle 25 gemeldet, Endstand mit allen 60 Dateien",
            zaehler.Count >= 3 && zaehler[0] == 25 && zaehler[1] == 50 && zaehler.Last() >= 60, string.Join(",", zaehler));
        Check("robocopy schnell: keine Dateizeilen im Protokoll", !log.Has("datei07.txt"), log.Text);

        c.FastMode = false;
        log.Clear();
        code = r.RunRobocopyAsync($"\"{quelle}\" \"{Path.Combine(root, "robo-zaehlen", "ziel2")}\" /E /NP",
                                  log.Write, _ => { }).GetAwaiter().GetResult();
        Check("robocopy ausfuehrlich: Dateizeilen im Protokoll", log.Has("datei07.txt"), log.Text.Length > 300 ? log.Text[..300] : log.Text);
        c.FastMode = true;

        c.RequestCancel();
        Check("robocopy nach Abbruch: startet gar nicht (999)",
            r.RunRobocopyAsync($"\"{quelle}\" \"{Path.Combine(root, "robo-zaehlen", "ziel3")}\"", log.Write).GetAwaiter().GetResult() == 999
            && !Directory.Exists(Path.Combine(root, "robo-zaehlen", "ziel3")));
        c.Reset();
    }

    private static void UpdateOverHttpCheck(string root)
    {
        Console.WriteLine("\n== Update ueber HTTP (eigener Server) ==");
        using var server = new MiniHttp();
        var exe = new byte[300_000];
        new Random(7).NextBytes(exe);
        var exeDatei = Path.Combine(root, "update-quelle.bin");
        File.WriteAllBytes(exeDatei, exe);
        var sha = UpdateService.ComputeSha256(exeDatei);
        server.Set("update.json", $$"""{ "program": { "version": "9.9.9", "url": "{{server.Basis}}neu.exe", "sha256": "{{sha.ToUpperInvariant()}}" } }""");
        server.Set("kaputt.json", "{ nicht json");
        server.Set("neu.exe", exe);

        var m = UpdateService.FetchAsync(server.Basis + "update.json", CancellationToken.None).GetAwaiter().GetResult();
        Check("Abfrage: update.json gelesen", m.Success && m.Program.Version == "9.9.9" && m.Program.Sha256 == sha, m.Error ?? m.Program.Version);
        var m404 = UpdateService.FetchAsync(server.Basis + "fehlt.json", CancellationToken.None).GetAwaiter().GetResult();
        Check("Abfrage: 404 -> kein Erfolg, mit Grund", !m404.Success && !string.IsNullOrEmpty(m404.Error));
        var mKaputt = UpdateService.FetchAsync(server.Basis + "kaputt.json", CancellationToken.None).GetAwaiter().GetResult();
        Check("Abfrage: unlesbares JSON -> kein Erfolg", !mKaputt.Success);
        bool geworfen = false;
        using (var abbruch = new CancellationTokenSource())
        {
            abbruch.Cancel();
            try { UpdateService.FetchAsync(server.Basis + "update.json", abbruch.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { geworfen = true; }
        }
        Check("Abfrage: Abbruch wird weitergereicht", geworfen);

        var ziel = Path.Combine(root, "update-ziel", "neu.exe");
        var d = UpdateService.DownloadAsync(server.Basis + "neu.exe", sha, ziel, CancellationToken.None).GetAwaiter().GetResult();
        Check("Download: Pruefsumme stimmt, Datei vollstaendig", d.Success && d.Bytes == exe.Length && File.ReadAllBytes(ziel).AsSpan().SequenceEqual(exe), d.Error);
        var falsch = UpdateService.DownloadAsync(server.Basis + "neu.exe", new string('0', 64), ziel, CancellationToken.None).GetAwaiter().GetResult();
        Check("Download: falsche Pruefsumme -> abgelehnt und Datei geloescht",
            !falsch.Success && falsch.Error!.Contains("Prüfsumme") && !File.Exists(ziel), falsch.Error);
        var ohne = UpdateService.DownloadAsync(server.Basis + "neu.exe", "", ziel, CancellationToken.None).GetAwaiter().GetResult();
        Check("Download: ohne Pruefsumme wird nur geladen", ohne.Success && File.Exists(ziel));
        var nichtDa = UpdateService.DownloadAsync(server.Basis + "fehlt.exe", sha, ziel, CancellationToken.None).GetAwaiter().GetResult();
        Check("Download: 404 -> abgelehnt, nichts liegen gelassen", !nichtDa.Success && !File.Exists(ziel));
        geworfen = false;
        using (var abbruch = new CancellationTokenSource())
        {
            abbruch.Cancel();
            try { UpdateService.DownloadAsync(server.Basis + "neu.exe", sha, ziel, abbruch.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { geworfen = true; }
        }
        Check("Download: Abbruch wird weitergereicht", geworfen);

        if (OperatingSystem.IsWindows())
        {
            // StartUpdateScript: exactly the way the program calls it.
            var neu = Path.Combine(root, "start-skript", "neu", "WindowsDaSiTool.exe");
            var alt = Path.Combine(root, "start-skript", "alt", "WindowsDaSiTool.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(neu)!);
            Directory.CreateDirectory(Path.GetDirectoryName(alt)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), neu);
            File.WriteAllText(alt, "alte Fassung");
            var skript = UpdateService.WriteUpdateScript(neu, alt);
            UpdateService.StartUpdateScript(skript, neu, alt);
            var bis = DateTime.Now.AddSeconds(40);
            while (DateTime.Now < bis && File.Exists(neu)) Thread.Sleep(250);
            Thread.Sleep(500);
            Check("StartUpdateScript: das Skript tauscht die Datei aus",
                !File.Exists(neu) && File.ReadAllBytes(alt).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "hostname.exe"))));
            UpdateService.CleanUp();
        }
    }

    private static void WingetInstallerCheck(string root)
    {
        Console.WriteLine("\n== winget nachinstallieren (eigener Server statt GitHub) ==");
        var log = new Protokoll();
        if (!OperatingSystem.IsWindows())
        {
            Check("ausserhalb von Windows: nichts", !WingetInstaller.InstallAsync(log.Write, () => false).GetAwaiter().GetResult());
            return;
        }
        using var server = new MiniHttp();
        // Dependencies as in the real package: one folder per architecture.
        var zipDatei = Path.Combine(root, "abhaengigkeiten.zip");
        using (var zip = ZipFile.Open(zipDatei, ZipArchiveMode.Create))
            foreach (var arch in new[] { "x64", "arm64", "x86" })
            {
                using var w = new StreamWriter(zip.CreateEntry($"{arch}/Microsoft.VCLibs.140.00_14.0.33519.0_{arch}.appx").Open());
                w.Write("keine echte appx");
            }
        server.Set(WingetInstaller.DependenciesFile, File.ReadAllBytes(zipDatei));
        var bundle = new byte[600_000];
        server.Set(WingetInstaller.BundleFile, bundle);

        // Real download, real PowerShell - only the package is a fake that Windows
        // rejects. Nothing gets installed.
        var ok = WingetInstaller.InstallAsync(log.Write, () => false, default, server.Basis).GetAwaiter().GetResult();
        Check("Download von Abhaengigkeiten und App-Installer", log.Has("Abhängigkeiten: geladen") && log.Has("App-Installer: geladen"), log.Text);
        Check("Fortschritt beim Laden wird gemeldet", log.Has("25 %"), log.Text);
        Check("Abhaengigkeiten fuer diese Architektur gefunden", log.Has("samt 1 Abhängigkeiten"), log.Text);
        Check("unbrauchbares Paket: Windows lehnt ab, Warnung statt Absturz", !ok && log.Has("[WARNUNG] Der App-Installer ließ sich nicht installieren"), log.Text);

        log.Clear();
        server.Set(WingetInstaller.BundleFile, "weg", 404);
        ok = WingetInstaller.InstallAsync(log.Write, () => false, default, server.Basis).GetAwaiter().GetResult();
        Check("Download scheitert (404): Warnung, kein Absturz", !ok && log.Has("ließ sich nicht von GitHub nachinstallieren"), log.Text);

        log.Clear();
        ok = WingetInstaller.InstallAsync(log.Write, () => true, default, server.Basis).GetAwaiter().GetResult();
        Check("Abbruch waehrend des Ladens", !ok && !log.Has("geladen ("), log.Text);
    }
}
