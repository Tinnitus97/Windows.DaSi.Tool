using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// The backup service (BackupService) as a whole: every action of the queue,
/// with real robocopy, netsh and PrintBrm, on a test profile in the temp
/// folder. Programs are installed by the winget fake; "running browsers" are
/// copies of ping.exe under the browser's name.
/// </summary>
internal static partial class Program
{
    /// <summary>A program like Firefox or Chrome - only with a name of its own, so nothing real is closed.</summary>
    private static readonly AppEintrag Testbrowser =
        new(Punkt.Chrome, "Testbrowser", @"AppData\Local\DaSiTest\Testbrowser\User Data", "dasi-testbrowser", "DaSi.Testbrowser", true);
    private static readonly AppEintrag OhnePaket =
        new(Punkt.Edge, "Ohnepaket", @"AppData\Local\DaSiTest\Ohnepaket\User Data", "dasi-ohnepaket", null, true);
    private static readonly AppEintrag Kaputtpaket =
        new(Punkt.Brave, "Kaputtpaket", @"AppData\Local\DaSiTest\Kaputtpaket\User Data", "dasi-kaputtpaket", "DaSi.Kaputt", true);

    private static void WriteFile(string pfad, string inhalt = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, inhalt);
    }

    /// <summary>A user profile as it looks on a customer's computer - just small.</summary>
    private static string CreateTestProfile(string pfad)
    {
        WriteFile(Path.Combine(pfad, "Documents", "brief.txt"), "Sehr geehrte Damen und Herren");
        WriteFile(Path.Combine(pfad, "Desktop", "Notizen.txt"), "Einkaufsliste");
        WriteFile(Path.Combine(pfad, "NTUSER.DAT"), "Registry - darf nie kopiert werden");
        WriteFile(Path.Combine(pfad, @"AppData\Roaming\Werkzeug\einstellungen.ini"), "a=1");
        WriteFile(Path.Combine(pfad, @"AppData\Local\Temp\muell.tmp"), "Zwischenspeicher");
        WriteFile(Path.Combine(pfad, "OneDrive", "Vertrag.docx"), "Cloud");
        // A program profile like Chrome's: data folder with "Local State", profile "Default".
        var daten = Path.Combine(pfad, Testbrowser.ProfilRel);
        WriteFile(Path.Combine(daten, "Local State"), "{\"profile\":{}}");
        WriteFile(Path.Combine(daten, "Default", "Preferences"), "{\"homepage\":\"https://example.test\"}");
        WriteFile(Path.Combine(daten, "Default", "Bookmarks"), "{\"roots\":{}}");
        WriteFile(Path.Combine(daten, "Default", "Cache", "Cache_Data", "f_000001"), new string('c', 2000));
        // Opera in three editions.
        var opera = Path.Combine(pfad, AppCatalog.Opera.Rel);
        WriteFile(Path.Combine(opera, "Opera Stable", "Local State"), "{}");
        WriteFile(Path.Combine(opera, "Opera Stable", "Preferences"), "{}");
        WriteFile(Path.Combine(opera, "Opera GX Stable", "Default", "Preferences"), "{}");
        WriteFile(Path.Combine(opera, "Opera Air", "Local State"), "{}");
        return pfad;
    }

    /// <summary>Starts a "running browser": ping.exe under another name.</summary>
    private static Process? RunningProcess(string root, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(root, "prozesse", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        if (!File.Exists(exe)) File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        return Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
    }

    private static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;

    private static void BackupServiceCheck(string root)
    {
        Console.WriteLine("\n== Sicherungsdienst: Fehlerfaelle ohne Sicherung ==");
        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        var basis = Path.Combine(root, "dienst");
        var profil = CreateTestProfile(Path.Combine(basis, "Besitzer"));
        var leerProfil = Path.Combine(basis, "Leer");
        Directory.CreateDirectory(leerProfil);
        var leer = Path.Combine(basis, "LeereSicherung");
        Directory.CreateDirectory(leer);
        BackupService Service(string quelle, string ziel) => new(runner, c, log.Write) { SourcePath = quelle, BackupPath = ziel };

        void RunCase(string name, Action<BackupService> a, string erwartet, string? quelle = null, string? ziel = null)
        {
            log.Clear();
            a(Service(quelle ?? leerProfil, ziel ?? leer));
            Check(name, log.Has(erwartet), log.Text);
        }
        RunCase("Profil zurueck ohne Sicherung", d => d.RestoreUserProfile().Wait(), "[FEHLER] Backup-Ordner nicht gefunden");
        RunCase("Profil sichern ins eigene Profil: abgelehnt", d => d.BackupUserProfile().Wait(), "Ziel liegt im Quellverzeichnis", profil, Path.Combine(profil, "Sicherung"));
        RunCase("AppData sichern ohne AppData", d => d.BackupAppDataOnly().Wait(), "[FEHLER] AppData nicht gefunden");
        RunCase("Cloud sichern ohne Cloud-Ordner", d => d.BackupCloudOnly().Wait(), "kein Cloud-Ordner gefunden");
        RunCase("AppData zurueck ohne Sicherung", d => d.RestoreAppDataOnly().Wait(), "In der Sicherung liegt kein AppData");
        RunCase("Cloud zurueck ohne Sicherung", d => d.RestoreCloudOnly().Wait(), "[FEHLER] Backup-Ordner nicht gefunden");
        RunCase("Programmprofil sichern, das es nicht gibt", d => d.BackupAppProfile(Testbrowser).Wait(), "[FEHLER] Pfad nicht gefunden");
        RunCase("Opera sichern ohne Opera", d => d.BackupOpera().Wait(), "Kein Opera-Profil gefunden");
        RunCase("Opera zurueck ohne Sicherung", d => d.RestoreOpera().Wait(), "Opera Backup nicht gefunden");
        RunCase("WLAN zurueck ohne Sicherung", d => d.ImportWlan().Wait(), "WLAN Ordner nicht gefunden");
        RunCase("Desktop-Foto oeffnen ohne Foto", d => d.ShowDesktopScreenshot().Wait(), "kein Desktop-Screenshot");
        RunCase("Symbole zurueck ohne Sicherung", d => d.RestoreDesktopIcons().Wait(), "weder Symbolpositionen noch Taskleisten-Symbole");
        if (OperatingSystem.IsWindows())
        {
            RunCase("AppData sichern ins eigene AppData: abgelehnt", d => d.BackupAppDataOnly().Wait(), "Ziel liegt im Quellverzeichnis", profil, Path.Combine(profil, "AppData", "x"));
            RunCase("Drucker zurueck ohne Sicherung", d => d.ImportPrinters().Wait(), "Keine Druckersicherung gefunden");
            RunCase("Design eines anderen Benutzers: abgelehnt", d => d.ExportDesign().Wait(), "[FEHLER] Design: Angemeldet ist", profil);
            RunCase("Desktop-Foto eines anderen Benutzers: abgelehnt", d => d.CaptureDesktop().Wait(), "[FEHLER] Kein Desktop-Screenshot", profil);
            RunCase("Symbole eines anderen Benutzers: abgelehnt", d => d.BackupDesktopIcons().Wait(), "Keine Symbolpositionen gesichert", profil);
        }
        else
        {
            RunCase("Drucker ohne PrintBrm", d => d.ExportPrinters().Wait(), "PrintBrm.exe wurde nicht gefunden");
            RunCase("Drucker zurueck ohne PrintBrm", d => d.ImportPrinters().Wait(), "PrintBrm.exe wurde nicht gefunden");
        }

        DesktopIconsViaService(root, c, runner);

        Console.WriteLine("\n== PrintBrm: Fehler in der Ausgabe trotz Exit-Code 0 ==");
        // Exactly what PrintBrm prints on Windows Server 2025 (exit code 0).
        var englisch = "Operation mode: restore\r\nTarget server: local machine\r\nThe following error occurred: 0x80070057.\r\nThe parameter is incorrect.\r\n\r\nCheck the eventlog for detailed information about the error which occurred.";
        Check("Fehlercode und Erklaerung erkannt (englisch)",
            BackupService.PrintBrmError(englisch) == "The following error occurred: 0x80070057. The parameter is incorrect.", BackupService.PrintBrmError(englisch));
        Check("Fehlercode erkannt (deutsch)", BackupService.PrintBrmError("Betriebsmodus: Wiederherstellen\nFolgender Fehler ist aufgetreten: 0x80070002.\nDas System kann die Datei nicht finden.")
            ?.Contains("0x80070002") == true);
        Check("Erfolg ohne Fehlercode: kein Fehler", BackupService.PrintBrmError("Operation mode: restore\nSuccessfully finished operation.") is null
            && BackupService.PrintBrmError(null) is null && BackupService.PrintBrmError("") is null);

        if (!OperatingSystem.IsWindows()) return;

        // ---- Profile, AppData, cloud with real robocopy ----
        Console.WriteLine("\n== Sicherungsdienst: Benutzerprofil, AppData und Cloud ==");
        var sicherung = Path.Combine(basis, "Sicherung");
        var aussen = Path.Combine(basis, "Spiele-auf-D");
        WriteFile(Path.Combine(aussen, "spielstand.sav"));
        try { Directory.CreateSymbolicLink(Path.Combine(profil, "Documents", "Spiele"), aussen); } catch { }

        log.Clear();
        var d = Service(profil, sicherung);
        d.BackupUserProfile().Wait();
        var gesichert = Path.Combine(sicherung, "Benutzerprofil");
        Check("Profil gesichert", log.Has("[ERFOLG] Benutzerprofil gesichert.") && File.Exists(Path.Combine(gesichert, "Documents", "brief.txt")), log.Text);
        Check("ohne Haken: kein AppData, keine Cloud-Ordner, nie NTUSER.DAT",
            !Directory.Exists(Path.Combine(gesichert, "AppData")) && !Directory.Exists(Path.Combine(gesichert, "OneDrive")) && !File.Exists(Path.Combine(gesichert, "NTUSER.DAT")));
        Check("Verknuepfung nach aussen: Warnung im Protokoll", log.Has("außerhalb des Profils"), log.Text);

        // With AppData and cloud - one file is locked as if by a running program.
        var gesperrt = Path.Combine(profil, @"AppData\Roaming\Werkzeug\gesperrt.db");
        WriteFile(gesperrt, "offen");
        log.Clear();
        using (new FileStream(gesperrt, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            d = Service(profil, sicherung);
            d.IncludeAppData = true;
            d.IncludeCloud = true;
            d.BackupUserProfile().Wait();
        }
        Check("mit Haken: AppData ohne Zwischenspeicher, Cloud-Ordner dabei",
            File.Exists(Path.Combine(gesichert, @"AppData\Roaming\Werkzeug\einstellungen.ini"))
            && !Directory.Exists(Path.Combine(gesichert, @"AppData\Local\Temp"))
            && File.Exists(Path.Combine(gesichert, "OneDrive", "Vertrag.docx")), log.Text);
        Check("mit Haken: im Protokoll vermerkt", log.Has("AppData wird mitgesichert") && log.Has("Cloud-Ordner werden mitgesichert: OneDrive"), log.Text);
        Check("gesperrte Datei: zusammengefasst, Liste neben der Sicherung, trotzdem Erfolg",
            log.Has("[ERFOLG] Benutzerprofil gesichert - bis auf") && File.Exists(Path.Combine(sicherung, RobocopySkips.ListFileName)), log.Text);

        // Again without a locked file: the list from before is wrong then and is removed.
        File.Delete(gesperrt);
        log.Clear();
        d = Service(profil, sicherung);
        d.IncludeAppData = true;
        d.BackupUserProfile().Wait();
        Check("ohne gesperrte Datei: alte Liste entfernt", log.Has("[ERFOLG] Benutzerprofil gesichert.") && !File.Exists(Path.Combine(sicherung, RobocopySkips.ListFileName)), log.Text);

        log.Clear();
        d = Service(leerProfil, Path.Combine(basis, "NurCloud"));
        d.IncludeCloud = true;
        d.BackupUserProfile().Wait();
        Check("Cloud gewuenscht, aber keiner da: Hinweis", log.Has("es wurde aber keiner gefunden"), log.Text);

        // Restore into a new profile.
        var neu = Path.Combine(basis, "Anwender");
        Directory.CreateDirectory(neu);
        log.Clear();
        d = Service(neu, sicherung);
        d.IncludeCloud = true;
        d.RestoreUserProfile().Wait();
        Check("Profil zurueck: Dokumente da, AppData ohne Haken nicht",
            log.Has("[ERFOLG] Benutzerprofil wiederhergestellt.") && File.Exists(Path.Combine(neu, "Documents", "brief.txt"))
            && !Directory.Exists(Path.Combine(neu, "AppData")), log.Text);
        Check("Cloud zurueck: neben den Cloud-Ordner, nicht hinein",
            File.Exists(Path.Combine(neu, CloudProviders.RestoredFolderName("OneDrive"), "Vertrag.docx")) && !Directory.Exists(Path.Combine(neu, "OneDrive")), log.Text);
        log.Clear();
        d = Service(neu, sicherung);
        d.IncludeAppData = true;
        d.IncludeCloud = true;
        d.RestoreUserProfile().Wait();
        Check("Profil zurueck mit AppData", log.Has("AppData wird mit wiederhergestellt") && File.Exists(Path.Combine(neu, @"AppData\Roaming\Werkzeug\einstellungen.ini")), log.Text);
        Check("Cloud zurueck ein zweites Mal: wird ergaenzt", log.Has("besteht schon"), log.Text);

        // AppData and cloud without the profile.
        var nurTeile = Path.Combine(basis, "NurTeile");
        log.Clear();
        d = Service(profil, nurTeile);
        d.BackupAppDataOnly().Wait();
        d.BackupCloudOnly().Wait();
        Check("nur AppData gesichert - an derselben Stelle wie mit Profil",
            log.Has("[ERFOLG] AppData gesichert.") && File.Exists(Path.Combine(nurTeile, @"Benutzerprofil\AppData\Roaming\Werkzeug\einstellungen.ini"))
            && !Directory.Exists(Path.Combine(nurTeile, @"Benutzerprofil\AppData\Local\Temp")), log.Text);
        Check("nur Cloud gesichert", log.Has("[ERFOLG] OneDrive gesichert") && File.Exists(Path.Combine(nurTeile, @"Benutzerprofil\OneDrive\Vertrag.docx")), log.Text);
        var neu2 = Path.Combine(basis, "Anwender2");
        Directory.CreateDirectory(neu2);
        log.Clear();
        d = Service(neu2, nurTeile);
        d.RestoreAppDataOnly().Wait();
        d.RestoreCloudOnly().Wait();
        Check("nur AppData zurueck", log.Has("[ERFOLG] AppData wiederhergestellt.") && File.Exists(Path.Combine(neu2, @"AppData\Roaming\Werkzeug\einstellungen.ini")), log.Text);
        Check("nur Cloud zurueck - daneben", File.Exists(Path.Combine(neu2, @"OneDrive_wiederhergestellt\Vertrag.docx")), log.Text);
        log.Clear();
        Service(neu2, leer).RestoreCloudOnly().Wait();
        Check("Cloud zurueck aus einer Sicherung ohne Cloud: Hinweis", log.Has("[FEHLER] Backup-Ordner nicht gefunden") || log.Has("enthält keine Cloud-Ordner"), log.Text);
        Directory.CreateDirectory(Path.Combine(leer, "Benutzerprofil"));
        log.Clear();
        Service(neu2, leer).RestoreCloudOnly().Wait();
        Check("Sicherung ohne Cloud-Ordner: Hinweis", log.Has("enthält keine Cloud-Ordner"), log.Text);

        // Running "browsers" are closed before AppData is restored.
        using (var edge = RunningProcess(root, AppCatalog.Edge.Prozess))
        {
            log.Clear();
            Service(neu2, nurTeile).RestoreAppDataOnly().Wait();
            Check("laufender Browser vor AppData-Rueckschreiben geschlossen", log.Has("Vor dem Zurückschreiben von AppData geschlossen: Edge") && !IsRunning(AppCatalog.Edge.Prozess), log.Text);
        }

        ProgramProfilesCheck(root, basis, profil, c, runner, log);
        WifiAndPrintersCheck(basis, c, runner, log);
    }

    private static void ProgramProfilesCheck(string root, string basis, string profil, CancellationTokenState c, ProcessRunner runner, Protokoll log)
    {
        Console.WriteLine("\n== Sicherungsdienst: Programmprofile (Browser, Opera) ==");
        var attrappe = Fakes.WingetFake();
        var ordner = Fakes.WingetScenario(root, Scenario(("install.DaSi.Kaputt.code", "1603"), ("install.Opera.OperaGX.code", "0")));
        var winget = new WingetService(runner, c, log.Write)
        {
            FesteKandidaten = attrappe is null ? Array.Empty<string>() : new[] { attrappe },
            Nachinstallation = () => System.Threading.Tasks.Task.FromResult(false),
        };
        var sicherung = Path.Combine(basis, "Programme");
        BackupService Service(string quelle) => new(runner, c, log.Write) { SourcePath = quelle, BackupPath = sicherung, Winget = winget };

        using (var p = RunningProcess(root, Testbrowser.Prozess))
        {
            log.Clear();
            Service(profil).BackupAppProfile(Testbrowser).Wait();
            Check("Programmprofil gesichert, laufendes Programm vorher beendet",
                log.Has("[ERFOLG] Testbrowser Profil gesichert.") && log.Has("Beende Testbrowser") && !IsRunning(Testbrowser.Prozess), log.Text);
        }
        var gesichert = Path.Combine(sicherung, Testbrowser.BackupOrdner);
        Check("Chromium-Zwischenspeicher bleibt draussen", File.Exists(Path.Combine(gesichert, "Default", "Preferences"))
            && !Directory.Exists(Path.Combine(gesichert, "Default", "Cache")));

        // Restore into a profile that already has a (fresh) browser profile.
        var ziel = Path.Combine(basis, "Browser-Anwender");
        var vorhanden = Path.Combine(ziel, Testbrowser.ProfilRel);
        WriteFile(Path.Combine(vorhanden, "Local State"), "frisch");
        var d = Service(ziel);
        bool nachgestartet = false;
        // While copying, the browser starts by itself (as after an install).
        d.OnFilesProgress = _ => { if (!nachgestartet) { nachgestartet = true; RunningProcess(root, Testbrowser.Prozess)?.Dispose(); } };
        log.Clear();
        using (var p = RunningProcess(root, Testbrowser.Prozess))
            d.RestoreAppProfile(Testbrowser).Wait();
        Check("nicht installiert: per winget (Attrappe) installiert", log.Has("Testbrowser wurde per winget installiert"), log.Text);
        Check("bisheriges Profil beiseitegelegt, nicht geloescht", log.Has("Bisheriges Testbrowser-Profil gesichert nach")
            && Directory.GetDirectories(Path.GetDirectoryName(vorhanden)!, "User Data_alt_*").Length == 1, log.Text);
        Check("waehrend des Kopierens gestartet: beendet und erneut kopiert", log.Has("während des Kopierens gestartet") && !IsRunning(Testbrowser.Prozess), log.Text);
        Check("Profil wiederhergestellt und abgeglichen", log.Has("[ERFOLG] Testbrowser Profil wiederhergestellt (Default)")
            && File.ReadAllText(Path.Combine(vorhanden, "Local State")) != "frisch", log.Text);

        // Without a winget package (like Edge) and with a failing install.
        WriteFile(Path.Combine(sicherung, OhnePaket.BackupOrdner, "Local State"), "{}");
        WriteFile(Path.Combine(sicherung, OhnePaket.BackupOrdner, "Default", "Preferences"), "{}");
        log.Clear();
        Service(ziel).RestoreAppProfile(OhnePaket).Wait();
        Check("ohne Paket: nur zurueckschreiben", log.Has("Prüfe, ob Ohnepaket installiert ist") && log.Has("[ERFOLG] Ohnepaket Profil wiederhergestellt"), log.Text);
        log.Clear();
        Service(ziel).RestoreAppProfile(Kaputtpaket).Wait();
        Check("Installation scheitert: Warnung, Backup fehlt: Fehler",
            log.Has("Kaputtpaket ließ sich per winget nicht installieren") && log.Has("[FEHLER] Kaputtpaket Backup nicht gefunden"), log.Text);

        // Without WingetService: "winget" from the search path - here the fake.
        if (attrappe is not null)
        {
            var pfad = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(attrappe) + ";" + pfad);
            try
            {
                Fakes.WriteScenario(ordner, Scenario(("install.*.code", WingetErrors.SchonInstalliert.ToString())));
                log.Clear();
                new BackupService(runner, c, log.Write) { SourcePath = ziel, BackupPath = sicherung }.RestoreAppProfile(Kaputtpaket).Wait();
                Check("winget aus dem Suchpfad: 'bereits installiert' erkannt", log.Has("Kaputtpaket ist bereits installiert"), log.Text);
            }
            finally { Environment.SetEnvironmentVariable("PATH", pfad); }
        }

        // The existing profile cannot be moved aside: touch nothing.
        var sperre = Path.Combine(vorhanden, "Default", "Preferences");
        log.Clear();
        using (new FileStream(sperre, FileMode.Open, FileAccess.Read, FileShare.None))
            Service(ziel).RestoreAppProfile(Testbrowser).Wait();
        Check("gesperrtes Profil: Abbruch, ohne etwas zu veraendern", log.Has("ließ sich nicht beiseitelegen") && File.Exists(sperre), log.Text);

        MozillaServiceCheck(basis, Service, log, ordner);

        // Opera: all editions, installed, reinstalled or just restored depending on the situation.
        Fakes.WriteScenario(ordner, Scenario(("install.*.code", "0")));
        log.Clear();
        Service(profil).BackupOpera().Wait();
        Check("Opera: alle Fassungen gefunden und gesichert", log.Has("Gefundene Opera-Fassungen: Opera, Opera Air, Opera GX")
            && log.Count("Profil gesichert") == 3, log.Text);
        var operaZiel = Path.Combine(basis, "Opera-Anwender");
        WriteFile(Path.Combine(operaZiel, @"AppData\Local\Programs\Opera\opera.exe"), "installiert");
        log.Clear();
        using (RunningProcess(root, AppCatalog.Opera.Prozess))
            Service(operaZiel).RestoreOpera().Wait();
        Check("Opera: vorhandene Fassung erkannt", log.Has("Opera ist bereits installiert"), log.Text);
        Check("Opera GX: per winget nachinstalliert", log.Has("Opera GX wurde per winget installiert"), log.Text);
        Check("Opera Air: kein Paket - Hinweis, Profil trotzdem zurueck", log.Has("Opera Air ist nicht installiert und lässt sich nicht automatisch"), log.Text);
        Check("Opera: alle drei zurueckgeschrieben", log.Count("Profil wiederhergestellt") == 3
            && File.Exists(Path.Combine(operaZiel, AppCatalog.Opera.Rel, "Opera GX Stable", "Default", "Preferences")), log.Text);

        c.RequestCancel();
        log.Clear();
        Service(operaZiel).RestoreOpera().Wait();
        Service(profil).BackupOpera().Wait();
        c.Reset();
        Check("Opera: Abbruch kopiert nichts mehr", !log.Has("Profil wiederhergestellt") && !log.Has("Profil gesichert"), log.Text);
        Environment.SetEnvironmentVariable("DASI_WINGET_FAKE", null);
    }

    private static void WifiAndPrintersCheck(string basis, CancellationTokenState c, ProcessRunner runner, Protokoll log)
    {
        Console.WriteLine("\n== Sicherungsdienst: WLAN (netsh) und Drucker (PrintBrm) ==");
        var sicherung = Path.Combine(basis, "Netz");
        var d = new BackupService(runner, c, log.Write) { SourcePath = basis, BackupPath = sicherung };

        log.Clear();
        d.ExportWlan().Wait();
        Check("WLAN-Export: Ordner angelegt, Ergebnis gemeldet (ohne WLAN-Dienst: Fehler mit Grund)",
            Directory.Exists(Path.Combine(sicherung, "WLAN-Profile")) && (log.Has("[FEHLER] WLAN-Export") || log.Has("WLAN-Profile exportiert") || log.Has("keine WLAN-Profile")), log.Text);

        log.Clear();
        d.ImportWlan().Wait();
        Check("WLAN-Import ohne Profile: 0 importiert", log.Has("[ERFOLG] 0 WLAN-Profile importiert"), log.Text);
        File.WriteAllText(Path.Combine(sicherung, "WLAN-Profile", "WLAN-Testnetz.xml"), "<WLANProfile>kaputt</WLANProfile>");
        log.Clear();
        d.ImportWlan().Wait();
        Check("WLAN-Import: unbrauchbares Profil wird als Fehler gezaehlt", log.Has("-> [FEHLER] WLAN-Testnetz") && log.Has("0 importiert, 1 fehlgeschlagen"), log.Text);

        log.Clear();
        var uhr = Stopwatch.StartNew();
        d.ExportPrinters().Wait();
        Console.WriteLine($"         PrintBrm: {uhr.Elapsed.TotalSeconds:F0} s");
        Check("Drucker-Export: Ergebnis gemeldet", log.Has("[ERFOLG] Drucker gesichert") || log.Has("[FEHLER] Druckersicherung fehlgeschlagen") || log.Has("PrintBrm.exe wurde nicht gefunden"), log.Text);

        WriteFile(Path.Combine(sicherung, "Drucker", "Drucker.printerExport"), "keine echte Druckersicherung");
        // What does PrintBrm itself say about an unusable file? (For evaluation in the CI log.)
        var brm = Path.Combine(Environment.SystemDirectory, "spool", "tools", "PrintBrm.exe");
        if (File.Exists(brm))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "WDST_Pruefung.printerExport");
            File.Copy(Path.Combine(sicherung, "Drucker", "Drucker.printerExport"), tmp, true);
            var p = Process.Start(new ProcessStartInfo(brm, "-R -F WDST_Pruefung.printerExport -O FORCE")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() })!;
            var aus = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine($"         PrintBrm -R mit unbrauchbarer Datei: ExitCode {p.ExitCode}, Ausgabe: {aus.Trim().Replace("\r", "").Replace("\n", " / ")}");
            try { File.Delete(tmp); } catch { }
        }
        log.Clear();
        d.ImportPrinters().Wait();
        Check("Drucker-Import: unbrauchbare Sicherung - trotz Exit-Code 0 als Fehler erkannt",
            log.Has("Druckerwiederherstellung fehlgeschlagen") && log.Has("0x8") && !log.Has("[ERFOLG]"), log.Text);
    }

    /// <summary>
    /// Desktop icons through the service - with a test desktop instead of
    /// Explorer, so missing icons and system icons can be played through as well.
    /// </summary>
    private static void DesktopIconsViaService(string root, CancellationTokenState c, ProcessRunner runner)
    {
        Console.WriteLine("\n== Sicherungsdienst: Desktop-Symbole (Test-Desktop) ==");
        var log = new Protokoll();
        // The service only works on the desktop of the signed-in user.
        var profil = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.Combine(root, "dienst", "Besitzer");
        var sicherung = Path.Combine(root, "dienst", "Symbole");
        var dieserPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        var vorher = DesktopIcons.OeffneDesktop;
        try
        {
            var alt = new TestDesktop(1920, 1080, true, true,
                new DesktopIcon("Brief.lnk", "Brief", 10, 10),
                new DesktopIcon("Rechnung.pdf", "Rechnung", 10, 110),
                new DesktopIcon("Programm.lnk", "Programm", 10, 210),
                new DesktopIcon(dieserPc, "Dieser PC", 110, 10));
            DesktopIcons.OeffneDesktop = () => alt;
            var d = new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = sicherung };
            d.BackupDesktopIcons().Wait();
            Check("Symbolpositionen gesichert (4 Symbole)", log.Has("[ERFOLG] Positionen von 4 Desktop-Symbolen gesichert") && DesktopIcons.Exists(sicherung), log.Text);
            Check("automatisch anordnen: vermerkt", log.Has("automatisch anordnen\" ist an"), log.Text);
            // Carry on without the taskbar - restoring it restarts Explorer.
            try { Directory.Delete(Taskbar.BackupFolder(sicherung), true); } catch { }
            // What was on the desktop in the backup comes from the profile.
            WriteFile(Path.Combine(sicherung, "Benutzerprofil", "Desktop", "Rechnung.pdf"));

            log.Clear();
            var leer = new TestDesktop(1920, 1080, false, false);
            DesktopIcons.OeffneDesktop = () => leer;
            d.BackupDesktopIcons().Wait();
            Check("leerer Desktop: nichts zu sichern", log.Has("keine Symbole"), log.Text);

            // New computer: smaller desktop, "This PC" off, two icons still missing.
            var neu = new TestDesktop(1280, 720, false, true, new DesktopIcon("Brief.lnk", "Brief", 500, 500))
            { SpaeterDazu = (1, new DesktopIcon(dieserPc, "Dieser PC", 600, 600)) };
            DesktopIcons.OeffneDesktop = () => neu;
            log.Clear();
            d.RestoreDesktopIcons().Wait();
            Check("Symbole zurueck: vorhandene gesetzt", log.Has("Desktop-Symbolen an ihre gesicherte Stelle gesetzt") && neu.Protokoll.Contains("setzen Brief.lnk"), log.Text);
            Check("anderer Bildschirm: Positionen umgerechnet", log.Has("im selben Verhältnis umgerechnet"), log.Text);
            Check("fehlendes Symbol aus dem Profil: eigener Hinweis", log.Has("gibt es auf diesem Desktop nicht: Rechnung"), log.Text);
            Check("fehlendes Symbol von aussen: Hinweis auf den oeffentlichen Desktop", log.Has("öffentlichen Desktop") && log.Has("Programm"), log.Text);
            Check("automatisch anordnen wie beim Sichern", log.Has("so ist es jetzt auch hier"), log.Text);
            if (OperatingSystem.IsWindows())
                Check("Systemsymbol 'Dieser PC' eingeschaltet", log.Has("Systemsymbole eingeschaltet wie auf dem alten Desktop: Dieser PC"), log.Text);

            // Nothing matches: error.
            DesktopIcons.OeffneDesktop = () => new TestDesktop(800, 600, false, false, new DesktopIcon("Fremd.lnk", "Fremd", 1, 1));
            var nurFremd = Path.Combine(root, "dienst", "SymboleFremd");
            DesktopIcons.Save(Path.Combine(nurFremd, DesktopIcons.FolderName), new DesktopLayout
            {
                Rechner = "ALT", Benutzer = "x", Erstellt = DateTime.Now, Breite = 800, Hoehe = 600,
                Symbole = { new DesktopIcon("Weg.lnk", "Weg", 5, 5) },
            });
            log.Clear();
            new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = nurFremd }.RestoreDesktopIcons().Wait();
            Check("kein Symbol zuzuordnen: Fehler", log.Has("[FEHLER] Kein gesichertes Symbol"), log.Text);

            // The desktop cannot be opened.
            DesktopIcons.OeffneDesktop = () => throw new InvalidOperationException("Explorer laeuft nicht");
            log.Clear();
            new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = nurFremd }.RestoreDesktopIcons().Wait();
            Check("Desktop nicht erreichbar beim Setzen: Fehler mit Grund", log.Has("Symbolpositionen ließen sich nicht setzen: Explorer laeuft nicht"), log.Text);
            log.Clear();
            new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = Path.Combine(root, "dienst", "SymboleNie") }.BackupDesktopIcons().Wait();
            Check("Desktop nicht erreichbar beim Lesen: Fehler mit Grund", log.Has("Desktop-Symbole ließen sich nicht lesen"), log.Text);
        }
        finally
        {
            DesktopIcons.OeffneDesktop = vorher;
        }
    }

    /// <summary>
    /// Design, desktop picture and icons on the real desktop of the signed-in
    /// user - Windows only. Runs at the end, because the wallpaper and Explorer
    /// really get changed.
    /// </summary>
    private static void BackupServiceOnRealDesktop(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Sicherungsdienst am echten Desktop: Design, Desktop-Foto ==");
        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sicherung = Path.Combine(root, "echt", "Sicherung");
        var d = new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = sicherung };

        // Desktop picture: capture, open (without an image viewer), error while opening.
        d.CaptureDesktop().Wait();
        Check("Desktop-Foto ueber den Dienst gespeichert", log.Has("[ERFOLG] Desktop-Screenshot gespeichert") && AppCatalog.HasDesktopScreenshot(sicherung), log.Text);
        var geoeffnet = new List<string>();
        d.BildOeffnen = geoeffnet.Add;
        log.Clear();
        d.ShowDesktopScreenshot().Wait();
        Check("Desktop-Foto oeffnen: jedes Bild einmal", geoeffnet.Count >= 1 && log.Has("[ERFOLG] Desktop-Screenshot geöffnet"), log.Text);
        d.BildOeffnen = _ => throw new InvalidOperationException("keine Bildanzeige");
        log.Clear();
        d.ShowDesktopScreenshot().Wait();
        Check("Desktop-Foto oeffnen scheitert: Fehler mit Ablageort", log.Has("konnte nicht geöffnet werden (keine Bildanzeige)"), log.Text);

        // Design: back up, set an image from an old backup, reset our own.
        log.Clear();
        d.ExportDesign().Wait();
        Check("Design gesichert (Design.json)", DesignSettings.Load(sicherung) is not null && log.Has("[ERFOLG] Design-Einstellungen gesichert"), log.Text);

        var alt = Path.Combine(root, "echt", "AlteSicherung");
        var bildAlt = Path.Combine(alt, "Hintergrundbild", "Urlaub.png");
        Directory.CreateDirectory(Path.GetDirectoryName(bildAlt)!);
        HintergrundEcht.SolidColorPng(bildAlt, (0x20, 0x60, 0xA0));
        File.WriteAllText(Path.Combine(alt, "Hintergrundbild", "Hintergrundbild-Pfad.txt"), @"C:\Users\Besitzer\Pictures\Urlaub.png");
        File.WriteAllText(Path.Combine(alt, "Hintergrundbild", "Hintergrundbild-Profil.txt"), @"C:\Users\Besitzer");
        log.Clear();
        new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = alt }.ImportDesign().Wait();
        var bildNeu = Path.Combine(profil, "Pictures", "Urlaub.png");
        Check("alte Sicherung: Bild ins gewaehlte Profil uebertragen", File.Exists(bildNeu) && log.Has("es kommt ins gewählte Profil"), log.Text);
        Check("alte Sicherung: Hintergrund gesetzt", log.Has("[ERFOLG] Hintergrund gesetzt") || log.Has("[WARNUNG] Hintergrund eingetragen"), log.Text);
        Check("alte Sicherung: ohne Design-Einstellungen nur der Hintergrund", log.Has("enthält keine Design-Einstellungen"), log.Text);

        // A backup with a solid color instead of an image.
        var farbig = Path.Combine(root, "echt", "Volltonfarbe");
        DesignSettings.Save(farbig, new DesignSicherung
        {
            Rechner = "ALT", Erstellt = DateTime.Now,
            Hintergrund = new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "30 90 150" },
        });
        log.Clear();
        new BackupService(runner, c, log.Write) { SourcePath = profil, BackupPath = farbig }.ImportDesign().Wait();
        Check("Volltonfarbe aus der Sicherung gesetzt", log.Has("Volltonfarbe RGB 30, 90, 150") && Wallpaper.ReadColor(UserWallpaper.Read(Microsoft.Win32.Registry.CurrentUser).Farbe) == (30, 90, 150), log.Text);

        // If the tool runs under another account: only register it, Windows shows it after sign-in.
        var fremd = UserWallpaper.SetColor(Microsoft.Win32.Registry.CurrentUser, eigenerBenutzer: false, (40, 40, 40), profil);
        Check("anderes Konto: eingetragen, ohne den Weg ueber den Explorer", fremd.Eingetragen && fremd.ExplorerFehler == "Tool läuft unter einem anderen Konto", fremd.ExplorerFehler);

        // Black desktop where an image was expected: Verify also looks at the screen.
        UserWallpaper.SetColor(Microsoft.Win32.Registry.CurrentUser, eigenerBenutzer: true, (0, 0, 0), profil);
        Thread.Sleep(1500);
        var schwarz = UserWallpaper.Verify(new HintergrundStand(@"C:\Bilder\Urlaub.png", "0 0 0", 0, "10", "0", Array.Empty<string>()), German);
        Console.WriteLine($"         Kontrolle bei schwarzem Desktop: {schwarz ?? "(Bildschirm nicht ueberwiegend schwarz)"}");
        Check("schwarzer Desktop statt Bild: erkannt (oder Bildschirm nicht schwarz)", schwarz is null || schwarz.Contains("schwarz statt des Bildes"), schwarz);
        var schirm = DesktopScreenshot.CaptureVirtualScreen();
        bool erwartet = DesktopScreenshot.ColorShare(schirm.Bgra, (0, 0, 0), 10) >= 0.5;
        Check("Bildschirm ueberwiegend schwarz? - wie eine eigene Aufnahme", UserWallpaper.IsScreenBlack() == erwartet, erwartet.ToString());

        // Apply a theme without a known user (directly through explorer.exe) - and with one that does not exist.
        var aktuell = UserWallpaper.ThemeFilePath(Microsoft.Win32.Registry.CurrentUser, profil);
        if (aktuell is not null && File.Exists(aktuell))
        {
            var kopie = Path.Combine(Path.GetDirectoryName(aktuell)!, WindowsDesign.NewFileName());
            File.Copy(aktuell, kopie);
            var seit = DateTime.Now;
            var (ok, fehler) = WindowsDesign.Apply(kopie, null).GetAwaiter().GetResult();
            Check("Design ohne bekannten Benutzer: direkt ueber explorer.exe", ok, fehler);
            Thread.Sleep(4000);
            WindowsDesign.CloseSettings(seit);
            var (ok2, fehler2) = WindowsDesign.Apply(kopie, @"NIEMAND\gibt-es-nicht").GetAwaiter().GetResult();
            Check("Design fuer einen unbekannten Benutzer: abgelehnt, mit Grund", !ok2 && !string.IsNullOrWhiteSpace(fehler2), fehler2);
        }

        log.Clear();
        d.ImportDesign().Wait();
        Check("eigenes Design zurueck: Einstellungen uebernommen", log.Has("[ERFOLG] Design-Einstellungen übernommen"), log.Text);
        try { File.Delete(bildNeu); } catch { }
    }

    /// <summary>Icons and taskbar on the real desktop - restarts Explorer, so it runs at the very end.</summary>
    private static void IconsOnRealDesktop(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Sicherungsdienst am echten Desktop: Symbole und Taskleiste ==");
        var c = new CancellationTokenState();
        var log = new Protokoll();
        var d = new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = Path.Combine(root, "echt", "Symbole"),
        };
        d.BackupDesktopIcons().Wait();
        Check("echte Symbole: gesichert oder leerer Desktop gemeldet",
            log.Has("Desktop-Symbolen gesichert") || log.Has("keine Symbole"), log.Text);
        Check("echte Taskleiste: gesichert oder nichts angeheftet", log.Has("Taskleiste gesichert") || log.Has("nichts angeheftet"), log.Text);

        // Alter the backup so that everything changes on the real desktop: another
        // grid, auto arrange, one more system icon.
        var ordner = Path.Combine(d.BackupPath, DesktopIcons.FolderName);
        var layout = DesktopIcons.Load(DesktopIcons.FilePath(d.BackupPath)) ?? new DesktopLayout
        {
            Rechner = Environment.MachineName, Benutzer = Environment.UserName, Erstellt = DateTime.Now, Breite = 1024, Hoehe = 768,
            Symbole = { new DesktopIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", "Papierkorb", 20, 20) },
        };
        DesktopIcons.Save(Path.Combine(root, "echt", "SymboleOriginal", DesktopIcons.FolderName), layout);
        layout.AmRasterAusrichten = !layout.AmRasterAusrichten;
        layout.AutomatischAnordnen = true;
        layout.Symbole.Add(new DesktopIcon("::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Netzwerk", 120, 20));
        DesktopIcons.Save(ordner, layout);

        log.Clear();
        d.RestoreDesktopIcons().Wait();
        Check("echte Symbole und Taskleiste zurueck", log.Has("Taskleiste wiederhergestellt") || log.Has("Desktop-Symbolen an ihre gesicherte Stelle gesetzt"), log.Text);
        Check("echter Desktop: Raster wie in der Sicherung umgestellt", log.Has("am Raster ausrichten"), log.Text);
        Check("echter Desktop: automatisch anordnen wieder an", log.Has("so ist es jetzt auch hier"), log.Text);
        Check("echter Desktop: Systemsymbol 'Netzwerk' eingeschaltet", log.Has("Systemsymbole eingeschaltet") && log.Has("Netzwerk"), log.Text);
        if (log.Has("Taskleiste wiederhergestellt"))
        {
            Check("echte Taskleiste: nach dem Neustart nachgesehen, alles behalten", !log.Has("nicht übernommen") && !log.Has("übernimmt die gesicherten"), log.Text);
            Check("echte Taskleiste: auf der Taskleiste nachgesehen, alle Symbole da",
                (log.Has("Die Taskleiste zeigt") || log.Has("ließ sich nicht nachsehen")) && !log.Has("zeigt diese angehefteten Symbole nicht"), log.Text);
        }

        // What the taskbar shows: an icon is missing although the program is
        // there - or the taskbar cannot be read.
        if (Taskbar.Load(d.BackupPath) is { Taskband.Count: > 0 } tl
            && Taskbar.Entries(Taskbar.Favorites(tl.Taskband), tl.Verknuepfungen) is { Count: > 1 } eintraege
            && eintraege[0].Kennung is { } nurDiese)
        {
            d.TaskleistenKontrolle = TimeSpan.FromSeconds(1);
            d.TaskleistenSicht = () => new HashSet<string> { nurDiese };
            log.Clear();
            d.RestoreDesktopIcons().Wait();
            Check("Taskleiste zeigt ein Symbol nicht: Fehler mit Namen und Kennung statt Erfolg",
                log.Has("[FEHLER] Die Taskleiste zeigt diese angehefteten Symbole nicht") && !log.Has("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
            d.TaskleistenSicht = () => null;
            log.Clear();
            d.RestoreDesktopIcons().Wait();
            Check("Taskleiste nicht lesbar: Hinweis, trotzdem Erfolg", log.Has("ließ sich nicht nachsehen") && log.Has("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
            d.TaskleistenSicht = null;
        }

        // Windows fills the taskbar by itself after the restart (as at one user's):
        // write once more, then an error instead of "success".
        if (Taskbar.Load(d.BackupPath) is { Taskband.Count: > 0 })
        {
            log.Clear();
            d.TaskleistenKontrolle = TimeSpan.FromSeconds(1);
            d.FavoritesNachlesen = () => null;
            d.RestoreDesktopIcons().Wait();
            d.FavoritesNachlesen = null;
            Check("Taskleiste nicht behalten: erneut geschrieben, dann Fehler statt Erfolg",
                log.Has("[WARNUNG] Windows hat die Taskleiste nach dem Neustart selbst neu belegt") && log.Count("Explorer startet kurz neu") == 1
                && log.Has("[FEHLER] Windows übernimmt die gesicherten Taskleisten-Symbole nicht") && !log.Has("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
        }

        // And back to how it was.
        var zurueck = new BackupService(new ProcessRunner(c), c, log.Write)
        {
            SourcePath = d.SourcePath,
            BackupPath = Path.Combine(root, "echt", "SymboleOriginal"),
        };
        zurueck.RestoreDesktopIcons().Wait();
    }
}
