using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// The migration with a real Thunderbird, as it happens in practice:
/// Thunderbird 32 bit on the old PC, 64 bit on the new one. In CI in two
/// steps; in between, the workflow uninstalls the 32-bit version and
/// installs the 64-bit version:
///
///  DASI_NUR=thunderbird-alt: Thunderbird (32 bit) creates its profile, plus
///    two accounts - "Local Folders" inside the profile and an archive
///    outside it (C:\DaSi-Mail\Archiv) - with one mail each. Then the tool
///    backs it up.
///  DASI_NUR=thunderbird-neu: profile folder and archive are gone (new PC).
///    Cross-check (for information only): the backup only copied back -
///    which profile does Thunderbird open? Then with the tool: Thunderbird
///    (64 bit) takes the old profile, and the archive is back.
///
/// The backup lives in DASI_SICHERUNG.
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

    /// <summary>The profile folders under Profiles.</summary>
    private static List<string> TbProfiles()
    {
        var p = Path.Combine(TbDaten, "Profiles");
        return Directory.Exists(p) ? Directory.GetDirectories(p).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList() : new List<string>();
    }

    /// <summary>The profile profiles.ini assigns to an installation ("xxxx.default-release").</summary>
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
    /// Which profile Thunderbird 64 bit opened: the one whose compatibility.ini
    /// now carries the 64-bit ID (before, it had the 32-bit ID or nothing). The
    /// version alone does not tell - 32 and 64 bit carry the same one.
    /// </summary>
    private static List<string> Tb64Used()
        => TbProfiles().Where(p => TbAbi(p).Contains("x86_64", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Starts Thunderbird without a window, waits until the condition holds (at most 60 s), and closes it.</summary>
    [SupportedOSPlatform("windows")]
    private static void StartThunderbird(string ordner, Func<bool> fertig)
    {
        var uhr = Stopwatch.StartNew();
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(ordner, "thunderbird.exe"), "-headless -no-remote") { UseShellExecute = false }))
        {
            while (uhr.Elapsed < TimeSpan.FromSeconds(60) && !AskDone(fertig)) System.Threading.Thread.Sleep(500);
            // One more moment: load the accounts, let profiles.ini and compatibility.ini finish writing.
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

    // ------------------------------------------------------------ old PC

    [SupportedOSPlatform("windows")]
    private static void ThunderbirdOldPc()
    {
        Console.WriteLine("\n== Thunderbird echt, alter PC: 32 Bit, Profil anlegen, sichern ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        Check("alter PC: Thunderbird 32 Bit installiert", File.Exists(Path.Combine(Tb32, "thunderbird.exe")), Tb32);
        if (!File.Exists(Path.Combine(Tb32, "thunderbird.exe"))) return;
        DeleteFolder(TbDaten);

        // First start: Thunderbird creates its profile.
        StartThunderbird(Tb32, () => TbProfileOf(TbDaten, Tb32) is { } p && TbVersion(p).Length > 0);
        Dump("profiles.ini (32 Bit)", Path.Combine(TbDaten, "profiles.ini"));
        var benutzt = TbProfileOf(TbDaten, Tb32);
        Check("alter PC: Thunderbird hat ein Profil angelegt, der 32-Bit-Installation zugeordnet",
            benutzt is not null && Directory.Exists(Path.Combine(TbDaten, "Profiles", benutzt)), $"Profile: {string.Join(", ", TbProfiles())}");
        if (benutzt is null) return;
        var profil = Path.Combine(TbDaten, "Profiles", benutzt);
        Dump("compatibility.ini (32 Bit)", Path.Combine(profil, "compatibility.ini"));

        // Two accounts: Local Folders inside the profile, an archive outside it.
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

        // Thunderbird loads the accounts once (as in everyday use), then the backup runs.
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

    // ------------------------------------------------------------ new PC

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

        // 1. Cross-check: the backup only copied back.
        NewPc();
        CopyFolder(gesichert, TbDaten);
        StartThunderbird(Tb64, () => Tb64Used().Count > 0);
        Dump("profiles.ini nach dem Start (Gegenprobe)", Path.Combine(TbDaten, "profiles.ini"));
        var gegenprobe = Tb64Used();
        Console.WriteLine($"         Profile: {string.Join(", ", TbProfiles())}; von Thunderbird 64 Bit geoeffnet: {string.Join(", ", gegenprobe)}");
        // For information only: unlike Firefox, Thunderbird (161, CI) takes over
        // another installation's profile even without adjustment - it registers an
        // entry of its own pointing to the same profile.
        Console.WriteLine(gegenprobe.Contains(altesProfil)
            ? "         Gegenprobe: Thunderbird 64 Bit uebernimmt das Profil der 32-Bit-Installation auch ohne Anpassung."
            : $"         Gegenprobe: ohne Anpassung oeffnet Thunderbird 64 Bit ein anderes Profil ({string.Join(", ", gegenprobe)}) - das alte waere leer erschienen.");

        // 2. With the tool.
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
