using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// "Ordner unter C:\": die Liste (was Windows dort anlegt, bleibt ausgeblendet),
/// die Verfuegbarkeit der Kachel und - unter Windows mit echtem robocopy -
/// Sichern und Wiederherstellen an einem Schein-Laufwerk im Temp-Ordner.
/// </summary>
internal static partial class Program
{
    private static void LaufwerkPruefen(string root)
    {
        Console.WriteLine("\n== Ordner unter C:\\: Liste, Windows-Ordner, Verfuegbarkeit ==");
        Check("Windows-Ordner erkannt", LaufwerksOrdner.IstWindowsOrdner("Windows") && LaufwerksOrdner.IstWindowsOrdner("program files")
            && LaufwerksOrdner.IstWindowsOrdner("Program Files (x86)") && LaufwerksOrdner.IstWindowsOrdner("Users") && LaufwerksOrdner.IstWindowsOrdner("ProgramData")
            && LaufwerksOrdner.IstWindowsOrdner("$Recycle.Bin") && LaufwerksOrdner.IstWindowsOrdner("$WinREAgent") && LaufwerksOrdner.IstWindowsOrdner("System Volume Information")
            && LaufwerksOrdner.IstWindowsOrdner("XboxGames") && LaufwerksOrdner.IstWindowsOrdner("Windows10Upgrade"));
        Check("versteckter Systemordner gilt als Windows-Ordner", LaufwerksOrdner.IstWindowsOrdner("Irgendwas", FileAttributes.Hidden | FileAttributes.System)
            && !LaufwerksOrdner.IstWindowsOrdner("Irgendwas", FileAttributes.Hidden));
        Check("eigene Ordner sind keine Windows-Ordner", !LaufwerksOrdner.IstWindowsOrdner("Daten") && !LaufwerksOrdner.IstWindowsOrdner("Scans")
            && !LaufwerksOrdner.IstWindowsOrdner("Intel") && !LaufwerksOrdner.IstWindowsOrdner("Windows-Daten"));
        Check("Systemlaufwerk mit Backslash", LaufwerksOrdner.StandardWurzel().EndsWith('\\') && LaufwerksOrdner.StandardWurzel().Length == 3, LaufwerksOrdner.StandardWurzel());

        // Ein Schein-Laufwerk: Windows-Ordner, eigene Ordner, die Sicherung selbst.
        var c = Path.Combine(root, "laufwerk", "C");
        foreach (var n in new[] { "Windows", "Program Files", "Users", "$Recycle.Bin", "PerfLogs" }) Directory.CreateDirectory(Path.Combine(c, n));
        Datei(Path.Combine(c, "Daten", "Rechnungen", "2026.pdf"), new string('r', 1000));
        Datei(Path.Combine(c, "Daten", "liste.txt"), new string('l', 24));
        Datei(Path.Combine(c, "scans", "scan1.pdf"), new string('s', 500));
        Directory.CreateDirectory(Path.Combine(c, "Leer"));
        var ziel = Path.Combine(c, "DaSi", "DASI_PC_01_01_2026");
        Directory.CreateDirectory(ziel);
        var verknuepft = false;
        try { Directory.CreateSymbolicLink(Path.Combine(c, "Verknuepfung"), Path.Combine(c, "Daten")); verknuepft = true; } catch { }

        var liste = LaufwerksOrdner.Auflisten(c, ziel);
        var namen = liste.Select(e => e.Name).ToList();
        Check("Liste nach Namen, ohne den Ordner der Sicherung", string.Join(",", namen.Where(n => !LaufwerksOrdner.IstWindowsOrdner(n))) == "Daten,Leer,scans",
            string.Join(",", namen));
        Check("Windows-Ordner in der Liste markiert", liste.Where(e => e.Windows).Select(e => e.Name).OrderBy(n => n).SequenceEqual(new[] { "$Recycle.Bin", "PerfLogs", "Program Files", "Users", "Windows" }),
            string.Join(",", liste.Where(e => e.Windows).Select(e => e.Name)));
        Check("voller Pfad je Eintrag", liste.First(e => e.Name == "Daten").Pfad == Path.Combine(c, "Daten"), liste.First(e => e.Name == "Daten").Pfad);
        if (verknuepft) Check("Verknuepfung (Junction/Link) nie in der Liste", !namen.Contains("Verknuepfung"), string.Join(",", namen));
        Check("Ziel woanders: DaSi-Ordner erscheint", LaufwerksOrdner.Auflisten(c, Path.Combine(root, "anderswo")).Any(e => e.Name == "DaSi")
            && LaufwerksOrdner.Auflisten(c, null).Any(e => e.Name == "DaSi"));
        Check("Laufwerk gibt es nicht: leere Liste", LaufwerksOrdner.Auflisten(Path.Combine(root, "gibtsnicht"), null).Count == 0);

        Check("enthaelt: Ordner selbst und darin, nicht daneben", LaufwerksOrdner.Enthaelt(Path.Combine(c, "DaSi"), ziel)
            && LaufwerksOrdner.Enthaelt(ziel, ziel) && !LaufwerksOrdner.Enthaelt(Path.Combine(c, "Da"), ziel) && !LaufwerksOrdner.Enthaelt(Path.Combine(c, "Daten"), ziel));
        Check("enthaelt: Gross-/Kleinschreibung egal", LaufwerksOrdner.Enthaelt(Path.Combine(c, "dasi"), ziel.ToUpperInvariant()) || !OperatingSystem.IsWindows());
        Check("Groesse: alle Dateien darin", LaufwerksOrdner.Groesse(Path.Combine(c, "Daten")) == 1024 && LaufwerksOrdner.Groesse(Path.Combine(c, "Leer")) == 0,
            LaufwerksOrdner.Groesse(Path.Combine(c, "Daten")).ToString());
        using (var abbruch = new CancellationTokenSource())
        {
            abbruch.Cancel();
            Check("Groesse: abgebrochen = -1", LaufwerksOrdner.Groesse(Path.Combine(c, "Daten"), abbruch.Token) == -1);
        }
        Check("Groesse: Ordner fehlt = 0", LaufwerksOrdner.Groesse(Path.Combine(c, "Fehlt")) == 0);

        // In der Sicherung.
        var sicherung = Path.Combine(root, "laufwerk", "Sicherung");
        Check("Sicherung ohne C-Ordner: nichts", !LaufwerksOrdner.Vorhanden(sicherung) && LaufwerksOrdner.InDerSicherung(sicherung).Count == 0 && !LaufwerksOrdner.Vorhanden(null));
        Datei(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner, "Daten", "liste.txt"));
        Directory.CreateDirectory(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner, "Program Files"));
        var gesichert = LaufwerksOrdner.InDerSicherung(sicherung);
        Check("Sicherung: Ordner darin, Windows-Ordner markiert", LaufwerksOrdner.Vorhanden(sicherung) && gesichert.Count == 2
            && gesichert.Single(e => e.Name == "Program Files").Windows && !gesichert.Single(e => e.Name == "Daten").Windows);

        // Verfuegbarkeit der Kachel.
        var vorher = LaufwerksOrdner.Wurzel;
        try
        {
            LaufwerksOrdner.Wurzel = c;
            Check("Sichern: Laufwerk da - Kachel frei", AppCatalog.BeimSichern(Punkt.Laufwerk, null) is null);
            LaufwerksOrdner.Wurzel = Path.Combine(root, "gibtsnicht") + Path.DirectorySeparatorChar;
            Check("Sichern: Laufwerk fehlt - Grund", AppCatalog.BeimSichern(Punkt.Laufwerk, null)?.De.Contains("gibt es hier nicht") == true);
        }
        finally { LaufwerksOrdner.Wurzel = vorher; }
        Check("Wiederherstellen: Ordner in der Sicherung - frei", AppCatalog.BeimWiederherstellen(Punkt.Laufwerk, sicherung) is null);
        Datei(Path.Combine(root, "laufwerk", "NurWlan", "WLAN-Profile", "a.xml"));
        Check("Wiederherstellen: keine Ordner in der Sicherung - Grund", AppCatalog.BeimWiederherstellen(Punkt.Laufwerk, Path.Combine(root, "laufwerk", "NurWlan"))?.De.Contains("keine Ordner von") == true);
        Check("Ordner unter C:\\ allein machen einen Ordner zur Sicherung", AppCatalog.IstSicherung(sicherung));

        if (OperatingSystem.IsWindows()) LaufwerkMitRobocopy(root, c, ziel);
    }

    /// <summary>Sichern und Wiederherstellen mit echtem robocopy.</summary>
    private static void LaufwerkMitRobocopy(string root, string c, string zielInC)
    {
        Console.WriteLine("\n== Ordner unter C:\\: sichern und wiederherstellen (robocopy) ==");
        var abbruch = new CancellationTokenState();
        var runner = new ProcessRunner(abbruch);
        var log = new Protokoll();
        var sicherung = Path.Combine(root, "laufwerk", "Lauf");
        var d = new BackupService(runner, abbruch, log.Schreiben) { SourcePath = root, BackupPath = sicherung };

        d.BackupLaufwerksOrdner(new List<string>()).Wait();
        Check("nichts gewaehlt: Hinweis, kein Ordner", log.Hat("Keine Ordner gewählt") && !Directory.Exists(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner)), log.Text);

        log.Leeren();
        d.BackupLaufwerksOrdner(new[] { Path.Combine(c, "Daten"), Path.Combine(c, "scans"), Path.Combine(c, "Weg") }).Wait();
        var gesichert = Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner);
        Check("zwei Ordner gesichert, samt Unterordnern", log.Hat("Daten gesichert") && log.Hat("scans gesichert")
            && File.Exists(Path.Combine(gesichert, "Daten", "Rechnungen", "2026.pdf")) && File.Exists(Path.Combine(gesichert, "scans", "scan1.pdf")), log.Text);
        Check("fehlender Ordner: Warnung, die anderen trotzdem", log.Hat("Weg gibt es nicht") && !Directory.Exists(Path.Combine(gesichert, "Weg")), log.Text);

        // Die Sicherung liegt in einem der gewaehlten Ordner: sie bleibt draussen.
        log.Leeren();
        new BackupService(runner, abbruch, log.Schreiben) { SourcePath = root, BackupPath = zielInC }.BackupLaufwerksOrdner(new[] { Path.Combine(c, "DaSi") }).Wait();
        Check("Sicherung im gewaehlten Ordner: nicht in sich selbst kopiert", log.Hat("DaSi gesichert")
            && !Directory.Exists(Path.Combine(zielInC, LaufwerksOrdner.BackupOrdner, "DaSi", "DASI_PC_01_01_2026")), log.Text);

        // Zurueck auf ein neues "C:" - einmal leer, einmal mit neuerer Datei.
        var neu = Path.Combine(root, "laufwerk", "NeuesC");
        Datei(Path.Combine(neu, "scans", "scan1.pdf"), "neuer auf dem neuen PC");
        File.SetLastWriteTimeUtc(Path.Combine(neu, "scans", "scan1.pdf"), DateTime.UtcNow.AddDays(1));
        Datei(Path.Combine(neu, "scans", "nur-hier.txt"), "bleibt");
        var vorher = LaufwerksOrdner.Wurzel;
        try
        {
            LaufwerksOrdner.Wurzel = neu + Path.DirectorySeparatorChar;
            log.Leeren();
            d.RestoreLaufwerksOrdner(new[] { "Daten", "scans", "Fehlt" }).Wait();
            Check("Daten zurueck", log.Hat("Daten wiederhergestellt") && File.Exists(Path.Combine(neu, "Daten", "Rechnungen", "2026.pdf")), log.Text);
            Check("vorhandener Ordner: ergaenzt, neuere Datei und Eigenes bleiben", log.Hat("gibt es schon")
                && File.ReadAllText(Path.Combine(neu, "scans", "scan1.pdf")) == "neuer auf dem neuen PC" && File.Exists(Path.Combine(neu, "scans", "nur-hier.txt")), log.Text);
            Check("nicht in der Sicherung: Warnung", log.Hat("Fehlt liegt nicht in der Sicherung"), log.Text);
            log.Leeren();
            d.RestoreLaufwerksOrdner(new List<string>()).Wait();
            Check("nichts gewaehlt: Hinweis", log.Hat("nichts wiederherzustellen"), log.Text);
            log.Leeren();
            new BackupService(runner, abbruch, log.Schreiben) { SourcePath = root, BackupPath = Path.Combine(root, "laufwerk", "Leer") }.RestoreLaufwerksOrdner(new[] { "Daten" }).Wait();
            Check("Sicherung ohne Ordner von C: Fehler", log.Hat("[FEHLER] Keine Ordner von"), log.Text);

            abbruch.RequestCancel();
            log.Leeren();
            d.BackupLaufwerksOrdner(new[] { Path.Combine(c, "Daten") }).Wait();
            d.RestoreLaufwerksOrdner(new[] { "Daten" }).Wait();
            abbruch.Reset();
            Check("Abbruch: nichts mehr kopiert", !log.Hat("gesichert") && !log.Hat("wiederhergestellt"), log.Text);
        }
        finally { LaufwerksOrdner.Wurzel = vorher; }
    }
}
