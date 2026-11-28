using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Der Umzug mit echtem Thunderbird, wie er beim Kunden laeuft: auf dem alten
/// PC Thunderbird 32 Bit, auf dem neuen 64 Bit. In der CI in zwei Schritten,
/// dazwischen deinstalliert der Workflow die 32-Bit-Fassung und installiert
/// die 64-Bit-Fassung:
///
///  DASI_NUR=thunderbird-alt: Thunderbird (32 Bit) legt sein Profil an, dazu
///    zwei Konten - "Lokale Ordner" im Profil und ein Archiv ausserhalb
///    (C:\DaSi-Mail\Archiv) - mit je einer Mail. Dann sichert das Tool.
///  DASI_NUR=thunderbird-neu: Profilordner und Archiv sind weg (neuer PC).
///    Gegenprobe (nur zur Auskunft): die Sicherung nur zurueckkopiert - welches
///    Profil oeffnet Thunderbird? Dann mit dem Tool: Thunderbird (64 Bit) nimmt
///    das alte Profil, das Archiv ist wieder da.
///
/// Die Sicherung liegt in DASI_SICHERUNG.
/// </summary>
internal static partial class Program
{
    private const string Tb32 = @"C:\Program Files (x86)\Mozilla Thunderbird";
    private const string Tb64 = @"C:\Program Files\Mozilla Thunderbird";
    private const string TbArchiv = @"C:\DaSi-Mail\Archiv";
    private const string TbMarke = "DaSi-Pruefmail vom alten PC";

    private static string TbDaten => AppCatalog.PathIn(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppCatalog.Thunderbird.ProfilRel);

    private static string Mbox(string betreff)
        => $"From - Mon Nov 25 10:00:00 2026\r\nFrom: alt@example.com\r\nTo: neu@example.com\r\nSubject: {betreff}\r\nDate: Mon, 25 Nov 2026 10:00:00 +0100\r\n\r\n{TbMarke}\r\n\r\n";

    /// <summary>Die Profilordner unter Profiles.</summary>
    private static List<string> TbProfiles()
    {
        var p = Path.Combine(TbDaten, "Profiles");
        return Directory.Exists(p) ? Directory.GetDirectories(p).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList() : new List<string>();
    }

    /// <summary>Das Profil, das profiles.ini einer Installation zuordnet ("xxxx.default-release").</summary>
    private static string? TbProfileOf(string datenOrdner, string installation)
    {
        var ini = Path.Combine(datenOrdner, "profiles.ini");
        if (!File.Exists(ini)) return null;
        var pfad = MozillaProfile.Ini.Read(File.ReadAllText(ini)).Value("Install" + MozillaProfile.InstallHash(installation), "Default");
        return pfad is null ? null : Path.GetFileName(pfad.Replace('/', '\\'));
    }

    private static string TbVersion(string profil)
        => MozillaProfile.Ini.Read(File.Exists(Path.Combine(TbDaten, "Profiles", profil, "compatibility.ini"))
            ? File.ReadAllText(Path.Combine(TbDaten, "Profiles", profil, "compatibility.ini")) : "").Value("Compatibility", "LastVersion") ?? "";

    private static string TbAbi(string profil)
        => MozillaProfile.Ini.Read(File.Exists(Path.Combine(TbDaten, "Profiles", profil, "compatibility.ini"))
            ? File.ReadAllText(Path.Combine(TbDaten, "Profiles", profil, "compatibility.ini")) : "").Value("Compatibility", "LastOSABI") ?? "";

