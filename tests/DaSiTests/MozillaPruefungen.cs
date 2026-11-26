using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Firefox/Thunderbird: Welches Profil nimmt das Programm nach dem
/// Zurueckschreiben? Anlass: Auf dem alten PC lief das 32-Bit-Firefox, auf dem
/// neuen installierte winget das 64-Bit-Firefox - in einen anderen Ordner. Das
/// zurueckgeschriebene Profil gehoerte der alten Installation, Firefox legte
/// ein leeres "default-release" an.
/// </summary>
internal static partial class Program
{
    /// <summary>Wie Firefox - nur mit eigenem Prozessnamen, damit kein echter Firefox beendet wird.</summary>
    private static readonly AppEintrag Testfuchs =
        new(Punkt.Firefox, "Testfuchs", @"AppData\Roaming\DaSiTest\Testfuchs", "dasi-testfuchs", "DaSi.Testfuchs", false);

    private const string Fx64 = @"C:\Program Files\Mozilla Firefox";
    private const string Fx32 = @"C:\Program Files (x86)\Mozilla Firefox";

    /// <summary>profiles.ini, wie sie ein PC mit 32-Bit-Firefox schreibt: zwei Profile, das neuere gehoert der Installation.</summary>
    private const string ProfilesIniAlt =
        "[InstallE7CF176E110C211B]\r\nDefault=Profiles/neu0abcd.default\r\nLocked=1\r\n\r\n"
        + "[Profile1]\r\nName=default\r\nIsRelative=1\r\nPath=Profiles/neu0abcd.default\r\n\r\n"
        + "[Profile0]\r\nName=uralt\r\nIsRelative=1\r\nPath=Profiles/alt0abcd.default\r\nDefault=1\r\n\r\n"
        + "[General]\r\nStartWithLastProfile=1\r\nVersion=2\r\n\r\n"
        + "[BackgroundTasksProfiles]\r\nMozillaBackgroundTask-E7CF176E110C211B-backgroundupdate=abc.MozillaBackgroundTask\r\n";

    /// <summary>
    /// Wie beim Anwender: Das Standardprofil gehoert - gesperrt - der
    /// 32-Bit-Installation. Ein 64-Bit-Firefox darf es nicht uebernehmen.
    /// </summary>
    private const string ProfilesIniGesperrt =
        "[InstallE7CF176E110C211B]\r\nDefault=Profiles/neu0abcd.default\r\nLocked=1\r\n\r\n"
        + "[Profile1]\r\nName=alt\r\nIsRelative=1\r\nPath=Profiles/alt0abcd.default\r\n\r\n"
        + "[Profile0]\r\nName=default-esr\r\nIsRelative=1\r\nPath=Profiles/neu0abcd.default\r\nDefault=1\r\n\r\n"
        + "[General]\r\nStartWithLastProfile=1\r\nVersion=2\r\n";

    private static string MozillaDaten(string ordner, string profilesIni, params (string Profil, DateTime Benutzt)[] profile)
    {
        Directory.CreateDirectory(ordner);
        File.WriteAllText(Path.Combine(ordner, "profiles.ini"), profilesIni);
        File.WriteAllText(Path.Combine(ordner, "installs.ini"), "[E7CF176E110C211B]\r\nDefault=Profiles/neu0abcd.default\r\nLocked=1\r\n");
        foreach (var (p, benutzt) in profile)
        {
            var prefs = Path.Combine(ordner, "Profiles", p, "prefs.js");
            Datei(prefs, "user_pref(\"browser.startup.homepage\", \"https://dasi.example/\");");
            File.SetLastWriteTimeUtc(prefs, benutzt);
        }
        return ordner;
    }

