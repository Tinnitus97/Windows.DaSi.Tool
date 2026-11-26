using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Der Umzug mit echtem Firefox, wie er beim Kunden laeuft: auf dem alten PC
/// Firefox ESR 32 Bit, auf dem neuen die aktuelle Fassung 64 Bit. In der CI in
/// zwei Schritten, dazwischen deinstalliert der Workflow Firefox ESR; die
/// aktuelle 64-Bit-Fassung ist auf dem Windows-Laeufer installiert:
///
///  DASI_NUR=firefox-alt: Firefox ESR (32 Bit) legt sein Profil an, dazu eine
///    Einstellung als Erkennungszeichen. Dann sichert das Tool.
///  DASI_NUR=firefox-neu: Datenordner weg (neuer PC). Gegenprobe zur Auskunft:
///    die Sicherung nur zurueckkopiert - welches Profil oeffnet Firefox? Dann
///    mit dem Tool: Firefox (64 Bit) oeffnet das Profil vom alten PC.
///
/// Die Sicherung liegt in DASI_SICHERUNG.
/// </summary>
internal static partial class Program
{
    private const string FxEsr32 = @"C:\Program Files (x86)\Mozilla Firefox";
    private const string FxAktuell64 = @"C:\Program Files\Mozilla Firefox";
    private const string FxMarke = "user_pref(\"dasi.umzug.marke\", \"alter PC\");";

    private static string FxDaten => AppCatalog.Pfad(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppCatalog.Firefox.ProfilRel);

    private static List<string> FxProfile()
    {
        var p = Path.Combine(FxDaten, "Profiles");
        return Directory.Exists(p) ? Directory.GetDirectories(p).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList() : new List<string>();
    }

    private static string FxKompat(string profil, string schluessel)
    {
        var datei = Path.Combine(FxDaten, "Profiles", profil, "compatibility.ini");
        return File.Exists(datei) ? MozillaProfile.Ini.Lesen(File.ReadAllText(datei)).Wert("Compatibility", schluessel) ?? "" : "";
    }

    /// <summary>Die Profile, die Firefox 64 Bit geoeffnet hat (64-Bit-Kennung in compatibility.ini).</summary>
    private static List<string> Fx64Benutzt()
        => FxProfile().Where(p => FxKompat(p, "LastOSABI").Contains("x86_64", StringComparison.OrdinalIgnoreCase)).ToList();