    /// <summary>
    /// Welches Profil Thunderbird 64 Bit geoeffnet hat: das, in dessen
    /// compatibility.ini jetzt die 64-Bit-Kennung steht (vorher stand dort die
    /// 32-Bit-Kennung oder gar nichts). Die Version allein verraet es nicht -
    /// 32 und 64 Bit tragen dieselbe.
    /// </summary>
    private static List<string> Tb64Used()
        => TbProfiles().Where(p => TbAbi(p).Contains("x86_64", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Startet Thunderbird ohne Fenster, wartet, bis die Bedingung gilt (hoechstens 60 s), und beendet ihn.</summary>
    [SupportedOSPlatform("windows")]
    private static void StartThunderbird(string ordner, Func<bool> fertig)
    {
        var uhr = Stopwatch.StartNew();
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(ordner, "thunderbird.exe"), "-headless -no-remote") { UseShellExecute = false }))
        {
            while (uhr.Elapsed < TimeSpan.FromSeconds(60) && !AskDone(fertig)) System.Threading.Thread.Sleep(500);
            // Noch einen Moment: Konten laden, profiles.ini und compatibility.ini fertig schreiben.
            System.Threading.Thread.Sleep(8000);
            try { p?.Kill(true); } catch { }
        }
        KillAllNamed("thunderbird");
        Console.WriteLine($"         {ordner}\\thunderbird.exe ({MozillaProfile.InstalledVersion(ordner)?.Version}): {uhr.Elapsed.TotalSeconds:F0} s");
    }

    private static void Dump(string titel, string datei)
    {
        Console.WriteLine($"   -- {titel} --");
        if (!File.Exists(datei)) { Console.WriteLine("         (fehlt)"); return; }
        foreach (var z in File.ReadAllLines(datei)) Console.WriteLine("         " + z);
    }

    private static void DumpFolder(string titel, string ordner)
    {
        Console.WriteLine($"   -- {titel}: {ordner} --");
        if (!Directory.Exists(ordner)) { Console.WriteLine("         (fehlt)"); return; }
        foreach (var d in Directory.GetFiles(ordner, "*", SearchOption.AllDirectories))
            Console.WriteLine($"         {Path.GetRelativePath(ordner, d)} ({new FileInfo(d).Length} Bytes)");
    }

    // ------------------------------------------------------------ alter PC

    [SupportedOSPlatform("windows")]
    private static void ThunderbirdOldPc()
    {
        Console.WriteLine("\n== Thunderbird echt, alter PC: 32 Bit, Profil anlegen, sichern ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        Check("alter PC: Thunderbird 32 Bit installiert", File.Exists(Path.Combine(Tb32, "thunderbird.exe")), Tb32);
        if (!File.Exists(Path.Combine(Tb32, "thunderbird.exe"))) return;
        DeleteFolder(TbDaten);

        // Erster Start: Thunderbird legt sein Profil an.
        StartThunderbird(Tb32, () => TbProfileOf(TbDaten, Tb32) is { } p && TbVersion(p).Length > 0);
        Dump("profiles.ini (32 Bit)", Path.Combine(TbDaten, "profiles.ini"));
        var benutzt = TbProfileOf(TbDaten, Tb32);
        Check("alter PC: Thunderbird hat ein Profil angelegt, der 32-Bit-Installation zugeordnet",
            benutzt is not null && Directory.Exists(Path.Combine(TbDaten, "Profiles", benutzt)), $"Profile: {string.Join(", ", TbProfiles())}");
        if (benutzt is null) return;
        var profil = Path.Combine(TbDaten, "Profiles", benutzt);
        Dump("compatibility.ini (32 Bit)", Path.Combine(profil, "compatibility.ini"));

        // Zwei Konten: Lokale Ordner im Profil, ein Archiv ausserhalb.
        var lokal = Path.Combine(profil, "Mail", "Local Folders");
        Directory.CreateDirectory(lokal);
        File.WriteAllText(Path.Combine(lokal, "Inbox"), Mbox("Lokale Ordner"));
        Directory.CreateDirectory(TbArchiv);
        File.WriteAllText(Path.Combine(TbArchiv, "Inbox"), Mbox("Archiv ausserhalb des Profils"));
        string Js(string pfad) => pfad.Replace(@"\", @"\\");
        File.AppendAllText(Path.Combine(profil, "prefs.js"),
            "\nuser_pref(\"mail.accountmanager.accounts\", \"account1,account2\");\n"
            + "user_pref(\"mail.accountmanager.localfoldersserver\", \"server1\");\n"
            + "user_pref(\"mail.account.account1.server\", \"server1\");\n"
            + "user_pref(\"mail.account.account2.server\", \"server2\");\n"
            + "user_pref(\"mail.server.server1.type\", \"none\");\n"
            + "user_pref(\"mail.server.server1.hostname\", \"Local Folders\");\n"
            + "user_pref(\"mail.server.server1.name\", \"Lokale Ordner\");\n"
            + "user_pref(\"mail.server.server1.userName\", \"nobody\");\n"
            + $"user_pref(\"mail.server.server1.directory\", \"{Js(lokal)}\");\n"
            + "user_pref(\"mail.server.server1.directory-rel\", \"[ProfD]Mail/Local Folders\");\n"
            + "user_pref(\"mail.server.server2.type\", \"none\");\n"
            + "user_pref(\"mail.server.server2.hostname\", \"Archiv\");\n"
            + "user_pref(\"mail.server.server2.name\", \"DaSi-Archiv\");\n"
            + "user_pref(\"mail.server.server2.userName\", \"nobody\");\n"
            + $"user_pref(\"mail.server.server2.directory\", \"{Js(TbArchiv)}\");\n");

        // Thunderbird laedt die Konten einmal (wie im Alltag), dann wird gesichert.
        StartThunderbird(Tb32, () => false);
        DumpFolder("Archiv nach dem Start", TbArchiv);

        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.BackupAppProfile(AppCatalog.Thunderbird).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("alter PC: Profil gesichert", log.Has("[ERFOLG] Thunderbird Profil gesichert"), log.Text);
        Check("alter PC: Archiv ausserhalb des Profils mitgesichert", log.Has("[ERFOLG] Thunderbird: Mails von server2 außerhalb des Profils gesichert")
            && File.Exists(Path.Combine(sicherung, BackupService.ThunderbirdExternOrdner, "1_server2", "Inbox")), log.Text);
    }

    // ------------------------------------------------------------ neuer PC

    [SupportedOSPlatform("windows")]
    private static void ThunderbirdNewPc()
    {
        Console.WriteLine("\n== Thunderbird echt, neuer PC: 64 Bit, wiederherstellen ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        Check("neuer PC: Thunderbird 64 Bit installiert, 32 Bit nicht mehr",
            File.Exists(Path.Combine(Tb64, "thunderbird.exe")) && !File.Exists(Path.Combine(Tb32, "thunderbird.exe")));
        if (!File.Exists(Path.Combine(Tb64, "thunderbird.exe"))) return;
        var gesichert = Path.Combine(sicherung, AppCatalog.Thunderbird.BackupOrdner);
        var altesProfil = TbProfileOf(gesichert, Tb32);
        var anzahl = MozillaProfile.ProfileFolders(gesichert).Count;
        Check("neuer PC: Sicherung vom alten PC da", altesProfil is not null, gesichert);
        if (altesProfil is null) return;
        Console.WriteLine($"         Profil vom alten PC: {altesProfil} (von {anzahl} Profilen)");

        void NewPc()
        {
            KillAllNamed("thunderbird");
            DeleteFolder(TbDaten);
            DeleteFolder(Path.GetDirectoryName(TbArchiv)!);
            foreach (var alt in Directory.Exists(Path.GetDirectoryName(TbDaten)!) ? Directory.GetDirectories(Path.GetDirectoryName(TbDaten)!, "Thunderbird_alt_*") : Array.Empty<string>())
                DeleteFolder(alt);
        }

        // 1. Gegenprobe: die Sicherung nur zurueckkopiert.
        NewPc();
        CopyFolder(gesichert, TbDaten);
        StartThunderbird(Tb64, () => Tb64Used().Count > 0);
        Dump("profiles.ini nach dem Start (Gegenprobe)", Path.Combine(TbDaten, "profiles.ini"));
        var gegenprobe = Tb64Used();
        Console.WriteLine($"         Profile: {string.Join(", ", TbProfiles())}; von Thunderbird 64 Bit geoeffnet: {string.Join(", ", gegenprobe)}");
        // Nur zur Auskunft: Anders als Firefox uebernimmt Thunderbird (161, CI)
        // das Profil einer anderen Installation auch ohne Anpassung - es traegt
        // fuer sich einen eigenen Eintrag auf dasselbe Profil ein.
        Console.WriteLine(gegenprobe.Contains(altesProfil)
            ? "         Gegenprobe: Thunderbird 64 Bit uebernimmt das Profil der 32-Bit-Installation auch ohne Anpassung."
            : $"         Gegenprobe: ohne Anpassung oeffnet Thunderbird 64 Bit ein anderes Profil ({string.Join(", ", gegenprobe)}) - das alte waere leer erschienen.");

        // 2. Mit dem Tool.
        NewPc();
        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.RestoreAppProfile(AppCatalog.Thunderbird).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("Tool: Profil zurueck, Standard fuer Thunderbird 64 Bit, alter Ort erkannt",
            log.Has("[ERFOLG] Thunderbird Profil wiederhergestellt") && log.Has($"ist jetzt Standard für {Tb64}") && log.Has($"lief Thunderbird aus {Tb32}"), log.Text);
        Check("Tool: Archiv ausserhalb des Profils an seinen Ort zurueck",
            log.Has("[ERFOLG] Thunderbird: Mails von server2 an ihren Ort außerhalb des Profils zurückgeschrieben")
            && File.Exists(Path.Combine(TbArchiv, "Inbox")) && File.ReadAllText(Path.Combine(TbArchiv, "Inbox")).Contains(TbMarke), log.Text);
        Dump("profiles.ini nach dem Zurueckschreiben", Path.Combine(TbDaten, "profiles.ini"));

        var vorher = TbAbi(altesProfil);
        Check("vor dem Start: das Profil lief zuletzt mit 32 Bit", vorher.Contains("x86-msvc", StringComparison.OrdinalIgnoreCase), vorher);
        StartThunderbird(Tb64, () => Tb64Used().Count > 0);
        Dump("compatibility.ini nach dem Start", Path.Combine(TbDaten, "Profiles", altesProfil, "compatibility.ini"));
        var geoeffnet = Tb64Used();
        Check("Thunderbird 64 Bit oeffnet das Profil vom alten PC (32 Bit) - und nur dieses",
            geoeffnet.Count == 1 && geoeffnet[0] == altesProfil, $"geoeffnet: {string.Join(", ", geoeffnet)}, erwartet {altesProfil}; ABI vorher {vorher}, jetzt {TbAbi(altesProfil)}");
        Check("kein neues, leeres Profil", TbProfiles().Count == anzahl, string.Join(", ", TbProfiles()));
        var prefs = File.ReadAllText(Path.Combine(TbDaten, "Profiles", altesProfil, "prefs.js"));
        Check("Konten und Mails noch da: Lokale Ordner, Archiv",
            prefs.Contains("\"mail.server.server2.directory\", \"C:\\\\DaSi-Mail\\\\Archiv\"")
            && File.ReadAllText(Path.Combine(TbDaten, "Profiles", altesProfil, "Mail", "Local Folders", "Inbox")).Contains(TbMarke)
            && File.ReadAllText(Path.Combine(TbArchiv, "Inbox")).Contains(TbMarke));
        DumpFolder("Archiv nach dem Start (64 Bit)", TbArchiv);
    }
}
