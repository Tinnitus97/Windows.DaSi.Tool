using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WindowsDaSiTool.Services;

/// <summary>
/// "Ordner unter C:\": eigene Ordner direkt auf dem Systemlaufwerk - etwa
/// C:\Daten, C:\Scans oder der Ordner einer Branchensoftware. Was davon
/// gesichert wird, waehlt der Benutzer beim Start aus einer Liste (wie bei
/// der Winget-Auswahl). Die Ordner, die jede Windows-Installation dort anlegt,
/// sind in der Liste ausgeblendet, solange der Haken dafuer steht.
///
/// Ohne Abhaengigkeiten, damit sich die Regeln pruefen lassen.
/// </summary>
public static class LaufwerksOrdner
{
    /// <summary>Unterordner in der Sicherung - darin je ein Ordner pro gesichertem Ordner.</summary>
    public const string BackupOrdner = "C-Ordner";

    /// <summary>Das Systemlaufwerk ("C:\"). Fuer die Pruefungen austauschbar.</summary>
    public static string Wurzel { get; set; } = StandardWurzel();

    public static string StandardWurzel()
        => (Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } d ? d.TrimEnd('\\', '/') : "C:") + @"\";

    /// <summary>
    /// Was eine Windows-Installation unter C:\ anlegt (dazu alles, was mit "$"
    /// beginnt, und versteckte Systemordner). "Windows.old" bleibt nach einem
    /// Upgrade liegen, "inetpub" kommt mit den Internetinformationsdiensten.
    /// </summary>
    public static readonly IReadOnlySet<string> WindowsOrdner = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "Users",
        "Documents and Settings", "PerfLogs", "Recovery", "System Volume Information", "Boot",
        "Config.Msi", "MSOCache", "OneDriveTemp", "inetpub", "ESD",
    };

    /// <summary>Ein Ordner der Liste.</summary>
    /// <param name="Windows">Gehoert zur Windows-Installation - ausgeblendet, solange der Haken steht.</param>
    public sealed record Eintrag(string Name, string Pfad, bool Windows);

    /// <summary>Legt Windows diesen Ordner selbst an?</summary>
    public static bool IstWindowsOrdner(string name, FileAttributes attribute = 0)
        => WindowsOrdner.Contains(name)
           || name.StartsWith('$')
           || (attribute.HasFlag(FileAttributes.Hidden) && attribute.HasFlag(FileAttributes.System));

    /// <summary>
    /// Die Ordner direkt unter <paramref name="wurzel"/>, nach Namen. Nie dabei:
    /// Verknuepfungen (etwa "Documents and Settings", das nur auf Users zeigt)
    /// und der Ordner, in dem die Sicherung selbst liegt - sie saehe sich sonst
    /// selbst.
    /// </summary>
    public static List<Eintrag> Auflisten(string wurzel, string? sicherungsZiel)
    {
        var liste = new List<Eintrag>();
        try
        {
            foreach (var d in new DirectoryInfo(wurzel).EnumerateDirectories())
            {
                try
                {
                    if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (!string.IsNullOrWhiteSpace(sicherungsZiel) && Enthaelt(d.FullName, sicherungsZiel)) continue;
                    liste.Add(new Eintrag(d.Name, d.FullName, IstWindowsOrdner(d.Name, d.Attributes)));
                }
                catch { /* nicht lesbar - auslassen */ }
            }
        }
        catch { /* Laufwerk nicht lesbar - dann eben keine */ }
        return liste.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Die Ordner, die in der Sicherung liegen.</summary>
    public static List<Eintrag> InDerSicherung(string? sicherung)
    {
        var liste = new List<Eintrag>();
        try
        {
            if (string.IsNullOrWhiteSpace(sicherung)) return liste;
            var ort = Path.Combine(sicherung, BackupOrdner);
            if (!Directory.Exists(ort)) return liste;
            foreach (var d in new DirectoryInfo(ort).EnumerateDirectories())
                liste.Add(new Eintrag(d.Name, d.FullName, IstWindowsOrdner(d.Name)));
        }
        catch { /* nicht lesbar - dann eben keine */ }
        return liste.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Liegen Ordner von C:\ in der Sicherung?</summary>
    public static bool Vorhanden(string? sicherung) => InDerSicherung(sicherung).Count > 0;

    /// <summary>Ist <paramref name="pfad"/> der Ordner selbst oder liegt er darin?</summary>
    public static bool Enthaelt(string ordner, string pfad)
    {
        try
        {
            var o = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ordner));
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pfad));
            return p.Equals(o, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(o + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Wie gross ist der Ordner? Verknuepfungen wird nicht gefolgt, nicht
    /// Lesbares zaehlt nicht. -1, wenn abgebrochen.
    /// </summary>
    public static long Groesse(string ordner, CancellationToken stop = default)
    {
        long summe = 0;
        var optionen = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var f in new DirectoryInfo(ordner).EnumerateFiles("*", optionen))
            {
                if (stop.IsCancellationRequested) return -1;
                summe += f.Length;
            }
        }
        catch { /* bis hierher */ }
        return stop.IsCancellationRequested ? -1 : summe;
    }
}
