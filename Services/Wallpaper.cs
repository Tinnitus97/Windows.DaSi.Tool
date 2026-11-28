using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>What is set as the desktop wallpaper.</summary>
public enum HintergrundArt
{
    /// <summary>An image (file).</summary>
    Bild,
    /// <summary>A solid color, no image.</summary>
    Volltonfarbe,
    /// <summary>Slideshow or Windows Spotlight - changing images, nothing fixed to back up.</summary>
    Wechselnd,
    /// <summary>Neither image nor color readable.</summary>
    Unbekannt,
}

/// <summary>
/// The wallpaper settings stored next to the image: kind, color and fit
/// mode ("Fill", "Fit" ...). Kept as Hintergrund.json in the backup folder
/// "Hintergrundbild" (old format) or inside Design.json.
/// </summary>
public sealed class HintergrundEinstellung
{
    public const string DateiName = "Hintergrund.json";

    /// <summary>"Bild" (image) or "Volltonfarbe" (solid color).</summary>
    public string Art { get; set; } = "Bild";

    /// <summary>The background color as in the registry: "R G B", e.g. "0 99 177".</summary>
    public string? Farbe { get; set; }

    /// <summary>Control Panel\Desktop\WallpaperStyle - 10 Fill, 6 Fit, 2 Stretch, 0 Center/Tile, 22 Span across monitors.</summary>
    public string? Darstellung { get; set; }

    /// <summary>Control Panel\Desktop\TileWallpaper - "1" for "Tile".</summary>
    public string? Kacheln { get; set; }

    /// <summary>For an image: the file name in the backup (next to Design.json).</summary>
    public string? Bild { get; set; }

    /// <summary>For an image: where it was on the old computer.</summary>
    public string? BildPfad { get; set; }

    /// <summary>The backed-up profile - so BildPfad can be carried over to another profile.</summary>
    public string? Profil { get; set; }
}

/// <summary>
/// Desktop wallpaper: read and write the color, detect the kind. No
/// dependencies, so it can be tested - the registry and Windows calls live
/// in BackupService.
/// </summary>
public static class Wallpaper
{
    private static readonly Regex Rgb = new(@"^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})\s*$", RegexOptions.CultureInvariant);

    /// <summary>"0 99 177" -> (0, 99, 177); anything else -> null.</summary>
    public static (byte R, byte G, byte B)? ReadColor(string? text)
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

    /// <summary>(0, 99, 177) -> "0 99 177" - the way the registry stores it.</summary>
    public static string ColorText((byte R, byte G, byte B) f)
        => string.Create(CultureInfo.InvariantCulture, $"{f.R} {f.G} {f.B}");

    /// <summary>(0, 99, 177) -> "#0063B1".</summary>
    public static string Hex((byte R, byte G, byte B) f) => $"#{f.R:X2}{f.G:X2}{f.B:X2}";

    /// <summary>For SetSysColors: 0x00BBGGRR.</summary>
    public static int ColorRef((byte R, byte G, byte B) f) => f.R | (f.G << 8) | (f.B << 16);

    /// <summary>
    /// What is configured?
    ///
    /// Windows 10/11 writes BackgroundType (Explorer\Wallpapers) when the user
    /// switches under Settings > Personalization > Background: 0 image, 1 solid
    /// color, 2 slideshow, 3 Windows Spotlight. If the value is missing (older
    /// Windows version, never switched), the image path decides: empty means
    /// solid color.
    /// </summary>
    /// <param name="bildPfad">Control Panel\Desktop\WallPaper.</param>
    /// <param name="bildVorhanden">Does the file exist?</param>
    /// <param name="hintergrundTyp">BackgroundType, if set.</param>
    /// <param name="farbe">Control Panel\Colors\Background.</param>
    public static HintergrundArt Determine(string? bildPfad, bool bildVorhanden, int? hintergrundTyp, string? farbe)
    {
        switch (hintergrundTyp)
        {
            case 1:
                return ReadColor(farbe) is null ? HintergrundArt.Unbekannt : HintergrundArt.Volltonfarbe;
            case 2:
            case 3:
                // A slideshow is showing an image right now - it is backed up if it exists.
                return bildVorhanden ? HintergrundArt.Bild : HintergrundArt.Wechselnd;
        }
        if (!string.IsNullOrWhiteSpace(bildPfad))
            return bildVorhanden ? HintergrundArt.Bild : HintergrundArt.Unbekannt;
        return ReadColor(farbe) is null ? HintergrundArt.Unbekannt : HintergrundArt.Volltonfarbe;
    }

    /// <summary>Windows' own copy of the wallpaper, relative to the profile.</summary>
    public const string ZwischenspeicherRel = @"AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper";

    /// <summary>The copy of the currently shown wallpaper in a profile.</summary>
    public static string? Cache(string? profil)
        => string.IsNullOrWhiteSpace(profil) ? null : AppCatalog.PathIn(profil, ZwischenspeicherRel);

    /// <summary>
    /// Is the image in Windows' cache - "TranscodedWallpaper" or "CachedFiles"
    /// under AppData\Roaming\Microsoft\Windows\Themes? Windows manages those
    /// files itself: registered as the wallpaper, they get overwritten or
    /// deleted the next time a wallpaper is set.
    /// </summary>
    public static bool IsWindowsCache(string? pfad)
    {
        if (string.IsNullOrWhiteSpace(pfad)) return false;
        var p = pfad.Replace('/', '\\');
        if (p.Contains(@"\Roaming\Microsoft\Windows\Themes\", StringComparison.OrdinalIgnoreCase)) return true;
        var name = FileName(p);
        return name.StartsWith("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file extension for the image type based on the first bytes - null if unknown.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> kopf)
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
    /// The name the image is backed up under or placed into "Bilder" with: its
    /// own - but Windows' cache as "Hintergrundbild.jpg" (matching extension),
    /// and a file without an extension gets the one for its image type.
    /// </summary>
    public static string BackupName(string pfad, ReadOnlySpan<byte> kopf)
    {
        var endung = ImageExtension(kopf);
        if (IsWindowsCache(pfad)) return "Hintergrundbild" + (endung ?? ".jpg");
        var name = FileName(pfad.Replace('/', '\\'));
        if (name.Length == 0) return "Hintergrundbild" + (endung ?? ".jpg");
        return name.Contains('.') || endung is null ? name : name + endung;
    }

    private static string FileName(string windowsPfad)
    {
        int i = windowsPfad.LastIndexOf('\\');
        return i < 0 ? windowsPfad : windowsPfad[(i + 1)..];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string ordner, HintergrundEinstellung e)
        => File.WriteAllText(Path.Combine(ordner, HintergrundEinstellung.DateiName), JsonSerializer.Serialize(e, Json));

    /// <summary>Reads Hintergrund.json - null if it does not exist (older backup) or is broken.</summary>
    public static HintergrundEinstellung? Load(string ordner)
    {
        try
        {
            var datei = Path.Combine(ordner, HintergrundEinstellung.DateiName);
            return File.Exists(datei) ? JsonSerializer.Deserialize<HintergrundEinstellung>(File.ReadAllText(datei)) : null;
        }
        catch { return null; }
    }

    /// <summary>true if the backup contains a solid color (and no image is meant).</summary>
    public static bool IsSolidColor(HintergrundEinstellung? e)
        => e is not null && string.Equals(e.Art, "Volltonfarbe", StringComparison.OrdinalIgnoreCase) && ReadColor(e.Farbe) is not null;
}
