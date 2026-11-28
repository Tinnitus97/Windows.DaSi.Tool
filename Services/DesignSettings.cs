using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsDaSiTool.Services;

/// <summary>A value from the user's registry (HKEY_CURRENT_USER).</summary>
/// <param name="Gruppe">For the log: "Farben", "Akzentfarbe", "Mauszeiger", "Taskleiste", "Start".</param>
/// <param name="Schluessel">Path under HKEY_CURRENT_USER.</param>
/// <param name="Name">Name of the value; "" is the default value.</param>
public sealed record DesignWert(string Gruppe, string Schluessel, string Name);

/// <summary>A backed-up value.</summary>
public sealed class DesignEintrag
{
    public string Gruppe { get; set; } = "";
    public string Schluessel { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"DWord", "String", "ExpandString" or "Binary".</summary>
    public string Typ { get; set; } = "";
    /// <summary>DWord as a number, text as is, binary as Base64.</summary>
    public string Wert { get; set; } = "";
}

/// <summary>The file Design\Design.json - wallpaper and settings in one.</summary>
public sealed class DesignSicherung
{
    public int Version { get; set; } = 2;
    public string Rechner { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>Image or solid color; null if there was nothing fixed to back up (slideshow).</summary>
    public HintergrundEinstellung? Hintergrund { get; set; }
    public List<DesignEintrag> Werte { get; set; } = new();
}

/// <summary>
/// Windows' design settings (Personalization) - what belongs to it besides
/// the wallpaper: light/dark for Windows and apps, transparency, accent
/// color, mouse pointers, taskbar and Start.
///
/// These are values in HKEY_CURRENT_USER. A fixed list is backed up - not
/// the whole key: next to them live interim states and machine-bound values
/// (screen positions, IDs) that do harm on another computer. Values that
/// Windows protects by now (such as the Widgets button) are deliberately
/// missing.
///
/// No dependencies: reading and writing the registry are passed in, so
/// everything can be tested without Windows.
/// </summary>
public static class DesignSettings
{
    public const string Ordner = "Design";
    public const string DateiName = "Design.json";

    /// <summary>
    /// The file's name in an interim version - back then only the settings;
    /// the wallpaper was kept separately in "Hintergrundbild". Still read on
    /// restore.
    /// </summary>
    public const string AlterDateiName = "Design-Einstellungen.json";

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Cursors = @"Control Panel\Cursors";

    /// <summary>The mouse pointers of a scheme - each value is the path of a .cur/.ani file.</summary>
    private static readonly string[] Zeiger =
    {
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS",
        "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person",
    };

    /// <summary>What is backed up.</summary>
    public static readonly IReadOnlyList<DesignWert> Katalog = BuildCatalog();

    private static List<DesignWert> BuildCatalog()
    {
        var k = new List<DesignWert>
        {
            // Settings > Personalization > Colors
            new("Farben", Personalize, "SystemUsesLightTheme"),
            new("Farben", Personalize, "AppsUseLightTheme"),
            new("Farben", Personalize, "EnableTransparency"),
            new("Farben", Personalize, "ColorPrevalence"),
            // Accent color - Start/taskbar and title bars
            new("Akzentfarbe", Accent, "AccentPalette"),
            new("Akzentfarbe", Accent, "AccentColorMenu"),
            new("Akzentfarbe", Accent, "StartColorMenu"),
            new("Akzentfarbe", Dwm, "AccentColor"),
            new("Akzentfarbe", Dwm, "AccentColorInactive"),
            new("Akzentfarbe", Dwm, "ColorizationColor"),
            new("Akzentfarbe", Dwm, "ColorizationAfterglow"),
            new("Akzentfarbe", Dwm, "ColorPrevalence"),
            new("Akzentfarbe", @"Control Panel\Desktop", "AutoColorization"),
            // Mouse pointers: scheme, size, the individual pointers
            new("Mauszeiger", Cursors, ""),
            new("Mauszeiger", Cursors, "Scheme Source"),
            new("Mauszeiger", Cursors, "CursorBaseSize"),
        };
        k.AddRange(Zeiger.Select(z => new DesignWert("Mauszeiger", Cursors, z)));
        k.AddRange(new DesignWert[]
        {
            // Settings > Personalization > Taskbar
            new("Taskleiste", Advanced, "TaskbarAl"),
            new("Taskleiste", Advanced, "ShowTaskViewButton"),
            new("Taskleiste", Advanced, "TaskbarGlomLevel"),
            new("Taskleiste", Advanced, "TaskbarSmallIcons"),
            new("Taskleiste", Advanced, "MMTaskbarEnabled"),
            new("Taskleiste", Advanced, "MMTaskbarMode"),
            new("Taskleiste", Advanced, "MMTaskbarGlomLevel"),
            new("Taskleiste", Search, "SearchboxTaskbarMode"),
            // Settings > Personalization > Start
            new("Start", Advanced, "Start_Layout"),
            new("Start", Advanced, "Start_TrackDocs"),
            new("Start", Advanced, "Start_TrackProgs"),
            new("Start", Advanced, "Start_IrisRecommendations"),
        });
        return k;
    }

