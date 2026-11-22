using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsDaSiTool.Services;

/// <summary>Ein Wert aus der Registry des Benutzers (HKEY_CURRENT_USER).</summary>
/// <param name="Gruppe">Fuers Protokoll: "Farben", "Akzentfarbe", "Mauszeiger", "Taskleiste", "Start".</param>
/// <param name="Schluessel">Pfad unter HKEY_CURRENT_USER.</param>
/// <param name="Name">Name des Werts; "" ist der Standardwert.</param>
public sealed record DesignWert(string Gruppe, string Schluessel, string Name);

/// <summary>Ein gesicherter Wert.</summary>
public sealed class DesignEintrag
{
    public string Gruppe { get; set; } = "";
    public string Schluessel { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"DWord", "String", "ExpandString" oder "Binary".</summary>
    public string Typ { get; set; } = "";
    /// <summary>DWord als Zahl, Text wie er ist, Binary als Base64.</summary>
    public string Wert { get; set; } = "";
}

/// <summary>Die Datei Design\Design.json - Hintergrund und Einstellungen in einem.</summary>
public sealed class DesignSicherung
{
    public int Version { get; set; } = 2;
    public string Rechner { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>Bild oder Volltonfarbe; null, wenn es nichts Festes zu sichern gab (Diashow).</summary>
    public HintergrundEinstellung? Hintergrund { get; set; }
    public List<DesignEintrag> Werte { get; set; } = new();
}

/// <summary>
/// Die Design-Einstellungen von Windows (Personalisierung) - was ausser dem
/// Hintergrund dazugehoert: Hell/Dunkel fuer Windows und Apps, Transparenz,
/// Akzentfarbe, Mauszeiger, Taskleiste und Start.
///
/// Es sind Werte in HKEY_CURRENT_USER. Gesichert wird eine feste Liste - nicht
/// der ganze Zweig: Daneben liegen Zwischenstaende und rechnergebundene Werte
/// (Bildschirmpositionen, Kennungen), die auf einem anderen Rechner Schaden
/// anrichten. Werte, die Windows inzwischen schuetzt (etwa der Widgets-Knopf),
/// fehlen bewusst.
///
/// Ohne Abhaengigkeiten: Lesen und Schreiben der Registry werden hereingereicht,
/// damit sich alles ohne Windows pruefen laesst.
/// </summary>
public static class DesignEinstellungen
{
    public const string Ordner = "Design";
    public const string DateiName = "Design.json";

    /// <summary>
    /// So hiess die Datei in einer Zwischenfassung - damals nur die
    /// Einstellungen, der Hintergrund lag getrennt in "Hintergrundbild".
    /// Wird beim Wiederherstellen weiter gelesen.
    /// </summary>
    public const string AlterDateiName = "Design-Einstellungen.json";

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Cursors = @"Control Panel\Cursors";

    /// <summary>Die Mauszeiger eines Schemas - jeder Wert ist der Pfad einer .cur/.ani-Datei.</summary>
    private static readonly string[] Zeiger =
    {
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS",
        "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person",
    };

    /// <summary>Was gesichert wird.</summary>
    public static readonly IReadOnlyList<DesignWert> Katalog = BaueKatalog();

    private static List<DesignWert> BaueKatalog()
    {
        var k = new List<DesignWert>
        {
            // Einstellungen > Personalisierung > Farben
            new("Farben", Personalize, "SystemUsesLightTheme"),
            new("Farben", Personalize, "AppsUseLightTheme"),
            new("Farben", Personalize, "EnableTransparency"),
            new("Farben", Personalize, "ColorPrevalence"),
            // Akzentfarbe - Start/Taskleiste und Titelleisten
            new("Akzentfarbe", Accent, "AccentPalette"),
            new("Akzentfarbe", Accent, "AccentColorMenu"),
            new("Akzentfarbe", Accent, "StartColorMenu"),
            new("Akzentfarbe", Dwm, "AccentColor"),
            new("Akzentfarbe", Dwm, "AccentColorInactive"),
            new("Akzentfarbe", Dwm, "ColorizationColor"),
            new("Akzentfarbe", Dwm, "ColorizationAfterglow"),
            new("Akzentfarbe", Dwm, "ColorPrevalence"),
            new("Akzentfarbe", @"Control Panel\Desktop", "AutoColorization"),
            // Mauszeiger: Schema, Groesse, die einzelnen Zeiger
            new("Mauszeiger", Cursors, ""),
            new("Mauszeiger", Cursors, "Scheme Source"),
            new("Mauszeiger", Cursors, "CursorBaseSize"),
        };
        k.AddRange(Zeiger.Select(z => new DesignWert("Mauszeiger", Cursors, z)));
        k.AddRange(new DesignWert[]
        {
            // Einstellungen > Personalisierung > Taskleiste
            new("Taskleiste", Advanced, "TaskbarAl"),
            new("Taskleiste", Advanced, "ShowTaskViewButton"),
            new("Taskleiste", Advanced, "TaskbarGlomLevel"),
            new("Taskleiste", Advanced, "TaskbarSmallIcons"),
            new("Taskleiste", Advanced, "MMTaskbarEnabled"),
            new("Taskleiste", Advanced, "MMTaskbarMode"),
            new("Taskleiste", Advanced, "MMTaskbarGlomLevel"),
            new("Taskleiste", Search, "SearchboxTaskbarMode"),
            // Einstellungen > Personalisierung > Start
            new("Start", Advanced, "Start_Layout"),
            new("Start", Advanced, "Start_TrackDocs"),
            new("Start", Advanced, "Start_TrackProgs"),
            new("Start", Advanced, "Start_IrisRecommendations"),
        });
        return k;
    }

