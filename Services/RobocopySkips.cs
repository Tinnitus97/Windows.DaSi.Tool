using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Fasst die Fehlermeldungen von robocopy fuer einen Ordner zusammen - gedacht
/// fuer AppData.
///
/// Dort sind gesperrte und verschwundene Dateien normal: Discord, Teams,
/// Browser und Co. schreiben staendig in ihre Ordner. robocopy meldet JEDE
/// solche Datei mit drei Zeilen:
///
///   2026/11/10 20:15:31 FEHLER 2 (0x00000002) Datei wird kopiert C:\...\012884.log
///   Das System kann die angegebene Datei nicht finden.
///   FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde ueberschritten.
///
/// Die letzte Zeile erscheint auch mit /R:0 - dann heisst sie nur: der eine
/// Versuch ist gescheitert. Bei tausenden Dateien ging das Protokoll darin
/// unter. Stattdessen werden diese Bloecke hier geschluckt und gezaehlt; am
/// Ende steht eine Zeile je Fehlerart im Protokoll und die vollstaendige Liste
/// in einer Datei neben der Sicherung. Meldungen zu Dateien AUSSERHALB des
/// Ordners bleiben unveraendert im Protokoll.
///
/// Ohne Abhaengigkeiten, damit sich die Erkennung pruefen laesst.
/// </summary>
public sealed class RobocopySkips
{
    /// <summary>Name der Liste, die neben der Sicherung abgelegt wird.</summary>
    public const string ListFileName = "AppData-nicht-gesichert.txt";

    /// <summary>Hoechstens so viele Pfade werden gemerkt; die Anzahl stimmt immer.</summary>
    public const int MaxPaths = 100_000;

    /// <summary>
    /// Fehlerarten, die bei AppData dazugehoeren: waehrend der Sicherung
    /// geloescht oder umbenannt (2, 3), von einem laufenden Programm gesperrt
    /// (32, 33), vom System nicht lesbar - etwa die App-Aliase in
    /// "WindowsApps" (1920). Alles andere - Zugriff verweigert, Platte voll -
    /// bleibt ein Fehler.
    /// </summary>
    public static readonly IReadOnlySet<int> Expected = new HashSet<int> { 2, 3, 32, 33, 1920 };

    // Kopfzeile eines Fehlers, in jeder Sprache gleich gebaut:
    // "<Datum> <Zeit> <FEHLER|ERROR|...> <Nummer> (0x<Nummer hexadezimal>) <Vorgang> <Pfad>".
    // Erkannt am Paar aus Nummer und derselben Nummer in hex - nicht am Wort.
    private static readonly Regex Kopf =
        new(@"\s(\d{1,10}) \(0x([0-9A-Fa-f]{8})\)\s+(.*)$", RegexOptions.CultureInvariant);

    // Der Pfad am Ende der Kopfzeile. robocopy schreibt Windows-Pfade
    // (Laufwerk oder UNC); "/..." nur, damit es in der Pruefumgebung genauso geht.
    private static readonly Regex PfadAmEnde =
        new(@"(?:[A-Za-z]:\\|\\\\|/).*$", RegexOptions.CultureInvariant);