    private static void MozillaPruefen(string root)
    {
        Console.WriteLine("\n== Firefox/Thunderbird: Standardprofil der Installation ==");

        // Der Schluessel einer Installation - wie Firefox ihn berechnet. Die
        // ersten drei stehen so in profiles.ini echter Rechner; die uebrigen
        // decken jede Laenge ab (CityHash rechnet je nach Laenge anders) und
        // werden auf dem Windows-Laeufer gegen ein echtes Firefox geprueft.
        var erwartet = new (string Pfad, string Schluessel)[]
        {
            (Fx64, "308046B0AF4A39CB"),
            (Fx32, "E7CF176E110C211B"),
            (@"C:\Program Files\Mozilla Thunderbird", "D78BF5DD33499EC2"),
            ("", "9AE16A3B2F90404F"),
            ("C", "6525C1F6669778F7"),
            (@"C:\F", "AD659CE199305044"),
            (@"C:\FFox", "92E5C30BE11667B2"),
            (@"C:\Firefox-1", "97DC4BE029CCCE39"),
            (@"C:\Users\Anwender\AppData\Local\Mozilla Firefox", "C6B16A5DF0475837"),
            (@"C:\Users\Anwender\AppData\Local\Mozilla Firefox\eine sehr lange Pfadangabe", "284924C2B5A43C77"),
            (@"C:\Users\Anwender\AppData\Local\Mozilla Firefox\eine sehr lange Pfadangabe mit noch mehr Zeichen fuer eine dritte Runde", "464C29FAAA5A2DB8"),
        };
        foreach (var (pfad, schluessel) in erwartet)
            Check($"Schluessel von \"{pfad}\"", MozillaProfile.Schluessel(pfad) == schluessel, MozillaProfile.Schluessel(pfad));
        Check("Programme mit Mozilla-Profilverwaltung", MozillaProfile.Programm(AppCatalog.Firefox) == ("firefox.exe", "Mozilla Firefox")
            && MozillaProfile.Programm(AppCatalog.Thunderbird) == ("thunderbird.exe", "Mozilla Thunderbird") && MozillaProfile.Programm(AppCatalog.Chrome) is null);
        Check("alter Ort aus dem Schluessel: 32-Bit-Firefox", MozillaProfile.BekannterOrt("E7CF176E110C211B", "Mozilla Firefox", @"C:\Users\Anwender") == Fx32);
        Check("alter Ort unbekannt", MozillaProfile.BekannterOrt("0123456789ABCDEF", "Mozilla Firefox", @"C:\Users\Anwender") is null);

        // Der Fall des Anwenders: Sicherung vom 32-Bit-Firefox, hier 64 Bit.
        var jetzt = DateTime.UtcNow;
        var daten = MozillaDaten(Path.Combine(root, "mozilla", "fall"), ProfilesIniAlt,
                                 ("neu0abcd.default", jetzt.AddDays(-1)), ("alt0abcd.default", jetzt.AddYears(-3)));
        var erg = MozillaProfile.Anpassen(daten, new[] { Fx64 });
        var ini = MozillaProfile.Ini.Lesen(File.ReadAllText(Path.Combine(daten, "profiles.ini")));
        Check("das Profil der alten Installation gewaehlt", erg.Profil == "Profiles/neu0abcd.default" && erg.Name == "default", erg.Profil);
        Check("Eintrag fuer das 64-Bit-Firefox: Profil, gesperrt",
            ini.Wert("Install308046B0AF4A39CB", "Default") == "Profiles/neu0abcd.default" && ini.Wert("Install308046B0AF4A39CB", "Locked") == "1");
        Check("Eintrag der alten Installation entfernt", ini.Wert("InstallE7CF176E110C211B", "Default") is null);
        Check("Default=1 beim gewaehlten Profil, nicht mehr beim alten",
            ini.Wert("Profile1", "Default") == "1" && ini.Wert("Profile0", "Default") is null);
        Check("uebrige Abschnitte unveraendert", ini.Wert("General", "Version") == "2" && ini.Wert("Profile0", "Path") == "Profiles/alt0abcd.default"
            && ini.Wert("BackgroundTasksProfiles", "MozillaBackgroundTask-E7CF176E110C211B-backgroundupdate") == "abc.MozillaBackgroundTask");
        Check("Zeilenende bleibt (CRLF)", File.ReadAllText(Path.Combine(daten, "profiles.ini")).Contains("\r\n"));
        var installs = MozillaProfile.Ini.Lesen(File.ReadAllText(Path.Combine(daten, "installs.ini")));
        Check("installs.ini: dieselbe Installation, nur der Schluessel als Name",
            installs.Abschnitte.Count == 1 && installs.Wert("308046B0AF4A39CB", "Default") == "Profiles/neu0abcd.default" && installs.Wert("308046B0AF4A39CB", "Locked") == "1");
        Check("alte und neue Installationen im Ergebnis", erg.AlteSchluessel.SequenceEqual(new[] { "E7CF176E110C211B" })
            && erg.Neu.Single() == (Fx64, "308046B0AF4A39CB") && !erg.SchonPassend);

        // Dieselbe Installation wie auf dem alten PC: nichts Neues - "wie gehabt".
        var gleich = MozillaDaten(Path.Combine(root, "mozilla", "gleich"), ProfilesIniAlt, ("neu0abcd.default", jetzt));
        Check("gleiche Installation: schon passend", MozillaProfile.Anpassen(gleich, new[] { Fx32 }).SchonPassend);

        // Mehrere Installationen (64 Bit und nur fuer den Benutzer) - beide bekommen das Profil.
        var zwei = MozillaDaten(Path.Combine(root, "mozilla", "zwei"), ProfilesIniAlt, ("neu0abcd.default", jetzt));
        var nurBenutzer = @"C:\Users\Anwender\AppData\Local\Mozilla Firefox";
        var ergZwei = MozillaProfile.Anpassen(zwei, new[] { Fx64, nurBenutzer, Fx64 });
        var iniZwei = MozillaProfile.Ini.Lesen(File.ReadAllText(Path.Combine(zwei, "profiles.ini")));
        Check("zwei Installationen: beide eingetragen, doppelte einmal",
            ergZwei.Neu.Count == 2 && iniZwei.Wert("InstallC6B16A5DF0475837", "Default") == "Profiles/neu0abcd.default"
            && iniZwei.Abschnitte.Count(a => a.Name.StartsWith("Install")) == 2);

        // Mehrere Installationen auf dem alten PC mit verschiedenen Profilen: das zuletzt benutzte.
        var mehrere = MozillaDaten(Path.Combine(root, "mozilla", "mehrere"),
            "[InstallE7CF176E110C211B]\nDefault=Profiles/alt0abcd.default\nLocked=1\n\n[Install308046B0AF4A39CB]\nDefault=Profiles/neu0abcd.default\nLocked=1\n\n"
            + "[Profile0]\nName=a\nIsRelative=1\nPath=Profiles/alt0abcd.default\n\n[Profile1]\nName=b\nIsRelative=1\nPath=Profiles/neu0abcd.default\n\n[General]\nStartWithLastProfile=1\n",
            ("alt0abcd.default", jetzt), ("neu0abcd.default", jetzt.AddDays(-30)));
        Check("mehrere alte Installationen: das zuletzt benutzte Profil",
            MozillaProfile.Anpassen(mehrere, new[] { Fx64 }).Profil == "Profiles/alt0abcd.default");
        Check("Zeilenende bleibt (LF)", !File.ReadAllText(Path.Combine(mehrere, "profiles.ini")).Contains('\r'));

        // Vor Firefox 67: keine Installationen, Default=1 entscheidet.
        var vor67 = MozillaDaten(Path.Combine(root, "mozilla", "vor67"),
            "[General]\nStartWithLastProfile=1\n\n[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/alt0abcd.default\nDefault=1\n\n[Profile1]\nName=test\nIsRelative=1\nPath=Profiles/neu0abcd.default\n",
            ("alt0abcd.default", jetzt.AddYears(-1)), ("neu0abcd.default", jetzt));
        Check("vor Firefox 67: das Profil mit Default=1", MozillaProfile.Anpassen(vor67, new[] { Fx64 }).Profil == "Profiles/alt0abcd.default");

        // Weder Installation noch Default=1: das zuletzt benutzte.
        var ohne = MozillaDaten(Path.Combine(root, "mozilla", "ohne"),
            "; von Hand bearbeitet\n[Profile0]\nName=a\nIsRelative=1\nPath=Profiles/alt0abcd.default\nunsinn ohne Gleichheitszeichen\n\n[Profile1]\nName=b\nIsRelative=1\nPath=Profiles/neu0abcd.default\n",
            ("alt0abcd.default", jetzt.AddYears(-1)), ("neu0abcd.default", jetzt));
        Check("ohne Standard: das zuletzt benutzte Profil", MozillaProfile.Anpassen(ohne, new[] { Fx64 }).Profil == "Profiles/neu0abcd.default");

        // Absoluter Profilpfad (IsRelative=0).
        var absolutOrdner = Path.Combine(root, "mozilla", "woanders", "profil");
        Datei(Path.Combine(absolutOrdner, "prefs.js"));
        var absolut = MozillaDaten(Path.Combine(root, "mozilla", "absolut"),
            $"[Profile0]\nName=extern\nIsRelative=0\nPath={absolutOrdner}\nDefault=1\n");
        Check("absoluter Profilpfad", MozillaProfile.Anpassen(absolut, new[] { Fx64 }).Profil == absolutOrdner);

        // Kein Firefox auf diesem PC: keine Installation eintragen, aber das
        // Profil als Standard - Firefox uebernimmt es beim ersten Start.
        var keiner = MozillaDaten(Path.Combine(root, "mozilla", "keiner"), ProfilesIniAlt, ("neu0abcd.default", jetzt));
        var ergKeiner = MozillaProfile.Anpassen(keiner, Array.Empty<string>());
        var iniKeiner = MozillaProfile.Ini.Lesen(File.ReadAllText(Path.Combine(keiner, "profiles.ini")));
        Check("keine Installation: keine Eintraege, installs.ini weg, Profil Standard",
            ergKeiner.Neu.Count == 0 && !ergKeiner.SchonPassend && !iniKeiner.Abschnitte.Any(a => a.Name.StartsWith("Install"))
            && !File.Exists(Path.Combine(keiner, "installs.ini")) && iniKeiner.Wert("Profile1", "Default") == "1");
        File.Delete(Path.Combine(keiner, "profiles.ini"));
        Check("keine profiles.ini: nichts zu tun", MozillaProfile.Anpassen(keiner, new[] { Fx64 }).Profil is null);

        // Das Profil fehlt im Datenordner: nichts anfassen.
        var leer = MozillaDaten(Path.Combine(root, "mozilla", "leer"), ProfilesIniAlt);
        var vorher = File.ReadAllText(Path.Combine(leer, "profiles.ini"));
        var ergLeer = MozillaProfile.Anpassen(leer, new[] { Fx64 });
        Check("Profilordner fehlt: unveraendert", ergLeer.Profil is null && ergLeer.AlteSchluessel.Count == 1
            && File.ReadAllText(Path.Combine(leer, "profiles.ini")) == vorher);
    }

