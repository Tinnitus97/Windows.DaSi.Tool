using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>Was als Desktop-Hintergrund eingestellt ist.</summary>
public enum HintergrundArt
{
    /// <summary>Ein Bild (Datei).</summary>
    Bild,
    /// <summary>Eine Volltonfarbe, kein Bild.</summary>
    Volltonfarbe,
    /// <summary>Diashow oder Windows-Blickpunkt - wechselnde Bilder, nichts Festes zu sichern.</summary>
    Wechselnd,
    /// <summary>Weder Bild noch Farbe lesbar.</summary>
    Unbekannt,
}

/// <summary>
/// Die Einstellungen des Hintergrunds neben dem Bild: Art, Farbe und
/// Darstellung ("Füllen", "Anpassen" ...). Liegt als Hintergrund.json im
/// Sicherungsordner "Hintergrundbild".
/// </summary>
public sealed class HintergrundEinstellung
{
    public const string DateiName = "Hintergrund.json";

    /// <summary>"Bild" oder "Volltonfarbe".</summary>
    public string Art { get; set; } = "Bild";

    /// <summary>Die Hintergrundfarbe wie in der Registry: "R G B", z. B. "0 99 177".</summary>
    public string? Farbe { get; set; }

    /// <summary>Control Panel\Desktop\WallpaperStyle - 10 Füllen, 6 Anpassen, 2 Strecken, 0 Zentriert/Kacheln, 22 Strecken über Bildschirme.</summary>
    public string? Darstellung { get; set; }

    /// <summary>Control Panel\Desktop\TileWallpaper - "1" bei "Kacheln".</summary>
    public string? Kacheln { get; set; }

    /// <summary>Bei einem Bild: der Dateiname in der Sicherung (neben Design.json).</summary>
    public string? Bild { get; set; }

    /// <summary>Bei einem Bild: wo es auf dem alten Rechner lag.</summary>
    public string? BildPfad { get; set; }

    /// <summary>Das gesicherte Profil - damit sich BildPfad in ein anderes Profil uebertragen laesst.</summary>
    public string? Profil { get; set; }
}

/// <summary>
/// Desktop-Hintergrund: Farbe lesen und schreiben, Art erkennen. Ohne
/// Abhaengigkeiten, damit es sich pruefen laesst - Registry und Windows-
/// Aufrufe stecken im BackupService.
/// </summary>
public static class Hintergrund
{
    private static readonly Regex Rgb = new(@"^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})\s*$", RegexOptions.CultureInvariant);

