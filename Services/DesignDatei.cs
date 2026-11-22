using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Die Design-Datei von Windows (".theme", meist
/// %LOCALAPPDATA%\Microsoft\Windows\Themes\Custom.theme) - eine INI-Datei.
///
/// Warum das Tool sie anfasst: Windows uebernimmt beim Anmelden Teile daraus
/// wieder in die Registry - unter Windows 11 nachweislich die Hintergrundfarbe
/// ([Control Panel\Colors] Background). Die Einstellungen-App traegt jede
/// Aenderung deshalb auch hier ein. Das Tool schrieb bisher nur die Registry:
/// Nach dem naechsten Neustart war die Volltonfarbe wieder die alte (schwarz).
/// Jetzt traegt es Hintergrund und Design-Einstellungen auch hier ein.
///
/// Ohne Abhaengigkeiten, damit es sich pruefen laesst.
/// </summary>
public static class DesignDatei
{
    public const string FarbenAbschnitt = @"Control Panel\Colors";
    public const string DesktopAbschnitt = @"Control Panel\Desktop";
    public const string ZeigerAbschnitt = @"Control Panel\Cursors";
    public const string StilAbschnitt = "VisualStyles";
    public const string DiashowAbschnitt = "Slideshow";

    // ------------------------------------------------------------ Inhalt

    /// <summary>Volltonfarbe: Farbe eintragen, kein Bild, keine Diashow.</summary>
    public static string FuerFarbe(string inhalt, (byte R, byte G, byte B) farbe)
    {
        var t = Setzen(inhalt, DesktopAbschnitt, "Wallpaper", "");
        t = Setzen(t, FarbenAbschnitt, "Background", Hintergrund.FarbeText(farbe));
        return AbschnittEntfernen(t, DiashowAbschnitt);
    }

    /// <summary>Bild: Pfad, Darstellung und Randfarbe eintragen, keine Diashow.</summary>
    public static string FuerBild(string inhalt, string pfad, HintergrundEinstellung? e)
    {
        var t = Setzen(inhalt, DesktopAbschnitt, "Wallpaper", pfad);
        if (!string.IsNullOrWhiteSpace(e?.Darstellung)) t = Setzen(t, DesktopAbschnitt, "WallpaperStyle", e.Darstellung);
        if (!string.IsNullOrWhiteSpace(e?.Kacheln)) t = Setzen(t, DesktopAbschnitt, "TileWallpaper", e.Kacheln);
        if (Hintergrund.FarbeLesen(e?.Farbe) is { } rand) t = Setzen(t, FarbenAbschnitt, "Background", Hintergrund.FarbeText(rand));
        return AbschnittEntfernen(t, DiashowAbschnitt);
    }

    /// <summary>
    /// Die gesetzten Design-Einstellungen: Hell/Dunkel, Akzentfarbe,
    /// "automatisch", Mauszeiger. Nur Eintraege, die die Datei schon kennt -
    /// was Windows dort nicht fuehrt, setzt es beim Anmelden auch nicht zurueck.
    /// </summary>
    public static string FuerDesign(string inhalt, IEnumerable<DesignEintrag> werte)
    {
        var t = inhalt;
        foreach (var e in werte)
        {
            var name = e.Name;
            if (e.Schluessel.EndsWith(@"Themes\Personalize", StringComparison.OrdinalIgnoreCase) && e.Typ == "DWord")
            {
                var modus = e.Wert == "0" ? "Dark" : "Light";
                if (name == "SystemUsesLightTheme") t = NurWennVorhanden(t, StilAbschnitt, "SystemMode", modus);
                else if (name == "AppsUseLightTheme") t = NurWennVorhanden(t, StilAbschnitt, "AppMode", modus);
            }
            else if (e.Schluessel.EndsWith(@"Windows\DWM", StringComparison.OrdinalIgnoreCase) && name == "ColorizationColor"
                     && int.TryParse(e.Wert, NumberStyles.Integer, CultureInfo.InvariantCulture, out var farbe))
            {
                t = NurWennVorhanden(t, StilAbschnitt, "ColorizationColor", "0X" + unchecked((uint)farbe).ToString("X8", CultureInfo.InvariantCulture));
            }
            else if (string.Equals(e.Schluessel, @"Control Panel\Desktop", StringComparison.OrdinalIgnoreCase) && name == "AutoColorization")
            {
                t = NurWennVorhanden(t, StilAbschnitt, "AutoColorization", e.Wert);
            }
            else if (string.Equals(e.Schluessel, @"Control Panel\Cursors", StringComparison.OrdinalIgnoreCase)
                     && e.Typ is "String" or "ExpandString")
            {
                // Der unbenannte Wert (Schemaname) heisst in der Datei "DefaultValue".
                t = NurWennVorhanden(t, ZeigerAbschnitt, name.Length == 0 ? "DefaultValue" : name, e.Wert);
            }
        }
        return t;
    }

    // ------------------------------------------------------------ INI

    /// <summary>Wert eines Schluessels - null, wenn es Abschnitt oder Schluessel nicht gibt.</summary>
    public static string? Lesen(string inhalt, string abschnitt, string schluessel)
    {
        var (zeilen, _, _) = Zerlegen(inhalt);
        var (von, bis) = Bereich(zeilen, abschnitt);
        if (von < 0) return null;
        int i = Zeile(zeilen, von, bis, schluessel);
        return i < 0 ? null : zeilen[i][(zeilen[i].IndexOf('=') + 1)..].Trim();
    }

    /// <summary>Setzt einen Schluessel - legt ihn (und den Abschnitt) an, wenn noetig.</summary>
    public static string Setzen(string inhalt, string abschnitt, string schluessel, string wert)
    {
        var (zeilen, umbruch, schlussUmbruch) = Zerlegen(inhalt);
        var (von, bis) = Bereich(zeilen, abschnitt);
        if (von < 0)
        {
            while (zeilen.Count > 0 && zeilen[^1].Trim().Length == 0) zeilen.RemoveAt(zeilen.Count - 1);
            if (zeilen.Count > 0) zeilen.Add("");
            zeilen.Add($"[{abschnitt}]");
            zeilen.Add($"{schluessel}={wert}");
            return Zusammensetzen(zeilen, umbruch, true);
        }
        int i = Zeile(zeilen, von, bis, schluessel);
        if (i >= 0)
        {
            zeilen[i] = $"{schluessel_wie_vorher(zeilen[i])}={wert}";
        }
        else
        {
            // Hinter die letzte nicht leere Zeile des Abschnitts.
            int pos = bis;
            while (pos > von + 1 && zeilen[pos - 1].Trim().Length == 0) pos--;
            zeilen.Insert(pos, $"{schluessel}={wert}");
        }
        return Zusammensetzen(zeilen, umbruch, schlussUmbruch);

        static string schluessel_wie_vorher(string zeile) => zeile[..zeile.IndexOf('=')].Trim();
    }

    /// <summary>Setzt einen Schluessel nur, wenn es ihn schon gibt.</summary>
    public static string NurWennVorhanden(string inhalt, string abschnitt, string schluessel, string wert)
        => Lesen(inhalt, abschnitt, schluessel) is null ? inhalt : Setzen(inhalt, abschnitt, schluessel, wert);

    /// <summary>Entfernt einen ganzen Abschnitt samt Kopfzeile.</summary>
    public static string AbschnittEntfernen(string inhalt, string abschnitt)
    {
        var (zeilen, umbruch, schlussUmbruch) = Zerlegen(inhalt);
        var (von, bis) = Bereich(zeilen, abschnitt);
        if (von < 0) return inhalt;
        zeilen.RemoveRange(von, bis - von);
        return Zusammensetzen(zeilen, umbruch, schlussUmbruch);
    }

    private static (List<string> Zeilen, string Umbruch, bool SchlussUmbruch) Zerlegen(string inhalt)
    {
        var umbruch = inhalt.Contains("\r\n") ? "\r\n" : "\n";
        bool schluss = inhalt.EndsWith('\n');
        var zeilen = inhalt.Replace("\r\n", "\n").Split('\n').ToList();
        if (schluss) zeilen.RemoveAt(zeilen.Count - 1);
        return (zeilen, umbruch, schluss);
    }

    private static string Zusammensetzen(List<string> zeilen, string umbruch, bool schlussUmbruch)
        => string.Join(umbruch, zeilen) + (schlussUmbruch ? umbruch : "");

    /// <summary>[von, bis): Kopfzeile bis vor den naechsten Abschnitt; (-1, -1), wenn es ihn nicht gibt.</summary>
    private static (int Von, int Bis) Bereich(List<string> zeilen, string abschnitt)
    {
        int von = zeilen.FindIndex(z => string.Equals(z.Trim(), $"[{abschnitt}]", StringComparison.OrdinalIgnoreCase));
        if (von < 0) return (-1, -1);
        int bis = von + 1;
        while (bis < zeilen.Count && !zeilen[bis].TrimStart().StartsWith('[')) bis++;
        return (von, bis);
    }

    private static int Zeile(List<string> zeilen, int von, int bis, string schluessel)
    {
        for (int i = von + 1; i < bis; i++)
        {
            int gleich = zeilen[i].IndexOf('=');
            if (gleich > 0 && string.Equals(zeilen[i][..gleich].Trim(), schluessel, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------ Datei

    /// <summary>
    /// Liest die Datei samt Kodierung: Windows schreibt Design-Dateien meist
    /// als UTF-16 mit BOM, aeltere oder von Hand erstellte auch ANSI.
    /// </summary>
    public static (string Inhalt, Encoding Kodierung) DateiLesen(string pfad)
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
    /// Schreibt in derselben Kodierung zurueck - ueber eine Zwischendatei, damit
    /// ein Abbruch nie eine halbe Design-Datei hinterlaesst. Passt ein neuer
    /// Wert nicht in ASCII, wird UTF-16 mit BOM daraus (liest Windows auch).
    /// </summary>
    public static void DateiSchreiben(string pfad, string inhalt, Encoding kodierung)
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
