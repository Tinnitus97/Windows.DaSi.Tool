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
/// Der Sicherungsdienst (BackupService) im Ganzen: jede Aktion der
/// Warteschlange, mit echtem robocopy, netsh und PrintBrm, an einem
/// Testprofil im Temp-Ordner. Programme installiert die winget-Attrappe,
/// "laufende Browser" sind Kopien von ping.exe unter dem Namen des Browsers.
/// </summary>
internal static partial class Program
{
    /// <summary>Ein Programm wie Firefox oder Chrome - nur mit eigenem Namen, damit nichts Echtes beendet wird.</summary>
    private static readonly AppEintrag Testbrowser =
        new(Punkt.Chrome, "Testbrowser", @"AppData\Local\DaSiTest\Testbrowser\User Data", "dasi-testbrowser", "DaSi.Testbrowser", true);
    private static readonly AppEintrag OhnePaket =
        new(Punkt.Edge, "Ohnepaket", @"AppData\Local\DaSiTest\Ohnepaket\User Data", "dasi-ohnepaket", null, true);
    private static readonly AppEintrag Kaputtpaket =
        new(Punkt.Brave, "Kaputtpaket", @"AppData\Local\DaSiTest\Kaputtpaket\User Data", "dasi-kaputtpaket", "DaSi.Kaputt", true);

