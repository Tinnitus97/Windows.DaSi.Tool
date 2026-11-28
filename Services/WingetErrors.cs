using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// What winget means by its return codes, and how to make its output
/// readable. No dependencies, so it can be tested.
///
/// winget's documentation (returnCodes.md) lists the values as HRESULT,
/// e.g. 0x8A150009; as a process exit code they arrive signed
/// (-1978335223).
/// </summary>
public static class WingetErrors
{
    public const int Ok = 0;

    /// <summary>0x8A150009: "Cannot write to index; it is a higher schema version".</summary>
    public const int IndexZuNeu = unchecked((int)0x8A150009);

    /// <summary>0x8A15000A: the index is corrupt.</summary>
    public const int IndexBeschaedigt = unchecked((int)0x8A15000A);

    /// <summary>0x8A15000B: the configured sources are corrupt.</summary>
    public const int QuellenBeschaedigt = unchecked((int)0x8A15000B);

    /// <summary>0x8A15000F: data required by the source is missing.</summary>
    public const int QuellDatenFehlen = unchecked((int)0x8A15000F);

    /// <summary>0x8A150014: no package matches the query.</summary>
    public const int NichtGefunden = unchecked((int)0x8A150014);

    /// <summary>0x8A15002B: no applicable update - already the newest version.</summary>
    public const int SchonAktuell = unchecked((int)0x8A15002B);

    /// <summary>0x8A150061: the package is already installed.</summary>
    public const int SchonInstalliert = unchecked((int)0x8A150061);

    /// <summary>
    /// true if it is not the package's fault but winget's own: its package
    /// source or the source's database is broken or comes from a newer winget
    /// version. Then EVERY further package fails the same way - repair first,
    /// then carry on.
    /// </summary>
    public static bool SourceBroken(int code)
        => code is IndexZuNeu or IndexBeschaedigt or QuellenBeschaedigt or QuellDatenFehlen;

    /// <summary>Also recognizable from the output - in case an exit code is missing or wrong.</summary>
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

    /// <summary>"0x8A150009" - the form used in the documentation and in search engines.</summary>
    public static string Hex(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// One sentence on what the value means - only for values the user can act
    /// on. Otherwise null.
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

    // ------------------------------------------------------------ Make the output readable

    // winget's progress spinner: single characters overwritten with "\r" or
    // backspace.
    private static readonly Regex NurKreisel = new(@"^[\s\-\\|/]*$", RegexOptions.CultureInvariant);

    // Progress bars: "██████▒▒▒  1.2 MB / 3.4 MB" or percentages.
    private static readonly Regex Balken = new(@"[█▒▓░]{3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The last <paramref name="zeilen"/> meaningful lines of the output, joined
    /// with " | ".
    ///
    /// The raw output contains the progress spinner ("-", "\", "|", "/") and
    /// progress bars that a console overwrites with carriage returns.
    /// Unfiltered, the log showed only "-" and a line full of spaces instead of
    /// the actual message.
    /// </summary>
    public static string LastMessage(string? ausgabe, int zeilen = 2)
    {
        if (string.IsNullOrEmpty(ausgabe)) return "";
        var sauber = new List<string>();
        foreach (var roh in ausgabe.Split('\n'))
        {
            // What follows the last carriage return is what a console would show in
            // the end.
            var teile = roh.Split('\r').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var zeile = teile.Count > 0 ? teile[^1] : "";
            // Backspaces are dropped, other control characters (tab) become spaces.
            zeile = new string(zeile.Where(c => c != '\b').Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (zeile.Length == 0 || NurKreisel.IsMatch(zeile) || Balken.IsMatch(zeile)) continue;
            sauber.Add(zeile);
        }
        return string.Join(" | ", sauber.TakeLast(Math.Max(1, zeilen)));
    }

    // ------------------------------------------------------------ Which winget.exe?

    // "Microsoft.DesktopAppInstaller_1.26.430.0_x64__8wekyb3d8bbwe"
    private static readonly Regex PaketOrdner =
        new(@"^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+){1,3})_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The version in the name of an App Installer folder, otherwise null.</summary>
    public static Version? FolderVersion(string ordner)
    {
        // Last part of the path - done by hand, so "\\" counts as a separator in
        // the Linux test environment as well.
        var name = ordner.TrimEnd('\\', '/');
        var schnitt = name.LastIndexOfAny(new[] { '\\', '/' });
        if (schnitt >= 0) name = name[(schnitt + 1)..];
        var m = PaketOrdner.Match(name);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>
    /// App Installer folders from WindowsApps, newest version first.
    ///
    /// They used to be sorted by name - as text, "1.9" is greater than "1.26",
    /// and an ancient App Installer came before the current one. It no longer
    /// works with today's package sources.
    /// </summary>
    public static List<string> NewestFirst(IEnumerable<string> ordner)
        => ordner.OrderByDescending(o => FolderVersion(o) ?? new Version(0, 0))
                 .ThenByDescending(o => o, StringComparer.OrdinalIgnoreCase)
                 .ToList();
}