    /// <summary>
    /// Reads all values of the catalog. <paramref name="lesen"/> returns
    /// (type, value) for a key and name, or null if the value does not exist.
    /// </summary>
    public static List<DesignEintrag> ReadAll(Func<string, string, (string Typ, string Wert)?> lesen)
    {
        var erg = new List<DesignEintrag>();
        foreach (var w in Katalog)
        {
            (string Typ, string Wert)? v;
            try { v = lesen(w.Schluessel, w.Name); } catch { v = null; }
            if (v is null) continue;
            erg.Add(new DesignEintrag { Gruppe = w.Gruppe, Schluessel = w.Schluessel, Name = w.Name, Typ = v.Value.Typ, Wert = v.Value.Wert });
        }
        return erg;
    }

    /// <summary>
    /// A mouse pointer whose file does not exist here (a custom scheme from the
    /// old computer)? Then it is skipped - otherwise Windows would show no
    /// pointer at all in that place.
    /// </summary>
    /// <param name="dateiDa">Does the file exist (after expanding %SystemRoot% etc.)?</param>
    public static bool PointerMissing(DesignEintrag e, Func<string, bool> dateiDa)
        => e.Gruppe == "Mauszeiger" && Zeiger.Contains(e.Name) && e.Typ is "String" or "ExpandString"
           && e.Wert.Trim().Length > 0 && !dateiDa(e.Wert.Trim());

    /// <summary>
    /// Short version for the log, e.g. "Windows dunkel, Apps dunkel,
    /// Transparenz an, Akzentfarbe #0063B1, Mauszeiger "Windows-Standard",
    /// Taskleiste zentriert" (in the UI language).
    /// </summary>
    public static string Summary(IReadOnlyList<DesignEintrag> werte, Func<string, string, string> tr)
    {
        int? Number(string schluessel, string name)
        {
            var e = werte.FirstOrDefault(x => x.Schluessel == schluessel && x.Name == name && x.Typ == "DWord");
            return e is not null && long.TryParse(e.Wert, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? unchecked((int)l) : null;
        }
        string LightDark(int v) => v == 0 ? tr("dunkel", "dark") : tr("hell", "light");
        string OnOff(int v) => v == 0 ? tr("aus", "off") : tr("an", "on");

        var teile = new List<string>();
        if (Number(Personalize, "SystemUsesLightTheme") is { } sys) teile.Add(tr($"Windows {LightDark(sys)}", $"Windows {LightDark(sys)}"));
        if (Number(Personalize, "AppsUseLightTheme") is { } apps) teile.Add(tr($"Apps {LightDark(apps)}", $"apps {LightDark(apps)}"));
        if (Number(Personalize, "EnableTransparency") is { } tr1) teile.Add(tr($"Transparenz {OnOff(tr1)}", $"transparency {OnOff(tr1)}"));
        if (Number(@"Control Panel\Desktop", "AutoColorization") == 1)
            teile.Add(tr("Akzentfarbe automatisch", "accent color automatic"));
        else if (Number(Dwm, "AccentColor") is { } akzent)
            teile.Add(tr($"Akzentfarbe {AccentHex(akzent)}", $"accent color {AccentHex(akzent)}"));
        var schema = werte.FirstOrDefault(x => x.Schluessel == Cursors && x.Name == "")?.Wert;
        if (!string.IsNullOrWhiteSpace(schema)) teile.Add(tr($"Mauszeiger \"{schema}\"", $"pointer \"{schema}\""));
        if (Number(Advanced, "TaskbarAl") is { } al) teile.Add(al == 0 ? tr("Taskleiste links", "taskbar left") : tr("Taskleiste zentriert", "taskbar centered"));
        return string.Join(", ", teile);
    }

    /// <summary>DWM color 0xAABBGGRR -> "#RRGGBB".</summary>
    public static string AccentHex(int abgr)
        => $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

    /// <summary>Which groups occur - "Farben, Akzentfarbe, Mauszeiger ..."?</summary>
    public static string Groups(IEnumerable<DesignEintrag> werte)
        => string.Join(", ", werte.Select(w => w.Gruppe).Distinct());

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath(string sicherung) => Path.Combine(sicherung, Ordner, DateiName);

    public static void Save(string sicherung, DesignSicherung s)
    {
        Directory.CreateDirectory(Path.Combine(sicherung, Ordner));
        File.WriteAllText(FilePath(sicherung), JsonSerializer.Serialize(s, Json));
    }

    /// <summary>
    /// Design.json - or the interim version's file. null if there is no
    /// (readable) one or it is empty.
    /// </summary>
    public static DesignSicherung? Load(string sicherung)
    {
        foreach (var name in new[] { DateiName, AlterDateiName })
        {
            try
            {
                var d = Path.Combine(sicherung, Ordner, name);
                if (!File.Exists(d)) continue;
                var s = JsonSerializer.Deserialize<DesignSicherung>(File.ReadAllText(d));
                if (s is not null && (s.Werte.Count > 0 || s.Hintergrund is not null)) return s;
            }
            catch { /* next one */ }
        }
        return null;
    }

    /// <summary>Are there design settings in the backup?</summary>
    public static bool Exists(string sicherung) => Load(sicherung) is not null;
}
