using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Was winget mit seinen Rueckgabewerten meint, und wie seine Ausgabe lesbar
/// wird. Ohne Abhaengigkeiten, damit es sich pruefen laesst.
///
/// Die Werte stehen in der Doku von winget (returnCodes.md) als HRESULT, z. B.
/// 0x8A150009; als Prozess-Rueckgabewert kommen sie vorzeichenbehaftet an
/// (-1978335223).
/// </summary>
public static class WingetFehler
{
    public const int Ok = 0;

    /// <summary>0x8A150009: "Cannot write to index; it is a higher schema version".</summary>
    public const int IndexZuNeu = unchecked((int)0x8A150009);

    /// <summary>0x8A15000A: Der Index ist beschaedigt.</summary>
    public const int IndexBeschaedigt = unchecked((int)0x8A15000A);

    /// <summary>0x8A15000B: Die eingerichteten Quellen sind beschaedigt.</summary>
    public const int QuellenBeschaedigt = unchecked((int)0x8A15000B);

    /// <summary>0x8A15000F: Daten, die die Quelle braucht, fehlen.</summary>
    public const int QuellDatenFehlen = unchecked((int)0x8A15000F);

    /// <summary>0x8A150014: Kein Paket passt zur Angabe.</summary>
    public const int NichtGefunden = unchecked((int)0x8A150014);

    /// <summary>0x8A15002B: Keine passende Aktualisierung - schon die neueste Fassung.</summary>
    public const int SchonAktuell = unchecked((int)0x8A15002B);

    /// <summary>0x8A150061: Das Paket ist schon installiert.</summary>
    public const int SchonInstalliert = unchecked((int)0x8A150061);

    /// <summary>
    /// true, wenn nicht das Paket schuld ist, sondern winget selbst: Seine
    /// Paketquelle oder deren Datenbank ist kaputt oder stammt von einer
    /// neueren winget-Fassung. Dann scheitert JEDES weitere Paket genauso -
    /// erst reparieren, dann weiter.
    /// </summary>
    public static bool SourceBroken(int code)
        => code is IndexZuNeu or IndexBeschaedigt or QuellenBeschaedigt or QuellDatenFehlen;

    /// <summary>Auch an der Ausgabe erkennbar - falls ein Rueckgabewert fehlt oder verfaelscht ist.</summary>
    public static bool SourceBroken(int code, string? ausgabe)
    {
        if (SourceBroken(code)) return true;
        if (string.IsNullOrEmpty(ausgabe)) return false;
        return ausgabe.Contains("0x8a150009", StringComparison.OrdinalIgnoreCase)
               || ausgabe.Contains("0x8a15000a", StringComparison.OrdinalIgnoreCase)
               || ausgabe.Contains("0x8a15000b", StringComparison.OrdinalIgnoreCase)
               || ausgabe.Contains("0x8a15000f", StringComparison.OrdinalIgnoreCase)
               || ausgabe.Contains("higher schema version", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"0x8A150009" - so steht der Wert in der Doku und in Suchmaschinen.</summary>
    public static string Hex(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Ein Satz, was der Wert bedeutet - nur fuer die Werte, bei denen der
    /// Benutzer daraus etwas machen kann. Sonst null.
    /// </summary>
    public static string? Explanation(int code, Func<string, string, string> tr) => code switch
    {
        IndexZuNeu => tr("Die Paketdatenbank von winget stammt von einer neueren winget-Fassung als der installierten.",
                         "winget's package database comes from a newer winget version than the installed one."),
        IndexBeschaedigt => tr("Die Paketdatenbank von winget ist beschädigt.",
                               "winget's package database is corrupt."),
        QuellenBeschaedigt => tr("Die eingerichteten Paketquellen von winget sind beschädigt.",
                                 "winget's configured package sources are corrupt."),
        QuellDatenFehlen => tr("Der Paketquelle von winget fehlen Daten.",
                               "winget's package source is missing data."),
        _ => null,
    };

    // ------------------------------------------------------------ Ausgabe lesbar machen

    // Der Fortschrittskreisel von winget: einzelne Zeichen, die per "\r" bzw.
    // Rueckschritt ueberschrieben werden.
    private static readonly Regex NurKreisel = new(@"^[\s\-\\|/]*$", RegexOptions.CultureInvariant);

    // Fortschrittsbalken: "██████▒▒▒  1.2 MB / 3.4 MB" bzw. Prozentangaben.
    private static readonly Regex Balken = new(@"[█▒▓░]{3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Die letzten <paramref name="zeilen"/> aussagekraeftigen Zeilen der
    /// Ausgabe, mit " | " verbunden.
    ///
    /// Die rohe Ausgabe enthaelt den Fortschrittskreisel ("-", "\", "|", "/")
    /// und Fortschrittsbalken, die eine Konsole per Wagenruecklauf
    /// ueberschreibt. Ungefiltert stand im Protokoll nur "-" und eine Zeile
    /// voller Leerzeichen statt der eigentlichen Meldung.
    /// </summary>
    public static string LastMessage(string? ausgabe, int zeilen = 2)
    {
        if (string.IsNullOrEmpty(ausgabe)) return "";
        var sauber = new List<string>();
        foreach (var roh in ausgabe.Split('\n'))
        {
            // Was nach dem letzten Wagenruecklauf steht, ist das, was in einer
            // Konsole am Ende zu sehen waere.
            var teile = roh.Split('\r').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var zeile = teile.Count > 0 ? teile[^1] : "";
            // Rueckschritte fallen weg, andere Steuerzeichen (Tabulator) werden zu Leerzeichen.
            zeile = new string(zeile.Where(c => c != '\b').Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (zeile.Length == 0 || NurKreisel.IsMatch(zeile) || Balken.IsMatch(zeile)) continue;
            sauber.Add(zeile);
        }
        return string.Join(" | ", sauber.TakeLast(Math.Max(1, zeilen)));
    }

    // ------------------------------------------------------------ Welche winget.exe?

    // "Microsoft.DesktopAppInstaller_1.26.430.0_x64__8wekyb3d8bbwe"
    private static readonly Regex PaketOrdner =
        new(@"^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+){1,3})_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Die Fassung im Namen eines App-Installer-Ordners, sonst null.</summary>
    public static Version? FolderVersion(string ordner)
    {
        // Letzter Teil des Pfads - von Hand, damit "\\" auch in der Pruefumgebung
        // unter Linux als Trennzeichen zaehlt.
        var name = ordner.TrimEnd('\\', '/');
        var schnitt = name.LastIndexOfAny(new[] { '\\', '/' });
        if (schnitt >= 0) name = name[(schnitt + 1)..];
        var m = PaketOrdner.Match(name);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>
    /// App-Installer-Ordner aus WindowsApps, neueste Fassung zuerst.
    ///
    /// Frueher nach dem Namen sortiert - als Text ist "1.9" groesser als
    /// "1.26", und ein uralter App-Installer stand vor dem aktuellen. Mit ihm
    /// passen die heutigen Paketquellen nicht mehr zusammen.
    /// </summary>
    public static List<string> NewestFirst(IEnumerable<string> ordner)
        => ordner.OrderByDescending(o => FolderVersion(o) ?? new Version(0, 0))
                 .ThenByDescending(o => o, StringComparer.OrdinalIgnoreCase)
                 .ToList();
}
