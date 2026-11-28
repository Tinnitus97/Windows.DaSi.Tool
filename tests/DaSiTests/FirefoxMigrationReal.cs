using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// The migration with a real Firefox, as it happens in practice: Firefox ESR
/// 32 bit on the old PC, the current 64-bit release on the new one. In CI
/// in two steps; in between, the workflow uninstalls Firefox ESR; the
/// current 64-bit release is installed on the Windows runner:
///
///  DASI_NUR=firefox-alt: Firefox ESR (32 bit) creates its profile, plus a
///    setting as a marker. Then the tool backs it up.
///  DASI_NUR=firefox-neu: data folder gone (new PC). Cross-check for
///    information: the backup only copied back - which profile does Firefox
///    open? Then with the tool: Firefox (64 bit) opens the old PC's profile.
///
/// The backup lives in DASI_SICHERUNG.
/// </summary>
internal static partial class Program
{
    private const string FxEsr32 = @"C:\Program Files (x86)\Mozilla Firefox";
    private const string FxAktuell64 = @"C:\Program Files\Mozilla Firefox";
    private const string FxMarke = "user_pref(\"dasi.umzug.marke\", \"alter PC\");";

    private static string FxDaten => AppCatalog.PathIn(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppCatalog.Firefox.ProfilRel);

    private static List<string> FxProfiles()
    {
        var p = Path.Combine(FxDaten, "Profiles");
        return Directory.Exists(p) ? Directory.GetDirectories(p).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList() : new List<string>();
    }

    private static string FxCompat(string profil, string schluessel)
    {
        var datei = Path.Combine(FxDaten, "Profiles", profil, "compatibility.ini");
        return File.Exists(datei) ? MozillaProfile.Ini.Read(File.ReadAllText(datei)).Value("Compatibility", schluessel) ?? "" : "";
    }

    /// <summary>The profiles Firefox 64 bit has opened (64-bit ID in compatibility.ini).</summary>
    private static List<string> Fx64Used()
        => FxProfiles().Where(p => FxCompat(p, "LastOSABI").Contains("x86_64", StringComparison.OrdinalIgnoreCase)).ToList();

