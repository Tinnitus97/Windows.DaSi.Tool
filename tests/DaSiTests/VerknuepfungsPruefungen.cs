using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Angeheftete Verknuepfungen vom alten PC, deren Programm hier woanders liegt
/// oder fehlt. Anlass: Auf dem alten PC zeigte Firefox.lnk auf
/// "C:\Program Files (x86)\Mozilla Firefox", winget installierte auf dem neuen
/// das 64-Bit-Firefox nach "C:\Program Files\Mozilla Firefox"; dazu Programme,
/// die erst spaeter von Hand installiert werden.
/// </summary>
internal static partial class Program
{
    private static void NeuesZielPruefen()
    {
        Console.WriteLine("\n== Taskleiste: Programme, die hier woanders liegen ==");
        var da = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Alt\alt.exe",
            @"D:\Programme\Eigen\eigen.exe",
            @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
        };
        string? AppPath(string datei) => datei.Equals("EXCEL.EXE", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE" : null;
        string? Neu(string ziel, string p = @"C:\Program Files", string p86 = @"C:\Program Files (x86)")
            => Taskleiste.NeuesZiel(ziel, da.Contains, AppPath, p, p86);

        Check("32-Bit-Ziel, hier 64 Bit (Firefox)", Neu(@"C:\Program Files (x86)\Mozilla Firefox\firefox.exe") == @"C:\Program Files\Mozilla Firefox\firefox.exe");
        Check("64-Bit-Ziel, hier 32 Bit", Neu(@"C:\Program Files\Alt\alt.exe") == @"C:\Program Files (x86)\Alt\alt.exe");
        Check("anderer Ort laut App Paths (Excel 2013 -> Office 16)",
            Neu(@"C:\Program Files (x86)\Microsoft Office\Office15\EXCEL.EXE") == @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE");
        Check("eigener Programme-Ordner dieses PCs", Neu(@"C:\Program Files (x86)\Eigen\eigen.exe", @"D:\Programme", @"D:\Programme (x86)") == @"D:\Programme\Eigen\eigen.exe");
        Check("Ziel ist da: nichts zu tun", Neu(@"C:\Program Files\Mozilla Firefox\firefox.exe") is null);
        Check("Programm fehlt ganz", Neu(@"C:\Program Files (x86)\TK-Suite\client.exe") is null && Neu(@"C:\Tools\gibtsnicht.exe") is null);
        Check("leeres Ziel (Shell-Objekt)", Neu("") is null);
    }

    /// <summary>Taskband\Favorites eines Windows Server 2025 (CI-Laeufer): Datei-Explorer und Edge angeheftet.</summary>
    private const string FavoritesServer =
        "AKQBAAA6AB+AyCc0HxBcEEKqAy7kUofWaCYAAQAmAO++EgAAAPFMLqUPfd0BP+Hwqw993QFYu/KrD33dARQAVgAxAAAAAAB5XXKYEQBUYXNrQmFyAEAACQAEAO++eV1ymHldcpguAAAAy3oEAAAABAAAAAAAAAAAAAAAAAAAABNmVABUAGEAcwBrAEIAYQByAAAAFgASATIAlwEAAIFYDjggAEZJTEVFWH4xLkxOSwAAfAAJAAQA7755XXKYeV1ymC4AAADMegQAAAAEAAAAAAAAAAAAUgAAAAAAwB/6AEYAaQBsAGUAIABFAHgAcABsAG8AcgBlAHIALgBsAG4AawAAAEAAcwBoAGUAbABsADMAMgAuAGQAbABsACwALQAyADIAMAA2ADcAAAAcABIAAAArAO++CG3/qw993QEcAEIAAAAdAO++AgBNAGkAYwByAG8AcwBvAGYAdAAuAFcAaQBuAGQAbwB3AHMALgBFAHgAcABsAG8AcgBlAHIAAAAcACYAAAAeAO++AgBTAHkAcwB0AGUAbQBQAGkAbgBuAGUAZAAAABwAAAAAVgEAADoAH4DIJzQfEFwQQqoDLuRSh9ZoJgABACYA774SAAAA8UwupQ993QE/4fCrD33dAVi78qsPfd0BFABWADEAAAAAAHldcpgRAFRhc2tCYXIAQAAJAAQA7755XXKYeV1ymC4AAADLegQAAAAEAAAAAAAAAAAAAAAAAAAAX0hKAFQAYQBzAGsAQgBhAHIAAAAWAMQAMgDZCQAAcV3PAiAATUlDUk9TfjEuTE5LAABWAAkABADvvnldcph5XXKYLgAAANF6BAAAAAQAAAAAAAAAAAAAAAAAAABm6AcBTQBpAGMAcgBvAHMAbwBmAHQAIABFAGQAZwBlAC4AbABuAGsAAAAcABIAAAArAO++/QELrA993QEcABoAAAAdAO++AgBNAFMARQBkAGcAZQAAABwAJgAAAB4A774CAFMAeQBzAHQAZQBtAFAAaQBuAG4AZQBkAAAAHAAAAP8=";

    private static void TaskbandNamenPruefen()
    {
        Console.WriteLine("\n== Taskleiste: angeheftete Verknuepfungen in Taskband\\Favorites ==");
        var fav = Convert.FromBase64String(FavoritesServer);
        var namen = new[] { "File Explorer.lnk", "microsoft edge.LNK", "Firefox.lnk", "" };
        Check("Favorites: welche Verknuepfungen angeheftet sind (auch an ungerader Stelle, Gross/klein egal)",
            Taskleiste.AngehefteteNamen(fav, namen).SequenceEqual(new[] { "File Explorer.lnk", "microsoft edge.LNK" }), string.Join(", ", Taskleiste.AngehefteteNamen(fav, namen)));
        Check("Favorites: nichts gespeichert - keine Namen", Taskleiste.AngehefteteNamen(null, namen).Count == 0 && Taskleiste.AngehefteteNamen(new byte[] { 0 }, namen).Count == 0);
        var band = new List<DesignEintrag>
        {
            new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesVersion", Typ = "DWord", Wert = "3" },
            new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "Favorites", Typ = "Binary", Wert = FavoritesServer },
        };
        Check("Favorites aus der Sicherung gelesen", Taskleiste.Favorites(band)?.Length == fav.Length);
        Check("Favorites fehlt oder ist kaputt", Taskleiste.Favorites(band.Take(1)) is null
            && Taskleiste.Favorites(new[] { new DesignEintrag { Name = "Favorites", Typ = "Binary", Wert = "kein base64!" } }) is null);
    }

