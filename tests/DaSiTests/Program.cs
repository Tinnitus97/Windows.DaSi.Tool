using System;
using System.IO;
using System.Linq;
using WindowsDaSiTool.Services;

namespace DaSiTests;

internal static class Program
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  [OK]   {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : $"  -> {detail}")}"); }
    }

    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "dasi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Console.WriteLine("== update.json auswerten ==");
        var json = """
            {
              "schema": 1,
              "program": {
                "version": "1.2.0",
                "released": "2026-08-17",
                "url": "https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/download/v1.2.0/WindowsDaSiTool.exe",
                "sha256": "AA11BB",
                "notes": "https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.2.0"
              },
              "spaeter_mal_was_neues": { "version": "", "url": "" }
            }
            """;

        var m = UpdateService.Parse(json);
        Check("Datei wird gelesen", m.Success, m.Error);
        Check("Version wird erkannt", m.Program.Version == "1.2.0", m.Program.Version);
        Check("Adresse zeigt auf die EXE",
            m.Program.Url.EndsWith("/WindowsDaSiTool.exe", StringComparison.Ordinal), m.Program.Url);
        Check("Pruefsumme wird kleingeschrieben", m.Program.Sha256 == "aa11bb", m.Program.Sha256);
        Check("Eintrag ist brauchbar", m.Program.IsUsable);
        Check("ein unbekannter Abschnitt stoert nicht", m.Success);

        Check("unbrauchbare Datei meldet Misserfolg", !UpdateService.Parse("{kein json").Success);
        Check("fehlende Angaben ergeben einen leeren Eintrag",
            UpdateService.Parse("{}") is { Success: true } leer && !leer.Program.IsUsable);

        Console.WriteLine("\n== Versionsvergleich ==");
        Check("neuere Version wird erkannt", UpdateService.IsProgramNewer("1.0.5", "1.1.0"));
        Check("gleiche Version ist kein Update", !UpdateService.IsProgramNewer("1.1.0", "1.1.0"));
        Check("aeltere Version ist kein Update", !UpdateService.IsProgramNewer("1.2.0", "1.1.0"));
        Check("vierstellige Nummer wird verglichen", UpdateService.IsProgramNewer("1.0.5.0", "1.0.5.1"));
        Check("unlesbare Angabe ist kein Update", !UpdateService.IsProgramNewer("1.0.5", "neu"));

        Console.WriteLine("\n== Adressen ==");
        Check("Standardadresse zeigt auf das richtige Repository",
            UpdateService.DefaultManifestUrl.Contains("Tinnitus97/Windows.DaSi.Tool", StringComparison.Ordinal),
            UpdateService.DefaultManifestUrl);
        Check("Standardadresse folgt dem Standardzweig (HEAD)",
            UpdateService.DefaultManifestUrl.Contains("/HEAD/", StringComparison.Ordinal),
            UpdateService.DefaultManifestUrl);
        Check("Veroeffentlichungsseite stimmt",
            UpdateService.ReleasesPageUrl.EndsWith("/releases", StringComparison.Ordinal));
        Check("ohne Hinterlegung gilt die Vorgabe",
            UpdateService.ResolveManifestUrl(root) == UpdateService.DefaultManifestUrl);

        File.WriteAllText(Path.Combine(root, "Update-Url.txt"),
            "# eigener Spiegel\nhttps://intern.example.test/dasi/update.json\n");
        Check("hinterlegte Adresse wird benutzt",
            UpdateService.ResolveManifestUrl(root) == "https://intern.example.test/dasi/update.json",
            UpdateService.ResolveManifestUrl(root));

        Console.WriteLine("\n== Arbeitsordner und Pruefsumme ==");
        Check("eigener Arbeitsordner unter TEMP",
            UpdateService.WorkFolder == Path.Combine(Path.GetTempPath(), "WindowsDaSiTool-Update"),
            UpdateService.WorkFolder);

        var datei = Path.Combine(root, "summe.txt");
        File.WriteAllText(datei, "abc");
        Check("SHA256 wird richtig gebildet",
            UpdateService.ComputeSha256(datei)
                == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            UpdateService.ComputeSha256(datei));

        Console.WriteLine("\n== Austauschskript ==");
        var neu = Path.Combine(root, "neu", "WindowsDaSiTool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(neu)!);
        File.WriteAllText(neu, "x");
        var ziel = Path.Combine(root, "WindowsDaSiTool.exe");

        var skript = UpdateService.WriteUpdateScript(neu, ziel, 4711);
        var inhalt = File.ReadAllText(skript);
        Check("Skript wurde geschrieben", File.Exists(skript));
        Check("Skript liegt im eigenen Arbeitsordner",
            skript.StartsWith(UpdateService.WorkFolder, StringComparison.OrdinalIgnoreCase), skript);
        Check("Skript wartet auf den Vorgang", inhalt.Contains("4711", StringComparison.Ordinal));
        Check("Skript ersetzt die Datei", inhalt.Contains("move /y", StringComparison.Ordinal));
        Check("Skript startet danach neu", inhalt.Contains("start \"\"", StringComparison.Ordinal));
        Check("Skript meldet einen Fehlschlag",
            inhalt.Contains("errorlevel", StringComparison.Ordinal)
            && inhalt.Contains("pause", StringComparison.Ordinal));

        UpdateService.CleanUp();
        Check("Arbeitsordner laesst sich aufraeumen", !Directory.Exists(UpdateService.WorkFolder));

        // ================================================================
        // Cloud-Anbieter erkennen
        // ================================================================
        Console.WriteLine("\n== Cloud-Anbieter erkennen ==");

        var profil = Path.Combine(root, "Profil");
        foreach (var d in new[]
                 {
                     "OneDrive",                 // genauer Name
                     "OneDrive - Muster GmbH",   // Firmenkonto, Praefixmuster
                     "Dropbox",
                     "HiDrive",                  // STRATO
                     "Nextcloud",
                     "Dokumente",                // keine Cloud
                     "Desktop",                  // keine Cloud
                     "AppData"                   // keine Cloud
                 })
        {
            Directory.CreateDirectory(Path.Combine(profil, d));
        }

        var clouds = CloudProviders.Detect(profil);
        var namen = clouds.Select(c => Path.GetFileName(c.Path)).OrderBy(n => n).ToArray();

        Check("alle fuenf Cloud-Ordner gefunden", clouds.Count == 5, string.Join(", ", namen));
        Check("OneDrive mit Firmennamen wird erkannt",
            namen.Contains("OneDrive - Muster GmbH"));
        Check("OneDrive ohne Zusatz wird erkannt", namen.Contains("OneDrive"));
        Check("STRATO HiDrive wird als Anbieter benannt",
            clouds.Any(c => c.Provider == "STRATO HiDrive"));
        Check("gewoehnliche Ordner bleiben unangetastet",
            !namen.Contains("Dokumente") && !namen.Contains("Desktop") && !namen.Contains("AppData"));
        Check("jeder Ordner wird nur einmal gemeldet",
            namen.Length == namen.Distinct().Count());

        var zusammen = CloudProviders.Summarize(clouds);
        Check("Zusammenfassung nennt jeden Anbieter einmal",
            zusammen.Split(", ").Length == zusammen.Split(", ").Distinct().Count(), zusammen);
        Check("Zusammenfassung nennt OneDrive", zusammen.Contains("OneDrive", StringComparison.Ordinal), zusammen);

        Check("ein nicht vorhandenes Profil liefert eine leere Liste",
            CloudProviders.Detect(Path.Combine(root, "gibt-es-nicht")).Count == 0);
        Check("ein leerer Pfad liefert eine leere Liste",
            CloudProviders.Detect("").Count == 0);

        // ================================================================
        // Was beim Profil-Backup ausgelassen wird
        // ================================================================
        Console.WriteLine("\n== Ausschlussliste fuer das Benutzerprofil ==");

        static bool Hat(System.Collections.Generic.List<string> liste, string profil, string rel)
            => liste.Contains(Path.Combine(profil, rel), StringComparer.OrdinalIgnoreCase);

        var vorgabe = ProfileExcludes.Build(profil);
        Check("Vorgabe: AppData bleibt draussen", Hat(vorgabe, profil, "AppData"));
        Check("Vorgabe: Cloud-Ordner bleiben draussen",
            Hat(vorgabe, profil, "Dropbox") && Hat(vorgabe, profil, "OneDrive - Muster GmbH"));
        Check("Vorgabe: Junctions bleiben draussen",
            Hat(vorgabe, profil, "Anwendungsdaten") && Hat(vorgabe, profil, "Eigene Dateien"));

        var mitAppData = ProfileExcludes.Build(profil, includeAppData: true);
        Check("AppData an: AppData ist nicht mehr ausgeschlossen",
            !Hat(mitAppData, profil, "AppData"));
        Check("AppData an: die Zwischenspeicher bleiben trotzdem draussen",
            Hat(mitAppData, profil, @"AppData\Local\Temp")
            && Hat(mitAppData, profil, @"AppData\Local\Microsoft\Windows\INetCache"));
        Check("AppData an: Cloud-Ordner bleiben unveraendert draussen",
            Hat(mitAppData, profil, "Dropbox"));

        var mitCloud = ProfileExcludes.Build(profil, includeCloud: true);
        Check("Cloud an: kein Cloud-Ordner mehr ausgeschlossen",
            !Hat(mitCloud, profil, "Dropbox")
            && !Hat(mitCloud, profil, "OneDrive")
            && !Hat(mitCloud, profil, "Nextcloud"));
        Check("Cloud an: AppData bleibt unveraendert draussen",
            Hat(mitCloud, profil, "AppData"));

        var beides = ProfileExcludes.Build(profil, includeAppData: true, includeCloud: true);
        Check("beides an: weder AppData noch Cloud ausgeschlossen",
            !Hat(beides, profil, "AppData") && !Hat(beides, profil, "Dropbox"));
        Check("beides an: Junctions und Zwischenspeicher bleiben draussen",
            Hat(beides, profil, "Anwendungsdaten") && Hat(beides, profil, @"AppData\Local\Temp"));

        Check("die Liste enthaelt keine Dubletten",
            beides.Count == beides.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // ================================================================
        // Liegt das Ziel im Quellprofil? (frueher ein reiner Praefixvergleich)
        // ================================================================
        Console.WriteLine("\n== Ziel im Quellverzeichnis ==");
        var nutzer = Path.Combine(root, "Users");
        var max = Path.Combine(nutzer, "Max");
        Check("Maximilian liegt NICHT in Max (der alte Fehler)",
            !ProfileExcludes.IsInside(Path.Combine(nutzer, "Maximilian", "Backup"), max));
        Check("ein Unterordner von Max liegt in Max",
            ProfileExcludes.IsInside(Path.Combine(max, "Backup", "Benutzerprofil"), max));
        Check("Max selbst gilt als darin",
            ProfileExcludes.IsInside(max, max));
        Check("abschliessendes Trennzeichen stoert nicht",
            ProfileExcludes.IsInside(Path.Combine(max, "x"), max + Path.DirectorySeparatorChar));
        Check("Gross-/Kleinschreibung zaehlt nicht",
            ProfileExcludes.IsInside(Path.Combine(max, "x").ToUpperInvariant(), max.ToLowerInvariant()));
        Check("ein Nachbarordner liegt nicht darin",
            !ProfileExcludes.IsInside(Path.Combine(nutzer, "Anna"), max));
        Check("leere Angaben liegen nirgends drin",
            !ProfileExcludes.IsInside("", max) && !ProfileExcludes.IsInside(max, ""));

        // ================================================================
        // Wann gilt eine Aktion als fehlgeschlagen?
        // ================================================================
        Console.WriteLine("\n== Fehlerzeilen im Protokoll ==");
        Check("[FEHLER] am Zeilenanfang zaehlt",
            ActionOutcome.IsErrorLine("[FEHLER] Robocopy Fehlercode: 16"));
        Check("[ERROR] im englischen Modus zaehlt",
            ActionOutcome.IsErrorLine("[ERROR] Robocopy error code: 16"));
        Check("eingerueckter Paketfehler zaehlt",
            ActionOutcome.IsErrorLine("    -> [FEHLER] Paket wurde im Winget-Katalog nicht gefunden."));
        Check("mit vorangestelltem Zeilenumbruch zaehlt",
            ActionOutcome.IsErrorLine("\r\n[FEHLER] Aktion abgebrochen"));
        Check("robocopy-eigene Zeile ohne Klammern zaehlt NICHT",
            !ActionOutcome.IsErrorLine("2026/11/05 10:00:00 FEHLER 32 (0x00000020) Datei wird kopiert"));
        Check("[WARNUNG] ist kein Fehlschlag",
            !ActionOutcome.IsErrorLine("[WARNUNG] Es konnte kein aktuelles Hintergrundbild gefunden werden"));
        Check("[ERFOLG] ist kein Fehlschlag",
            !ActionOutcome.IsErrorLine("[ERFOLG] Benutzerprofil gesichert."));
        Check("leere Zeile ist kein Fehlschlag",
            !ActionOutcome.IsErrorLine("") && !ActionOutcome.IsErrorLine(null));

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }
}
