using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>Was in der Registry eines Benutzers zum Desktop-Hintergrund steht.</summary>
/// <param name="Bild">Control Panel\Desktop\WallPaper - leer bei einer Volltonfarbe.</param>
/// <param name="Farbe">Control Panel\Colors\Background ("R G B"), sonst die Farbe der laufenden Sitzung.</param>
/// <param name="Typ">Explorer\Wallpapers\BackgroundType: 0 Bild, 1 Volltonfarbe, 2 Diashow, 3 Blickpunkt.</param>
/// <param name="Stil">WallpaperStyle ("10" Füllen, "6" Anpassen ...).</param>
/// <param name="Kacheln">TileWallpaper.</param>
/// <param name="DesktopBilder">Eigene Hintergruende der virtuellen Desktops (Windows 11).</param>
public sealed record HintergrundStand(string? Bild, string? Farbe, int? Typ, string? Stil, string? Kacheln,
                                      IReadOnlyList<string> DesktopBilder);

/// <param name="Eingetragen">Steht der Hintergrund danach in der Registry des Benutzers?</param>
/// <param name="Angezeigt">Hat Windows ihn fuer die laufende Sitzung uebernommen?</param>
/// <param name="VirtuelleDesktops">So viele virtuelle Desktops hatten einen eigenen Hintergrund - angeglichen.</param>
public sealed record HintergrundGesetzt(bool Eingetragen, bool Angezeigt, int VirtuelleDesktops);

