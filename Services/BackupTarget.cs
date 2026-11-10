using System;
using System.Globalization;
using System.IO;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Der eigene Sicherungsordner: Wer als Ziel nur "G:\" angibt, bekommt
/// darunter einen Ordner je Sicherung - "G:\DASI_DESKTOP-7K4Q2M_10_11_2026_18_45".
///
/// So liegen mehrere Rechner und mehrere Staende sauber nebeneinander, statt
/// sich im Wurzelverzeichnis gegenseitig zu ueberschreiben: Das Profil wird
/// mit /MIR gespiegelt, eine zweite Sicherung in denselben Ordner loeschte
/// alles, was der erste Rechner dort hinterlassen hat.
///
/// Ohne Abhaengigkeiten, damit sich die Regel pruefen laesst.
/// </summary>
public static class BackupTarget
{
    /// <summary>Anfang jedes Ordners, den das Tool selbst anlegt.</summary>
    public const string Prefix = "DASI_";

    /// <summary>
    /// "DASI_&lt;Rechnername&gt;_TT_MM_JJJJ_HH_MM". Zeichen, die in einem
    /// Ordnernamen nicht erlaubt sind, werden zu "_".
    /// </summary>
    public static string FolderName(string machineName, DateTime when)
    {
        var pc = string.IsNullOrWhiteSpace(machineName) ? "PC" : machineName.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) pc = pc.Replace(ch, '_');
        // Zusaetzlich die unter Windows verbotenen Zeichen - Path liefert unter
        // Linux nur '/' und '\0', die Regel soll aber ueberall gleich wirken.
        foreach (var ch in "\\/:*?\"<>|") pc = pc.Replace(ch, '_');
        return Prefix + pc + "_" + when.ToString("dd_MM_yyyy_HH_mm", CultureInfo.InvariantCulture);
    }

    /// <summary>true, wenn der Ordner selbst schon ein DASI-Ordner ist.</summary>
    public static bool IsOwnFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = path.TrimEnd('\\', '/');
        var schnitt = name.LastIndexOfAny(new[] { '\\', '/' });
        if (schnitt >= 0) name = name[(schnitt + 1)..];
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Das tatsaechliche Ziel einer Sicherung.
    ///
    /// Ist das Gewaehlte selbst schon ein DASI-Ordner - etwa weil er gerade als
    /// Quelle zum Wiederherstellen diente -, kommt der neue Ordner DANEBEN, nicht
    /// hinein und nicht an seine Stelle: Das Profil wird mit /MIR gespiegelt,
    /// in eine aeltere Sicherung geschrieben, loeschte es dort alles, was dieser
    /// Rechner nicht hat. Wer wirklich in einen bestimmten Ordner sichern will,
    /// schaltet "Eigener Ordner" ab.
    /// </summary>
    /// <param name="basePath">Was der Benutzer gewaehlt hat, z. B. "G:\".</param>
    /// <param name="enabled">Schalter "Eigener Ordner".</param>
    public static string Resolve(string basePath, bool enabled, string machineName, DateTime when)
    {
        if (!enabled || string.IsNullOrWhiteSpace(basePath)) return basePath;
        var basis = basePath;
        if (IsOwnFolder(basePath))
            basis = Path.GetDirectoryName(basePath.TrimEnd('\\', '/')) ?? basePath;
        return Path.Combine(basis, FolderName(machineName, when));
    }
}
