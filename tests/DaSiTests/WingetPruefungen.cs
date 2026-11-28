using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// WingetService against the winget fake (tests/WingetAttrappe): readiness,
/// repair, export with filter and known list, import with every outcome.
/// </summary>
internal static partial class Program
{
    private static Dictionary<string, string> Scenario(params (string K, string V)[] werte)
        => werte.ToDictionary(w => w.K, w => w.V);

    private static void WingetServiceCheck(string root)
    {
        // Windows only: winget reports 32-bit return codes (0x8A15...), other
        // systems truncate exit codes to 8 bit.
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== winget (Attrappe statt echtem winget) ==");
        var attrappe = Attrappen.WingetFake();
        if (attrappe is null)
        {
            Check("winget-Attrappe ist gebaut (dotnet build tests/WingetAttrappe)", false);
            return;
        }

        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        WingetService NewService(Func<System.Threading.Tasks.Task<bool>>? nach = null)
            => new(runner, c, log.Write) { FesteKandidaten = new[] { attrappe }, Nachinstallation = nach ?? (() => System.Threading.Tasks.Task.FromResult(false)) };

        // ---- Readiness ----
        var ordner = Attrappen.WingetScenario(root, Scenario());
        var w = NewService();
        var stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("bereit: Version erkannt", stand.Ready && stand.Version == "v1.12.460", $"{stand.Problem} {log.Text}");
        Check("bereit: Startweg im Protokoll", log.Has("Verwende winget v1.12.460") && log.Has("Startmethode: Direct"), log.Text);
        Check("bereit: Paketquelle gefragt", Attrappen.Calls(ordner).Any(a => a.StartsWith("source list")));

        // winget does not start - registration (real) and reinstall (replaced) do not help.
        log.Clear();
        ordner = Attrappen.WingetScenario(root, Scenario(("version.code", "1"), ("version", "kaputt")));
        int nachinstalliert = 0;
        w = NewService(() => { nachinstalliert++; return System.Threading.Tasks.Task.FromResult(false); });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("startet nicht: nicht bereit, mit Hinweis", !stand.Ready && stand.Problem!.Contains("nicht starten") && !string.IsNullOrEmpty(stand.Hint), stand.Problem);
        Check("startet nicht: beide Startwege versucht", log.Has("[Direct]") && log.Has("[Cmd]"), log.Text);
        Check("startet nicht: Registrierung versucht", log.Has("Versuche Registrierung"), log.Text);
        Check("startet nicht: Nachinstallation genau einmal versucht", nachinstalliert == 1, nachinstalliert.ToString());
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Nachinstallation hoechstens einmal je Lauf", nachinstalliert == 1 && !stand.Ready);

        // Reinstalling repairs winget.
        log.Clear();
        ordner = Attrappen.WingetScenario(root, Scenario(("version.code", "1")));
        var ordnerFuerReparatur = ordner;
        w = NewService(() =>
        {
            Attrappen.WriteScenario(ordnerFuerReparatur, Scenario());
            return System.Threading.Tasks.Task.FromResult(true);
        });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("nach der Nachinstallation startet winget", stand.Ready, $"{stand.Problem} {log.Text}");

        // Package source missing - resetting does not help, reinstalling does.
        log.Clear();
        ordner = Attrappen.WingetScenario(root, Scenario(("source list", "Name Argument\n---- --------\nmsstore https://storeedgefd.dsx.mp.microsoft.com")));
        var ordnerQuelle = ordner;
        w = NewService(() =>
        {
            Attrappen.WriteScenario(ordnerQuelle, Scenario());
            return System.Threading.Tasks.Task.FromResult(true);
        });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        var aufrufe = Attrappen.Calls(ordner);
        Check("fehlende Quelle: zuruecksetzen und neu laden", aufrufe.Any(a => a.StartsWith("source reset")) && aufrufe.Any(a => a.StartsWith("source update")), string.Join(" | ", aufrufe));
        Check("fehlende Quelle: nach der Nachinstallation bereit", stand.Ready, $"{stand.Problem} {log.Text}");

        log.Clear();
        Attrappen.WingetScenario(root, Scenario(("source list", "nur msstore")));
        stand = NewService().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("fehlende Quelle bleibt: Hinweis auf 'winget source reset'", !stand.Ready && stand.Problem!.Contains("nicht eingerichtet") && stand.Hint!.Contains("source reset"), stand.Problem);

        log.Clear();
        Attrappen.WingetScenario(root, Scenario(("source list.code", "5")));
        stand = NewService().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Quelle unlesbar: eigener Hinweis", !stand.Ready && stand.Problem!.Contains("Paketquellen nicht lesen"), stand.Problem);

        log.Clear();
        c.RequestCancel();
        stand = NewService().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Abbruch: Pruefung meldet 'Abgebrochen'", !stand.Ready && stand.Problem == "Abgebrochen.", stand.Problem);
        c.Reset();

        // Cancel while winget reads its package sources.
        log.Clear();
        Attrappen.WingetScenario(root, Scenario(("source list.warte", "8000")));
        var laeuft = NewService().CheckReadinessAsync();
        System.Threading.Thread.Sleep(2000);
        c.RequestCancel();
        stand = laeuft.GetAwaiter().GetResult();
        c.Reset();
        Check("Abbruch waehrend der Paketquellen-Pruefung: 'Abgebrochen'", !stand.Ready && stand.Problem == "Abgebrochen." && !log.Has("setze sie zurück"), $"{stand.Problem} {log.Text}");

        // Without the fake: look for this computer's winget (search path, alias, App Installer folder).
        log.Clear();
        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
        var echt = new WingetService(runner, c, log.Write) { Nachinstallation = () => System.Threading.Tasks.Task.FromResult(false) };
        var echtStand = echt.CheckReadinessAsync().GetAwaiter().GetResult();
        Console.WriteLine($"         winget dieses Rechners: {(echtStand.Ready ? "bereit, " + echtStand.Version : echtStand.Problem)}");
        Check("winget dieses Rechners: gefunden und bereit - oder nicht bereit mit Grund",
            (echtStand.Ready && log.Has("Verwende winget")) || (!echtStand.Ready && !string.IsNullOrEmpty(echtStand.Hint)), log.Text);

        // ---- Export ----
        Console.WriteLine("\n== winget-Export: Filter, Bekannt-Liste, alte Liste bleibt bei Fehlern ==");
        var sicherung = Path.Combine(root, "winget-sicherung");
        var roh = Path.Combine(root, "winget-roh.json");
        File.WriteAllText(roh, """
            {
              "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
              "CreationDate": "2026-11-20T10:00:00.000-00:00",
              "Sources": [ {
                "Packages": [
                  { "PackageIdentifier": "Notepad++.Notepad++", "Version": "8.7" },
                  { "PackageIdentifier": "7zip.7zip" },
                  { "PackageIdentifier": "Microsoft.VCRedist.2015+.x64" },
                  { "PackageIdentifier": "Microsoft.Edge" },
                  { "PackageIdentifier": "ARP\\Machine\\X64\\{1234}" },
                  { "PackageIdentifier": "" }
                ],
                "SourceDetails": { "Argument": "https://cdn.winget.microsoft.com/cache", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", "Type": "Microsoft.PreIndexed.Package" }
              } ],
              "WinGetVersion": "1.12.460"
            }
            """);
        ordner = Attrappen.WingetScenario(root, Scenario(
            ("export.datei", roh),
            ("export.ausgabe", "Installiertes Paket ist nicht verfügbar: Mozilla Firefox (x64 de)\nInstalled package is not available: Firmensoftware XY\nInstalled package is not available: Microsoft Visual C++ 2015 Redistributable\nInstalled package is not available: Mozilla Thunderbird (de)"),
            ("show.Mozilla.Firefox.de.code", "0")));
        log.Clear();
        c.FastMode = false;
        w = NewService();
        w.ExportAsync(sicherung).GetAwaiter().GetResult();
        c.FastMode = true;
        var exportDatei = Path.Combine(sicherung, "Winget", "Export.json");
        var pakete = WingetService.ReadExportedPackages(sicherung, out var status);
        var ids = pakete.Select(p => p.PackageIdentifier).ToList();
        Check("Export: Erfolg", log.Has("[ERFOLG] 3 Programme exportiert"), log.Text);
        Check("Export: echte Programme bleiben, ohne Versionsnummer", status == "OK" && ids.Contains("Notepad++.Notepad++") && ids.Contains("7zip.7zip")
            && pakete.All(p => p.Version is null), string.Join(", ", ids));
        Check("Export: Laufzeit-/Zusatzpakete und ARP-Eintraege fallen heraus",
            !ids.Any(i => i.StartsWith("Microsoft.") || i.StartsWith("ARP")), string.Join(", ", ids));
        Check("Export: Firefox ueber die Bekannt-Liste gerettet", ids.Contains("Mozilla.Firefox.de") && log.Has("Mozilla Firefox (x64 de) -> Mozilla.Firefox.de"), log.Text);
        Check("Export: Begleitkomponenten werden ignoriert (ausfuehrliches Protokoll)", log.Has("Ignoriert (System-/Begleitkomponente): Microsoft Visual C++"), log.Text);
        Check("Export: Uebersprungenes steht im ausfuehrlichen Protokoll", log.Has("Übersprungen (Zusatz-/Laufzeitpaket): Microsoft.Edge"), log.Text);
        var nichtZugeordnet = Path.Combine(sicherung, "Winget", "Nicht-zugeordnete-Programme.txt");
        var liste = File.Exists(nichtZugeordnet) ? File.ReadAllLines(nichtZugeordnet) : Array.Empty<string>();
        Check("Export: Unbekanntes landet in Nicht-zugeordnete-Programme.txt", liste.Contains("Firmensoftware XY") && liste.Contains("Mozilla Thunderbird (de)"), string.Join(", ", liste));
        Check("Export: lesbares JSON (Notepad++ ohne \\u002B)", File.ReadAllText(exportDatei).Contains("Notepad++"));
        Check("Export: keine Zwischendatei liegen geblieben", !File.Exists(exportDatei + ".neu"));

        var vorher = File.ReadAllText(exportDatei);
        void ExportFails(string name, Dictionary<string, string> sz, string erwartet)
        {
            log.Clear();
            Attrappen.WingetScenario(root, sz);
            NewService().ExportAsync(sicherung).GetAwaiter().GetResult();
            Check($"Export scheitert ({name}): Fehler gemeldet, alte Liste unangetastet",
                log.Has(erwartet) && File.ReadAllText(exportDatei) == vorher && !File.Exists(exportDatei + ".neu"), log.Text);
        }
        ExportFails("keine Datei", Scenario(("export.code", "-1978335212"), ("export.ausgabe", "Fehler")), "keine Datei erzeugt");
        var leer = Path.Combine(root, "winget-leer.json");
        File.WriteAllText(leer, """{ "Sources": [] }""");
        ExportFails("keine Pakete", Scenario(("export.datei", leer)), "enthält keine Pakete");
        var nurZusatz = Path.Combine(root, "winget-zusatz.json");
        File.WriteAllText(nurZusatz, """{ "Sources": [ { "Packages": [ { "PackageIdentifier": "Microsoft.VCLibs.Desktop.14" } ] } ] }""");
        ExportFails("nur Zusatzpakete", Scenario(("export.datei", nurZusatz)), "keine Programme übrig");
        var kaputt = Path.Combine(root, "winget-kaputt.json");
        File.WriteAllText(kaputt, "{ kein json");
        ExportFails("unlesbar", Scenario(("export.datei", kaputt)), "konnte nicht gelesen werden");
        var nullJson = Path.Combine(root, "winget-null.json");
        File.WriteAllText(nullJson, "null");
        ExportFails("leer (null)", Scenario(("export.datei", nullJson)), "Export.json ist leer");

        // Cancel after winget has been found - as in the middle of the queue.
        Attrappen.WingetScenario(root, Scenario(("export.datei", roh)));
        var bereit = NewService();
        bereit.CheckReadinessAsync().GetAwaiter().GetResult();
        log.Clear();
        c.RequestCancel();
        bereit.ExportAsync(sicherung).GetAwaiter().GetResult();
        c.Reset();
        Check("Export: Abbruch gemeldet, alte Liste bleibt stehen", log.Has("[ABBRUCH] Winget-Export") && File.ReadAllText(exportDatei) == vorher, log.Text);

        // ---- Import ----
        Console.WriteLine("\n== winget-Import: jeder Ausgang ==");
        log.Clear();
        Attrappen.WingetScenario(root, Scenario());
        NewService().ImportAsync(new List<WingetPackage>()).GetAwaiter().GetResult();
        Check("Import ohne Auswahl: nur ein Hinweis", log.Has("keine Programme ausgewählt"), log.Text);

        log.Clear();
        ordner = Attrappen.WingetScenario(root, Scenario(
            ("install.Neu.Paket.code", "0"),
            ("install.Schon.Aktuell.code", WingetFehler.SchonAktuell.ToString()),
            ("install.Schon.Da.code", WingetFehler.SchonInstalliert.ToString()),
            ("install.Gibt.Esnicht.code", WingetFehler.NichtGefunden.ToString()),
            ("install.Geht.Schief.code", "1603"),
            ("install.Geht.Schief.ausgabe", "Installer failed with exit code: 1603")));
        var auswahl = new[] { "Neu.Paket", "Schon.Aktuell", "Schon.Da", "Gibt.Esnicht", "Geht.Schief", "" }
            .Select(i => new WingetPackage { PackageIdentifier = i }).ToList();
        NewService().ImportAsync(auswahl).GetAwaiter().GetResult();
        Check("Import: 1 installiert, 2 uebersprungen, 2 fehlgeschlagen",
            log.Has("1 installiert, 2 übersprungen (bereits vorhanden), 2 fehlgeschlagen") && log.Has("[WARNUNG] Winget-Import abgeschlossen"), log.Text);
        Check("Import: fremder Fehler mit Code und letzter Meldung", log.Has("ExitCode 1603") && log.Has("Installer failed with exit code: 1603"), log.Text);
        Check("Import: leere Kennung wird ausgelassen", Attrappen.Calls(ordner).Count(a => a.StartsWith("install")) == 5);

        log.Clear();
        Attrappen.WingetScenario(root, Scenario(("install.*.code", "0")));
        NewService().ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        Check("Import ohne Fehler: Bilanz mit [ERFOLG]", log.Has("[ERFOLG] Winget-Import abgeschlossen: 1 installiert"), log.Text);

        // Firefox ESR comes from the old PC - the current release is installed here, once.
        Check("Zielpaket: Firefox ESR -> aktuelle Fassung (mit und ohne Sprache)",
            WingetService.TargetPackage("Mozilla.Firefox.ESR.de") == "Mozilla.Firefox.de" && WingetService.TargetPackage("Mozilla.Firefox.ESR") == "Mozilla.Firefox"
            && WingetService.TargetPackage("mozilla.firefox.esr.en-US") == "Mozilla.Firefox.en-US");
        Check("Zielpaket: alles andere bleibt", WingetService.TargetPackage("Mozilla.Firefox.de") == "Mozilla.Firefox.de"
            && WingetService.TargetPackage("Mozilla.Thunderbird.de") == "Mozilla.Thunderbird.de" && WingetService.TargetPackage("Mozilla.Firefox.ESR.Beta") == "Mozilla.Firefox.ESR.Beta");
        log.Clear();
        ordner = Attrappen.WingetScenario(root, Scenario(("install.*.code", "0")));
        NewService().ImportAsync(new[] { "Mozilla.Firefox.ESR.de", "Mozilla.Firefox.de", "Anderes.Paket" }
            .Select(i => new WingetPackage { PackageIdentifier = i, Version = "115.18.0" }).ToList()).GetAwaiter().GetResult();
        var installiert = Attrappen.Calls(ordner).Where(a => a.StartsWith("install")).ToList();
        Check("Import: Firefox ESR wird als aktuelle Fassung installiert, nur einmal",
            installiert.Count == 2 && installiert.Any(a => a.Contains("Mozilla.Firefox.de")) && !installiert.Any(a => a.Contains("ESR"))
            && log.Has("[INFO] Mozilla.Firefox.ESR.de -> Mozilla.Firefox.de: Firefox kommt auf diesem PC in der aktuellen Fassung statt ESR"), string.Join(" | ", installiert) + " / " + log.Text);

        // Package source broken (0x8A150009): repair, reinstall, then give up.
        log.Clear();
        var kaputtCode = unchecked((int)0x8A150009).ToString();
        ordner = Attrappen.WingetScenario(root, Scenario(("install.*.code", kaputtCode)));
        nachinstalliert = 0;
        w = NewService(() => { nachinstalliert++; return System.Threading.Tasks.Task.FromResult(true); });
        w.ImportAsync(new[] { "Eins.Paket", "Zwei.Paket", "Drei.Paket" }.Select(i => new WingetPackage { PackageIdentifier = i }).ToList()).GetAwaiter().GetResult();
        aufrufe = Attrappen.Calls(ordner);
        Check("kaputte Quelle: zuruecksetzen und erneut versuchen", aufrufe.Count(a => a.StartsWith("source reset")) >= 1 && aufrufe.Count(a => a.StartsWith("install")) >= 3, string.Join(" | ", aufrufe));
        Check("kaputte Quelle: App-Installer einmal nachinstalliert", nachinstalliert == 1, nachinstalliert.ToString());
        Check("kaputte Quelle: die uebrigen Pakete werden nicht mehr versucht",
            log.Has("die übrigen 2 Programme werden nicht versucht") && !aufrufe.Any(a => a.Contains("Zwei.Paket")), log.Text);
        Check("kaputte Quelle: Abhilfe im Protokoll", log.Has("winget source reset --force"), log.Text);

        ordner = Attrappen.WingetScenario(root, Scenario(("install.*.code", "0")));
        bereit = NewService();
        bereit.CheckReadinessAsync().GetAwaiter().GetResult();
        log.Clear();
        c.RequestCancel();
        bereit.ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        c.Reset();
        Check("Import: Abbruch vor dem ersten Paket, nichts installiert",
            log.Has("[ABBRUCH] Winget-Import") && !Attrappen.Calls(ordner).Any(a => a.StartsWith("install")), log.Text);

        // Without a startable winget: the import fails before the first package.
        log.Clear();
        Attrappen.WingetScenario(root, Scenario(("version.code", "1")));
        NewService().ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        Check("Import ohne winget: Fehler, keine Bilanz", log.Has("[FEHLER] winget konnte mit keiner Methode gestartet werden") && !log.Has("Winget-Import abgeschlossen"), log.Text);
        log.Clear();
        NewService().ExportAsync(Path.Combine(root, "winget-ohne")).GetAwaiter().GetResult();
        Check("Export ohne winget: keine Liste", !File.Exists(Path.Combine(root, "winget-ohne", "Winget", "Export.json")));

        Check("Beschreibung: Code, Hex und letzte Meldung",
            WingetService.DescribeResult(new ProcessResult(1603, "zeile eins\nInstaller failed", null)).Contains("ExitCode 1603 (0x00000643)"));

        // ReadExportedPackages: every state.
        var lesen = Path.Combine(root, "winget-lesen");
        Directory.CreateDirectory(lesen);
        WingetService.ReadExportedPackages(lesen, out status);
        Check("Liste lesen: keine Datei -> NotFound", status == "NotFound");
        File.WriteAllText(Path.Combine(lesen, "Export.json"), "{ kaputt");
        WingetService.ReadExportedPackages(lesen, out status);
        Check("Liste lesen: alte Ablage direkt im Ordner, unlesbar -> ParseError", status == "ParseError");
        File.WriteAllText(Path.Combine(lesen, "Export.json"), """{ "Sources": [ { "Packages": [ { "PackageIdentifier": " " } ] } ] }""");
        WingetService.ReadExportedPackages(lesen, out status);
        Check("Liste lesen: nur leere Kennungen -> Empty", status == "Empty");

        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
        _ = JsonSerializer.Serialize(new WingetExport());   // A model without content can be serialized
    }
}
