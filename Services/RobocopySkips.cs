using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Summarizes robocopy's error messages for one folder - meant for AppData.
///
/// Locked and vanished files are normal there: Discord, Teams, browsers and
/// the like write to their folders all the time. robocopy reports EVERY
/// such file with three lines:
///
///   2026/11/10 20:15:31 ERROR 2 (0x00000002) Copying File C:\...\012884.log
///   The system cannot find the file specified.
///   ERROR: RETRY LIMIT EXCEEDED.
///
/// The last line appears with /R:0 as well - it then only means: the one
/// attempt failed. With thousands of files, the log drowned in these
/// blocks. Instead, they are swallowed and counted here; at the end the log
/// gets one line per error kind and a file next to the backup gets the full
/// list. Messages about files OUTSIDE the folder stay in the log unchanged.
///
/// No dependencies, so the detection can be tested.
/// </summary>
public sealed class RobocopySkips
{
    /// <summary>Name of the list placed next to the backup.</summary>
    public const string ListFileName = "AppData-nicht-gesichert.txt";

    /// <summary>Name of the list after a restore - also next to the backup.</summary>
    public const string RestoreListFileName = "AppData-nicht-wiederhergestellt.txt";

    /// <summary>At most this many paths are kept; the count is always right.</summary>
    public const int MaxPaths = 100_000;

    /// <summary>
    /// Error kinds that come with AppData: deleted or renamed during the backup
    /// (2, 3), locked by a running program (32, 33) or mapped into its memory
    /// (1224 - "a file with a user-mapped section open", e.g. the executable or
    /// database of a running program), not readable by the system - e.g. the
    /// app aliases in "WindowsApps" (1920). Everything else - access denied,
    /// disk full - stays an error.
    /// </summary>
    public static readonly IReadOnlySet<int> Expected = new HashSet<int> { 2, 3, 32, 33, 1224, 1920 };

    // Header line of an error, built the same way in every language:
    // "<date> <time> <ERROR|FEHLER|...> <number> (0x<number in hex>) <operation> <path>".
    // Recognized by the number followed by the same number in hex - not by the word.
    private static readonly Regex Kopf =
        new(@"\s(\d{1,10}) \(0x([0-9A-Fa-f]{8})\)\s+(.*)$", RegexOptions.CultureInvariant);

    // The path at the end of the header line. robocopy writes Windows paths
    // (drive or UNC); "/..." only so it works the same in the test environment.
    private static readonly Regex PfadAmEnde =
        new(@"(?:[A-Za-z]:\\|\\\\|/).*$", RegexOptions.CultureInvariant);

