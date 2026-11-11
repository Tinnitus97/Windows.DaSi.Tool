using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Der eigene Sicherungsordner: Wer als Ziel nur "G:\" angibt, bekommt
/// darunter einen Ordner je Rechner und Tag - "G:\DASI_DESKTOP-7K4Q2M_10_11_2026".
///
/// So liegen mehrere Rechner und mehrere Staende sauber nebeneinander, statt
/// sich im Wurzelverzeichnis gegenseitig zu ueberschreiben: Das Profil wird
/// mit /MIR gespiegelt, eine zweite Sicherung in denselben Ordner loeschte
/// alles, was der erste Rechner dort hinterlassen hat.
///
/// Ist das gewaehlte Ziel selbst schon eine Sicherung, wird sie aktualisiert -
/// es entsteht kein weiterer Ordner. Ebenso bei einer zweiten Sicherung am
/// selben Tag: Der Ordnername ist derselbe.
///
/// Ohne Abhaengigkeiten an die Oberflaeche, damit sich die Regel pruefen laesst.
/// </summary>
public static class BackupTarget
{
    /// <summary>Anfang jedes Ordners, den das Tool selbst anlegt.</summary>
    public const string Prefix = "DASI_";

    /// <summary>
    /// "DASI_&lt;Rechnername&gt;_TT_MM_JJJJ". Zeichen, die in einem
    /// Ordnernamen nicht erlaubt sind, werden zu "_".
    /// </summary>
    public static string FolderName(string machineName, DateTime when)
        => Prefix + SafeMachineName(machineName) + "_" + when.ToString("dd_MM_yyyy", CultureInfo.InvariantCulture);

    /// <summary>Der Rechnername, wie er im Ordnernamen steht.</summary>
    public static string SafeMachineName(string machineName)
    {
        var pc = string.IsNullOrWhiteSpace(machineName) ? "PC" : machineName.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) pc = pc.Replace(ch, '_');
        // Zusaetzlich die unter Windows verbotenen Zeichen - Path liefert unter
        // Linux nur '/' und '\0', die Regel soll aber ueberall gleich wirken.
        foreach (var ch in "\\/:*?\"<>|") pc = pc.Replace(ch, '_');
        return pc;
    }

    /// <summary>
    /// Der Rechner, von dem ein DASI-Ordner stammt: "DASI_ALTPC_01_10_2026" -> "ALTPC".
    /// Auch fuer Ordner aelterer Fassungen mit Uhrzeit ("..._2026_18_45").
    /// null, wenn der Name nicht diesem Muster folgt.
    /// </summary>
    public static string? MachineOf(string path)
    {
        var m = Regex.Match(LastSegment(path), @"^DASI_(.+?)_\d{2}_\d{2}_\d{4}(_\d{2}_\d{2})?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>true, wenn der Ordner selbst schon ein DASI-Ordner ist.</summary>
    public static bool IsOwnFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return LastSegment(path).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ist der Ordner schon eine Sicherung - am Namen (DASI_...) oder am Inhalt
    /// (Benutzerprofil, Browserprofile, Programmliste ... oder ein Protokoll)?
    /// </summary>
    public static bool IsBackupFolder(string path)
        => IsOwnFolder(path) || AppCatalog.IstSicherung(path);

    /// <summary>
    /// Das tatsaechliche Ziel einer Sicherung.
    ///
    /// Ist das Gewaehlte schon eine Sicherung, geht die neue Sicherung genau
    /// dort hinein und aktualisiert sie - kein neuer Ordner darin und keiner
    /// daneben. Sonst entsteht darunter "DASI_&lt;Rechner&gt;_&lt;Datum&gt;"; gibt es
    /// den von heute schon, wird er ebenso aktualisiert.
    /// </summary>
    /// <param name="basePath">Was der Benutzer gewaehlt hat, z. B. "G:\".</param>
    /// <param name="enabled">Schalter "Eigener Ordner".</param>
    public static string Resolve(string basePath, bool enabled, string machineName, DateTime when)
    {
        if (!enabled || string.IsNullOrWhiteSpace(basePath)) return basePath;
        if (IsBackupFolder(basePath)) return basePath;
        return Path.Combine(basePath, FolderName(machineName, when));
    }

    private static string LastSegment(string path)
    {
        var name = (path ?? "").TrimEnd('\\', '/');
        var schnitt = name.LastIndexOfAny(new[] { '\\', '/' });
        return schnitt >= 0 ? name[(schnitt + 1)..] : name;
    }
}
