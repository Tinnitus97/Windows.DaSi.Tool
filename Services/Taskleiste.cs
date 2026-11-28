using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsDaSiTool.Services;

/// <summary>Die Datei Desktop-Symbole\Taskleiste\Taskleiste.json.</summary>
public sealed class TaskleistenSicherung
{
    public int Version { get; set; } = 1;
    public string Rechner { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>Die angehefteten Verknuepfungen (Dateinamen), wie sie im Ordner lagen.</summary>
    public List<string> Verknuepfungen { get; set; } = new();
    /// <summary>Die Werte des Schluessels Taskband - Reihenfolge und Verweise der Symbole.</summary>
    public List<DesignEintrag> Taskband { get; set; } = new();
}

/// <summary>
/// Die an die Taskleiste angehefteten Symbole.
///
/// Windows haelt sie an zwei Stellen: als Verknuepfungen im Ordner
/// "...\Quick Launch\User Pinned\TaskBar" des Profils und - mit Reihenfolge
/// und Verweisen, auch auf Store-Apps ohne Verknuepfung - im Registry-
/// Schluessel "Explorer\Taskband". Gesichert wird beides; beim
/// Zurueckschreiben muss der Explorer neu starten, denn er liest den
/// Schluessel nur beim Start.
///
/// Ohne Abhaengigkeiten, damit es sich pruefen laesst.
/// </summary>
public static class Taskleiste
{
    public const string Ordner = "Taskleiste";
    public const string DateiName = "Taskleiste.json";

    /// <summary>Unter HKEY_CURRENT_USER.</summary>
    public const string TaskbandSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    /// <summary>Der Ordner der angehefteten Verknuepfungen, relativ zum Profil.</summary>
    public const string PinRel = @"AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar";

    public static string PinFolder(string profil) => AppCatalog.PathIn(profil, PinRel);

    /// <summary>Desktop-Symbole\Taskleiste in der Sicherung.</summary>
    public static string BackupFolder(string sicherung) => Path.Combine(sicherung, DesktopIcons.FolderName, Ordner);

    public static string FilePath(string sicherung) => Path.Combine(BackupFolder(sicherung), DateiName);