    // "ERROR: RETRY LIMIT EXCEEDED." or
    // "FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde ueberschritten."
    private static readonly Regex Abschluss =
        new(@"^\s*(?:ERROR|FEHLER)\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // With /R:1 and more, robocopy announces the next attempt instead of the
    // final line: "Waiting 1 seconds... Retrying..." - in every language with
    // the wait time as a number and an ellipsis. Then either a new block for
    // the same file follows - or the file is copied on the second attempt.
    private static readonly Regex Wiederholung =
        new(@"\d.*\.\.\.\s*$", RegexOptions.CultureInvariant);

    /// <summary>A message whose reason or final line is still pending.</summary>
    private sealed class Offen
    {
        /// <summary>The file is inside the folder - the message's lines are swallowed.</summary>
        public bool Innen;
        public int Code;
        /// <summary>Counted for the first time with this message (otherwise null).</summary>
        public string? Gezaehlt;
        public bool HatGrund;
    }

    /// <summary>At most this many messages are kept open at the same time.</summary>
    private const int MaxOffen = 64;

    private readonly string _rootAnzeige;
    private readonly string _root;
    private readonly bool _zurueck;
    private readonly Dictionary<int, int> _anzahl = new();
    private readonly Dictionary<int, string> _grund = new();
    private readonly List<(int Code, string Path)> _pfade = new();
    private readonly HashSet<string> _gesehen = new(StringComparer.OrdinalIgnoreCase);
    // Messages in the order of their header lines. With /MT:32, 32 threads
    // copy at once, and their messages can interleave: header A, header B,
    // reason A, reason B ... So reason and final lines are assigned to the
    // oldest message still waiting for one.
    private readonly List<Offen> _offen = new();

    /// <param name="root">The folder whose messages are summarized, e.g. "C:\Users\Max\AppData".</param>
    /// <param name="zurueck">
    /// true when restoring into the profile. robocopy names the path in the
    /// SOURCE then as well - so <paramref name="root"/> is the AppData folder in
    /// the backup. What is locked in that case is the file at the target: a
    /// running program holds it open, and the existing version stays there.
    /// </param>
    public RobocopySkips(string root, bool zurueck = false)
    {
        _rootAnzeige = root;
        _root = Normal(root);
        _zurueck = zurueck;
    }

    /// <summary>true when restoring into the profile.</summary>
    public bool Zurueck => _zurueck;

    /// <summary>Skipped files in the folder.</summary>
    public int Count { get; private set; }

    /// <summary>Error messages about files outside the folder (they stay in the log).</summary>
    public int OtherErrors { get; private set; }

    public IReadOnlyDictionary<int, int> CountByCode => _anzahl;
    public IReadOnlyDictionary<int, string> ReasonByCode => _grund;
    public IReadOnlyList<(int Code, string Path)> Paths => _pfade;

    /// <summary>true if every skipped file was only locked or gone.</summary>
    public bool AllExpected => _anzahl.Keys.All(Expected.Contains);

    /// <summary>
    /// Takes one output line of robocopy. true means: it belongs to a message
    /// about a file inside the folder and does NOT go to the log. Expects the
    /// lines in the order robocopy writes them.
    /// </summary>
    public bool Swallow(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        if (TryParseHeader(line, out var code, out var pfad))
        {
            var meldung = new Offen { Innen = pfad is not null && IsUnder(pfad, _root), Code = code };
            if (meldung.Innen)
            {
                // Each file only once - even if robocopy reports it several times.
                if (_gesehen.Add(pfad!))
                {
                    Count++;
                    _anzahl[code] = _anzahl.TryGetValue(code, out var n) ? n + 1 : 1;
                    if (_pfade.Count < MaxPaths) _pfade.Add((code, pfad!));
                    meldung.Gezaehlt = pfad;
                }
            }
            else
            {
                OtherErrors++;
            }
            _offen.Add(meldung);
            if (_offen.Count > MaxOffen) _offen.RemoveAt(0);
            return meldung.Innen;
        }

        if (_offen.Count == 0) return false;

        // File lines of other copy threads (/MT) are indented and can slip in
        // between - they do not belong to the message.
        if (line[0] == ' ' || line[0] == '\t') return false;

        // The separator line before the summary: nothing more is added after it.
        if (line.StartsWith("---", StringComparison.Ordinal))
        {
            _offen.Clear();
            return false;
        }

        bool abschluss = Abschluss.IsMatch(line);
        if (abschluss || Wiederholung.IsMatch(line))
        {
            var fertig = _offen.FirstOrDefault(o => o.HatGrund) ?? _offen[0];
            _offen.Remove(fertig);
            // With /R:1 and more: not given up yet - undo the count. If the next
            // attempt fails as well, a new message follows; if it succeeds, the file
            // was copied and does not belong in the list.
            if (!abschluss && fertig.Innen) Undo(fertig);
            return fertig.Innen;
        }

        var ohneGrund = _offen.FirstOrDefault(o => !o.HatGrund);
        if (ohneGrund is null)
        {
            // Something else came instead of the final line - it no longer belongs to
            // the message.
            _offen.Clear();
            return false;
        }
        ohneGrund.HatGrund = true;
        if (ohneGrund.Innen && !_grund.ContainsKey(ohneGrund.Code)) _grund[ohneGrund.Code] = line.Trim();
        return ohneGrund.Innen;
    }

    private void Undo(Offen meldung)
    {
        if (meldung.Gezaehlt is null) return;
        _gesehen.Remove(meldung.Gezaehlt);
        Count--;
        if (_anzahl.TryGetValue(meldung.Code, out var n))
        {
            if (n <= 1) _anzahl.Remove(meldung.Code); else _anzahl[meldung.Code] = n - 1;
        }
        for (int i = _pfade.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_pfade[i].Path, meldung.Gezaehlt, StringComparison.OrdinalIgnoreCase)) { _pfade.RemoveAt(i); break; }
        }
    }

    /// <summary>Recognizes the header line of a robocopy error message.</summary>
    public static bool TryParseHeader(string line, out int code, out string? path)
    {
        code = 0;
        path = null;
        if (string.IsNullOrEmpty(line) || line[0] == ' ' || line[0] == '\t') return false;
        var m = Kopf.Match(line);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out code)) return false;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) || hex != code)
            return false;
        var p = PfadAmEnde.Match(m.Groups[3].Value);
        if (p.Success) path = p.Value.Trim();
        return true;
    }

    /// <summary>
    /// Short version for the log: one line per error kind, most frequent first.
    /// </summary>
    /// <param name="was">What the folder is called in the text, e.g. "AppData".</param>
    /// <param name="listPath">Where the full list is (or null).</param>
    /// <param name="tr">Translation (German, English) - Loc.Tr in the program.</param>
    public string Summary(string was, string? listPath, Func<string, string, string> tr)
    {
        var sb = new StringBuilder();
        if (_zurueck)
        {
            sb.Append(tr($"[WARNUNG] {was}: {FormatCount(Count, tr)} Dateien nicht zurückgeschrieben",
                         $"[WARNING] {was}: {FormatCount(Count, tr)} files not restored"));
            sb.Append(AllExpected
                ? tr(" - im Profil von laufenden Programmen gesperrt; dort bleibt die vorhandene Fassung stehen. "
                     + "Meist sind es Zwischenspeicher, die sich von selbst neu anlegen.",
                     " - locked in the profile by running programs; the existing version stays in place. "
                     + "Mostly these are caches that are recreated automatically.")
                : ":");
        }
        else
        {
            sb.Append(tr($"[WARNUNG] {was}: {FormatCount(Count, tr)} Dateien nicht gesichert",
                         $"[WARNING] {was}: {FormatCount(Count, tr)} files not backed up"));
            sb.Append(AllExpected
                ? tr(" - von laufenden Programmen gesperrt oder während der Sicherung gelöscht. Bei AppData ist das normal.",
                     " - locked by running programs or deleted during the backup. This is normal for AppData.")
                : ":");
        }
        foreach (var (code, n) in _anzahl.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
        {
            sb.Append('\n').Append("    ").Append(FormatCount(n, tr).PadLeft(7)).Append(" × ")
              .Append(tr("Fehler", "error")).Append(' ').Append(code.ToString(CultureInfo.InvariantCulture));
            if (_grund.TryGetValue(code, out var g)) sb.Append(": ").Append(g);
        }
        if (listPath is not null)
            sb.Append('\n').Append(tr($"    Vollständige Liste: {listPath}", $"    Full list: {listPath}"));
        return sb.ToString();
    }

    /// <summary>Content of the list next to the backup: all paths, by error kind.</summary>
    public string ListText(string was, DateTime when, Func<string, string, string> tr)
    {
        var sb = new StringBuilder();
        if (_zurueck)
        {
            sb.AppendLine(tr($"Windows DaSi Tool - {was}: Dateien, die nicht zurückgeschrieben werden konnten",
                             $"Windows DaSi Tool - {was}: files that could not be restored"));
            sb.AppendLine(tr($"Wiederherstellung vom {when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}, Quelle: {_rootAnzeige}",
                             $"Restore of {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, source: {_rootAnzeige}"));
            sb.AppendLine();
            sb.AppendLine(tr("Fehler 32/33: Die Datei im Profil war von einem laufenden Programm gesperrt - dort",
                             "Error 32/33: the file in the profile was locked by a running program - the existing"));
            sb.AppendLine(tr("steht weiter die vorhandene Fassung. Fehler 2/3: Die Datei verschwand während des",
                             "version stays in place. Error 2/3: the file vanished while copying. Mostly these"));
            sb.AppendLine(tr("Kopierens. Meist sind es Zwischenspeicher. Wer diese Dateien braucht, schließt die",
                             "are caches. If you need these files, close the programs (including those in the"));
            sb.AppendLine(tr("Programme (auch die im Infobereich) und stellt AppData erneut wieder her.",
                             "notification area) and restore AppData again."));
        }
        else
        {
            sb.AppendLine(tr($"Windows DaSi Tool - {was}: Dateien, die nicht gesichert werden konnten",
                             $"Windows DaSi Tool - {was}: files that could not be backed up"));
            sb.AppendLine(tr($"Sicherung vom {when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}, Quelle: {_rootAnzeige}",
                             $"Backup of {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, source: {_rootAnzeige}"));
            sb.AppendLine();
            sb.AppendLine(tr("Fehler 32/33: von einem laufenden Programm gesperrt. Fehler 2/3: während der Sicherung",
                             "Error 32/33: locked by a running program. Error 2/3: deleted or renamed during the"));
            sb.AppendLine(tr("gelöscht oder umbenannt. Programme wie Discord, Teams oder Browser schreiben ständig",
                             "backup. Programs such as Discord, Teams or browsers write to AppData all the time -"));
            sb.AppendLine(tr("in AppData - wer diese Dateien braucht, schließt die Programme und sichert erneut.",
                             "if you need these files, close the programs and back up again."));
        }

        foreach (var gruppe in _pfade.GroupBy(p => p.Code).OrderByDescending(g => _anzahl[g.Key]).ThenBy(g => g.Key))
        {
            sb.AppendLine();
            sb.Append(tr("Fehler ", "Error ")).Append(gruppe.Key.ToString(CultureInfo.InvariantCulture));
            if (_grund.TryGetValue(gruppe.Key, out var g)) sb.Append(" - ").Append(g);
            sb.AppendLine(tr($" ({FormatCount(_anzahl[gruppe.Key], tr)} Dateien)", $" ({FormatCount(_anzahl[gruppe.Key], tr)} files)"));
            foreach (var (_, pfad) in gruppe) sb.Append("  ").AppendLine(pfad);
        }
        if (Count > _pfade.Count)
        {
            sb.AppendLine();
            sb.AppendLine(tr($"... und {FormatCount(Count - _pfade.Count, tr)} weitere (nicht aufgeführt).",
                             $"... and {FormatCount(Count - _pfade.Count, tr)} more (not listed)."));
        }
        return sb.ToString();
    }

    /// <summary>"1.234" or "1,234" - without the system's culture data.</summary>
    private static string FormatCount(int n, Func<string, string, string> tr)
    {
        var nf = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        nf.NumberGroupSeparator = tr(".", ",");
        return n.ToString("N0", nf);
    }

    private static string Normal(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\');

    /// <summary>Is the path inside the folder? "AppDataAlt" is not inside "AppData".</summary>
    private static bool IsUnder(string pfad, string root)
    {
        if (root.Length == 0) return false;
        var p = Normal(pfad);
        return p.Equals(root, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
