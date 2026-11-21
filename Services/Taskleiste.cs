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
}
