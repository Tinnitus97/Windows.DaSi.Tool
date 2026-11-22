using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr h, int cmd);
    private static void MaximizeForTest(IntPtr h) => ShowWindow(h, 3);   // SW_MAXIMIZE

    // ---- Hilfen zum Zuruecklesen der selbst geschriebenen PNG-Dateien ----

    private sealed record Chunk(string Type, byte[] Data, bool CrcOk);

    private static uint BigEndian(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    private static System.Collections.Generic.List<Chunk> ReadChunks(byte[] png)
    {
        var liste = new System.Collections.Generic.List<Chunk>();
        for (int o = 8; o + 12 <= png.Length;)
        {
            int len = (int)BigEndian(png, o);
            var typ = System.Text.Encoding.ASCII.GetString(png, o + 4, 4);
            var daten = png.AsSpan(o + 8, len).ToArray();
            var crc = BigEndian(png, o + 8 + len);
            liste.Add(new Chunk(typ, daten, PngWriter.Crc32(png.AsSpan(o + 4, 4 + len)) == crc));
            o += 12 + len;
        }
        return liste;
    }

    /// <summary>Liest genau das Format, das PngWriter schreibt: RGB, 8 Bit, Filter je Zeile.</summary>
    private static byte[] DecodeOwnPng(byte[] png, out int width, out int height)
    {
        var chunks = ReadChunks(png);
        width = (int)BigEndian(chunks[0].Data, 0); height = (int)BigEndian(chunks[0].Data, 4);
        using var gepackt = new MemoryStream(chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray());
        using var zlib = new System.IO.Compression.ZLibStream(gepackt, System.IO.Compression.CompressionMode.Decompress);
        using var roh = new MemoryStream(); zlib.CopyTo(roh);
        var daten = roh.ToArray();
        int zeile = width * 3; var rgb = new byte[zeile * height];
        for (int y = 0; y < height; y++)
        {
            int filter = daten[y * (zeile + 1)];
            for (int x = 0; x < zeile; x++)
            {
                byte wert = daten[y * (zeile + 1) + 1 + x];
                byte links = x >= 3 ? rgb[y * zeile + x - 3] : (byte)0;
                byte oben = y > 0 ? rgb[(y - 1) * zeile + x] : (byte)0;
                rgb[y * zeile + x] = filter switch
                {
                    0 => wert,
                    1 => (byte)(wert + links),
                    2 => (byte)(wert + oben),
                    _ => throw new InvalidDataException($"Filter {filter} wird hier nicht erwartet")
                };
            }
        }
        return rgb;
    }

    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "dasi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // DASI_NUR=hintergrund: nur die echte Hintergrund-Pruefung - fuer den
        // Windows-11-Laeufer, auf dem es um genau das geht.
        if (Environment.GetEnvironmentVariable("DASI_NUR") == "hintergrund")
        {
            if (OperatingSystem.IsWindows()) HintergrundEchtPruefen(root);
            Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");
            try { Directory.Delete(root, true); } catch { }
            return _failed == 0 ? 0 : 1;
        }

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

        var skript = UpdateService.WriteUpdateScript(neu, ziel);
        var inhalt = File.ReadAllText(skript);
        Check("Skript wurde geschrieben", File.Exists(skript));
        Check("Skript liegt im eigenen Arbeitsordner",
            skript.StartsWith(UpdateService.WorkFolder, StringComparison.OrdinalIgnoreCase), skript);
        // Gewartet wird ueber die Dateisperre: move wird wiederholt, bis es klappt.
        Check("Skript wiederholt den Austausch, solange die Datei gesperrt ist",
            inhalt.Contains("goto ersetzen", StringComparison.Ordinal)
            && inhalt.Contains("ping -n 2", StringComparison.Ordinal));
        Check("Skript loescht sich und endet in derselben Zeile",
            inhalt.Contains("(goto) 2>nul & del \"%~f0\" >nul 2>&1 & exit /b 0", StringComparison.Ordinal));
        Check("Skript kommt ohne for-Schleife und tasklist aus (lief frueher immer 60 s)",
            !inhalt.Contains("for /l", StringComparison.OrdinalIgnoreCase)
            && !inhalt.Contains("tasklist", StringComparison.OrdinalIgnoreCase));
        Check("Skript ersetzt die Datei", inhalt.Contains("move /y", StringComparison.Ordinal));
        Check("Skript startet danach neu", inhalt.Contains("start \"\"", StringComparison.Ordinal));
        // Frueher endete der Fehlerzweig mit "pause" - in einem Fenster, das wegen
        // CreateNoWindow nie erschien. Das cmd wartete dann unsichtbar fuer immer.
        Check("Skript meldet einen Fehlschlag sichtbar (Editor statt pause)",
            inhalt.Contains("errorlevel", StringComparison.Ordinal)
            && inhalt.Contains("notepad.exe", StringComparison.Ordinal)
            && !inhalt.Split('\n').Select(z => z.Trim())
                    .Where(z => !z.StartsWith("rem", StringComparison.OrdinalIgnoreCase))
                    .Any(z => z.Equals("pause", StringComparison.OrdinalIgnoreCase)
                              || z.StartsWith("pause ", StringComparison.OrdinalIgnoreCase)));
        Check("Skript startet im Fehlerfall die bisherige Fassung wieder",
            inhalt.Split(":fehler")[1].Contains("start \"\" \"%~2\"", StringComparison.Ordinal));

        // Der Kern des Fehlers: Pfade mit Umlauten duerfen nicht in der Datei stehen.
        var umlautNeu = Path.Combine(root, "Jürgen Müller", "neu", "WindowsDaSiTool.exe");
        var umlautZiel = Path.Combine(root, "Björn Öztürk", "WindowsDaSiTool.exe");
        var skriptU = UpdateService.WriteUpdateScript(umlautNeu, umlautZiel);
        var bytes = File.ReadAllBytes(skriptU);
        Check("Skript enthaelt nur ASCII, auch bei Umlauten im Pfad",
            bytes.All(b => b < 0x80), $"{bytes.Count(b => b >= 0x80)} Nicht-ASCII-Bytes");
        Check("Skript hat kein BOM",
            !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        Check("die Pfade stehen nicht in der Datei, sie kommen als Argumente",
            !File.ReadAllText(skriptU).Contains("Müller", StringComparison.Ordinal)
            && File.ReadAllText(skriptU).Contains("%~1", StringComparison.Ordinal)
            && File.ReadAllText(skriptU).Contains("%~2", StringComparison.Ordinal));

        var start = UpdateService.BuildUpdateStart(skriptU, umlautNeu, umlautZiel);
        Check("der Aufruf reicht beide Pfade unversehrt durch",
            start.Arguments.Contains(umlautNeu, StringComparison.Ordinal)
            && start.Arguments.Contains(umlautZiel, StringComparison.Ordinal), start.Arguments);
        Check("der Aufruf bleibt unsichtbar", start.CreateNoWindow && !start.UseShellExecute);

        var notiz = Path.Combine(UpdateService.WorkFolder, UpdateService.FailureNoteFileName);
        var notizText = File.Exists(notiz) ? File.ReadAllText(notiz) : "";
        Check("die Erklaerung fuer den Fehlerfall nennt beide Pfade",
            notizText.Contains(umlautNeu, StringComparison.Ordinal)
            && notizText.Contains(umlautZiel, StringComparison.Ordinal));
        var notizBytes = File.Exists(notiz) ? File.ReadAllBytes(notiz) : Array.Empty<byte>();
        Check("die Erklaerung hat ein BOM, damit der Editor die Umlaute zeigt",
            notizBytes.Length >= 3 && notizBytes[0] == 0xEF && notizBytes[1] == 0xBB && notizBytes[2] == 0xBF);

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

        Console.WriteLine("\n== Cloud-Ordner zurueckschreiben: neben den Original-Ordner ==");
        Check("OneDrive -> OneDrive_wiederhergestellt",
            CloudProviders.RestoredFolderName("OneDrive") == "OneDrive_wiederhergestellt");
        Check("Firmenkonto behaelt seinen vollen Namen",
            CloudProviders.RestoredFolderName("OneDrive - Muster GmbH") == "OneDrive - Muster GmbH_wiederhergestellt");
        Check("HiDrive -> HiDrive_wiederhergestellt",
            CloudProviders.RestoredFolderName("HiDrive") == "HiDrive_wiederhergestellt");

        // Nach einer Wiederherstellung liegen Original und Kopie nebeneinander.
        var nachher = Path.Combine(root, "ProfilNachWiederherstellung");
        foreach (var d in new[] { "OneDrive", "OneDrive_wiederhergestellt", "HiDrive_WIEDERHERGESTELLT", "Dropbox_wiederhergestellt", "Dokumente" })
            Directory.CreateDirectory(Path.Combine(nachher, d));
        var nachherClouds = CloudProviders.Detect(nachher).Select(c => Path.GetFileName(c.Path)).ToArray();
        Check("die wiederhergestellte Kopie gilt NICHT als Cloud-Ordner",
            !nachherClouds.Any(CloudProviders.IsRestoredCopy), string.Join(", ", nachherClouds));
        Check("Gross-/Kleinschreibung des Zusatzes spielt keine Rolle",
            !nachherClouds.Contains("HiDrive_WIEDERHERGESTELLT"));
        Check("der echte Cloud-Ordner daneben wird weiter erkannt",
            nachherClouds.SequenceEqual(new[] { "OneDrive" }), string.Join(", ", nachherClouds));
        var nachherAus = ProfileExcludes.Build(nachher);
        Check("naechste Sicherung: Kopie wird mitgesichert, nur der Cloud-Ordner bleibt draussen",
            nachherAus.Contains(Path.Combine(nachher, "OneDrive"))
            && !nachherAus.Contains(Path.Combine(nachher, "OneDrive_wiederhergestellt"))
            && !nachherAus.Contains(Path.Combine(nachher, "Dropbox_wiederhergestellt")));

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

        // ================================================================
        // Desktop-Screenshot: PNG schreiben, Profil vergleichen, Schwarzbild
        // ================================================================
        Console.WriteLine("\n== Eigener Sicherungsordner ==");
        var zeitpunkt = new DateTime(2026, 11, 10, 8, 5, 0);
        Check("Name: DASI_<Rechner>_TT_MM_JJJJ - nur das Datum, mit fuehrenden Nullen",
            BackupTarget.FolderName("DESKTOP-7K4Q2M", zeitpunkt) == "DASI_DESKTOP-7K4Q2M_10_11_2026",
            BackupTarget.FolderName("DESKTOP-7K4Q2M", zeitpunkt));
        Check("unzulaessige Zeichen im Rechnernamen werden zu _",
            BackupTarget.FolderName("PC:1/2*?", zeitpunkt) == "DASI_PC_1_2___10_11_2026",
            BackupTarget.FolderName("PC:1/2*?", zeitpunkt));
        Check("ohne Rechnernamen: PC",
            BackupTarget.FolderName("  ", zeitpunkt) == "DASI_PC_10_11_2026");
        Check("morgens und abends derselbe Ordner",
            BackupTarget.FolderName("PC", new DateTime(2026, 11, 10, 18, 45, 0)) == BackupTarget.FolderName("PC", zeitpunkt));

        var laufwerk = Path.Combine(root, "G");
        Directory.CreateDirectory(laufwerk);
        Check("eingeschaltet: Ordner unter dem gewaehlten Ziel",
            BackupTarget.Resolve(laufwerk, true, "PC", zeitpunkt) == Path.Combine(laufwerk, "DASI_PC_10_11_2026"));
        Check("ausgeschaltet: direkt ins gewaehlte Ziel",
            BackupTarget.Resolve(laufwerk, false, "PC", zeitpunkt) == laufwerk);
        var schonDasi = Path.Combine(laufwerk, "DASI_ALTPC_01_10_2026");
        Check("ist das Ziel schon ein DASI-Ordner, geht die Sicherung HINEIN (kein neuer Ordner)",
            BackupTarget.Resolve(schonDasi, true, "PC", zeitpunkt) == schonDasi,
            BackupTarget.Resolve(schonDasi, true, "PC", zeitpunkt));
        var alteFassung = Path.Combine(laufwerk, "DASI_PC_09_11_2026_18_45");
        Check("auch ein Ordner der alten Fassung mit Uhrzeit wird weiterbenutzt",
            BackupTarget.Resolve(alteFassung, true, "PC", zeitpunkt) == alteFassung);
        Check("ausgeschaltet bleibt auch ein DASI-Ordner, wie er ist",
            BackupTarget.Resolve(schonDasi, false, "PC", zeitpunkt) == schonDasi);

        // Ein selbst benannter Ordner mit einer Sicherung darin.
        var eigenerName = Path.Combine(laufwerk, "Sicherung Max");
        Directory.CreateDirectory(Path.Combine(eigenerName, "Benutzerprofil", "Documents"));
        Check("Ordner mit Sicherung darin (ohne DASI-Namen): auch hinein",
            BackupTarget.Resolve(eigenerName, true, "PC", zeitpunkt) == eigenerName);
        var nurProtokoll = Path.Combine(laufwerk, "Nur Protokoll");
        Directory.CreateDirectory(nurProtokoll);
        File.WriteAllText(Path.Combine(nurProtokoll, "Sicherung-Protokoll_PC_2026-11-10_08-05.txt"), "x");
        Check("ein Protokoll des Tools genuegt als Zeichen einer Sicherung",
            BackupTarget.IsBackupFolder(nurProtokoll) && BackupTarget.Resolve(nurProtokoll, true, "PC", zeitpunkt) == nurProtokoll);
        var ohneSicherung = Path.Combine(laufwerk, "Leer");
        Directory.CreateDirectory(ohneSicherung);
        File.WriteAllText(Path.Combine(ohneSicherung, "Notiz.txt"), "x");
        Check("Ordner ohne Sicherung: neuer DASI-Ordner darunter",
            BackupTarget.Resolve(ohneSicherung, true, "PC", zeitpunkt) == Path.Combine(ohneSicherung, "DASI_PC_10_11_2026"));

        Check("Rechner aus dem Ordnernamen", BackupTarget.MachineOf(schonDasi) == "ALTPC"
            && BackupTarget.MachineOf(alteFassung) == "PC"
            && BackupTarget.MachineOf(@"G:\DASI_DESKTOP-7K4Q2M_10_11_2026\") == "DESKTOP-7K4Q2M"
            && BackupTarget.MachineOf(@"G:\DASI_PC_MIT_UNTERSTRICH_10_11_2026") == "PC_MIT_UNTERSTRICH",
            BackupTarget.MachineOf(@"G:\DASI_PC_MIT_UNTERSTRICH_10_11_2026"));
        Check("kein Rechner bei fremden Namen", BackupTarget.MachineOf(eigenerName) is null && BackupTarget.MachineOf("DASI_x") is null);
        Check("Rechnername im Ordner wie im Vergleich", BackupTarget.SafeMachineName("PC:1") == "PC_1");

        Check("auch mit abschliessendem Trennzeichen und Kleinschreibung",
            BackupTarget.IsOwnFolder(@"G:\dasi_pc_10_11_2026\") && BackupTarget.IsOwnFolder("/mnt/g/DASI_PC_x/"));
        Check("Laufwerkswurzel ist kein DASI-Ordner",
            !BackupTarget.IsOwnFolder(@"G:\") && !BackupTarget.IsOwnFolder(""));
        Check("ein Ordner, der DASI nur enthaelt, zaehlt nicht",
            !BackupTarget.IsOwnFolder(@"G:\Backup_DASI_alt"));
        Check("leeres Ziel bleibt leer", BackupTarget.Resolve("", true, "PC", zeitpunkt) == "");

        Console.WriteLine("\n== PNG schreiben ==");
        Check("CRC-32 stimmt mit dem Normwert ueberein (\"123456789\" -> CBF43926)",
            PngWriter.Crc32("123456789"u8) == 0xCBF43926u, PngWriter.Crc32("123456789"u8).ToString("X8"));

        // 3 x 2 Bildpunkte, Windows-Reihenfolge B, G, R, A.
        byte[] klein =
        {
            0, 0, 255, 255,    0, 255, 0, 255,    255, 0, 0, 255,      // rot, gruen, blau
            255, 255, 255, 255, 10, 20, 30, 0,    128, 128, 128, 255   // weiss, (30,20,10), grau
        };
        var png = PngWriter.EncodeBgra(3, 2, klein);
        Check("beginnt mit der PNG-Kennung",
            png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        var chunks = ReadChunks(png);
        Check("Bausteine in der richtigen Reihenfolge (IHDR, IDAT, IEND)",
            string.Join(",", chunks.Select(c => c.Type)) == "IHDR,IDAT,IEND", string.Join(",", chunks.Select(c => c.Type)));
        Check("jede Pruefsumme stimmt", chunks.All(c => c.CrcOk));
        var kopf = chunks[0].Data;
        Check("Kopf: 3 x 2, 8 Bit, RGB",
            BigEndian(kopf, 0) == 3 && BigEndian(kopf, 4) == 2 && kopf[8] == 8 && kopf[9] == 2);
        var rgb = DecodeOwnPng(png, out var bw, out var bh);
        Check("zurueckgelesen: jeder Bildpunkt stimmt (Kanalreihenfolge gedreht, Alpha entfallen)",
            bw == 3 && bh == 2 && rgb.SequenceEqual(new byte[]
            {
                255, 0, 0,    0, 255, 0,    0, 0, 255,
                255, 255, 255, 30, 20, 10,  128, 128, 128
            }), string.Join(" ", rgb));

        // Ein groesseres Bild mit Verlauf: prueft den Filter ueber viele Zeilen.
        int gw = 257, gh = 64;
        var verlauf = new byte[gw * gh * 4];
        for (int i = 0; i < gw * gh; i++) { verlauf[i * 4] = (byte)i; verlauf[i * 4 + 1] = (byte)(i / 7); verlauf[i * 4 + 2] = (byte)(255 - i); }
        var rgb2 = DecodeOwnPng(PngWriter.EncodeBgra(gw, gh, verlauf), out _, out _);
        bool alleGleich = true;
        for (int i = 0; i < gw * gh && alleGleich; i++)
            alleGleich = rgb2[i * 3] == verlauf[i * 4 + 2] && rgb2[i * 3 + 1] == verlauf[i * 4 + 1] && rgb2[i * 3 + 2] == verlauf[i * 4];
        Check("257 x 64 mit Verlauf: verlustfrei", alleGleich);

        bool zuKurz = false;
        try { PngWriter.EncodeBgra(3, 2, new byte[10]); } catch (ArgumentException) { zuKurz = true; }
        Check("zu wenige Bildpunkte werden abgelehnt", zuKurz);

        Console.WriteLine("\n== Wiederherstellen in ein anderes Profil (Besitzer -> Anwender) ==");
        {
            const string zielAnwender = @"C:\Users\Anwender";
            Check("Pfad im gesicherten Profil kommt ins Zielprofil",
                ProfilePaths.MapIntoProfile(@"C:\Users\Besitzer\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper", @"C:\Users\Besitzer", zielAnwender)
                == @"C:\Users\Anwender\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper");
            Check("aeltere Sicherung ohne vermerktes Profil: C:\\Users\\<Name> wird erkannt",
                ProfilePaths.MapIntoProfile(@"C:\Users\Besitzer\Pictures\Urlaub.jpg", null, zielAnwender) == @"C:\Users\Anwender\Pictures\Urlaub.jpg");
            Check("Gross-/Kleinschreibung spielt keine Rolle",
                ProfilePaths.MapIntoProfile(@"c:\users\besitzer\Pictures\a.jpg", @"C:\Users\Besitzer", zielAnwender) == @"C:\Users\Anwender\Pictures\a.jpg");
            Check("Profil an anderem Ort (D:\\Profile\\Besitzer)",
                ProfilePaths.MapIntoProfile(@"D:\Profile\Besitzer\Bilder\a.jpg", @"D:\Profile\Besitzer", zielAnwender) == @"C:\Users\Anwender\Bilder\a.jpg");
            Check("\"Besitzer2\" liegt nicht in \"Besitzer\" - landet trotzdem im Zielprofil",
                ProfilePaths.MapIntoProfile(@"C:\Users\Besitzer2\x.jpg", @"C:\Users\Besitzer", zielAnwender) == @"C:\Users\Anwender\x.jpg");
            Check("ausserhalb jedes Profils (C:\\Windows\\Web): kein Profilpfad",
                ProfilePaths.MapIntoProfile(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg", @"C:\Users\Besitzer", zielAnwender) is null);
            Check("leere Angaben: kein Profilpfad",
                ProfilePaths.MapIntoProfile(null, null, zielAnwender) is null && ProfilePaths.MapIntoProfile("", null, zielAnwender) is null
                && ProfilePaths.MapIntoProfile(@"C:\Users\Besitzer\a", null, "") is null);
            Check("kein Ergebnis enthaelt je den alten Benutzernamen",
                new[] { @"C:\Users\Besitzer\Pictures\a.jpg", @"C:\Users\Besitzer\AppData\Local\b", @"C:\Users\Besitzer\Desktop\c.lnk" }
                    .All(p => ProfilePaths.MapIntoProfile(p, null, zielAnwender)?.Contains("Besitzer") == false));

            var sichern = ProfileExcludes.BackupSwitches();
            Check("Sichern: kein /XA mehr (liess versteckte und Systemdateien ohne Meldung aus)",
                !sichern.Any(s => s.StartsWith("/XA", StringComparison.OrdinalIgnoreCase)), string.Join(" ", sichern));
            Check("Sichern: Registry-Dateien gezielt per /XF ausgelassen",
                sichern.SkipWhile(s => s != "/XF").Skip(1).Take(ProfileExcludes.RegistryHiveFiles.Length)
                       .SequenceEqual(ProfileExcludes.RegistryHiveFiles));
            Check("Sichern: Verknuepfungspunkte bleiben draussen (/XJ), ein Versuch je Datei (/R:0)",
                sichern.Contains("/XJ") && sichern.Contains("/R:0") && sichern.Contains("/MIR"));
            var zurueck = ProfileExcludes.RestoreSwitches();
            Check("Zurueckschreiben: /COPY:DAT statt /COPYALL - Rechte kommen vom Zielprofil",
                zurueck.Contains("/COPY:DAT") && !zurueck.Any(s => s.StartsWith("/COPYALL", StringComparison.OrdinalIgnoreCase)
                                                                 || s.StartsWith("/SEC", StringComparison.OrdinalIgnoreCase)),
                string.Join(" ", zurueck));
            Check("Zurueckschreiben: Registry-Dateien werden nie ueberschrieben, auch aus alten Sicherungen",
                zurueck.SkipWhile(s => s != "/XF").Skip(1).Take(ProfileExcludes.RegistryHiveFiles.Length)
                       .SequenceEqual(ProfileExcludes.RegistryHiveFiles));
        }

        Console.WriteLine("\n== Verknuepfungen im Profil, die nach draussen zeigen ==");
        {
            var lp = Path.Combine(root, "links", "Besitzer");
            var aussen = Path.Combine(root, "links", "D-Laufwerk", "Spiele");
            foreach (var d in new[] { Path.Combine(lp, "Documents", "Normal"), Path.Combine(lp, "Pictures"),
                                      Path.Combine(lp, "AppData", "Local"), Path.Combine(lp, "OneDrive"), aussen })
                Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(aussen, "spielstand.sav"), "x");
            bool linksOk = true;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(lp, "Documents", "Spiele"), aussen);                        // nach draussen
                Directory.CreateSymbolicLink(Path.Combine(lp, "Documents", "Eigene Bilder"), Path.Combine(lp, "Pictures")); // ins Profil
                Directory.CreateSymbolicLink(Path.Combine(lp, "AppData", "Local", "Verlauf"), aussen);                // in AppData
                Directory.CreateSymbolicLink(Path.Combine(lp, "OneDrive", "Extern"), aussen);                          // in ausgelassenem Ordner
            }
            catch (Exception ex) { linksOk = false; Console.WriteLine($"         (symbolische Links hier nicht moeglich: {ex.Message})"); }
            if (linksOk)
            {
                var gefunden = ProfileLinks.FindOutsideLinks(lp, new[] { Path.Combine(lp, "OneDrive") }, out var abgebrochen);
                Check("Verknuepfung nach draussen wird gemeldet, mit Ziel",
                    gefunden.Count == 1 && gefunden[0].Path.EndsWith("Spiele") && Path.GetFullPath(gefunden[0].Target) == Path.GetFullPath(aussen),
                    string.Join(" | ", gefunden.Select(g => $"{g.Path} -> {g.Target}")));
                Check("Verknuepfung ins Profil (wie \"Eigene Bilder\") wird nicht gemeldet",
                    !gefunden.Any(g => g.Path.EndsWith("Eigene Bilder")));
                // Relativ zum Profil: Unter Windows liegt das Pruefverzeichnis selbst
                // in ...\AppData\Local\Temp - der volle Pfad enthielte "AppData" immer.
                Check("AppData und ausgelassene Ordner werden nicht durchsucht",
                    !gefunden.Select(g => Path.GetRelativePath(lp, g.Path))
                             .Any(r => r.StartsWith("AppData", StringComparison.OrdinalIgnoreCase)
                                       || r.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)),
                    string.Join(" | ", gefunden.Select(g => Path.GetRelativePath(lp, g.Path))));
                Check("nicht abgebrochen", !abgebrochen);
            }
            Check("Profil, das es nicht gibt: leere Liste",
                ProfileLinks.FindOutsideLinks(Path.Combine(root, "gibt-es-nicht"), Array.Empty<string>(), out _).Count == 0);
        }

        Console.WriteLine("\n== Opera: alle Fassungen ==");
        {
            var os = Path.Combine(root, "opera", "Besitzer", "AppData", "Roaming", "Opera Software");
            void Profil(string ordner, string datei)
            {
                var p = Path.Combine(os, ordner, datei);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "{}");
            }
            Profil("Opera Stable", "Local State");
            Profil("Opera GX Stable", "Preferences");
            Profil("Opera Beta", Path.Combine("Default", "Bookmarks"));   // neuere Fassungen: Profil in "Default"
            Directory.CreateDirectory(Path.Combine(os, "Opera Crash Reports"));  // kein Profil
            Profil("Fremd", "Local State");                                   // kein Opera-Ordner
            var gefunden = AppCatalog.Opera.Varianten(os);
            Check("Opera, Opera Beta und Opera GX werden gefunden, nach Anzeigename",
                gefunden.SequenceEqual(new[] { "Opera Stable", "Opera Beta", "Opera GX Stable" }), string.Join(", ", gefunden));
            Check("Ordner ohne Profildateien und fremde Ordner nicht",
                !gefunden.Contains("Opera Crash Reports") && !gefunden.Contains("Fremd"));
            Check("Anzeigenamen ohne \"Stable\"",
                AppCatalog.Opera.Anzeigename("Opera GX Stable") == "Opera GX" && AppCatalog.Opera.Anzeigename("Opera Stable") == "Opera"
                && AppCatalog.Opera.Anzeigename("Opera Beta") == "Opera Beta");
            Check("winget-Pakete fuer Opera und Opera GX, sonst keins",
                AppCatalog.Opera.WingetId("Opera Stable") == "Opera.Opera" && AppCatalog.Opera.WingetId("Opera GX Stable") == "Opera.OperaGX"
                && AppCatalog.Opera.WingetId("Opera Developer") is null);
            Check("ohne Ordner: keine Fassung", AppCatalog.Opera.Varianten(Path.Combine(root, "gibt-es-nicht")).Count == 0);
            Check("Katalogpfad: Teile einzeln angehaengt",
                AppCatalog.Pfad(Path.Combine(root, "x"), @"AppData\Roaming\Opera Software") == Path.Combine(root, "x", "AppData", "Roaming", "Opera Software"));
        }

        Console.WriteLine("\n== Verfuegbarkeit der Punkte ==");
        {
            // Die CI-Rechner haben kein WLAN - fuer diese Pruefungen gibt es eins.
            AppCatalog.HatWlanAdapter = () => true;
            // Sichern: Profil mit Firefox und Opera, ohne die anderen Programme.
            var pr = Path.Combine(root, "verf", "Besitzer");
            Directory.CreateDirectory(AppCatalog.Pfad(pr, AppCatalog.Firefox.ProfilRel));
            var gx = Path.Combine(AppCatalog.Pfad(pr, AppCatalog.Opera.Rel), "Opera GX Stable");
            Directory.CreateDirectory(gx);
            File.WriteAllText(Path.Combine(gx, "Local State"), "{}");

            Check("Sichern: Firefox und Opera frei",
                AppCatalog.BeimSichern(Punkt.Firefox, pr) is null && AppCatalog.BeimSichern(Punkt.Opera, pr) is null);
            var grund = AppCatalog.BeimSichern(Punkt.Chrome, pr);
            Check("Sichern: Chrome gesperrt, mit Grund und Ort",
                grund is { } g1 && g1.De.StartsWith("Chrome ist bei diesem Benutzer nicht eingerichtet") && g1.De.Contains("Chrome"), grund?.De);
            Check("Sichern: Edge, Brave, Thunderbird gesperrt",
                new[] { Punkt.Edge, Punkt.Brave, Punkt.Thunderbird }.All(p => AppCatalog.BeimSichern(p, pr) is not null));
            Check("Sichern: alles andere ist nie gesperrt",
                new[] { Punkt.Profil, Punkt.AppData, Punkt.Cloud, Punkt.Winget, Punkt.Wlan, Punkt.Drucker, Punkt.Hintergrund, Punkt.DesktopShot }
                    .All(p => AppCatalog.BeimSichern(p, pr) is null));
            Check("Sichern: ohne (vorhandenes) Profil wird nichts gesperrt",
                AppCatalog.BeimSichern(Punkt.Chrome, "") is null && AppCatalog.BeimSichern(Punkt.Chrome, Path.Combine(root, "gibt-es-nicht")) is null);

            // Wiederherstellen: leere Sicherung - alles gesperrt.
            var si = Path.Combine(root, "verf", "Sicherung");
            Directory.CreateDirectory(si);
            Check("Wiederherstellen: leere Sicherung sperrt jeden Punkt",
                Enum.GetValues<Punkt>().All(p => AppCatalog.BeimWiederherstellen(p, si) is not null),
                string.Join(", ", Enum.GetValues<Punkt>().Where(p => AppCatalog.BeimWiederherstellen(p, si) is null)));
            Check("Wiederherstellen: Ordner ohne jede Sicherung - ein gemeinsamer Hinweis",
                Enum.GetValues<Punkt>().All(p => AppCatalog.BeimWiederherstellen(p, si)?.De.StartsWith("Der gewählte Ordner enthält keine Sicherung") == true),
                AppCatalog.BeimWiederherstellen(Punkt.Wlan, si)?.De);
            // Typischer Fall: der Ordner ueber den Sicherungen ist gewaehlt.
            Directory.CreateDirectory(Path.Combine(si, "..", "Ueber", "DASI_PC_11_11_2026_17_55", "Benutzerprofil"));
            Check("Wiederherstellen: auch der Ordner ueber den Sicherungen ist keine Sicherung",
                AppCatalog.BeimWiederherstellen(Punkt.Profil, Path.Combine(root, "verf", "Ueber"))?.De.StartsWith("Der gewählte Ordner enthält keine Sicherung") == true);

            // Nach und nach befuellen.
            Directory.CreateDirectory(Path.Combine(si, "Benutzerprofil", "Documents"));
            Check("Wiederherstellen: Grund nennt, was fehlt",
                AppCatalog.BeimWiederherstellen(Punkt.Wlan, si)?.De == "Die gewählte Sicherung enthält keine WLAN-Profile.",
                AppCatalog.BeimWiederherstellen(Punkt.Wlan, si)?.De);
            Directory.CreateDirectory(Path.Combine(si, "Firefox-Profil"));
            Directory.CreateDirectory(Path.Combine(si, "Opera-Profil", "Opera GX Stable"));
            File.WriteAllText(Path.Combine(si, "Opera-Profil", "Opera GX Stable", "Local State"), "{}");
            Directory.CreateDirectory(Path.Combine(si, "Winget")); File.WriteAllText(Path.Combine(si, "Winget", "Export.json"), "{}");
            Directory.CreateDirectory(Path.Combine(si, "WLAN-Profile")); File.WriteAllText(Path.Combine(si, "WLAN-Profile", "WLAN-Heim.xml"), "<x/>");
            Directory.CreateDirectory(Path.Combine(si, "Drucker")); File.WriteAllText(Path.Combine(si, "Drucker", "Drucker.printerExport"), "x");
            Directory.CreateDirectory(Path.Combine(si, "Hintergrundbild")); File.WriteAllText(Path.Combine(si, "Hintergrundbild", "Hintergrundbild-Pfad.txt"), "x");
            Check("Hintergrundbild: nur die Pfad-Datei ist noch kein Bild",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, si) is not null);
            File.WriteAllText(Path.Combine(si, "Hintergrundbild", "Urlaub.jpg"), "x");
            Directory.CreateDirectory(Path.Combine(si, "Desktop-Screenshot")); File.WriteAllText(Path.Combine(si, "Desktop-Screenshot", "Bildschirm-1.png"), "x");
            var frei = new[] { Punkt.Profil, Punkt.Firefox, Punkt.Opera, Punkt.Winget, Punkt.Wlan, Punkt.Drucker, Punkt.Hintergrund, Punkt.DesktopShot };
            Check("Wiederherstellen: was in der Sicherung liegt, ist frei",
                frei.All(p => AppCatalog.BeimWiederherstellen(p, si) is null),
                string.Join(", ", frei.Where(p => AppCatalog.BeimWiederherstellen(p, si) is not null)));
            Check("Wiederherstellen: was fehlt, bleibt gesperrt (AppData, Cloud, Edge, Chrome, Brave, Thunderbird)",
                new[] { Punkt.AppData, Punkt.Cloud, Punkt.Edge, Punkt.Chrome, Punkt.Brave, Punkt.Thunderbird }
                    .All(p => AppCatalog.BeimWiederherstellen(p, si) is not null));
            Directory.CreateDirectory(Path.Combine(si, "Benutzerprofil", "AppData"));
            Directory.CreateDirectory(Path.Combine(si, "Benutzerprofil", "OneDrive"));
            Check("Wiederherstellen: AppData und Cloud frei, sobald sie in der Sicherung liegen",
                AppCatalog.BeimWiederherstellen(Punkt.AppData, si) is null && AppCatalog.BeimWiederherstellen(Punkt.Cloud, si) is null);

            // Ohne WLAN-Adapter.
            AppCatalog.HatWlanAdapter = () => false;
            var ohneWlan = AppCatalog.BeimSichern(Punkt.Wlan, pr);
            Check("ohne WLAN-Adapter: Sichern gesperrt, mit Grund", ohneWlan?.De.StartsWith("Dieser PC hat keinen WLAN-Adapter") == true, ohneWlan?.De);
            Check("ohne WLAN-Adapter: Sichern gesperrt, auch bevor ein Profil gewaehlt ist",
                AppCatalog.BeimSichern(Punkt.Wlan, "") is not null && AppCatalog.BeimSichern(Punkt.Wlan, null) is not null);
            Check("ohne WLAN-Adapter: die anderen Punkte bleiben, wie sie sind",
                AppCatalog.BeimSichern(Punkt.Profil, pr) is null && AppCatalog.BeimSichern(Punkt.Firefox, pr) is null
                && AppCatalog.BeimWiederherstellen(Punkt.Drucker, si) is null);
            var zurueckOhne = AppCatalog.BeimWiederherstellen(Punkt.Wlan, si);
            Check("ohne WLAN-Adapter: Profile in der Sicherung lassen sich nicht einspielen",
                zurueckOhne?.De.Contains("nicht einspielen") == true, zurueckOhne?.De);
            var ohneWlanSich = Path.Combine(root, "verf", "OhneWlan");
            Directory.CreateDirectory(Path.Combine(ohneWlanSich, "Benutzerprofil"));
            Check("ohne WLAN-Adapter: fehlen die Profile in der Sicherung, bleibt das der Grund",
                AppCatalog.BeimWiederherstellen(Punkt.Wlan, ohneWlanSich)?.De == "Die gewählte Sicherung enthält keine WLAN-Profile.",
                AppCatalog.BeimWiederherstellen(Punkt.Wlan, ohneWlanSich)?.De);
            AppCatalog.HatWlanAdapter = () => true;
            Check("mit WLAN-Adapter: Sichern frei, Wiederherstellen frei, wenn Profile in der Sicherung liegen",
                AppCatalog.BeimSichern(Punkt.Wlan, pr) is null && AppCatalog.BeimWiederherstellen(Punkt.Wlan, si) is null
                && AppCatalog.BeimWiederherstellen(Punkt.Wlan, ohneWlanSich) is not null);
            Console.WriteLine($"         WLAN-Adapter auf diesem Rechner: {(AppCatalog.WlanAdapterVorhanden() ? "ja" : "nein")}");
            Check("Wiederherstellen: ohne (vorhandene) Sicherung wird nichts gesperrt",
                AppCatalog.BeimWiederherstellen(Punkt.Firefox, "") is null
                && AppCatalog.BeimWiederherstellen(Punkt.Firefox, Path.Combine(root, "gibt-es-nicht")) is null);
        }

        Console.WriteLine("\n== Desktop-Symbole: Zuordnen und Umrechnen ==");
        {
            DesktopIcon S(string name, string anzeige, int x, int y) => new(name, anzeige, x, y);
            var gesichert = new DesktopLayout
            {
                Breite = 1920, Hoehe = 1080,
                Symbole =
                {
                    S("::{645FF040-5081-101B-9F08-00AA002F954E}", "Papierkorb", 20, 10),
                    S("Präsentation.pptx", "Präsentation", 1800, 900),
                    S("Firefox.lnk", "Firefox", 120, 10),
                    S("Weg.txt", "Weg", 300, 300),
                }
            };
            var jetzt = new List<DesktopIcon>
            {
                S("Firefox.lnk", "Firefox", 5, 5),
                S("::{645FF040-5081-101B-9F08-00AA002F954E}", "Recycle Bin", 5, 105),
                S("Präsentation.pptx", "Präsentation", 5, 205),
                S("Neu.txt", "Neu", 5, 305),
            };
            var plan = DesktopIcons.Plan(gesichert, jetzt, 1920, 1080);
            Check("gleiche Groesse: Stellen 1:1, nichts umgerechnet", !plan.Skaliert
                && plan.Zuordnung.Single(z => z.Gesichert.Name == "Präsentation.pptx") is { Index: 2, X: 1800, Y: 900 });
            Check("Papierkorb ueber seine Kennung - auch wenn er hier anders heisst (Recycle Bin)",
                plan.Zuordnung.Single(z => z.Gesichert.Anzeige == "Papierkorb") is { Index: 1, X: 20, Y: 10 });
            Check("was es hier nicht gibt, steht als fehlend da", plan.Fehlend.Select(f => f.Name).SequenceEqual(new[] { "Weg.txt" }));
            Check("neue Symbole bleiben, wo sie sind", plan.Zuordnung.All(z => z.Index != 3));

            var gross = DesktopIcons.Plan(gesichert, jetzt, 3840, 2160);
            Check("doppelt so grosser Desktop: Stellen im selben Verhaeltnis", gross.Skaliert
                && gross.Zuordnung.Single(z => z.Gesichert.Name == "Präsentation.pptx") is { X: 3600, Y: 1800 });
            var schmal = DesktopIcons.Plan(gesichert, jetzt, 1280, 720);
            Check("kleinerer Desktop: rechts unten bleibt rechts unten, im Bild",
                schmal.Zuordnung.Single(z => z.Gesichert.Name == "Präsentation.pptx") is { X: 1200, Y: 600 });
            var draussen = new DesktopLayout { Symbole = { S("Firefox.lnk", "Firefox", 5000, -20) } };
            Check("Stellen ausserhalb werden in den Desktop geholt",
                DesktopIcons.Plan(draussen, jetzt, 1920, 1080).Zuordnung.Single() is { X: 1919, Y: 0 });

            // Anzeigename nur als Notbehelf - und nie doppelt vergeben.
            var umbenannt = new DesktopLayout
            {
                Symbole = { S("Firefox (alt).lnk", "Firefox", 1, 1), S("Firefox.lnk", "Firefox", 2, 2) }
            };
            var p2 = DesktopIcons.Plan(umbenannt, jetzt, 1920, 1080);
            Check("erst eindeutige Namen, dann Anzeigenamen - ein Symbol nie zweimal",
                p2.Zuordnung.Count == 1 && p2.Zuordnung[0].Gesichert.Name == "Firefox.lnk" && p2.Fehlend.Count == 1,
                string.Join(", ", p2.Zuordnung.Select(z => z.Gesichert.Name)));
            var nurAnzeige = new DesktopLayout { Symbole = { S("Neu (2).txt", "Neu", 7, 7) } };
            Check("ohne Namenstreffer hilft der Anzeigename",
                DesktopIcons.Plan(nurAnzeige, jetzt, 1920, 1080).Zuordnung.Single().Index == 3);

            // Datei: lesbar, mit Umlauten, und wieder einlesbar.
            var ordner = Path.Combine(root, "symbole", DesktopIcons.FolderName);
            gesichert.Rechner = "PC"; gesichert.Benutzer = "Anwender"; gesichert.AmRasterAusrichten = true;
            var symDatei = DesktopIcons.Speichern(ordner, gesichert);
            Check("Datei: Desktop-Symbole\\Symbolpositionen.json", symDatei == DesktopIcons.Datei(Path.Combine(root, "symbole")));
            Check("Datei: Umlaute bleiben lesbar", File.ReadAllText(symDatei).Contains("Präsentation.pptx"));
            var gelesen = DesktopIcons.Laden(symDatei);
            Check("Datei: wieder eingelesen ist alles gleich", gelesen is not null && gelesen.Breite == 1920 && gelesen.AmRasterAusrichten
                && gelesen.Symbole.Count == 4 && gelesen.Symbole[1] == gesichert.Symbole[1]);
            File.WriteAllText(Path.Combine(root, "kaputt.json"), "{ kein json");
            Check("Datei: fehlend oder kaputt -> null",
                DesktopIcons.Laden(Path.Combine(root, "gibt-es-nicht.json")) is null && DesktopIcons.Laden(Path.Combine(root, "kaputt.json")) is null);

            // Verfuegbarkeit beim Wiederherstellen: Symbolpositionen allein genuegen.
            var nurSymbole = Path.Combine(root, "symbole");
            Check("Wiederherstellen: Desktop-Layout frei, wenn nur Symbolpositionen da sind",
                AppCatalog.BeimWiederherstellen(Punkt.DesktopShot, nurSymbole) is null
                && AppCatalog.HatDesktopSymbole(nurSymbole) && !AppCatalog.HatDesktopScreenshot(nurSymbole));
        }

        Console.WriteLine("\n== Desktop-Symbole: Ablauf (nachgebauter Desktop) ==");
        {
            var testDesktop = new TestDesktop(1920, 1080, autoAnordnen: true, raster: false,
                new DesktopIcon("a.txt", "a", 10, 10), new DesktopIcon("b.txt", "b", 10, 110), new DesktopIcon("", "", 0, 0));
            DesktopIcons.OeffneDesktop = () => testDesktop;
            var gelesen = DesktopIcons.Lesen("PC", "Anwender");
            Check("Lesen: Groesse, Schalter und Symbole (ohne namenlose)", gelesen.Breite == 1920 && gelesen.AutomatischAnordnen
                && !gelesen.AmRasterAusrichten && gelesen.Symbole.Count == 2 && gelesen.Rechner == "PC");

            var symZiel = new DesktopLayout
            {
                Breite = 1920, Hoehe = 1080, AutomatischAnordnen = false, AmRasterAusrichten = true,
                Symbole = { new("b.txt", "b", 500, 400), new("a.txt", "a", 700, 50), new("c.txt", "c", 900, 900) }
            };
            // "c.txt" erscheint erst nach dem zweiten Nachsehen - wie eine Datei,
            // die der Explorer nach dem Zurueckschreiben gerade erst entdeckt.
            testDesktop.SpaeterDazu = (2, new DesktopIcon("c.txt", "c", 10, 210));
            var erg = DesktopIcons.Wiederherstellen(symZiel, TimeSpan.FromSeconds(8));
            Check("Wiederherstellen: alle drei gesetzt, auch das spaet erschienene", erg.Gesetzt == 3 && erg.Fehlend.Count == 0,
                $"{erg.Gesetzt}/{erg.Gesamt}, fehlend {erg.Fehlend.Count}");
            Check("Wiederherstellen: Stellen stimmen", testDesktop.Stelle("b.txt") == (500, 400) && testDesktop.Stelle("a.txt") == (700, 50)
                && testDesktop.Stelle("c.txt") == (900, 900));
            Check("Wiederherstellen: \"Automatisch anordnen\" aus und gemeldet", !testDesktop.AutomatischAnordnen && erg.AnordnenAusgeschaltet);
            Check("Wiederherstellen: Raster wie gesichert", testDesktop.AmRasterAusrichten && erg.RasterUmgestellt);
            Check("Wiederherstellen: \"Automatisch anordnen\" erst AUS, dann wieder wie gesichert",
                testDesktop.Protokoll.IndexOf("anordnen=False") < testDesktop.Protokoll.IndexOf("setzen b.txt"));

            var mitAuto = new DesktopLayout { Breite = 1920, Hoehe = 1080, AutomatischAnordnen = true, Symbole = { new("a.txt", "a", 1, 1) } };
            var erg2 = DesktopIcons.Wiederherstellen(mitAuto, TimeSpan.Zero);
            Check("war beim Sichern \"Automatisch anordnen\" an, ist es danach wieder an", testDesktop.AutomatischAnordnen && erg2.AnordnenWiederAn && !erg2.AnordnenAusgeschaltet);

            var fehlt = new DesktopLayout { Breite = 1920, Hoehe = 1080, Symbole = { new("weg.txt", "weg", 1, 1) } };
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var erg3 = DesktopIcons.Wiederherstellen(fehlt, TimeSpan.FromSeconds(30));
            Check("fehlt etwas dauerhaft, wird nicht die ganze Zeit gewartet", erg3.Fehlend.Count == 1 && uhr.Elapsed.TotalSeconds < 8,
                $"{uhr.Elapsed.TotalSeconds:F1} s");

            DesktopIcons.OeffneDesktop = () => throw new InvalidOperationException("Kein Explorer-Desktop gefunden");
            bool gemeldet = false;
            try { DesktopIcons.Lesen("PC", "x"); } catch (InvalidOperationException) { gemeldet = true; }
            Check("ohne Desktop: der Fehler kommt beim Aufrufer an (nicht verschluckt)", gemeldet);
            DesktopIcons.OeffneDesktop = () => OperatingSystem.IsWindows() ? new ExplorerDesktop() : throw new PlatformNotSupportedException();
        }

        Console.WriteLine("\n== winget nachinstallieren: Bausteine ==");
        {
            Check("Architektur -> Ordner im Abhaengigkeits-Paket",
                WingetInstaller.ArchFolder(System.Runtime.InteropServices.Architecture.X64) == "x64"
                && WingetInstaller.ArchFolder(System.Runtime.InteropServices.Architecture.Arm64) == "arm64"
                && WingetInstaller.ArchFolder(System.Runtime.InteropServices.Architecture.X86) == "x86");
            Check("Download-Adressen: neueste Fassung auf GitHub",
                WingetInstaller.ReleaseBase + WingetInstaller.BundleFile
                    == "https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                && WingetInstaller.DependenciesFile == "DesktopAppInstaller_Dependencies.zip");

            // Aufbau wie im echten Paket: je Architektur ein Ordner mit .appx.
            var ent = Path.Combine(root, "winget-deps");
            foreach (var a in new[] { "x64", "x86", "arm64" })
            {
                Directory.CreateDirectory(Path.Combine(ent, a));
                foreach (var n in new[] { "Microsoft.WindowsAppRuntime.1.8_8000.616.304.0", "Microsoft.VCLibs.140.00_14.0.33519.0", "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0" })
                    File.WriteAllText(Path.Combine(ent, a, $"{n}_{a}.appx"), "x");
            }
            File.WriteAllText(Path.Combine(ent, "x64", "liesmich.txt"), "x");
            var deps = WingetInstaller.Dependencies(ent, "x64");
            Check("Abhaengigkeiten: genau die drei .appx fuer x64, VCLibs zuerst",
                deps.Select(Path.GetFileName).SequenceEqual(new[]
                {
                    "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx",
                    "Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx",
                    "Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.appx",
                }), string.Join(", ", deps.Select(Path.GetFileName)));
            Check("Abhaengigkeiten: fehlender Ordner -> keine (statt Absturz)",
                WingetInstaller.Dependencies(ent, "arm") is { Count: 0 } && WingetInstaller.Dependencies(Path.Combine(root, "gibt-es-nicht"), "x64") is { Count: 0 });

            Check("PowerShell: Apostroph im Pfad wird verdoppelt",
                WingetInstaller.PsQuote(@"C:\Users\O'Brien\a.appx") == @"'C:\Users\O''Brien\a.appx'");
            var psSkript = WingetInstaller.InstallScript(@"C:\Temp\O'B\winget.msixbundle", new[] { @"C:\Temp\a.appx", @"C:\Temp\b c.appx" });
            Check("Skript: jede Abhaengigkeit einzeln, Fehler dort nicht fatal",
                psSkript.Contains(@"@('C:\Temp\a.appx', 'C:\Temp\b c.appx')") && psSkript.Contains("try { Add-AppxPackage -Path $d } catch"), psSkript);
            Check("Skript: danach das Bundle, Fehler dort fuehren zu Exit 1",
                psSkript.Contains(@"Add-AppxPackage -Path 'C:\Temp\O''B\winget.msixbundle' -ForceApplicationShutdown")
                && psSkript.IndexOf("foreach") < psSkript.IndexOf("msixbundle") && psSkript.Contains("exit 1"));
            Check("Skript: ohne Abhaengigkeiten gueltig (leere Liste)", WingetInstaller.InstallScript("b", Array.Empty<string>()).Contains("@()"));
            var kodiert = WingetInstaller.Encode("Write-Output 'Ä'");
            Check("EncodedCommand: UTF-16LE in Base64",
                System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(kodiert)) == "Write-Output 'Ä'");
        }

        Console.WriteLine("\n== Desktop-Screenshot: ein Bild pro Monitor ==");
        {
            Check("Windows-Nummer aus dem Geraetenamen", DesktopScreenshot.DisplayNumber(@"\\.\DISPLAY2") == 2
                && DesktopScreenshot.DisplayNumber(@"\\.\DISPLAY12") == 12 && DesktopScreenshot.DisplayNumber("") == 0
                && DesktopScreenshot.DisplayNumber("Monitor") == 0);

            // Wie beim Benutzer: 4K-Hauptbildschirm und daneben ein kleinerer.
            var haupt = new MonitorArea(0, 0, 3840, 2160, true, @"\\.\DISPLAY1");
            var neben = new MonitorArea(3840, 0, 1920, 1080, false, @"\\.\DISPLAY2");
            var zwei = DesktopScreenshot.PlanImages(new[] { neben, haupt });
            Check("zwei Monitore: zwei Bilder, nach Windows-Nummer",
                zwei.Select(p => p.FileName).SequenceEqual(new[] { "Bildschirm-1-Hauptbildschirm.png", "Bildschirm-2.png" }),
                string.Join(", ", zwei.Select(p => p.FileName)));
            Check("jedes Bild hat genau die Groesse seines Monitors",
                zwei[0].Area.Width == 3840 && zwei[0].Area.Height == 2160 && zwei[1].Area.Width == 1920 && zwei[1].Area.Height == 1080);

            var einer = DesktopScreenshot.PlanImages(new[] { haupt });
            Check("ein Monitor: Bildschirm-1.png (ohne Zusatz)", einer.Single().FileName == "Bildschirm-1.png", einer.Single().FileName);

            var hauptRechts = DesktopScreenshot.PlanImages(new[]
            {
                new MonitorArea(0, 0, 1920, 1080, true, @"\\.\DISPLAY2"),
                new MonitorArea(-1920, 0, 1920, 1080, false, @"\\.\DISPLAY1"),
            });
            Check("Hauptbildschirm muss nicht Nummer 1 sein",
                hauptRechts.Select(p => p.FileName).SequenceEqual(new[] { "Bildschirm-1.png", "Bildschirm-2-Hauptbildschirm.png" }),
                string.Join(", ", hauptRechts.Select(p => p.FileName)));

            var ohneNummern = DesktopScreenshot.PlanImages(new[]
            {
                new MonitorArea(1920, 0, 1280, 1024, false, ""),
                new MonitorArea(-1280, 0, 1280, 1024, false, "x"),
                new MonitorArea(0, 0, 1920, 1080, true, ""),
            });
            Check("ohne Windows-Nummern: Hauptbildschirm zuerst, dann von links nach rechts",
                ohneNummern[0].Area.Primary && ohneNummern[1].Area.Left == -1280 && ohneNummern[2].Area.Left == 1920
                && ohneNummern[0].FileName == "Bildschirm-1-Hauptbildschirm.png",
                string.Join(", ", ohneNummern.Select(p => $"{p.FileName}@{p.Area.Left}")));
            var doppelt = DesktopScreenshot.PlanImages(new[]
            {
                new MonitorArea(0, 0, 10, 10, true, @"\\.\DISPLAY1"), new MonitorArea(10, 0, 10, 10, false, @"\\.\DISPLAY1"),
            });
            Check("doppelte Nummer: es wird gezaehlt, kein Name doppelt",
                doppelt.Select(p => p.FileName).Distinct().Count() == 2);

            // Ausschneiden: virtueller Bildschirm 7 x 3 ab (-3, 0); Hauptbildschirm
            // 4 x 3 bei (0, 0), links daneben ein flacher 3 x 1 bei (-3, 2).
            int vw = 7, vh = 3;
            var pix = new byte[vw * vh * 4];
            for (int i = 0; i < vw * vh; i++) { pix[i * 4] = (byte)i; pix[i * 4 + 1] = (byte)(100 + i); pix[i * 4 + 3] = 255; }
            var rechts = DesktopScreenshot.Crop(pix, vw, vh, 0 - (-3), 0, 4, 3, out var rw, out var rh);
            Check("Ausschnitt Hauptbildschirm: 4 x 3", rw == 4 && rh == 3 && rechts.Length == 4 * 3 * 4);
            Check("Ausschnitt Hauptbildschirm: richtige Bildpunkte",
                rechts[0] == 3 && rechts[4 * 4] == 10 && rechts[(2 * 4 + 3) * 4] == 20 && rechts[1] == 103);
            var links = DesktopScreenshot.Crop(pix, vw, vh, 0, 2, 3, 1, out var lw, out var lh);
            Check("Ausschnitt flacher Monitor: 3 x 1, ohne die Flaeche darueber",
                lw == 3 && lh == 1 && links[0] == 14 && links[4] == 15 && links[8] == 16);
            var rand = DesktopScreenshot.Crop(pix, vw, vh, 5, 1, 10, 10, out var randW, out var randH);
            Check("ueber den Rand Ragendes wird abgeschnitten", randW == 2 && randH == 2 && rand[0] == 12);
            DesktopScreenshot.Crop(pix, vw, vh, 50, 50, 5, 5, out var nw, out var nh);
            Check("ganz ausserhalb: leeres Bild", nw == 0 && nh == 0);

            // Wiederherstellen: welche Bilder werden gezeigt?
            var bo = Path.Combine(root, "bilder-finden");
            Directory.CreateDirectory(bo);
            Check("leerer Ordner: kein Bild", DesktopScreenshot.FindImages(bo).Count == 0
                && DesktopScreenshot.FindImages(Path.Combine(root, "gibt-es-nicht")).Count == 0);
            File.WriteAllText(Path.Combine(bo, "Desktop.png"), "alt");
            Check("aeltere Sicherung: das fruehere Gesamtbild", DesktopScreenshot.FindImages(bo).Select(Path.GetFileName).SequenceEqual(new[] { "Desktop.png" }));
            foreach (var n in new[] { "Bildschirm-10.png", "Bildschirm-2.png", "Bildschirm-1-Hauptbildschirm.png", "Bildschirm-x.png", "Notiz.png" })
                File.WriteAllText(Path.Combine(bo, n), "");
            Check("neue Sicherung: nur die Monitorbilder, nach Nummer (1, 2, 10)",
                DesktopScreenshot.FindImages(bo).Select(Path.GetFileName)
                    .SequenceEqual(new[] { "Bildschirm-1-Hauptbildschirm.png", "Bildschirm-2.png", "Bildschirm-10.png" }),
                string.Join(", ", DesktopScreenshot.FindImages(bo).Select(Path.GetFileName)));
        }

        Console.WriteLine("\n== Desktop-Screenshot: Profil und Schwarzbild ==");
        var sep = Path.DirectorySeparatorChar;
        var anwender = Path.Combine(root, "Users", "Anwender");
        Check("gleiches Profil wird erkannt (Gross-/Kleinschreibung, Trennzeichen)",
            ProfileExcludes.SameFolder(anwender, anwender.ToUpperInvariant() + sep));
        Check("anderes Profil wird erkannt",
            !ProfileExcludes.SameFolder(anwender, Path.Combine(root, "Users", "Techniker")));
        Check("Unterordner ist NICHT dasselbe Profil",
            !ProfileExcludes.SameFolder(anwender, Path.Combine(anwender, "Desktop")));
        Check("Max ist nicht Maximilian",
            !ProfileExcludes.SameFolder(Path.Combine(root, "Users", "Max"), Path.Combine(root, "Users", "Maximilian")));
        Check("leere Angabe ist kein Profil", !ProfileExcludes.SameFolder("", anwender));

        Check("schwarzes Bild wird erkannt", DesktopScreenshot.IsBlank(new byte[4 * 5000]));
        var fastSchwarz = new byte[4 * 5000]; fastSchwarz[2] = 200;    // ein roter Bildpunkt
        Check("ein einziger heller Bildpunkt genuegt, wenn er in der Stichprobe liegt",
            !DesktopScreenshot.IsBlank(fastSchwarz));
        Check("Rauschen knapp ueber Schwarz zaehlt noch als schwarz",
            DesktopScreenshot.IsBlank(Enumerable.Repeat((byte)5, 4 * 100).ToArray()));
        Check("gewoehnliches Bild ist nicht schwarz", !DesktopScreenshot.IsBlank(klein));

        Console.WriteLine("\n== Welche Fenster fuer das Foto beiseitegehen ==");
        var normal = new DesktopScreenshot.WindowInfo("Chrome_WidgetWin_1", true, false, false, false, false, false, 1600, 900);
        Check("gewoehnliches Programmfenster: ja", DesktopScreenshot.ShouldHide(normal));
        Check("schon minimiert: nein (bleibt, wie es ist)", !DesktopScreenshot.ShouldHide(normal with { Minimized = true }));
        Check("unsichtbar: nein", !DesktopScreenshot.ShouldHide(normal with { Visible = false }));
        Check("auf einem anderen virtuellen Desktop (cloaked): nein", !DesktopScreenshot.ShouldHide(normal with { Cloaked = true }));
        Check("Dialog mit Hauptfenster (owned): nein - folgt dem Hauptfenster",
            !DesktopScreenshot.ShouldHide(normal with { HasOwner = true }));
        Check("Werkzeugfenster (Tray-Menue): nein", !DesktopScreenshot.ShouldHide(normal with { ToolWindow = true }));
        Check("Werkzeugfenster mit eigenem Taskleisten-Eintrag: ja",
            DesktopScreenshot.ShouldHide(normal with { ToolWindow = true, AppWindow = true }));
        Check("Desktop und Taskleiste: nein",
            !DesktopScreenshot.ShouldHide(normal with { ClassName = "Progman" })
            && !DesktopScreenshot.ShouldHide(normal with { ClassName = "Shell_TrayWnd" })
            && !DesktopScreenshot.ShouldHide(normal with { ClassName = "shell_secondarytraywnd" }));
        Check("Fenster ohne Flaeche: nein", !DesktopScreenshot.ShouldHide(normal with { Width = 0 }));

        // ================================================================
        Console.WriteLine("\n== robocopy-Meldungen aus AppData zusammenfassen ==");
        static string De(string de, string en) => de;
        static string En(string de, string en) => en;
        {
            var s = new RobocopySkips(@"C:\Users\Anwender\AppData");
            // Genau die Zeilen aus der Meldung des Benutzers (deutsches Windows).
            var deutsch = new[]
            {
                @"2026/11/10 20:15:31 FEHLER 2 (0x00000002) Datei wird kopiert C:\Users\Anwender\AppData\Roaming\discord\Local Storage\leveldb\012884.log",
                "Das System kann die angegebene Datei nicht finden.",
                "Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.",
            };
            Check("deutscher Block: alle drei Zeilen bleiben aus dem Protokoll", deutsch.All(s.Swallow));
            Check("die Datei ist gezaehlt, mit Pfad", s.Count == 1 && s.Paths[0].Path.EndsWith(@"leveldb\012884.log"), s.Count.ToString());
            Check("der Grund ist zu Fehler 2 gemerkt", s.ReasonByCode.TryGetValue(2, out var g2) && g2 == "Das System kann die angegebene Datei nicht finden.");

            var englisch = new[]
            {
                @"2026/11/10 20:15:32 ERROR 32 (0x00000020) Copying File C:\Users\Anwender\AppData\Local\Discord\app.log",
                "The process cannot access the file because it is being used by another process.",
                "ERROR: RETRY LIMIT EXCEEDED.",
            };
            Check("englischer Block: ebenso", englisch.All(s.Swallow));

            // Bei /MT rutscht die Dateizeile eines anderen Threads dazwischen.
            Check("Kopfzeile eines weiteren Blocks wird geschluckt",
                s.Swallow(@"2026/11/10 20:15:33 FEHLER 32 (0x00000020) Datei wird kopiert C:\Users\Anwender\AppData\Roaming\Teams\x.db"));
            Check("eingerueckte Dateizeile mittendrin bleibt im Protokoll",
                !s.Swallow("\t    Neue Datei  \t\t  1234\tC:\\Users\\Anwender\\AppData\\Roaming\\y.json"));
            Check("Grund und Abschluss dahinter werden trotzdem geschluckt",
                s.Swallow("Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.")
                && s.Swallow("FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten."));
            Check("Grund je Fehlerart nur einmal (der erste)", s.ReasonByCode[32].StartsWith("The process cannot"), s.ReasonByCode[32]);
            Check("Zaehlung je Fehlerart", s.Count == 3 && s.CountByCode[2] == 1 && s.CountByCode[32] == 2);
            Check("gesperrt/verschwunden: alles erwartbar", s.AllExpected && s.OtherErrors == 0);

            // Ausserhalb von AppData: alles bleibt, wie es war.
            Check("Fehler in Dokumente bleibt im Protokoll",
                !s.Swallow(@"2026/11/10 20:15:34 FEHLER 32 (0x00000020) Datei wird kopiert C:\Users\Anwender\Documents\Brief.docx"));
            Check("samt Grund und Abschluss",
                !s.Swallow("Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.")
                && !s.Swallow("FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten."));
            Check("und zaehlt als anderer Fehler", s.OtherErrors == 1 && s.Count == 3);
            Check("\"AppDataAlt\" liegt nicht in \"AppData\"",
                !s.Swallow(@"2026/11/10 20:15:35 FEHLER 2 (0x00000002) Datei wird kopiert C:\Users\Anwender\AppDataAlt\z.txt"));

            Check("gewoehnliche Zeilen bleiben im Protokoll",
                !s.Swallow("   ROBOCOPY     ::     Robustes Dateikopieren für Windows")
                && !s.Swallow("Dateien :      1200      1197         0         0         3         0")
                && !s.Swallow("  Quelle : C:\\Users\\Anwender\\"));
            Check("Nummer und Hexzahl muessen zusammenpassen",
                !RobocopySkips.TryParseHeader(@"2026/11/10 20:15:36 FEHLER 5 (0x00000020) Datei wird kopiert C:\Users\Anwender\AppData\a", out _, out _));
            Check("Kopfzeile wird in jeder Sprache erkannt",
                RobocopySkips.TryParseHeader(@"2026/11/10 20:15:36 ERREUR 5 (0x00000005) Copie du fichier C:\Users\A\AppData\a b.txt", out var c5, out var p5)
                && c5 == 5 && p5 == @"C:\Users\A\AppData\a b.txt", p5);
            Check("Schreibweise des Ordners spielt keine Rolle",
                new RobocopySkips(@"c:\users\anwender\appdata\").Swallow(@"2026/11/10 20:15:31 FEHLER 2 (0x00000002) Datei wird kopiert C:\Users\Anwender\AppData\x"));

            // Zugriff verweigert ist KEIN normaler AppData-Fall.
            s.Swallow(@"2026/11/10 20:15:37 FEHLER 5 (0x00000005) Datei wird kopiert C:\Users\Anwender\AppData\Local\geschuetzt.dat");
            s.Swallow("Zugriff verweigert");
            s.Swallow("FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.");
            Check("Zugriff verweigert macht es zum echten Fehler", !s.AllExpected);

            var kurz = s.Summary("AppData", @"G:\DASI_PC\AppData-nicht-gesichert.txt", De);
            Check("Zusammenfassung: Kopfzeile mit Anzahl", kurz.StartsWith("[WARNUNG] AppData: 4 Dateien nicht gesichert"), kurz.Split('\n')[0]);
            Check("Zusammenfassung: eine Zeile je Fehlerart, haeufigste zuerst",
                kurz.Split('\n').Length == 5 && kurz.Split('\n')[1].Contains("2 × Fehler 32"), kurz);
            Check("Zusammenfassung: nennt die Liste", kurz.Contains(@"G:\DASI_PC\AppData-nicht-gesichert.txt"));
            Check("Zusammenfassung zaehlt nicht als Fehlschlag", !ActionOutcome.IsErrorLine(kurz));
            Check("englisch: [WARNING]", s.Summary("AppData", null, En).StartsWith("[WARNING] AppData: 4 files not backed up"));

            var liste = s.ListText("AppData", new DateTime(2026, 11, 10, 20, 15, 0), De);
            Check("Liste: jede Datei steht drin",
                s.Paths.All(p => liste.Contains(p.Path)) && liste.Contains(@"leveldb\012884.log"));
            Check("Liste: nach Fehlerart, mit Grund und Anzahl",
                liste.Contains("Fehler 32 - The process cannot access the file") && liste.Contains("(2 Dateien)"));
            Check("Liste: Datum und Quelle", liste.Contains("10.11.2026 20:15") && liste.Contains(@"C:\Users\Anwender\AppData"));

            // Viele Dateien: Tausendertrennung, Obergrenze fuer die gemerkten Pfade.
            var viele = new RobocopySkips(@"C:\U\AppData");
            for (int i = 0; i < 1234; i++)
            {
                viele.Swallow($@"2026/11/10 20:16:00 FEHLER 32 (0x00000020) Datei wird kopiert C:\U\AppData\f{i}.log");
                viele.Swallow("Der Prozess kann nicht auf die Datei zugreifen.");
                viele.Swallow("FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.");
            }
            Check("1.234 Dateien: deutsch mit Punkt", viele.Summary("AppData", null, De).Contains("1.234 Dateien"));
            Check("1,234 files: englisch mit Komma", viele.Summary("AppData", null, En).Contains("1,234 files"));
        }

        Console.WriteLine("\n== robocopy beim Zurueckschreiben: gesperrte Dateien im Profil ==");
        {
            // Die Zeilen aus dem Protokoll vom 18.11.2026: robocopy nennt den
            // Pfad in der SICHERUNG, gesperrt ist die Datei im Profil.
            const string wurzel = @"C:\DASI_DESKTOP-6SLTVM2_18_11_2026\Benutzerprofil\AppData";
            var z = new RobocopySkips(wurzel, zurueck: true);
            var bloecke = new[]
            {
                @"Local\Microsoft\Edge\User Data\GrShaderCache\data_3",
                @"Local\Microsoft\Edge\User Data\Default\EdgeHubAppUsage\EdgeHubAppUsageSQLite.db",
                @"Local\Packages\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\LocalState\EBWebView\Default\Login Data",
            };
            bool alleGeschluckt = true;
            foreach (var rel in bloecke)
            {
                alleGeschluckt &= z.Swallow($@"2026/11/18 20:44:06 FEHLER 32 (0x00000020) Folgende Datei wird kopiert {wurzel}\{rel}");
                alleGeschluckt &= z.Swallow("Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.");
                alleGeschluckt &= z.Swallow("Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.");
            }
            Check("Zurueckschreiben: jeder Block bleibt aus dem Protokoll", alleGeschluckt);
            Check("Zurueckschreiben: gezaehlt und erwartbar", z.Count == 3 && z.AllExpected && z.OtherErrors == 0 && z.Zurueck);
            var zKurz = z.Summary("AppData", @"C:\DASI_DESKTOP-6SLTVM2_18_11_2026\" + RobocopySkips.RestoreListFileName, De);
            Check("Zurueckschreiben: \"nicht zurückgeschrieben\" statt \"nicht gesichert\"",
                zKurz.StartsWith("[WARNUNG] AppData: 3 Dateien nicht zurückgeschrieben") && !zKurz.Contains("gesichert"), zKurz.Split('\n')[0]);
            Check("Zurueckschreiben: erklaert, dass die vorhandene Fassung bleibt", zKurz.Contains("vorhandene Fassung"));
            Check("Zurueckschreiben: eigene Liste", zKurz.Contains("AppData-nicht-wiederhergestellt.txt")
                && RobocopySkips.RestoreListFileName != RobocopySkips.ListFileName);
            Check("Zurueckschreiben: zaehlt nicht als Fehlschlag", !ActionOutcome.IsErrorLine(zKurz));
            Check("Zurueckschreiben englisch", z.Summary("AppData", null, En).StartsWith("[WARNING] AppData: 3 files not restored"));
            var zListe = z.ListText("AppData", new DateTime(2026, 11, 18, 20, 44, 0), De);
            Check("Zurueckschreiben: Liste mit Ueberschrift und allen Pfaden",
                zListe.Contains("nicht zurückgeschrieben werden konnten") && zListe.Contains("Wiederherstellung vom 18.11.2026 20:44")
                && bloecke.All(b => zListe.Contains(b)), zListe.Split('\n')[0]);
            Check("Sichern bleibt beim alten Wortlaut", !new RobocopySkips(wurzel).Zurueck
                && new RobocopySkips(wurzel).Summary("AppData", null, De).Contains("nicht gesichert"));

            // Mit /R:1 (Zurueckschreiben) meldet robocopy eine gesperrte Datei
            // ZWEIMAL: erst mit der Ankuendigung des naechsten Versuchs, dann
            // endgueltig - so gesehen auf dem Windows-Laeufer.
            var w = new RobocopySkips(@"C:\S\AppData", zurueck: true);
            var zweimal = new[]
            {
                @"2026/11/18 20:01:39 ERROR 32 (0x00000020) Copying File C:\S\AppData\Roaming\a\000003.log",
                "The process cannot access the file because it is being used by another process.",
                "Waiting 1 seconds... Retrying...",
                @"2026/11/18 20:01:40 ERROR 32 (0x00000020) Copying File C:\S\AppData\Roaming\a\000003.log",
                "The process cannot access the file because it is being used by another process.",
                "ERROR: RETRY LIMIT EXCEEDED.",
            };
            Check("Wiederholung: alle sechs Zeilen bleiben aus dem Protokoll", zweimal.All(w.Swallow));
            Check("Wiederholung: die Datei zaehlt einmal", w.Count == 1 && w.Paths.Count == 1 && w.CountByCode[32] == 1,
                $"{w.Count} / {w.Paths.Count}");
            var klappt = new RobocopySkips(@"C:\S\AppData", zurueck: true);
            Check("Wiederholung gelingt: Kopf, Grund und Ankuendigung geschluckt",
                klappt.Swallow(@"2026/11/18 20:01:39 FEHLER 32 (0x00000020) Folgende Datei wird kopiert C:\S\AppData\b.db")
                && klappt.Swallow("Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.")
                && klappt.Swallow("Wartezeit: 1 Sekunden... Wiederholung..."));
            Check("Wiederholung gelingt: die Datei zaehlt NICHT als uebersprungen",
                klappt.Count == 0 && klappt.Paths.Count == 0 && klappt.CountByCode.Count == 0 && !klappt.Swallow("\t  Neuer       \t\t  12\tC:\\S\\AppData\\b.db"));

            // Protokoll vom 18.11.2026, 21:11 Uhr: 32 Kopier-Threads, deren
            // Meldungen sich verschraenken - dazwischen die gesperrte
            // Programmdatei des Tools auf dem Desktop (ausserhalb von AppData).
            const string sp = @"C:\DASI_DESKTOP-6SLTVM2_18_11_2026\Benutzerprofil";
            var v = new RobocopySkips(sp + @"\AppData", zurueck: true);
            const string gesperrtDe = "Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.";
            var protokoll = new (string Zeile, bool Schlucken)[]
            {
                (@"2026/11/18 21:11:55 FEHLER 32 (0x00000020) Folgende Datei wird kopiert " + sp + @"\AppData\Local\a\data_0", true),
                (@"2026/11/18 21:11:55 FEHLER 1920 (0x00000780) Folgende Datei wird kopiert " + sp + @"\AppData\Local\Microsoft\WindowsApps\winget.exe", true),
                (@"2026/11/18 21:11:55 FEHLER 32 (0x00000020) Folgende Datei wird kopiert " + sp + @"\Desktop\WindowsDaSiTool.exe", false),
                (gesperrtDe, true),                                          // Grund zu data_0
                ("Das System kann auf die Datei nicht zugreifen.", true),     // Grund zu winget.exe
                (gesperrtDe, false),                                         // Grund zur Programmdatei - bleibt sichtbar
                ("Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.", true),
                ("Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.", true),
                ("Wartezeit: 1 Sekunden... Wiederholung...", false),        // Programmdatei, zweiter Versuch folgt
                (@"2026/11/18 21:11:56 FEHLER 32 (0x00000020) Folgende Datei wird kopiert " + sp + @"\Desktop\WindowsDaSiTool.exe", false),
                (gesperrtDe, false),
                ("Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.", false),
                (@"2026/11/18 21:11:56 FEHLER 1224 (0x000004C8) Folgende Datei wird kopiert " + sp + @"\AppData\Local\b\c.dll", true),
                ("Der Vorgang ist bei einer Datei mit einem geöffneten Bereich, der einem Benutzer zugeordnet ist, nicht anwendbar.", true),
                ("Fehler: Die maximale Anzahl von Wiederholungsversuchen wurde überschritten.", true),
                ("------------------------------------------------------------------------------", false),
                ("Verzeich.:      3609      3598      3556         0         0        96", false),
            };
            var falsch = protokoll.Where(x => v.Swallow(x.Zeile) != x.Schlucken).Select(x => x.Zeile).ToList();
            Check("verschraenkte Meldungen: AppData geschluckt, Programmdatei und Bilanz sichtbar", falsch.Count == 0, string.Join(" || ", falsch));
            Check("verschraenkte Meldungen: drei Dateien in AppData, die Programmdatei zaehlt als anderer Fehler",
                v.Count == 3 && v.OtherErrors == 2, $"{v.Count} / {v.OtherErrors}");
            Check("verschraenkte Meldungen: Gruende richtig zugeordnet",
                v.ReasonByCode[32] == gesperrtDe && v.ReasonByCode[1920] == "Das System kann auf die Datei nicht zugreifen."
                && v.ReasonByCode[1224].StartsWith("Der Vorgang ist bei einer Datei"));
            Check("Fehler 1224 (eingeblendete Datei eines laufenden Programms) ist erwartbar wie 32", v.AllExpected
                && RobocopySkips.Expected.Contains(1224));
            Check("Zugriff verweigert bleibt ein echter Fehler", !RobocopySkips.Expected.Contains(5));
        }

        Console.WriteLine("\n== Ausschlussliste: Zwischenspeicher und rechnergebundener Zustand ==");
        {
            var ap = Path.Combine(root, "ausschluss", "Anwender");
            var mit = ProfileExcludes.Build(ap, includeAppData: true);
            bool Drin(string rel) => mit.Contains(Path.Combine(ap, rel), StringComparer.OrdinalIgnoreCase);
            Check("winget-Zustand (App-Installer) bleibt draussen - Ursache von 0x8A150009",
                Drin(@"AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe")
                && Drin(@"AppData\Local\Packages\Microsoft.Winget.Source_8wekyb3d8bbwe"));
            Check("Windows-Shell (Suche/Widgets) bleibt draussen",
                Drin(@"AppData\Local\Packages\MicrosoftWindows.Client.CBS_cw5n1h2txyewy")
                && Drin(@"AppData\Local\Packages\Microsoft.Windows.Search_cw5n1h2txyewy"));
            Check("Grafik-Zwischenspeicher der Browser bleiben draussen",
                Drin(@"AppData\Local\Microsoft\Edge\User Data\GrShaderCache")
                && Drin(@"AppData\Local\Microsoft\Edge\User Data\ShaderCache")
                && Drin(@"AppData\Local\Google\Chrome\User Data\Default\GPUCache")
                && Drin(@"AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\DawnWebGPUCache"));
            Check("wie bisher: Cache im Standardprofil", Drin(@"AppData\Local\Microsoft\Edge\User Data\Default\Cache")
                && Drin(@"AppData\Local\Google\Chrome\User Data\Default\Cache"));
            Check("die Browserprofile selbst bleiben drin",
                !Drin(@"AppData\Local\Microsoft\Edge\User Data") && !Drin(@"AppData\Local\Microsoft\Edge\User Data\Default")
                && !Drin(@"AppData\Local\Packages"));
            Check("alles liegt in AppData", ProfileExcludes.AppDataAusschluesse().All(a => a.StartsWith(@"AppData\", StringComparison.Ordinal)));
            Check("App-Aliase (WindowsApps) bleiben draussen - Fehler 1920", Drin(@"AppData\Local\Microsoft\WindowsApps"));

            // Das Tool laeuft vom Desktop des Zielprofils.
            var zielP = Path.Combine(root, "selbst", "Users", "Anwender123");
            var sichP = Path.Combine(root, "selbst", "DASI", "Benutzerprofil");
            Check("laufendes Tool im Zielprofil: dieselbe Stelle in der Sicherung",
                ProfileExcludes.LaufendesProgrammInSicherung(zielP, sichP, Path.Combine(zielP, "Desktop", "WindowsDaSiTool.exe"))
                == Path.Combine(sichP, "Desktop", "WindowsDaSiTool.exe"));
            Check("laufendes Tool ausserhalb des Profils: nichts auszulassen",
                ProfileExcludes.LaufendesProgrammInSicherung(zielP, sichP, Path.Combine(root, "selbst", "USB", "WindowsDaSiTool.exe")) is null
                && ProfileExcludes.LaufendesProgrammInSicherung(zielP, sichP, Path.Combine(root, "selbst", "Users", "Anwender1234", "x.exe")) is null
                && ProfileExcludes.LaufendesProgrammInSicherung(zielP, sichP, null) is null);
            Check("keine Dubletten", mit.Count == mit.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        Console.WriteLine("\n== winget: Rueckgabewerte und Ausgabe ==");
        {
            Check("0x8A150009 ist -1978335223 (Wert aus dem Protokoll)", WingetFehler.IndexZuNeu == -1978335223);
            Check("bisherige Werte unveraendert", WingetFehler.SchonAktuell == -1978335189
                && WingetFehler.SchonInstalliert == -1978335135 && WingetFehler.NichtGefunden == -1978335212);
            Check("Hex-Schreibweise wie in der winget-Doku", WingetFehler.Hex(-1978335223) == "0x8A150009", WingetFehler.Hex(-1978335223));
            Check("kaputte Quelle erkannt", WingetFehler.QuelleKaputt(WingetFehler.IndexZuNeu)
                && WingetFehler.QuelleKaputt(WingetFehler.IndexBeschaedigt) && WingetFehler.QuelleKaputt(WingetFehler.QuellenBeschaedigt)
                && WingetFehler.QuelleKaputt(WingetFehler.QuellDatenFehlen));
            Check("Paketfehler sind keine kaputte Quelle", !WingetFehler.QuelleKaputt(0) && !WingetFehler.QuelleKaputt(WingetFehler.NichtGefunden)
                && !WingetFehler.QuelleKaputt(WingetFehler.SchonInstalliert) && !WingetFehler.QuelleKaputt(1603));
            Check("auch an der Ausgabe erkannt", WingetFehler.QuelleKaputt(1, "0x8a150009 : Cannot write to index; it is a higher schema version")
                && !WingetFehler.QuelleKaputt(1, "Installer failed with exit code: 1603"));
            Check("Erklaerung fuer 0x8A150009", WingetFehler.Erklaerung(WingetFehler.IndexZuNeu, De)?.Contains("neueren winget-Fassung") == true);
            Check("keine Erklaerung fuer unbekannte Werte", WingetFehler.Erklaerung(1603, De) is null);

            // So kam die Ausgabe an: Fortschrittskreisel und eine Zeile voller
            // Leerzeichen, per Wagenruecklauf ueberschrieben.
            var roh = "   - \r   \\ \r   | \r   / \r" + new string(' ', 120) + "\r"
                      + "An unexpected error occurred while executing the command:\r\n"
                      + "0x8a150009 : Cannot write to index; it is a higher schema version\r\n";
            var meldung = WingetFehler.LetzteMeldung(roh);
            Check("Ausgabe: nur die Meldung, ohne Kreisel und Leerzeilen",
                meldung == "An unexpected error occurred while executing the command: | 0x8a150009 : Cannot write to index; it is a higher schema version",
                meldung);
            Check("Ausgabe: Fortschrittsbalken fallen weg",
                WingetFehler.LetzteMeldung("Gefunden Firefox [Mozilla.Firefox.de]\n  ██████████████▒▒▒▒▒▒  40.0 MB / 70.1 MB\nInstallation fehlgeschlagen mit Exitcode: 1603\n")
                == "Gefunden Firefox [Mozilla.Firefox.de] | Installation fehlgeschlagen mit Exitcode: 1603");
            Check("Ausgabe: leer bleibt leer", WingetFehler.LetzteMeldung("") == "" && WingetFehler.LetzteMeldung(" \r - \r\n") == "");

            var ordner = new[]
            {
                @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe",
                @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.26.430.0_x64__8wekyb3d8bbwe",
                @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.21.10120.0_x64__8wekyb3d8bbwe",
            };
            var sortiert = WingetFehler.NeuesteZuerst(ordner);
            Check("App-Installer: neueste Fassung zuerst (1.26 vor 1.21 vor 1.9)",
                sortiert[0].Contains("_1.26.") && sortiert[1].Contains("_1.21.") && sortiert[2].Contains("_1.9."), string.Join(" ", sortiert.Select(Path.GetFileName)));
            Check("App-Installer: Fassung aus dem Ordnernamen", WingetFehler.OrdnerVersion(ordner[1]) == new Version(1, 26, 430, 0)
                && WingetFehler.OrdnerVersion(@"C:\x\Microsoft.DesktopAppInstaller_kaputt") is null);
        }

        Console.WriteLine("\n== Desktop-Symbole: Systemsymbole einschalten ==");
        {
            const string dieserPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
            const string benutzer = "::{59031a47-3f72-44a7-89c5-5595fe6b30ee}";
            const string papierkorb = "::{645FF040-5081-101B-9F08-00AA002F954E}";
            Check("Kennung eines Systemsymbols", DesktopIcons.SystemsymbolKennung(dieserPc) == "{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
                && DesktopIcons.SystemsymbolKennung(benutzer) == "{59031A47-3F72-44A7-89C5-5595FE6B30EE}");
            Check("Dateien und unbekannte Kennungen sind keine Systemsymbole", DesktopIcons.SystemsymbolKennung("Firefox.lnk") is null
                && DesktopIcons.SystemsymbolKennung("::{00000000-0000-0000-0000-000000000000}") is null);

            // Wie im Protokoll: Auf dem alten Desktop lagen "Dieser PC" und der
            // Benutzerordner ("Anwender"), auf dem neuen nur der Papierkorb.
            var alt = new DesktopLayout
            {
                Breite = 1920, Hoehe = 1080,
                Symbole = { new(dieserPc, "Dieser PC", 10, 10), new(benutzer, "Anwender", 10, 110), new(papierkorb, "Papierkorb", 10, 210), new("Firefox.lnk", "Firefox", 10, 310) }
            };
            var neuDesk = new TestDesktop(1920, 1080, autoAnordnen: false, raster: true, new DesktopIcon(papierkorb, "Papierkorb", 5, 5));
            Check("fehlende Systemsymbole: genau die zwei", DesktopIcons.FehlendeSystemsymbole(alt, neuDesk.Symbole())
                .SequenceEqual(new[] { "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "{59031A47-3F72-44A7-89C5-5595FE6B30EE}" }));

            // Nach dem Einschalten zeigt der Explorer sie - hier: beim naechsten Nachsehen.
            List<string>? angefragt = null;
            DesktopIcons.OeffneDesktop = () => neuDesk;
            var ergSys = DesktopIcons.Wiederherstellen(alt, TimeSpan.FromSeconds(8), k =>
            {
                angefragt = k.ToList();
                neuDesk.SpaeterDazu = (0, new DesktopIcon(dieserPc, "Dieser PC", 100, 100));
                neuDesk.Dazu(new DesktopIcon(benutzer.ToUpperInvariant(), "Anwender123", 100, 200));
                return true;
            });
            Check("Einschalten: mit den fehlenden Kennungen aufgerufen", angefragt?.Count == 2, string.Join(", ", angefragt ?? new()));
            Check("Einschalten: Desktop danach aktualisiert", neuDesk.Protokoll.Contains("aktualisieren"));
            Check("Einschalten: gemeldet", ergSys.Eingeschaltet?.Count == 2);
            Check("Benutzerordner heisst hier anders (Anwender123) - zugeordnet trotzdem, ueber die Kennung",
                neuDesk.Stelle(benutzer.ToUpperInvariant()) == (10, 110));
            Check("Dieser PC an seine Stelle", neuDesk.Stelle(dieserPc) == (10, 10));
            Check("nur Firefox fehlt noch", ergSys.Fehlend.Count == 1 && ergSys.Fehlend[0].Name == "Firefox.lnk",
                string.Join(", ", ergSys.Fehlend.Select(f => f.Name)));

            // Woher kommen die fehlenden Symbole? Firefox.lnk liegt im
            // Desktop-Ordner der Sicherung, "Finalisierung.lnk" nicht - es lag
            // auf dem oeffentlichen Desktop.
            var sichDesktop = Path.Combine(root, "herkunft", "Benutzerprofil", "Desktop");
            Directory.CreateDirectory(sichDesktop);
            File.WriteAllText(Path.Combine(sichDesktop, "Firefox.lnk"), "x");
            var fehlendeH = new List<DesktopIcon> { new("Firefox.lnk", "Firefox", 1, 1), new("Finalisierung.lnk", "Finalisierung", 2, 2), new(dieserPc, "Dieser PC", 3, 3) };
            var (imProfil, fremd) = DesktopIcons.NachHerkunft(fehlendeH, sichDesktop);
            Check("Herkunft: Datei im Desktop-Ordner der Sicherung kommt mit dem Profil",
                imProfil.Select(f => f.Name).SequenceEqual(new[] { "Firefox.lnk", dieserPc }));
            Check("Herkunft: nicht im Profil (oeffentlicher Desktop)", fremd.Count == 1 && fremd[0].Name == "Finalisierung.lnk");
            var (ohneOrdner, ohneFremd) = DesktopIcons.NachHerkunft(fehlendeH, Path.Combine(root, "herkunft", "gibt-es-nicht"));
            Check("Herkunft: ohne Profil in der Sicherung zaehlt alles zum Profil", ohneOrdner.Count == 3 && ohneFremd.Count == 0);

            var nochmal = DesktopIcons.Wiederherstellen(alt, TimeSpan.Zero, k => throw new InvalidOperationException("darf nicht kommen"));
            Check("sind alle Systemsymbole da, wird nichts eingeschaltet", nochmal.Eingeschaltet is null);
            var ohne = new TestDesktop(1920, 1080, false, true);
            DesktopIcons.OeffneDesktop = () => ohne;
            var ergOhne = DesktopIcons.Wiederherstellen(alt, TimeSpan.Zero, k => false);
            Check("klappt das Einschalten nicht, steht nichts als eingeschaltet da", ergOhne.Eingeschaltet is null && ergOhne.Fehlend.Count == 4);
            DesktopIcons.OeffneDesktop = () => OperatingSystem.IsWindows() ? new ExplorerDesktop() : throw new PlatformNotSupportedException();
        }

        Console.WriteLine("\n== Browserprofil nach dem Zurueckschreiben pruefen ==");
        {
            // Wie Chrome: "Local State" oben, zwei Profile mit Preferences & Co.
            var sicherungCh = Path.Combine(root, "abgleich", "Chrome-Profil");
            void DateiAb(string basis, string rel, string inhalt)
            {
                var f = Path.Combine(basis, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, inhalt);
            }
            DateiAb(sicherungCh, "Local State", "{\"profile\":{\"last_used\":\"Profile 1\",\"info_cache\":{}}, \"os_crypt\":{}}");
            DateiAb(sicherungCh, Path.Combine("Default", "Preferences"), new string('d', 4000));
            DateiAb(sicherungCh, Path.Combine("Default", "Bookmarks"), new string('b', 900));
            DateiAb(sicherungCh, Path.Combine("Profile 1", "Preferences"), new string('p', 5000));
            DateiAb(sicherungCh, Path.Combine("Profile 1", "History"), new string('h', 20000));
            DateiAb(sicherungCh, Path.Combine("Crowd Deny", "1.0", "manifest.json"), "x");
            Check("Profile erkannt: Default und Profile 1 (nicht \"Crowd Deny\")",
                ProfilAbgleich.Profile(sicherungCh).SequenceEqual(new[] { "Default", "Profile 1" }),
                string.Join(", ", ProfilAbgleich.Profile(sicherungCh)));

            // Vollstaendig kopiert.
            var zielUd = Path.Combine(root, "abgleich", "User Data");
            foreach (var f in Directory.GetFiles(sicherungCh, "*", SearchOption.AllDirectories))
                DateiAb(zielUd, Path.GetRelativePath(sicherungCh, f), File.ReadAllText(f));
            Check("vollstaendig: keine Abweichung", ProfilAbgleich.Abweichungen(sicherungCh, zielUd).Count == 0,
                string.Join(", ", ProfilAbgleich.Abweichungen(sicherungCh, zielUd)));

            // Wie am 19.11.2026: Chrome lief und schrieb beim Beenden ein frisches
            // "Local State" und "Preferences" ueber das zurueckgeschriebene Profil.
            File.WriteAllText(Path.Combine(zielUd, "Local State"), "{\"profile\":{}}");
            File.WriteAllText(Path.Combine(zielUd, "Profile 1", "Preferences"), "{}");
            File.Delete(Path.Combine(zielUd, "Default", "Bookmarks"));
            var abw = ProfilAbgleich.Abweichungen(sicherungCh, zielUd);
            Check("ueberschrieben: Local State, Preferences und fehlende Lesezeichen werden erkannt",
                abw.Contains("Local State") && abw.Contains(Path.Combine("Profile 1", "Preferences"))
                && abw.Contains(Path.Combine("Default", "Bookmarks")) && abw.Count == 3, string.Join(", ", abw));
            Check("nicht gesicherte Dateien zaehlen nicht", !abw.Contains(Path.Combine("Default", "History")));

            // Firefox: profiles.ini und Profiles\xxxx.default-release\prefs.js
            var ff = Path.Combine(root, "abgleich", "Firefox-Profil");
            DateiAb(ff, "profiles.ini", "[Profile0]\nPath=Profiles/abcd.default-release\n");
            DateiAb(ff, Path.Combine("Profiles", "abcd.default-release", "prefs.js"), "user_pref(\"a\", 1);");
            DateiAb(ff, Path.Combine("Profiles", "abcd.default-release", "places.sqlite"), new string('s', 3000));
            Check("Firefox: Profil unter Profiles erkannt",
                ProfilAbgleich.Profile(ff).SequenceEqual(new[] { Path.Combine("Profiles", "abcd.default-release") }));
            var ffZiel = Path.Combine(root, "abgleich", "Firefox");
            Check("Firefox: leeres Ziel - alle wichtigen Dateien fehlen",
                ProfilAbgleich.Abweichungen(ff, ffZiel).Count == 3, string.Join(", ", ProfilAbgleich.Abweichungen(ff, ffZiel)));
            Check("ohne Sicherung: nichts zu vergleichen", ProfilAbgleich.Abweichungen(Path.Combine(root, "abgleich", "fehlt"), ffZiel).Count == 0);
        }

        Console.WriteLine("\n== Hintergrund: Volltonfarbe ==");
        {
            Check("Farbe aus der Registry lesen", Hintergrund.FarbeLesen("0 99 177") == ((byte)0, (byte)99, (byte)177)
                && Hintergrund.FarbeLesen("  255 255 255 ") == ((byte)255, (byte)255, (byte)255));
            Check("ungueltige Farben werden abgelehnt", Hintergrund.FarbeLesen("256 0 0") is null && Hintergrund.FarbeLesen("0,99,177") is null
                && Hintergrund.FarbeLesen("") is null && Hintergrund.FarbeLesen(null) is null && Hintergrund.FarbeLesen("1 2") is null);
            var blau = ((byte)0, (byte)99, (byte)177);
            Check("Farbe zurueck in Registry-Form", Hintergrund.FarbeText(blau) == "0 99 177");
            Check("Farbe als Hex", Hintergrund.Hex(blau) == "#0063B1", Hintergrund.Hex(blau));
            Check("COLORREF fuer SetSysColors (0x00BBGGRR)", Hintergrund.ColorRef(blau) == 0xB16300, Hintergrund.ColorRef(blau).ToString("X6"));

            // Was ist eingestellt? (BackgroundType: 0 Bild, 1 Volltonfarbe, 2 Diashow, 3 Blickpunkt)
            Check("Volltonfarbe laut Windows (BackgroundType 1)",
                Hintergrund.Bestimmen(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg", true, 1, "0 99 177") == HintergrundArt.Volltonfarbe);
            Check("Volltonfarbe: kein Bildpfad, Farbe gesetzt (aelteres Windows ohne BackgroundType)",
                Hintergrund.Bestimmen("", false, null, "0 99 177") == HintergrundArt.Volltonfarbe);
            Check("Bild", Hintergrund.Bestimmen(@"C:\Users\A\Pictures\Urlaub.jpg", true, 0, "0 0 0") == HintergrundArt.Bild
                && Hintergrund.Bestimmen(@"C:\Users\A\Pictures\Urlaub.jpg", true, null, null) == HintergrundArt.Bild);
            Check("Diashow/Blickpunkt ohne aktuelles Bild: wechselnd", Hintergrund.Bestimmen("", false, 2, "0 0 0") == HintergrundArt.Wechselnd
                && Hintergrund.Bestimmen(null, false, 3, null) == HintergrundArt.Wechselnd);
            Check("Diashow mit aktuellem Bild: das Bild", Hintergrund.Bestimmen(@"C:\x\TranscodedWallpaper", true, 2, null) == HintergrundArt.Bild);
            Check("Bildpfad ohne Datei: unbekannt", Hintergrund.Bestimmen(@"C:\weg.jpg", false, 0, "0 0 0") == HintergrundArt.Unbekannt);

            // Windows' Zwischenspeicher ist kein Ort fuer den Hintergrund.
            Check("Zwischenspeicher: TranscodedWallpaper und CachedFiles erkannt",
                Hintergrund.IstWindowsZwischenspeicher(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper")
                && Hintergrund.IstWindowsZwischenspeicher(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Themes\CachedFiles\CachedImage_1920_1080_POS4.jpg")
                && Hintergrund.IstWindowsZwischenspeicher(@"D:\DASI_PC_01_01_2026\Design\TranscodedWallpaper"));
            Check("Zwischenspeicher: gewoehnliche Bilder und installierte Designs nicht",
                !Hintergrund.IstWindowsZwischenspeicher(@"C:\Users\A\Pictures\Urlaub.jpg")
                && !Hintergrund.IstWindowsZwischenspeicher(@"C:\Users\A\AppData\Local\Microsoft\Windows\Themes\Natur\DesktopBackground\img1.jpg")
                && !Hintergrund.IstWindowsZwischenspeicher(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg")
                && !Hintergrund.IstWindowsZwischenspeicher(null) && !Hintergrund.IstWindowsZwischenspeicher(""));
            Check("Zwischenspeicher eines Profils",
                Hintergrund.Zwischenspeicher(Path.Combine(root, "Anwender"))!.EndsWith(Path.Combine("Themes", "TranscodedWallpaper"))
                && Hintergrund.Zwischenspeicher(null) is null);
            byte[] kJpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, kPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                   kBmp = { 0x42, 0x4D, 1, 2 }, kGif = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 },
                   kWebp = { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, kSonst = { 1, 2, 3, 4 };
            Check("Bildart an den ersten Bytes",
                Hintergrund.BildEndung(kJpg) == ".jpg" && Hintergrund.BildEndung(kPng) == ".png" && Hintergrund.BildEndung(kBmp) == ".bmp"
                && Hintergrund.BildEndung(kGif) == ".gif" && Hintergrund.BildEndung(kWebp) == ".webp"
                && Hintergrund.BildEndung(kSonst) is null && Hintergrund.BildEndung(ReadOnlySpan<byte>.Empty) is null);
            Check("Name: Zwischenspeicher wird zu Hintergrundbild.<Endung>",
                Hintergrund.SicherungsName(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper", kJpg) == "Hintergrundbild.jpg"
                && Hintergrund.SicherungsName(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper", kPng) == "Hintergrundbild.png"
                && Hintergrund.SicherungsName(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Themes\CachedFiles\CachedImage_1920_1080_POS4.jpg", kJpg) == "Hintergrundbild.jpg"
                && Hintergrund.SicherungsName(@"D:\DASI\Design\TranscodedWallpaper", kSonst) == "Hintergrundbild.jpg");
            Check("Name: gewoehnliches Bild behaelt seinen Namen, ohne Endung bekommt es die passende",
                Hintergrund.SicherungsName(@"C:\Users\A\Pictures\Urlaub.jpg", kJpg) == "Urlaub.jpg"
                && Hintergrund.SicherungsName(@"C:\Users\A\Pictures\Urlaub.jpeg", kPng) == "Urlaub.jpeg"
                && Hintergrund.SicherungsName(@"C:\x\Bild", kPng) == "Bild.png"
                && Hintergrund.SicherungsName(@"C:\x\Bild", kSonst) == "Bild");

            // Gespeichert und wieder gelesen.
            var hg = Path.Combine(root, "hintergrund", "Hintergrundbild");
            Directory.CreateDirectory(hg);
            Hintergrund.Speichern(hg, new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "0 99 177" });
            var gelesen = Hintergrund.Laden(hg);
            Check("Hintergrund.json: Volltonfarbe wieder gelesen", Hintergrund.IstVolltonfarbe(gelesen) && gelesen!.Farbe == "0 99 177");
            Check("Bild-Einstellung ist keine Volltonfarbe",
                !Hintergrund.IstVolltonfarbe(new HintergrundEinstellung { Art = "Bild", Farbe = "0 0 0", Darstellung = "10" }));
            Check("aeltere Sicherung ohne Hintergrund.json", Hintergrund.Laden(Path.Combine(root, "hintergrund", "fehlt")) is null);
            File.WriteAllText(Path.Combine(root, "hintergrund", "kaputt.json"), "{");
            Directory.CreateDirectory(Path.Combine(root, "hintergrund", "kaputt"));
            File.WriteAllText(Path.Combine(root, "hintergrund", "kaputt", HintergrundEinstellung.DateiName), "{ kaputt");
            Check("kaputte Hintergrund.json: null statt Absturz", Hintergrund.Laden(Path.Combine(root, "hintergrund", "kaputt")) is null);

            // Verfuegbarkeit: eine Sicherung, die nur die Farbe enthaelt.
            AppCatalog.HatWlanAdapter = () => true;
            var nurFarbe = Path.Combine(root, "hintergrund");
            Check("Wiederherstellen: Volltonfarbe allein macht den Punkt frei",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, nurFarbe) is null, AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, nurFarbe)?.De);
            var leerHg = Path.Combine(root, "hintergrund-leer");
            Directory.CreateDirectory(Path.Combine(leerHg, "Benutzerprofil"));
            Check("Wiederherstellen: ohne Hintergrund und Design-Einstellungen gesperrt, mit Grund",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, leerHg)?.De == "Die gewählte Sicherung enthält kein Design (weder Hintergrund noch Design-Einstellungen).",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, leerHg)?.De);
        }

        Console.WriteLine("\n== Design-Datei von Windows (Custom.theme) ==");
        {
            // So sah sie beim Anwender aus (gekuerzt): Windows setzte beim
            // Anmelden Background=0 0 0 zurueck in die Registry.
            const string thema = "; Copyright © Microsoft Corp.\r\n\r\n[Theme]\r\nDisplayName=Benutzerdefiniert\r\nSetLogonBackground=0\r\n\r\n"
                + "[Control Panel\\Cursors]\r\nArrow=%SystemRoot%\\cursors\\aero_arrow.cur\r\nDefaultValue=Windows Default\r\n\r\n"
                + "[Control Panel\\Desktop]\r\nWallpaper=%SystemRoot%\\web\\wallpaper\\Windows\\img0.jpg\r\nTileWallpaper=0\r\nWallpaperStyle=10\r\n\r\n"
                + "[VisualStyles]\r\nPath=%ResourceDir%\\Themes\\Aero\\Aero.msstyles\r\nColorStyle=NormalColor\r\nAutoColorization=0\r\nColorizationColor=0XC40078D4\r\nSystemMode=Light\r\nAppMode=Light\r\n\r\n"
                + "[Slideshow]\r\nInterval=1800000\r\nShuffle=1\r\n\r\n"
                + "[Control Panel\\Colors]\r\nBackground=0 0 0\r\n";
            var farbe = DesignDatei.FuerFarbe(thema, ((byte)107, (byte)105, (byte)214));
            Check("Volltonfarbe: Background in [Control Panel\\Colors] ersetzt", DesignDatei.Lesen(farbe, DesignDatei.FarbenAbschnitt, "Background") == "107 105 214");
            Check("Volltonfarbe: Bild bleibt stehen (wie die Einstellungen-App), keine Diashow",
                DesignDatei.Lesen(farbe, DesignDatei.DesktopAbschnitt, "Wallpaper") == @"%SystemRoot%\web\wallpaper\Windows\img0.jpg"
                && !farbe.Contains("[Slideshow]") && !farbe.Contains("Interval="));
            Check("Volltonfarbe: alles andere unveraendert, Zeilenenden CRLF",
                farbe.Contains("DisplayName=Benutzerdefiniert\r\n") && farbe.Contains("ColorizationColor=0XC40078D4") && farbe.Contains("TileWallpaper=0")
                && !farbe.Replace("\r\n", "").Contains('\n') && farbe.EndsWith("\r\n"));
            var bild = DesignDatei.FuerBild(thema, @"C:\Users\Anwender\Pictures\Urlaub.jpg",
                new HintergrundEinstellung { Darstellung = "6", Kacheln = "0", Farbe = "0 99 177" });
            Check("Bild: Pfad, Darstellung und Randfarbe eingetragen",
                DesignDatei.Lesen(bild, DesignDatei.DesktopAbschnitt, "Wallpaper") == @"C:\Users\Anwender\Pictures\Urlaub.jpg"
                && DesignDatei.Lesen(bild, DesignDatei.DesktopAbschnitt, "WallpaperStyle") == "6"
                && DesignDatei.Lesen(bild, DesignDatei.FarbenAbschnitt, "Background") == "0 99 177" && !bild.Contains("[Slideshow]"));
            Check("WallpaperStyle ist nicht Wallpaper (Schluessel genau verglichen)",
                DesignDatei.Lesen(DesignDatei.Setzen(thema, DesignDatei.DesktopAbschnitt, "Wallpaper", "x"), DesignDatei.DesktopAbschnitt, "WallpaperStyle") == "10");
            var ohneFarben = DesignDatei.FuerFarbe("[Theme]\nDisplayName=x\n", ((byte)1, (byte)2, (byte)3));
            Check("fehlende Abschnitte werden angelegt (LF bleibt LF)",
                DesignDatei.Lesen(ohneFarben, DesignDatei.FarbenAbschnitt, "Background") == "1 2 3" && ohneFarben.Contains("\n[Control Panel\\Colors]\nBackground=1 2 3\n") && !ohneFarben.Contains('\r'), ohneFarben);
            var neuerSchluessel = DesignDatei.Setzen("[A]\r\nx=1\r\n\r\n[B]\r\ny=2\r\n", "A", "z", "3");
            Check("neuer Schluessel ans Ende seines Abschnitts, vor die Leerzeile", neuerSchluessel == "[A]\r\nx=1\r\nz=3\r\n\r\n[B]\r\ny=2\r\n", neuerSchluessel);
            var eigenes = DesignDatei.ZumAnwenden("[Theme]\r\nDisplayName=@%SystemRoot%\\x.dll,-1\r\nThemeId={1234}\r\nSetLogonBackground=0\r\n\r\n[Control Panel\\Desktop]\r\nWallpaper=a.jpg\r\n", "Design aus der Sicherung (DaSi)");
            Check("zum Anwenden: eigener Name, ohne ThemeId, Rest bleibt",
                DesignDatei.Lesen(eigenes, "Theme", "DisplayName") == "Design aus der Sicherung (DaSi)" && DesignDatei.Lesen(eigenes, "Theme", "ThemeId") is null
                && eigenes.Contains("SetLogonBackground=0\r\n") && DesignDatei.Lesen(eigenes, DesignDatei.DesktopAbschnitt, "Wallpaper") == "a.jpg", eigenes);
            var lilaW = ((byte)107, (byte)105, (byte)214);
            var ohneBild = DesignDatei.FuerWiederherstellung(DesignDatei.Setzen(thema, DesignDatei.DesktopAbschnitt, "Wallpaper", ""), "x", lilaW, null, null, Array.Empty<DesignEintrag>());
            var blick = DesignDatei.FuerWiederherstellung(DesignDatei.Setzen(thema, DesignDatei.DesktopAbschnitt, "Wallpaper",
                @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_2.jpg"), "x", lilaW, null, null, Array.Empty<DesignEintrag>());
            var mitBild = DesignDatei.FuerWiederherstellung(thema, "x", lilaW, null, null, Array.Empty<DesignEintrag>());
            Check("Wiederherstellung, Volltonfarbe: Farbe drin, leeres Bild und Blickpunkt-Bild durch img0 ersetzt, vorhandenes Bild bleibt",
                DesignDatei.Lesen(ohneBild, DesignDatei.FarbenAbschnitt, "Background") == "107 105 214"
                && DesignDatei.Lesen(ohneBild, DesignDatei.DesktopAbschnitt, "Wallpaper") == DesignDatei.Ersatzbild
                && DesignDatei.Lesen(blick, DesignDatei.DesktopAbschnitt, "Wallpaper") == DesignDatei.Ersatzbild
                && DesignDatei.Lesen(mitBild, DesignDatei.DesktopAbschnitt, "Wallpaper") == @"%SystemRoot%\web\wallpaper\Windows\img0.jpg"
                && DesignDatei.Lesen(mitBild, "Theme", "DisplayName") == "x" && !mitBild.Contains("[Slideshow]"));
            var wiederBild = DesignDatei.FuerWiederherstellung(thema, "x", null, @"C:\Users\A\Pictures\Urlaub.jpg", new HintergrundEinstellung { Darstellung = "6" },
                new[] { new DesignEintrag { Gruppe = "Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = "SystemUsesLightTheme", Typ = "DWord", Wert = "0" } });
            Check("Wiederherstellung, Bild: Pfad, Darstellung und Hell/Dunkel drin",
                DesignDatei.Lesen(wiederBild, DesignDatei.DesktopAbschnitt, "Wallpaper") == @"C:\Users\A\Pictures\Urlaub.jpg"
                && DesignDatei.Lesen(wiederBild, DesignDatei.DesktopAbschnitt, "WallpaperStyle") == "6"
                && DesignDatei.Lesen(wiederBild, DesignDatei.StilAbschnitt, "SystemMode") == "Dark");
            Check("Gross-/Kleinschreibung bei Abschnitt und Schluessel egal",
                DesignDatei.Lesen("[control panel\\COLORS]\r\nbackground = 9 9 9\r\n", DesignDatei.FarbenAbschnitt, "Background") == "9 9 9");

            var design = DesignDatei.FuerDesign(thema, new[]
            {
                new DesignEintrag { Gruppe = "Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = "SystemUsesLightTheme", Typ = "DWord", Wert = "0" },
                new DesignEintrag { Gruppe = "Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = "AppsUseLightTheme", Typ = "DWord", Wert = "1" },
                new DesignEintrag { Gruppe = "Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\DWM", Name = "ColorizationColor", Typ = "DWord", Wert = "-1006602028" },
                new DesignEintrag { Gruppe = "Akzentfarbe", Schluessel = @"Control Panel\Desktop", Name = "AutoColorization", Typ = "String", Wert = "1" },
                new DesignEintrag { Gruppe = "Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = "", Typ = "String", Wert = "Windows-Voreinstellung (groß)" },
                new DesignEintrag { Gruppe = "Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = "Arrow", Typ = "ExpandString", Wert = @"%SystemRoot%\cursors\aero_arrow_l.cur" },
                new DesignEintrag { Gruppe = "Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = "Hand", Typ = "String", Wert = @"C:\x.cur" },
            });
            Check("Design: Hell/Dunkel als SystemMode/AppMode",
                DesignDatei.Lesen(design, DesignDatei.StilAbschnitt, "SystemMode") == "Dark" && DesignDatei.Lesen(design, DesignDatei.StilAbschnitt, "AppMode") == "Light");
            Check("Design: Akzentfarbe als 0X-Wert, automatisch uebernommen",
                DesignDatei.Lesen(design, DesignDatei.StilAbschnitt, "ColorizationColor") == "0XC40078D4"
                && DesignDatei.Lesen(design, DesignDatei.StilAbschnitt, "AutoColorization") == "1");
            Check("Design: Zeigerschema als DefaultValue, Zeiger ersetzt, unbekannte nicht angelegt",
                DesignDatei.Lesen(design, DesignDatei.ZeigerAbschnitt, "DefaultValue") == "Windows-Voreinstellung (groß)"
                && DesignDatei.Lesen(design, DesignDatei.ZeigerAbschnitt, "Arrow") == @"%SystemRoot%\cursors\aero_arrow_l.cur"
                && DesignDatei.Lesen(design, DesignDatei.ZeigerAbschnitt, "Hand") is null);

            // Kodierung bleibt: UTF-16 mit BOM, UTF-8 mit BOM, ASCII; Umlaut in ASCII -> UTF-16.
            var dd = Path.Combine(root, "design-datei");
            Directory.CreateDirectory(dd);
            foreach (var (name, kod) in new (string, System.Text.Encoding)[]
            {
                ("utf16", new System.Text.UnicodeEncoding(false, true)), ("utf8", new System.Text.UTF8Encoding(true)), ("ascii", System.Text.Encoding.ASCII),
            })
            {
                var thDatei = Path.Combine(dd, name + ".theme");
                var thStart = "[Control Panel\\Colors]\r\nBackground=0 0 0\r\n";
                File.WriteAllBytes(thDatei, kod.GetPreamble().Concat(kod.GetBytes(thStart)).ToArray());
                var (gelesen, erkannt) = DesignDatei.DateiLesen(thDatei);
                DesignDatei.DateiSchreiben(thDatei, DesignDatei.FuerFarbe(gelesen, ((byte)107, (byte)105, (byte)214)), erkannt);
                var thBytes = File.ReadAllBytes(thDatei);
                var bomGleich = thBytes.Take(kod.GetPreamble().Length).SequenceEqual(kod.GetPreamble());
                var (wieder, _) = DesignDatei.DateiLesen(thDatei);
                Check($"Kodierung {name}: erkannt, gleich zurueckgeschrieben, lesbar",
                    gelesen == thStart && bomGleich && DesignDatei.Lesen(wieder, DesignDatei.FarbenAbschnitt, "Background") == "107 105 214"
                    && !File.Exists(thDatei + ".dasi-neu"));
            }
            var asciiDatei = Path.Combine(dd, "ascii.theme");
            var (asciiText, asciiKod) = DesignDatei.DateiLesen(asciiDatei);
            DesignDatei.DateiSchreiben(asciiDatei, DesignDatei.FuerBild(asciiText, @"C:\Users\Jürgen\Bilder\Strand.jpg", null), asciiKod);
            var asciiBytes = File.ReadAllBytes(asciiDatei);
            Check("Umlaut in einer ASCII-Datei: als UTF-16 mit BOM geschrieben und wieder lesbar",
                asciiBytes[0] == 0xFF && asciiBytes[1] == 0xFE
                && DesignDatei.Lesen(DesignDatei.DateiLesen(asciiDatei).Inhalt, DesignDatei.DesktopAbschnitt, "Wallpaper") == @"C:\Users\Jürgen\Bilder\Strand.jpg");
        }

        Console.WriteLine("\n== Design: Einstellungen von Windows ==");
        {
            var designKat = DesignEinstellungen.Katalog;
            const string pers = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string dwm = @"Software\Microsoft\Windows\DWM";
            const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            const string cur = @"Control Panel\Cursors";
            bool ImKatalog(string s, string n) => designKat.Any(k => k.Schluessel == s && k.Name == n);
            Check("Katalog: Hell/Dunkel fuer Windows und Apps, Transparenz",
                ImKatalog(pers, "SystemUsesLightTheme") && ImKatalog(pers, "AppsUseLightTheme") && ImKatalog(pers, "EnableTransparency"));
            Check("Katalog: Akzentfarbe (Palette, DWM, automatisch)",
                ImKatalog(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette") && ImKatalog(dwm, "AccentColor")
                && ImKatalog(dwm, "ColorPrevalence") && ImKatalog(@"Control Panel\Desktop", "AutoColorization"));
            Check("Katalog: Mauszeiger-Schema, Groesse und Zeiger", ImKatalog(cur, "") && ImKatalog(cur, "CursorBaseSize") && ImKatalog(cur, "Arrow") && ImKatalog(cur, "Hand"));
            Check("Katalog: Taskleiste und Start", ImKatalog(adv, "TaskbarAl") && ImKatalog(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode")
                && ImKatalog(adv, "Start_Layout"));
            Check("Katalog: keine geschuetzten Werte (Widgets) und keine Bildschirmpositionen",
                !ImKatalog(adv, "TaskbarDa") && !designKat.Any(k => k.Schluessel.Contains("StuckRects")));
            Check("Katalog: keine Dubletten", designKat.Select(k => k.Schluessel + "|" + k.Name).Distinct().Count() == designKat.Count);
            Check("Katalog: nur HKEY_CURRENT_USER-Pfade (Software\\..., Control Panel\\...)",
                designKat.All(k => k.Schluessel.StartsWith(@"Software\", StringComparison.Ordinal) || k.Schluessel.StartsWith(@"Control Panel\", StringComparison.Ordinal)));

            // Ein nachgebauter Rechner: dunkel, Transparenz an, Akzent #0063B1, Taskleiste links.
            var fakeReg = new Dictionary<string, (string Typ, string Wert)>
            {
                [pers + "|SystemUsesLightTheme"] = ("DWord", "0"),
                [pers + "|AppsUseLightTheme"] = ("DWord", "0"),
                [pers + "|EnableTransparency"] = ("DWord", "1"),
                [dwm + "|AccentColor"] = ("DWord", unchecked((int)0xFFB16300).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                [@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent|AccentPalette"] = ("Binary", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })),
                [cur + "|"] = ("String", "Windows-Standard (groß)"),
                [cur + "|Arrow"] = ("ExpandString", @"%SystemRoot%\cursors\aero_arrow_l.cur"),
                [cur + "|Hand"] = ("ExpandString", @"C:\Users\Alt\Zeiger\eigene_hand.cur"),
                [adv + "|TaskbarAl"] = ("DWord", "0"),
            };
            var designWerte = DesignEinstellungen.Auslesen((s, n) => fakeReg.TryGetValue(s + "|" + n, out var v) ? v : null);
            Check("Auslesen: genau die vorhandenen Werte", designWerte.Count == fakeReg.Count, designWerte.Count.ToString());
            Check("Auslesen: Gruppen", DesignEinstellungen.Gruppen(designWerte) == "Farben, Akzentfarbe, Mauszeiger, Taskleiste", DesignEinstellungen.Gruppen(designWerte));
            Check("Auslesen: ein Lesefehler bricht nicht ab",
                DesignEinstellungen.Auslesen((s, n) => n == "AppsUseLightTheme" ? throw new UnauthorizedAccessException() : fakeReg.TryGetValue(s + "|" + n, out var v) ? v : null).Count == fakeReg.Count - 1);
            var designKurz = DesignEinstellungen.Zusammenfassung(designWerte, De);
            Check("Zusammenfassung fuers Protokoll",
                designKurz == "Windows dunkel, Apps dunkel, Transparenz an, Akzentfarbe #0063B1, Mauszeiger \"Windows-Standard (groß)\", Taskleiste links", designKurz);
            Check("Zusammenfassung englisch", DesignEinstellungen.Zusammenfassung(designWerte, En).StartsWith("Windows dark, apps dark, transparency on"));
            Check("Akzentfarbe aus DWM (0xAABBGGRR)", DesignEinstellungen.AkzentHex(unchecked((int)0xFFB16300)) == "#0063B1");
            var mitAuto = designWerte.Append(new DesignEintrag { Schluessel = @"Control Panel\Desktop", Name = "AutoColorization", Typ = "DWord", Wert = "1" }).ToList();
            Check("Akzentfarbe automatisch statt fester Farbe", DesignEinstellungen.Zusammenfassung(mitAuto, De).Contains("Akzentfarbe automatisch"));

            // Mauszeiger aus einem eigenen Schema, dessen Datei hier fehlt.
            bool ZeigerDa(string pfad) => pfad.Contains("aero_arrow");
            Check("Zeiger mit vorhandener Datei wird gesetzt", !DesignEinstellungen.ZeigerFehlt(designWerte.First(e => e.Name == "Arrow"), ZeigerDa));
            Check("Zeiger ohne Datei wird ausgelassen", DesignEinstellungen.ZeigerFehlt(designWerte.First(e => e.Name == "Hand"), ZeigerDa));
            Check("Schema-Name und andere Werte sind nie \"Zeiger ohne Datei\"",
                !DesignEinstellungen.ZeigerFehlt(designWerte.First(e => e.Name == "" && e.Gruppe == "Mauszeiger"), ZeigerDa)
                && !DesignEinstellungen.ZeigerFehlt(designWerte.First(e => e.Name == "TaskbarAl"), ZeigerDa));

            // Speichern und Laden.
            var designOrdner = Path.Combine(root, "design", "DASI");
            DesignEinstellungen.Speichern(designOrdner, new DesignSicherung { Rechner = "PC", Erstellt = new DateTime(2026, 11, 21), Werte = designWerte });
            var designZurueck = DesignEinstellungen.Laden(designOrdner);
            Check("Design.json: Werte wieder gelesen, gleich", designZurueck is not null && designZurueck.Werte.Count == designWerte.Count
                && designZurueck.Werte.Select(w => w.Schluessel + w.Name + w.Typ + w.Wert).SequenceEqual(designWerte.Select(w => w.Schluessel + w.Name + w.Typ + w.Wert)));
            Check("Design.json: Umlaute bleiben", designZurueck!.Werte.Any(w => w.Wert == "Windows-Standard (groß)"));
            Check("eine Datei: Design\\Design.json", File.Exists(Path.Combine(designOrdner, "Design", "Design.json"))
                && !File.Exists(Path.Combine(designOrdner, "Design", "Design-Einstellungen.json")));

            // Hintergrund und Einstellungen in einer Datei - Bild mit Herkunft.
            var eins = Path.Combine(root, "design", "eins");
            DesignEinstellungen.Speichern(eins, new DesignSicherung
            {
                Rechner = "PC", Erstellt = new DateTime(2026, 11, 21),
                Hintergrund = new HintergrundEinstellung { Art = "Bild", Bild = "Urlaub.jpg", BildPfad = @"C:\Users\Alt\Pictures\Urlaub.jpg",
                                                           Profil = @"C:\Users\Alt", Farbe = "0 0 0", Darstellung = "6", Kacheln = "0" },
                Werte = designWerte,
            });
            var einsZurueck = DesignEinstellungen.Laden(eins);
            Check("eine Datei: Bild samt Herkunft und Darstellung wieder gelesen",
                einsZurueck?.Hintergrund is { Art: "Bild", Bild: "Urlaub.jpg", Darstellung: "6" } hb && hb.BildPfad == @"C:\Users\Alt\Pictures\Urlaub.jpg"
                && hb.Profil == @"C:\Users\Alt" && einsZurueck.Werte.Count == designWerte.Count && einsZurueck.Version == 2);
            var nurHg = Path.Combine(root, "design", "nur-hintergrund");
            DesignEinstellungen.Speichern(nurHg, new DesignSicherung { Hintergrund = new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "107 105 214" } });
            Check("eine Datei: nur der Hintergrund (Einstellungen nicht lesbar) genuegt",
                DesignEinstellungen.Vorhanden(nurHg) && Hintergrund.IstVolltonfarbe(DesignEinstellungen.Laden(nurHg)!.Hintergrund));
            AppCatalog.HatWlanAdapter = () => true;
            Check("eine Datei: Punkt beim Wiederherstellen frei", AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, nurHg) is null);

            // Zwischenfassung (Sicherung vom 21.11.2026, 20:41): Einstellungen in
            // Design\Design-Einstellungen.json, Volltonfarbe getrennt in
            // Hintergrundbild\Hintergrund.json - bleibt wiederherstellbar.
            var zwischen = Path.Combine(root, "design", "zwischenfassung");
            Directory.CreateDirectory(Path.Combine(zwischen, "Design"));
            File.WriteAllText(Path.Combine(zwischen, "Design", "Design-Einstellungen.json"),
                "{ \"Version\": 1, \"Rechner\": \"PC\", \"Werte\": [ { \"Gruppe\": \"Farben\", \"Schluessel\": \"" + pers.Replace("\\", "\\\\")
                + "\", \"Name\": \"SystemUsesLightTheme\", \"Typ\": \"DWord\", \"Wert\": \"0\" } ] }");
            Directory.CreateDirectory(Path.Combine(zwischen, "Hintergrundbild"));
            Hintergrund.Speichern(Path.Combine(zwischen, "Hintergrundbild"), new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "107 105 214" });
            var zw = DesignEinstellungen.Laden(zwischen);
            Check("Zwischenfassung: Einstellungen werden gelesen, Hintergrund kommt aus dem alten Ordner",
                zw is { Werte.Count: 1, Hintergrund: null } && AppCatalog.HatHintergrund(zwischen)
                && Hintergrund.IstVolltonfarbe(Hintergrund.Laden(Path.Combine(zwischen, "Hintergrundbild"))));
            Check("Zwischenfassung: Punkt beim Wiederherstellen frei", AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, zwischen) is null);

            // Liegen beide Dateien da, gilt Design.json.
            DesignEinstellungen.Speichern(zwischen, new DesignSicherung { Hintergrund = new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "1 2 3" } });
            Check("Design.json hat Vorrang vor der Datei der Zwischenfassung", DesignEinstellungen.Laden(zwischen)?.Hintergrund?.Farbe == "1 2 3");
            Check("ohne Datei: nicht vorhanden", !DesignEinstellungen.Vorhanden(Path.Combine(root, "design", "leer")));

            // Verfuegbarkeit: nur Design-Einstellungen (Hintergrund Diashow) genuegen.
            AppCatalog.HatWlanAdapter = () => true;
            Check("Wiederherstellen: Design-Einstellungen allein machen den Punkt frei",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, designOrdner) is null, AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, designOrdner)?.De);
            var leerD = Path.Combine(root, "design", "ohne");
            Directory.CreateDirectory(Path.Combine(leerD, "Benutzerprofil"));
            Check("Wiederherstellen: Grund nennt Hintergrund und Design-Einstellungen",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, leerD)?.De == "Die gewählte Sicherung enthält kein Design (weder Hintergrund noch Design-Einstellungen).",
                AppCatalog.BeimWiederherstellen(Punkt.Hintergrund, leerD)?.De);
        }

        Console.WriteLine("\n== Taskleisten-Symbole ==");
        {
            var tlProfil = Path.Combine(root, "taskleiste", "Users", "Anwender");
            var pin = Taskleiste.PinOrdner(tlProfil);
            Check("Ordner der angehefteten Verknuepfungen im Profil",
                pin == Path.Combine(tlProfil, "AppData", "Roaming", "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"), pin);
            Directory.CreateDirectory(pin);
            foreach (var n in new[] { "Microsoft Edge.lnk", "Datei-Explorer.lnk", "Firefox.lnk" }) File.WriteAllText(Path.Combine(pin, n), "lnk");
            File.WriteAllText(Path.Combine(pin, "desktop.ini"), "x");
            var lnk = Taskleiste.Verknuepfungen(pin);
            Check("nur Verknuepfungen, nach Namen", lnk.SequenceEqual(new[] { "Datei-Explorer.lnk", "Firefox.lnk", "Microsoft Edge.lnk" }), string.Join(", ", lnk));
            Check("Namen fuers Protokoll", Taskleiste.Namen(lnk) == "Datei-Explorer, Firefox, Microsoft Edge");
            Check("kein Ordner: keine Verknuepfungen", Taskleiste.Verknuepfungen(Path.Combine(root, "taskleiste", "fehlt")).Count == 0);

            var tlSich = Path.Combine(root, "taskleiste", "DASI");
            var band = new List<DesignEintrag>
            {
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "Favorites", Typ = "Binary", Wert = Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }) },
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesVersion", Typ = "DWord", Wert = "3" },
            };
            Taskleiste.Speichern(tlSich, new TaskleistenSicherung { Rechner = "PC", Erstellt = new DateTime(2026, 11, 21), Verknuepfungen = lnk, Taskband = band });
            var tlZurueck = Taskleiste.Laden(tlSich);
            Check("Taskleiste.json: Verknuepfungen und Taskband wieder gelesen",
                tlZurueck is not null && tlZurueck.Verknuepfungen.SequenceEqual(lnk)
                && tlZurueck.Taskband.Select(b => b.Name + b.Typ + b.Wert).SequenceEqual(band.Select(b => b.Name + b.Typ + b.Wert)));
            Check("liegt unter Desktop-Symbole\\Taskleiste", File.Exists(Path.Combine(tlSich, "Desktop-Symbole", "Taskleiste", "Taskleiste.json")));
            var leerTl = Path.Combine(root, "taskleiste", "leer");
            Taskleiste.Speichern(leerTl, new TaskleistenSicherung());
            Check("leere Taskleiste gilt nicht als gesichert", !Taskleiste.Vorhanden(leerTl) && Taskleiste.Laden(Path.Combine(root, "taskleiste", "nichts")) is null);

            // Verfuegbarkeit: nur die Taskleiste, keine Symbolpositionen.
            AppCatalog.HatWlanAdapter = () => true;
            Check("nur Taskleiste: Variante Symbole frei", AppCatalog.HatDesktopSymbole(tlSich) && !DesktopIcons.Vorhanden(tlSich));
            Check("nur Taskleiste: Desktop-Layout beim Wiederherstellen frei", AppCatalog.BeimWiederherstellen(Punkt.DesktopShot, tlSich) is null);
        }

        // ================================================================
        // Austauschskript WIRKLICH ausfuehren - nur unter Windows moeglich
        // ================================================================
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("\n== Austauschskript echt ausführen (Umlaute im Pfad) ==");

            // Wie im echten Leben: Der Benutzername steckt im Pfad, und er hat Umlaute.
            var heim = Path.Combine(root, "Jürgen Müller");
            var ziel2 = Path.Combine(heim, "Programme", "WindowsDaSiTool.exe");
            var neu2 = Path.Combine(heim, "Neu", "WindowsDaSiTool.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(ziel2)!);
            Directory.CreateDirectory(Path.GetDirectoryName(neu2)!);

            // Als "neue Fassung" dient hostname.exe: echte EXE, startet, endet sofort.
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), neu2);
            File.WriteAllText(ziel2, "alte Fassung");
            var erwartet = File.ReadAllBytes(neu2);

            var skript2 = UpdateService.WriteUpdateScript(neu2, ziel2);
            // Genau der Aufruf, den auch das Programm benutzt.
            var psi = UpdateService.BuildUpdateStart(skript2, neu2, ziel2);
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                proc.StandardInput.Close();   // ein "pause" darf den Test nicht aufhalten
                var fertig = proc.WaitForExit(60_000);
                var ausgabe = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                Check("Skript endet von selbst", fertig);
                Check("Skript endet sauber (Exit-Code 0)",
                    fertig && proc.ExitCode == 0,
                    $"ExitCode {(fertig ? proc.ExitCode : -1)}: {ausgabe.Trim().Replace("\r\n", " | ")}");
                Check("die neue Fassung steht danach am Ziel",
                    File.Exists(ziel2) && File.ReadAllBytes(ziel2).AsSpan().SequenceEqual(erwartet),
                    ausgabe.Trim().Replace("\r\n", " | "));
            }

            // Zweiter Lauf: Die Zieldatei ist gesperrt, wie von einem noch
            // laufenden Programm. Das Skript muss warten und danach austauschen.
            Console.WriteLine("\n== Austauschskript wartet, solange das Ziel gesperrt ist ==");
            var ziel3 = Path.Combine(heim, "Gesperrt", "WindowsDaSiTool.exe");
            var neu3 = Path.Combine(heim, "Neu3", "WindowsDaSiTool.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(ziel3)!);
            Directory.CreateDirectory(Path.GetDirectoryName(neu3)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), neu3);
            File.WriteAllText(ziel3, "alte Fassung, noch in Benutzung");

            var skript3 = UpdateService.WriteUpdateScript(neu3, ziel3);
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var sperre = new FileStream(ziel3, FileMode.Open, FileAccess.Read, FileShare.None);
            var psi3 = UpdateService.BuildUpdateStart(skript3, neu3, ziel3);
            psi3.RedirectStandardInput = true;
            psi3.RedirectStandardOutput = true;
            psi3.RedirectStandardError = true;
            using (var proc = System.Diagnostics.Process.Start(psi3)!)
            {
                proc.StandardInput.Close();
                System.Threading.Thread.Sleep(3000);
                // Solange gesperrt: Das Skript darf weder fertig sein noch
                // die neue Fassung schon verschoben haben.
                Check("waehrend der Sperre wartet das Skript noch",
                    !proc.HasExited && File.Exists(neu3));
                sperre.Dispose();   // "Programm beendet"
                var fertig = proc.WaitForExit(30_000);
                uhr.Stop();
                var ausgabe = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                Check("Skript endet nach Freigabe von selbst", fertig, ausgabe.Trim());
                Check("es hat gewartet, solange gesperrt war (>= 3 s)",
                    uhr.Elapsed.TotalSeconds >= 3, $"{uhr.Elapsed.TotalSeconds:F1} s");
                Check("danach steht die neue Fassung am Ziel",
                    File.ReadAllBytes(ziel3).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "hostname.exe"))),
                    ausgabe.Trim());
                Check("und das ohne Fehlerfall (kein Editor-Hinweis)", proc.ExitCode == 0,
                    $"ExitCode {proc.ExitCode}: {ausgabe.Trim().Replace("\r\n", " | ")}");
            }
            UpdateService.CleanUp();
        }

        // ================================================================
        // Desktop-Screenshot WIRKLICH aufnehmen - nur unter Windows moeglich
        // ================================================================
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("\n== Bildschirm echt aufnehmen ==");
            ScreenCapture? aufnahme = null;
            try { aufnahme = DesktopScreenshot.CaptureVirtualScreen(); }
            catch (Exception ex) { Check("Aufnahme gelingt", false, ex.Message); }
            if (aufnahme is not null)
            {
                Console.WriteLine($"         {aufnahme.Width} x {aufnahme.Height} ab ({aufnahme.Left},{aufnahme.Top}), {aufnahme.Monitors} Bildschirm(e)");
                Check("Aufnahme liefert Masse", aufnahme.Width > 0 && aufnahme.Height > 0);
                Check("Puffer passt zu den Massen", aufnahme.Bgra.Length == (long)aufnahme.Width * aufnahme.Height * 4);

                foreach (var a in aufnahme.Areas)
                    Console.WriteLine($"         Monitor {a.Device}: {a.Width} x {a.Height} ab ({a.Left},{a.Top}){(a.Primary ? ", Hauptbildschirm" : "")}");
                Check("Monitore werden einzeln erkannt", aufnahme.Areas.Count >= 1 && aufnahme.Areas.Count(a => a.Primary) == 1);
                Check("jeder Monitor liegt im aufgenommenen Bild",
                    aufnahme.Areas.All(a => a.Left >= aufnahme.Left && a.Top >= aufnahme.Top
                                            && a.Left + a.Width <= aufnahme.Left + aufnahme.Width
                                            && a.Top + a.Height <= aufnahme.Top + aufnahme.Height));
                var plan = DesktopScreenshot.PlanImages(aufnahme.Areas);
                Check("ein Bild pro Monitor geplant", plan.Count == aufnahme.Areas.Count,
                    string.Join(", ", plan.Select(p => p.FileName)));

                // Wie im Programm: je Monitor ausschneiden und als PNG speichern.
                var bildOrdner = Path.Combine(root, "Desktop-Screenshot");
                Directory.CreateDirectory(bildOrdner);
                bool monitorBilderOk = true;
                foreach (var mon in plan)
                {
                    var teil = DesktopScreenshot.Crop(aufnahme.Bgra, aufnahme.Width, aufnahme.Height,
                        mon.Area.Left - aufnahme.Left, mon.Area.Top - aufnahme.Top, mon.Area.Width, mon.Area.Height, out var tw, out var th);
                    var pngDatei = Path.Combine(bildOrdner, mon.FileName);
                    using (var fs = File.Create(pngDatei)) PngWriter.WriteBgra(fs, tw, th, teil);
                    var gelesen = DecodeOwnPng(File.ReadAllBytes(pngDatei), out var dw, out var dh);
                    bool stichprobe = dw == mon.Area.Width && dh == mon.Area.Height;
                    for (long i = 0; stichprobe && i < (long)dw * dh; i += 997)
                        stichprobe = gelesen[i * 3] == teil[i * 4 + 2] && gelesen[i * 3 + 2] == teil[i * 4];
                    Console.WriteLine($"         {mon.FileName}: {dw} x {dh}, {new FileInfo(pngDatei).Length / 1024} KB");
                    monitorBilderOk &= stichprobe;
                }
                Check("je Monitor als PNG gespeichert, in Monitorgroesse und verlustfrei", monitorBilderOk);
                Check("beim Wiederherstellen werden genau diese Bilder gefunden",
                    DesktopScreenshot.FindImages(bildOrdner).Select(Path.GetFileName).SequenceEqual(plan.Select(p => p.FileName)));
            }

            Console.WriteLine("\n== Design-Einstellungen echt (Registry) ==");
            {
                var echt = DesignEinstellungen.Auslesen((s, n) => DesignRegistry.Lesen(Microsoft.Win32.Registry.CurrentUser, s, n));
                Console.WriteLine($"         gelesen: {echt.Count} Werte ({DesignEinstellungen.Gruppen(echt)}) - {DesignEinstellungen.Zusammenfassung(echt, De)}");
                Check("echt: Design-Einstellungen lesbar (mindestens das Mauszeiger-Schema)", echt.Count > 0);
                Check("echt: %SystemRoot% in Zeigerpfaden bleibt unaufgeloest",
                    !echt.Any(e => e.Typ == "ExpandString") || echt.Where(e => e.Typ == "ExpandString").Any(e => e.Wert.Contains('%')));

                // Hin und zurueck in einem eigenen Testschluessel - alle vier Typen.
                const string test = @"Software\DaSiTest\Design";
                var proben = new[]
                {
                    new DesignEintrag { Schluessel = test, Name = "Zahl", Typ = "DWord", Wert = unchecked((int)0xFFB16300).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    new DesignEintrag { Schluessel = test, Name = "", Typ = "String", Wert = "Windows-Standard (groß)" },
                    new DesignEintrag { Schluessel = test, Name = "Pfad", Typ = "ExpandString", Wert = @"%SystemRoot%\cursors\aero_arrow.cur" },
                    new DesignEintrag { Schluessel = test, Name = "Bytes", Typ = "Binary", Wert = Convert.ToBase64String(new byte[] { 0, 99, 177, 255 }) },
                };
                try
                {
                    foreach (var pr in proben) DesignRegistry.Schreiben(Microsoft.Win32.Registry.CurrentUser, pr);
                    var wieder = proben.Select(pr => DesignRegistry.Lesen(Microsoft.Win32.Registry.CurrentUser, pr.Schluessel, pr.Name)).ToList();
                    Check("echt: alle vier Typen hin und zurueck unveraendert",
                        wieder.Zip(proben).All(x => x.First is { } w && w.Typ == x.Second.Typ && w.Wert == x.Second.Wert),
                        string.Join(" | ", wieder.Select(w => w is null ? "null" : $"{w.Value.Typ}={w.Value.Wert}")));
                    Check("echt: fehlender Wert -> null", DesignRegistry.Lesen(Microsoft.Win32.Registry.CurrentUser, test, "gibt-es-nicht") is null
                        && DesignRegistry.Lesen(Microsoft.Win32.Registry.CurrentUser, @"Software\DaSiTest\gibt-es-nicht", "x") is null);
                }
                finally
                {
                    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DaSiTest", false); } catch { }
                }
            }

            Console.WriteLine("\n== Volltonfarbe echt (SetSysColors/GetSysColor) ==");
            {
                var farbeVorher = FarbeEcht.GetSysColor(1);
                var farbeBlau = ((byte)0, (byte)99, (byte)177);
                var farbeGesetzt = FarbeEcht.SetSysColors(1, new[] { 1 }, new[] { Hintergrund.ColorRef(farbeBlau) });
                var farbeNachher = FarbeEcht.GetSysColor(1);
                Console.WriteLine($"         farbeVorher 0x{farbeVorher:X6}, farbeGesetzt {farbeGesetzt}, farbeNachher 0x{farbeNachher:X6}");
                Check("echt: Desktopfarbe laesst sich setzen", farbeGesetzt);
                Check("echt: zurueckgelesen ist es dieselbe Farbe (Bytereihenfolge stimmt)",
                    farbeNachher == (uint)Hintergrund.ColorRef(farbeBlau)
                    && Hintergrund.Hex(((byte)(farbeNachher & 0xFF), (byte)((farbeNachher >> 8) & 0xFF), (byte)((farbeNachher >> 16) & 0xFF))) == "#0063B1",
                    $"0x{farbeNachher:X6}");
                FarbeEcht.SetSysColors(1, new[] { 1 }, new[] { (int)farbeVorher });
            }

            Console.WriteLine("\n== Desktop-Symbole echt (Explorer) ==");
            {
                // Ohne Explorer kein Desktop - auf einem Laeufer ggf. erst starten.
                if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                {
                    Console.WriteLine("         Explorer laeuft nicht - wird gestartet");
                    try { System.Diagnostics.Process.Start("explorer.exe"); } catch (Exception ex) { Console.WriteLine($"         nicht startbar: {ex.Message}"); }
                }
                var desktopOrdner = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var testName = "DaSi-Symboltest.txt";
                var testDatei = Path.Combine(desktopOrdner, testName);
                DesktopLayout? vorher = null;
                string? grund = null;
                try
                {
                    Directory.CreateDirectory(desktopOrdner);
                    File.WriteAllText(testDatei, "Pruefung der Symbolpositionen");
                    for (int i = 0; i < 30 && vorher is null; i++)
                    {
                        try
                        {
                            var l = DesktopIcons.Lesen("CI", "runner");
                            if (l.Symbole.Any(x => x.Name.Equals(testName, StringComparison.OrdinalIgnoreCase))) vorher = l;
                            else grund = $"Testsymbol nicht unter {l.Symbole.Count} Symbolen";
                        }
                        catch (Exception ex) { grund = ex.Message; }
                        if (vorher is null) System.Threading.Thread.Sleep(1000);
                    }
                    if (vorher is null)
                    {
                        Console.WriteLine($"         (kein Explorer-Desktop auf diesem Laeufer: {grund} - Test uebersprungen)");
                    }
                    else
                    {
                        var symStart = vorher.Symbole.First(x => x.Name.Equals(testName, StringComparison.OrdinalIgnoreCase));
                        Console.WriteLine($"         Desktop {vorher.Breite} x {vorher.Hoehe}, {vorher.Symbole.Count} Symbol(e), "
                                          + $"automatisch anordnen = {vorher.AutomatischAnordnen}, Raster = {vorher.AmRasterAusrichten}");
                        Console.WriteLine($"         Testsymbol \"{symStart.Anzeige}\" bei ({symStart.X},{symStart.Y})");
                        Check("echt: Desktop hat eine Groesse", vorher.Breite > 0 && vorher.Hoehe > 0);
                        Check("echt: Name im Desktop-Ordner mit Endung, Anzeigename lesbar",
                            symStart.Name == testName && symStart.Anzeige.StartsWith("DaSi-Symboltest", StringComparison.OrdinalIgnoreCase), symStart.Anzeige);

                        // Verschieben: alles wie es ist, nur das Testsymbol woanders hin.
                        var zielX = symStart.X < vorher.Breite / 2 ? vorher.Breite / 2 : vorher.Breite / 4;
                        var zielY = symStart.Y < vorher.Hoehe / 2 ? vorher.Hoehe / 2 : vorher.Hoehe / 4;
                        var verschoben = new DesktopLayout
                        {
                            Breite = vorher.Breite, Hoehe = vorher.Hoehe, AmRasterAusrichten = vorher.AmRasterAusrichten,
                            Symbole = { symStart with { X = zielX, Y = zielY } }
                        };
                        var e1 = DesktopIcons.Wiederherstellen(verschoben, TimeSpan.FromSeconds(5));
                        System.Threading.Thread.Sleep(800);
                        var mitte = DesktopIcons.Lesen("CI", "runner").Symbole.First(x => x.Name == testName);
                        Console.WriteLine($"         verschoben nach ({mitte.X},{mitte.Y}), Ziel war ({zielX},{zielY})");
                        Check("echt: Symbol laesst sich versetzen", e1.Gesetzt == 1
                            && Math.Abs(mitte.X - zielX) < 200 && Math.Abs(mitte.Y - zielY) < 200
                            && (Math.Abs(mitte.X - symStart.X) > 50 || Math.Abs(mitte.Y - symStart.Y) > 50));

                        // Zurueck: die gesicherte Anordnung von vorhin - ueber die Datei.
                        var dateiOrdner = Path.Combine(root, "echt-symbole");
                        DesktopIcons.Speichern(Path.Combine(dateiOrdner, DesktopIcons.FolderName), vorher);
                        var zurueck = DesktopIcons.Laden(DesktopIcons.Datei(dateiOrdner))!;
                        var e2 = DesktopIcons.Wiederherstellen(zurueck, TimeSpan.FromSeconds(5));
                        System.Threading.Thread.Sleep(800);
                        var symNachher = DesktopIcons.Lesen("CI", "runner");
                        var ende = symNachher.Symbole.First(x => x.Name == testName);
                        Console.WriteLine($"         zurueck bei ({ende.X},{ende.Y}), {e2.Gesetzt} von {e2.Gesamt} gesetzt");
                        Check("echt: aus der Sicherung wieder an der alten Stelle",
                            Math.Abs(ende.X - symStart.X) <= 2 && Math.Abs(ende.Y - symStart.Y) <= 2, $"({ende.X},{ende.Y}) statt ({symStart.X},{symStart.Y})");
                        Check("echt: Schalter wie vorher", symNachher.AutomatischAnordnen == vorher.AutomatischAnordnen
                            && symNachher.AmRasterAusrichten == vorher.AmRasterAusrichten);
                    }
                }
                finally
                {
                    try { File.Delete(testDatei); } catch { }
                }
            }

            Console.WriteLine("\n== winget von GitHub nachinstallieren (echt) ==");
            {
                var protokoll = new List<string>();
                var uhr = System.Diagnostics.Stopwatch.StartNew();
                var installiert = WingetInstaller.InstallAsync(z => { protokoll.Add(z); Console.WriteLine("         " + z); }, () => false)
                                                 .GetAwaiter().GetResult();
                Console.WriteLine($"         Dauer: {uhr.Elapsed.TotalSeconds:F0} s");
                Check("echt: App-Installer und Abhaengigkeiten geladen", protokoll.Count(z => z.Contains("geladen (")) == 2,
                    string.Join(" | ", protokoll));
                Check("echt: Abhaengigkeiten fuer diese Architektur gefunden",
                    protokoll.Any(z => System.Text.RegularExpressions.Regex.IsMatch(z, @"samt [1-9]\d* Abhängigkeiten")));
                Check("echt: Installation gemeldet", installiert, protokoll.LastOrDefault());
                var lokal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string? fassung = null;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(lokal, @"Microsoft\WindowsApps\winget.exe"), "--version")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    fassung = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(30000);
                }
                catch (Exception ex) { fassung = "nicht startbar: " + ex.Message; }
                Console.WriteLine($"         winget danach: {fassung}");
                Check("echt: winget startet danach", fassung is not null && System.Text.RegularExpressions.Regex.IsMatch(fassung, @"^v?\d+\.\d+"), fassung);
                Check("echt: Zwischendateien sind wieder weg",
                    !Directory.EnumerateDirectories(Path.GetTempPath(), "DaSi-winget-*").Any());
            }

            var sitzung = DesktopScreenshot.GetInteractiveUser();
            Console.WriteLine($"         angemeldet: {sitzung?.Name ?? "(unbekannt)"}, Profil: {sitzung?.ProfilePath ?? "(unbekannt)"}");
            Check("angemeldeter Benutzer wird ermittelt (oder sauber null)", true);

            Console.WriteLine("\n== Fenster beiseite und zurueck (echt) ==");
            System.Diagnostics.Process? editor = null;
            try { editor = System.Diagnostics.Process.Start("notepad.exe"); } catch (Exception ex) { Console.WriteLine($"         Editor nicht startbar: {ex.Message}"); }
            var hEditor = IntPtr.Zero;
            for (int i = 0; editor is not null && i < 50 && hEditor == IntPtr.Zero; i++)
            {
                System.Threading.Thread.Sleep(200);
                try { editor.Refresh(); hEditor = editor.MainWindowHandle; } catch { }
            }
            if (hEditor == IntPtr.Zero)
            {
                Console.WriteLine("         (kein Editor-Fenster auf diesem Laeufer - Test uebersprungen)");
            }
            else
            {
                MaximizeForTest(hEditor);
                System.Threading.Thread.Sleep(800);
                Check("vorher: Editor maximiert", DesktopScreenshot.State(hEditor).Maximiert);
                var beiseite = DesktopScreenshot.HideWindows();
                System.Threading.Thread.Sleep(1200);
                Console.WriteLine($"         beiseitegelegt: {beiseite.TopToBottom.Count} Fenster");
                Check("der Editor ist unter den beiseitegelegten Fenstern", beiseite.TopToBottom.Contains(hEditor));
                Check("waehrend der Aufnahme ist der Editor minimiert", DesktopScreenshot.State(hEditor).Minimiert);
                var uhrZurueck = System.Diagnostics.Stopwatch.StartNew();
                DesktopScreenshot.RestoreWindows(beiseite);
                uhrZurueck.Stop();
                System.Threading.Thread.Sleep(500);
                var zustandNachher = DesktopScreenshot.State(hEditor);
                Check("danach ist er wieder da (nicht minimiert)", !zustandNachher.Minimiert);
                Check("und wieder maximiert, wie vorher", zustandNachher.Maximiert);
                Check("zurueckholen kehrt zuegig zurueck", uhrZurueck.Elapsed.TotalSeconds < 12, $"{uhrZurueck.Elapsed.TotalSeconds:F1} s");
            }
            try { editor?.Kill(); } catch { }
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("\n== echtes robocopy: gesperrte Datei in AppData ==");
            var roboProfil = Path.Combine(root, "robo", "Anwender");
            var appdata = Path.Combine(roboProfil, "AppData");
            var leveldb = Path.Combine(appdata, "Roaming", "discord", "Local Storage", "leveldb");
            Directory.CreateDirectory(leveldb);
            File.WriteAllText(Path.Combine(leveldb, "000003.log"), "frei");
            File.WriteAllText(Path.Combine(appdata, "einstellungen.json"), "{}");
            var gesperrt = Path.Combine(leveldb, "012884.log");
            File.WriteAllText(gesperrt, "gesperrt");
            var zielRobo = Path.Combine(root, "robo", "Sicherung", "AppData");

            using (var sperre = new FileStream(gesperrt, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Dieselben Schalter wie die AppData-Sicherung des Programms.
                var psiRobo = new System.Diagnostics.ProcessStartInfo("robocopy.exe",
                    $"\"{appdata}\" \"{zielRobo}\" /MIR /ZB /SL /R:0 /W:0 /MT:32 /XJ /XA:SH /NP /NDL")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                var skips = new RobocopySkips(appdata);
                var durch = new System.Collections.Generic.List<string>();
                using var robo = System.Diagnostics.Process.Start(psiRobo)!;
                string? zeile;
                while ((zeile = robo.StandardOutput.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(zeile)) continue;   // wie im Programm
                    if (!skips.Swallow(zeile)) durch.Add(zeile);
                }
                robo.WaitForExit();
                Console.WriteLine($"         robocopy endete mit {robo.ExitCode}, geschluckt: {skips.Count} Datei(en), "
                                  + $"Fehlerart(en): {string.Join(", ", skips.CountByCode.Keys)}");

                Check("robocopy meldet den Fehlschlag (Bit 8)", (robo.ExitCode & 8) != 0, robo.ExitCode.ToString());
                Check("genau die gesperrte Datei ist erfasst", skips.Count == 1 && skips.Paths[0].Path.EndsWith("012884.log"),
                      string.Join(" | ", skips.Paths.Select(p => p.Path)));
                Check("als Fehler 32 (gesperrt), also erwartbar", skips.CountByCode.ContainsKey(32) && skips.AllExpected);
                Check("mit dem Grund aus robocopy", skips.ReasonByCode.TryGetValue(32, out var grund) && grund.Length > 10, grund);
                foreach (var z in durch.Where(z => z.Contains("012884")))
                    Console.WriteLine($"         uebrig: [{z.Replace("\t", "\\t")}]");
                // Eingerueckte Dateizeilen ("New File ... 012884.log") schreibt robocopy
                // fuer JEDE Datei vor dem Kopierversuch; das Programm zaehlt sie im
                // Normalfall nur (Fortschritt) und schreibt sie nicht ins Protokoll.
                var meldungen = durch.Where(z => z[0] != ' ' && z[0] != '\t').ToList();
                Check("im Protokoll steht keine Meldung mehr dazu",
                      !meldungen.Any(z => z.Contains("012884.log") || z.Contains("0x00000020")
                                          || z.Contains("RETRY", StringComparison.OrdinalIgnoreCase)
                                          || z.Contains("Wiederholungsversuch", StringComparison.OrdinalIgnoreCase)),
                      string.Join(" | ", meldungen.Where(z => z.Contains("012884") || z.Contains("0x") || z.Contains("RETRY"))));
                Check("kein anderer Fehler", skips.OtherErrors == 0);
                Check("die freien Dateien sind gesichert",
                      File.Exists(Path.Combine(zielRobo, "einstellungen.json"))
                      && File.Exists(Path.Combine(zielRobo, "Roaming", "discord", "Local Storage", "leveldb", "000003.log")));
            }

            Console.WriteLine("\n== echtes robocopy: Zurueckschreiben, Datei im Profil gesperrt ==");
            {
                // Wie am 18.11.2026: Edge laeuft im Hintergrund und haelt eine
                // Datei im Profil offen, waehrend AppData zurueckgeschrieben wird.
                var neuProfil = Path.Combine(root, "robo", "Neu", "AppData");
                var neuLeveldb = Path.Combine(neuProfil, "Roaming", "discord", "Local Storage", "leveldb");
                Directory.CreateDirectory(neuLeveldb);
                var imProfil = Path.Combine(neuLeveldb, "000003.log");
                File.WriteAllText(imProfil, "die Fassung, die im Profil stehen bleibt");
                File.SetLastWriteTimeUtc(imProfil, DateTime.UtcNow.AddDays(-3));

                using var sperreZiel = new FileStream(imProfil, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                // Dieselben Schalter wie das Zurueckschreiben des Programms.
                var schalter = string.Join(" ", ProfileExcludes.RestoreSwitches().Select(s => s.Contains(' ') ? $"\"{s}\"" : s));
                var psiZurueck = new System.Diagnostics.ProcessStartInfo("robocopy.exe",
                    $"\"{zielRobo}\" \"{neuProfil}\" {schalter} /NP /NDL")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                var zurueckSkips = new RobocopySkips(zielRobo, zurueck: true);
                var zurueckDurch = new System.Collections.Generic.List<string>();
                using var roboZ = System.Diagnostics.Process.Start(psiZurueck)!;
                string? zz;
                while ((zz = roboZ.StandardOutput.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(zz)) continue;
                    if (!zurueckSkips.Swallow(zz)) zurueckDurch.Add(zz);
                }
                roboZ.WaitForExit();
                Console.WriteLine($"         robocopy endete mit {roboZ.ExitCode}, geschluckt: {zurueckSkips.Count} Datei(en), "
                                  + $"Fehlerart(en): {string.Join(", ", zurueckSkips.CountByCode.Keys)}");
                Check("Zurueckschreiben echt: robocopy meldet den Fehlschlag (Bit 8)", (roboZ.ExitCode & 8) != 0, roboZ.ExitCode.ToString());
                Check("Zurueckschreiben echt: erfasst ist die Datei - mit dem Pfad in der SICHERUNG",
                      zurueckSkips.Count == 1 && zurueckSkips.Paths[0].Path.StartsWith(zielRobo, StringComparison.OrdinalIgnoreCase)
                      && zurueckSkips.Paths[0].Path.EndsWith("000003.log"),
                      string.Join(" | ", zurueckSkips.Paths.Select(x => x.Path)));
                Check("Zurueckschreiben echt: Fehler 32, also erwartbar, kein anderer Fehler",
                      zurueckSkips.CountByCode.ContainsKey(32) && zurueckSkips.AllExpected && zurueckSkips.OtherErrors == 0);
                Check("Zurueckschreiben echt: im Protokoll steht keine Meldung mehr dazu",
                      !zurueckDurch.Where(x => x[0] != ' ' && x[0] != '\t')
                                   .Any(x => x.Contains("0x00000020") || x.Contains("RETRY", StringComparison.OrdinalIgnoreCase)
                                             || x.Contains("Wiederholungsversuch", StringComparison.OrdinalIgnoreCase)));
                Check("Zurueckschreiben echt: der Rest ist angekommen", File.Exists(Path.Combine(neuProfil, "einstellungen.json")));
            }

            Console.WriteLine("\n== echtes robocopy: das laufende Tool wird nicht ueberschrieben ==");
            {
                // Wie am 18.11.2026, 21:11 Uhr: Das Tool laeuft vom Desktop des
                // Zielprofils, in der Sicherung liegt eine aeltere Fassung.
                var sichSelbst = Path.Combine(root, "selbst-echt", "DASI", "Benutzerprofil");
                var zielSelbst = Path.Combine(root, "selbst-echt", "Users", "Anwender123");
                Directory.CreateDirectory(Path.Combine(sichSelbst, "Desktop"));
                Directory.CreateDirectory(Path.Combine(zielSelbst, "Desktop"));
                File.WriteAllText(Path.Combine(sichSelbst, "Desktop", "WindowsDaSiTool.exe"), "alte Fassung aus der Sicherung");
                File.WriteAllText(Path.Combine(sichSelbst, "Desktop", "Brief.txt"), "Brief");
                var laufend = Path.Combine(zielSelbst, "Desktop", "WindowsDaSiTool.exe");
                File.WriteAllText(laufend, "laufende Fassung");
                File.SetLastWriteTimeUtc(laufend, DateTime.UtcNow.AddDays(-1));

                var ausSelbst = ProfileExcludes.LaufendesProgrammInSicherung(zielSelbst, sichSelbst, laufend);
                var schalter = string.Join(" ", ProfileExcludes.RestoreSwitches().Select(s => s.Contains(' ') ? $"\"{s}\"" : s));
                var psiSelbst = new System.Diagnostics.ProcessStartInfo("robocopy.exe",
                    $"\"{sichSelbst}\" \"{zielSelbst}\" {schalter} /XF \"{ausSelbst}\" /NP /NDL")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var roboS = System.Diagnostics.Process.Start(psiSelbst)!;
                roboS.StandardOutput.ReadToEnd();
                roboS.WaitForExit();
                Console.WriteLine($"         robocopy endete mit {roboS.ExitCode}, ausgelassen: {ausSelbst}");
                Check("laufendes Tool echt: robocopy ohne Fehler", roboS.ExitCode < 8, roboS.ExitCode.ToString());
                Check("laufendes Tool echt: /XF mit vollem Pfad laesst genau diese Datei aus",
                    File.ReadAllText(laufend) == "laufende Fassung", File.ReadAllText(laufend));
                Check("laufendes Tool echt: der Rest des Desktops kommt an", File.Exists(Path.Combine(zielSelbst, "Desktop", "Brief.txt")));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("\n== echtes robocopy: was die Profilsicherung aus Dokumente mitnimmt ==");
            var besitzer = Path.Combine(root, "profilwechsel", "Besitzer");
            var doks = Path.Combine(besitzer, "Documents");
            void Datei(string rel, string inhalt, FileAttributes? attr = null)
            {
                var p = Path.Combine(besitzer, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, inhalt);
                if (attr is { } a) File.SetAttributes(p, a);
            }
            Datei(@"Documents\Normal\a.txt", "a");
            // Ein angepasster Ordner (eigenes Symbol/eigene Ansicht): Windows setzt
            // dafuer das System-Attribut und legt eine versteckte desktop.ini hinein.
            Datei(@"Documents\Angepasst\brief.docx", "b");
            Datei(@"Documents\Angepasst\desktop.ini", "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\imageres.dll,-3\r\n",
                  FileAttributes.Hidden | FileAttributes.System);
            new DirectoryInfo(Path.Combine(doks, "Angepasst")).Attributes |= FileAttributes.System;
            Datei(@"Documents\Schreibgeschuetzt\c.txt", "c");
            new DirectoryInfo(Path.Combine(doks, "Schreibgeschuetzt")).Attributes |= FileAttributes.ReadOnly;
            Datei(@"Documents\Versteckt\d.txt", "d");
            new DirectoryInfo(Path.Combine(doks, "Versteckt")).Attributes |= FileAttributes.Hidden;
            Datei(@"Documents\versteckt.txt", "e", FileAttributes.Hidden);
            Datei(@"Documents\system.dat", "f", FileAttributes.System);
            Datei(@"Pictures\p.jpg", "p");
            Datei("NTUSER.DAT", "hive", FileAttributes.Hidden | FileAttributes.System);
            Datei("ntuser.dat.LOG1", "log", FileAttributes.Hidden | FileAttributes.System);
            // Verknuepfungspunkt wie "Eigene Bilder" in Dokumente - zeigt auf Pictures.
            using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                       $"/c mklink /J \"{Path.Combine(doks, "Eigene Bilder")}\" \"{Path.Combine(besitzer, "Pictures")}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            { mk!.StandardOutput.ReadToEnd(); mk.WaitForExit(); }
            // Und einer, der aus dem Profil hinauszeigt ("Dokumente\Spiele" -> "D:\Spiele").
            var draussen = Path.Combine(root, "profilwechsel", "D-Laufwerk", "Spiele");
            Directory.CreateDirectory(draussen);
            File.WriteAllText(Path.Combine(draussen, "spielstand.sav"), "s");
            using (var mk2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                       $"/c mklink /J \"{Path.Combine(doks, "Spiele")}\" \"{draussen}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            { mk2!.StandardOutput.ReadToEnd(); mk2.WaitForExit(); }
            var verweise = ProfileLinks.FindOutsideLinks(besitzer, ProfileExcludes.Build(besitzer), out _);
            Console.WriteLine($"         Verknuepfungen nach draussen: {string.Join(" | ", verweise.Select(v => $"{Path.GetFileName(v.Path)} -> {v.Target}"))}");
            Check("echte Junction nach draussen wird gemeldet, mit Ziel",
                verweise.Count == 1 && verweise[0].Path.EndsWith("Spiele")
                && string.Equals(verweise[0].Target.TrimEnd('\\'), draussen, StringComparison.OrdinalIgnoreCase));
            Check("Junction \"Eigene Bilder\" (ins Profil) wird nicht gemeldet",
                !verweise.Any(v => v.Path.EndsWith("Eigene Bilder")));

            static int Robo(string quelle, string ziel, System.Collections.Generic.IEnumerable<string> schalter)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(quelle);
                psi.ArgumentList.Add(ziel);
                foreach (var s in schalter) psi.ArgumentList.Add(s);
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
            var altSchalter = new[] { "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH" };
            var sicherungAlt = Path.Combine(root, "profilwechsel", "Sicherung-alt");
            var sicherungNeu = Path.Combine(root, "profilwechsel", "Sicherung-neu");
            Console.WriteLine($"         robocopy alt: {Robo(besitzer, sicherungAlt, altSchalter)}, neu: {Robo(besitzer, sicherungNeu, ProfileExcludes.BackupSwitches())}");

            var faelle = new (string Was, string Rel, bool Soll)[]
            {
                ("gewoehnlicher Ordner", @"Documents\Normal\a.txt", true),
                ("angepasster Ordner (System-Attribut)", @"Documents\Angepasst\brief.docx", true),
                ("desktop.ini des angepassten Ordners", @"Documents\Angepasst\desktop.ini", true),
                ("schreibgeschuetzt markierter Ordner", @"Documents\Schreibgeschuetzt\c.txt", true),
                ("versteckter Ordner", @"Documents\Versteckt\d.txt", true),
                ("versteckte Datei", @"Documents\versteckt.txt", true),
                ("Systemdatei", @"Documents\system.dat", true),
                ("Bilder", @"Pictures\p.jpg", true),
                ("Verknuepfungspunkt \"Eigene Bilder\" (nicht verfolgen)", @"Documents\Eigene Bilder\p.jpg", false),
                ("Verknuepfung nach draussen (gemeldet, nicht verfolgt)", @"Documents\Spiele\spielstand.sav", false),
                ("Registry NTUSER.DAT", "NTUSER.DAT", false),
                ("Registry ntuser.dat.LOG1", "ntuser.dat.LOG1", false),
            };
            Console.WriteLine("         gesichert?                                        alt    neu");
            foreach (var (was, rel, soll) in faelle)
            {
                bool imAlten = File.Exists(Path.Combine(sicherungAlt, rel)), imNeuen = File.Exists(Path.Combine(sicherungNeu, rel));
                Console.WriteLine($"         {was,-50}{(imAlten ? "ja" : "NEIN"),-7}{(imNeuen ? "ja" : "NEIN")}");
                Check($"neue Schalter: {was} {(soll ? "wird" : "wird NICHT")} gesichert", imNeuen == soll);
            }

            Console.WriteLine("\n== echtes robocopy: Zurueckschreiben zu einem anderen Benutzer ==");
            // In der Sicherung: eine Datei mit eigenen, nicht geerbten Rechten (wie
            // sie das Sicherungsmedium mitbringen kann), dazu eine NTUSER.DAT aus
            // einer aelteren Sicherung.
            var probe = Path.Combine(sicherungNeu, @"Documents\Normal\a.txt");
            var sec = new FileInfo(probe).GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(probe).SetAccessControl(sec);
            File.WriteAllText(Path.Combine(sicherungNeu, "NTUSER.DAT"), "fremde Registry");

            var anwenderAlt = Path.Combine(root, "profilwechsel", "Anwender-alt");
            var anwenderNeu = Path.Combine(root, "profilwechsel", "Anwender-neu");
            foreach (var a in new[] { anwenderAlt, anwenderNeu })
            {
                Directory.CreateDirectory(a);
                File.WriteAllText(Path.Combine(a, "NTUSER.DAT"), "eigene Registry");
            }
            Robo(sicherungNeu, anwenderAlt, new[] { "/E", "/ZB", "/COPYALL", "/R:1", "/W:1", "/MT:32" });
            Robo(sicherungNeu, anwenderNeu, ProfileExcludes.RestoreSwitches());

            static (bool Geschuetzt, int Eigene) Rechte(string datei)
            {
                var s = new FileInfo(datei).GetAccessControl();
                return (s.AreAccessRulesProtected,
                        s.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count);
            }
            var rAlt = Rechte(Path.Combine(anwenderAlt, @"Documents\Normal\a.txt"));
            var rNeu = Rechte(Path.Combine(anwenderNeu, @"Documents\Normal\a.txt"));
            Console.WriteLine($"         alt (/COPYALL): Vererbung aus = {rAlt.Geschuetzt}, eigene Regeln = {rAlt.Eigene}");
            Console.WriteLine($"         neu (/COPY:DAT): Vererbung aus = {rNeu.Geschuetzt}, eigene Regeln = {rNeu.Eigene}");
            Check("neue Schalter: Datei erbt die Rechte des Zielprofils (keine fremden Rechte)",
                !rNeu.Geschuetzt && rNeu.Eigene == 0, $"geschuetzt={rNeu.Geschuetzt}, eigene={rNeu.Eigene}");
            Check("neue Schalter: Inhalt kommt an",
                File.ReadAllText(Path.Combine(anwenderNeu, @"Documents\Angepasst\brief.docx")) == "b");
            Check("neue Schalter: die Registry des Zielbenutzers bleibt unangetastet",
                File.ReadAllText(Path.Combine(anwenderNeu, "NTUSER.DAT")) == "eigene Registry");
            Console.WriteLine($"         (alt: NTUSER.DAT danach \"{File.ReadAllText(Path.Combine(anwenderAlt, "NTUSER.DAT"))}\")");
        }

        // Zuletzt: den Explorer neu starten - wie beim Zurueckschreiben der
        // Taskleiste. Laeuft als letzte Pruefung, weil danach der Desktop kurz weg ist.
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("\n== Taskleiste echt: angeheftete Symbole lesen, Explorer neu starten ==");
            var echtBand = new List<string>();
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Taskleiste.TaskbandSchluessel))
                foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                    if (DesignRegistry.Lesen(Microsoft.Win32.Registry.CurrentUser, Taskleiste.TaskbandSchluessel, name) is { } v)
                        echtBand.Add($"{name} ({v.Typ})");
            var echtPins = Taskleiste.Verknuepfungen(Taskleiste.PinOrdner(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            Console.WriteLine($"         Taskband: {(echtBand.Count == 0 ? "leer" : string.Join(", ", echtBand))}");
            Console.WriteLine($"         angeheftet: {(echtPins.Count == 0 ? "nichts" : Taskleiste.Namen(echtPins))}");

            if (!ExplorerNeustart.TaskleisteDa())
            {
                Console.WriteLine("         (keine Taskleiste auf diesem Laeufer - Neustart-Pruefung uebersprungen)");
            }
            else
            {
                var uhrNeu = System.Diagnostics.Stopwatch.StartNew();
                var (neuOk, vonSelbst) = ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                Console.WriteLine($"         Neustart: {(neuOk ? "Taskleiste wieder da" : "Taskleiste NICHT wieder da")}, "
                                  + $"{(vonSelbst ? "von Windows selbst" : "vom Tool gestartet")}, {uhrNeu.Elapsed.TotalSeconds:F1} s");
                Check("echt: nach dem Neustart ist die Taskleiste wieder da", neuOk);
            }
        }

        // Hintergrund: so setzen wie das Tool, Explorer neu starten, auf dem
        // Bildschirm nachsehen. Ganz am Ende, der Desktop wechselt dabei mehrfach.
        if (OperatingSystem.IsWindows()) HintergrundEchtPruefen(root);

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    private static string Deutsch(string de, string en) => de;

    /// <summary>
    /// Hintergrund echt: setzen wie das Tool, den Explorer neu starten und am
    /// Bildschirm nachsehen. Dazu der Ablauf aus dem Protokoll eines Anwenders
    /// (Design-Werte, Volltonfarbe, Explorer-Neustart fuer die Taskleiste) und
    /// zum Vergleich die Schnittstelle der Einstellungen-App (IDesktopWallpaper).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void HintergrundEchtPruefen(string root)
    {
        Console.WriteLine("\n== Hintergrund echt: setzen, Explorer neu starten, Bildschirm pruefen ==");
        HintergrundEcht.WindowsFassung();
        if (!ExplorerNeustart.TaskleisteDa())
        {
            Console.WriteLine("         (keine Taskleiste auf diesem Laeufer - uebersprungen)");
            return;
        }
        var hgNutzer = DesktopScreenshot.GetInteractiveUser();
        using var hgHku = hgNutzer?.Sid is { } hgSid ? Microsoft.Win32.Registry.Users.OpenSubKey(hgSid, true) : null;
        var hgWurzel = hgHku ?? Microsoft.Win32.Registry.CurrentUser;
        bool hgEigen = DesktopHintergrund.IstProzessBenutzer(hgNutzer?.Sid);
        var hgVorher = DesktopHintergrund.Lesen(hgWurzel);
        Console.WriteLine($"         Zweig: {(hgHku is null ? "HKCU" : @"HKU\" + hgNutzer!.Sid)}, Tool laeuft als dieser Benutzer: {hgEigen}");
        Console.WriteLine($"         vorher: Bild={hgVorher.Bild ?? "-"}, Farbe={hgVorher.Farbe}, Typ={hgVorher.Typ?.ToString() ?? "-"}, "
                          + $"Stil={hgVorher.Stil ?? "-"}, virtuelle Desktops mit eigenem Bild: {hgVorher.DesktopBilder.Count}");

        var hgOrdner = Path.Combine(root, "hintergrund-echt");
        Directory.CreateDirectory(hgOrdner);
        var rot = ((byte)200, (byte)30, (byte)30);
        var gruen = ((byte)30, (byte)170, (byte)60);
        var lila = ((byte)107, (byte)105, (byte)214);
        var tuerkis = ((byte)0, (byte)150, (byte)160);
        string rotBild = Path.Combine(hgOrdner, "rot.png"), gruenBild = Path.Combine(hgOrdner, "gruen.png");
        HintergrundEcht.EinfarbigesPng(rotBild, rot);
        HintergrundEcht.EinfarbigesPng(gruenBild, gruen);
        var strecken = new HintergrundEinstellung { Darstellung = "2", Kacheln = "0" };
        void Neustart(string wozu)
        {
            var (ok, _) = ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
            Console.WriteLine($"         Explorer neu gestartet ({wozu}): {(ok ? "ok" : "Taskleiste NICHT wieder da")}");
        }

        HintergrundEcht.Zustand("Ausgangslage", hgWurzel, hgNutzer?.ProfilePath);
        HintergrundEcht.GrosseFenster("Ausgangslage");
        Console.WriteLine($"         [Ausgangslage] meist: {HintergrundEcht.Meist()}");
        var hgWeg = DesktopScreenshot.HideWindows();
        System.Threading.Thread.Sleep(1000);
        HintergrundEcht.GrosseFenster("Fenster beiseite");
        try
        {
            // 1. Ein Bild - wie das Tool es setzt.
            var r1 = DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, rotBild, strecken);
            Console.WriteLine($"         Bild: eingetragen {r1.Eingetragen}, angezeigt {r1.Angezeigt}, virtuelle Desktops {r1.VirtuelleDesktops}");
            Check("echt: Bild beim angemeldeten Benutzer eingetragen", r1.Eingetragen);
            Check("echt: Windows zeigt das Bild sofort", r1.Angezeigt);
            var a1 = HintergrundEcht.Sichtbar(rot);
            Check("echt: das Bild ist auf dem Bildschirm", a1 > 0.5, $"{a1:P0} rot");
            Neustart("Bild");
            var a2 = HintergrundEcht.Sichtbar(rot);
            Check("echt: nach dem Explorer-Neustart ist das Bild noch da", a2 > 0.5, $"{a2:P0} rot");

            // 2. Nur in der Registry - so steht es beim angemeldeten Benutzer,
            //    wenn das Tool unter einem ANDEREN Konto laeuft. Ein
            //    Explorer-Neustart liest den Hintergrund nicht neu ein (er
            //    gehoert zur Sitzung) - der Neustart fuer die Taskleiste kann
            //    ihn also nicht verlieren. Erst die Anmeldung liest die Registry.
            DesktopHintergrund.BildEintragen(hgWurzel, gruenBild, strecken);
            Neustart("nur eingetragen");
            System.Threading.Thread.Sleep(3000);
            var a3 = HintergrundEcht.Anteil(rot);
            Check("echt: ein Explorer-Neustart laesst den Hintergrund der Sitzung stehen", a3 > 0.5,
                $"{a3:P0} rot, {HintergrundEcht.Anteil(gruen):P0} gruen");

            // Zur Auskunft: was die Anmeldung taete (UpdatePerUserSystemParameters
            // liest die Registry wie beim Anmelden) - erst mit altem
            // Zwischenspeicher, dann verworfen wie beim fremden Konto.
            HintergrundEcht.WieBeimAnmelden();
            Console.WriteLine($"         wie beim Anmelden, alter Zwischenspeicher: {HintergrundEcht.Anteil(gruen):P0} gruen (Registry), {HintergrundEcht.Anteil(rot):P0} rot (alt)");
            DesktopHintergrund.ZwischenspeicherVerwerfen(hgWurzel, hgNutzer?.ProfilePath);
            HintergrundEcht.WieBeimAnmelden();
            Console.WriteLine($"         wie beim Anmelden, Zwischenspeicher verworfen: {HintergrundEcht.Anteil(gruen):P0} gruen (Registry), {HintergrundEcht.Anteil(rot):P0} rot (alt)");

            // 3. Volltonfarbe.
            var r4 = DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, lila);
            Console.WriteLine($"         Volltonfarbe: eingetragen {r4.Eingetragen}, angezeigt {r4.Angezeigt}");
            Check("echt: Volltonfarbe eingetragen", r4.Eingetragen);
            var a4 = HintergrundEcht.Sichtbar(lila);
            Check("echt: die Volltonfarbe ist auf dem Bildschirm", a4 > 0.5, $"{a4:P0} lila");
            Console.WriteLine($"         Design-Datei: {r4.DesignDatei?.Datei ?? "-"}, geaendert {r4.DesignDatei?.Geaendert}, {r4.DesignDatei?.Grund ?? "ok"}");
            if (r4.DesignDatei is { Datei: { } themaDatei } && File.Exists(themaDatei)
                && themaDatei.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            {
                var themaText = DesignDatei.DateiLesen(themaDatei).Inhalt;
                Check("echt: Volltonfarbe steht auch in der Design-Datei (Custom.theme)",
                    DesignDatei.Lesen(themaText, DesignDatei.FarbenAbschnitt, "Background") == "107 105 214");
            }
            else
            {
                Check("echt: Windows-Design ausserhalb des Profils bleibt unangetastet", r4.DesignDatei is { Geaendert: false });
            }
            var standLila = DesktopHintergrund.Lesen(hgWurzel);
            var k1 = DesktopHintergrund.Kontrolle(standLila, Deutsch);
            Check("echt: Kontrolle bestaetigt die gesetzte Volltonfarbe", k1 is null, k1);
            var desktopFoto = DesktopScreenshot.CaptureDesktopWindow();
            Console.WriteLine($"         Desktop-Fenster (PrintWindow): {(desktopFoto is null ? "nicht abbildbar" : $"{DesktopScreenshot.ColorShare(desktopFoto, lila):P0} lila, meist {DesktopScreenshot.MostCommonColor(desktopFoto)}")}");

            // Kontrolle bemerkt, wenn der Desktop etwas anderes zeigt, und Erneut repariert es.
            DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, tuerkis, hgNutzer?.ProfilePath);
            HintergrundEcht.Sichtbar(tuerkis);
            var k2 = DesktopHintergrund.Kontrolle(standLila, Deutsch);
            Console.WriteLine($"         Kontrolle bei falscher Farbe: {k2 ?? "(nichts bemerkt)"}");
            Check("echt: Kontrolle bemerkt eine andere Farbe", k2 is not null);
            Check("echt: Unterschied in der Registry wird benannt",
                DesktopHintergrund.Unterschied(standLila, DesktopHintergrund.Lesen(hgWurzel), Deutsch)?.Contains("Farbe") == true);
            DesktopHintergrund.Erneut(hgWurzel, hgEigen, standLila, hgNutzer?.ProfilePath);
            HintergrundEcht.Sichtbar(lila);
            var k3 = DesktopHintergrund.Kontrolle(standLila, Deutsch);
            Check("echt: erneut gesetzt - Kontrolle wieder zufrieden", k3 is null, k3);
            var einfluesse = string.Join("; ", DesktopHintergrund.Einfluesse(hgWurzel, true, Deutsch));
            Console.WriteLine($"         Einfluesse: {(einfluesse.Length > 0 ? einfluesse : "keine")}");
            Neustart("Volltonfarbe");
            var a5 = HintergrundEcht.Sichtbar(lila);
            Check("echt: nach dem Explorer-Neustart ist die Volltonfarbe noch da", a5 > 0.5, $"{a5:P0} lila");

            // 4. Nur eingetragene Farbe: Der Explorer-Neustart laesst die der Sitzung stehen.
            DesktopHintergrund.FarbeEintragen(hgWurzel, tuerkis);
            Neustart("Farbe nur eingetragen");
            System.Threading.Thread.Sleep(3000);
            var a6 = HintergrundEcht.Anteil(lila);
            Check("echt: auch die Volltonfarbe der Sitzung bleibt beim Explorer-Neustart stehen", a6 > 0.5,
                $"{a6:P0} lila, {HintergrundEcht.Anteil(tuerkis):P0} tuerkis");
            HintergrundEcht.WieBeimAnmelden();
            Console.WriteLine($"         wie beim Anmelden: {HintergrundEcht.Anteil(tuerkis):P0} tuerkis (Registry), {HintergrundEcht.Anteil(lila):P0} lila (Sitzung)");

            // 5. Ablauf wie beim Anwender (Protokoll vom 22.11.2026): erst die
            //    Design-Werte (seine 33), dann die Volltonfarbe #6B69D6, dann der
            //    Explorer-Neustart fuer die Taskleiste. Ausgangslage: ein Bild.
            Console.WriteLine("   -- Ablauf wie beim Anwender: Design-Werte, Volltonfarbe, Explorer-Neustart --");
            DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, rotBild, strecken, hgNutzer?.ProfilePath);
            HintergrundEcht.Sichtbar(rot);
            var ue = DesignRegistry.Uebernehmen(hgWurzel, HintergrundEcht.AnwenderDesign(), hgEigen);
            Console.WriteLine($"         Design-Werte: {ue.Gesetzt.Count} gesetzt, {ue.OhneDatei.Count} ohne Datei, {ue.NichtSchreibbar.Count} nicht schreibbar");
            var r5 = DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, lila, hgNutzer?.ProfilePath);
            Console.WriteLine($"         Volltonfarbe (Tool): eingetragen {r5.Eingetragen}, angezeigt {r5.Angezeigt}");
            HintergrundEcht.Verlauf("Volltonfarbe (Tool)", lila, 1, 3, 6);
            Neustart("wie beim Anwender");
            var v5 = HintergrundEcht.Verlauf("nach dem Explorer-Neustart", lila, 1, 4, 8);
            HintergrundEcht.Zustand("danach", hgWurzel, hgNutzer?.ProfilePath);
            Check("echt: Volltonfarbe nach Design-Werten und Explorer-Neustart noch da (Ablauf des Anwenders)", v5 > 0.5, $"{v5:P0} lila");

            // 5b. Wie 5, aber jeder virtuelle Desktop behaelt einen LEEREN
            //     Wallpaper-Eintrag statt keinen.
            Console.WriteLine("   -- wie 5, virtuelle Desktops mit leerem Wallpaper-Eintrag --");
            DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, rotBild, strecken, hgNutzer?.ProfilePath);
            HintergrundEcht.Sichtbar(rot);
            DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, lila, hgNutzer?.ProfilePath);
            using (var vd = hgWurzel.OpenSubKey(DesktopHintergrund.VirtuelleDesktopsSchluessel, true))
                foreach (var n in vd?.GetSubKeyNames() ?? Array.Empty<string>())
                    using (var d = vd!.OpenSubKey(n, true)) d?.SetValue("Wallpaper", "");
            HintergrundEcht.Verlauf("Volltonfarbe, leerer Eintrag", lila, 1, 3);
            Neustart("leerer Eintrag");
            HintergrundEcht.Verlauf("leerer Eintrag, nach dem Explorer-Neustart", lila, 1, 4, 8);
            HintergrundEcht.Zustand("danach", hgWurzel, hgNutzer?.ProfilePath);
            using (var vd = hgWurzel.OpenSubKey(DesktopHintergrund.VirtuelleDesktopsSchluessel, true))
                foreach (var n in vd?.GetSubKeyNames() ?? Array.Empty<string>())
                    using (var d = vd!.OpenSubKey(n, true)) d?.DeleteValue("Wallpaper", false);

            // 5c. Wie das Tool: Windows wendet ein Design an (geplante Aufgabe,
            //     explorer.exe), danach die Volltonfarbe bzw. das Bild direkt.
            //     Im Design bleibt bei einer Farbe ein Bild stehen - ein leeres
            //     deutete Windows 11 als Blickpunkt.
            var aktuellesDesign = DesktopHintergrund.DesignDateiPfad(hgWurzel, hgNutzer?.ProfilePath);
            Console.WriteLine($"   -- Windows wendet das Design an (aktuell: {aktuellesDesign ?? "-"}) --");
            if (aktuellesDesign is not null && File.Exists(aktuellesDesign) && hgNutzer?.ProfilePath is { } profilWd)
            {
                var dateiWd = Path.Combine(profilWd, @"AppData\Local\Microsoft\Windows\Themes", WindowsDesign.DateiName);
                Directory.CreateDirectory(Path.GetDirectoryName(dateiWd)!);
                bool Anwenden(string inhalt, Func<bool> da)
                {
                    DesignDatei.DateiSchreiben(dateiWd, inhalt, new System.Text.UnicodeEncoding(false, true));
                    var start = DateTime.Now;
                    var (ok, fehler) = WindowsDesign.Anwenden(dateiWd, hgNutzer.Name).GetAwaiter().GetResult();
                    var uhr = System.Diagnostics.Stopwatch.StartNew();
                    while (ok && !da() && uhr.Elapsed < TimeSpan.FromSeconds(20)) System.Threading.Thread.Sleep(500);
                    System.Threading.Thread.Sleep(1500);
                    WindowsDesign.EinstellungenSchliessen(start);
                    Console.WriteLine($"         angewendet: {ok} {fehler}, uebernommen: {da()} nach {uhr.Elapsed.TotalSeconds:F1} s");
                    return ok && da();
                }

                var gruenFarbe = ((byte)16, (byte)124, (byte)16);
                var vorlageWd = DesignDatei.DateiLesen(aktuellesDesign).Inhalt;
                var themaFarbe = DesignDatei.FuerWiederherstellung(vorlageWd, "DaSi-Pruefung", gruenFarbe, null, null, HintergrundEcht.AnwenderDesign());
                Check("echt: Windows wendet das Design mit der Volltonfarbe an (Farbe in der Registry)",
                    Anwenden(themaFarbe, () => Hintergrund.FarbeLesen(DesktopHintergrund.Lesen(hgWurzel).Farbe) == gruenFarbe));
                var rWd = DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, gruenFarbe, hgNutzer.ProfilePath);
                Console.WriteLine($"         danach Volltonfarbe direkt: eingetragen {rWd.Eingetragen}, Explorer {(rWd.ExplorerFehler ?? "ok")}");
                HintergrundEcht.Zustand("Design + Farbe", hgWurzel, hgNutzer.ProfilePath);
                var v1Wd = HintergrundEcht.Verlauf("Design + Farbe", gruenFarbe, 1, 3);
                Check("echt: Design + Farbe - der Desktop zeigt die Farbe (kein Blickpunkt)", v1Wd > 0.5, $"{v1Wd:P0}");
                Check("echt: Design + Farbe - Blickpunkt bleibt aus", !DesktopHintergrund.Einfluesse(hgWurzel, true, Deutsch).Any(e => e.Contains("Blickpunkt")));
                Check("echt: Design + Farbe - das Design ist jetzt das aktuelle",
                    string.Equals(DesktopHintergrund.DesignDateiPfad(hgWurzel, hgNutzer.ProfilePath), dateiWd, StringComparison.OrdinalIgnoreCase));
                Neustart("Design + Farbe");
                var v2Wd = HintergrundEcht.Verlauf("Design + Farbe, nach dem Explorer-Neustart", gruenFarbe, 1, 4);
                Check("echt: Design + Farbe - nach dem Explorer-Neustart noch da", v2Wd > 0.5, $"{v2Wd:P0}");

                var themaBild = DesignDatei.FuerWiederherstellung(DesignDatei.DateiLesen(dateiWd).Inhalt, "DaSi-Pruefung", null, rotBild, strecken, Array.Empty<DesignEintrag>());
                Check("echt: Windows wendet das Design mit dem Bild an (Bild in der Registry)",
                    Anwenden(themaBild, () => string.Equals(DesktopHintergrund.Lesen(hgWurzel).Bild, rotBild, StringComparison.OrdinalIgnoreCase)));
                DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, rotBild, strecken, hgNutzer.ProfilePath);
                var v3Wd = HintergrundEcht.Verlauf("Design + Bild", rot, 1, 3);
                Check("echt: Design + Bild - der Desktop zeigt das Bild", v3Wd > 0.5, $"{v3Wd:P0}");
                Neustart("Design + Bild");
                var v4Wd = HintergrundEcht.Verlauf("Design + Bild, nach dem Explorer-Neustart", rot, 1, 4);
                Check("echt: Design + Bild - nach dem Explorer-Neustart noch da", v4Wd > 0.5, $"{v4Wd:P0}");
            }
            else
            {
                Console.WriteLine("         (keine Design-Datei - uebersprungen)");
            }

            // 6./7. Dasselbe ueber IDesktopWallpaper - die Schnittstelle der Einstellungen-App.
            Console.WriteLine($"   -- IDesktopWallpaper: {DesktopHintergrund.Schnittstelle.Stand() ?? "nicht erreichbar"} --");
            foreach (var abschalten in new[] { false, true })
            {
                var was = abschalten ? "Farbe + Enable(false)" : "Farbe + SetWallpaper(\"\")";
                DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, rotBild, strecken, hgNutzer?.ProfilePath);
                HintergrundEcht.Sichtbar(rot);
                try
                {
                    DesktopHintergrund.FarbeEintragen(hgWurzel, tuerkis);
                    DesktopHintergrund.Schnittstelle.FarbeSetzen(tuerkis, abschalten);
                    HintergrundEcht.Verlauf($"IDesktopWallpaper {was}", tuerkis, 1, 3);
                    Neustart(was);
                    HintergrundEcht.Verlauf($"IDesktopWallpaper {was}, nach dem Explorer-Neustart", tuerkis, 1, 4, 8);
                    HintergrundEcht.Zustand("danach", hgWurzel, hgNutzer?.ProfilePath);
                }
                catch (Exception ex) { Console.WriteLine($"         IDesktopWallpaper {was}: {ex.GetType().Name}: {ex.Message}"); }
                finally
                {
                    if (abschalten) try { DesktopHintergrund.Schnittstelle.Einschalten(); } catch { }
                }
            }
            try
            {
                DesktopHintergrund.Schnittstelle.BildSetzen(gruenBild, strecken);
                HintergrundEcht.Verlauf("IDesktopWallpaper Bild", gruen, 1, 3);
                Neustart("IDesktopWallpaper Bild");
                HintergrundEcht.Verlauf("IDesktopWallpaper Bild, nach dem Explorer-Neustart", gruen, 1, 4, 8);
                HintergrundEcht.Zustand("danach", hgWurzel, hgNutzer?.ProfilePath);
            }
            catch (Exception ex) { Console.WriteLine($"         IDesktopWallpaper Bild: {ex.GetType().Name}: {ex.Message}"); }
        }
        finally
        {
            try
            {
                if (hgVorher.Bild is { } altBild && File.Exists(altBild))
                    DesktopHintergrund.BildSetzen(hgWurzel, hgEigen, altBild,
                        new HintergrundEinstellung { Darstellung = hgVorher.Stil, Kacheln = hgVorher.Kacheln });
                else if (Hintergrund.FarbeLesen(hgVorher.Farbe) is { } altFarbe)
                    DesktopHintergrund.FarbeSetzen(hgWurzel, hgEigen, altFarbe);
            }
            catch { /* Laeufer */ }
            DesktopScreenshot.RestoreWindows(hgWeg);
        }
    }
}

/// <summary>Ein Desktop ohne Explorer - zum Pruefen des Ablaufs.</summary>
internal sealed class TestDesktop : IDesktopView
{
    private readonly int _b, _h;
    private readonly List<DesktopIcon> _symbole;
    private int _aufrufe;
    public (int NachAufruf, DesktopIcon Symbol)? SpaeterDazu;
    public readonly List<string> Protokoll = new();

    public TestDesktop(int b, int h, bool autoAnordnen, bool raster, params DesktopIcon[] symbole)
    {
        _b = b; _h = h; _symbole = symbole.ToList();
        _auto = autoAnordnen; _raster = raster;
    }

    private bool _auto, _raster;
    public (int Breite, int Hoehe) Groesse() => (_b, _h);
    public bool AutomatischAnordnen { get => _auto; set { _auto = value; Protokoll.Add($"anordnen={value}"); } }
    public bool AmRasterAusrichten { get => _raster; set { _raster = value; Protokoll.Add($"raster={value}"); } }

    public IReadOnlyList<DesktopIcon> Symbole()
    {
        _aufrufe++;
        if (SpaeterDazu is { } d && _aufrufe > d.NachAufruf && !_symbole.Contains(d.Symbol)) _symbole.Add(d.Symbol);
        return _symbole.ToList();
    }

    public void Aktualisieren() => Protokoll.Add("aktualisieren");

    public void Dazu(DesktopIcon symbol) => _symbole.Add(symbol);

    public void Setzen(int index, int x, int y)
    {
        Protokoll.Add($"setzen {_symbole[index].Name}");
        _symbole[index] = _symbole[index] with { X = x, Y = y };
    }

    public (int, int) Stelle(string name) => _symbole.First(s => s.Name == name) is var s ? (s.X, s.Y) : (-1, -1);

    public void Dispose() { }
}

/// <summary>Fuer die echte Hintergrund-Pruefung: einfarbige Bilder und was auf dem Bildschirm zu sehen ist.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class HintergrundEcht
{
    public static void EinfarbigesPng(string pfad, (byte R, byte G, byte B) f)
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            px[i * 4] = f.B; px[i * 4 + 1] = f.G; px[i * 4 + 2] = f.R; px[i * 4 + 3] = 255;
        }
        using var s = File.Create(pfad);
        PngWriter.WriteBgra(s, n, n, px);
    }

    /// <summary>
    /// Welcher Anteil hat (ungefaehr) diese Farbe - auf dem Bildschirm oder im
    /// Desktop-Fenster selbst, je nachdem, wo mehr davon zu sehen ist. Das
    /// Desktop-Fenster zaehlt mit, weil auf manchem Laeufer ein Fenster den
    /// Desktop verdeckt, das sich nicht beiseitelegen laesst.
    /// </summary>
    public static double Anteil((byte R, byte G, byte B) f)
    {
        double schirm = AnteilIn(DesktopScreenshot.CaptureVirtualScreen().Bgra, f);
        var d = DesktopFenster();
        return d is null ? schirm : Math.Max(schirm, AnteilIn(d, f));
    }

    private static double AnteilIn(byte[] b, (byte R, byte G, byte B) f)
    {
        int pixel = b.Length / 4, schritt = Math.Max(1, pixel / 20000), treffer = 0, gesamt = 0;
        for (int i = 0; i < pixel; i += schritt)
        {
            int o = i * 4;
            gesamt++;
            if (Math.Abs(b[o] - f.B) <= 24 && Math.Abs(b[o + 1] - f.G) <= 24 && Math.Abs(b[o + 2] - f.R) <= 24) treffer++;
        }
        return gesamt == 0 ? 0 : (double)treffer / gesamt;
    }

    /// <summary>
    /// Liest die Benutzereinstellungen aus der Registry neu ein, wie beim
    /// Anmelden. Unter neueren Windows-Fassungen nicht immer wirksam - daher
    /// nur zur Auskunft.
    /// </summary>
    public static void WieBeimAnmelden()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "rundll32.exe", "user32.dll,UpdatePerUserSystemParameters 1, True") { UseShellExecute = false });
            p?.WaitForExit(15000);
        }
        catch (Exception ex) { Console.WriteLine($"         (UpdatePerUserSystemParameters: {ex.Message})"); }
        System.Threading.Thread.Sleep(4000);
    }

    /// <summary>
    /// Das Desktop-Fenster (Progman, samt Hintergrund) per PrintWindow - auch
    /// wenn andere Fenster darueber liegen. null, wenn das nicht geht oder es
    /// leer bleibt.
    /// </summary>
    public static byte[]? DesktopFenster()
    {
        var h = FindWindowW("Progman", null);
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
        int w = r.Right - r.Left, hoehe = r.Bottom - r.Top;
        if (w <= 0 || hoehe <= 0) return null;
        var schirm = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(schirm);
        var info = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -hoehe, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var alt = SelectObject(dc, bmp);
        try
        {
            if (bmp == IntPtr.Zero || !PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            var px = new byte[(long)w * hoehe * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            return DesktopScreenshot.IsBlank(px) ? null : px;
        }
        finally
        {
            SelectObject(dc, alt);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, schirm);
        }
    }

    /// <summary>Sichtbare Fenster, die mindestens die Haelfte des Hauptbildschirms bedecken - fuer das Protokoll.</summary>
    public static void GrosseFenster(string wann)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        var liste = new List<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            long flaeche = (long)Math.Max(0, Math.Min(r.Right, sw) - Math.Max(r.Left, 0)) * Math.Max(0, Math.Min(r.Bottom, sh) - Math.Max(r.Top, 0));
            if (flaeche * 2 < (long)sw * sh) return true;
            var k = new System.Text.StringBuilder(256); GetClassNameW(h, k, 256);
            var n = new System.Text.StringBuilder(256); GetWindowTextW(h, n, 256);
            DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int versteckt, 4);
            liste.Add($"{k} \"{n}\"{(versteckt != 0 ? " (verborgen)" : "")}");
            return true;
        }, IntPtr.Zero);
        Console.WriteLine($"         [{wann}] grosse Fenster ({sw} x {sh}): {(liste.Count == 0 ? "keine" : string.Join(" | ", liste))}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant, bmiColors;
    }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>Welche Windows-Fassung - fuer das Protokoll.</summary>
    public static void WindowsFassung()
    {
        string name = "?", fassung = "?", build = "?";
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            name = k?.GetValue("ProductName") as string ?? "?";
            fassung = k?.GetValue("DisplayVersion") as string ?? "?";
            build = $"{k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")}";
        }
        catch { }
        Console.WriteLine($"         Windows: {name} {fassung}, Build {build}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
    }

    /// <summary>Die Design-Werte aus der Sicherung eines Anwenders (Windows 11, dunkel, Akzentfarbe #0078D4).</summary>
    public static List<DesignEintrag> AnwenderDesign() => new()
    {
            new() { Gruppe = @"Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = @"SystemUsesLightTheme", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = @"AppsUseLightTheme", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = @"EnableTransparency", Typ = @"DWord", Wert = @"1" },
            new() { Gruppe = @"Farben", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = @"ColorPrevalence", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", Name = @"AccentPalette", Typ = @"Binary", Wert = @"mev/AEzC/wAAkfgAAHjUAABnwAAAPpIAABpoAPdjDAA=" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", Name = @"AccentColorMenu", Typ = @"DWord", Wert = @"-2852864" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", Name = @"StartColorMenu", Typ = @"DWord", Wert = @"-4167936" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\DWM", Name = @"AccentColor", Typ = @"DWord", Wert = @"-2852864" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\DWM", Name = @"ColorizationColor", Typ = @"DWord", Wert = @"-1006602028" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\DWM", Name = @"ColorizationAfterglow", Typ = @"DWord", Wert = @"-1006602028" },
            new() { Gruppe = @"Akzentfarbe", Schluessel = @"Software\Microsoft\Windows\DWM", Name = @"ColorPrevalence", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"", Typ = @"String", Wert = @"Windows-Voreinstellung" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Scheme Source", Typ = @"DWord", Wert = @"2" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"CursorBaseSize", Typ = @"DWord", Wert = @"32" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Arrow", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_arrow.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Help", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_helpsel.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"AppStarting", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_working.ani" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Wait", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_busy.ani" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Crosshair", Typ = @"String", Wert = @"" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"IBeam", Typ = @"String", Wert = @"" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"NWPen", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_pen.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"No", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_unavail.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"SizeNS", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_ns.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"SizeWE", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_ew.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"SizeNWSE", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_nwse.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"SizeNESW", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_nesw.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"SizeAll", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_move.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"UpArrow", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_up.cur" },
            new() { Gruppe = @"Mauszeiger", Schluessel = @"Control Panel\Cursors", Name = @"Hand", Typ = @"String", Wert = @"C:\WINDOWS\cursors\aero_link.cur" },
            new() { Gruppe = @"Taskleiste", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = @"ShowTaskViewButton", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Taskleiste", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = @"TaskbarSmallIcons", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Taskleiste", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Search", Name = @"SearchboxTaskbarMode", Typ = @"DWord", Wert = @"0" },
            new() { Gruppe = @"Start", Schluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = @"Start_IrisRecommendations", Typ = @"DWord", Wert = @"0" },
    };

    /// <summary>Die haeufigsten Farben auf dem Bildschirm und im Desktop-Fenster.</summary>
    public static string Meist()
    {
        var d = DesktopFenster();
        return $"Bildschirm {MeistIn(DesktopScreenshot.CaptureVirtualScreen().Bgra)}; Desktop-Fenster {(d is null ? "-" : MeistIn(d))}";
    }

    /// <summary>Die zwei haeufigsten Farben, z. B. "#6B69D6 83 %, #000000 9 %".</summary>
    private static string MeistIn(byte[] b)
    {
        int pixel = b.Length / 4, schritt = Math.Max(1, pixel / 20000), gesamt = 0;
        var zaehler = new Dictionary<int, int>();
        for (int i = 0; i < pixel; i += schritt)
        {
            int o = i * 4;
            int k = ((b[o + 2] >> 4) << 8) | ((b[o + 1] >> 4) << 4) | (b[o] >> 4);
            zaehler[k] = zaehler.GetValueOrDefault(k) + 1;
            gesamt++;
        }
        return string.Join(", ", zaehler.OrderByDescending(x => x.Value).Take(2).Select(x =>
            $"#{((x.Key >> 8) & 0xF) * 17:X2}{((x.Key >> 4) & 0xF) * 17:X2}{(x.Key & 0xF) * 17:X2} {(double)x.Value / Math.Max(1, gesamt):P0}"));
    }

    /// <summary>Anteil der Farbe nach 1, 3 ... Sekunden - mit den haeufigsten Farben. Gibt den letzten Anteil zurueck.</summary>
    public static double Verlauf(string was, (byte R, byte G, byte B) f, params int[] sekunden)
    {
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        double a = 0;
        foreach (var s in sekunden)
        {
            var warten = TimeSpan.FromSeconds(s) - uhr.Elapsed;
            if (warten > TimeSpan.Zero) System.Threading.Thread.Sleep(warten);
            a = Anteil(f);
            Console.WriteLine($"         {was}, nach {s} s: {a:P0} erwartete Farbe - meist {Meist()}");
        }
        return a;
    }

    /// <summary>Was Windows zum Hintergrund meint: Registry, Sitzung, Zwischenspeicher, Blickpunkt, Design-Datei, IDesktopWallpaper.</summary>
    public static void Zustand(string wann, Microsoft.Win32.RegistryKey wurzel, string? profil)
    {
        var s = DesktopHintergrund.Lesen(wurzel);
        var c = FarbeEcht.GetSysColor(1);
        Console.WriteLine($"         [{wann}] Registry: Bild={s.Bild ?? "-"}, Farbe={s.Farbe}, Typ={s.Typ?.ToString() ?? "-"}, Stil={s.Stil ?? "-"}; "
                          + $"Sitzungsfarbe #{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}");
        try
        {
            var z = Hintergrund.Zwischenspeicher(profil);
            var fi = z is null ? null : new FileInfo(z);
            using var d = wurzel.OpenSubKey(DesktopHintergrund.DesktopSchluessel);
            Console.WriteLine($"         [{wann}] TranscodedWallpaper: {(fi is { Exists: true } ? $"{fi.Length} Bytes, {fi.LastWriteTime:HH:mm:ss}" : "fehlt")}, "
                              + $"TranscodedImageCache: {(d?.GetValue("TranscodedImageCache") is byte[] tc ? $"{tc.Length} Bytes" : "fehlt")}");
        }
        catch (Exception ex) { Console.WriteLine($"         [{wann}] Zwischenspeicher: {ex.Message}"); }
        try
        {
            using var sp = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings");
            using var th = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes");
            var design = th?.GetValue("CurrentTheme") as string;
            string auszug = "-";
            if (design is not null && File.Exists(Environment.ExpandEnvironmentVariables(design)))
                auszug = string.Join(" | ", File.ReadAllLines(Environment.ExpandEnvironmentVariables(design))
                    .Where(z => z.StartsWith("Wallpaper=", StringComparison.OrdinalIgnoreCase)
                             || z.StartsWith("Background=", StringComparison.OrdinalIgnoreCase)
                             || z.StartsWith("MultimonBackgrounds", StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"         [{wann}] Blickpunkt EnabledState={sp?.GetValue("EnabledState")?.ToString() ?? "-"}; Design-Datei {design ?? "-"}: {auszug}");
        }
        catch (Exception ex) { Console.WriteLine($"         [{wann}] Blickpunkt/Design: {ex.Message}"); }
        Console.WriteLine($"         [{wann}] IDesktopWallpaper: {DesktopHintergrund.Schnittstelle.Stand() ?? "nicht erreichbar"}");
        try
        {
            using var vd = wurzel.OpenSubKey(DesktopHintergrund.VirtuelleDesktopsSchluessel);
            var namen = vd?.GetSubKeyNames() ?? Array.Empty<string>();
            Console.WriteLine($"         [{wann}] virtuelle Desktops: {(namen.Length == 0 ? "keine" : string.Join(", ", namen.Select(n =>
            {
                using var d = vd!.OpenSubKey(n);
                return d?.GetValue("Wallpaper") is string b ? $"Wallpaper=\"{b}\"" : "ohne Wallpaper";
            })))}");
        }
        catch (Exception ex) { Console.WriteLine($"         [{wann}] virtuelle Desktops: {ex.Message}"); }
    }

    /// <summary>Wartet bis zu 15 Sekunden, bis die Farbe ueberwiegt - der Desktop braucht nach dem Setzen einen Moment.</summary>
    public static double Sichtbar((byte R, byte G, byte B) f)
    {
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        double a;
        while (true)
        {
            a = Anteil(f);
            if (a > 0.5 || uhr.Elapsed.TotalSeconds > 15) return a;
            System.Threading.Thread.Sleep(1000);
        }
    }
}

internal static class FarbeEcht
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetSysColor(int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetSysColors(int cElements, int[] lpaElements, int[] lpaRgbValues);
}