    /// <summary>Wie Thunderbird - eigener Prozessname, damit kein echter Thunderbird beendet wird.</summary>
    private static readonly AppEintrag Testvogel =
        new(Punkt.Thunderbird, "Testvogel", @"AppData\Roaming\DaSiTest\Testvogel", "dasi-testvogel", "DaSi.Testvogel", false);

    /// <summary>Ein Installationsordner mit application.ini, wie Firefox ihn anlegt.</summary>
    private static string Installation(string ordner, string version, string buildId)
    {
        Directory.CreateDirectory(ordner);
        File.WriteAllText(Path.Combine(ordner, "application.ini"), $"[App]\r\nVendor=Mozilla\r\nName=Firefox\r\nVersion={version}\r\nBuildID={buildId}\r\n");
        return ordner;
    }

    /// <summary>
    /// Was ein Profil ausser profiles.ini an den alten PC bindet - Anregungen
    /// aus MozBackup: neuere Fassung (Firefox lehnt das Profil ab), Pfade des
    /// alten Benutzers in prefs.js, Mails ausserhalb des Profils.
    /// </summary>
    private static void MozillaPflegePruefen(string root)
    {
        Console.WriteLine("\n== Firefox/Thunderbird: Fassung, Pfade, Mails ausserhalb ==");
        Check("LastVersion zerlegen", MozillaProfile.LetzteVersion("146.0.1_20261120123456/20261120123456") == ("146.0.1", "20261120123456")
            && MozillaProfile.LetzteVersion("146.0") == ("146.0", "") && MozillaProfile.LetzteVersion(null) is null && MozillaProfile.LetzteVersion(" ") is null);
        MozillaProfile.Abstand V(string p, string i, string pb = "1", string ib = "1") => MozillaProfile.Vergleichen((p, pb), (i, ib));
        Check("Fassung: Profil etwas neuer (146.0.1 / 146.0)", V("146.0.1", "146.0") == MozillaProfile.Abstand.Neuer);
        Check("Fassung: Profil aelter oder gleich", V("146.0", "146.0.1") == MozillaProfile.Abstand.Passt && V("146.0", "146.0") == MozillaProfile.Abstand.Passt);
        Check("Fassung: neuere Hauptversion", V("147.0", "146.0.1") == MozillaProfile.Abstand.VielNeuer && V("140.0", "128.5.0esr") == MozillaProfile.Abstand.VielNeuer);
        Check("Fassung: gleiche Version, neuere BuildID", V("146.0", "146.0", "20261121000000", "20261120000000") == MozillaProfile.Abstand.Neuer
            && V("146.0", "146.0", "20261119000000", "20261120000000") == MozillaProfile.Abstand.Passt);
        Check("Fassung: Beta gegen fertige Fassung", V("147.0b3", "147.0") == MozillaProfile.Abstand.Passt && V("147.0", "147.0b3") == MozillaProfile.Abstand.Neuer);
        Check("Fassung: ESR", V("128.5.0esr", "128.6.0esr") == MozillaProfile.Abstand.Passt && V("128.6.0esr", "128.5.0esr") == MozillaProfile.Abstand.Neuer);

        var inst = Installation(Path.Combine(root, "mozpflege", "inst"), "146.0", "20261110094521");
        Check("installierte Fassung aus application.ini", MozillaProfile.InstallierteFassung(inst) == ("146.0", "20261110094521")
            && MozillaProfile.InstallierteFassung(Path.Combine(root, "mozpflege", "fehlt")) is null);
        var profil = Path.Combine(root, "mozpflege", "profil");
        Datei(Path.Combine(profil, "compatibility.ini"), "[Compatibility]\r\nLastVersion=146.0.1_20261120123456/20261120123456\r\nLastOSABI=WINNT_x86_64-msvc\r\n");
        Check("Fassung des Profils aus compatibility.ini", MozillaProfile.ProfilFassung(profil) == ("146.0.1", "20261120123456")
            && MozillaProfile.ProfilFassung(inst) is null);

        // Pfade des alten Benutzers in prefs.js.
        Datei(Path.Combine(profil, "prefs.js"),
            "// Mozilla User Preferences\n"
            + "user_pref(\"browser.download.dir\", \"C:\\\\Users\\\\Besitzer\\\\Downloads\");\n"
            + "user_pref(\"browser.cache.disk.parent_directory\", \"C:\\\\Users\\\\Besitzer\\\\AppData\\\\Local\\\\Mozilla\\\\Firefox\\\\Profiles\\\\abc.default\");\n"
            + "user_pref(\"browser.startup.homepage\", \"file:///C:/Users/Besitzer/Documents/Start.html\");\n"
            + "user_pref(\"mail.server.server1.directory\", \"C:\\\\Users\\\\Besitzerin\\\\Mail\");\n"
            + "user_pref(\"browser.startup.page\", 3);\n"
            + "// C:\\\\Users\\\\Besitzer im Kommentar bleibt\n");
        Check("alter Benutzerordner aus prefs.js", MozillaProfile.AlterBenutzerordner(profil) == @"C:\Users\Besitzer", MozillaProfile.AlterBenutzerordner(profil));
        var umgestellt = MozillaProfile.PfadeUmstellen(profil, @"C:\Users\Besitzer", @"C:\Users\Anwender");
        var prefs = File.ReadAllText(Path.Combine(profil, "prefs.js"));
        Check("Pfade umgestellt: Ordner, Cache, file:///", umgestellt == 3
            && prefs.Contains("\"C:\\\\Users\\\\Anwender\\\\Downloads\"") && prefs.Contains("C:\\\\Users\\\\Anwender\\\\AppData\\\\Local")
            && prefs.Contains("file:///C:/Users/Anwender/Documents/Start.html"), prefs);
        Check("Pfade: aehnlicher Name, Kommentar und Zahlen bleiben", prefs.Contains("C:\\\\Users\\\\Besitzerin\\\\Mail")
            && prefs.Contains("// C:\\\\Users\\\\Besitzer im Kommentar") && prefs.Contains("browser.startup.page\", 3"), prefs);
        Check("Pfade: gleicher Ordner oder keine prefs.js - nichts zu tun",
            MozillaProfile.PfadeUmstellen(profil, @"C:\Users\Anwender", @"c:\users\ANWENDER\") == 0 && MozillaProfile.PfadeUmstellen(inst, @"C:\Users\A", @"C:\Users\B") == 0);
        var nurErweiterungen = Path.Combine(root, "mozpflege", "erweiterungen");
        Datei(Path.Combine(nurErweiterungen, "extensions.json"), "{\"addons\":[{\"path\":\"C:\\\\Users\\\\Max\\\\AppData\\\\Roaming\\\\Mozilla\\\\Firefox\\\\Profiles\\\\x\\\\extensions\\\\a.xpi\"}]}");
        Check("alter Benutzerordner aus extensions.json; ohne Hinweis keiner",
            MozillaProfile.AlterBenutzerordner(nurErweiterungen) == @"C:\Users\Max" && MozillaProfile.AlterBenutzerordner(inst) is null);

        // Thunderbird: Mails ausserhalb des Profils.
        var tb = Path.Combine(root, "mozpflege", "tb");
        var imProfil = Path.Combine(tb, "ImapMail", "imap.example.com");
        Datei(Path.Combine(tb, "prefs.js"),
            $"user_pref(\"mail.server.server1.directory\", \"{imProfil.Replace("\\", "\\\\")}\");\n"
            + "user_pref(\"mail.server.server1.directory-rel\", \"[ProfD]ImapMail/imap.example.com\");\n"
            + "user_pref(\"mail.server.server2.directory\", \"D:\\\\Mail\\\\Local Folders\");\n"
            + "user_pref(\"mail.server.server3.directory\", \"\");\n");
        var aussen = MozillaProfile.PostfaecherAusserhalb(tb);
        Check("Mails ausserhalb: nur das Konto auf D:", aussen.Count == 1 && aussen[0] == new MozillaProfile.Postfach("server2", @"D:\Mail\Local Folders")
            || !OperatingSystem.IsWindows() && aussen.Any(a => a.Ordner == @"D:\Mail\Local Folders"), string.Join(", ", aussen));
        Check("Mails ausserhalb: ohne prefs.js keine", MozillaProfile.PostfaecherAusserhalb(inst).Count == 0);
        var verlegtNach = Path.Combine(tb, "Mail", "Extern-server2");
        Check("Postfach verlegen: absolut und relativ eingetragen", MozillaProfile.PostfachVerlegen(tb, "server2", verlegtNach)
            && File.ReadAllText(Path.Combine(tb, "prefs.js")).Contains("mail.server.server2.directory-rel\", \"[ProfD]Mail/Extern-server2\"")
            && File.ReadAllText(Path.Combine(tb, "prefs.js")).Contains(verlegtNach.Replace("\\", "\\\\")), File.ReadAllText(Path.Combine(tb, "prefs.js")));
        Check("Postfach verlegen: unbekanntes Konto, keine prefs.js", !MozillaProfile.PostfachVerlegen(tb, "server9", verlegtNach) && !MozillaProfile.PostfachVerlegen(inst, "server2", verlegtNach));

        // Profilordner laut profiles.ini.
        var daten = MozillaDaten(Path.Combine(root, "mozpflege", "daten"), ProfilesIniAlt, ("neu0abcd.default", DateTime.UtcNow));
        Check("Profilordner laut profiles.ini (nur vorhandene)", MozillaProfile.Profilordner(daten).Count == 1
            && MozillaProfile.Profilordner(daten)[0].EndsWith("neu0abcd.default") && MozillaProfile.Profilordner(inst).Count == 0);
    }

    /// <summary>Firefox zurueckschreiben ueber den Dienst - mit der winget-Attrappe und festen Installationsordnern.</summary>
    private static void MozillaDienstPruefen(string basis, Func<string, BackupService> dienst, Protokoll log, string szenario)
    {
        Console.WriteLine("\n== Sicherungsdienst: Firefox-Profil einer anderen Installation ==");
        // "bereits installiert" - sonst wartete jeder Lauf, ob der Installer das Programm startet.
        Attrappen.SzenarioSchreiben(szenario, Szenario(("install.*.code", WingetFehler.SchonInstalliert.ToString())));
        var sicherung = dienst(basis).BackupPath;
        var gesichert = Path.Combine(sicherung, Testfuchs.BackupOrdner);
        MozillaDaten(gesichert, ProfilesIniAlt, ("neu0abcd.default", DateTime.UtcNow), ("alt0abcd.default", DateTime.UtcNow.AddYears(-3)));
        var ziel = Path.Combine(basis, "Fuchs-Anwender");

        var d = dienst(ziel);
        d.TestInstallationen = new List<string> { Fx64 };
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        var daten = AppCatalog.Pfad(ziel, Testfuchs.ProfilRel);
        var ini = MozillaProfile.Ini.Lesen(File.ReadAllText(Path.Combine(daten, "profiles.ini")));
        Check("Dienst: Profil zurueck und Standard des 64-Bit-Firefox",
            log.Hat("[ERFOLG] Testfuchs Profil wiederhergestellt") && ini.Wert("Install308046B0AF4A39CB", "Default") == "Profiles/neu0abcd.default", log.Text);
        Check("Dienst: Protokoll nennt Profil, neuen und alten Ort",
            log.Hat($"[INFO] Testfuchs: Profil \"default\" (Profiles/neu0abcd.default) ist jetzt Standard für {Fx64}. Auf dem alten PC lief Testfuchs aus {Fx32}"), log.Text);
        Check("Sicherung selbst bleibt unveraendert", File.ReadAllText(Path.Combine(gesichert, "profiles.ini")) == ProfilesIniAlt);
        // Kein "App Paths"-Eintrag (den nimmt etwa das Deinstallieren einer
        // parallelen ESR-Fassung mit), das Programm liegt aber im Programmordner.
        Check("Dienst: im Programmordner gefunden - kein winget", log.Hat("[INFO] Testfuchs ist bereits installiert.") && !log.Hat("via Winget"), log.Text);

        d.TestInstallationen = new List<string> { Fx32 };
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: gleiche Installation wie vorher", log.Hat($"ist Standard für {Fx32} - wie auf dem alten PC"), log.Text);

        d.TestInstallationen = new List<string>();
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: nicht installiert - Profil trotzdem Standard", log.Hat("[WARNUNG] Testfuchs ist auf diesem PC nicht installiert. Das Profil \"default\""), log.Text);
        Check("Dienst: nicht installiert - erst winget versucht", log.Hat("Versuche, Testfuchs via Winget zu installieren"), log.Text);

        // Ohne profiles.ini in der Sicherung.
        File.Move(Path.Combine(gesichert, "profiles.ini"), Path.Combine(gesichert, "profiles.bak"));
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: ohne profiles.ini - Hinweis", log.Hat("[WARNUNG] Testfuchs: In der Sicherung steht kein Profil in profiles.ini"), log.Text);
        File.Move(Path.Combine(gesichert, "profiles.bak"), Path.Combine(gesichert, "profiles.ini"));

        // Schreiben scheitert (an der Stelle der Zwischendatei liegt ein Ordner).
        Directory.CreateDirectory(Path.Combine(gesichert, "profiles.ini.dasi"));
        d.TestInstallationen = new List<string> { Fx64 };
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: Eintragen scheitert - Fehler mit Abhilfe", log.Hat("[FEHLER] Testfuchs: Das Profil ließ sich nicht als Standard eintragen") && log.Hat("-P"), log.Text);
        Directory.Delete(Path.Combine(gesichert, "profiles.ini.dasi"));

        // Pfade des alten Benutzers und eine etwas neuere Fassung im Profil.
        var profilDaten = Path.Combine(gesichert, "Profiles", "neu0abcd.default");
        File.AppendAllText(Path.Combine(profilDaten, "prefs.js"),
            "\nuser_pref(\"browser.download.dir\", \"C:\\\\Users\\\\Besitzer\\\\Downloads\");\n"
            + "user_pref(\"browser.cache.disk.parent_directory\", \"C:\\\\Users\\\\Besitzer\\\\AppData\\\\Local\\\\Mozilla\");\n");
        File.WriteAllText(Path.Combine(profilDaten, "compatibility.ini"), "[Compatibility]\r\nLastVersion=146.0.1_20261120123456/20261120123456\r\n");
        var instHier = Installation(Path.Combine(basis, "Programme", "Testfuchs"), "146.0", "20261110094521");
        d.TestInstallationen = new List<string> { instHier };
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        var prefsZiel = File.ReadAllText(Path.Combine(daten, "Profiles", "neu0abcd.default", "prefs.js"));
        Check("Dienst: Pfade des alten Benutzers aufs gewaehlte Profil umgestellt",
            log.Hat("[INFO] Testfuchs: 2 Einstellungen in neu0abcd.default\\prefs.js zeigten auf den Benutzerordner des alten PCs (C:\\Users\\Besitzer)")
            && prefsZiel.Contains(ziel.Replace("\\", "\\\\") + "\\\\Downloads"), log.Text);
        Check("Dienst: etwas neuere Fassung, gleiche Hauptversion - kein Eingriff",
            !log.Hat("lief zuletzt mit") && File.Exists(Path.Combine(daten, "Profiles", "neu0abcd.default", "compatibility.ini")), log.Text);
        File.WriteAllText(Path.Combine(profilDaten, "compatibility.ini"), "[Compatibility]\r\nLastVersion=147.0_20261201000000/20261201000000\r\n");
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: neuere Hauptversion - Fehler mit Abhilfe, Pruefung bleibt",
            log.Hat("[FEHLER] Testfuchs: Das Profil lief zuletzt mit 147.0, hier ist erst 146.0 installiert") && log.Hat("aktualisieren")
            && File.Exists(Path.Combine(daten, "Profiles", "neu0abcd.default", "compatibility.ini")), log.Text);
        File.WriteAllText(Path.Combine(profilDaten, "compatibility.ini"), "[Compatibility]\r\nLastVersion=145.0_20261001000000/20261001000000\r\n");
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        Check("Dienst: aeltere Fassung im Profil - kein Hinweis", !log.Hat("lief zuletzt mit"), log.Text);
        File.Delete(Path.Combine(profilDaten, "compatibility.ini"));

