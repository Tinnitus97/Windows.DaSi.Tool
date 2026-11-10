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
        Check("Name: DASI_<Rechner>_TT_MM_JJJJ_HH_MM, mit fuehrenden Nullen",
            BackupTarget.FolderName("DESKTOP-7K4Q2M", zeitpunkt) == "DASI_DESKTOP-7K4Q2M_10_11_2026_08_05",
            BackupTarget.FolderName("DESKTOP-7K4Q2M", zeitpunkt));
        Check("unzulaessige Zeichen im Rechnernamen werden zu _",
            BackupTarget.FolderName("PC:1/2*?", zeitpunkt) == "DASI_PC_1_2___10_11_2026_08_05",
            BackupTarget.FolderName("PC:1/2*?", zeitpunkt));
        Check("ohne Rechnernamen: PC",
            BackupTarget.FolderName("  ", zeitpunkt) == "DASI_PC_10_11_2026_08_05");
        Check("24-Stunden-Format (18 Uhr, nicht 6)",
            BackupTarget.FolderName("PC", new DateTime(2026, 11, 10, 18, 45, 0)).EndsWith("_10_11_2026_18_45"));

        var laufwerk = Path.Combine(root, "G");
        Check("eingeschaltet: Ordner unter dem gewaehlten Ziel",
            BackupTarget.Resolve(laufwerk, true, "PC", zeitpunkt) == Path.Combine(laufwerk, "DASI_PC_10_11_2026_08_05"));
        Check("ausgeschaltet: direkt ins gewaehlte Ziel",
            BackupTarget.Resolve(laufwerk, false, "PC", zeitpunkt) == laufwerk);
        var schonDasi = Path.Combine(laufwerk, "DASI_ALTPC_01_10_2026_12_00");
        Check("ist das Ziel schon ein DASI-Ordner, wird nichts verschachtelt",
            BackupTarget.Resolve(schonDasi, true, "PC", zeitpunkt) == schonDasi);
        Check("auch mit abschliessendem Trennzeichen und Kleinschreibung",
            BackupTarget.IsOwnFolder(@"G:\dasi_pc_10_11_2026_08_05\") && BackupTarget.IsOwnFolder("/mnt/g/DASI_PC_x/"));
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

                var pngDatei = Path.Combine(root, "Desktop-Screenshot", DesktopScreenshot.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(pngDatei)!);
                using (var fs = File.Create(pngDatei)) PngWriter.WriteBgra(fs, aufnahme.Width, aufnahme.Height, aufnahme.Bgra);
                var gelesen = DecodeOwnPng(File.ReadAllBytes(pngDatei), out var dw, out var dh);
                bool stichprobe = dw == aufnahme.Width && dh == aufnahme.Height;
                for (long i = 0; stichprobe && i < (long)dw * dh; i += 997)
                    stichprobe = gelesen[i * 3] == aufnahme.Bgra[i * 4 + 2] && gelesen[i * 3 + 2] == aufnahme.Bgra[i * 4];
                Check("als PNG gespeichert und verlustfrei zurueckgelesen", stichprobe, $"{new FileInfo(pngDatei).Length / 1024} KB");
            }

            var sitzung = DesktopScreenshot.GetInteractiveUser();
            Console.WriteLine($"         angemeldet: {sitzung?.Name ?? "(unbekannt)"}, Profil: {sitzung?.ProfilePath ?? "(unbekannt)"}");
            Check("angemeldeter Benutzer wird ermittelt (oder sauber null)", true);

            var uhrDesktop = System.Diagnostics.Stopwatch.StartNew();
            var hin = DesktopScreenshot.ToggleDesktop();
            var zurueck = DesktopScreenshot.ToggleDesktop();
            uhrDesktop.Stop();
            Console.WriteLine($"         Desktop anzeigen: {hin}/{zurueck} in {uhrDesktop.Elapsed.TotalSeconds:F1} s");
            Check("\"Desktop anzeigen\" kehrt zurueck, auch ohne Explorer (kein Haenger)", uhrDesktop.Elapsed.TotalSeconds < 25);
        }

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }
}