    /// <summary>
    /// Liest alle Werte des Katalogs. <paramref name="lesen"/> liefert zu
    /// Schluessel und Name (Typ, Wert) oder null, wenn es den Wert nicht gibt.
    /// </summary>
    public static List<DesignEintrag> Auslesen(Func<string, string, (string Typ, string Wert)?> lesen)
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
    /// Ein Mauszeiger, dessen Datei es hier nicht gibt (eigenes Schema vom
    /// alten Rechner)? Dann wird er ausgelassen - Windows zeigte sonst gar
    /// keinen Zeiger an dieser Stelle.
    /// </summary>
    /// <param name="dateiDa">Gibt es die Datei (nach Aufloesen von %SystemRoot% usw.)?</param>
    public static bool ZeigerFehlt(DesignEintrag e, Func<string, bool> dateiDa)
        => e.Gruppe == "Mauszeiger" && Zeiger.Contains(e.Name) && e.Typ is "String" or "ExpandString"
           && e.Wert.Trim().Length > 0 && !dateiDa(e.Wert.Trim());

    /// <summary>
    /// Kurzfassung fuers Protokoll, z. B. "Windows dunkel, Apps dunkel,
    /// Transparenz an, Akzentfarbe #0063B1, Mauszeiger "Windows-Standard",
    /// Taskleiste zentriert".
    /// </summary>
    public static string Zusammenfassung(IReadOnlyList<DesignEintrag> werte, Func<string, string, string> tr)
    {
        int? Zahl(string schluessel, string name)
        {
            var e = werte.FirstOrDefault(x => x.Schluessel == schluessel && x.Name == name && x.Typ == "DWord");
            return e is not null && long.TryParse(e.Wert, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? unchecked((int)l) : null;
        }
        string HellDunkel(int v) => v == 0 ? tr("dunkel", "dark") : tr("hell", "light");
        string AnAus(int v) => v == 0 ? tr("aus", "off") : tr("an", "on");

        var teile = new List<string>();
        if (Zahl(Personalize, "SystemUsesLightTheme") is { } sys) teile.Add(tr($"Windows {HellDunkel(sys)}", $"Windows {HellDunkel(sys)}"));
        if (Zahl(Personalize, "AppsUseLightTheme") is { } apps) teile.Add(tr($"Apps {HellDunkel(apps)}", $"apps {HellDunkel(apps)}"));
        if (Zahl(Personalize, "EnableTransparency") is { } tr1) teile.Add(tr($"Transparenz {AnAus(tr1)}", $"transparency {AnAus(tr1)}"));
        if (Zahl(@"Control Panel\Desktop", "AutoColorization") == 1)
            teile.Add(tr("Akzentfarbe automatisch", "accent color automatic"));
        else if (Zahl(Dwm, "AccentColor") is { } akzent)
            teile.Add(tr($"Akzentfarbe {AkzentHex(akzent)}", $"accent color {AkzentHex(akzent)}"));
        var schema = werte.FirstOrDefault(x => x.Schluessel == Cursors && x.Name == "")?.Wert;
        if (!string.IsNullOrWhiteSpace(schema)) teile.Add(tr($"Mauszeiger \"{schema}\"", $"pointer \"{schema}\""));
        if (Zahl(Advanced, "TaskbarAl") is { } al) teile.Add(al == 0 ? tr("Taskleiste links", "taskbar left") : tr("Taskleiste zentriert", "taskbar centered"));
        return string.Join(", ", teile);
    }

    /// <summary>DWM-Farbe 0xAABBGGRR -> "#RRGGBB".</summary>
    public static string AkzentHex(int abgr)
        => $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

    /// <summary>Welche Gruppen kommen vor - "Farben, Akzentfarbe, Mauszeiger ..."?</summary>
    public static string Gruppen(IEnumerable<DesignEintrag> werte)
        => string.Join(", ", werte.Select(w => w.Gruppe).Distinct());

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Datei(string sicherung) => Path.Combine(sicherung, Ordner, DateiName);

    public static void Speichern(string sicherung, DesignSicherung s)
    {
        Directory.CreateDirectory(Path.Combine(sicherung, Ordner));
        File.WriteAllText(Datei(sicherung), JsonSerializer.Serialize(s, Json));
    }

    /// <summary>
    /// Design.json - oder die Datei der Zwischenfassung. null, wenn es keine
    /// (lesbare) gibt oder nichts darin steht.
    /// </summary>
    public static DesignSicherung? Laden(string sicherung)
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
            catch { /* naechste */ }
        }
        return null;
    }

    /// <summary>Liegen Design-Einstellungen in der Sicherung?</summary>
    public static bool Vorhanden(string sicherung) => Laden(sicherung) is not null;
}