        // Thunderbird: Mails ausserhalb des Profils - mitsichern und zurueckschreiben.
        var vogelBesitzer = Path.Combine(basis, "Vogel-Besitzer");
        var vogelDaten = AppCatalog.Pfad(vogelBesitzer, Testvogel.ProfilRel);
        var vogelProfil = Path.Combine(vogelDaten, "Profiles", "vogel.default");
        var extern1 = Path.Combine(basis, "Mailablage", "Local Folders");
        Datei(Path.Combine(extern1, "Inbox"), "From: a@example.com");
        Datei(Path.Combine(extern1, "Inbox.msf"), "index");
        var imBenutzer = Path.Combine(vogelBesitzer, "Documents", "Mail");
        Datei(Path.Combine(vogelDaten, "profiles.ini"), "[Profile0]\r\nName=default\r\nIsRelative=1\r\nPath=Profiles/vogel.default\r\nDefault=1\r\n");
        Datei(Path.Combine(vogelProfil, "prefs.js"),
            $"user_pref(\"mail.server.server1.directory\", \"{Path.Combine(vogelProfil, "ImapMail", "x").Replace("\\", "\\\\")}\");\n"
            + $"user_pref(\"mail.server.server2.directory\", \"{extern1.Replace("\\", "\\\\")}\");\n"
            + $"user_pref(\"mail.server.server3.directory\", \"{imBenutzer.Replace("\\", "\\\\")}\");\n"
            + "user_pref(\"mail.server.server4.directory\", \"Q:\\\\gibt-es-nicht\\\\Mail\");\n");
        log.Leeren();
        dienst(vogelBesitzer).BackupAppProfile(Testvogel).Wait();
        var liste = Path.Combine(sicherung, BackupService.ThunderbirdExternOrdner, "Postfaecher.json");
        if (OperatingSystem.IsWindows())
        {
            Check("Thunderbird: Mails ausserhalb des Profils mitgesichert",
                log.Hat("[ERFOLG] Testvogel: Mails von server2 außerhalb des Profils gesichert") && File.Exists(liste)
                && File.Exists(Path.Combine(sicherung, BackupService.ThunderbirdExternOrdner, "1_server2", "Inbox")), log.Text);
            Check("Thunderbird: im Benutzerprofil - Hinweis; fehlender Ordner - Warnung",
                log.Hat("Die Mails von server3 liegen außerhalb des Thunderbird-Profils, aber im Benutzerprofil")
                && log.Hat("[WARNUNG] Testvogel: Der Mailordner von server4 fehlt"), log.Text);

            Directory.Delete(extern1, true);
            log.Leeren();
            dienst(Path.Combine(basis, "Vogel-Anwender")).RestoreAppProfile(Testvogel).Wait();
            Check("Thunderbird: Mails an ihren Ort zurueckgeschrieben",
                log.Hat("[ERFOLG] Testvogel: Mails von server2 an ihren Ort außerhalb des Profils zurückgeschrieben") && File.Exists(Path.Combine(extern1, "Inbox")), log.Text);

            // Das Laufwerk gibt es hier nicht: in den Profilordner, prefs.js angepasst.
            File.WriteAllText(liste, File.ReadAllText(liste).Replace(extern1.Replace("\\", "\\\\"), "Q:\\\\gibt-es-nicht\\\\Local Folders"));
            log.Leeren();
            var anwender2 = Path.Combine(basis, "Vogel-Anwender2");
            dienst(anwender2).RestoreAppProfile(Testvogel).Wait();
            var neuProfil = Path.Combine(AppCatalog.Pfad(anwender2, Testvogel.ProfilRel), "Profiles", "vogel.default");
            Check("Thunderbird: Laufwerk fehlt - Mails im Profil, prefs.js angepasst",
                log.Hat("das Laufwerk von Q:\\gibt-es-nicht\\Local Folders gibt es hier nicht") && File.Exists(Path.Combine(neuProfil, "Mail", "Extern-server2", "Inbox"))
                && File.ReadAllText(Path.Combine(neuProfil, "prefs.js")).Contains("mail.server.server2.directory-rel\", \"[ProfD]Mail/Extern-server2\""), log.Text);

            File.WriteAllText(liste, "{ kaputt");
            log.Leeren();
            dienst(anwender2).RestoreAppProfile(Testvogel).Wait();
            Check("Thunderbird: Liste unlesbar - Fehler", log.Hat("[FEHLER] Testvogel: Die Liste der Mailordner außerhalb des Profils ist unlesbar"), log.Text);
            File.Delete(liste);
        }