/// <summary>
/// Liest und setzt den Desktop-Hintergrund eines Benutzers.
///
/// Warum nicht einfach SystemParametersInfo mit SPIF_UPDATEINIFILE: Das
/// schreibt in HKEY_CURRENT_USER des PROZESSES. Das Tool laeuft mit
/// Adminrechten - gibt beim UAC-Dialog jemand die Daten eines anderen Kontos
/// ein, ist das dessen Registry. Zu sehen ist der Hintergrund dann sofort,
/// eingetragen aber beim falschen Benutzer: Nach dem naechsten Neustart des
/// Explorers oder von Windows ist er wieder weg (schwarz bzw. der alte).
///
/// Deshalb: Registry-Werte selbst in den Zweig des angemeldeten Benutzers
/// (HKEY_USERS\&lt;SID&gt;) schreiben und SystemParametersInfo nur fuer die
/// laufende Sitzung rufen - mit SPIF_UPDATEINIFILE nur, wenn das Tool unter
/// genau diesem Benutzer laeuft. Sonst wird zusaetzlich Windows'
/// Zwischenspeicher des Benutzers verworfen (TranscodedWallpaper), damit
/// Windows das Bild bei der naechsten Anmeldung neu einliest, statt den
/// alten Stand zu zeigen.
///
/// Ein Neustart des Explorers liest den Hintergrund uebrigens NICHT neu
/// ein - der gehoert zur Sitzung (unter Windows geprueft). Massgeblich ist,
/// was bei der naechsten Anmeldung in der Registry steht.
///
/// Windows 11 kann sich ausserdem je virtuellem Desktop einen eigenen
/// Hintergrund merken und setzt ihn beim Wechsel bzw. Anmelden wieder - auch
/// der wird angeglichen.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DesktopHintergrund
{
    public const string DesktopSchluessel = @"Control Panel\Desktop";
    public const string FarbenSchluessel = @"Control Panel\Colors";
    public const string WallpapersSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    public const string VirtuelleDesktopsSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

    /// <summary>Laeuft das Tool unter dem Benutzer mit dieser SID? (null: unbekannt - dann gilt der Prozess.)</summary>
    public static bool IstProzessBenutzer(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return true;
        try
        {
            using var ich = WindowsIdentity.GetCurrent();
            return string.Equals(ich.User?.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static HintergrundStand Lesen(RegistryKey wurzel)
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
        catch { /* bleibt leer */ }
        try
        {
            using var k = wurzel.OpenSubKey(FarbenSchluessel);
            farbe = k?.GetValue("Background") as string;
        }
        catch { /* dann aus der Sitzung */ }
        if (Hintergrund.FarbeLesen(farbe) is null)
        {
            try
            {
                var c = GetSysColor(COLOR_DESKTOP);
                farbe = Hintergrund.FarbeText(((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
            }
            catch { /* bleibt leer */ }
        }
        try
        {
            using var k = wurzel.OpenSubKey(WallpapersSchluessel);
            if (k?.GetValue("BackgroundType") is int t) typ = t;
        }
        catch { /* aelteres Windows */ }
        return new HintergrundStand(string.IsNullOrWhiteSpace(bild) ? null : bild, farbe, typ, stil, kacheln,
                                    DesktopBilderLesen(wurzel));
    }

    /// <summary>
    /// Ein Bild als Hintergrund: dauerhaft beim Benutzer eintragen und in der
    /// laufenden Sitzung zeigen.
    /// </summary>
    /// <param name="eigenerBenutzer">Laeuft das Tool unter dem Benutzer, dem <paramref name="wurzel"/> gehoert?</param>
    /// <param name="profil">Profilordner des Benutzers - fuer seinen Zwischenspeicher.</param>
    public static HintergrundGesetzt BildSetzen(RegistryKey wurzel, bool eigenerBenutzer, string pfad, HintergrundEinstellung? e, string? profil = null)
    {
        int vd = BildEintragen(wurzel, pfad, e);
        if (!eigenerBenutzer) ZwischenspeicherVerwerfen(wurzel, profil);
        bool angezeigt = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, pfad, Flags(eigenerBenutzer));
        if (Hintergrund.FarbeLesen(e?.Farbe) is { } rand) SitzungsFarbe(rand);

        // Mit SPIF_UPDATEINIFILE schreibt Windows den Pfad selbst noch einmal -
        // was am Ende dasteht, zaehlt.
        bool eingetragen = IstEingetragen(wurzel, pfad);
        if (!eingetragen)
        {
            BildEintragen(wurzel, pfad, e);
            eingetragen = IstEingetragen(wurzel, pfad);
        }
        return new HintergrundGesetzt(eingetragen, angezeigt, vd);
    }

    /// <summary>
    /// Nur die Registry - so, wie sie der Explorer beim naechsten Start liest.
    /// </summary>
    /// <returns>Wie viele virtuelle Desktops einen eigenen Hintergrund hatten.</returns>
    public static int BildEintragen(RegistryKey wurzel, string pfad, HintergrundEinstellung? e)
    {
        using (var k = wurzel.CreateSubKey(DesktopSchluessel))
        {
            if (!string.IsNullOrWhiteSpace(e?.Darstellung)) k.SetValue("WallpaperStyle", e.Darstellung);
            if (!string.IsNullOrWhiteSpace(e?.Kacheln)) k.SetValue("TileWallpaper", e.Kacheln);
            k.SetValue("WallPaper", pfad);
        }
        if (Hintergrund.FarbeLesen(e?.Farbe) is { } rand)
        {
            try
            {
                using var c = wurzel.CreateSubKey(FarbenSchluessel);
                c.SetValue("Background", Hintergrund.FarbeText(rand));
            }
            catch { /* Randfarbe ist Nebensache */ }
        }
        TypEintragen(wurzel, 0);
        return DesktopBilderAngleichen(wurzel, pfad);
    }

    /// <summary>Eine Volltonfarbe als Hintergrund - wie unter Einstellungen > Personalisierung > Hintergrund.</summary>
    public static HintergrundGesetzt FarbeSetzen(RegistryKey wurzel, bool eigenerBenutzer, (byte R, byte G, byte B) farbe, string? profil = null)
    {
        int vd = FarbeEintragen(wurzel, farbe);
        if (!eigenerBenutzer) ZwischenspeicherVerwerfen(wurzel, profil);
        // Ohne Bild zeigt der Desktop die Farbe.
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "", Flags(eigenerBenutzer));
        bool angezeigt = SitzungsFarbe(farbe);

        bool eingetragen = IstFarbeEingetragen(wurzel, farbe);
        if (!eingetragen)
        {
            FarbeEintragen(wurzel, farbe);
            eingetragen = IstFarbeEingetragen(wurzel, farbe);
        }
        return new HintergrundGesetzt(eingetragen, angezeigt, vd);
    }

    /// <summary>Nur die Registry: Farbe, kein Bild, Art "Volltonfarbe".</summary>
    public static int FarbeEintragen(RegistryKey wurzel, (byte R, byte G, byte B) farbe)
    {
        using (var c = wurzel.CreateSubKey(FarbenSchluessel))
            c.SetValue("Background", Hintergrund.FarbeText(farbe));
        using (var k = wurzel.CreateSubKey(DesktopSchluessel))
            k.SetValue("WallPaper", "");
        TypEintragen(wurzel, 1);
        return DesktopBilderAngleichen(wurzel, null);
    }

    /// <summary>
    /// Windows' Kopie des bisherigen Hintergrunds verwerfen - die Datei
    /// TranscodedWallpaper und den Verweis darauf (TranscodedImageCache). Bei
    /// der naechsten Anmeldung liest Windows den Hintergrund dann aus der
    /// Registry neu ein, wie bei einem frischen Profil. Nur noetig, wenn das
    /// Tool unter einem anderen Konto laeuft: Sonst erneuert Windows die Kopie
    /// beim Setzen selbst.
    /// </summary>
    public static void ZwischenspeicherVerwerfen(RegistryKey wurzel, string? profil)
    {
        try
        {
            using var k = wurzel.OpenSubKey(DesktopSchluessel, writable: true);
            k?.DeleteValue("TranscodedImageCache", throwOnMissingValue: false);
        }
        catch { /* dann eben nur die Datei */ }
        try
        {
            if (Hintergrund.Zwischenspeicher(profil) is { } datei && System.IO.File.Exists(datei))
                System.IO.File.Delete(datei);
        }
        catch { /* gesperrt - Windows vergleicht ohnehin den Pfad */ }
    }

    private static bool IstEingetragen(RegistryKey wurzel, string pfad)
        => string.Equals(Lesen(wurzel).Bild, pfad, StringComparison.OrdinalIgnoreCase);

    private static bool IstFarbeEingetragen(RegistryKey wurzel, (byte R, byte G, byte B) farbe)
    {
        var s = Lesen(wurzel);
        return s.Bild is null && Hintergrund.FarbeLesen(s.Farbe) == farbe;
    }

    private static uint Flags(bool eigenerBenutzer)
        => eigenerBenutzer ? SPIF_UPDATEINIFILE | SPIF_SENDCHANGE : SPIF_SENDCHANGE;

    /// <summary>BackgroundType, damit Windows und die Einstellungen-App die richtige Art kennen: 0 Bild, 1 Volltonfarbe.</summary>
    private static void TypEintragen(RegistryKey wurzel, int typ)
    {
        try
        {
            using var k = wurzel.CreateSubKey(WallpapersSchluessel);
            k.SetValue("BackgroundType", typ, RegistryValueKind.DWord);
        }
        catch { /* aelteres Windows */ }
    }

    /// <summary>Die Farbe der laufenden Sitzung - gilt sofort, aber nur bis zur Abmeldung.</summary>
    private static bool SitzungsFarbe((byte R, byte G, byte B) farbe)
    {
        try { return SetSysColors(1, new[] { COLOR_DESKTOP }, new[] { Hintergrund.ColorRef(farbe) }); }
        catch { return false; }
    }

    private static List<string> DesktopBilderLesen(RegistryKey wurzel)
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
        catch { /* ohne virtuelle Desktops */ }
        return bilder;
    }

    /// <summary>
    /// Virtuelle Desktops mit eigenem Hintergrund bekommen den neuen - bei
    /// einer Volltonfarbe (bild null) faellt ihr eigener weg, sie zeigen dann
    /// den allgemeinen.
    /// </summary>
    private static int DesktopBilderAngleichen(RegistryKey wurzel, string? bild)
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
                catch { /* naechster */ }
            }
        }
        catch { /* ohne virtuelle Desktops */ }
        return n;
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
