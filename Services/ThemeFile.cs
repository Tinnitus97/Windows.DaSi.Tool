using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Windows' theme file (".theme", usually
/// %LOCALAPPDATA%\Microsoft\Windows\Themes\Custom.theme) - an INI file.
///
/// Why the tool touches it: at sign-in, Windows copies parts of it back into
/// the registry - on Windows 11 demonstrably the background color
/// ([Control Panel\Colors] Background). The Settings app therefore records
/// every change here as well. The tool used to write only the registry:
/// after the next restart the solid color was the old one again (black).
/// Now it records the wallpaper and the design settings here as well.
///
/// No dependencies, so it can be tested.
/// </summary>
public static class ThemeFile
{
    public const string FarbenAbschnitt = @"Control Panel\Colors";
    public const string DesktopAbschnitt = @"Control Panel\Desktop";
    public const string ZeigerAbschnitt = @"Control Panel\Cursors";
    public const string StilAbschnitt = "VisualStyles";
    public const string DiashowAbschnitt = "Slideshow";

    // ------------------------------------------------------------ Content

    /// <summary>
    /// Solid color: only record the color, no slideshow. The image stays - the
    /// Settings app does the same (whether image or color is shown is decided
    /// by BackgroundType in the registry). Windows 11 reads an empty
    /// "Wallpaper=" as "Windows Spotlight" when applying the theme.
    /// </summary>
    public static string ForColor(string inhalt, (byte R, byte G, byte B) farbe)
        => RemoveSection(Set(inhalt, FarbenAbschnitt, "Background", Wallpaper.ColorText(farbe)), DiashowAbschnitt);

    /// <summary>Image: record path, fit mode and edge color, no slideshow.</summary>
    public static string ForImage(string inhalt, string pfad, HintergrundEinstellung? e)
    {
        var t = Set(inhalt, DesktopAbschnitt, "Wallpaper", pfad);
        if (!string.IsNullOrWhiteSpace(e?.Darstellung)) t = Set(t, DesktopAbschnitt, "WallpaperStyle", e.Darstellung);
        if (!string.IsNullOrWhiteSpace(e?.Kacheln)) t = Set(t, DesktopAbschnitt, "TileWallpaper", e.Kacheln);
        if (Wallpaper.ReadColor(e?.Farbe) is { } rand) t = Set(t, FarbenAbschnitt, "Background", Wallpaper.ColorText(rand));
        return RemoveSection(t, DiashowAbschnitt);
    }

    /// <summary>
    /// The design settings that were set: light/dark, accent color,
    /// "automatic", mouse pointers. Only keys the file already has - what
    /// Windows does not keep there, it does not reset at sign-in either.
    /// </summary>
    public static string ForDesign(string inhalt, IEnumerable<DesignEintrag> werte)
    {
        var t = inhalt;
        foreach (var e in werte)
        {
            var name = e.Name;
            if (e.Schluessel.EndsWith(@"Themes\Personalize", StringComparison.OrdinalIgnoreCase) && e.Typ == "DWord")
            {
                var modus = e.Wert == "0" ? "Dark" : "Light";
                if (name == "SystemUsesLightTheme") t = OnlyIfPresent(t, StilAbschnitt, "SystemMode", modus);
                else if (name == "AppsUseLightTheme") t = OnlyIfPresent(t, StilAbschnitt, "AppMode", modus);
            }
            else if (e.Schluessel.EndsWith(@"Windows\DWM", StringComparison.OrdinalIgnoreCase) && name == "ColorizationColor"
                     && int.TryParse(e.Wert, NumberStyles.Integer, CultureInfo.InvariantCulture, out var farbe))
            {
                t = OnlyIfPresent(t, StilAbschnitt, "ColorizationColor", "0X" + unchecked((uint)farbe).ToString("X8", CultureInfo.InvariantCulture));
            }
            else if (string.Equals(e.Schluessel, @"Control Panel\Desktop", StringComparison.OrdinalIgnoreCase) && name == "AutoColorization")
            {
                t = OnlyIfPresent(t, StilAbschnitt, "AutoColorization", e.Wert);
            }
            else if (string.Equals(e.Schluessel, @"Control Panel\Cursors", StringComparison.OrdinalIgnoreCase)
                     && e.Typ is "String" or "ExpandString")
            {
                // The unnamed value (scheme name) is called "DefaultValue" in the file.
                t = OnlyIfPresent(t, ZeigerAbschnitt, name.Length == 0 ? "DefaultValue" : name, e.Wert);
            }
        }
        return t;
    }

