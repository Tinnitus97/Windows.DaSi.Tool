using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>What a user's registry says about the desktop wallpaper.</summary>
/// <param name="Bild">Control Panel\Desktop\WallPaper - empty for a solid color.</param>
/// <param name="Farbe">Control Panel\Colors\Background ("R G B"), otherwise the color of the running session.</param>
/// <param name="Typ">Explorer\Wallpapers\BackgroundType: 0 image, 1 solid color, 2 slideshow, 3 Spotlight.</param>
/// <param name="Stil">WallpaperStyle ("10" Fill, "6" Fit ...).</param>
/// <param name="Kacheln">TileWallpaper.</param>
/// <param name="DesktopBilder">Wallpapers of the individual virtual desktops (Windows 11).</param>
public sealed record HintergrundStand(string? Bild, string? Farbe, int? Typ, string? Stil, string? Kacheln,
                                      IReadOnlyList<string> DesktopBilder);

/// <param name="Eingetragen">Is the wallpaper in the user's registry afterwards?</param>
/// <param name="Angezeigt">Did Windows take it over for the running session?</param>
/// <param name="VirtuelleDesktops">This many virtual desktops had a wallpaper of their own - aligned.</param>
/// <param name="DesignDatei">What happened to Windows' theme file (Custom.theme).</param>
/// <param name="ExplorerFehler">null: set through Explorer (IDesktopWallpaper); otherwise why not - then through SystemParametersInfo.</param>
public sealed record HintergrundGesetzt(bool Eingetragen, bool Angezeigt, int VirtuelleDesktops, DesignDateiErgebnis? DesignDatei = null,
                                        string? ExplorerFehler = null);

/// <param name="Datei">The current theme file, if known.</param>
/// <param name="Geaendert">Was it adjusted?</param>
/// <param name="Grund">Why not - null if it already matched or was adjusted.</param>
public sealed record DesignDateiErgebnis(string? Datei, bool Geaendert, string? Grund);

