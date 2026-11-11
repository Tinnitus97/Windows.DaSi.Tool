using System;
using System.Collections.Generic;
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

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
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

    public void Setzen(int index, int x, int y)
    {
        Protokoll.Add($"setzen {_symbole[index].Name}");
        _symbole[index] = _symbole[index] with { X = x, Y = y };
    }

    public (int, int) Stelle(string name) => _symbole.First(s => s.Name == name) is var s ? (s.X, s.Y) : (-1, -1);

    public void Dispose() { }
}