    /// <summary>If the file has no (usable) image for a solid color, this one - like the Settings app.</summary>
    public const string Ersatzbild = @"%SystemRoot%\web\wallpaper\Windows\img0.jpg";

    /// <summary>
    /// The theme file Windows should apply on restore: the user's current one
    /// as a template, with the wallpaper and design settings from the backup.
    ///
    /// For a solid color an image stays registered - that is how the Settings
    /// app does it: whether image or color is shown is decided by BackgroundType
    /// in the registry, and the tool switches it afterwards. Windows 11 reads an
    /// empty "Wallpaper=" or a Spotlight image as "Spotlight" when applying the
    /// theme (verified on Windows 11 25H2).
    /// </summary>
    public static string ForRestore(string vorlage, string anzeigename, (byte R, byte G, byte B)? farbe,
                                               string? bild, HintergrundEinstellung? e, IEnumerable<DesignEintrag> werte)
    {
        var t = ForApplying(vorlage, anzeigename);
        if (farbe is { } f)
        {
            t = ForColor(t, f);
            var b = Read(t, DesktopAbschnitt, "Wallpaper");
            if (string.IsNullOrWhiteSpace(b) || b.Contains("DesktopSpotlight", StringComparison.OrdinalIgnoreCase))
                t = Set(t, DesktopAbschnitt, "Wallpaper", Ersatzbild);
        }
        else if (bild is not null)
        {
            t = ForImage(t, bild, e);
        }
        return ForDesign(t, werte);
    }

    /// <summary>
    /// Turns the current theme file into one of our own for Windows to apply:
    /// its own display name, without the template's ID (ThemeId) - otherwise
    /// Windows would consider it the same theme.
    /// </summary>
    public static string ForApplying(string vorlage, string anzeigename)
    {
        var t = Set(vorlage, "Theme", "DisplayName", anzeigename);
        return RemoveKey(t, "Theme", "ThemeId");
    }

    // ------------------------------------------------------------ INI

    /// <summary>Value of a key - null if the section or the key does not exist.</summary>
    public static string? Read(string inhalt, string abschnitt, string schluessel)
    {
        var (zeilen, _, _) = SplitLines(inhalt);
        var (von, bis) = Section(zeilen, abschnitt);
        if (von < 0) return null;
        int i = Line(zeilen, von, bis, schluessel);
        return i < 0 ? null : zeilen[i][(zeilen[i].IndexOf('=') + 1)..].Trim();
    }

    /// <summary>Sets a key - creates it (and the section) if needed.</summary>
    public static string Set(string inhalt, string abschnitt, string schluessel, string wert)
    {
        var (zeilen, umbruch, schlussUmbruch) = SplitLines(inhalt);
        var (von, bis) = Section(zeilen, abschnitt);
        if (von < 0)
        {
            while (zeilen.Count > 0 && zeilen[^1].Trim().Length == 0) zeilen.RemoveAt(zeilen.Count - 1);
            if (zeilen.Count > 0) zeilen.Add("");
            zeilen.Add($"[{abschnitt}]");
            zeilen.Add($"{schluessel}={wert}");
            return JoinLines(zeilen, umbruch, true);
        }
        int i = Line(zeilen, von, bis, schluessel);
        if (i >= 0)
        {
            zeilen[i] = $"{KeyAsBefore(zeilen[i])}={wert}";
        }
        else
        {
            // After the last non-empty line of the section.
            int pos = bis;
            while (pos > von + 1 && zeilen[pos - 1].Trim().Length == 0) pos--;
            zeilen.Insert(pos, $"{schluessel}={wert}");
        }
        return JoinLines(zeilen, umbruch, schlussUmbruch);

        static string KeyAsBefore(string zeile) => zeile[..zeile.IndexOf('=')].Trim();
    }

    /// <summary>Sets a key only if it already exists.</summary>
    public static string OnlyIfPresent(string inhalt, string abschnitt, string schluessel, string wert)
        => Read(inhalt, abschnitt, schluessel) is null ? inhalt : Set(inhalt, abschnitt, schluessel, wert);

