using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WindowsDaSiTool.Services;

/// <summary>
/// "Folders under C:\": the user's own folders directly on the system drive
/// - e.g. C:\Daten, C:\Scans or the folder of a line-of-business
/// application. Which of them are backed up, the user picks from a list
/// when checking the item (like the winget selection). The folders every
/// Windows installation creates there are hidden in the list as long as
/// the box for that is checked.
///
/// No dependencies, so the rules can be tested.
/// </summary>
public static class LaufwerksOrdner
{
    /// <summary>Subfolder in the backup - holding one folder per backed-up folder.</summary>
    public const string BackupOrdner = "C-Ordner";

    /// <summary>The system drive ("C:\"). Replaceable for the tests.</summary>
    public static string Wurzel { get; set; } = DefaultRoot();

    public static string DefaultRoot()
        => (Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } d ? d.TrimEnd('\\', '/') : "C:") + @"\";

    /// <summary>
    /// What a Windows installation creates under C:\ (plus everything starting
    /// with "$", and hidden system folders). "Windows.old" stays behind after an
    /// upgrade, "inetpub" comes with Internet Information Services, the Xbox
    /// app creates "XboxGames" for its games (managed by Gaming Services - a
    /// copy of it is no use), and the Update Assistant creates
    /// "Windows10Upgrade".
    /// </summary>
    public static readonly IReadOnlySet<string> WindowsOrdner = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "Users",
        "Documents and Settings", "PerfLogs", "Recovery", "System Volume Information", "Boot",
        "Config.Msi", "MSOCache", "OneDriveTemp", "inetpub", "ESD", "XboxGames", "Windows10Upgrade",
    };

    /// <summary>A folder in the list.</summary>
    /// <param name="Windows">Belongs to the Windows installation - hidden as long as the box is checked.</param>
    public sealed record Eintrag(string Name, string Pfad, bool Windows);

    /// <summary>Does Windows create this folder itself?</summary>
    public static bool IsWindowsFolder(string name, FileAttributes attribute = 0)
        => WindowsOrdner.Contains(name)
           || name.StartsWith('$')
           || (attribute.HasFlag(FileAttributes.Hidden) && attribute.HasFlag(FileAttributes.System));

    /// <summary>
    /// The folders directly under <paramref name="wurzel"/>, sorted by name.
    /// Never included: junctions (such as "Documents and Settings", which only
    /// points to Users) and the folder that holds the backup itself - the
    /// backup would otherwise copy itself.
    /// </summary>
    public static List<Eintrag> ListFolders(string wurzel, string? sicherungsZiel)
    {
        var liste = new List<Eintrag>();
        try
        {
            foreach (var d in new DirectoryInfo(wurzel).EnumerateDirectories())
            {
                try
                {
                    if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (!string.IsNullOrWhiteSpace(sicherungsZiel) && Contains(d.FullName, sicherungsZiel)) continue;
                    liste.Add(new Eintrag(d.Name, d.FullName, IsWindowsFolder(d.Name, d.Attributes)));
                }
                catch { /* not readable - skip it */ }
            }
        }
        catch { /* drive not readable - then there are none */ }
        return liste.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folders contained in the backup.</summary>
    public static List<Eintrag> InBackup(string? sicherung)
    {
        var liste = new List<Eintrag>();
        try
        {
            if (string.IsNullOrWhiteSpace(sicherung)) return liste;
            var ort = Path.Combine(sicherung, BackupOrdner);
            if (!Directory.Exists(ort)) return liste;
            foreach (var d in new DirectoryInfo(ort).EnumerateDirectories())
                liste.Add(new Eintrag(d.Name, d.FullName, IsWindowsFolder(d.Name)));
        }
        catch { /* not readable - then there are none */ }
        return liste.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Are there folders from C:\ in the backup?</summary>
    public static bool Exists(string? sicherung) => InBackup(sicherung).Count > 0;

    /// <summary>Is <paramref name="pfad"/> the folder itself or inside it?</summary>
    public static bool Contains(string ordner, string pfad)
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
    /// How large is the folder? Junctions are not followed, unreadable entries
    /// do not count. -1 if cancelled.
    /// </summary>
    public static long SizeOf(string ordner, CancellationToken stop = default)
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
        catch { /* up to here */ }
        return stop.IsCancellationRequested ? -1 : summe;
    }
}