    /// <summary>"0 99 177" -> (0, 99, 177); alles andere -> null.</summary>
    public static (byte R, byte G, byte B)? FarbeLesen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Rgb.Match(text);
        if (!m.Success) return null;
        int r = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (r > 255 || g > 255 || b > 255) return null;
        return ((byte)r, (byte)g, (byte)b);
    }

    /// <summary>(0, 99, 177) -> "0 99 177" - so steht es in der Registry.</summary>
    public static string FarbeText((byte R, byte G, byte B) f)
        => string.Create(CultureInfo.InvariantCulture, $"{f.R} {f.G} {f.B}");

    /// <summary>(0, 99, 177) -> "#0063B1".</summary>
    public static string Hex((byte R, byte G, byte B) f) => $"#{f.R:X2}{f.G:X2}{f.B:X2}";

    /// <summary>Fuer SetSysColors: 0x00BBGGRR.</summary>
    public static int ColorRef((byte R, byte G, byte B) f) => f.R | (f.G << 8) | (f.B << 16);

    /// <summary>
    /// Was ist eingestellt?
    ///
    /// BackgroundType (Explorer\Wallpapers) schreibt Windows 10/11 beim
    /// Umstellen unter Einstellungen > Personalisierung > Hintergrund: 0 Bild,
    /// 1 Volltonfarbe, 2 Diashow, 3 Windows-Blickpunkt. Fehlt der Wert (aeltere
    /// Windows-Fassung, nie umgestellt), entscheidet der Bildpfad: leer heisst
    /// Volltonfarbe.
    /// </summary>
    /// <param name="bildPfad">Control Panel\Desktop\WallPaper.</param>
    /// <param name="bildVorhanden">Gibt es die Datei?</param>
    /// <param name="hintergrundTyp">BackgroundType, falls gesetzt.</param>
    /// <param name="farbe">Control Panel\Colors\Background.</param>
    public static HintergrundArt Bestimmen(string? bildPfad, bool bildVorhanden, int? hintergrundTyp, string? farbe)
    {
        switch (hintergrundTyp)
        {
            case 1:
                return FarbeLesen(farbe) is null ? HintergrundArt.Unbekannt : HintergrundArt.Volltonfarbe;
            case 2:
            case 3:
                // Eine Diashow zeigt gerade ein Bild - das wird gesichert, wenn es da ist.
                return bildVorhanden ? HintergrundArt.Bild : HintergrundArt.Wechselnd;
        }
        if (!string.IsNullOrWhiteSpace(bildPfad))
            return bildVorhanden ? HintergrundArt.Bild : HintergrundArt.Unbekannt;
        return FarbeLesen(farbe) is null ? HintergrundArt.Unbekannt : HintergrundArt.Volltonfarbe;
    }

    /// <summary>Windows' eigene Kopie des Hintergrunds, relativ zum Profil.</summary>
    public const string ZwischenspeicherRel = @"AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper";

    /// <summary>Die Kopie des gerade gezeigten Hintergrunds in einem Profil.</summary>
    public static string? Zwischenspeicher(string? profil)
        => string.IsNullOrWhiteSpace(profil) ? null : AppCatalog.Pfad(profil, ZwischenspeicherRel);

    /// <summary>
    /// Liegt das Bild in Windows' Zwischenspeicher - "TranscodedWallpaper"
    /// oder "CachedFiles" unter AppData\Roaming\Microsoft\Windows\Themes? Diese
    /// Dateien verwaltet Windows selbst: Als Hintergrund eingetragen, werden
    /// sie beim naechsten Setzen ueberschrieben oder geloescht.
    /// </summary>
    public static bool IstWindowsZwischenspeicher(string? pfad)
    {
        if (string.IsNullOrWhiteSpace(pfad)) return false;
        var p = pfad.Replace('/', '\\');
        if (p.Contains(@"\Roaming\Microsoft\Windows\Themes\", StringComparison.OrdinalIgnoreCase)) return true;
        var name = DateiName(p);
        return name.StartsWith("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Die Endung zur Bildart nach den ersten Bytes - null, wenn unbekannt.</summary>
    public static string? BildEndung(ReadOnlySpan<byte> kopf)
    {
        if (kopf.Length >= 3 && kopf[0] == 0xFF && kopf[1] == 0xD8 && kopf[2] == 0xFF) return ".jpg";
        if (kopf.Length >= 4 && kopf[0] == 0x89 && kopf[1] == 0x50 && kopf[2] == 0x4E && kopf[3] == 0x47) return ".png";
        if (kopf.Length >= 4 && kopf[0] == 0x47 && kopf[1] == 0x49 && kopf[2] == 0x46 && kopf[3] == 0x38) return ".gif";
        if (kopf.Length >= 12 && kopf[0] == 0x52 && kopf[1] == 0x49 && kopf[2] == 0x46 && kopf[3] == 0x46
            && kopf[8] == 0x57 && kopf[9] == 0x45 && kopf[10] == 0x42 && kopf[11] == 0x50) return ".webp";
        if (kopf.Length >= 2 && kopf[0] == 0x42 && kopf[1] == 0x4D) return ".bmp";
        return null;
    }

    /// <summary>
    /// Unter welchem Namen das Bild gesichert bzw. in "Bilder" abgelegt wird:
    /// sein eigener - Windows' Zwischenspeicher aber als "Hintergrundbild.jpg"
    /// (passende Endung), und eine Datei ohne Endung bekommt die zur Bildart.
    /// </summary>
    public static string SicherungsName(string pfad, ReadOnlySpan<byte> kopf)
    {
        var endung = BildEndung(kopf);
        if (IstWindowsZwischenspeicher(pfad)) return "Hintergrundbild" + (endung ?? ".jpg");
        var name = DateiName(pfad.Replace('/', '\\'));
        if (name.Length == 0) return "Hintergrundbild" + (endung ?? ".jpg");
        return name.Contains('.') || endung is null ? name : name + endung;
    }

    private static string DateiName(string windowsPfad)
    {
        int i = windowsPfad.LastIndexOf('\\');
        return i < 0 ? windowsPfad : windowsPfad[(i + 1)..];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Speichern(string ordner, HintergrundEinstellung e)
        => File.WriteAllText(Path.Combine(ordner, HintergrundEinstellung.DateiName), JsonSerializer.Serialize(e, Json));

    /// <summary>Liest Hintergrund.json - null, wenn es sie nicht gibt (aeltere Sicherung) oder sie kaputt ist.</summary>
    public static HintergrundEinstellung? Laden(string ordner)
    {
        try
        {
            var datei = Path.Combine(ordner, HintergrundEinstellung.DateiName);
            return File.Exists(datei) ? JsonSerializer.Deserialize<HintergrundEinstellung>(File.ReadAllText(datei)) : null;
        }
        catch { return null; }
    }

    /// <summary>true, wenn die Sicherung eine Volltonfarbe enthaelt (und kein Bild gemeint ist).</summary>
    public static bool IstVolltonfarbe(HintergrundEinstellung? e)
        => e is not null && string.Equals(e.Art, "Volltonfarbe", StringComparison.OrdinalIgnoreCase) && FarbeLesen(e.Farbe) is not null;
}
