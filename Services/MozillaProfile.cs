using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Welches Profil Firefox (und Thunderbird) beim Start nimmt.
///
/// Seit Firefox 67 hat jede Installation ihr eigenes Standardprofil. In
/// profiles.ini (und installs.ini) steht es unter "[Install&lt;Schluessel&gt;]";
/// der Schluessel ist aus dem Installationsordner berechnet. Ein Anwender
/// hatte auf dem alten PC das 32-Bit-Firefox in "C:\Program Files (x86)\Mozilla
/// Firefox" (Schluessel E7CF176E110C211B), winget installierte auf dem neuen
/// das 64-Bit-Firefox in "C:\Program Files\Mozilla Firefox" (308046B0AF4A39CB).
/// Das zurueckgeschriebene Profil gehoerte - gesperrt ("Locked=1") - der alten
/// Installation, Firefox fand fuer sich keins und legte ein leeres
/// "default-release" an. Die Daten waren da, nur nicht im benutzten Profil.
///
/// Abhilfe nach dem Zurueckschreiben: das Profil, das auf dem alten PC lief,
/// als Standard fuer jede Installation auf diesem PC eintragen.
///
/// Bis auf das Suchen der Installationen ohne Abhaengigkeiten, damit es sich
/// pruefen laesst.
/// </summary>
public static class MozillaProfile
{
    /// <summary>Was <see cref="Anpassen"/> getan hat.</summary>
    /// <param name="Profil">Pfad des Profils, wie in profiles.ini (z. B. "Profiles/abcd1234.default"); null: keins gefunden.</param>
    /// <param name="Name">Name des Profils (z. B. "default").</param>
    /// <param name="AlteSchluessel">Die Installationen aus der Sicherung (Schluessel).</param>
    /// <param name="Neu">Die Installationen auf diesem PC: Ordner und Schluessel.</param>
    public sealed record Ergebnis(string? Profil, string? Name, IReadOnlyList<string> AlteSchluessel, IReadOnlyList<(string Ordner, string Schluessel)> Neu)
    {
        /// <summary>Lief das Profil auf dem alten PC unter einer der Installationen dieses PCs?</summary>
        public bool SchonPassend => Neu.Count > 0 && Neu.All(n => AlteSchluessel.Contains(n.Schluessel, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Die Programme mit Mozilla-Profilverwaltung: Programmdatei und ueblicher Installationsordner.</summary>
    public static (string Exe, string Ordner)? Programm(AppEintrag app) => app.Punkt switch
    {
        Punkt.Firefox => ("firefox.exe", "Mozilla Firefox"),
        Punkt.Thunderbird => ("thunderbird.exe", "Mozilla Thunderbird"),
        _ => null,
    };

    // ------------------------------------------------------------ Schluessel einer Installation

    /// <summary>
    /// Der Schluessel einer Installation, wie Firefox ihn berechnet: CityHash64
    /// (Fassung 1.0, wie in Firefox enthalten) ueber den Pfad des Ordners in
    /// UTF-16, hexadezimal in Grossbuchstaben ohne fuehrende Nullen.
    /// "C:\Program Files\Mozilla Firefox" -> "308046B0AF4A39CB".
    /// </summary>
    public static string Schluessel(string installationsOrdner)
        => CityHash64(Encoding.Unicode.GetBytes(installationsOrdner)).ToString("X");

    private const ulong K0 = 0xc3a5c85c97cb3127UL, K1 = 0xb492b66fbe98f273UL, K2 = 0x9ae16a3b2f90404fUL, K3 = 0xc949d7c7509e6557UL;

    private static ulong F64(byte[] s, int i) => BitConverter.ToUInt64(s, i);
    private static ulong F32(byte[] s, int i) => BitConverter.ToUInt32(s, i);
    private static ulong Rot(ulong v, int n) => n == 0 ? v : (v >> n) | (v << (64 - n));
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong Hash16(ulong u, ulong v)
    {
        const ulong mul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * mul; a ^= a >> 47;
        ulong b = (v ^ a) * mul; b ^= b >> 47;
        return b * mul;
    }

    private static ulong Hash0bis16(byte[] s, int n)
    {
        if (n > 8)
        {
            ulong a = F64(s, 0), b = F64(s, n - 8);
            return Hash16(a, Rot(b + (ulong)n, n)) ^ b;
        }
        if (n >= 4)
            return Hash16((ulong)n + (F32(s, 0) << 3), F32(s, n - 4));
        if (n > 0)
        {
            uint y = s[0] + ((uint)s[n >> 1] << 8);
            uint z = (uint)n + ((uint)s[n - 1] << 2);
            return ShiftMix((y * K2) ^ (z * K3)) * K2;
        }
        return K2;
    }

    private static ulong Hash17bis32(byte[] s, int n)
    {
        ulong a = F64(s, 0) * K1, b = F64(s, 8), c = F64(s, n - 8) * K2, d = F64(s, n - 16) * K0;
        return Hash16(Rot(a - b, 43) + Rot(c, 30) + d, a + Rot(b ^ K3, 20) - c + (ulong)n);
    }

    private static ulong Hash33bis64(byte[] s, int n)
    {
        ulong z = F64(s, 24);
        ulong a = F64(s, 0) + ((ulong)n + F64(s, n - 16)) * K0;
        ulong b = Rot(a + z, 52), c = Rot(a, 37);
        a += F64(s, 8); c += Rot(a, 7);
        a += F64(s, 16);
        ulong vf = a + z, vs = b + Rot(a, 31) + c;
        a = F64(s, 16) + F64(s, n - 32);
        z = F64(s, n - 8);
        b = Rot(a + z, 52); c = Rot(a, 37);
        a += F64(s, n - 24); c += Rot(a, 7);
        a += F64(s, n - 16);
        ulong wf = a + z, ws = b + Rot(a, 31) + c;
        ulong r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
        return ShiftMix(r * K0 + vs) * K2;
    }

    private static (ulong, ulong) Schwach32(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rot(b + a + z, 21);
        ulong c = a;
        a += x; a += y;
        b += Rot(a, 44);
        return (a + z, b + c);
    }

    private static (ulong, ulong) Schwach32(byte[] s, int i, ulong a, ulong b)
        => Schwach32(F64(s, i), F64(s, i + 8), F64(s, i + 16), F64(s, i + 24), a, b);

    internal static ulong CityHash64(byte[] s)
    {
        int n = s.Length;
        if (n <= 16) return Hash0bis16(s, n);
        if (n <= 32) return Hash17bis32(s, n);
        if (n <= 64) return Hash33bis64(s, n);

        ulong x = F64(s, 0), y = F64(s, n - 16) ^ K1, z = F64(s, n - 56) ^ K0;
        var v = Schwach32(s, n - 64, (ulong)n, y);
        var w = Schwach32(s, n - 32, (ulong)n * K1, K0);
        z += ShiftMix(v.Item2) * K1;
        x = Rot(z + x, 39) * K1;
        y = Rot(y, 33) * K1;

        int rest = (n - 1) & ~63, i = 0;
        do
        {
            x = Rot(x + y + v.Item1 + F64(s, i + 16), 37) * K1;
            y = Rot(y + v.Item2 + F64(s, i + 48), 42) * K1;
            x ^= w.Item2;
            y ^= v.Item1;
            z = Rot(z ^ w.Item1, 33);
            v = Schwach32(s, i, v.Item2 * K1, x + w.Item1);
            w = Schwach32(s, i + 32, z + w.Item2, y);
            (z, x) = (x, z);
            i += 64; rest -= 64;
        } while (rest != 0);
        return Hash16(Hash16(v.Item1, w.Item1) + ShiftMix(y) * K1 + z, Hash16(v.Item2, w.Item2) + x);
    }

    // ------------------------------------------------------------ profiles.ini

    /// <summary>Eine INI-Datei, wie Firefox sie schreibt - Reihenfolge bleibt erhalten.</summary>
    internal sealed class Ini
    {
        public List<(string Name, List<KeyValuePair<string, string>> Werte)> Abschnitte { get; } = new();

        public static Ini Lesen(string text)
        {
            var ini = new Ini();
            List<KeyValuePair<string, string>>? aktuell = null;
            foreach (var roh in text.Split('\n'))
            {
                var z = roh.Trim();
                if (z.Length == 0 || z.StartsWith(';') || z.StartsWith('#')) continue;
                if (z.StartsWith('[') && z.EndsWith(']'))
                {
                    aktuell = new List<KeyValuePair<string, string>>();
                    ini.Abschnitte.Add((z[1..^1], aktuell));
                    continue;
                }
                var gl = z.IndexOf('=');
                if (aktuell is null || gl <= 0) continue;
                aktuell.Add(new(z[..gl], z[(gl + 1)..]));
            }
            return ini;
        }

        public string? Wert(string abschnitt, string schluessel)
            => Abschnitte.FirstOrDefault(a => a.Name.Equals(abschnitt, StringComparison.OrdinalIgnoreCase)).Werte?
                .FirstOrDefault(w => w.Key.Equals(schluessel, StringComparison.OrdinalIgnoreCase)).Value;

        public string Text(string zeilenende)
        {
            var sb = new StringBuilder();
            foreach (var (name, werte) in Abschnitte)
            {
                sb.Append('[').Append(name).Append(']').Append(zeilenende);
                foreach (var w in werte) sb.Append(w.Key).Append('=').Append(w.Value).Append(zeilenende);
                sb.Append(zeilenende);
            }
            return sb.ToString();
        }
    }

    // [Install308046B0AF4A39CB], [Profile0] - nicht [General], [BackgroundTasksProfiles] ...
    private static bool IstInstallation(string abschnitt) => Regex.IsMatch(abschnitt, "^Install[0-9A-Fa-f]+$");
    private static bool IstProfil(string abschnitt) => Regex.IsMatch(abschnitt, "^Profile[0-9]+$");

    private static string? Wert(List<KeyValuePair<string, string>> werte, string schluessel)
        => werte.FirstOrDefault(w => w.Key.Equals(schluessel, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Der Ordner eines Profils (relativ zum Datenordner oder absolut).</summary>
    private static string ProfilOrdner(string datenOrdner, List<KeyValuePair<string, string>> profil)
    {
        var pfad = Wert(profil, "Path") ?? "";
        return Wert(profil, "IsRelative") == "0"
            ? pfad
            : Path.Combine(new[] { datenOrdner }.Concat(pfad.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)).ToArray());
    }

    /// <summary>Zuletzt benutzt: prefs.js schreibt Firefox bei jedem Beenden.</summary>
    private static DateTime ZuletztBenutzt(string ordner)
    {
        try
        {
            var prefs = Path.Combine(ordner, "prefs.js");
            return File.Exists(prefs) ? File.GetLastWriteTimeUtc(prefs) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Traegt im Datenordner (…\Mozilla\Firefox) das Profil, das auf dem alten
    /// PC lief, als Standard fuer die Installationen dieses PCs ein.
    ///
    /// Welches Profil lief: das einer Installation aus der Sicherung - bei
    /// mehreren das zuletzt benutzte. Ohne Installationseintraege (vor Firefox
    /// 67) das mit "Default=1", sonst das zuletzt benutzte.
    ///
    /// Die Installationseintraege des alten PCs fallen weg, jede Installation
    /// dieses PCs bekommt das Profil - gesperrt, wie Firefox es selbst
    /// eintraegt. Zusaetzlich traegt das Profil "Default=1": Findet sich auf
    /// diesem PC keine Installation (oder kommt eine andere hinzu), uebernimmt
    /// Firefox beim ersten Start dieses Profil, statt ein neues anzulegen.
    /// </summary>
    /// <param name="installationen">Die Installationsordner auf diesem PC (Schreibweise wie auf der Platte).</param>
    public static Ergebnis Anpassen(string datenOrdner, IEnumerable<string> installationen)
    {
        var neu = installationen.Select(o => (Ordner: o, Schluessel: Schluessel(o)))
                                .DistinctBy(n => n.Schluessel).ToList();
        var datei = Path.Combine(datenOrdner, "profiles.ini");
        if (!File.Exists(datei)) return new Ergebnis(null, null, Array.Empty<string>(), neu);

        var text = File.ReadAllText(datei);
        var zeilenende = text.Contains("\r\n") ? "\r\n" : "\n";
        var ini = Ini.Lesen(text);
        var profile = ini.Abschnitte.Where(a => IstProfil(a.Name)).ToList();
        var installs = ini.Abschnitte.Where(a => IstInstallation(a.Name)).ToList();
        var alteSchluessel = installs.Select(a => a.Name["Install".Length..]).ToList();

        // Welches Profil lief auf dem alten PC?
        var benutzt = installs.Select(a => Wert(a.Werte, "Default")).OfType<string>()
            .Select(p => profile.FirstOrDefault(pr => string.Equals(Wert(pr.Werte, "Path"), p, StringComparison.OrdinalIgnoreCase)))
            .Where(pr => pr.Werte is not null).ToList();
        if (benutzt.Count == 0)
            benutzt = profile.Where(pr => Wert(pr.Werte, "Default") == "1").ToList();
        if (benutzt.Count == 0)
            benutzt = profile;
        var gewaehlt = benutzt.Where(pr => Directory.Exists(ProfilOrdner(datenOrdner, pr.Werte)))
                              .OrderByDescending(pr => ZuletztBenutzt(ProfilOrdner(datenOrdner, pr.Werte)))
                              .FirstOrDefault();
        if (gewaehlt.Werte is null) return new Ergebnis(null, null, alteSchluessel, neu);
        var pfad = Wert(gewaehlt.Werte, "Path")!;

        // Profile: nur das gewaehlte ist der Standard.
        foreach (var (_, werte) in profile)
        {
            werte.RemoveAll(w => w.Key.Equals("Default", StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(werte, gewaehlt.Werte)) werte.Add(new("Default", "1"));
        }

        // Installationen: die des alten PCs weg, die dieses PCs vorne dazu.
        ini.Abschnitte.RemoveAll(a => IstInstallation(a.Name));
        var eintraege = neu.Select(n => ($"Install{n.Schluessel}", new List<KeyValuePair<string, string>> { new("Default", pfad), new("Locked", "1") })).ToList();
        ini.Abschnitte.InsertRange(0, eintraege);
        Schreiben(datei, ini.Text(zeilenende));

        // installs.ini: dieselben Installationen, dort nur mit dem Schluessel
        // als Namen ("[308046B0AF4A39CB]"). Liest ein Firefox profiles.ini ohne
        // "Version=2" (vor Firefox 69), uebernimmt er sie von dort.
        var installsIni = Path.Combine(datenOrdner, "installs.ini");
        if (eintraege.Count > 0)
        {
            var nurInstalls = new Ini();
            nurInstalls.Abschnitte.AddRange(eintraege.Select(e => (e.Item1["Install".Length..], e.Item2.ToList())));
            Schreiben(installsIni, nurInstalls.Text(zeilenende));
        }
        else if (File.Exists(installsIni))
        {
            File.Delete(installsIni);
        }

        return new Ergebnis(pfad, Wert(gewaehlt.Werte, "Name"), alteSchluessel, neu);
    }

    private static void Schreiben(string datei, string inhalt)
    {
        // Ueber eine Zwischendatei: Bricht das Schreiben ab, bleibt die alte stehen.
        var tmp = datei + ".dasi";
        File.WriteAllText(tmp, inhalt, new UTF8Encoding(false));
        File.Move(tmp, datei, overwrite: true);
    }

    /// <summary>Wo eine Installation laut ihrem Schluessel lag - soweit es ein ueblicher Ort ist.</summary>
    public static string? BekannterOrt(string schluessel, string ordnerName, string profil)
    {
        foreach (var ort in UeblicheOrte(ordnerName, profil))
            if (Schluessel(ort).Equals(schluessel, StringComparison.OrdinalIgnoreCase)) return ort;
        return null;
    }

    /// <summary>
    /// Die ueblichen Installationsordner: fuer alle Benutzer (64 und 32 Bit) und
    /// nur fuer den Benutzer (AppData\Local). Feste Laufwerksbuchstaben, wie sie
    /// auf dem alten PC lagen - unabhaengig davon, wo dieser PC Programme ablegt.
    /// </summary>
    internal static IEnumerable<string> UeblicheOrte(string ordnerName, string profil)
    {
        yield return $@"C:\Program Files\{ordnerName}";
        yield return $@"C:\Program Files (x86)\{ordnerName}";
        yield return Path.Combine(profil, "AppData", "Local", ordnerName);
    }

    // ------------------------------------------------------------ Installationen auf diesem PC

    /// <summary>
    /// Die Installationsordner des Programms auf diesem PC: die ueblichen Orte
    /// und die, die Windows unter "App Paths" fuehrt - jeweils nur, wenn die
    /// Programmdatei dort liegt. Geschrieben wie auf der Platte, denn so
    /// berechnet Firefox den Schluessel.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static List<string> Installationen(string exe, string ordnerName, string profil)
    {
        var orte = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ordnerName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ordnerName),
            Path.Combine(profil, "AppData", "Local", ordnerName),
        };
        var appPaths = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}";
        foreach (var ansicht in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var basis = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, ansicht);
                using var k = basis.OpenSubKey(appPaths);
                if (k?.GetValue("") is string p && Path.GetDirectoryName(p.Trim().Trim('"')) is { Length: > 0 } ordner)
                    orte.Add(ordner);
            }
            catch { /* naechste */ }
        }
        return orte.Where(o => o.Length > 0 && File.Exists(Path.Combine(o, exe)))
                   .Select(EchterPfad)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>
    /// Der Pfad so, wie er auf der Platte steht - Gross-/Kleinschreibung,
    /// Verknuepfungen aufgeloest. Firefox tut dasselbe, bevor es den Schluessel
    /// berechnet.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string EchterPfad(string ordner)
    {
        var voll = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ordner));
        using var h = CreateFileW(voll, 0, 7 /* lesen, schreiben, loeschen */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                  0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: auch Ordner */, IntPtr.Zero);
        if (h.IsInvalid) return voll;
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return voll;
        var echt = sb.ToString();
        if (echt.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + echt[8..];
        if (echt.StartsWith(@"\\?\", StringComparison.Ordinal)) return echt[4..];
        return echt;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint len, uint flags);
}