/// <summary>
/// Reads and sets a user's desktop wallpaper.
///
/// Why not simply SystemParametersInfo with SPIF_UPDATEINIFILE: it writes
/// to the PROCESS's HKEY_CURRENT_USER. The tool runs with admin rights - if
/// someone enters another account's credentials in the UAC dialog, that is
/// the other account's registry. The wallpaper then shows right away, but
/// is registered for the wrong user: after the next restart of Explorer or
/// Windows it is gone again (black or the old one).
///
/// So: write the registry values ourselves into the key of the signed-in
/// user (HKEY_USERS\&lt;SID&gt;) and call SystemParametersInfo only for the
/// running session - with SPIF_UPDATEINIFILE only if the tool runs as
/// exactly this user. Otherwise Windows' cache of the user's wallpaper
/// (TranscodedWallpaper) is discarded as well, so Windows reads the image
/// anew at the next sign-in instead of showing the old state.
///
/// By the way, an Explorer restart does NOT reload the wallpaper - it
/// belongs to the session (verified on Windows). What counts is what the
/// registry says at the next sign-in.
///
/// Windows 11 can also remember a wallpaper per virtual desktop and sets it
/// again when switching or signing in - that one is aligned as well.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DesktopHintergrund
{
    public const string DesktopSchluessel = @"Control Panel\Desktop";
    public const string FarbenSchluessel = @"Control Panel\Colors";
    public const string WallpapersSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    public const string VirtuelleDesktopsSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

    /// <summary>Does the tool run as the user with this SID? (null: unknown - then the process counts.)</summary>
    public static bool IsProcessUser(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return true;
        try
        {
            using var ich = WindowsIdentity.GetCurrent();
            return string.Equals(ich.User?.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static HintergrundStand Read(RegistryKey wurzel)
    {
        string? bild = null, farbe = null, stil = null, kacheln = null;
        int? typ = null;
        try
        {
            using var k = wurzel.OpenSubKey(DesktopSchluessel);
            bild = k?.GetValue("WallPaper") as string;
            stil = k?.GetValue("WallpaperStyle") as string;
            kacheln = k?.GetValue("TileWallpaper") as string;
        }
        catch { /* stays empty */ }
        try
        {
            using var k = wurzel.OpenSubKey(FarbenSchluessel);
            farbe = k?.GetValue("Background") as string;
        }
        catch { /* then from the session */ }
        if (Hintergrund.ReadColor(farbe) is null)
        {
            try
            {
                var c = GetSysColor(COLOR_DESKTOP);
                farbe = Hintergrund.ColorText(((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
            }
            catch { /* stays empty */ }
        }
        try
        {
            using var k = wurzel.OpenSubKey(WallpapersSchluessel);
            if (k?.GetValue("BackgroundType") is int t) typ = t;
        }
        catch { /* older Windows */ }
        return new HintergrundStand(string.IsNullOrWhiteSpace(bild) ? null : bild, farbe, typ, stil, kacheln,
                                    ReadDesktopImages(wurzel));
    }

    /// <summary>
    /// An image as the wallpaper: register it permanently for the user and
    /// show it in the running session.
    /// </summary>
    /// <param name="eigenerBenutzer">Does the tool run as the user who owns <paramref name="wurzel"/>?</param>
    /// <param name="profil">The user's profile folder - for the user's cache.</param>
    public static HintergrundGesetzt SetImage(RegistryKey wurzel, bool eigenerBenutzer, string pfad, HintergrundEinstellung? e, string? profil = null)
    {
        int vd = WriteImage(wurzel, pfad, e);
        if (!eigenerBenutzer) DiscardCache(wurzel, profil);
        // Like the Settings app, through Explorer (IDesktopWallpaper) - then it
        // remembers the change itself. Otherwise, or if that does not work:
        // SystemParametersInfo.
        var explorer = eigenerBenutzer ? ViaExplorer(() => Schnittstelle.SetImage(pfad, e)) : "Tool läuft unter einem anderen Konto";
        bool angezeigt = explorer is null || SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, pfad, Flags(eigenerBenutzer));
        if (Hintergrund.ReadColor(e?.Farbe) is { } rand) SessionColor(rand);

        // With SPIF_UPDATEINIFILE, Windows writes the path once more itself -
        // what is there in the end is what counts.
        bool eingetragen = IsRegistered(wurzel, pfad);
        if (!eingetragen)
        {
            WriteImage(wurzel, pfad, e);
            eingetragen = IsRegistered(wurzel, pfad);
        }
        var datei = AlignThemeFile(wurzel, profil, t => DesignDatei.ForImage(t, pfad, e));
        return new HintergrundGesetzt(eingetragen, angezeigt, vd, datei, explorer);
    }

    /// <summary>
    /// Only the registry - the way Explorer reads it at its next start.
    /// </summary>
    /// <returns>How many virtual desktops had a wallpaper of their own.</returns>
    public static int WriteImage(RegistryKey wurzel, string pfad, HintergrundEinstellung? e)
    {
        using (var k = wurzel.CreateSubKey(DesktopSchluessel))
        {
            if (!string.IsNullOrWhiteSpace(e?.Darstellung)) k.SetValue("WallpaperStyle", e.Darstellung);
            if (!string.IsNullOrWhiteSpace(e?.Kacheln)) k.SetValue("TileWallpaper", e.Kacheln);
            k.SetValue("WallPaper", pfad);
        }
        if (Hintergrund.ReadColor(e?.Farbe) is { } rand)
        {
            try
            {
                using var c = wurzel.CreateSubKey(FarbenSchluessel);
                c.SetValue("Background", Hintergrund.ColorText(rand));
            }
            catch { /* the edge color is a minor matter */ }
        }
        WriteKind(wurzel, 0);
        return AlignDesktopImages(wurzel, pfad);
    }

    /// <summary>A solid color as the wallpaper - like Settings > Personalization > Background.</summary>
    public static HintergrundGesetzt SetColor(RegistryKey wurzel, bool eigenerBenutzer, (byte R, byte G, byte B) farbe, string? profil = null)
    {
        int vd = WriteColor(wurzel, farbe);
        if (!eigenerBenutzer) DiscardCache(wurzel, profil);
        // Like the Settings app, through Explorer (IDesktopWallpaper): on
        // Windows 11 it remembers the color itself and would otherwise write the
        // old one back at restart. Otherwise, or if that does not work: without
        // an image the desktop shows the session's color.
        var explorer = eigenerBenutzer ? ViaExplorer(() => Schnittstelle.SetColor(farbe, abschalten: false)) : "Tool läuft unter einem anderen Konto";
        if (explorer is not null) SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "", Flags(eigenerBenutzer));
        bool angezeigt = SessionColor(farbe);

        bool eingetragen = IsColorRegistered(wurzel, farbe);
        if (!eingetragen)
        {
            WriteColor(wurzel, farbe);
            eingetragen = IsColorRegistered(wurzel, farbe);
        }
        var datei = AlignThemeFile(wurzel, profil, t => DesignDatei.ForColor(t, farbe));
        return new HintergrundGesetzt(eingetragen, angezeigt, vd, datei, explorer);
    }

    /// <summary>null if the call through IDesktopWallpaper worked - otherwise the reason.</summary>
    private static string? ViaExplorer(Action tun)
    {
        try { tun(); return null; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// Also records a change in the user's current theme file (CurrentTheme,
    /// usually ...\Themes\Custom.theme). At sign-in, Windows copies values from
    /// it back into the registry - without this step a solid color was the old
    /// one again after a restart. Only a file inside the user's profile is
    /// touched, never a Windows theme under C:\Windows.
    /// </summary>
    /// <param name="profil">The user's profile folder; null: the process's.</param>
    public static DesignDateiErgebnis AlignThemeFile(RegistryKey wurzel, string? profil, Func<string, string> anpassen)
    {
        profil ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = profil.TrimEnd('\\', '/');
        string? pfad;
        try { pfad = ThemeFilePath(wurzel, profil); }
        catch (Exception ex) { return new DesignDateiErgebnis(null, false, ex.Message); }
        if (pfad is null) return new DesignDateiErgebnis(null, false, "kein aktuelles Design eingetragen");
        if (!pfad.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
            return new DesignDateiErgebnis(pfad, false, "Windows-Design außerhalb des Profils - bleibt unverändert");
        if (!System.IO.File.Exists(pfad)) return new DesignDateiErgebnis(pfad, false, "Datei fehlt");
        try
        {
            var (inhalt, kodierung) = DesignDatei.ReadFile(pfad);
            var neu = anpassen(inhalt);
            if (neu == inhalt) return new DesignDateiErgebnis(pfad, false, null);
            DesignDatei.WriteFile(pfad, neu, kodierung);
            return new DesignDateiErgebnis(pfad, true, null);
        }
        catch (Exception ex) { return new DesignDateiErgebnis(pfad, false, ex.Message); }
    }

    /// <summary>Only the registry: color, no image, kind "solid color".</summary>
    public static int WriteColor(RegistryKey wurzel, (byte R, byte G, byte B) farbe)
    {
        using (var c = wurzel.CreateSubKey(FarbenSchluessel))
            c.SetValue("Background", Hintergrund.ColorText(farbe));
        using (var k = wurzel.CreateSubKey(DesktopSchluessel))
            k.SetValue("WallPaper", "");
        WriteKind(wurzel, 1);
        return AlignDesktopImages(wurzel, null);
    }

    /// <summary>
    /// Discards Windows' copy of the previous wallpaper - the file
    /// TranscodedWallpaper and the reference to it (TranscodedImageCache). At
    /// the next sign-in Windows then reads the wallpaper from the registry
    /// anew, as for a fresh profile. Only needed if the tool runs under another
    /// account: otherwise Windows renews the copy itself when setting it.
    /// </summary>
    public static void DiscardCache(RegistryKey wurzel, string? profil)
    {
        try
        {
            using var k = wurzel.OpenSubKey(DesktopSchluessel, writable: true);
            k?.DeleteValue("TranscodedImageCache", throwOnMissingValue: false);
        }
        catch { /* then just the file */ }
        try
        {
            if (Hintergrund.Cache(profil) is { } datei && System.IO.File.Exists(datei))
                System.IO.File.Delete(datei);
        }
        catch { /* locked - Windows compares the path anyway */ }
    }

    /// <summary>
    /// Does the desktop show what <paramref name="soll"/> says? For this the
    /// desktop window itself is captured (even beneath other windows).
    /// Solid color: does the color dominate? Image: is the desktop not black?
    /// </summary>
    /// <returns>null if so or if it cannot be checked - otherwise what is wrong.</returns>
    public static string? Verify(HintergrundStand soll, Func<string, string, string> tr)
    {
        var bild = DesktopScreenshot.CaptureDesktopWindow();
        if (bild is null) return null;
        var (meist, anteil) = DesktopScreenshot.MostCommonColor(bild);
        // If the capture of the desktop window stays black, PrintWindow may be the
        // cause - then it only counts if the screen is mostly black as well.
        bool schwarz = DesktopScreenshot.IsBlank(bild);
        if (soll.Bild is null)
        {
            if (Hintergrund.ReadColor(soll.Farbe) is not { } f) return null;
            if (DesktopScreenshot.ColorShare(bild, f) >= 0.5) return null;
            if (schwarz && !IsScreenBlack()) return null;
            return tr($"Der Desktop zeigt nicht die Farbe {Hintergrund.Hex(f)}, sondern meist {meist} ({anteil:P0})",
                      $"The desktop does not show the color {Hintergrund.Hex(f)} but mostly {meist} ({anteil:P0})");
        }
        return schwarz && IsScreenBlack()
            ? tr($"Der Desktop ist schwarz statt des Bildes {soll.Bild}", $"The desktop is black instead of the image {soll.Bild}")
            : null;
    }

    /// <summary>Is the whole screen mostly black (not just the capture of the desktop window)?</summary>
    internal static bool IsScreenBlack()
    {
        try { return DesktopScreenshot.ColorShare(DesktopScreenshot.CaptureVirtualScreen().Bgra, (0, 0, 0), 10) >= 0.5; }
        catch { return false; }
    }

    /// <summary>What changed in the registry compared to <paramref name="soll"/> - null if nothing.</summary>
    public static string? Difference(HintergrundStand soll, HintergrundStand jetzt, Func<string, string, string> tr)
    {
        if (!string.Equals(soll.Bild ?? "", jetzt.Bild ?? "", StringComparison.OrdinalIgnoreCase))
            return tr($"Windows hat das Bild geändert: {soll.Bild ?? "(keins)"} -> {jetzt.Bild ?? "(keins)"}",
                      $"Windows changed the image: {soll.Bild ?? "(none)"} -> {jetzt.Bild ?? "(none)"}");
        if (soll.Bild is null && Hintergrund.ReadColor(soll.Farbe) != Hintergrund.ReadColor(jetzt.Farbe))
            return tr($"Windows hat die Farbe geändert: {soll.Farbe} -> {jetzt.Farbe}", $"Windows changed the color: {soll.Farbe} -> {jetzt.Farbe}");
        if (soll.Typ is { } t && jetzt.Typ != t)
            return tr($"Windows hat die Art geändert: {t} -> {jetzt.Typ?.ToString() ?? "-"} (0 Bild, 1 Farbe, 2 Diashow, 3 Blickpunkt)",
                      $"Windows changed the type: {t} -> {jetzt.Typ?.ToString() ?? "-"} (0 image, 1 color, 2 slideshow, 3 Spotlight)");
        return null;
    }

    /// <summary>Set the state <paramref name="soll"/> once more.</summary>
    public static HintergrundGesetzt Retry(RegistryKey wurzel, bool eigenerBenutzer, HintergrundStand soll, string? profil)
        => soll.Bild is { } b
            ? SetImage(wurzel, eigenerBenutzer, b, new HintergrundEinstellung { Darstellung = soll.Stil, Kacheln = soll.Kacheln }, profil)
            : SetColor(wurzel, eigenerBenutzer, Hintergrund.ReadColor(soll.Farbe) ?? ((byte)0, (byte)0, (byte)0), profil);

    /// <summary>What can dictate or change the wallpaper from outside - policies, Spotlight, slideshow.</summary>
    /// <param name="mitWechsel">Also name Spotlight and slideshow (Windows normally turns those off itself when the wallpaper is set).</param>
    public static List<string> Influences(RegistryKey wurzel, bool mitWechsel, Func<string, string, string> tr)
    {
        var l = new List<string>();
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
            if (k?.GetValue("Wallpaper") is string w)
                l.Add(tr($"Eine Gruppenrichtlinie legt den Hintergrund fest: \"{w}\" - sie gewinnt bei jeder Anmeldung",
                         $"A group policy sets the background: \"{w}\" - it wins at every sign-in"));
        }
        catch { /* carry on */ }
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop");
            if (k?.GetValue("NoChangingWallPaper") is int n && n != 0)
                l.Add(tr("Eine Richtlinie verbietet das Ändern des Hintergrunds", "A policy prevents changing the background"));
        }
        catch { /* carry on */ }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP");
            if ((k?.GetValue("DesktopImagePath") ?? k?.GetValue("DesktopImageUrl")) is string p)
                l.Add(tr($"Die Geräteverwaltung (Intune/MDM) legt das Desktopbild fest: {p}", $"Device management (Intune/MDM) sets the desktop image: {p}"));
        }
        catch { /* carry on */ }
        if (!mitWechsel) return l;
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings");
            if (k?.GetValue("EnabledState") is int e && e != 0)
                l.Add(tr("Windows-Blickpunkt für den Desktop ist noch eingeschaltet", "Windows Spotlight for the desktop is still on"));
        }
        catch { /* carry on */ }
        if (Schnittstelle.Slideshow() == true)
            l.Add(tr("Eine Diashow wechselt den Hintergrund", "A slideshow changes the background"));
        return l;
    }

    /// <summary>
    /// The user's current theme file (CurrentTheme), expanded -
    /// %LOCALAPPDATA% etc. relative to THE USER's profile, not the process's.
    /// null if none is registered.
    /// </summary>
    public static string? ThemeFilePath(RegistryKey wurzel, string? profil)
    {
        string? pfad;
        using (var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes"))
            pfad = k?.GetValue("CurrentTheme") as string;
        if (string.IsNullOrWhiteSpace(pfad)) return null;
        var p = (profil ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\', '/');
        pfad = pfad.Replace("%LOCALAPPDATA%", p + @"\AppData\Local", StringComparison.OrdinalIgnoreCase)
                   .Replace("%APPDATA%", p + @"\AppData\Roaming", StringComparison.OrdinalIgnoreCase)
                   .Replace("%USERPROFILE%", p, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(pfad);
    }

    private static bool IsRegistered(RegistryKey wurzel, string pfad)
        => string.Equals(Read(wurzel).Bild, pfad, StringComparison.OrdinalIgnoreCase);

    private static bool IsColorRegistered(RegistryKey wurzel, (byte R, byte G, byte B) farbe)
    {
        var s = Read(wurzel);
        return s.Bild is null && Hintergrund.ReadColor(s.Farbe) == farbe;
    }

    private static uint Flags(bool eigenerBenutzer)
        => eigenerBenutzer ? SPIF_UPDATEINIFILE | SPIF_SENDCHANGE : SPIF_SENDCHANGE;

    /// <summary>
    /// BackgroundType, so Windows and the Settings app know the right kind:
    /// 0 image, 1 solid color. Also before applying a theme file: if it says
    /// "solid color", Windows takes only the color from the file, not the image
    /// (verified on Windows 11).
    /// </summary>
    public static void WriteStyle(RegistryKey wurzel, int typ) => WriteKind(wurzel, typ);

    private static void WriteKind(RegistryKey wurzel, int typ)
    {
        try
        {
            using var k = wurzel.CreateSubKey(WallpapersSchluessel);
            k.SetValue("BackgroundType", typ, RegistryValueKind.DWord);
        }
        catch { /* older Windows */ }
    }

    /// <summary>The color of the running session - takes effect at once, but only until sign-out.</summary>
    private static bool SessionColor((byte R, byte G, byte B) farbe)
    {
        try { return SetSysColors(1, new[] { COLOR_DESKTOP }, new[] { Hintergrund.ColorRef(farbe) }); }
        catch { return false; }
    }

    private static List<string> ReadDesktopImages(RegistryKey wurzel)
    {
        var bilder = new List<string>();
        try
        {
            using var k = wurzel.OpenSubKey(VirtuelleDesktopsSchluessel);
            if (k is null) return bilder;
            foreach (var name in k.GetSubKeyNames())
            {
                using var d = k.OpenSubKey(name);
                if (d?.GetValue("Wallpaper") is string b) bilder.Add(b);
            }
        }
        catch { /* no virtual desktops */ }
        return bilder;
    }

    /// <summary>
    /// Virtual desktops with a wallpaper of their own get the new one - for a
    /// solid color (image null) their own one is removed, so they show the
    /// general one.
    /// </summary>
    private static int AlignDesktopImages(RegistryKey wurzel, string? bild)
    {
        int n = 0;
        try
        {
            using var k = wurzel.OpenSubKey(VirtuelleDesktopsSchluessel, writable: true);
            if (k is null) return 0;
            foreach (var name in k.GetSubKeyNames())
            {
                try
                {
                    using var d = k.OpenSubKey(name, writable: true);
                    if (d?.GetValue("Wallpaper") is null) continue;
                    if (bild is null) d.DeleteValue("Wallpaper", throwOnMissingValue: false);
                    else d.SetValue("Wallpaper", bild);
                    n++;
                }
                catch { /* next one */ }
            }
        }
        catch { /* no virtual desktops */ }
        return n;
    }

    // ------------------------------------------------------------ IDesktopWallpaper

    /// <summary>
    /// The interface the Settings app uses to set the wallpaper as well
    /// (Explorer). Affects the running session and the user the tool runs as.
    /// </summary>
    public static class Schnittstelle
    {
        /// <summary>State according to Explorer, as text - for the log and the tests; null if not reachable.</summary>
        public static string? Snapshot()
        {
            try
            {
                return OnSta(dw =>
                {
                    string bild;
                    try { bild = dw.GetWallpaper(null) ?? ""; } catch (Exception ex) { bild = $"({ex.GetType().Name})"; }
                    var c = dw.GetBackgroundColor();
                    var farbe = Hintergrund.Hex(((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
                    string status;
                    try { status = dw.GetStatus().ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { status = "?"; }
                    return $"Bild={(bild.Length == 0 ? "-" : bild)}, Farbe={farbe}, Position={dw.GetPosition()}, Status={status}, Monitore={dw.GetMonitorDevicePathCount()}";
                });
            }
            catch { return null; }
        }

        /// <summary>Is a slideshow running? null if not reachable.</summary>
        public static bool? Slideshow()
        {
            try { return OnSta(dw => (dw.GetStatus() & 2) != 0); }   // DSS_SLIDESHOW
            catch { return null; }
        }

        /// <summary>One image for all monitors, including fit mode.</summary>
        public static void SetImage(string pfad, HintergrundEinstellung? e)
            => OnSta(dw =>
            {
                if (Position(e) is { } pos) dw.SetPosition(pos);
                if (Hintergrund.ReadColor(e?.Farbe) is { } rand) dw.SetBackgroundColor((uint)Hintergrund.ColorRef(rand));
                dw.SetWallpaper(null, pfad);
                return true;
            });

        /// <summary>Solid color: set the color, clear the image (like the Settings app).</summary>
        public static void SetColor((byte R, byte G, byte B) farbe, bool abschalten)
            => OnSta(dw =>
            {
                dw.SetBackgroundColor((uint)Hintergrund.ColorRef(farbe));
                if (abschalten) dw.Enable(false);
                else dw.SetWallpaper(null, "");
                return true;
            });

        /// <summary>Turns the wallpaper image back on (after <see cref="SetColor"/> turned it off).</summary>
        public static void Enable() => OnSta(dw => { dw.Enable(true); return true; });

        /// <summary>WallpaperStyle/TileWallpaper -> DESKTOP_WALLPAPER_POSITION.</summary>
        public static int? Position(HintergrundEinstellung? e)
        {
            if (e?.Kacheln == "1") return 1;               // Tile
            return e?.Darstellung switch
            {
                "0" => 0,   // Center
                "2" => 2,   // Stretch
                "6" => 3,   // Fit
                "10" => 4,  // Fill
                "22" => 5,  // Span across monitors
                _ => null,
            };
        }

        private static T OnSta<T>(Func<IDesktopWallpaper, T> tun)
        {
            T ergebnis = default!;
            Exception? fehler = null;
            var faden = new Thread(() =>
            {
                object? obj = null;
                try
                {
                    var typ = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"), throwOnError: true)!;
                    obj = Activator.CreateInstance(typ);
                    ergebnis = tun((IDesktopWallpaper)obj!);
                }
                catch (Exception ex) { fehler = ex; }
                finally
                {
                    if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
                }
            });
            faden.SetApartmentState(ApartmentState.STA);
            faden.IsBackground = true;
            faden.Start();
            if (!faden.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("IDesktopWallpaper antwortet nicht.");
            if (fehler is not null) throw fehler;
            return ergebnis;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(int options, uint tick);
        void GetSlideshowOptions(out int options, out uint tick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const int COLOR_DESKTOP = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSysColors(int cElements, int[] lpaElements, int[] lpaRgbValues);
}