        // Ohne feste Ordner: die echte Suche nach Firefox - auf dem Windows-
        // Laeufer ist eins installiert, sonst meist nicht.
        d.TestInstallationen = null;
        log.Leeren();
        d.RestoreAppProfile(Testfuchs).Wait();
        var echt = OperatingSystem.IsWindows() ? MozillaProfile.Installationen("firefox.exe", "Mozilla Firefox", ziel) : new List<string>();
        Check($"Dienst: echte Suche ({(echt.Count == 0 ? "kein Firefox" : string.Join(", ", echt))})",
            echt.Count == 0 ? log.Hat("[WARNUNG] Testfuchs ist auf diesem PC nicht installiert") : log.Hat($"ist jetzt Standard für {string.Join(", ", echt)}"), log.Text);
    }

    /// <summary>
    /// Mit dem echten Firefox des Windows-Laeufers: Nimmt er nach dem
    /// Zurueckschreiben das gesicherte Profil? Und rechnet er die Schluessel
    /// genauso wie das Tool? Nur in der CI - Firefox schreibt dabei in den
    /// echten Profilordner des Benutzers (der Ordner wird vorher beiseitegelegt
    /// und danach zurueckgeholt).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void FirefoxEchtPruefen(string root)
    {
        Console.WriteLine("\n== Firefox echt: Standardprofil nach dem Zurueckschreiben ==");
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installiert = MozillaProfile.Installationen("firefox.exe", "Mozilla Firefox", profil);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || installiert.Count == 0)
        {
            Console.WriteLine($"         (nur in der CI mit installiertem Firefox - hier {(installiert.Count == 0 ? "kein Firefox" : "keine CI")}, uebersprungen)");
            return;
        }
        var firefox = installiert[0];
        Console.WriteLine($"         Firefox: {firefox} (Schluessel {MozillaProfile.Schluessel(firefox)})");
        Check("echt: Installationsordner wie auf der Platte", string.Equals(MozillaProfile.EchterPfad(firefox.ToUpperInvariant()), firefox, StringComparison.Ordinal),
            MozillaProfile.EchterPfad(firefox.ToUpperInvariant()));

        var daten = AppCatalog.Pfad(profil, AppCatalog.Firefox.ProfilRel);
        var beiseite = daten + "_dasi_pruefung";
        if (Directory.Exists(daten)) Directory.Move(daten, beiseite);
        var kopien = new List<string>();
        try
        {
            // 1. Wiederherstellen ueber den Dienst - Sicherung von einem PC mit 32-Bit-Firefox.
            var sicherung = Path.Combine(root, "firefox-echt");
            var gesichert = Path.Combine(sicherung, AppCatalog.Firefox.BackupOrdner);
            MozillaDaten(gesichert, ProfilesIniGesperrt, ("neu0abcd.default", DateTime.UtcNow), ("alt0abcd.default", DateTime.UtcNow.AddYears(-3)));
            foreach (var p in new[] { "neu0abcd.default", "alt0abcd.default" })
                File.WriteAllText(Path.Combine(gesichert, "Profiles", p, "compatibility.ini"),
                    "[Compatibility]\r\nLastVersion=115.18.0_20241104101315/20241104101315\r\nLastOSABI=WINNT_x86-msvc\r\n");
            var c = new CancellationTokenState();
            var log = new Protokoll();
            new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung }.RestoreAppProfile(AppCatalog.Firefox).Wait();
            Check("echt: Firefox bereits installiert, Profil zurueck, als Standard eingetragen",
                log.Hat("Firefox ist bereits installiert") && log.Hat("[ERFOLG] Firefox Profil wiederhergestellt") && log.Hat("ist jetzt Standard für"), log.Text);
            foreach (var alt in Directory.GetDirectories(Path.GetDirectoryName(daten)!, "Firefox_alt_*")) OrdnerLoeschen(alt);

            FirefoxStarten(Path.Combine(firefox, "firefox.exe"), () => Version(daten, "neu0abcd.default") != "115.18.0_20241104101315/20241104101315");
            var neuAngelegt = NeueProfile(daten);
            Check($"echt: Profil aus Firefox ESR 115 (32 Bit) - Firefox {MozillaProfile.InstallierteFassung(firefox)?.Version} (64 Bit) nimmt es", Version(daten, "neu0abcd.default").Length > 0
                && Version(daten, "neu0abcd.default") != "115.18.0_20241104101315/20241104101315",
                $"compatibility.ini: {Version(daten, "neu0abcd.default")}");
            Check("echt: kein neues, leeres Profil", neuAngelegt.Count == 0, string.Join(", ", neuAngelegt));

            // 2. Gegenprobe: dieselbe Sicherung ohne Anpassung - so wie bisher.
            OrdnerLoeschen(daten);
            OrdnerKopieren(gesichert, daten);
            FirefoxStarten(Path.Combine(firefox, "firefox.exe"), () => NeueProfile(daten).Count > 0);
            Check("echt, Gegenprobe: ohne Anpassung legt Firefox ein neues Profil an", NeueProfile(daten).Count > 0, "keins angelegt");

            // 3. Keine Installation gefunden: nur Default=1 - uebernimmt Firefox das Profil?
            OrdnerLoeschen(daten);
            OrdnerKopieren(gesichert, daten);
            MozillaProfile.Anpassen(daten, Array.Empty<string>());
            FirefoxStarten(Path.Combine(firefox, "firefox.exe"), () => Version(daten, "neu0abcd.default") != "115.18.0_20241104101315/20241104101315");
            Check("echt: ohne Installationseintrag uebernimmt Firefox das Standardprofil",
                Version(daten, "neu0abcd.default") != "115.18.0_20241104101315/20241104101315" && NeueProfile(daten).Count == 0,
                $"{Version(daten, "neu0abcd.default")}; neu: {string.Join(", ", NeueProfile(daten))}");

            // 3b. Das Profil lief zuletzt mit einer neueren Fassung. Gleiche
            //     Hauptversion: Firefox nimmt es. Neuere Hauptversion: Firefox
            //     lehnt es ab - der Dienst meldet das als Fehler.
            if (MozillaProfile.InstallierteFassung(firefox) is { } fassung && int.TryParse(fassung.Version.Split('.')[0], out var haupt))
            {
                foreach (var (lastVersion, nimmt) in new[] { ($"{fassung.Version}.99_29991231000000/29991231000000", true), ($"{haupt + 1}.0_29991231000000/29991231000000", false) })
                {
                    File.WriteAllText(Path.Combine(gesichert, "Profiles", "neu0abcd.default", "compatibility.ini"), $"[Compatibility]\r\nLastVersion={lastVersion}\r\n");
                    OrdnerLoeschen(daten);
                    var log2 = new Protokoll();
                    new BackupService(new ProcessRunner(c), c, log2.Schreiben) { SourcePath = profil, BackupPath = sicherung }.RestoreAppProfile(AppCatalog.Firefox).Wait();
                    foreach (var alt in Directory.GetDirectories(Path.GetDirectoryName(daten)!, "Firefox_alt_*")) OrdnerLoeschen(alt);
                    Check(nimmt ? "echt: gleiche Hauptversion, etwas neuer - kein Hinweis" : "echt: neuere Hauptversion - Fehler mit Abhilfe",
                        nimmt ? !log2.Hat("lief zuletzt mit") : log2.Hat("lehnt ein Profil einer neueren Hauptversion ab"), log2.Text);
                    FirefoxStarten(Path.Combine(firefox, "firefox.exe"),
                        () => NeueProfile(daten).Count > 0 || Version(daten, "neu0abcd.default").StartsWith(fassung.Version + "_", StringComparison.Ordinal));
                    var genommen = Version(daten, "neu0abcd.default").StartsWith(fassung.Version + "_", StringComparison.Ordinal) && NeueProfile(daten).Count == 0;
                    Check(nimmt ? $"echt: Firefox {fassung.Version} nimmt ein Profil von {lastVersion.Split('_')[0]}" : $"echt: Firefox {fassung.Version} lehnt ein Profil von {lastVersion.Split('_')[0]} ab",
                        genommen == nimmt, $"{Version(daten, "neu0abcd.default")}; neu: {string.Join(", ", NeueProfile(daten))}");
                }
                File.WriteAllText(Path.Combine(gesichert, "Profiles", "neu0abcd.default", "compatibility.ini"),
                    "[Compatibility]\r\nLastVersion=115.18.0_20241104101315/20241104101315\r\nLastOSABI=WINNT_x86-msvc\r\n");
            }

            // 4. Schluessel jeder Laenge: Firefox aus anderen Ordnern starten
            //    (Dateien hart verknuepft - kein Kopieren) und nachsehen, unter
            //    welchem Schluessel er sich eintraegt.
            foreach (var ordner in new[] { @"C:\F", @"C:\FFox", @"C:\Firefox-1",
                                           @"C:\DaSi-Pruefung\Firefox mit einem sehr langen Ordnernamen fuer zwei Runden\Mozilla Firefox" })
            {
                if (!HartVerknuepfen(firefox, ordner)) { Console.WriteLine($"         {ordner}: nicht anzulegen, uebersprungen"); continue; }
                kopien.Add(ordner);
                OrdnerLoeschen(daten);
                var echt = MozillaProfile.EchterPfad(ordner);
                var schluessel = MozillaProfile.Schluessel(echt);
                FirefoxStarten(Path.Combine(ordner, "firefox.exe"), () => InstallSchluessel(daten).Count > 0);
                var firefoxSagt = InstallSchluessel(daten);
                Check($"echt: Schluessel fuer {ordner} ({ByteLaenge(echt)} Bytes) wie Firefox", firefoxSagt.Contains(schluessel),
                    $"Tool {schluessel}, Firefox {string.Join(", ", firefoxSagt)}");
            }
        }
        finally
        {
            AlleBeenden("firefox");
            try { OrdnerLoeschen(daten); } catch (Exception ex) { Console.WriteLine("         Aufraeumen: " + ex.Message); }
            if (Directory.Exists(beiseite)) Directory.Move(beiseite, daten);
            foreach (var k in kopien) try { Directory.Delete(k, true); } catch { }
            try { Directory.Delete(@"C:\DaSi-Pruefung", true); } catch { }
        }
    }

    /// <summary>
    /// Beendet alle Prozesse dieses Namens und wartet, bis keiner mehr laeuft.
    /// Kill kehrt sofort zurueck - ohne Warten hielt Firefox parent.lock noch,
    /// waehrend die Pruefung den Profilordner schon loeschen wollte.
    /// </summary>
    private static void AlleBeenden(string name)
    {
        var uhr = Stopwatch.StartNew();
        while (uhr.Elapsed < TimeSpan.FromSeconds(30))
        {
            var liste = Process.GetProcessesByName(name);
            if (liste.Length == 0) return;
            foreach (var p in liste) { try { p.Kill(true); p.WaitForExit(5000); } catch { } p.Dispose(); }
            System.Threading.Thread.Sleep(300);
        }
    }

    /// <summary>Loescht einen Ordner, falls da - mit Wiederholung, solange ein eben beendetes Programm noch Dateien haelt.</summary>
    private static void OrdnerLoeschen(string ordner)
    {
        for (int versuch = 1; ; versuch++)
        {
            try { if (Directory.Exists(ordner)) Directory.Delete(ordner, true); return; }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && versuch < 40)
            { System.Threading.Thread.Sleep(500); }
        }
    }

    private static int ByteLaenge(string s) => s.Length * 2;

    private static void OrdnerKopieren(string quelle, string ziel)
    {
        foreach (var datei in Directory.GetFiles(quelle, "*", SearchOption.AllDirectories))
        {
            var neu = Path.Combine(ziel, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(neu)!);
            File.Copy(datei, neu, true);
        }
    }

    private static string Version(string daten, string profil)
    {
        var datei = Path.Combine(daten, "Profiles", profil, "compatibility.ini");
        try { return File.Exists(datei) ? MozillaProfile.Ini.Lesen(File.ReadAllText(datei)).Wert("Compatibility", "LastVersion") ?? "" : ""; }
        catch { return ""; }
    }

    /// <summary>Profile, die Firefox selbst angelegt hat (default-release ...).</summary>
    private static List<string> NeueProfile(string daten)
    {
        var p = Path.Combine(daten, "Profiles");
        return Directory.Exists(p)
            ? Directory.GetDirectories(p).Select(Path.GetFileName).OfType<string>()
                       .Where(n => n != "neu0abcd.default" && n != "alt0abcd.default").ToList()
            : new List<string>();
    }

    private static List<string> InstallSchluessel(string daten)
    {
        try
        {
            var datei = Path.Combine(daten, "profiles.ini");
            return File.Exists(datei)
                ? MozillaProfile.Ini.Lesen(File.ReadAllText(datei)).Abschnitte.Select(a => a.Name)
                    .Where(n => n.StartsWith("Install")).Select(n => n["Install".Length..]).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Startet Firefox ohne Fenster, wartet, bis die Bedingung gilt (hoechstens 45 s), und beendet ihn.</summary>
    [SupportedOSPlatform("windows")]
    private static void FirefoxStarten(string exe, Func<bool> fertig)
    {
        var uhr = Stopwatch.StartNew();
        using (var p = Process.Start(new ProcessStartInfo(exe, "-headless -no-remote about:blank") { UseShellExecute = false }))
        {
            while (uhr.Elapsed < TimeSpan.FromSeconds(45) && !fertig()) System.Threading.Thread.Sleep(500);
            // Noch einen Moment: profiles.ini und compatibility.ini fertig schreiben lassen.
            System.Threading.Thread.Sleep(3000);
            try { p?.Kill(true); } catch { }
        }
        AlleBeenden("firefox");
        Console.WriteLine($"         {exe}: {uhr.Elapsed.TotalSeconds:F0} s");
    }

    /// <summary>Legt den Ordner neu an, jede Datei hart verknuepft mit dem Original.</summary>
    [SupportedOSPlatform("windows")]
    private static bool HartVerknuepfen(string quelle, string ziel)
    {
        try
        {
            foreach (var datei in Directory.GetFiles(quelle, "*", SearchOption.AllDirectories))
            {
                var neu = Path.Combine(ziel, Path.GetRelativePath(quelle, datei));
                Directory.CreateDirectory(Path.GetDirectoryName(neu)!);
                if (!CreateHardLinkW(neu, datei, IntPtr.Zero)) File.Copy(datei, neu);
            }
            return true;
        }
        catch (Exception ex) { Console.WriteLine($"         {ziel}: {ex.Message}"); return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string neu, string vorhanden, IntPtr security);
}
