using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// WingetService gegen die winget-Attrappe (tests/WingetAttrappe): Bereitschaft,
/// Reparatur, Export mit Filter und Bekannt-Liste, Import mit allen Ausgaengen.
/// </summary>
internal static partial class Program
{
    private static Dictionary<string, string> Szenario(params (string K, string V)[] werte)
        => werte.ToDictionary(w => w.K, w => w.V);

    private static void WingetDienstPruefen(string root)
    {
        // Nur unter Windows: winget meldet 32-Bit-Rueckgabewerte (0x8A15...),
        // andere Systeme kuerzen Exit-Codes auf 8 Bit.
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== winget (Attrappe statt echtem winget) ==");
        var attrappe = Attrappen.WingetAttrappe();
        if (attrappe is null)
        {
            Check("winget-Attrappe ist gebaut (dotnet build tests/WingetAttrappe)", false);
            return;
        }

        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        WingetService Neu(Func<System.Threading.Tasks.Task<bool>>? nach = null)
            => new(runner, c, log.Schreiben) { FesteKandidaten = new[] { attrappe }, Nachinstallation = nach ?? (() => System.Threading.Tasks.Task.FromResult(false)) };

        // ---- Bereitschaft ----
        var ordner = Attrappen.WingetSzenario(root, Szenario());
        var w = Neu();
        var stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("bereit: Version erkannt", stand.Ready && stand.Version == "v1.12.460", $"{stand.Problem} {log.Text}");
        Check("bereit: Startweg im Protokoll", log.Hat("Verwende winget v1.12.460") && log.Hat("Startmethode: Direct"), log.Text);
        Check("bereit: Paketquelle gefragt", Attrappen.Aufrufe(ordner).Any(a => a.StartsWith("source list")));

        // winget startet nicht - Registrierung (echt) und Nachinstallation (ersetzt) helfen nicht.
        log.Leeren();
        ordner = Attrappen.WingetSzenario(root, Szenario(("version.code", "1"), ("version", "kaputt")));
        int nachinstalliert = 0;
        w = Neu(() => { nachinstalliert++; return System.Threading.Tasks.Task.FromResult(false); });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("startet nicht: nicht bereit, mit Hinweis", !stand.Ready && stand.Problem!.Contains("nicht starten") && !string.IsNullOrEmpty(stand.Hint), stand.Problem);
        Check("startet nicht: beide Startwege versucht", log.Hat("[Direct]") && log.Hat("[Cmd]"), log.Text);
        Check("startet nicht: Registrierung versucht", log.Hat("Versuche Registrierung"), log.Text);
        Check("startet nicht: Nachinstallation genau einmal versucht", nachinstalliert == 1, nachinstalliert.ToString());
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Nachinstallation hoechstens einmal je Lauf", nachinstalliert == 1 && !stand.Ready);

        // Nachinstallation repariert winget.
        log.Leeren();
        ordner = Attrappen.WingetSzenario(root, Szenario(("version.code", "1")));
        var ordnerFuerReparatur = ordner;
        w = Neu(() =>
        {
            Attrappen.SzenarioSchreiben(ordnerFuerReparatur, Szenario());
            return System.Threading.Tasks.Task.FromResult(true);
        });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        Check("nach der Nachinstallation startet winget", stand.Ready, $"{stand.Problem} {log.Text}");

        // Paketquelle fehlt - Zuruecksetzen hilft nicht, Nachinstallation schon.
        log.Leeren();
        ordner = Attrappen.WingetSzenario(root, Szenario(("source list", "Name Argument\n---- --------\nmsstore https://storeedgefd.dsx.mp.microsoft.com")));
        var ordnerQuelle = ordner;
        w = Neu(() =>
        {
            Attrappen.SzenarioSchreiben(ordnerQuelle, Szenario());
            return System.Threading.Tasks.Task.FromResult(true);
        });
        stand = w.CheckReadinessAsync().GetAwaiter().GetResult();
        var aufrufe = Attrappen.Aufrufe(ordner);
        Check("fehlende Quelle: zuruecksetzen und neu laden", aufrufe.Any(a => a.StartsWith("source reset")) && aufrufe.Any(a => a.StartsWith("source update")), string.Join(" | ", aufrufe));
        Check("fehlende Quelle: nach der Nachinstallation bereit", stand.Ready, $"{stand.Problem} {log.Text}");

        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario(("source list", "nur msstore")));
        stand = Neu().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("fehlende Quelle bleibt: Hinweis auf 'winget source reset'", !stand.Ready && stand.Problem!.Contains("nicht eingerichtet") && stand.Hint!.Contains("source reset"), stand.Problem);

        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario(("source list.code", "5")));
        stand = Neu().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Quelle unlesbar: eigener Hinweis", !stand.Ready && stand.Problem!.Contains("Paketquellen nicht lesen"), stand.Problem);

        log.Leeren();
        c.RequestCancel();
        stand = Neu().CheckReadinessAsync().GetAwaiter().GetResult();
        Check("Abbruch: Pruefung meldet 'Abgebrochen'", !stand.Ready && stand.Problem == "Abgebrochen.", stand.Problem);
        c.Reset();

        // Abbruch, waehrend winget seine Paketquellen liest.
        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario(("source list.warte", "8000")));
        var laeuft = Neu().CheckReadinessAsync();
        System.Threading.Thread.Sleep(2000);
        c.RequestCancel();
        stand = laeuft.GetAwaiter().GetResult();
        c.Reset();
        Check("Abbruch waehrend der Paketquellen-Pruefung: 'Abgebrochen'", !stand.Ready && stand.Problem == "Abgebrochen." && !log.Hat("setze sie zurück"), $"{stand.Problem} {log.Text}");

        // Ohne Attrappe: das winget dieses Rechners suchen (Suchpfad, Alias, App-Installer-Ordner).
        log.Leeren();
        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
        var echt = new WingetService(runner, c, log.Schreiben) { Nachinstallation = () => System.Threading.Tasks.Task.FromResult(false) };
        var echtStand = echt.CheckReadinessAsync().GetAwaiter().GetResult();
        Console.WriteLine($"         winget dieses Rechners: {(echtStand.Ready ? "bereit, " + echtStand.Version : echtStand.Problem)}");
        Check("winget dieses Rechners: gefunden und bereit - oder nicht bereit mit Grund",
            (echtStand.Ready && log.Hat("Verwende winget")) || (!echtStand.Ready && !string.IsNullOrEmpty(echtStand.Hint)), log.Text);

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
        ordner = Attrappen.WingetSzenario(root, Szenario(
            ("export.datei", roh),
            ("export.ausgabe", "Installiertes Paket ist nicht verfügbar: Mozilla Firefox (x64 de)\nInstalled package is not available: Firmensoftware XY\nInstalled package is not available: Microsoft Visual C++ 2015 Redistributable\nInstalled package is not available: Mozilla Thunderbird (de)"),
            ("show.Mozilla.Firefox.de.code", "0")));
        log.Leeren();
        c.FastMode = false;
        w = Neu();
        w.ExportAsync(sicherung).GetAwaiter().GetResult();
        c.FastMode = true;
        var exportDatei = Path.Combine(sicherung, "Winget", "Export.json");
        var pakete = WingetService.ReadExportedPackages(sicherung, out var status);
        var ids = pakete.Select(p => p.PackageIdentifier).ToList();
        Check("Export: Erfolg", log.Hat("[ERFOLG] 3 Programme exportiert"), log.Text);
        Check("Export: echte Programme bleiben, ohne Versionsnummer", status == "OK" && ids.Contains("Notepad++.Notepad++") && ids.Contains("7zip.7zip")
            && pakete.All(p => p.Version is null), string.Join(", ", ids));
        Check("Export: Laufzeit-/Zusatzpakete und ARP-Eintraege fallen heraus",
            !ids.Any(i => i.StartsWith("Microsoft.") || i.StartsWith("ARP")), string.Join(", ", ids));
        Check("Export: Firefox ueber die Bekannt-Liste gerettet", ids.Contains("Mozilla.Firefox.de") && log.Hat("Mozilla Firefox (x64 de) -> Mozilla.Firefox.de"), log.Text);
        Check("Export: Begleitkomponenten werden ignoriert (ausfuehrliches Protokoll)", log.Hat("Ignoriert (System-/Begleitkomponente): Microsoft Visual C++"), log.Text);
        Check("Export: Uebersprungenes steht im ausfuehrlichen Protokoll", log.Hat("Übersprungen (Zusatz-/Laufzeitpaket): Microsoft.Edge"), log.Text);
        var nichtZugeordnet = Path.Combine(sicherung, "Winget", "Nicht-zugeordnete-Programme.txt");
        var liste = File.Exists(nichtZugeordnet) ? File.ReadAllLines(nichtZugeordnet) : Array.Empty<string>();
        Check("Export: Unbekanntes landet in Nicht-zugeordnete-Programme.txt", liste.Contains("Firmensoftware XY") && liste.Contains("Mozilla Thunderbird (de)"), string.Join(", ", liste));
        Check("Export: lesbares JSON (Notepad++ ohne \\u002B)", File.ReadAllText(exportDatei).Contains("Notepad++"));
        Check("Export: keine Zwischendatei liegen geblieben", !File.Exists(exportDatei + ".neu"));

        var vorher = File.ReadAllText(exportDatei);
        void ExportScheitert(string name, Dictionary<string, string> sz, string erwartet)
        {
            log.Leeren();
            Attrappen.WingetSzenario(root, sz);
            Neu().ExportAsync(sicherung).GetAwaiter().GetResult();
            Check($"Export scheitert ({name}): Fehler gemeldet, alte Liste unangetastet",
                log.Hat(erwartet) && File.ReadAllText(exportDatei) == vorher && !File.Exists(exportDatei + ".neu"), log.Text);
        }
        ExportScheitert("keine Datei", Szenario(("export.code", "-1978335212"), ("export.ausgabe", "Fehler")), "keine Datei erzeugt");
        var leer = Path.Combine(root, "winget-leer.json");
        File.WriteAllText(leer, """{ "Sources": [] }""");
        ExportScheitert("keine Pakete", Szenario(("export.datei", leer)), "enthält keine Pakete");
        var nurZusatz = Path.Combine(root, "winget-zusatz.json");
        File.WriteAllText(nurZusatz, """{ "Sources": [ { "Packages": [ { "PackageIdentifier": "Microsoft.VCLibs.Desktop.14" } ] } ] }""");
        ExportScheitert("nur Zusatzpakete", Szenario(("export.datei", nurZusatz)), "keine Programme übrig");
        var kaputt = Path.Combine(root, "winget-kaputt.json");
        File.WriteAllText(kaputt, "{ kein json");
        ExportScheitert("unlesbar", Szenario(("export.datei", kaputt)), "konnte nicht gelesen werden");
        var nullJson = Path.Combine(root, "winget-null.json");
        File.WriteAllText(nullJson, "null");
        ExportScheitert("leer (null)", Szenario(("export.datei", nullJson)), "Export.json ist leer");

        // Abbruch, nachdem winget schon gefunden ist - wie mitten in der Warteschlange.
        Attrappen.WingetSzenario(root, Szenario(("export.datei", roh)));
        var bereit = Neu();
        bereit.CheckReadinessAsync().GetAwaiter().GetResult();
        log.Leeren();
        c.RequestCancel();
        bereit.ExportAsync(sicherung).GetAwaiter().GetResult();
        c.Reset();
        Check("Export: Abbruch gemeldet, alte Liste bleibt stehen", log.Hat("[ABBRUCH] Winget-Export") && File.ReadAllText(exportDatei) == vorher, log.Text);

        // ---- Import ----
        Console.WriteLine("\n== winget-Import: jeder Ausgang ==");
        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario());
        Neu().ImportAsync(new List<WingetPackage>()).GetAwaiter().GetResult();
        Check("Import ohne Auswahl: nur ein Hinweis", log.Hat("keine Programme ausgewählt"), log.Text);

        log.Leeren();
        ordner = Attrappen.WingetSzenario(root, Szenario(
            ("install.Neu.Paket.code", "0"),
            ("install.Schon.Aktuell.code", WingetFehler.SchonAktuell.ToString()),
            ("install.Schon.Da.code", WingetFehler.SchonInstalliert.ToString()),
            ("install.Gibt.Esnicht.code", WingetFehler.NichtGefunden.ToString()),
            ("install.Geht.Schief.code", "1603"),
            ("install.Geht.Schief.ausgabe", "Installer failed with exit code: 1603")));
        var auswahl = new[] { "Neu.Paket", "Schon.Aktuell", "Schon.Da", "Gibt.Esnicht", "Geht.Schief", "" }
            .Select(i => new WingetPackage { PackageIdentifier = i }).ToList();
        Neu().ImportAsync(auswahl).GetAwaiter().GetResult();
        Check("Import: 1 installiert, 2 uebersprungen, 2 fehlgeschlagen",
            log.Hat("1 installiert, 2 übersprungen (bereits vorhanden), 2 fehlgeschlagen") && log.Hat("[WARNUNG] Winget-Import abgeschlossen"), log.Text);
        Check("Import: fremder Fehler mit Code und letzter Meldung", log.Hat("ExitCode 1603") && log.Hat("Installer failed with exit code: 1603"), log.Text);
        Check("Import: leere Kennung wird ausgelassen", Attrappen.Aufrufe(ordner).Count(a => a.StartsWith("install")) == 5);

        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario(("install.*.code", "0")));
        Neu().ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        Check("Import ohne Fehler: Bilanz mit [ERFOLG]", log.Hat("[ERFOLG] Winget-Import abgeschlossen: 1 installiert"), log.Text);

        // Paketquelle kaputt (0x8A150009): reparieren, nachinstallieren, dann aufgeben.
        log.Leeren();
        var kaputtCode = unchecked((int)0x8A150009).ToString();
        ordner = Attrappen.WingetSzenario(root, Szenario(("install.*.code", kaputtCode)));
        nachinstalliert = 0;
        w = Neu(() => { nachinstalliert++; return System.Threading.Tasks.Task.FromResult(true); });
        w.ImportAsync(new[] { "Eins.Paket", "Zwei.Paket", "Drei.Paket" }.Select(i => new WingetPackage { PackageIdentifier = i }).ToList()).GetAwaiter().GetResult();
        aufrufe = Attrappen.Aufrufe(ordner);
        Check("kaputte Quelle: zuruecksetzen und erneut versuchen", aufrufe.Count(a => a.StartsWith("source reset")) >= 1 && aufrufe.Count(a => a.StartsWith("install")) >= 3, string.Join(" | ", aufrufe));
        Check("kaputte Quelle: App-Installer einmal nachinstalliert", nachinstalliert == 1, nachinstalliert.ToString());
        Check("kaputte Quelle: die uebrigen Pakete werden nicht mehr versucht",
            log.Hat("die übrigen 2 Programme werden nicht versucht") && !aufrufe.Any(a => a.Contains("Zwei.Paket")), log.Text);
        Check("kaputte Quelle: Abhilfe im Protokoll", log.Hat("winget source reset --force"), log.Text);

        ordner = Attrappen.WingetSzenario(root, Szenario(("install.*.code", "0")));
        bereit = Neu();
        bereit.CheckReadinessAsync().GetAwaiter().GetResult();
        log.Leeren();
        c.RequestCancel();
        bereit.ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        c.Reset();
        Check("Import: Abbruch vor dem ersten Paket, nichts installiert",
            log.Hat("[ABBRUCH] Winget-Import") && !Attrappen.Aufrufe(ordner).Any(a => a.StartsWith("install")), log.Text);

        // Ohne startbares winget: Import scheitert vor dem ersten Paket.
        log.Leeren();
        Attrappen.WingetSzenario(root, Szenario(("version.code", "1")));
        Neu().ImportAsync(new[] { new WingetPackage { PackageIdentifier = "A.B" } }).GetAwaiter().GetResult();
        Check("Import ohne winget: Fehler, keine Bilanz", log.Hat("[FEHLER] winget konnte mit keiner Methode gestartet werden") && !log.Hat("Winget-Import abgeschlossen"), log.Text);
        log.Leeren();
        Neu().ExportAsync(Path.Combine(root, "winget-ohne")).GetAwaiter().GetResult();
        Check("Export ohne winget: keine Liste", !File.Exists(Path.Combine(root, "winget-ohne", "Winget", "Export.json")));

        Check("Beschreibung: Code, Hex und letzte Meldung",
            WingetService.Beschreibung(new ProcessResult(1603, "zeile eins\nInstaller failed", null)).Contains("ExitCode 1603 (0x00000643)"));

        // ReadExportedPackages: alle Zustaende.
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
        _ = JsonSerializer.Serialize(new WingetExport());   // Modell ohne Inhalt ist serialisierbar
    }
}