    [SupportedOSPlatform("windows")]
    private static void FirefoxUmzugStarten(string ordner, Func<bool> fertig)
    {
        var uhr = Stopwatch.StartNew();
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(ordner, "firefox.exe"), "-headless -no-remote about:blank") { UseShellExecute = false }))
        {
            while (uhr.Elapsed < TimeSpan.FromSeconds(60) && !fertig()) System.Threading.Thread.Sleep(500);
            System.Threading.Thread.Sleep(5000);
            try { p?.Kill(true); } catch { }
        }
        foreach (var rest in Process.GetProcessesByName("firefox")) { try { rest.Kill(true); } catch { } rest.Dispose(); }
        System.Threading.Thread.Sleep(2000);
        Console.WriteLine($"         {ordner}\\firefox.exe ({MozillaProfile.InstallierteFassung(ordner)?.Version}): {uhr.Elapsed.TotalSeconds:F0} s");
    }

    // ------------------------------------------------------------ alter PC

    [SupportedOSPlatform("windows")]
    private static void FirefoxAlterPc()
    {
        Console.WriteLine("\n== Firefox echt, alter PC: ESR 32 Bit, Profil anlegen, sichern ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        var esr = MozillaProfile.InstallierteFassung(FxEsr32);
        Check("alter PC: Firefox ESR 32 Bit installiert", File.Exists(Path.Combine(FxEsr32, "firefox.exe")), $"{FxEsr32}: {esr?.Version}");
        if (!File.Exists(Path.Combine(FxEsr32, "firefox.exe"))) return;
        if (Directory.Exists(FxDaten)) Directory.Delete(FxDaten, true);

        // Erster Start: Firefox ESR legt sein Profil an ("default-esr").
        FirefoxUmzugStarten(FxEsr32, () => TbProfilDer(FxDaten, FxEsr32) is { } p && FxKompat(p, "LastVersion").Length > 0);
        Zeigen("profiles.ini (ESR 32 Bit)", Path.Combine(FxDaten, "profiles.ini"));
        var benutzt = TbProfilDer(FxDaten, FxEsr32);
        Check("alter PC: Firefox ESR hat sein Profil angelegt, der 32-Bit-Installation zugeordnet",
            benutzt is not null && Directory.Exists(Path.Combine(FxDaten, "Profiles", benutzt)), $"Profile: {string.Join(", ", FxProfile())}");
        if (benutzt is null) return;
        Zeigen("compatibility.ini (ESR 32 Bit)", Path.Combine(FxDaten, "Profiles", benutzt, "compatibility.ini"));
        File.AppendAllText(Path.Combine(FxDaten, "Profiles", benutzt, "prefs.js"), "\n" + FxMarke + "\n");

        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Schreiben)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.BackupAppProfile(AppCatalog.Firefox).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("alter PC: Profil gesichert", log.Hat("[ERFOLG] Firefox Profil gesichert"), log.Text);
    }

    // ------------------------------------------------------------ neuer PC

    [SupportedOSPlatform("windows")]
    private static void FirefoxNeuerPc()
    {
        Console.WriteLine("\n== Firefox echt, neuer PC: aktuelle Fassung 64 Bit, wiederherstellen ==");
        var sicherung = Environment.GetEnvironmentVariable("DASI_SICHERUNG") ?? throw new InvalidOperationException("DASI_SICHERUNG fehlt");
        var aktuell = MozillaProfile.InstallierteFassung(FxAktuell64);
        Check("neuer PC: aktuelles Firefox 64 Bit da, ESR 32 Bit nicht mehr",
            File.Exists(Path.Combine(FxAktuell64, "firefox.exe")) && !File.Exists(Path.Combine(FxEsr32, "firefox.exe")), $"{FxAktuell64}: {aktuell?.Version}");
        if (!File.Exists(Path.Combine(FxAktuell64, "firefox.exe"))) return;
        var gesichert = Path.Combine(sicherung, AppCatalog.Firefox.BackupOrdner);
        var altesProfil = TbProfilDer(gesichert, FxEsr32);
        var anzahl = MozillaProfile.Profilordner(gesichert).Count;
        Check("neuer PC: Sicherung vom alten PC da", altesProfil is not null, gesichert);
        if (altesProfil is null) return;
        Console.WriteLine($"         Profil vom alten PC: {altesProfil} (von {anzahl} Profilen), zuletzt mit {FxKompat2(gesichert, altesProfil)}");

        void NeuerPc()
        {
            foreach (var p in Process.GetProcessesByName("firefox")) { try { p.Kill(true); } catch { } p.Dispose(); }
            if (Directory.Exists(FxDaten)) Directory.Delete(FxDaten, true);
            foreach (var alt in Directory.Exists(Path.GetDirectoryName(FxDaten)!) ? Directory.GetDirectories(Path.GetDirectoryName(FxDaten)!, "Firefox_alt_*") : Array.Empty<string>())
                Directory.Delete(alt, true);
        }

        // 1. Gegenprobe (zur Auskunft): die Sicherung nur zurueckkopiert.
        NeuerPc();
        OrdnerKopieren(gesichert, FxDaten);
        FirefoxUmzugStarten(FxAktuell64, () => Fx64Benutzt().Count > 0 || FxProfile().Count > anzahl);
        Zeigen("profiles.ini nach dem Start (Gegenprobe)", Path.Combine(FxDaten, "profiles.ini"));
        var gegenprobe = Fx64Benutzt();
        Console.WriteLine(gegenprobe.Contains(altesProfil)
            ? "         Gegenprobe: Firefox 64 Bit oeffnet das Profil der ESR-Installation auch ohne Anpassung."
            : $"         Gegenprobe: ohne Anpassung oeffnet Firefox 64 Bit {(gegenprobe.Count > 0 ? string.Join(", ", gegenprobe) : "ein neues Profil")} - das Profil vom alten PC bliebe ungenutzt, Firefox erschiene leer.");
        Check("Gegenprobe: ohne Anpassung nimmt Firefox nicht das Profil vom alten PC (der Fehler beim Kunden)",
            !gegenprobe.Contains(altesProfil), $"geoeffnet: {string.Join(", ", gegenprobe)}; Profile: {string.Join(", ", FxProfile())}");

        // 2. Mit dem Tool.
        NeuerPc();
        var c = new CancellationTokenState();
        var log = new Protokoll();
        new BackupService(new ProcessRunner(c), c, log.Schreiben)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = sicherung,
        }.RestoreAppProfile(AppCatalog.Firefox).Wait();
        foreach (var z in log.Zeilen.Where(z => z.Contains("[") && !z.Contains("Beende"))) Console.WriteLine("         Dienst: " + z.Trim());
        Check("Tool: Profil zurueck, Standard fuer Firefox 64 Bit, alter Ort erkannt",
            log.Hat("[ERFOLG] Firefox Profil wiederhergestellt") && log.Hat($"ist jetzt Standard für {FxAktuell64}") && log.Hat($"lief Firefox aus {FxEsr32}"), log.Text);
        Check("Tool: keine Meldung zur Fassung (ESR ist aelter als die aktuelle)", !log.Hat("lief zuletzt mit"), log.Text);
        Zeigen("profiles.ini nach dem Zurueckschreiben", Path.Combine(FxDaten, "profiles.ini"));

        FirefoxUmzugStarten(FxAktuell64, () => Fx64Benutzt().Count > 0 || FxProfile().Count > anzahl);
        Zeigen("compatibility.ini nach dem Start", Path.Combine(FxDaten, "Profiles", altesProfil, "compatibility.ini"));
        var geoeffnet = Fx64Benutzt();
        Check($"Firefox {aktuell?.Version} (64 Bit) oeffnet das Profil vom alten PC (ESR 32 Bit) - und nur dieses",
            geoeffnet.Count == 1 && geoeffnet[0] == altesProfil, $"geoeffnet: {string.Join(", ", geoeffnet)}, erwartet {altesProfil}");
        Check("kein neues, leeres Profil", FxProfile().Count == anzahl, string.Join(", ", FxProfile()));
        Check("Erkennungszeichen vom alten PC im geoeffneten Profil",
            File.ReadAllText(Path.Combine(FxDaten, "Profiles", altesProfil, "prefs.js")).Contains(FxMarke));
    }

    private static string FxKompat2(string datenOrdner, string profil)
    {
        var datei = Path.Combine(datenOrdner, "Profiles", profil, "compatibility.ini");
        return File.Exists(datei) ? MozillaProfile.Ini.Lesen(File.ReadAllText(datei)).Wert("Compatibility", "LastVersion") + " / "
            + MozillaProfile.Ini.Lesen(File.ReadAllText(datei)).Wert("Compatibility", "LastOSABI") : "";
    }
}