    /// <summary>Die Verknuepfungen in einem Ordner, nach Namen sortiert.</summary>
    public static List<string> Shortcuts(string ordner)
    {
        try
        {
            if (!Directory.Exists(ordner)) return new List<string>();
            return Directory.GetFiles(ordner, "*.lnk")
                .Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>"Firefox.lnk", "Microsoft Edge.lnk" -> "Firefox, Microsoft Edge".</summary>
    public static string Names(IEnumerable<string> verknuepfungen)
        => string.Join(", ", verknuepfungen.Select(Path.GetFileNameWithoutExtension));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string sicherung, TaskleistenSicherung s)
    {
        Directory.CreateDirectory(BackupFolder(sicherung));
        File.WriteAllText(FilePath(sicherung), JsonSerializer.Serialize(s, Json));
    }

    /// <summary>null, wenn es keine (lesbare) Datei gibt oder nichts darin steht.</summary>
    public static TaskleistenSicherung? Load(string sicherung)
    {
        try
        {
            var d = FilePath(sicherung);
            if (!File.Exists(d)) return null;
            var s = JsonSerializer.Deserialize<TaskleistenSicherung>(File.ReadAllText(d));
            return s is not null && (s.Taskband.Count > 0 || s.Verknuepfungen.Count > 0) ? s : null;
        }
        catch { return null; }
    }

    public static bool Exists(string sicherung) => Load(sicherung) is not null;

    /// <summary>Der Wert Favorites aus der Sicherung (Binaerdaten), falls darin.</summary>
    public static byte[]? Favorites(IEnumerable<DesignEintrag> taskband)
    {
        var e = taskband.FirstOrDefault(t => t.Name == "Favorites" && t.Typ == "Binary");
        try { return e is null ? null : Convert.FromBase64String(e.Wert); }
        catch { return null; }
    }

    /// <summary>
    /// Welche dieser Verknuepfungen ("Firefox.lnk" ...) Taskband\Favorites
    /// nennt. Die Eintraege verweisen relativ auf den Ordner "User Pinned" und
    /// tragen den langen Dateinamen in UTF-16 - so laesst sich nach einem
    /// Explorer-Neustart vergleichen, was Windows behalten hat.
    /// </summary>
    public static List<string> PinnedNames(byte[]? favorites, IEnumerable<string> verknuepfungen)
    {
        if (favorites is null || favorites.Length < 2) return new List<string>();
        // Die Namen stehen nicht immer an geraden Stellen - beide Lesarten.
        var texte = new[] { System.Text.Encoding.Unicode.GetString(favorites), System.Text.Encoding.Unicode.GetString(favorites, 1, favorites.Length - 1) };
        return verknuepfungen.Where(n => n.Length > 0 && texte.Any(t => t.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ------------------------------------------------------------ App-Kennungen in Favorites

    /// <summary>Bekannte Ordner, aus denen Windows die Kennung eines Programms ohne eigene bildet.</summary>
    public const string OrdnerProgramme86 = "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}";
    public const string OrdnerProgramme64 = "{6D809377-6AF0-444B-8957-A3773F02200E}";
    public const string OrdnerProgramme = "{905E63B6-C1BF-494E-B29C-65B732D3D21A}";

    /// <summary>
    /// Die App-Kennungen (AppUserModelID) in Taskband\Favorites - je Eintrag
    /// eine, im Block BEEF001D. Ueber sie ordnet die Taskleiste ein angeheftetes
    /// Symbol einem installierten Programm zu: "Microsoft.Windows.Explorer",
    /// bei Firefox der Schluessel seines Ordners ("E7CF176E110C211B"), bei
    /// Programmen ohne eigene der Pfad ab einem bekannten Ordner
    /// ("{7C5A40EF-...}\Hersteller\programm.exe").
    /// </summary>
    public static List<string> AppIds(byte[]? favorites)
    {
        var erg = new List<string>();
        if (favorites is null) return erg;
        for (int i = 0; i + 12 <= favorites.Length; i++)
        {
            // Block: Laenge (2), Fassung (2), Kennzeichen 0xBEEF001D (4), 2 Bytes, Text (UTF-16, Null am Ende)
            if (favorites[i + 4] != 0x1D || favorites[i + 5] != 0x00 || favorites[i + 6] != 0xEF || favorites[i + 7] != 0xBE) continue;
            var laenge = favorites[i] | (favorites[i + 1] << 8);
            if (laenge < 12 || i + laenge > favorites.Length) continue;
            var text = System.Text.Encoding.Unicode.GetString(favorites, i + 10, laenge - 10);
            var ende = text.IndexOf('\0');
            if (ende > 0) erg.Add(text[..ende]);
        }
        return erg;
    }

    /// <summary>Ein angehefteter Eintrag aus Favorites: seine Verknuepfung (falls bekannt) und App-Kennung.</summary>
    public sealed record Eintrag(string? Verknuepfung, string? Kennung);

    /// <summary>
    /// Die Eintraege von Taskband\Favorites: je ein Byte 0, die Laenge (4 Bytes)
    /// und die Daten; am Ende 0xFF. Verknuepfung: welche der genannten Dateien
    /// der Eintrag meint (Store-Apps haben keine).
    /// </summary>
    public static List<Eintrag> Entries(byte[]? favorites, IEnumerable<string> verknuepfungen)
    {
        var erg = new List<Eintrag>();
        if (favorites is null) return erg;
        var namen = verknuepfungen.ToList();
        int pos = 0;
        while (pos + 5 <= favorites.Length && favorites[pos] == 0x00)
        {
            var laenge = BitConverter.ToUInt32(favorites, pos + 1);
            if (laenge == 0 || laenge > favorites.Length - pos - 5) break;
            var daten = favorites.AsSpan(pos + 5, (int)laenge).ToArray();
            erg.Add(new Eintrag(PinnedNames(daten, namen).FirstOrDefault(), AppIds(daten).FirstOrDefault()));
            pos += 5 + (int)laenge;
        }
        return erg;
    }

    /// <summary>
    /// Ersetzt eine App-Kennung durch eine gleich lange - ueberall in den
    /// Binaerdaten (UTF-16). Gleich lang, damit kein Laengenfeld der Eintraege
    /// angepasst werden muss.
    /// </summary>
    public static byte[] ReplaceAppId(byte[] daten, string alt, string neu)
    {
        if (alt.Length != neu.Length) throw new ArgumentException("Die Kennungen muessen gleich lang sein.");
        var a = System.Text.Encoding.Unicode.GetBytes(alt);
        var n = System.Text.Encoding.Unicode.GetBytes(neu);
        var erg = (byte[])daten.Clone();
        for (int i = 0; i + a.Length <= erg.Length; i++)
            if (erg.AsSpan(i, a.Length).SequenceEqual(a)) { n.CopyTo(erg, i); i += a.Length - 1; }
        return erg;
    }

    /// <summary>
    /// Die Kennung, unter der das Programm auf diesem PC laeuft - oder null,
    /// wenn sie bleibt (oder keine gleich lange passt).
    /// </summary>
    /// <param name="mozilla">Firefox/Thunderbird: Kennung (Schluessel) der Installation auf dem alten PC -> auf diesem.</param>
    /// <param name="ordner">Bekannter Ordner (z. B. <see cref="OrdnerProgramme86"/>) -> sein Pfad auf diesem PC.</param>
    /// <param name="gibtEs">Liegt dort eine Datei?</param>
    public static string? NewAppId(string kennung, IReadOnlyDictionary<string, string> mozilla,
                                      IReadOnlyDictionary<string, string> ordner, Func<string, bool> gibtEs)
    {
        if (mozilla.TryGetValue(kennung, out var m))
            return !m.Equals(kennung, StringComparison.OrdinalIgnoreCase) && m.Length == kennung.Length ? m : null;

        // "{Ordner}\rest": liegt das Programm hier in einem anderen Programme-Ordner?
        var trenner = kennung.IndexOf('\\');
        if (trenner != 38 || !kennung.StartsWith('{')) return null;
        var guid = kennung[..38];
        var rest = kennung[39..];
        if (!ordner.Keys.Contains(guid, StringComparer.OrdinalIgnoreCase)) return null;
        var hier = ordner.First(o => o.Key.Equals(guid, StringComparison.OrdinalIgnoreCase)).Value;
        if (gibtEs(Path.Combine(hier, rest))) return null;
        foreach (var (anderer, pfad) in ordner)
            if (!anderer.Equals(guid, StringComparison.OrdinalIgnoreCase) && gibtEs(Path.Combine(pfad, rest)))
                return anderer + "\\" + rest;
        return null;
    }

    /// <summary>
    /// Wo ein Programm hier liegt, wenn es nicht mehr dort ist, wohin die
    /// Verknuepfung vom alten PC zeigt: im anderen Programme-Ordner (32 statt
    /// 64 Bit oder umgekehrt - wie Firefox, das winget hier als 64-Bit-Fassung
    /// installiert) oder dort, wo Windows es unter "App Paths" fuehrt.
    /// </summary>
    /// <param name="gibtEs">Liegt dort eine Datei?</param>
    /// <param name="appPath">Der Ort laut "App Paths" zu einem Dateinamen (z. B. "firefox.exe").</param>
    /// <param name="programme">Der Programme-Ordner dieses PCs (64 Bit).</param>
    /// <param name="programme86">Der Programme-Ordner fuer 32-Bit-Programme.</param>
    /// <returns>null, wenn das Ziel da ist oder sich nirgends findet.</returns>
    public static string? NewTarget(string ziel, Func<string, bool> gibtEs, Func<string, string?> appPath,
                                    string programme, string programme86)
    {
        if (string.IsNullOrWhiteSpace(ziel) || gibtEs(ziel)) return null;
        // Der Teil hinter dem Programme-Ordner ("Mozilla Firefox\firefox.exe") -
        // gesucht in jedem Programme-Ordner dieses PCs.
        var wurzeln = new[] { programme86, programme, @"C:\Program Files (x86)", @"C:\Program Files" }
            .Where(w => w.Length > 0).Select(w => w.TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kandidaten = new List<string>();
        foreach (var w in wurzeln.Where(w => ziel.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            foreach (var nach in wurzeln)
                kandidaten.Add(nach + ziel[w.Length..]);
        var datei = ziel[(ziel.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        if (appPath(datei) is { Length: > 0 } laut) kandidaten.Add(laut);
        return kandidaten.FirstOrDefault(k => !k.Equals(ziel, StringComparison.OrdinalIgnoreCase) && gibtEs(k));
    }
}