    private static void Datei(string pfad, string inhalt = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, inhalt);
    }

    /// <summary>Ein Benutzerprofil, wie es auf einem Kundenrechner aussieht - nur klein.</summary>
    private static string TestprofilAnlegen(string pfad)
    {
        Datei(Path.Combine(pfad, "Documents", "brief.txt"), "Sehr geehrte Damen und Herren");
        Datei(Path.Combine(pfad, "Desktop", "Notizen.txt"), "Einkaufsliste");
        Datei(Path.Combine(pfad, "NTUSER.DAT"), "Registry - darf nie kopiert werden");
        Datei(Path.Combine(pfad, @"AppData\Roaming\Werkzeug\einstellungen.ini"), "a=1");
        Datei(Path.Combine(pfad, @"AppData\Local\Temp\muell.tmp"), "Zwischenspeicher");
        Datei(Path.Combine(pfad, "OneDrive", "Vertrag.docx"), "Cloud");
        // Ein Programmprofil wie Chrome: Datenordner mit "Local State", Profil "Default".
        var daten = Path.Combine(pfad, Testbrowser.ProfilRel);
        Datei(Path.Combine(daten, "Local State"), "{\"profile\":{}}");
        Datei(Path.Combine(daten, "Default", "Preferences"), "{\"homepage\":\"https://example.test\"}");
        Datei(Path.Combine(daten, "Default", "Bookmarks"), "{\"roots\":{}}");
        Datei(Path.Combine(daten, "Default", "Cache", "Cache_Data", "f_000001"), new string('c', 2000));
        // Opera in drei Fassungen.
        var opera = Path.Combine(pfad, AppCatalog.Opera.Rel);
        Datei(Path.Combine(opera, "Opera Stable", "Local State"), "{}");
        Datei(Path.Combine(opera, "Opera Stable", "Preferences"), "{}");
        Datei(Path.Combine(opera, "Opera GX Stable", "Default", "Preferences"), "{}");
        Datei(Path.Combine(opera, "Opera Air", "Local State"), "{}");
        return pfad;
    }

    /// <summary>Startet einen "laufenden Browser": ping.exe unter fremdem Namen.</summary>
    private static Process? LaufenderProzess(string root, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(root, "prozesse", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        if (!File.Exists(exe)) File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        return Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
    }

    private static bool Laeuft(string name) => Process.GetProcessesByName(name).Length > 0;

    private static void SicherungsdienstPruefen(string root)
    {
        Console.WriteLine("\n== Sicherungsdienst: Fehlerfaelle ohne Sicherung ==");
        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        var basis = Path.Combine(root, "dienst");
        var profil = TestprofilAnlegen(Path.Combine(basis, "Besitzer"));
        var leerProfil = Path.Combine(basis, "Leer");
        Directory.CreateDirectory(leerProfil);
        var leer = Path.Combine(basis, "LeereSicherung");
        Directory.CreateDirectory(leer);
        BackupService Dienst(string quelle, string ziel) => new(runner, c, log.Schreiben) { SourcePath = quelle, BackupPath = ziel };

        void Fall(string name, Action<BackupService> a, string erwartet, string? quelle = null, string? ziel = null)
        {
            log.Leeren();
            a(Dienst(quelle ?? leerProfil, ziel ?? leer));
            Check(name, log.Hat(erwartet), log.Text);
        }
        Fall("Profil zurueck ohne Sicherung", d => d.RestoreUserProfile().Wait(), "[FEHLER] Backup-Ordner nicht gefunden");
        Fall("Profil sichern ins eigene Profil: abgelehnt", d => d.BackupUserProfile().Wait(), "Ziel liegt im Quellverzeichnis", profil, Path.Combine(profil, "Sicherung"));
        Fall("AppData sichern ohne AppData", d => d.BackupAppDataOnly().Wait(), "[FEHLER] AppData nicht gefunden");
        Fall("Cloud sichern ohne Cloud-Ordner", d => d.BackupCloudOnly().Wait(), "kein Cloud-Ordner gefunden");
        Fall("AppData zurueck ohne Sicherung", d => d.RestoreAppDataOnly().Wait(), "In der Sicherung liegt kein AppData");
        Fall("Cloud zurueck ohne Sicherung", d => d.RestoreCloudOnly().Wait(), "[FEHLER] Backup-Ordner nicht gefunden");
        Fall("Programmprofil sichern, das es nicht gibt", d => d.BackupAppProfile(Testbrowser).Wait(), "[FEHLER] Pfad nicht gefunden");
        Fall("Opera sichern ohne Opera", d => d.BackupOpera().Wait(), "Kein Opera-Profil gefunden");
        Fall("Opera zurueck ohne Sicherung", d => d.RestoreOpera().Wait(), "Opera Backup nicht gefunden");
        Fall("WLAN zurueck ohne Sicherung", d => d.ImportWlan().Wait(), "WLAN Ordner nicht gefunden");
        Fall("Desktop-Foto oeffnen ohne Foto", d => d.ShowDesktopScreenshot().Wait(), "kein Desktop-Screenshot");
        Fall("Symbole zurueck ohne Sicherung", d => d.RestoreDesktopIcons().Wait(), "weder Symbolpositionen noch Taskleisten-Symbole");
        if (OperatingSystem.IsWindows())
        {
            Fall("AppData sichern ins eigene AppData: abgelehnt", d => d.BackupAppDataOnly().Wait(), "Ziel liegt im Quellverzeichnis", profil, Path.Combine(profil, "AppData", "x"));
            Fall("Drucker zurueck ohne Sicherung", d => d.ImportPrinters().Wait(), "Keine Druckersicherung gefunden");
            Fall("Design eines anderen Benutzers: abgelehnt", d => d.ExportDesign().Wait(), "[FEHLER] Design: Angemeldet ist", profil);
            Fall("Desktop-Foto eines anderen Benutzers: abgelehnt", d => d.CaptureDesktop().Wait(), "[FEHLER] Kein Desktop-Screenshot", profil);
            Fall("Symbole eines anderen Benutzers: abgelehnt", d => d.BackupDesktopIcons().Wait(), "Keine Symbolpositionen gesichert", profil);
        }
        else
        {
            Fall("Drucker ohne PrintBrm", d => d.ExportPrinters().Wait(), "PrintBrm.exe wurde nicht gefunden");
            Fall("Drucker zurueck ohne PrintBrm", d => d.ImportPrinters().Wait(), "PrintBrm.exe wurde nicht gefunden");
        }

        DesktopSymboleUeberDienst(root, c, runner);

        Console.WriteLine("\n== PrintBrm: Fehler in der Ausgabe trotz Exit-Code 0 ==");
        // Genau so von PrintBrm unter Windows Server 2025 ausgegeben (Exit-Code 0).
        var englisch = "Operation mode: restore\r\nTarget server: local machine\r\nThe following error occurred: 0x80070057.\r\nThe parameter is incorrect.\r\n\r\nCheck the eventlog for detailed information about the error which occurred.";
        Check("Fehlercode und Erklaerung erkannt (englisch)",
            BackupService.PrintBrmFehler(englisch) == "The following error occurred: 0x80070057. The parameter is incorrect.", BackupService.PrintBrmFehler(englisch));
        Check("Fehlercode erkannt (deutsch)", BackupService.PrintBrmFehler("Betriebsmodus: Wiederherstellen\nFolgender Fehler ist aufgetreten: 0x80070002.\nDas System kann die Datei nicht finden.")
            ?.Contains("0x80070002") == true);
        Check("Erfolg ohne Fehlercode: kein Fehler", BackupService.PrintBrmFehler("Operation mode: restore\nSuccessfully finished operation.") is null
            && BackupService.PrintBrmFehler(null) is null && BackupService.PrintBrmFehler("") is null);

        if (!OperatingSystem.IsWindows()) return;

        // ---- Profil, AppData, Cloud mit echtem robocopy ----
        Console.WriteLine("\n== Sicherungsdienst: Benutzerprofil, AppData und Cloud ==");
        var sicherung = Path.Combine(basis, "Sicherung");
        var aussen = Path.Combine(basis, "Spiele-auf-D");
        Datei(Path.Combine(aussen, "spielstand.sav"));
        try { Directory.CreateSymbolicLink(Path.Combine(profil, "Documents", "Spiele"), aussen); } catch { }

        log.Leeren();
        var d = Dienst(profil, sicherung);
        d.BackupUserProfile().Wait();
        var gesichert = Path.Combine(sicherung, "Benutzerprofil");
        Check("Profil gesichert", log.Hat("[ERFOLG] Benutzerprofil gesichert.") && File.Exists(Path.Combine(gesichert, "Documents", "brief.txt")), log.Text);
        Check("ohne Haken: kein AppData, keine Cloud-Ordner, nie NTUSER.DAT",
            !Directory.Exists(Path.Combine(gesichert, "AppData")) && !Directory.Exists(Path.Combine(gesichert, "OneDrive")) && !File.Exists(Path.Combine(gesichert, "NTUSER.DAT")));
        Check("Verknuepfung nach aussen: Warnung im Protokoll", log.Hat("außerhalb des Profils"), log.Text);

        // Mit AppData und Cloud - eine Datei ist gesperrt wie von einem laufenden Programm.
        var gesperrt = Path.Combine(profil, @"AppData\Roaming\Werkzeug\gesperrt.db");
        Datei(gesperrt, "offen");
        log.Leeren();
        using (new FileStream(gesperrt, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            d = Dienst(profil, sicherung);
            d.IncludeAppData = true;
            d.IncludeCloud = true;
            d.BackupUserProfile().Wait();
        }
        Check("mit Haken: AppData ohne Zwischenspeicher, Cloud-Ordner dabei",
            File.Exists(Path.Combine(gesichert, @"AppData\Roaming\Werkzeug\einstellungen.ini"))
            && !Directory.Exists(Path.Combine(gesichert, @"AppData\Local\Temp"))
            && File.Exists(Path.Combine(gesichert, "OneDrive", "Vertrag.docx")), log.Text);
        Check("mit Haken: im Protokoll vermerkt", log.Hat("AppData wird mitgesichert") && log.Hat("Cloud-Ordner werden mitgesichert: OneDrive"), log.Text);
        Check("gesperrte Datei: zusammengefasst, Liste neben der Sicherung, trotzdem Erfolg",
            log.Hat("[ERFOLG] Benutzerprofil gesichert - bis auf") && File.Exists(Path.Combine(sicherung, RobocopySkips.ListFileName)), log.Text);

        // Erneut ohne gesperrte Datei: Die Liste von eben ist dann falsch und geht weg.
        File.Delete(gesperrt);
        log.Leeren();
        d = Dienst(profil, sicherung);
        d.IncludeAppData = true;
        d.BackupUserProfile().Wait();
        Check("ohne gesperrte Datei: alte Liste entfernt", log.Hat("[ERFOLG] Benutzerprofil gesichert.") && !File.Exists(Path.Combine(sicherung, RobocopySkips.ListFileName)), log.Text);

        log.Leeren();
        d = Dienst(leerProfil, Path.Combine(basis, "NurCloud"));
        d.IncludeCloud = true;
        d.BackupUserProfile().Wait();
        Check("Cloud gewuenscht, aber keiner da: Hinweis", log.Hat("es wurde aber keiner gefunden"), log.Text);

        // Zurueck in ein neues Profil.
        var neu = Path.Combine(basis, "Anwender");
        Directory.CreateDirectory(neu);
        log.Leeren();
        d = Dienst(neu, sicherung);
        d.IncludeCloud = true;
        d.RestoreUserProfile().Wait();
        Check("Profil zurueck: Dokumente da, AppData ohne Haken nicht",
            log.Hat("[ERFOLG] Benutzerprofil wiederhergestellt.") && File.Exists(Path.Combine(neu, "Documents", "brief.txt"))
            && !Directory.Exists(Path.Combine(neu, "AppData")), log.Text);
        Check("Cloud zurueck: neben den Cloud-Ordner, nicht hinein",
            File.Exists(Path.Combine(neu, CloudProviders.RestoredFolderName("OneDrive"), "Vertrag.docx")) && !Directory.Exists(Path.Combine(neu, "OneDrive")), log.Text);
        log.Leeren();
        d = Dienst(neu, sicherung);
        d.IncludeAppData = true;
        d.IncludeCloud = true;
        d.RestoreUserProfile().Wait();
        Check("Profil zurueck mit AppData", log.Hat("AppData wird mit wiederhergestellt") && File.Exists(Path.Combine(neu, @"AppData\Roaming\Werkzeug\einstellungen.ini")), log.Text);
        Check("Cloud zurueck ein zweites Mal: wird ergaenzt", log.Hat("besteht schon"), log.Text);

        // AppData und Cloud ohne das Profil.
        var nurTeile = Path.Combine(basis, "NurTeile");
        log.Leeren();
        d = Dienst(profil, nurTeile);
        d.BackupAppDataOnly().Wait();
        d.BackupCloudOnly().Wait();
        Check("nur AppData gesichert - an derselben Stelle wie mit Profil",
            log.Hat("[ERFOLG] AppData gesichert.") && File.Exists(Path.Combine(nurTeile, @"Benutzerprofil\AppData\Roaming\Werkzeug\einstellungen.ini"))
            && !Directory.Exists(Path.Combine(nurTeile, @"Benutzerprofil\AppData\Local\Temp")), log.Text);
        Check("nur Cloud gesichert", log.Hat("[ERFOLG] OneDrive gesichert") && File.Exists(Path.Combine(nurTeile, @"Benutzerprofil\OneDrive\Vertrag.docx")), log.Text);
        var neu2 = Path.Combine(basis, "Anwender2");
        Directory.CreateDirectory(neu2);
        log.Leeren();
        d = Dienst(neu2, nurTeile);
        d.RestoreAppDataOnly().Wait();
        d.RestoreCloudOnly().Wait();
        Check("nur AppData zurueck", log.Hat("[ERFOLG] AppData wiederhergestellt.") && File.Exists(Path.Combine(neu2, @"AppData\Roaming\Werkzeug\einstellungen.ini")), log.Text);
        Check("nur Cloud zurueck - daneben", File.Exists(Path.Combine(neu2, @"OneDrive_wiederhergestellt\Vertrag.docx")), log.Text);
        log.Leeren();
        Dienst(neu2, leer).RestoreCloudOnly().Wait();
        Check("Cloud zurueck aus einer Sicherung ohne Cloud: Hinweis", log.Hat("[FEHLER] Backup-Ordner nicht gefunden") || log.Hat("enthält keine Cloud-Ordner"), log.Text);
        Directory.CreateDirectory(Path.Combine(leer, "Benutzerprofil"));
        log.Leeren();
        Dienst(neu2, leer).RestoreCloudOnly().Wait();
        Check("Sicherung ohne Cloud-Ordner: Hinweis", log.Hat("enthält keine Cloud-Ordner"), log.Text);

        // Laufende "Browser" werden vor dem Zurueckschreiben von AppData geschlossen.
        using (var edge = LaufenderProzess(root, AppCatalog.Edge.Prozess))
        {
            log.Leeren();
            Dienst(neu2, nurTeile).RestoreAppDataOnly().Wait();
            Check("laufender Browser vor AppData-Rueckschreiben geschlossen", log.Hat("Vor dem Zurückschreiben von AppData geschlossen: Edge") && !Laeuft(AppCatalog.Edge.Prozess), log.Text);
        }

        ProgrammprofilePruefen(root, basis, profil, c, runner, log);
        WlanUndDruckerPruefen(basis, c, runner, log);
    }

    private static void ProgrammprofilePruefen(string root, string basis, string profil, CancellationTokenState c, ProcessRunner runner, Protokoll log)
    {
        Console.WriteLine("\n== Sicherungsdienst: Programmprofile (Browser, Opera) ==");
        var attrappe = Attrappen.WingetAttrappe();
        var ordner = Attrappen.WingetSzenario(root, Szenario(("install.DaSi.Kaputt.code", "1603"), ("install.Opera.OperaGX.code", "0")));
        var winget = new WingetService(runner, c, log.Schreiben)
        {
            FesteKandidaten = attrappe is null ? Array.Empty<string>() : new[] { attrappe },
            Nachinstallation = () => System.Threading.Tasks.Task.FromResult(false),
        };
        var sicherung = Path.Combine(basis, "Programme");
        BackupService Dienst(string quelle) => new(runner, c, log.Schreiben) { SourcePath = quelle, BackupPath = sicherung, Winget = winget };

        using (var p = LaufenderProzess(root, Testbrowser.Prozess))
        {
            log.Leeren();
            Dienst(profil).BackupAppProfile(Testbrowser).Wait();
            Check("Programmprofil gesichert, laufendes Programm vorher beendet",
                log.Hat("[ERFOLG] Testbrowser Profil gesichert.") && log.Hat("Beende Testbrowser") && !Laeuft(Testbrowser.Prozess), log.Text);
        }
        var gesichert = Path.Combine(sicherung, Testbrowser.BackupOrdner);
        Check("Chromium-Zwischenspeicher bleibt draussen", File.Exists(Path.Combine(gesichert, "Default", "Preferences"))
            && !Directory.Exists(Path.Combine(gesichert, "Default", "Cache")));

        // Zurueck in ein Profil, in dem schon ein (frisches) Browserprofil liegt.
        var ziel = Path.Combine(basis, "Browser-Anwender");
        var vorhanden = Path.Combine(ziel, Testbrowser.ProfilRel);
        Datei(Path.Combine(vorhanden, "Local State"), "frisch");
        var d = Dienst(ziel);
        bool nachgestartet = false;
        // Waehrend des Kopierens startet sich der Browser selbst (wie nach einer Installation).
        d.OnFilesProgress = _ => { if (!nachgestartet) { nachgestartet = true; LaufenderProzess(root, Testbrowser.Prozess)?.Dispose(); } };
        log.Leeren();
        using (var p = LaufenderProzess(root, Testbrowser.Prozess))
            d.RestoreAppProfile(Testbrowser).Wait();
        Check("nicht installiert: per winget (Attrappe) installiert", log.Hat("Testbrowser wurde per winget installiert"), log.Text);
        Check("bisheriges Profil beiseitegelegt, nicht geloescht", log.Hat("Bisheriges Testbrowser-Profil gesichert nach")
            && Directory.GetDirectories(Path.GetDirectoryName(vorhanden)!, "User Data_alt_*").Length == 1, log.Text);
        Check("waehrend des Kopierens gestartet: beendet und erneut kopiert", log.Hat("während des Kopierens gestartet") && !Laeuft(Testbrowser.Prozess), log.Text);
        Check("Profil wiederhergestellt und abgeglichen", log.Hat("[ERFOLG] Testbrowser Profil wiederhergestellt (Default)")
            && File.ReadAllText(Path.Combine(vorhanden, "Local State")) != "frisch", log.Text);

        // Ohne winget-Paket (wie Edge) und mit scheiternder Installation.
        Datei(Path.Combine(sicherung, OhnePaket.BackupOrdner, "Local State"), "{}");
        Datei(Path.Combine(sicherung, OhnePaket.BackupOrdner, "Default", "Preferences"), "{}");
        log.Leeren();
        Dienst(ziel).RestoreAppProfile(OhnePaket).Wait();
        Check("ohne Paket: nur zurueckschreiben", log.Hat("Prüfe, ob Ohnepaket installiert ist") && log.Hat("[ERFOLG] Ohnepaket Profil wiederhergestellt"), log.Text);
        log.Leeren();
        Dienst(ziel).RestoreAppProfile(Kaputtpaket).Wait();
        Check("Installation scheitert: Warnung, Backup fehlt: Fehler",
            log.Hat("Kaputtpaket ließ sich per winget nicht installieren") && log.Hat("[FEHLER] Kaputtpaket Backup nicht gefunden"), log.Text);

        // Ohne WingetService: "winget" aus dem Suchpfad - hier die Attrappe.
        if (attrappe is not null)
        {
            var pfad = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(attrappe) + ";" + pfad);
            try
            {
                Attrappen.SzenarioSchreiben(ordner, Szenario(("install.*.code", WingetFehler.SchonInstalliert.ToString())));
                log.Leeren();
                new BackupService(runner, c, log.Schreiben) { SourcePath = ziel, BackupPath = sicherung }.RestoreAppProfile(Kaputtpaket).Wait();
                Check("winget aus dem Suchpfad: 'bereits installiert' erkannt", log.Hat("Kaputtpaket ist bereits installiert"), log.Text);
            }
            finally { Environment.SetEnvironmentVariable("PATH", pfad); }
        }

        // Das bisherige Profil laesst sich nicht beiseitelegen: nichts anfassen.
        var sperre = Path.Combine(vorhanden, "Default", "Preferences");
        log.Leeren();
        using (new FileStream(sperre, FileMode.Open, FileAccess.Read, FileShare.None))
            Dienst(ziel).RestoreAppProfile(Testbrowser).Wait();
        Check("gesperrtes Profil: Abbruch, ohne etwas zu veraendern", log.Hat("ließ sich nicht beiseitelegen") && File.Exists(sperre), log.Text);

        MozillaDienstPruefen(basis, Dienst, log, ordner);

        // Opera: alle Fassungen, je nach Lage installiert, nachinstalliert oder nur zurueckgeschrieben.
        Attrappen.SzenarioSchreiben(ordner, Szenario(("install.*.code", "0")));
        log.Leeren();
        Dienst(profil).BackupOpera().Wait();
        Check("Opera: alle Fassungen gefunden und gesichert", log.Hat("Gefundene Opera-Fassungen: Opera, Opera Air, Opera GX")
            && log.Anzahl("Profil gesichert") == 3, log.Text);
        var operaZiel = Path.Combine(basis, "Opera-Anwender");
        Datei(Path.Combine(operaZiel, @"AppData\Local\Programs\Opera\opera.exe"), "installiert");
        log.Leeren();
        using (LaufenderProzess(root, AppCatalog.Opera.Prozess))
            Dienst(operaZiel).RestoreOpera().Wait();
        Check("Opera: vorhandene Fassung erkannt", log.Hat("Opera ist bereits installiert"), log.Text);
        Check("Opera GX: per winget nachinstalliert", log.Hat("Opera GX wurde per winget installiert"), log.Text);
        Check("Opera Air: kein Paket - Hinweis, Profil trotzdem zurueck", log.Hat("Opera Air ist nicht installiert und lässt sich nicht automatisch"), log.Text);
        Check("Opera: alle drei zurueckgeschrieben", log.Anzahl("Profil wiederhergestellt") == 3
            && File.Exists(Path.Combine(operaZiel, AppCatalog.Opera.Rel, "Opera GX Stable", "Default", "Preferences")), log.Text);

        c.RequestCancel();
        log.Leeren();
        Dienst(operaZiel).RestoreOpera().Wait();
        Dienst(profil).BackupOpera().Wait();
        c.Reset();
        Check("Opera: Abbruch kopiert nichts mehr", !log.Hat("Profil wiederhergestellt") && !log.Hat("Profil gesichert"), log.Text);
        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
    }

    private static void WlanUndDruckerPruefen(string basis, CancellationTokenState c, ProcessRunner runner, Protokoll log)
    {
        Console.WriteLine("\n== Sicherungsdienst: WLAN (netsh) und Drucker (PrintBrm) ==");
        var sicherung = Path.Combine(basis, "Netz");
        var d = new BackupService(runner, c, log.Schreiben) { SourcePath = basis, BackupPath = sicherung };

        log.Leeren();
        d.ExportWlan().Wait();
        Check("WLAN-Export: Ordner angelegt, Ergebnis gemeldet (ohne WLAN-Dienst: Fehler mit Grund)",
            Directory.Exists(Path.Combine(sicherung, "WLAN-Profile")) && (log.Hat("[FEHLER] WLAN-Export") || log.Hat("WLAN-Profile exportiert") || log.Hat("keine WLAN-Profile")), log.Text);

        log.Leeren();
        d.ImportWlan().Wait();
        Check("WLAN-Import ohne Profile: 0 importiert", log.Hat("[ERFOLG] 0 WLAN-Profile importiert"), log.Text);
        File.WriteAllText(Path.Combine(sicherung, "WLAN-Profile", "WLAN-Testnetz.xml"), "<WLANProfile>kaputt</WLANProfile>");
        log.Leeren();
        d.ImportWlan().Wait();
        Check("WLAN-Import: unbrauchbares Profil wird als Fehler gezaehlt", log.Hat("-> [FEHLER] WLAN-Testnetz") && log.Hat("0 importiert, 1 fehlgeschlagen"), log.Text);

        log.Leeren();
        var uhr = Stopwatch.StartNew();
        d.ExportPrinters().Wait();
        Console.WriteLine($"         PrintBrm: {uhr.Elapsed.TotalSeconds:F0} s");
        Check("Drucker-Export: Ergebnis gemeldet", log.Hat("[ERFOLG] Drucker gesichert") || log.Hat("[FEHLER] Druckersicherung fehlgeschlagen") || log.Hat("PrintBrm.exe wurde nicht gefunden"), log.Text);

        Datei(Path.Combine(sicherung, "Drucker", "Drucker.printerExport"), "keine echte Druckersicherung");
        // Was sagt PrintBrm selbst zu einer unbrauchbaren Datei? (Zur Auswertung im Protokoll der CI.)
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
        log.Leeren();
        d.ImportPrinters().Wait();
        Check("Drucker-Import: unbrauchbare Sicherung - trotz Exit-Code 0 als Fehler erkannt",
            log.Hat("Druckerwiederherstellung fehlgeschlagen") && log.Hat("0x8") && !log.Hat("[ERFOLG]"), log.Text);
    }

    /// <summary>
    /// Desktop-Symbole ueber den Dienst - mit einem Test-Desktop statt des
    /// Explorers, damit sich auch fehlende und Systemsymbole durchspielen lassen.
    /// </summary>
    private static void DesktopSymboleUeberDienst(string root, CancellationTokenState c, ProcessRunner runner)
    {
        Console.WriteLine("\n== Sicherungsdienst: Desktop-Symbole (Test-Desktop) ==");
        var log = new Protokoll();
        // Der Dienst arbeitet nur am Desktop des angemeldeten Benutzers.
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
            var d = new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung };
            d.BackupDesktopIcons().Wait();
            Check("Symbolpositionen gesichert (4 Symbole)", log.Hat("[ERFOLG] Positionen von 4 Desktop-Symbolen gesichert") && DesktopIcons.Vorhanden(sicherung), log.Text);
            Check("automatisch anordnen: vermerkt", log.Hat("automatisch anordnen\" ist an"), log.Text);
            // Ohne Taskleiste weiter - deren Rueckschreiben startet den Explorer neu.
            try { Directory.Delete(Taskleiste.SicherungsOrdner(sicherung), true); } catch { }
            // Was in der Sicherung auf dem Desktop lag, stammt aus dem Profil.
            Datei(Path.Combine(sicherung, "Benutzerprofil", "Desktop", "Rechnung.pdf"));

            log.Leeren();
            var leer = new TestDesktop(1920, 1080, false, false);
            DesktopIcons.OeffneDesktop = () => leer;
            d.BackupDesktopIcons().Wait();
            Check("leerer Desktop: nichts zu sichern", log.Hat("keine Symbole"), log.Text);

            // Neuer Rechner: kleinerer Desktop, "Dieser PC" aus, zwei Symbole fehlen noch.
            var neu = new TestDesktop(1280, 720, false, true, new DesktopIcon("Brief.lnk", "Brief", 500, 500))
            { SpaeterDazu = (1, new DesktopIcon(dieserPc, "Dieser PC", 600, 600)) };
            DesktopIcons.OeffneDesktop = () => neu;
            log.Leeren();
            d.RestoreDesktopIcons().Wait();
            Check("Symbole zurueck: vorhandene gesetzt", log.Hat("Desktop-Symbolen an ihre gesicherte Stelle gesetzt") && neu.Protokoll.Contains("setzen Brief.lnk"), log.Text);
            Check("anderer Bildschirm: Positionen umgerechnet", log.Hat("im selben Verhältnis umgerechnet"), log.Text);
            Check("fehlendes Symbol aus dem Profil: eigener Hinweis", log.Hat("gibt es auf diesem Desktop nicht: Rechnung"), log.Text);
            Check("fehlendes Symbol von aussen: Hinweis auf den oeffentlichen Desktop", log.Hat("öffentlichen Desktop") && log.Hat("Programm"), log.Text);
            Check("automatisch anordnen wie beim Sichern", log.Hat("so ist es jetzt auch hier"), log.Text);
            if (OperatingSystem.IsWindows())
                Check("Systemsymbol 'Dieser PC' eingeschaltet", log.Hat("Systemsymbole eingeschaltet wie auf dem alten Desktop: Dieser PC"), log.Text);

            // Nichts passt: Fehler.
            DesktopIcons.OeffneDesktop = () => new TestDesktop(800, 600, false, false, new DesktopIcon("Fremd.lnk", "Fremd", 1, 1));
            var nurFremd = Path.Combine(root, "dienst", "SymboleFremd");
            DesktopIcons.Speichern(Path.Combine(nurFremd, DesktopIcons.FolderName), new DesktopLayout
            {
                Rechner = "ALT", Benutzer = "x", Erstellt = DateTime.Now, Breite = 800, Hoehe = 600,
                Symbole = { new DesktopIcon("Weg.lnk", "Weg", 5, 5) },
            });
            log.Leeren();
            new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = nurFremd }.RestoreDesktopIcons().Wait();
            Check("kein Symbol zuzuordnen: Fehler", log.Hat("[FEHLER] Kein gesichertes Symbol"), log.Text);

            // Der Desktop laesst sich nicht oeffnen.
            DesktopIcons.OeffneDesktop = () => throw new InvalidOperationException("Explorer laeuft nicht");
            log.Leeren();
            new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = nurFremd }.RestoreDesktopIcons().Wait();
            Check("Desktop nicht erreichbar beim Setzen: Fehler mit Grund", log.Hat("Symbolpositionen ließen sich nicht setzen: Explorer laeuft nicht"), log.Text);
            log.Leeren();
            new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = Path.Combine(root, "dienst", "SymboleNie") }.BackupDesktopIcons().Wait();
            Check("Desktop nicht erreichbar beim Lesen: Fehler mit Grund", log.Hat("Desktop-Symbole ließen sich nicht lesen"), log.Text);
        }
        finally
        {
            DesktopIcons.OeffneDesktop = vorher;
        }
    }

    /// <summary>
    /// Design, Desktop-Foto und Symbole am echten Desktop des angemeldeten
    /// Benutzers - nur unter Windows. Laeuft am Ende, weil dabei Hintergrund
    /// und Explorer wirklich umgestellt werden.
    /// </summary>
    private static void SicherungsdienstAmEchtenDesktop(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Sicherungsdienst am echten Desktop: Design, Desktop-Foto ==");
        var c = new CancellationTokenState();
        var runner = new ProcessRunner(c);
        var log = new Protokoll();
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sicherung = Path.Combine(root, "echt", "Sicherung");
        var d = new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung };

        // Desktop-Foto: aufnehmen, oeffnen (ohne Bildanzeige), Fehler beim Oeffnen.
        d.CaptureDesktop().Wait();
        Check("Desktop-Foto ueber den Dienst gespeichert", log.Hat("[ERFOLG] Desktop-Screenshot gespeichert") && AppCatalog.HatDesktopScreenshot(sicherung), log.Text);
        var geoeffnet = new List<string>();
        d.BildOeffnen = geoeffnet.Add;
        log.Leeren();
        d.ShowDesktopScreenshot().Wait();
        Check("Desktop-Foto oeffnen: jedes Bild einmal", geoeffnet.Count >= 1 && log.Hat("[ERFOLG] Desktop-Screenshot geöffnet"), log.Text);
        d.BildOeffnen = _ => throw new InvalidOperationException("keine Bildanzeige");
        log.Leeren();
        d.ShowDesktopScreenshot().Wait();
        Check("Desktop-Foto oeffnen scheitert: Fehler mit Ablageort", log.Hat("konnte nicht geöffnet werden (keine Bildanzeige)"), log.Text);

        // Design: sichern, ein Bild aus einer alten Sicherung setzen, das eigene zuruecksetzen.
        log.Leeren();
        d.ExportDesign().Wait();
        Check("Design gesichert (Design.json)", DesignEinstellungen.Laden(sicherung) is not null && log.Hat("[ERFOLG] Design-Einstellungen gesichert"), log.Text);

        var alt = Path.Combine(root, "echt", "AlteSicherung");
        var bildAlt = Path.Combine(alt, "Hintergrundbild", "Urlaub.png");
        Directory.CreateDirectory(Path.GetDirectoryName(bildAlt)!);
        HintergrundEcht.EinfarbigesPng(bildAlt, (0x20, 0x60, 0xA0));
        File.WriteAllText(Path.Combine(alt, "Hintergrundbild", "Hintergrundbild-Pfad.txt"), @"C:\Users\Besitzer\Pictures\Urlaub.png");
        File.WriteAllText(Path.Combine(alt, "Hintergrundbild", "Hintergrundbild-Profil.txt"), @"C:\Users\Besitzer");
        log.Leeren();
        new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = alt }.ImportDesign().Wait();
        var bildNeu = Path.Combine(profil, "Pictures", "Urlaub.png");
        Check("alte Sicherung: Bild ins gewaehlte Profil uebertragen", File.Exists(bildNeu) && log.Hat("es kommt ins gewählte Profil"), log.Text);
        Check("alte Sicherung: Hintergrund gesetzt", log.Hat("[ERFOLG] Hintergrund gesetzt") || log.Hat("[WARNUNG] Hintergrund eingetragen"), log.Text);
        Check("alte Sicherung: ohne Design-Einstellungen nur der Hintergrund", log.Hat("enthält keine Design-Einstellungen"), log.Text);

        // Eine Sicherung mit Volltonfarbe statt Bild.
        var farbig = Path.Combine(root, "echt", "Volltonfarbe");
        DesignEinstellungen.Speichern(farbig, new DesignSicherung
        {
            Rechner = "ALT", Erstellt = DateTime.Now,
            Hintergrund = new HintergrundEinstellung { Art = "Volltonfarbe", Farbe = "30 90 150" },
        });
        log.Leeren();
        new BackupService(runner, c, log.Schreiben) { SourcePath = profil, BackupPath = farbig }.ImportDesign().Wait();
        Check("Volltonfarbe aus der Sicherung gesetzt", log.Hat("Volltonfarbe RGB 30, 90, 150") && Hintergrund.FarbeLesen(DesktopHintergrund.Lesen(Microsoft.Win32.Registry.CurrentUser).Farbe) == (30, 90, 150), log.Text);

        // Laeuft das Tool unter einem anderen Konto: nur eintragen, Windows zeigt es nach der Anmeldung.
        var fremd = DesktopHintergrund.FarbeSetzen(Microsoft.Win32.Registry.CurrentUser, eigenerBenutzer: false, (40, 40, 40), profil);
        Check("anderes Konto: eingetragen, ohne den Weg ueber den Explorer", fremd.Eingetragen && fremd.ExplorerFehler == "Tool läuft unter einem anderen Konto", fremd.ExplorerFehler);

        // Schwarzer Desktop, erwartet war ein Bild: Kontrolle sieht auch auf den Bildschirm.
        DesktopHintergrund.FarbeSetzen(Microsoft.Win32.Registry.CurrentUser, eigenerBenutzer: true, (0, 0, 0), profil);
        Thread.Sleep(1500);
        var schwarz = DesktopHintergrund.Kontrolle(new HintergrundStand(@"C:\Bilder\Urlaub.png", "0 0 0", 0, "10", "0", Array.Empty<string>()), Deutsch);
        Console.WriteLine($"         Kontrolle bei schwarzem Desktop: {schwarz ?? "(Bildschirm nicht ueberwiegend schwarz)"}");
        Check("schwarzer Desktop statt Bild: erkannt (oder Bildschirm nicht schwarz)", schwarz is null || schwarz.Contains("schwarz statt des Bildes"), schwarz);
        var schirm = DesktopScreenshot.CaptureVirtualScreen();
        bool erwartet = DesktopScreenshot.ColorShare(schirm.Bgra, (0, 0, 0), 10) >= 0.5;
        Check("Bildschirm ueberwiegend schwarz? - wie eine eigene Aufnahme", DesktopHintergrund.SchirmSchwarz() == erwartet, erwartet.ToString());

        // Ein Design anwenden ohne bekannten Benutzer (direkt ueber explorer.exe) - und mit einem, den es nicht gibt.
        var aktuell = DesktopHintergrund.DesignDateiPfad(Microsoft.Win32.Registry.CurrentUser, profil);
        if (aktuell is not null && File.Exists(aktuell))
        {
            var kopie = Path.Combine(Path.GetDirectoryName(aktuell)!, WindowsDesign.NeuerDateiName());
            File.Copy(aktuell, kopie);
            var seit = DateTime.Now;
            var (ok, fehler) = WindowsDesign.Anwenden(kopie, null).GetAwaiter().GetResult();
            Check("Design ohne bekannten Benutzer: direkt ueber explorer.exe", ok, fehler);
            Thread.Sleep(4000);
            WindowsDesign.EinstellungenSchliessen(seit);
            var (ok2, fehler2) = WindowsDesign.Anwenden(kopie, @"NIEMAND\gibt-es-nicht").GetAwaiter().GetResult();
            Check("Design fuer einen unbekannten Benutzer: abgelehnt, mit Grund", !ok2 && !string.IsNullOrWhiteSpace(fehler2), fehler2);
        }

        log.Leeren();
        d.ImportDesign().Wait();
        Check("eigenes Design zurueck: Einstellungen uebernommen", log.Hat("[ERFOLG] Design-Einstellungen übernommen"), log.Text);
        try { File.Delete(bildNeu); } catch { }
    }

    /// <summary>Symbole und Taskleiste am echten Desktop - startet den Explorer neu, deshalb ganz zuletzt.</summary>
    private static void SymboleAmEchtenDesktop(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Sicherungsdienst am echten Desktop: Symbole und Taskleiste ==");
        var c = new CancellationTokenState();
        var log = new Protokoll();
        var d = new BackupService(new ProcessRunner(c), c, log.Schreiben)
        {
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BackupPath = Path.Combine(root, "echt", "Symbole"),
        };
        d.BackupDesktopIcons().Wait();
        Check("echte Symbole: gesichert oder leerer Desktop gemeldet",
            log.Hat("Desktop-Symbolen gesichert") || log.Hat("keine Symbole"), log.Text);
        Check("echte Taskleiste: gesichert oder nichts angeheftet", log.Hat("Taskleiste gesichert") || log.Hat("nichts angeheftet"), log.Text);

        // Die Sicherung so abwandeln, dass am echten Desktop alles umgestellt
        // wird: anderes Raster, automatisch anordnen, ein Systemsymbol mehr.
        var ordner = Path.Combine(d.BackupPath, DesktopIcons.FolderName);
        var layout = DesktopIcons.Laden(DesktopIcons.Datei(d.BackupPath)) ?? new DesktopLayout
        {
            Rechner = Environment.MachineName, Benutzer = Environment.UserName, Erstellt = DateTime.Now, Breite = 1024, Hoehe = 768,
            Symbole = { new DesktopIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", "Papierkorb", 20, 20) },
        };
        DesktopIcons.Speichern(Path.Combine(root, "echt", "SymboleOriginal", DesktopIcons.FolderName), layout);
        layout.AmRasterAusrichten = !layout.AmRasterAusrichten;
        layout.AutomatischAnordnen = true;
        layout.Symbole.Add(new DesktopIcon("::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Netzwerk", 120, 20));
        DesktopIcons.Speichern(ordner, layout);

        log.Leeren();
        d.RestoreDesktopIcons().Wait();
        Check("echte Symbole und Taskleiste zurueck", log.Hat("Taskleiste wiederhergestellt") || log.Hat("Desktop-Symbolen an ihre gesicherte Stelle gesetzt"), log.Text);
        Check("echter Desktop: Raster wie in der Sicherung umgestellt", log.Hat("am Raster ausrichten"), log.Text);
        Check("echter Desktop: automatisch anordnen wieder an", log.Hat("so ist es jetzt auch hier"), log.Text);
        Check("echter Desktop: Systemsymbol 'Netzwerk' eingeschaltet", log.Hat("Systemsymbole eingeschaltet") && log.Hat("Netzwerk"), log.Text);
        if (log.Hat("Taskleiste wiederhergestellt"))
        {
            Check("echte Taskleiste: nach dem Neustart nachgesehen, alles behalten", !log.Hat("nicht übernommen") && !log.Hat("übernimmt die gesicherten"), log.Text);
            Check("echte Taskleiste: auf der Taskleiste nachgesehen, alle Symbole da",
                (log.Hat("Die Taskleiste zeigt") || log.Hat("ließ sich nicht nachsehen")) && !log.Hat("zeigt diese angehefteten Symbole nicht"), log.Text);
        }

        // Was die Taskleiste zeigt: ein Symbol fehlt, obwohl das Programm da ist -
        // oder die Taskleiste laesst sich nicht lesen.
        if (Taskleiste.Laden(d.BackupPath) is { Taskband.Count: > 0 } tl
            && Taskleiste.Eintraege(Taskleiste.Favorites(tl.Taskband), tl.Verknuepfungen) is { Count: > 1 } eintraege
            && eintraege[0].Kennung is { } nurDiese)
        {
            d.TaskleistenKontrolle = TimeSpan.FromSeconds(1);
            d.TaskleistenSicht = () => new HashSet<string> { nurDiese };
            log.Leeren();
            d.RestoreDesktopIcons().Wait();
            Check("Taskleiste zeigt ein Symbol nicht: Fehler mit Namen und Kennung statt Erfolg",
                log.Hat("[FEHLER] Die Taskleiste zeigt diese angehefteten Symbole nicht") && !log.Hat("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
            d.TaskleistenSicht = () => null;
            log.Leeren();
            d.RestoreDesktopIcons().Wait();
            Check("Taskleiste nicht lesbar: Hinweis, trotzdem Erfolg", log.Hat("ließ sich nicht nachsehen") && log.Hat("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
            d.TaskleistenSicht = null;
        }

        // Windows belegt die Taskleiste nach dem Neustart selbst neu (wie bei
        // einem Anwender): einmal erneut schreiben, dann ein Fehler statt "Erfolg".
        if (Taskleiste.Laden(d.BackupPath) is { Taskband.Count: > 0 })
        {
            log.Leeren();
            d.TaskleistenKontrolle = TimeSpan.FromSeconds(1);
            d.FavoritesNachlesen = () => null;
            d.RestoreDesktopIcons().Wait();
            d.FavoritesNachlesen = null;
            Check("Taskleiste nicht behalten: erneut geschrieben, dann Fehler statt Erfolg",
                log.Hat("[WARNUNG] Windows hat die Taskleiste nach dem Neustart selbst neu belegt") && log.Anzahl("Explorer startet kurz neu") == 1
                && log.Hat("[FEHLER] Windows übernimmt die gesicherten Taskleisten-Symbole nicht") && !log.Hat("[ERFOLG] Taskleiste wiederhergestellt"), log.Text);
        }

        // Und wieder wie vorher.
        var zurueck = new BackupService(new ProcessRunner(c), c, log.Schreiben)
        {
            SourcePath = d.SourcePath,
            BackupPath = Path.Combine(root, "echt", "SymboleOriginal"),
        };
        zurueck.RestoreDesktopIcons().Wait();
    }
}
