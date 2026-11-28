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
/// Which profile Firefox (and Thunderbird) picks at startup.
///
/// Since Firefox 67, every installation has its own default profile. In
/// profiles.ini (and installs.ini) it is listed under "[Install&lt;hash&gt;]";
/// the hash is computed from the installation folder. One user had the
/// 32-bit Firefox in "C:\Program Files (x86)\Mozilla Firefox" on the old PC
/// (hash E7CF176E110C211B); on the new one winget installed the 64-bit
/// Firefox in "C:\Program Files\Mozilla Firefox" (308046B0AF4A39CB). The
/// restored profile belonged - locked ("Locked=1") - to the old
/// installation, Firefox found none for itself and created an empty
/// "default-release". The data was there, just not in the profile in use.
///
/// Remedy after restoring: register the profile that ran on the old PC as
/// the default for every installation on this PC.
///
/// Apart from finding the installations, free of dependencies, so it can be
/// tested.
/// </summary>
public static class MozillaProfile
{
    /// <summary>What <see cref="Adjust"/> did.</summary>
    /// <param name="Profil">Path of the profile as in profiles.ini (e.g. "Profiles/abcd1234.default"); null: none found.</param>
    /// <param name="Name">Name of the profile (e.g. "default").</param>
    /// <param name="AlteSchluessel">The installations from the backup (hashes).</param>
    /// <param name="Neu">The installations on this PC: folder and hash.</param>
    /// <param name="Verzeichnis">The profile's folder on disk.</param>
    public sealed record Ergebnis(string? Profil, string? Name, IReadOnlyList<string> AlteSchluessel, IReadOnlyList<(string Ordner, string Schluessel)> Neu,
                                  string? Verzeichnis = null)
    {
        /// <summary>Did the profile run on the old PC under one of this PC's installations?</summary>
        public bool SchonPassend => Neu.Count > 0 && Neu.All(n => AlteSchluessel.Contains(n.Schluessel, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The programs with Mozilla profile management: executable and usual installation folder.</summary>
    public static (string Exe, string Ordner)? ProgramOf(AppEintrag app) => app.Punkt switch
    {
        Punkt.Firefox => ("firefox.exe", "Mozilla Firefox"),
        Punkt.Thunderbird => ("thunderbird.exe", "Mozilla Thunderbird"),
        _ => null,
    };

    // ------------------------------------------------------------ Hash of an installation

    /// <summary>
    /// The hash of an installation as Firefox computes it: CityHash64 (version
    /// 1.0, as shipped in Firefox) over the folder path in UTF-16, hexadecimal
    /// in upper case without leading zeros.
    /// "C:\Program Files\Mozilla Firefox" -> "308046B0AF4A39CB".
    /// </summary>
    public static string InstallHash(string installationsOrdner)
        => CityHash64(Encoding.Unicode.GetBytes(installationsOrdner)).ToString("X");

    private const ulong K0 = 0xc3a5c85c97cb3127UL, K1 = 0xb492b66fbe98f273UL, K2 = 0x9ae16a3b2f90404fUL, K3 = 0xc949d7c7509e6557UL;

    private static ulong F64(byte[] s, int i) => BitConverter.ToUInt64(s, i);
    private static ulong F32(byte[] s, int i) => BitConverter.ToUInt32(s, i);
    private static ulong Rotate(ulong v, int n) => n == 0 ? v : (v >> n) | (v << (64 - n));
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong Hash16(ulong u, ulong v)
    {
        const ulong mul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * mul; a ^= a >> 47;
        ulong b = (v ^ a) * mul; b ^= b >> 47;
        return b * mul;
    }

    private static ulong Hash0To16(byte[] s, int n)
    {
        if (n > 8)
        {
            ulong a = F64(s, 0), b = F64(s, n - 8);
            return Hash16(a, Rotate(b + (ulong)n, n)) ^ b;
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

    private static ulong Hash17To32(byte[] s, int n)
    {
        ulong a = F64(s, 0) * K1, b = F64(s, 8), c = F64(s, n - 8) * K2, d = F64(s, n - 16) * K0;
        return Hash16(Rotate(a - b, 43) + Rotate(c, 30) + d, a + Rotate(b ^ K3, 20) - c + (ulong)n);
    }

    private static ulong Hash33To64(byte[] s, int n)
    {
        ulong z = F64(s, 24);
        ulong a = F64(s, 0) + ((ulong)n + F64(s, n - 16)) * K0;
        ulong b = Rotate(a + z, 52), c = Rotate(a, 37);
        a += F64(s, 8); c += Rotate(a, 7);
        a += F64(s, 16);
        ulong vf = a + z, vs = b + Rotate(a, 31) + c;
        a = F64(s, 16) + F64(s, n - 32);
        z = F64(s, n - 8);
        b = Rotate(a + z, 52); c = Rotate(a, 37);
        a += F64(s, n - 24); c += Rotate(a, 7);
        a += F64(s, n - 16);
        ulong wf = a + z, ws = b + Rotate(a, 31) + c;
        ulong r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
        return ShiftMix(r * K0 + vs) * K2;
    }

    private static (ulong, ulong) WeakHashLen32(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        ulong c = a;
        a += x; a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }

    private static (ulong, ulong) WeakHashLen32(byte[] s, int i, ulong a, ulong b)
        => WeakHashLen32(F64(s, i), F64(s, i + 8), F64(s, i + 16), F64(s, i + 24), a, b);

    internal static ulong CityHash64(byte[] s)
    {
        int n = s.Length;
        if (n <= 16) return Hash0To16(s, n);
        if (n <= 32) return Hash17To32(s, n);
        if (n <= 64) return Hash33To64(s, n);

        ulong x = F64(s, 0), y = F64(s, n - 16) ^ K1, z = F64(s, n - 56) ^ K0;
        var v = WeakHashLen32(s, n - 64, (ulong)n, y);
        var w = WeakHashLen32(s, n - 32, (ulong)n * K1, K0);
        z += ShiftMix(v.Item2) * K1;
        x = Rotate(z + x, 39) * K1;
        y = Rotate(y, 33) * K1;

        int rest = (n - 1) & ~63, i = 0;
        do
        {
            x = Rotate(x + y + v.Item1 + F64(s, i + 16), 37) * K1;
            y = Rotate(y + v.Item2 + F64(s, i + 48), 42) * K1;
            x ^= w.Item2;
            y ^= v.Item1;
            z = Rotate(z ^ w.Item1, 33);
            v = WeakHashLen32(s, i, v.Item2 * K1, x + w.Item1);
            w = WeakHashLen32(s, i + 32, z + w.Item2, y);
            (z, x) = (x, z);
            i += 64; rest -= 64;
        } while (rest != 0);
        return Hash16(Hash16(v.Item1, w.Item1) + ShiftMix(y) * K1 + z, Hash16(v.Item2, w.Item2) + x);
    }

    // ------------------------------------------------------------ profiles.ini

    /// <summary>An INI file the way Firefox writes it - the order is preserved.</summary>
    internal sealed class Ini
    {
        public List<(string Name, List<KeyValuePair<string, string>> Werte)> Abschnitte { get; } = new();

        public static Ini Read(string text)
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

        public string? Value(string abschnitt, string schluessel)
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

    // [Install308046B0AF4A39CB], [Profile0] - not [General], [BackgroundTasksProfiles] ...
    private static bool IsInstallSection(string abschnitt) => Regex.IsMatch(abschnitt, "^Install[0-9A-Fa-f]+$");
    private static bool IsProfileSection(string abschnitt) => Regex.IsMatch(abschnitt, "^Profile[0-9]+$");

    private static string? Value(List<KeyValuePair<string, string>> werte, string schluessel)
        => werte.FirstOrDefault(w => w.Key.Equals(schluessel, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The folder of a profile (relative to the data folder or absolute).</summary>
    private static string ProfileFolder(string datenOrdner, List<KeyValuePair<string, string>> profil)
    {
        var pfad = Value(profil, "Path") ?? "";
        return Value(profil, "IsRelative") == "0"
            ? pfad
            : Path.Combine(new[] { datenOrdner }.Concat(pfad.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)).ToArray());
    }

    /// <summary>Last used: Firefox writes prefs.js every time it exits.</summary>
    private static DateTime LastUsed(string ordner)
    {
        try
        {
            var prefs = Path.Combine(ordner, "prefs.js");
            return File.Exists(prefs) ? File.GetLastWriteTimeUtc(prefs) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// In the data folder (…\Mozilla\Firefox), registers the profile that ran on
    /// the old PC as the default for this PC's installations.
    ///
    /// Which profile ran: the one of an installation from the backup - the most
    /// recently used one if there are several. Without installation entries
    /// (before Firefox 67) the one with "Default=1", otherwise the most recently
    /// used one.
    ///
    /// The old PC's installation entries are removed, and every installation of
    /// this PC gets the profile - locked, as Firefox registers it itself. In
    /// addition the profile gets "Default=1": if no installation is found on
    /// this PC (or another one is added), Firefox takes this profile at first
    /// start instead of creating a new one.
    /// </summary>
    /// <param name="installationen">The installation folders on this PC (spelled as on disk).</param>
    public static Ergebnis Adjust(string datenOrdner, IEnumerable<string> installationen)
    {
        var neu = installationen.Select(o => (Ordner: o, Schluessel: InstallHash(o)))
                                .DistinctBy(n => n.Schluessel).ToList();
        var datei = Path.Combine(datenOrdner, "profiles.ini");
        if (!File.Exists(datei)) return new Ergebnis(null, null, Array.Empty<string>(), neu);

        var text = File.ReadAllText(datei);
        var zeilenende = text.Contains("\r\n") ? "\r\n" : "\n";
        var ini = Ini.Read(text);
        var profile = ini.Abschnitte.Where(a => IsProfileSection(a.Name)).ToList();
        var installs = ini.Abschnitte.Where(a => IsInstallSection(a.Name)).ToList();
        var alteSchluessel = installs.Select(a => a.Name["Install".Length..]).ToList();

        // Which profile ran on the old PC?
        var benutzt = installs.Select(a => Value(a.Werte, "Default")).OfType<string>()
            .Select(p => profile.FirstOrDefault(pr => string.Equals(Value(pr.Werte, "Path"), p, StringComparison.OrdinalIgnoreCase)))
            .Where(pr => pr.Werte is not null).ToList();
        if (benutzt.Count == 0)
            benutzt = profile.Where(pr => Value(pr.Werte, "Default") == "1").ToList();
        if (benutzt.Count == 0)
            benutzt = profile;
        var gewaehlt = benutzt.Where(pr => Directory.Exists(ProfileFolder(datenOrdner, pr.Werte)))
                              .OrderByDescending(pr => LastUsed(ProfileFolder(datenOrdner, pr.Werte)))
                              .FirstOrDefault();
        if (gewaehlt.Werte is null) return new Ergebnis(null, null, alteSchluessel, neu);
        var pfad = Value(gewaehlt.Werte, "Path")!;

        // Profiles: only the selected one is the default.
        foreach (var (_, werte) in profile)
        {
            werte.RemoveAll(w => w.Key.Equals("Default", StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(werte, gewaehlt.Werte)) werte.Add(new("Default", "1"));
        }

        // Installations: remove the old PC's, add this PC's at the front.
        ini.Abschnitte.RemoveAll(a => IsInstallSection(a.Name));
        var eintraege = neu.Select(n => ($"Install{n.Schluessel}", new List<KeyValuePair<string, string>> { new("Default", pfad), new("Locked", "1") })).ToList();
        ini.Abschnitte.InsertRange(0, eintraege);
        Write(datei, ini.Text(zeilenende));

        // installs.ini: the same installations, there just with the hash as the
        // name ("[308046B0AF4A39CB]"). A Firefox that reads profiles.ini without
        // "Version=2" (before Firefox 69) takes them from there.
        var installsIni = Path.Combine(datenOrdner, "installs.ini");
        if (eintraege.Count > 0)
        {
            var nurInstalls = new Ini();
            nurInstalls.Abschnitte.AddRange(eintraege.Select(e => (e.Item1["Install".Length..], e.Item2.ToList())));
            Write(installsIni, nurInstalls.Text(zeilenende));
        }
        else if (File.Exists(installsIni))
        {
            File.Delete(installsIni);
        }

        return new Ergebnis(pfad, Value(gewaehlt.Werte, "Name"), alteSchluessel, neu, ProfileFolder(datenOrdner, gewaehlt.Werte));
    }

    /// <summary>The profile folders profiles.ini names and that exist.</summary>
    public static List<string> ProfileFolders(string datenOrdner)
    {
        var datei = Path.Combine(datenOrdner, "profiles.ini");
        if (!File.Exists(datei)) return new List<string>();
        return Ini.Read(File.ReadAllText(datei)).Abschnitte.Where(a => IsProfileSection(a.Name))
                  .Select(a => ProfileFolder(datenOrdner, a.Werte)).Where(Directory.Exists)
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------ Versions (protection against older versions)

    /// <summary>How far the profile is ahead of the installed version.</summary>
    public enum Abstand
    {
        /// <summary>Same age or older - Firefox accepts the profile.</summary>
        Passt,
        /// <summary>Same major version, but newer (146.0.1 instead of 146.0) - Firefox accepts it (it only compares the major version).</summary>
        Neuer,
        /// <summary>Newer major version (147 instead of 146) - Firefox rejects the profile and creates a new one.</summary>
        VielNeuer,
    }

    /// <summary>
    /// "146.0.1_20261120123456/20261120123456" (compatibility.ini, LastVersion)
    /// or version and BuildID from application.ini -> (version, BuildID).
    /// </summary>
    public static (string Version, string BuildId)? LastVersion(string? lastVersion)
    {
        if (string.IsNullOrWhiteSpace(lastVersion)) return null;
        var teil = lastVersion.Split('/')[0];
        var strich = teil.IndexOf('_');
        return strich > 0 ? (teil[..strich], teil[(strich + 1)..]) : (teil, "");
    }

    /// <summary>The numbers at the start of each part: "147.0b3" -> 147, 0; "128.5.0esr" -> 128, 5, 0.</summary>
    private static List<long> Numbers(string version)
        => version.Split('.').Select(t => new string(t.TakeWhile(char.IsDigit).ToArray()))
                  .Select(t => long.TryParse(t, out var z) ? z : 0).ToList();

    /// <summary>
    /// Compares the version the profile last ran with against the installed
    /// one - like Firefox at startup: first the version, if equal the BuildID.
    /// </summary>
    public static Abstand Compare((string Version, string BuildId) profil, (string Version, string BuildId) installiert)
    {
        var p = Numbers(profil.Version);
        var i = Numbers(installiert.Version);
        for (int n = 0; n < Math.Max(p.Count, i.Count); n++)
        {
            var a = n < p.Count ? p[n] : 0;
            var b = n < i.Count ? i[n] : 0;
            if (a == b) continue;
            if (a < b) return Abstand.Passt;
            return n == 0 ? Abstand.VielNeuer : Abstand.Neuer;
        }
        // A pre-release (147.0b3) against the final one (147.0): the final one is newer.
        var pVorab = profil.Version.Any(char.IsLetter) && !profil.Version.EndsWith("esr", StringComparison.OrdinalIgnoreCase);
        var iVorab = installiert.Version.Any(char.IsLetter) && !installiert.Version.EndsWith("esr", StringComparison.OrdinalIgnoreCase);
        if (pVorab != iVorab) return pVorab ? Abstand.Passt : Abstand.Neuer;
        return string.CompareOrdinal(profil.BuildId, installiert.BuildId) > 0 ? Abstand.Neuer : Abstand.Passt;
    }

    /// <summary>The version the profile last ran with (compatibility.ini) - null if unknown.</summary>
    public static (string Version, string BuildId)? ProfileVersion(string profilOrdner)
    {
        var datei = Path.Combine(profilOrdner, "compatibility.ini");
        try { return File.Exists(datei) ? LastVersion(Ini.Read(File.ReadAllText(datei)).Value("Compatibility", "LastVersion")) : null; }
        catch { return null; }
    }

    /// <summary>The installed version (application.ini in the installation folder) - null if unknown.</summary>
    public static (string Version, string BuildId)? InstalledVersion(string installationsOrdner)
    {
        var datei = Path.Combine(installationsOrdner, "application.ini");
        try
        {
            if (!File.Exists(datei)) return null;
            var ini = Ini.Read(File.ReadAllText(datei));
            var version = ini.Value("App", "Version");
            return string.IsNullOrWhiteSpace(version) ? null : (version, ini.Value("App", "BuildID") ?? "");
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ Paths of the old user

    // In prefs.js and extensions.json every backslash is written as "\\" - so
    // four in the pattern: "C:\\Users\\Besitzer\\AppData\\Roaming\\...".
    private static readonly Regex AlterBenutzer = new(@"(?<![A-Za-z])([A-Za-z]:(?:\\\\[^\\""\r\n]+?)+?)\\\\AppData\\\\(?:Roaming|Local)\\\\", RegexOptions.IgnoreCase);

    /// <summary>
    /// The old PC's user folder ("C:\Users\Besitzer"), read from paths in
    /// prefs.js or extensions.json that point into AppData - null if there are
    /// none.
    /// </summary>
    public static string? OldUserFolder(string profilOrdner)
    {
        var funde = new List<string>();
        foreach (var name in new[] { "prefs.js", "extensions.json" })
        {
            var datei = Path.Combine(profilOrdner, name);
            try
            {
                if (!File.Exists(datei)) continue;
                foreach (Match m in AlterBenutzer.Matches(File.ReadAllText(datei)))
                    funde.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { /* next one */ }
        }
        return funde.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    /// <summary>
    /// Rewrites the old user's paths in prefs.js to the new one - Thunderbird's
    /// mail folders, the download folder, pages from the profile
    /// (file:///C:/Users/...). Otherwise they point to a folder that does not
    /// exist here.
    /// </summary>
    /// <returns>How many entries were rewritten.</returns>
    public static int RewritePaths(string profilOrdner, string alt, string neu)
    {
        var datei = Path.Combine(profilOrdner, "prefs.js");
        // Windows paths (also from the old PC) - regardless of casing and trailing separator.
        if (!File.Exists(datei) || string.Equals(alt.TrimEnd('\\', '/'), neu.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return 0;
        string Escaped(string pfad) => pfad.TrimEnd('\\').Replace(@"\", @"\\");
        string Url(string pfad) => "file:///" + pfad.TrimEnd('\\').Replace('\\', '/');
        var ersetzen = new[]
        {
            (Alt: Escaped(alt), Neu: Escaped(neu)),
            (Alt: Url(alt), Neu: Url(neu)),
        };
        int anzahl = 0;
        var zeilen = File.ReadAllLines(datei);
        for (int z = 0; z < zeilen.Length; z++)
        {
            if (!zeilen[z].StartsWith("user_pref(", StringComparison.Ordinal)) continue;
            foreach (var (a, n) in ersetzen)
            {
                // Whole folder names only: "C:\Users\Max" not in "C:\Users\Maxi".
                var muster = Regex.Escape(a) + @"(?=\\\\|/|"")";
                var neuZeile = Regex.Replace(zeilen[z], muster, n.Replace("$", "$$"), RegexOptions.IgnoreCase);
                if (neuZeile != zeilen[z]) { zeilen[z] = neuZeile; anzahl++; break; }
            }
        }
        if (anzahl > 0) Write(datei, string.Join("\n", zeilen) + "\n");
        return anzahl;
    }

    // ------------------------------------------------------------ Thunderbird: mail outside the profile

    /// <summary>An account whose mail lives outside the profile folder.</summary>
    public sealed record Postfach(string Server, string Ordner);

    private static readonly Regex MailOrdner = new(@"^user_pref\(""mail\.server\.(server\d+)\.directory"",\s*""(.*)""\);", RegexOptions.IgnoreCase);

    /// <summary>
    /// The accounts whose folder (mail.server.serverN.directory) is not inside
    /// the profile folder - moved in Thunderbird under Account Settings >
    /// Server Settings > "Local Directory".
    /// </summary>
    public static List<Postfach> MailFoldersOutside(string profilOrdner)
    {
        var erg = new List<Postfach>();
        var datei = Path.Combine(profilOrdner, "prefs.js");
        if (!File.Exists(datei)) return erg;
        var profil = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilOrdner));
        foreach (var zeile in File.ReadAllLines(datei))
        {
            var m = MailOrdner.Match(zeile);
            if (!m.Success) continue;
            var ordner = Regex.Unescape(m.Groups[2].Value);
            if (ordner.Length < 3 || ordner[1] != ':') continue;
            var normal = ordner.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (normal.StartsWith(profil + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            erg.Add(new Postfach(m.Groups[1].Value, ordner));
        }
        return erg;
    }

    /// <summary>
    /// Registers a new folder for an account (absolute and, if inside the
    /// profile, relative "[ProfD]...") - after its folder was moved.
    /// </summary>
    public static bool MoveMailFolder(string profilOrdner, string server, string neuerOrdner)
    {
        var datei = Path.Combine(profilOrdner, "prefs.js");
        if (!File.Exists(datei)) return false;
        var profil = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilOrdner));
        string? relativ = neuerOrdner.StartsWith(profil + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "[ProfD]" + neuerOrdner[(profil.Length + 1)..].Replace('\\', '/') : null;
        var zeilen = File.ReadAllLines(datei).Where(z => !z.StartsWith($"user_pref(\"mail.server.{server}.directory-rel\"", StringComparison.OrdinalIgnoreCase)).ToList();
        var abs = $"user_pref(\"mail.server.{server}.directory\", \"{neuerOrdner.Replace(@"\", @"\\")}\");";
        var i = zeilen.FindIndex(z => z.StartsWith($"user_pref(\"mail.server.{server}.directory\"", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        zeilen[i] = abs;
        if (relativ is not null) zeilen.Insert(i + 1, $"user_pref(\"mail.server.{server}.directory-rel\", \"{relativ}\");");
        Write(datei, string.Join("\n", zeilen) + "\n");
        return true;
    }

    private static void Write(string datei, string inhalt)
    {
        // Through a temporary file: if writing is interrupted, the old file stays.
        var tmp = datei + ".dasi";
        File.WriteAllText(tmp, inhalt, new UTF8Encoding(false));
        File.Move(tmp, datei, overwrite: true);
    }

    /// <summary>Where an installation was according to its hash - as far as it is a usual location.</summary>
    public static string? KnownLocation(string schluessel, string ordnerName, string profil)
    {
        foreach (var ort in UsualLocations(ordnerName, profil))
            if (InstallHash(ort).Equals(schluessel, StringComparison.OrdinalIgnoreCase)) return ort;
        return null;
    }

    /// <summary>
    /// The usual installation folders: for all users (64 and 32 bit) and for
    /// the user only (AppData\Local). Fixed drive letters, as on the old PC -
    /// regardless of where this PC puts programs.
    /// </summary>
    internal static IEnumerable<string> UsualLocations(string ordnerName, string profil)
    {
        yield return $@"C:\Program Files\{ordnerName}";
        yield return $@"C:\Program Files (x86)\{ordnerName}";
        yield return Path.Combine(profil, "AppData", "Local", ordnerName);
    }

    // ------------------------------------------------------------ Installations on this PC

    /// <summary>
    /// The program's installation folders on this PC: the usual locations and
    /// those Windows lists under "App Paths" - each only if the executable is
    /// there. Spelled as on disk, because that is what Firefox computes the
    /// hash from.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static List<string> Installations(string exe, string ordnerName, string profil)
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
            catch { /* next one */ }
        }
        return orte.Where(o => o.Length > 0 && File.Exists(Path.Combine(o, exe)))
                   .Select(RealPath)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>
    /// The path as it is on disk - casing and links resolved. Firefox does the
    /// same before computing the hash.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string RealPath(string ordner)
    {
        var voll = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ordner));
        using var h = CreateFileW(voll, 0, 7 /* read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                  0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: folders as well */, IntPtr.Zero);
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
