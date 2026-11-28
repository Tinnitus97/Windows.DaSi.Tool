using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DaSiPruefung;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Backups written by earlier versions must stay restorable - and backups written
/// now must stay readable for earlier versions. The files under
/// tests/DaSiTests/fixtures/backup-1.2.8 were written by version 1.2.8, before the
/// code got English names. Each one is read with the current code and written again:
/// the values must match, and the file must come out character for character the same.
/// </summary>
internal static partial class Program
{
    private static string FixtureBackup()
        => Path.Combine(Fakes.Repository(), "tests", "DaSiTests", "fixtures", "backup-1.2.8");

    private static void BackupCompatibilityCheck(string root)
    {
        Console.WriteLine("\n== Sicherung von 1.2.8: lesen und gleich zurueckschreiben ==");
        var alt = FixtureBackup();
        var neu = Path.Combine(root, "kompatibel");
        Directory.CreateDirectory(neu);
        bool Gleich(params string[] rel)
        {
            var a = Path.Combine(alt, Path.Combine(rel));
            var n = Path.Combine(neu, Path.Combine(rel));
            // Line endings follow the platform (and git's checkout settings) - the content must match.
            static string Text(string f) => File.ReadAllText(f).Replace("\r\n", "\n");
            return File.Exists(a) && File.Exists(n) && Text(a) == Text(n);
        }
        var erstellt = new DateTime(2026, 11, 20, 18, 45, 0);

        // Design.json: design settings and wallpaper in one file.
        var d = DesignSettings.Load(alt);
        Check("Design.json von 1.2.8: Kopf gelesen", d is { Version: 2, Rechner: "DESKTOP-ALT" } && d.Erstellt == erstellt);
        Check("Design.json von 1.2.8: Werte gelesen",
            d is { Werte.Count: 2 }
            && d.Werte[0] is { Gruppe: "Farben", Schluessel: @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name: "AppsUseLightTheme", Typ: "DWord", Wert: "0" }
            && d.Werte[1] is { Gruppe: "Mauszeiger", Name: "", Typ: "String", Wert: "Windows-Standard (große Zeiger)" });
        Check("Design.json von 1.2.8: Hintergrund gelesen",
            d?.Hintergrund is { Art: "Bild", Farbe: "0 99 177", Darstellung: "10", Kacheln: "0", Bild: "Strand.jpg", BildPfad: @"C:\Users\Anwender\Pictures\Strand.jpg", Profil: @"C:\Users\Anwender" });
        if (d is not null) DesignSettings.Save(neu, d);
        Check("Design.json: Zeichen fuer Zeichen gleich zurueckgeschrieben", Gleich(DesignSettings.Ordner, DesignSettings.DateiName));

        // Hintergrundbild\Hintergrund.json: the wallpaper in the older format.
        var h = Wallpaper.Load(Path.Combine(alt, "Hintergrundbild"));
        Check("Hintergrund.json von 1.2.8 gelesen",
            h is { Art: "Volltonfarbe", Farbe: "107 105 214", Darstellung: "6", Kacheln: "0", Bild: null, BildPfad: null, Profil: @"C:\Users\Anwender" }
            && Wallpaper.IsSolidColor(h));
        Directory.CreateDirectory(Path.Combine(neu, "Hintergrundbild"));
        if (h is not null) Wallpaper.Save(Path.Combine(neu, "Hintergrundbild"), h);
        Check("Hintergrund.json: Zeichen fuer Zeichen gleich zurueckgeschrieben", Gleich("Hintergrundbild", HintergrundEinstellung.DateiName));

        // Desktop-Symbole\Symbolpositionen.json: the icon positions.
        var s = DesktopIcons.Load(DesktopIcons.FilePath(alt));
        Check("Symbolpositionen.json von 1.2.8: Kopf gelesen",
            s is { Version: 1, Rechner: "DESKTOP-ALT", Benutzer: "Anwender", Breite: 1920, Hoehe: 1080, AutomatischAnordnen: false, AmRasterAusrichten: true } && s.Erstellt == erstellt);
        Check("Symbolpositionen.json von 1.2.8: Symbole gelesen",
            s is { Symbole.Count: 2 }
            && s.Symbole[0] == new DesktopIcon("Präsentation.pptx", "Präsentation", 100, 200)
            && s.Symbole[1] == new DesktopIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", "Papierkorb", 10, 10));
        if (s is not null) DesktopIcons.Save(Path.Combine(neu, DesktopIcons.FolderName), s);
        Check("Symbolpositionen.json: Zeichen fuer Zeichen gleich zurueckgeschrieben", Gleich(DesktopIcons.FolderName, DesktopIcons.FileName));
        Check("Symbolpositionen.json: Verfuegbarkeit erkennt die Sicherung", AppCatalog.HasDesktopIcons(alt));

        // Desktop-Symbole\Taskleiste\Taskleiste.json: the pinned taskbar icons.
        var t = Taskbar.Load(alt);
        Check("Taskleiste.json von 1.2.8 gelesen",
            t is { Version: 1, Rechner: "DESKTOP-ALT", Verknuepfungen.Count: 2, Taskband.Count: 1 } && t.Erstellt == erstellt
            && t.Verknuepfungen.SequenceEqual(new[] { "Firefox.lnk", "Microsoft Edge.lnk" })
            && t.Taskband[0] is { Name: "Favorites", Typ: "Binary", Wert: "AKQDAAA=" });
        if (t is not null) Taskbar.Save(neu, t);
        Check("Taskleiste.json: Zeichen fuer Zeichen gleich zurueckgeschrieben", Gleich(DesktopIcons.FolderName, Taskbar.Ordner, Taskbar.DateiName));

        // Thunderbird-Extern\Postfaecher.json: mail folders outside the profile.
        var pfad = Path.Combine(alt, BackupService.ThunderbirdExternOrdner, BackupService.ExternalMailFile);
        var p = File.Exists(pfad) ? BackupService.DeserializeExternalMail(File.ReadAllText(pfad)) : null;
        Check("Postfaecher.json von 1.2.8 gelesen",
            p is { Benutzerordner: @"C:\Users\Anwender", Postfaecher.Count: 1 }
            && p.Postfaecher[0] is { Profil: @"Profiles\abcd1234.default-release", Server: "Archiv", Ordner: @"D:\Mail\Archiv", Sicherung: "1" });
        Directory.CreateDirectory(Path.Combine(neu, BackupService.ThunderbirdExternOrdner));
        if (p is not null)
            File.WriteAllText(Path.Combine(neu, BackupService.ThunderbirdExternOrdner, BackupService.ExternalMailFile), BackupService.SerializeExternalMail(p));
        Check("Postfaecher.json: Zeichen fuer Zeichen gleich zurueckgeschrieben", Gleich(BackupService.ThunderbirdExternOrdner, BackupService.ExternalMailFile));
    }
}
