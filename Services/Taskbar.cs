using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsDaSiTool.Services;

/// <summary>The file Desktop-Symbole\Taskleiste\Taskleiste.json.</summary>
public sealed class TaskleistenSicherung
{
    public int Version { get; set; } = 1;
    public string Rechner { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>The pinned shortcuts (file names), as they were in the folder.</summary>
    public List<string> Verknuepfungen { get; set; } = new();
    /// <summary>The values of the Taskband key - order and references of the icons.</summary>
    public List<DesignEintrag> Taskband { get; set; } = new();
}

/// <summary>
/// The icons pinned to the taskbar.
///
/// Windows keeps them in two places: as shortcuts in the profile folder
/// "...\Quick Launch\User Pinned\TaskBar" and - with order and references,
/// also to Store apps without a shortcut - in the registry key
/// "Explorer\Taskband". Both are backed up; on restore Explorer has to
/// restart, because it reads the key only at startup.
///
/// No dependencies, so it can be tested.
/// </summary>
public static class Taskbar
{
    public const string Ordner = "Taskleiste";
    public const string DateiName = "Taskleiste.json";

    /// <summary>Below HKEY_CURRENT_USER.</summary>
    public const string TaskbandSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    /// <summary>The folder of the pinned shortcuts, relative to the profile.</summary>
    public const string PinRel = @"AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar";

    public static string PinFolder(string profil) => AppCatalog.PathIn(profil, PinRel);

    /// <summary>Desktop-Symbole\Taskleiste in the backup.</summary>
    public static string BackupFolder(string sicherung) => Path.Combine(sicherung, DesktopIcons.FolderName, Ordner);

    public static string FilePath(string sicherung) => Path.Combine(BackupFolder(sicherung), DateiName);

    /// <summary>The shortcuts in a folder, sorted by name.</summary>
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

    /// <summary>null if there is no (readable) file or it is empty.</summary>
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

    /// <summary>The Favorites value from the backup (binary data), if present.</summary>
    public static byte[]? Favorites(IEnumerable<DesignEintrag> taskband)
    {
        var e = taskband.FirstOrDefault(t => t.Name == "Favorites" && t.Typ == "Binary");
        try { return e is null ? null : Convert.FromBase64String(e.Wert); }
        catch { return null; }
    }

    /// <summary>
    /// Which of these shortcuts ("Firefox.lnk" ...) Taskband\Favorites names.
    /// The entries refer to the "User Pinned" folder relatively and carry the
    /// long file name in UTF-16 - so after an Explorer restart one can compare
    /// what Windows kept.
    /// </summary>
    public static List<string> PinnedNames(byte[]? favorites, IEnumerable<string> verknuepfungen)
    {
        if (favorites is null || favorites.Length < 2) return new List<string>();
        // The names are not always at even offsets - try both alignments.
        var texte = new[] { System.Text.Encoding.Unicode.GetString(favorites), System.Text.Encoding.Unicode.GetString(favorites, 1, favorites.Length - 1) };
        return verknuepfungen.Where(n => n.Length > 0 && texte.Any(t => t.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ------------------------------------------------------------ App IDs in Favorites

    /// <summary>Known folders Windows builds the ID of a program without its own ID from.</summary>
    public const string OrdnerProgramme86 = "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}";
    public const string OrdnerProgramme64 = "{6D809377-6AF0-444B-8957-A3773F02200E}";
    public const string OrdnerProgramme = "{905E63B6-C1BF-494E-B29C-65B732D3D21A}";

    /// <summary>
    /// The app IDs (AppUserModelID) in Taskband\Favorites - one per entry, in
    /// block BEEF001D. The taskbar uses them to map a pinned icon to an
    /// installed program: "Microsoft.Windows.Explorer", for Firefox the hash of
    /// its folder ("E7CF176E110C211B"), for programs without an ID of their own
    /// the path from a known folder on ("{7C5A40EF-...}\Vendor\program.exe").
    /// </summary>
    public static List<string> AppIds(byte[]? favorites)
    {
        var erg = new List<string>();
        if (favorites is null) return erg;
        for (int i = 0; i + 12 <= favorites.Length; i++)
        {
            // Block: length (2), version (2), signature 0xBEEF001D (4), 2 bytes, text (UTF-16, null-terminated)
            if (favorites[i + 4] != 0x1D || favorites[i + 5] != 0x00 || favorites[i + 6] != 0xEF || favorites[i + 7] != 0xBE) continue;
            var laenge = favorites[i] | (favorites[i + 1] << 8);
            if (laenge < 12 || i + laenge > favorites.Length) continue;
            var text = System.Text.Encoding.Unicode.GetString(favorites, i + 10, laenge - 10);
            var ende = text.IndexOf('\0');
            if (ende > 0) erg.Add(text[..ende]);
        }
        return erg;
    }

    /// <summary>A pinned entry from Favorites: its shortcut (if known) and app ID.</summary>
    public sealed record Eintrag(string? Verknuepfung, string? Kennung);

    /// <summary>
    /// The entries of Taskband\Favorites: each a 0 byte, the length (4 bytes)
    /// and the data; 0xFF at the end. Shortcut: which of the named files the
    /// entry refers to (Store apps have none).
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
    /// Replaces an app ID with one of the same length - everywhere in the
    /// binary data (UTF-16). Same length, so no length field of the entries has
    /// to be adjusted.
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
    /// The ID the program runs under on this PC - or null if it stays the same
    /// (or no ID of the same length fits).
    /// </summary>
    /// <param name="mozilla">Firefox/Thunderbird: ID (hash) of the installation on the old PC -> on this one.</param>
    /// <param name="ordner">Known folder (e.g. <see cref="OrdnerProgramme86"/>) -> its path on this PC.</param>
    /// <param name="gibtEs">Is there a file at that path?</param>
    public static string? NewAppId(string kennung, IReadOnlyDictionary<string, string> mozilla,
                                      IReadOnlyDictionary<string, string> ordner, Func<string, bool> gibtEs)
    {
        if (mozilla.TryGetValue(kennung, out var m))
            return !m.Equals(kennung, StringComparison.OrdinalIgnoreCase) && m.Length == kennung.Length ? m : null;

        // "{folder}\rest": is the program in another Program Files folder here?
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
    /// Where a program is here if it is no longer where the shortcut from the
    /// old PC points: in the other Program Files folder (32 instead of 64 bit or
    /// vice versa - like Firefox, which winget installs here as the 64-bit
    /// edition) or where Windows lists it under "App Paths".
    /// </summary>
    /// <param name="gibtEs">Is there a file at that path?</param>
    /// <param name="appPath">The location according to "App Paths" for a file name (e.g. "firefox.exe").</param>
    /// <param name="programme">This PC's Program Files folder (64 bit).</param>
    /// <param name="programme86">The Program Files folder for 32-bit programs.</param>
    /// <returns>null if the target exists or cannot be found anywhere.</returns>
    public static string? NewTarget(string ziel, Func<string, bool> gibtEs, Func<string, string?> appPath,
                                    string programme, string programme86)
    {
        if (string.IsNullOrWhiteSpace(ziel) || gibtEs(ziel)) return null;
        // The part after the Program Files folder ("Mozilla Firefox\firefox.exe") -
        // looked for in every Program Files folder of this PC.
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