    [SupportedOSPlatform("windows")]
    private static void StartFirefoxForMigration(string ordner, Func<bool> fertig)
    {
        var uhr = Stopwatch.StartNew();
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(ordner, "firefox.exe"), "-headless -no-remote about:blank") { UseShellExecute = false }))
        {
            while (uhr.Elapsed < TimeSpan.FromSeconds(60) && !AskDone(fertig)) System.Threading.Thread.Sleep(500);
            System.Threading.Thread.Sleep(5000);
            try { p?.Kill(true); } catch { }
        }
        KillAllNamed("firefox");
        Console.WriteLine($"         {ordner}\\firefox.exe ({MozillaProfile.InstalledVersion(ordner)?.Version}): {uhr.Elapsed.TotalSeconds:F0} s");
    }

    // ------------------------------------------------------------ old PC

    [SupportedOSPlatform("windows")]
    private static void FirefoxOldPc()
    {
        Console.WriteLine("\n== Firefox echt, alter PC: ESR 32 Bit, Profil anlegen, sichern ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        var esr = MozillaProfile.InstalledVersion(FxEsr32);
        Check("alter PC: Firefox ESR 32 Bit installiert", File.Exists(Path.Combine(FxEsr32, "firefox.exe")), $"{FxEsr32}: {esr?.Version}");
        if (!File.Exists(Path.Combine(FxEsr32, "firefox.exe"))) return;
        DeleteFolder(FxDaten);

        // First start: Firefox ESR creates its profile ("default-esr").
        StartFirefoxForMigration(FxEsr32, () => TbProfileOf(FxDaten, FxEsr32) is { } p && FxCompat(p, "LastVersion").Length > 0);
        Dump("profiles.ini (ESR 32 Bit)", Path.Combine(FxDaten, "profiles.ini"));
        var benutzt = TbProfileOf(FxDaten, FxEsr32);
        Check("alter PC: Firefox ESR hat sein Profil angelegt, der 32-Bit-Installation zugeordnet",
            benutzt is not null && Directory.Exists(Path.Combine(FxDaten, "Profiles", benutzt)), $"Profile: {string.Join(", ", FxProfiles())}");
        if (benutzt is null) return;
        Dump("compatibility.ini (ESR 32 Bit)", Path.Combine(FxDaten, "Profiles", benutzt, "compatibility.ini"));
        File.AppendAllText(Path.Combine(FxDaten, "Profiles", benutzt, "prefs.js"), "\n" + FxMarke + "\n");

        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.BackupAppProfile(AppCatalog.Firefox).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("alter PC: Profil gesichert", log.Has("[ERFOLG] Firefox Profil gesichert"), log.Text);
    }

    // ------------------------------------------------------------ new PC

    [SupportedOSPlatform("windows")]
    private static void FirefoxNewPc()
    {
        Console.WriteLine("\n== Firefox echt, neuer PC: aktuelle Fassung 64 Bit, wiederherstellen ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        var aktuell = MozillaProfile.InstalledVersion(FxAktuell64);
        Check("neuer PC: aktuelles Firefox 64 Bit da, ESR 32 Bit nicht mehr",
            File.Exists(Path.Combine(FxAktuell64, "firefox.exe")) && !File.Exists(Path.Combine(FxEsr32, "firefox.exe")), $"{FxAktuell64}: {aktuell?.Version}");
        if (!File.Exists(Path.Combine(FxAktuell64, "firefox.exe"))) return;
        var gesichert = Path.Combine(sicherung, AppCatalog.Firefox.BackupOrdner);
        var altesProfil = TbProfileOf(gesichert, FxEsr32);
        var anzahl = MozillaProfile.ProfileFolders(gesichert).Count;
        Check("neuer PC: Sicherung vom alten PC da", altesProfil is not null, gesichert);
        if (altesProfil is null) return;
        Console.WriteLine($"         Profil vom alten PC: {altesProfil} (von {anzahl} Profilen), zuletzt mit {FxCompatSummary(gesichert, altesProfil)}");

        void NewPc()
        {
            KillAllNamed("firefox");
            DeleteFolder(FxDaten);
            foreach (var alt in Directory.Exists(Path.GetDirectoryName(FxDaten)!) ? Directory.GetDirectories(Path.GetDirectoryName(FxDaten)!, "Firefox_alt_*") : Array.Empty<string>())
                DeleteFolder(alt);
        }

        // 1. Cross-check (for information): the backup only copied back.
        NewPc();
        CopyFolder(gesichert, FxDaten);
        StartFirefoxForMigration(FxAktuell64, () => Fx64Used().Count > 0 || FxProfiles().Count > anzahl);
        Dump("profiles.ini nach dem Start (Gegenprobe)", Path.Combine(FxDaten, "profiles.ini"));
        var gegenprobe = Fx64Used();
        Console.WriteLine(gegenprobe.Contains(altesProfil)
            ? "         Gegenprobe: Firefox 64 Bit oeffnet das Profil der ESR-Installation auch ohne Anpassung."
            : $"         Gegenprobe: ohne Anpassung oeffnet Firefox 64 Bit {(gegenprobe.Count > 0 ? string.Join(", ", gegenprobe) : "ein neues Profil")} - das Profil vom alten PC bliebe ungenutzt, Firefox erschiene leer.");
        Check("Gegenprobe: ohne Anpassung nimmt Firefox nicht das Profil vom alten PC (der Fehler beim Kunden)",
            !gegenprobe.Contains(altesProfil), $"geoeffnet: {string.Join(", ", gegenprobe)}; Profile: {string.Join(", ", FxProfiles())}");

        // 2. With the tool.
        NewPc();
        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.RestoreAppProfile(AppCatalog.Firefox).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("Tool: Profil zurueck, Standard fuer Firefox 64 Bit, alter Ort erkannt",
            log.Has("[ERFOLG] Firefox Profil wiederhergestellt") && log.Has($"ist jetzt Standard für {FxAktuell64}") && log.Has($"lief Firefox aus {FxEsr32}"), log.Text);
        Check("Tool: keine Meldung zur Fassung (ESR ist aelter als die aktuelle)", !log.Has("lief zuletzt mit"), log.Text);
        // Uninstalling ESR removes the shared "App Paths" entry.
        Check("Tool: Firefox 64 Bit als installiert erkannt - kein winget", log.Has("[INFO] Firefox ist bereits installiert.") && !log.Has("via Winget"), log.Text);
        Dump("profiles.ini nach dem Zurueckschreiben", Path.Combine(FxDaten, "profiles.ini"));

        StartFirefoxForMigration(FxAktuell64, () => Fx64Used().Count > 0 || FxProfiles().Count > anzahl);
        Dump("compatibility.ini nach dem Start", Path.Combine(FxDaten, "Profiles", altesProfil, "compatibility.ini"));
        var geoeffnet = Fx64Used();
        Check($"Firefox {aktuell?.Version} (64 Bit) oeffnet das Profil vom alten PC (ESR 32 Bit) - und nur dieses",
            geoeffnet.Count == 1 && geoeffnet[0] == altesProfil, $"geoeffnet: {string.Join(", ", geoeffnet)}, erwartet {altesProfil}");
        Check("kein neues, leeres Profil", FxProfiles().Count == anzahl, string.Join(", ", FxProfiles()));
        Check("Erkennungszeichen vom alten PC im geoeffneten Profil",
            File.ReadAllText(Path.Combine(FxDaten, "Profiles", altesProfil, "prefs.js")).Contains(FxMarke));
    }

    private static string FxCompatSummary(string datenOrdner, string profil)
    {
        var datei = Path.Combine(datenOrdner, "Profiles", profil, "compatibility.ini");
        return File.Exists(datei) ? MozillaProfile.Ini.Read(File.ReadAllText(datei)).Value("Compatibility", "LastVersion") + " / "
            + MozillaProfile.Ini.Read(File.ReadAllText(datei)).Value("Compatibility", "LastOSABI") : "";
    }
}