    /// <summary>Removes a key if it exists.</summary>
    public static string RemoveKey(string inhalt, string abschnitt, string schluessel)
    {
        var (zeilen, umbruch, schlussUmbruch) = SplitLines(inhalt);
        var (von, bis) = Section(zeilen, abschnitt);
        if (von < 0) return inhalt;
        int i = Line(zeilen, von, bis, schluessel);
        if (i < 0) return inhalt;
        zeilen.RemoveAt(i);
        return JoinLines(zeilen, umbruch, schlussUmbruch);
    }

    /// <summary>Removes a whole section including its header line.</summary>
    public static string RemoveSection(string inhalt, string abschnitt)
    {
        var (zeilen, umbruch, schlussUmbruch) = SplitLines(inhalt);
        var (von, bis) = Section(zeilen, abschnitt);
        if (von < 0) return inhalt;
        zeilen.RemoveRange(von, bis - von);
        return JoinLines(zeilen, umbruch, schlussUmbruch);
    }

    private static (List<string> Zeilen, string Umbruch, bool SchlussUmbruch) SplitLines(string inhalt)
    {
        var umbruch = inhalt.Contains("\r\n") ? "\r\n" : "\n";
        bool schluss = inhalt.EndsWith('\n');
        var zeilen = inhalt.Replace("\r\n", "\n").Split('\n').ToList();
        if (schluss) zeilen.RemoveAt(zeilen.Count - 1);
        return (zeilen, umbruch, schluss);
    }

    private static string JoinLines(List<string> zeilen, string umbruch, bool schlussUmbruch)
        => string.Join(umbruch, zeilen) + (schlussUmbruch ? umbruch : "");

    /// <summary>[from, to): header line up to the next section; (-1, -1) if the section does not exist.</summary>
    private static (int Von, int Bis) Section(List<string> zeilen, string abschnitt)
    {
        int von = zeilen.FindIndex(z => string.Equals(z.Trim(), $"[{abschnitt}]", StringComparison.OrdinalIgnoreCase));
        if (von < 0) return (-1, -1);
        int bis = von + 1;
        while (bis < zeilen.Count && !zeilen[bis].TrimStart().StartsWith('[')) bis++;
        return (von, bis);
    }

    private static int Line(List<string> zeilen, int von, int bis, string schluessel)
    {
        for (int i = von + 1; i < bis; i++)
        {
            int gleich = zeilen[i].IndexOf('=');
            if (gleich > 0 && string.Equals(zeilen[i][..gleich].Trim(), schluessel, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------ File

    /// <summary>
    /// Reads the file together with its encoding: Windows usually writes theme
    /// files as UTF-16 with BOM, older or hand-made ones also as ANSI.
    /// </summary>
    public static (string Inhalt, Encoding Kodierung) ReadFile(string pfad)
    {
        var bytes = File.ReadAllBytes(pfad);
        Encoding kodierung;
        int bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { kodierung = new UnicodeEncoding(false, true); bom = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { kodierung = new UnicodeEncoding(true, true); bom = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { kodierung = new UTF8Encoding(true); bom = 3; }
        else { kodierung = bytes.All(b => b < 0x80) ? Encoding.ASCII : Ansi(); bom = 0; }
        return (kodierung.GetString(bytes, bom, bytes.Length - bom), kodierung);
    }

    /// <summary>
    /// Writes back in the same encoding - through a temporary file, so an abort
    /// never leaves half a theme file. If a new value does not fit into ASCII,
    /// it becomes UTF-16 with BOM (Windows reads that as well).
    /// </summary>
    public static void WriteFile(string pfad, string inhalt, Encoding kodierung)
    {
        if (kodierung is ASCIIEncoding && inhalt.Any(c => c >= 0x80)) kodierung = new UnicodeEncoding(false, true);
        var zwischen = pfad + ".dasi-neu";
        using (var f = File.Create(zwischen))
        {
            var bom = kodierung.GetPreamble();
            f.Write(bom, 0, bom.Length);
            var daten = kodierung.GetBytes(inhalt);
            f.Write(daten, 0, daten.Length);
        }
        File.Move(zwischen, pfad, overwrite: true);
    }

    private static Encoding Ansi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { return Encoding.Latin1; }
    }
}
