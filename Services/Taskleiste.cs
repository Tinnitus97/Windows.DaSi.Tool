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

    public static string PinOrdner(string profil) => AppCatalog.Pfad(profil, PinRel);

    /// <summary>Desktop-Symbole\Taskleiste in der Sicherung.</summary>
    public static string SicherungsOrdner(string sicherung) => Path.Combine(sicherung, DesktopIcons.FolderName, Ordner);

    public static string Datei(string sicherung) => Path.Combine(SicherungsOrdner(sicherung), DateiName);

    /// <summary>Die Verknuepfungen in einem Ordner, nach Namen sortiert.</summary>
    public static List<string> Verknuepfungen(string ordner)
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
    public static string Namen(IEnumerable<string> verknuepfungen)
        => string.Join(", ", verknuepfungen.Select(Path.GetFileNameWithoutExtension));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Speichern(string sicherung, TaskleistenSicherung s)
    {
        Directory.CreateDirectory(SicherungsOrdner(sicherung));
        File.WriteAllText(Datei(sicherung), JsonSerializer.Serialize(s, Json));
    }

    /// <summary>null, wenn es keine (lesbare) Datei gibt oder nichts darin steht.</summary>
    public static TaskleistenSicherung? Laden(string sicherung)
    {
        try
        {
            var d = Datei(sicherung);
            if (!File.Exists(d)) return null;
            var s = JsonSerializer.Deserialize<TaskleistenSicherung>(File.ReadAllText(d));
            return s is not null && (s.Taskband.Count > 0 || s.Verknuepfungen.Count > 0) ? s : null;
        }
        catch { return null; }
    }

    public static bool Vorhanden(string sicherung) => Laden(sicherung) is not null;

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
    public static List<string> AngehefteteNamen(byte[]? favorites, IEnumerable<string> verknuepfungen)
    {
        if (favorites is null || favorites.Length < 2) return new List<string>();
        // Die Namen stehen nicht immer an geraden Stellen - beide Lesarten.
        var texte = new[] { System.Text.Encoding.Unicode.GetString(favorites), System.Text.Encoding.Unicode.GetString(favorites, 1, favorites.Length - 1) };
        return verknuepfungen.Where(n => n.Length > 0 && texte.Any(t => t.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
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
    public static string? NeuesZiel(string ziel, Func<string, bool> gibtEs, Func<string, string?> appPath,
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