    /// <summary>Mit echten Verknuepfungen - nur unter Windows (IShellLink).</summary>
    [SupportedOSPlatform("windows")]
    private static void VerknuepfungenEchtPruefen(string root)
    {
        Console.WriteLine("\n== Taskleiste: Verknuepfungen umstellen (echt) ==");
        var programme = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programme86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var neuOrdner = Path.Combine(programme, "DaSiTest-Fuchs");
        var altOrdner = Path.Combine(programme86, "DaSiTest-Fuchs");
        try
        {
            Directory.CreateDirectory(neuOrdner);
            var exe = Path.Combine(neuOrdner, "fuchs.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, true);
            if (Directory.Exists(altOrdner)) Directory.Delete(altOrdner, true);

            var ordner = Path.Combine(root, "verknuepfungen");
            Directory.CreateDirectory(ordner);
            var altesZiel = Path.Combine(altOrdner, "fuchs.exe");
            // Wie Firefox: die App-Kennung ist der Schluessel des Installationsordners.
            Verknuepfung.Anlegen(Path.Combine(ordner, "Fuchs.lnk"), altesZiel, MozillaProfile.Schluessel(altOrdner));
            Verknuepfung.Anlegen(Path.Combine(ordner, "Weg.lnk"), @"C:\DaSiTest-gibt-es-nicht\weg.exe");
            Verknuepfung.Anlegen(Path.Combine(ordner, "Da.lnk"), Path.Combine(Environment.SystemDirectory, "cmd.exe"), "DaSi.Test.Da");

            var gelesen = Verknuepfung.Lesen(Path.Combine(ordner, "Fuchs.lnk"));
            Check("Verknuepfung lesen: Ziel, Ordner, Symbol, App-Kennung",
                gelesen is not null && gelesen.Ziel.Equals(altesZiel, StringComparison.OrdinalIgnoreCase)
                && gelesen.Arbeitsordner.Equals(altOrdner, StringComparison.OrdinalIgnoreCase)
                && gelesen.AppId == MozillaProfile.Schluessel(altOrdner), $"{gelesen}");
            Check("keine Verknuepfung: nichts zu lesen", Verknuepfung.Lesen(Path.Combine(ordner, "gibtsnicht.lnk")) is null);
            Check("keine Verknuepfung: nichts umzustellen", !Verknuepfung.ZielSetzen(Path.Combine(ordner, "gibtsnicht.lnk"), exe));

            var c = new CancellationTokenState();
            var log = new Protokoll();
            var d = new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = root, BackupPath = root };
            d.TaskleistenZielePruefen(ordner, new[] { "Da.lnk", "Fuchs.lnk", "Weg.lnk" });

            var fuchs = Verknuepfung.Lesen(Path.Combine(ordner, "Fuchs.lnk"));
            Check("Fuchs: auf den 64-Bit-Ordner umgestellt, samt Arbeitsordner und Symbol",
                fuchs is not null && fuchs.Ziel.Equals(exe, StringComparison.OrdinalIgnoreCase)
                && fuchs.Arbeitsordner.Equals(neuOrdner, StringComparison.OrdinalIgnoreCase)
                && fuchs.Symbol.Equals(exe, StringComparison.OrdinalIgnoreCase), $"{fuchs}");
            Check("Fuchs: App-Kennung der neuen Installation", fuchs?.AppId == MozillaProfile.Schluessel(MozillaProfile.EchterPfad(neuOrdner)), fuchs?.AppId);
            Check("Da: unveraendert", Verknuepfung.Lesen(Path.Combine(ordner, "Da.lnk"))?.AppId == "DaSi.Test.Da");
            Check("Protokoll: umgestellt und fehlend",
                log.Hat("[INFO] Taskleiste: Fuchs zeigte auf") && log.Hat("die Verknüpfung ist umgestellt")
                && log.Hat("[WARNUNG] Taskleiste: Diese angehefteten Programme gibt es auf diesem PC (noch) nicht: Weg (C:\\DaSiTest-gibt-es-nicht\\weg.exe)")
                && !log.Hat("Da ("), log.Text);
        }
        finally
        {
            try { Directory.Delete(neuOrdner, true); } catch { }
        }
    }
}