    // "ERROR: RETRY LIMIT EXCEEDED." bzw.
    // "FEHLER: Die maximale Anzahl von Wiederholungsversuchen wurde ueberschritten."
    private static readonly Regex Abschluss =
        new(@"^\s*(?:ERROR|FEHLER)\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private enum Erwartet { Nichts, Grund, Abschluss }

    private readonly string _rootAnzeige;
    private readonly string _root;
    private readonly Dictionary<int, int> _anzahl = new();
    private readonly Dictionary<int, string> _grund = new();
    private readonly List<(int Code, string Path)> _pfade = new();
    private Erwartet _erwartet = Erwartet.Nichts;
    private int _code;

    /// <param name="root">Der Ordner, dessen Meldungen zusammengefasst werden, z. B. "C:\Users\Max\AppData".</param>
    public RobocopySkips(string root)
    {
        _rootAnzeige = root;
        _root = Normal(root);
    }

    /// <summary>Uebersprungene Dateien im Ordner.</summary>
    public int Count { get; private set; }

    /// <summary>Fehlermeldungen zu Dateien ausserhalb des Ordners (bleiben im Protokoll).</summary>
    public int OtherErrors { get; private set; }

    public IReadOnlyDictionary<int, int> CountByCode => _anzahl;
    public IReadOnlyDictionary<int, string> ReasonByCode => _grund;
    public IReadOnlyList<(int Code, string Path)> Paths => _pfade;

    /// <summary>true, wenn jede uebersprungene Datei nur gesperrt oder verschwunden war.</summary>
    public bool AllExpected => _anzahl.Keys.All(Expected.Contains);

    /// <summary>
    /// Nimmt eine Ausgabezeile von robocopy entgegen. true heisst: Sie gehoert
    /// zu einer Meldung ueber eine Datei im Ordner und kommt NICHT ins Protokoll.
    /// Erwartet die Zeilen in der Reihenfolge, in der robocopy sie schreibt.
    /// </summary>
    public bool Swallow(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        if (TryParseHeader(line, out var code, out var pfad))
        {
            if (pfad is not null && IsUnder(pfad, _root))
            {
                Count++;
                _anzahl[code] = _anzahl.TryGetValue(code, out var n) ? n + 1 : 1;
                if (_pfade.Count < MaxPaths) _pfade.Add((code, pfad));
                _code = code;
                _erwartet = Erwartet.Grund;
                return true;
            }
            OtherErrors++;
            _erwartet = Erwartet.Nichts;
            return false;
        }

        if (_erwartet == Erwartet.Nichts) return false;

        // Dateizeilen anderer Kopier-Threads (/MT) sind eingerueckt und koennen
        // dazwischenrutschen - sie gehoeren nicht zur Meldung.
        if (line[0] == ' ' || line[0] == '\t') return false;

        if (Abschluss.IsMatch(line))
        {
            _erwartet = Erwartet.Nichts;
            return true;
        }
        if (_erwartet == Erwartet.Grund)
        {
            if (!_grund.ContainsKey(_code)) _grund[_code] = line.Trim();
            _erwartet = Erwartet.Abschluss;
            return true;
        }
        // Statt des Abschlusses kam etwas anderes - das gehoert nicht mehr dazu.
        _erwartet = Erwartet.Nichts;
        return false;
    }

    /// <summary>Erkennt die Kopfzeile einer robocopy-Fehlermeldung.</summary>
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
    /// Kurzfassung fuers Protokoll: eine Zeile je Fehlerart, haeufigste zuerst.
    /// </summary>
    /// <param name="was">Wie der Ordner im Text heisst, z. B. "AppData".</param>
    /// <param name="listPath">Wo die vollstaendige Liste liegt (oder null).</param>
    /// <param name="tr">Uebersetzung (deutsch, englisch) - im Programm Loc.Tr.</param>
    public string Summary(string was, string? listPath, Func<string, string, string> tr)
    {
        var sb = new StringBuilder();
        sb.Append(tr($"[WARNUNG] {was}: {Zahl(Count, tr)} Dateien nicht gesichert",
                     $"[WARNING] {was}: {Zahl(Count, tr)} files not backed up"));
        sb.Append(AllExpected
            ? tr(" - von laufenden Programmen gesperrt oder während der Sicherung gelöscht. Bei AppData ist das normal.",
                 " - locked by running programs or deleted during the backup. This is normal for AppData.")
            : ":");
        foreach (var (code, n) in _anzahl.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
        {
            sb.Append('\n').Append("    ").Append(Zahl(n, tr).PadLeft(7)).Append(" × ")
              .Append(tr("Fehler", "error")).Append(' ').Append(code.ToString(CultureInfo.InvariantCulture));
            if (_grund.TryGetValue(code, out var g)) sb.Append(": ").Append(g);
        }
        if (listPath is not null)
            sb.Append('\n').Append(tr($"    Vollständige Liste: {listPath}", $"    Full list: {listPath}"));
        return sb.ToString();
    }

    /// <summary>Inhalt der Liste neben der Sicherung: alle Pfade, nach Fehlerart.</summary>
    public string ListText(string was, DateTime when, Func<string, string, string> tr)
    {
        var sb = new StringBuilder();
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

        foreach (var gruppe in _pfade.GroupBy(p => p.Code).OrderByDescending(g => _anzahl[g.Key]).ThenBy(g => g.Key))
        {
            sb.AppendLine();
            sb.Append(tr("Fehler ", "Error ")).Append(gruppe.Key.ToString(CultureInfo.InvariantCulture));
            if (_grund.TryGetValue(gruppe.Key, out var g)) sb.Append(" - ").Append(g);
            sb.AppendLine(tr($" ({Zahl(_anzahl[gruppe.Key], tr)} Dateien)", $" ({Zahl(_anzahl[gruppe.Key], tr)} files)"));
            foreach (var (_, pfad) in gruppe) sb.Append("  ").AppendLine(pfad);
        }
        if (Count > _pfade.Count)
        {
            sb.AppendLine();
            sb.AppendLine(tr($"... und {Zahl(Count - _pfade.Count, tr)} weitere (nicht aufgeführt).",
                             $"... and {Zahl(Count - _pfade.Count, tr)} more (not listed)."));
        }
        return sb.ToString();
    }

    /// <summary>"1.234" bzw. "1,234" - ohne Kulturdaten des Systems.</summary>
    private static string Zahl(int n, Func<string, string, string> tr)
    {
        var nf = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        nf.NumberGroupSeparator = tr(".", ",");
        return n.ToString("N0", nf);
    }

    private static string Normal(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\');

    /// <summary>Liegt der Pfad im Ordner? "AppDataAlt" liegt nicht in "AppData".</summary>
    private static bool IsUnder(string pfad, string root)
    {
        if (root.Length == 0) return false;
        var p = Normal(pfad);
        return p.Equals(root, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
