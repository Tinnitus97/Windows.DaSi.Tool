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
        Check("Programm fehlt ganz", Neu(@"C:\Program Files (x86)\Telefonie\client.exe") is null && Neu(@"C:\Tools\gibtsnicht.exe") is null);
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
        var eintraege = Taskleiste.Eintraege(fav, new[] { "File Explorer.lnk", "Microsoft Edge.lnk" });
        Check("Favorites: Eintraege mit Verknuepfung und App-Kennung",
            eintraege.SequenceEqual(new[] { new Taskleiste.Eintrag("File Explorer.lnk", "Microsoft.Windows.Explorer"), new Taskleiste.Eintrag("Microsoft Edge.lnk", "MSEdge") }),
            string.Join(", ", eintraege));
        Check("Favorites: Eintraege ohne bekannte Verknuepfung, abgeschnitten, leer",
            Taskleiste.Eintraege(fav, Array.Empty<string>()).Select(e => e.Verknuepfung).All(v => v is null)
            && Taskleiste.Eintraege(fav[..300], Array.Empty<string>()).Count == 0 && Taskleiste.Eintraege(null, Array.Empty<string>()).Count == 0);
        Check("Favorites fehlt oder ist kaputt", Taskleiste.Favorites(band.Take(1)) is null
            && Taskleiste.Favorites(new[] { new DesignEintrag { Name = "Favorites", Typ = "Binary", Wert = "kein base64!" } }) is null);
    }

    /// <summary>Ein Favorites-Wert mit je einem Block BEEF001D (App-Kennung) pro Kennung - wie Windows ihn schreibt.</summary>
    private static byte[] FavoritesMit(params string[] kennungen)
    {
        var b = new List<byte> { 0x00 };
        foreach (var k in kennungen)
        {
            var text = System.Text.Encoding.Unicode.GetBytes(k + "\0");
            var laenge = 8 + 2 + text.Length + 2;
            b.AddRange(BitConverter.GetBytes((ushort)laenge));
            b.AddRange(new byte[] { 0x00, 0x00, 0x1D, 0x00, 0xEF, 0xBE, 0x02, 0x00 });
            b.AddRange(text);
            b.AddRange(new byte[] { 0x1C, 0x00 });
        }
        b.Add(0xFF);
        return b.ToArray();
    }

    private static void KennungenPruefen()
    {
        Console.WriteLine("\n== Taskleiste: App-Kennungen in Taskband\\Favorites ==");
        Check("Kennungen eines Windows Server 2025", Taskleiste.Kennungen(Convert.FromBase64String(FavoritesServer))
            .SequenceEqual(new[] { "Microsoft.Windows.Explorer", "MSEdge" }), string.Join(", ", Taskleiste.Kennungen(Convert.FromBase64String(FavoritesServer))));
        var fav = FavoritesMit("E7CF176E110C211B", Taskleiste.OrdnerProgramme86 + @"\Hersteller\app.exe");
        Check("Kennungen eines Windows-10-PCs (Firefox 32 Bit, Programm in x86)",
            Taskleiste.Kennungen(fav).SequenceEqual(new[] { "E7CF176E110C211B", Taskleiste.OrdnerProgramme86 + @"\Hersteller\app.exe" }));
        Check("Kennungen: nichts gespeichert", Taskleiste.Kennungen(null).Count == 0 && Taskleiste.Kennungen(new byte[] { 0x20, 0, 0x1D, 0, 0xEF, 0xBE, 0, 0, 0, 0, 0, 0 }).Count == 0);
        var ersetzt = Taskleiste.KennungErsetzen(fav, "E7CF176E110C211B", "308046B0AF4A39CB");
        Check("Kennung ersetzt, Laenge gleich", ersetzt.Length == fav.Length && Taskleiste.Kennungen(ersetzt)[0] == "308046B0AF4A39CB" && Taskleiste.Kennungen(fav)[0] == "E7CF176E110C211B");
        bool abgelehnt = false;
        try { Taskleiste.KennungErsetzen(fav, "MSEdge", "Firefox"); } catch (ArgumentException) { abgelehnt = true; }
        Check("Kennung anderer Laenge: abgelehnt", abgelehnt);

        var mozilla = new Dictionary<string, string> { ["E7CF176E110C211B"] = "308046B0AF4A39CB", ["ABC"] = "308046B0AF4A39CB", ["308046B0AF4A39CB"] = "308046B0AF4A39CB" };
        var ordner = new Dictionary<string, string>
        {
            [Taskleiste.OrdnerProgramme86] = @"C:\Program Files (x86)",
            [Taskleiste.OrdnerProgramme64] = @"C:\Program Files",
        };
        var da = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\Neu64\app.exe", @"C:\Program Files (x86)\Alt86\app.exe" };
        string? Neu(string k) => Taskleiste.NeueKennung(k, mozilla, ordner, p => da.Contains(p.Replace('/', '\\')));
        Check("Firefox 32 -> 64 Bit", Neu("E7CF176E110C211B") == "308046B0AF4A39CB");
        Check("Firefox: gleiche Installation oder andere Laenge - bleibt", Neu("308046B0AF4A39CB") is null && Neu("ABC") is null);
        Check("Programm aus x86, hier 64 Bit", Neu(Taskleiste.OrdnerProgramme86 + @"\Neu64\app.exe") == Taskleiste.OrdnerProgramme64 + @"\Neu64\app.exe");
        Check("Programm liegt hier wie dort - bleibt", Neu(Taskleiste.OrdnerProgramme86 + @"\Alt86\app.exe") is null);
        Check("Programm gibt es hier nicht - bleibt", Neu(Taskleiste.OrdnerProgramme86 + @"\Fehlt\app.exe") is null);
        Check("eigene Kennung, unbekannter Ordner - bleibt", Neu("Hersteller.Programm.Client.1") is null
            && Neu("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\SnippingTool.exe") is null && Neu("{kurz}\\x.exe") is null);
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
            var ohneProgramm = d.TaskleistenZielePruefen(ordner, new[] { "Da.lnk", "Fuchs.lnk", "Weg.lnk" });
            Check("fehlendes Programm zurueckgemeldet", ohneProgramm.SequenceEqual(new[] { "Weg.lnk" }), string.Join(", ", ohneProgramm));

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

            // App-Kennungen in Favorites: 32-Bit-Firefox -> das Firefox hier,
            // Programm aus "Program Files (x86)" -> liegt hier in "Program Files".
            var kennungFuchs = Taskleiste.OrdnerProgramme86 + @"\DaSiTest-Fuchs\fuchs.exe";
            var fav = FavoritesMit("E7CF176E110C211B", kennungFuchs, "Microsoft.Windows.Explorer", "Hersteller.Programm.Client.1");
            var band = new List<DesignEintrag>
            {
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "Favorites", Typ = "Binary", Wert = Convert.ToBase64String(fav) },
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesResolve", Typ = "Binary", Wert = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("x E7CF176E110C211B y")) },
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesVersion", Typ = "DWord", Wert = "3" },
            };
            d.TestInstallationen = new List<string> { @"C:\Program Files\Mozilla Firefox" };
            log.Leeren();
            var angepasst = d.KennungenAnpassen(band);
            var kennungen = Taskleiste.Kennungen(Taskleiste.Favorites(angepasst));
            Check("Kennungen: Firefox und Programm an diesen PC angepasst, die anderen bleiben",
                kennungen.SequenceEqual(new[] { "308046B0AF4A39CB", Taskleiste.OrdnerProgramme64 + @"\DaSiTest-Fuchs\fuchs.exe", "Microsoft.Windows.Explorer", "Hersteller.Programm.Client.1" }),
                string.Join(", ", kennungen));
            Check("Kennungen: auch in FavoritesResolve, die Sicherung selbst unveraendert",
                System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(angepasst[1].Wert)).Contains("308046B0AF4A39CB")
                && Taskleiste.Kennungen(Taskleiste.Favorites(band))[0] == "E7CF176E110C211B" && angepasst[2].Wert == "3");
            Check("Kennungen: im Protokoll", log.Hat("[INFO] Taskleiste: Kennungen an die Programme dieses PCs angepasst") && log.Hat("E7CF176E110C211B -> 308046B0AF4A39CB"), log.Text);
            log.Leeren();
            var ohne = d.KennungenAnpassen(band.Skip(2).ToList());
            Check("Kennungen: ohne Favorites nichts zu tun", ohne.Count == 1 && !log.Hat("Kennungen"), log.Text);
            d.TestInstallationen = new List<string>();
            Check("Kennungen: kein Firefox hier - Firefox-Kennung bleibt",
                Taskleiste.Kennungen(Taskleiste.Favorites(d.KennungenAnpassen(band)))[0] == "E7CF176E110C211B");
        }
        finally
        {
            try { Directory.Delete(neuOrdner, true); } catch { }
        }
    }
}
