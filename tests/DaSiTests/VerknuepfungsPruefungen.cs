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
    private static void NewTargetCheck()
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
        string? NewTarget(string ziel, string p = @"C:\Program Files", string p86 = @"C:\Program Files (x86)")
            => Taskleiste.NewTarget(ziel, da.Contains, AppPath, p, p86);

        Check("32-Bit-Ziel, hier 64 Bit (Firefox)", NewTarget(@"C:\Program Files (x86)\Mozilla Firefox\firefox.exe") == @"C:\Program Files\Mozilla Firefox\firefox.exe");
        Check("64-Bit-Ziel, hier 32 Bit", NewTarget(@"C:\Program Files\Alt\alt.exe") == @"C:\Program Files (x86)\Alt\alt.exe");
        Check("anderer Ort laut App Paths (Excel 2013 -> Office 16)",
            NewTarget(@"C:\Program Files (x86)\Microsoft Office\Office15\EXCEL.EXE") == @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE");
        Check("eigener Programme-Ordner dieses PCs", NewTarget(@"C:\Program Files (x86)\Eigen\eigen.exe", @"D:\Programme", @"D:\Programme (x86)") == @"D:\Programme\Eigen\eigen.exe");
        Check("Ziel ist da: nichts zu tun", NewTarget(@"C:\Program Files\Mozilla Firefox\firefox.exe") is null);
        Check("Programm fehlt ganz", NewTarget(@"C:\Program Files (x86)\Telefonie\client.exe") is null && NewTarget(@"C:\Tools\gibtsnicht.exe") is null);
        Check("leeres Ziel (Shell-Objekt)", NewTarget("") is null);
    }

    /// <summary>Taskband\Favorites eines Windows Server 2025 (CI-Laeufer): Datei-Explorer und Edge angeheftet.</summary>
    private const string FavoritesServer =
        "AKQBAAA6AB+AyCc0HxBcEEKqAy7kUofWaCYAAQAmAO++EgAAAPFMLqUPfd0BP+Hwqw993QFYu/KrD33dARQAVgAxAAAAAAB5XXKYEQBUYXNrQmFyAEAACQAEAO++eV1ymHldcpguAAAAy3oEAAAABAAAAAAAAAAAAAAAAAAAABNmVABUAGEAcwBrAEIAYQByAAAAFgASATIAlwEAAIFYDjggAEZJTEVFWH4xLkxOSwAAfAAJAAQA7755XXKYeV1ymC4AAADMegQAAAAEAAAAAAAAAAAAUgAAAAAAwB/6AEYAaQBsAGUAIABFAHgAcABsAG8AcgBlAHIALgBsAG4AawAAAEAAcwBoAGUAbABsADMAMgAuAGQAbABsACwALQAyADIAMAA2ADcAAAAcABIAAAArAO++CG3/qw993QEcAEIAAAAdAO++AgBNAGkAYwByAG8AcwBvAGYAdAAuAFcAaQBuAGQAbwB3AHMALgBFAHgAcABsAG8AcgBlAHIAAAAcACYAAAAeAO++AgBTAHkAcwB0AGUAbQBQAGkAbgBuAGUAZAAAABwAAAAAVgEAADoAH4DIJzQfEFwQQqoDLuRSh9ZoJgABACYA774SAAAA8UwupQ993QE/4fCrD33dAVi78qsPfd0BFABWADEAAAAAAHldcpgRAFRhc2tCYXIAQAAJAAQA7755XXKYeV1ymC4AAADLegQAAAAEAAAAAAAAAAAAAAAAAAAAX0hKAFQAYQBzAGsAQgBhAHIAAAAWAMQAMgDZCQAAcV3PAiAATUlDUk9TfjEuTE5LAABWAAkABADvvnldcph5XXKYLgAAANF6BAAAAAQAAAAAAAAAAAAAAAAAAABm6AcBTQBpAGMAcgBvAHMAbwBmAHQAIABFAGQAZwBlAC4AbABuAGsAAAAcABIAAAArAO++/QELrA993QEcABoAAAAdAO++AgBNAFMARQBkAGcAZQAAABwAJgAAAB4A774CAFMAeQBzAHQAZQBtAFAAaQBuAG4AZQBkAAAAHAAAAP8=";

    private static void TaskbandNamesCheck()
    {
        Console.WriteLine("\n== Taskleiste: angeheftete Verknuepfungen in Taskband\\Favorites ==");
        var fav = Convert.FromBase64String(FavoritesServer);
        var namen = new[] { "File Explorer.lnk", "microsoft edge.LNK", "Firefox.lnk", "" };
        Check("Favorites: welche Verknuepfungen angeheftet sind (auch an ungerader Stelle, Gross/klein egal)",
            Taskleiste.PinnedNames(fav, namen).SequenceEqual(new[] { "File Explorer.lnk", "microsoft edge.LNK" }), string.Join(", ", Taskleiste.PinnedNames(fav, namen)));
        Check("Favorites: nichts gespeichert - keine Namen", Taskleiste.PinnedNames(null, namen).Count == 0 && Taskleiste.PinnedNames(new byte[] { 0 }, namen).Count == 0);
        var band = new List<DesignEintrag>
        {
            new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesVersion", Typ = "DWord", Wert = "3" },
            new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "Favorites", Typ = "Binary", Wert = FavoritesServer },
        };
        Check("Favorites aus der Sicherung gelesen", Taskleiste.Favorites(band)?.Length == fav.Length);
        var eintraege = Taskleiste.Entries(fav, new[] { "File Explorer.lnk", "Microsoft Edge.lnk" });
        Check("Favorites: Eintraege mit Verknuepfung und App-Kennung",
            eintraege.SequenceEqual(new[] { new Taskleiste.Eintrag("File Explorer.lnk", "Microsoft.Windows.Explorer"), new Taskleiste.Eintrag("Microsoft Edge.lnk", "MSEdge") }),
            string.Join(", ", eintraege));
        Check("Favorites: Eintraege ohne bekannte Verknuepfung, abgeschnitten, leer",
            Taskleiste.Entries(fav, Array.Empty<string>()).Select(e => e.Verknuepfung).All(v => v is null)
            && Taskleiste.Entries(fav[..300], Array.Empty<string>()).Count == 0 && Taskleiste.Entries(null, Array.Empty<string>()).Count == 0);
        Check("Favorites fehlt oder ist kaputt", Taskleiste.Favorites(band.Take(1)) is null
            && Taskleiste.Favorites(new[] { new DesignEintrag { Name = "Favorites", Typ = "Binary", Wert = "kein base64!" } }) is null);
    }

    /// <summary>Ein Favorites-Wert mit je einem Block BEEF001D (App-Kennung) pro Kennung - wie Windows ihn schreibt.</summary>
    private static byte[] FavoritesWith(params string[] kennungen)
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

    private static void AppIdsCheck()
    {
        Console.WriteLine("\n== Taskleiste: App-Kennungen in Taskband\\Favorites ==");
        Check("Kennungen eines Windows Server 2025", Taskleiste.AppIds(Convert.FromBase64String(FavoritesServer))
            .SequenceEqual(new[] { "Microsoft.Windows.Explorer", "MSEdge" }), string.Join(", ", Taskleiste.AppIds(Convert.FromBase64String(FavoritesServer))));
        var fav = FavoritesWith("E7CF176E110C211B", Taskleiste.OrdnerProgramme86 + @"\Hersteller\app.exe");
        Check("Kennungen eines Windows-10-PCs (Firefox 32 Bit, Programm in x86)",
            Taskleiste.AppIds(fav).SequenceEqual(new[] { "E7CF176E110C211B", Taskleiste.OrdnerProgramme86 + @"\Hersteller\app.exe" }));
        Check("Kennungen: nichts gespeichert", Taskleiste.AppIds(null).Count == 0 && Taskleiste.AppIds(new byte[] { 0x20, 0, 0x1D, 0, 0xEF, 0xBE, 0, 0, 0, 0, 0, 0 }).Count == 0);
        var ersetzt = Taskleiste.ReplaceAppId(fav, "E7CF176E110C211B", "308046B0AF4A39CB");
        Check("Kennung ersetzt, Laenge gleich", ersetzt.Length == fav.Length && Taskleiste.AppIds(ersetzt)[0] == "308046B0AF4A39CB" && Taskleiste.AppIds(fav)[0] == "E7CF176E110C211B");
        bool abgelehnt = false;
        try { Taskleiste.ReplaceAppId(fav, "MSEdge", "Firefox"); } catch (ArgumentException) { abgelehnt = true; }
        Check("Kennung anderer Laenge: abgelehnt", abgelehnt);

        var mozilla = new Dictionary<string, string> { ["E7CF176E110C211B"] = "308046B0AF4A39CB", ["ABC"] = "308046B0AF4A39CB", ["308046B0AF4A39CB"] = "308046B0AF4A39CB" };
        var ordner = new Dictionary<string, string>
        {
            [Taskleiste.OrdnerProgramme86] = @"C:\Program Files (x86)",
            [Taskleiste.OrdnerProgramme64] = @"C:\Program Files",
        };
        var da = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\Neu64\app.exe", @"C:\Program Files (x86)\Alt86\app.exe" };
        string? NewId(string k) => Taskleiste.NewAppId(k, mozilla, ordner, p => da.Contains(p.Replace('/', '\\')));
        Check("Firefox 32 -> 64 Bit", NewId("E7CF176E110C211B") == "308046B0AF4A39CB");
        Check("Firefox: gleiche Installation oder andere Laenge - bleibt", NewId("308046B0AF4A39CB") is null && NewId("ABC") is null);
        Check("Programm aus x86, hier 64 Bit", NewId(Taskleiste.OrdnerProgramme86 + @"\Neu64\app.exe") == Taskleiste.OrdnerProgramme64 + @"\Neu64\app.exe");
        Check("Programm liegt hier wie dort - bleibt", NewId(Taskleiste.OrdnerProgramme86 + @"\Alt86\app.exe") is null);
        Check("Programm gibt es hier nicht - bleibt", NewId(Taskleiste.OrdnerProgramme86 + @"\Fehlt\app.exe") is null);
        Check("eigene Kennung, unbekannter Ordner - bleibt", NewId("Hersteller.Programm.Client.1") is null
            && NewId("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\SnippingTool.exe") is null && NewId("{kurz}\\x.exe") is null);
    }

    /// <summary>Mit echten Verknuepfungen - nur unter Windows (IShellLink).</summary>
    [SupportedOSPlatform("windows")]
    private static void ShortcutsRealCheck(string root)
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
            Verknuepfung.Create(Path.Combine(ordner, "Fuchs.lnk"), altesZiel, MozillaProfile.InstallHash(altOrdner));
            Verknuepfung.Create(Path.Combine(ordner, "Weg.lnk"), @"C:\DaSiTest-gibt-es-nicht\weg.exe");
            Verknuepfung.Create(Path.Combine(ordner, "Da.lnk"), Path.Combine(Environment.SystemDirectory, "cmd.exe"), "DaSi.Test.Da");

            var gelesen = Verknuepfung.Read(Path.Combine(ordner, "Fuchs.lnk"));
            Check("Verknuepfung lesen: Ziel, Ordner, Symbol, App-Kennung",
                gelesen is not null && gelesen.Ziel.Equals(altesZiel, StringComparison.OrdinalIgnoreCase)
                && gelesen.Arbeitsordner.Equals(altOrdner, StringComparison.OrdinalIgnoreCase)
                && gelesen.AppId == MozillaProfile.InstallHash(altOrdner), $"{gelesen}");
            Check("keine Verknuepfung: nichts zu lesen", Verknuepfung.Read(Path.Combine(ordner, "gibtsnicht.lnk")) is null);
            Check("keine Verknuepfung: nichts umzustellen", !Verknuepfung.SetTarget(Path.Combine(ordner, "gibtsnicht.lnk"), exe));

            var c = new CancellationTokenState();
            var log = new Protokoll();
            var d = new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = root, BackupPath = root };
            var ohneProgramm = d.CheckTaskbarTargets(ordner, new[] { "Da.lnk", "Fuchs.lnk", "Weg.lnk" });
            Check("fehlendes Programm zurueckgemeldet", ohneProgramm.SequenceEqual(new[] { "Weg.lnk" }), string.Join(", ", ohneProgramm));

            var fuchs = Verknuepfung.Read(Path.Combine(ordner, "Fuchs.lnk"));
            Check("Fuchs: auf den 64-Bit-Ordner umgestellt, samt Arbeitsordner und Symbol",
                fuchs is not null && fuchs.Ziel.Equals(exe, StringComparison.OrdinalIgnoreCase)
                && fuchs.Arbeitsordner.Equals(neuOrdner, StringComparison.OrdinalIgnoreCase)
                && fuchs.Symbol.Equals(exe, StringComparison.OrdinalIgnoreCase), $"{fuchs}");
            Check("Fuchs: App-Kennung der neuen Installation", fuchs?.AppId == MozillaProfile.InstallHash(MozillaProfile.RealPath(neuOrdner)), fuchs?.AppId);
            Check("Da: unveraendert", Verknuepfung.Read(Path.Combine(ordner, "Da.lnk"))?.AppId == "DaSi.Test.Da");
            Check("Protokoll: umgestellt und fehlend",
                log.Has("[INFO] Taskleiste: Fuchs zeigte auf") && log.Has("die Verknüpfung ist umgestellt")
                && log.Has("[WARNUNG] Taskleiste: Diese angehefteten Programme gibt es auf diesem PC (noch) nicht: Weg (C:\\DaSiTest-gibt-es-nicht\\weg.exe)")
                && !log.Has("Da ("), log.Text);

            // App-Kennungen in Favorites: 32-Bit-Firefox -> das Firefox hier,
            // Programm aus "Program Files (x86)" -> liegt hier in "Program Files".
            var kennungFuchs = Taskleiste.OrdnerProgramme86 + @"\DaSiTest-Fuchs\fuchs.exe";
            var fav = FavoritesWith("E7CF176E110C211B", kennungFuchs, "Microsoft.Windows.Explorer", "Hersteller.Programm.Client.1");
            var band = new List<DesignEintrag>
            {
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "Favorites", Typ = "Binary", Wert = Convert.ToBase64String(fav) },
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesResolve", Typ = "Binary", Wert = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("x E7CF176E110C211B y")) },
                new() { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = "FavoritesVersion", Typ = "DWord", Wert = "3" },
            };
            d.TestInstallationen = new List<string> { @"C:\Program Files\Mozilla Firefox" };
            log.Clear();
            var angepasst = d.AdjustAppIds(band);
            var kennungen = Taskleiste.AppIds(Taskleiste.Favorites(angepasst));
            Check("Kennungen: Firefox und Programm an diesen PC angepasst, die anderen bleiben",
                kennungen.SequenceEqual(new[] { "308046B0AF4A39CB", Taskleiste.OrdnerProgramme64 + @"\DaSiTest-Fuchs\fuchs.exe", "Microsoft.Windows.Explorer", "Hersteller.Programm.Client.1" }),
                string.Join(", ", kennungen));
            Check("Kennungen: auch in FavoritesResolve, die Sicherung selbst unveraendert",
                System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(angepasst[1].Wert)).Contains("308046B0AF4A39CB")
                && Taskleiste.AppIds(Taskleiste.Favorites(band))[0] == "E7CF176E110C211B" && angepasst[2].Wert == "3");
            Check("Kennungen: im Protokoll", log.Has("[INFO] Taskleiste: Kennungen an die Programme dieses PCs angepasst") && log.Has("E7CF176E110C211B -> 308046B0AF4A39CB"), log.Text);
            log.Clear();
            var ohne = d.AdjustAppIds(band.Skip(2).ToList());
            Check("Kennungen: ohne Favorites nichts zu tun", ohne.Count == 1 && !log.Has("Kennungen"), log.Text);
            d.TestInstallationen = new List<string>();
            Check("Kennungen: kein Firefox hier - Firefox-Kennung bleibt",
                Taskleiste.AppIds(Taskleiste.Favorites(d.AdjustAppIds(band)))[0] == "E7CF176E110C211B");
        }
        finally
        {
            try { Directory.Delete(neuOrdner, true); } catch { }
        }
    }
}
