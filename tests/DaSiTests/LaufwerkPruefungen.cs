using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// "Folders under C:\": the list (what Windows creates there stays hidden),
/// the availability of the item and - on Windows with real robocopy -
/// backup and restore on a fake drive in the temp folder.
/// </summary>
internal static partial class Program
{
    private static void DriveFoldersCheck(string root)
    {
        Console.WriteLine("\n== Ordner unter C:\\: Liste, Windows-Ordner, Verfuegbarkeit ==");
        Check("Windows-Ordner erkannt", LaufwerksOrdner.IsWindowsFolder("Windows") && LaufwerksOrdner.IsWindowsFolder("program files")
            && LaufwerksOrdner.IsWindowsFolder("Program Files (x86)") && LaufwerksOrdner.IsWindowsFolder("Users") && LaufwerksOrdner.IsWindowsFolder("ProgramData")
            && LaufwerksOrdner.IsWindowsFolder("$Recycle.Bin") && LaufwerksOrdner.IsWindowsFolder("$WinREAgent") && LaufwerksOrdner.IsWindowsFolder("System Volume Information")
            && LaufwerksOrdner.IsWindowsFolder("XboxGames") && LaufwerksOrdner.IsWindowsFolder("Windows10Upgrade"));
        Check("versteckter Systemordner gilt als Windows-Ordner", LaufwerksOrdner.IsWindowsFolder("Irgendwas", FileAttributes.Hidden | FileAttributes.System)
            && !LaufwerksOrdner.IsWindowsFolder("Irgendwas", FileAttributes.Hidden));
        Check("eigene Ordner sind keine Windows-Ordner", !LaufwerksOrdner.IsWindowsFolder("Daten") && !LaufwerksOrdner.IsWindowsFolder("Scans")
            && !LaufwerksOrdner.IsWindowsFolder("Intel") && !LaufwerksOrdner.IsWindowsFolder("Windows-Daten"));
        Check("Systemlaufwerk mit Backslash", LaufwerksOrdner.DefaultRoot().EndsWith('\\') && LaufwerksOrdner.DefaultRoot().Length == 3, LaufwerksOrdner.DefaultRoot());

        // A fake drive: Windows folders, own folders, the backup itself.
        var c = Path.Combine(root, "laufwerk", "C");
        foreach (var n in new[] { "Windows", "Program Files", "Users", "$Recycle.Bin", "PerfLogs" }) Directory.CreateDirectory(Path.Combine(c, n));
        WriteFile(Path.Combine(c, "Daten", "Rechnungen", "2026.pdf"), new string('r', 1000));
        WriteFile(Path.Combine(c, "Daten", "liste.txt"), new string('l', 24));
        WriteFile(Path.Combine(c, "scans", "scan1.pdf"), new string('s', 500));
        Directory.CreateDirectory(Path.Combine(c, "Leer"));
        var ziel = Path.Combine(c, "DaSi", "DASI_PC_01_01_2026");
        Directory.CreateDirectory(ziel);
        var verknuepft = false;
        try { Directory.CreateSymbolicLink(Path.Combine(c, "Verknuepfung"), Path.Combine(c, "Daten")); verknuepft = true; } catch { }

        var liste = LaufwerksOrdner.ListFolders(c, ziel);
        var namen = liste.Select(e => e.Name).ToList();
        Check("Liste nach Namen, ohne den Ordner der Sicherung", string.Join(",", namen.Where(n => !LaufwerksOrdner.IsWindowsFolder(n))) == "Daten,Leer,scans",
            string.Join(",", namen));
        Check("Windows-Ordner in der Liste markiert", liste.Where(e => e.Windows).Select(e => e.Name).OrderBy(n => n).SequenceEqual(new[] { "$Recycle.Bin", "PerfLogs", "Program Files", "Users", "Windows" }),
            string.Join(",", liste.Where(e => e.Windows).Select(e => e.Name)));
        Check("voller Pfad je Eintrag", liste.First(e => e.Name == "Daten").Pfad == Path.Combine(c, "Daten"), liste.First(e => e.Name == "Daten").Pfad);
        if (verknuepft) Check("Verknuepfung (Junction/Link) nie in der Liste", !namen.Contains("Verknuepfung"), string.Join(",", namen));
        Check("Ziel woanders: DaSi-Ordner erscheint", LaufwerksOrdner.ListFolders(c, Path.Combine(root, "anderswo")).Any(e => e.Name == "DaSi")
            && LaufwerksOrdner.ListFolders(c, null).Any(e => e.Name == "DaSi"));
        Check("Laufwerk gibt es nicht: leere Liste", LaufwerksOrdner.ListFolders(Path.Combine(root, "gibtsnicht"), null).Count == 0);

        Check("enthaelt: Ordner selbst und darin, nicht daneben", LaufwerksOrdner.Contains(Path.Combine(c, "DaSi"), ziel)
            && LaufwerksOrdner.Contains(ziel, ziel) && !LaufwerksOrdner.Contains(Path.Combine(c, "Da"), ziel) && !LaufwerksOrdner.Contains(Path.Combine(c, "Daten"), ziel));
        Check("enthaelt: Gross-/Kleinschreibung egal", LaufwerksOrdner.Contains(Path.Combine(c, "dasi"), ziel.ToUpperInvariant()) || !OperatingSystem.IsWindows());
        Check("Groesse: alle Dateien darin", LaufwerksOrdner.SizeOf(Path.Combine(c, "Daten")) == 1024 && LaufwerksOrdner.SizeOf(Path.Combine(c, "Leer")) == 0,
            LaufwerksOrdner.SizeOf(Path.Combine(c, "Daten")).ToString());
        using (var abbruch = new CancellationTokenSource())
        {
            abbruch.Cancel();
            Check("Groesse: abgebrochen = -1", LaufwerksOrdner.SizeOf(Path.Combine(c, "Daten"), abbruch.Token) == -1);
        }
        Check("Groesse: Ordner fehlt = 0", LaufwerksOrdner.SizeOf(Path.Combine(c, "Fehlt")) == 0);

        // In the backup.
        var sicherung = Path.Combine(root, "laufwerk", "Sicherung");
        Check("Sicherung ohne C-Ordner: nichts", !LaufwerksOrdner.Exists(sicherung) && LaufwerksOrdner.InBackup(sicherung).Count == 0 && !LaufwerksOrdner.Exists(null));
        WriteFile(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner, "Daten", "liste.txt"));
        Directory.CreateDirectory(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner, "Program Files"));
        var gesichert = LaufwerksOrdner.InBackup(sicherung);
        Check("Sicherung: Ordner darin, Windows-Ordner markiert", LaufwerksOrdner.Exists(sicherung) && gesichert.Count == 2
            && gesichert.Single(e => e.Name == "Program Files").Windows && !gesichert.Single(e => e.Name == "Daten").Windows);

        // Availability of the item.
        var vorher = LaufwerksOrdner.Wurzel;
        try
        {
            LaufwerksOrdner.Wurzel = c;
            Check("Sichern: Laufwerk da - Kachel frei", AppCatalog.ForBackup(Punkt.Laufwerk, null) is null);
            LaufwerksOrdner.Wurzel = Path.Combine(root, "gibtsnicht") + Path.DirectorySeparatorChar;
            Check("Sichern: Laufwerk fehlt - Grund", AppCatalog.ForBackup(Punkt.Laufwerk, null)?.De.Contains("gibt es hier nicht") == true);
        }
        finally { LaufwerksOrdner.Wurzel = vorher; }
        Check("Wiederherstellen: Ordner in der Sicherung - frei", AppCatalog.ForRestore(Punkt.Laufwerk, sicherung) is null);
        WriteFile(Path.Combine(root, "laufwerk", "NurWlan", "WLAN-Profile", "a.xml"));
        Check("Wiederherstellen: keine Ordner in der Sicherung - Grund", AppCatalog.ForRestore(Punkt.Laufwerk, Path.Combine(root, "laufwerk", "NurWlan"))?.De.Contains("keine Ordner von") == true);
        Check("Ordner unter C:\\ allein machen einen Ordner zur Sicherung", AppCatalog.IsBackup(sicherung));

        if (OperatingSystem.IsWindows()) DriveFoldersWithRobocopy(root, c, ziel);
    }

    /// <summary>Backup and restore with real robocopy.</summary>
    private static void DriveFoldersWithRobocopy(string root, string c, string zielInC)
    {
        Console.WriteLine("\n== Ordner unter C:\\: sichern und wiederherstellen (robocopy) ==");
        var abbruch = new CancellationTokenState();
        var runner = new ProcessRunner(abbruch);
        var log = new Protokoll();
        var sicherung = Path.Combine(root, "laufwerk", "Lauf");
        var d = new BackupService(runner, abbruch, log.Write) { SourcePath = root, BackupPath = sicherung };

        d.BackupDriveFolders(new List<string>()).Wait();
        Check("nichts gewaehlt: Hinweis, kein Ordner", log.Has("Keine Ordner gewählt") && !Directory.Exists(Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner)), log.Text);

        log.Clear();
        d.BackupDriveFolders(new[] { Path.Combine(c, "Daten"), Path.Combine(c, "scans"), Path.Combine(c, "Weg") }).Wait();
        var gesichert = Path.Combine(sicherung, LaufwerksOrdner.BackupOrdner);
        Check("zwei Ordner gesichert, samt Unterordnern", log.Has("Daten gesichert") && log.Has("scans gesichert")
            && File.Exists(Path.Combine(gesichert, "Daten", "Rechnungen", "2026.pdf")) && File.Exists(Path.Combine(gesichert, "scans", "scan1.pdf")), log.Text);
        Check("fehlender Ordner: Warnung, die anderen trotzdem", log.Has("Weg gibt es nicht") && !Directory.Exists(Path.Combine(gesichert, "Weg")), log.Text);

        // The backup lives inside one of the chosen folders: it stays out.
        log.Clear();
        new BackupService(runner, abbruch, log.Write) { SourcePath = root, BackupPath = zielInC }.BackupDriveFolders(new[] { Path.Combine(c, "DaSi") }).Wait();
        Check("Sicherung im gewaehlten Ordner: nicht in sich selbst kopiert", log.Has("DaSi gesichert")
            && !Directory.Exists(Path.Combine(zielInC, LaufwerksOrdner.BackupOrdner, "DaSi", "DASI_PC_01_01_2026")), log.Text);

        // Restore onto a new "C:" - once empty, once with a newer file.
        var neu = Path.Combine(root, "laufwerk", "NeuesC");
        WriteFile(Path.Combine(neu, "scans", "scan1.pdf"), "neuer auf dem neuen PC");
        File.SetLastWriteTimeUtc(Path.Combine(neu, "scans", "scan1.pdf"), DateTime.UtcNow.AddDays(1));
        WriteFile(Path.Combine(neu, "scans", "nur-hier.txt"), "bleibt");
        var vorher = LaufwerksOrdner.Wurzel;
        try
        {
            LaufwerksOrdner.Wurzel = neu + Path.DirectorySeparatorChar;
            log.Clear();
            d.RestoreDriveFolders(new[] { "Daten", "scans", "Fehlt" }).Wait();
            Check("Daten zurueck", log.Has("Daten wiederhergestellt") && File.Exists(Path.Combine(neu, "Daten", "Rechnungen", "2026.pdf")), log.Text);
            Check("vorhandener Ordner: ergaenzt, neuere Datei und Eigenes bleiben", log.Has("gibt es schon")
                && File.ReadAllText(Path.Combine(neu, "scans", "scan1.pdf")) == "neuer auf dem neuen PC" && File.Exists(Path.Combine(neu, "scans", "nur-hier.txt")), log.Text);
            Check("nicht in der Sicherung: Warnung", log.Has("Fehlt liegt nicht in der Sicherung"), log.Text);
            log.Clear();
            d.RestoreDriveFolders(new List<string>()).Wait();
            Check("nichts gewaehlt: Hinweis", log.Has("nichts wiederherzustellen"), log.Text);
            log.Clear();
            new BackupService(runner, abbruch, log.Write) { SourcePath = root, BackupPath = Path.Combine(root, "laufwerk", "Leer") }.RestoreDriveFolders(new[] { "Daten" }).Wait();
            Check("Sicherung ohne Ordner von C: Fehler", log.Has("[FEHLER] Keine Ordner von"), log.Text);

            abbruch.RequestCancel();
            log.Clear();
            d.BackupDriveFolders(new[] { Path.Combine(c, "Daten") }).Wait();
            d.RestoreDriveFolders(new[] { "Daten" }).Wait();
            abbruch.Reset();
            Check("Abbruch: nichts mehr kopiert", !log.Has("gesichert") && !log.Has("wiederhergestellt"), log.Text);
        }
        finally { LaufwerksOrdner.Wurzel = vorher; }
    }
}
