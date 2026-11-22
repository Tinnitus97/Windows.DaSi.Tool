using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
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
/// <param name="DesignDatei">Was mit der Design-Datei von Windows (Custom.theme) geschah.</param>
public sealed record HintergrundGesetzt(bool Eingetragen, bool Angezeigt, int VirtuelleDesktops, DesignDateiErgebnis? DesignDatei = null);

/// <param name="Datei">Die aktuelle Design-Datei, falls bekannt.</param>
/// <param name="Geaendert">Wurde sie angepasst?</param>
/// <param name="Grund">Warum nicht - null, wenn sie schon passte oder angepasst wurde.</param>
public sealed record DesignDateiErgebnis(string? Datei, bool Geaendert, string? Grund);

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
        // Wie die Einstellungen-App ueber den Explorer (IDesktopWallpaper) -
        // dann merkt er es sich auch selbst. Sonst bzw. wenn das nicht geht:
        // SystemParametersInfo.
        bool angezeigt = eigenerBenutzer && UeberExplorer(() => Schnittstelle.BildSetzen(pfad, e))
                         || SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, pfad, Flags(eigenerBenutzer));
        if (Hintergrund.FarbeLesen(e?.Farbe) is { } rand) SitzungsFarbe(rand);

        // Mit SPIF_UPDATEINIFILE schreibt Windows den Pfad selbst noch einmal -
        // was am Ende dasteht, zaehlt.
        bool eingetragen = IstEingetragen(wurzel, pfad);
        if (!eingetragen)
        {
            BildEintragen(wurzel, pfad, e);
            eingetragen = IstEingetragen(wurzel, pfad);
        }
        var datei = DesignDateiAngleichen(wurzel, profil, t => DesignDatei.FuerBild(t, pfad, e));
        return new HintergrundGesetzt(eingetragen, angezeigt, vd, datei);
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
        // Wie die Einstellungen-App ueber den Explorer (IDesktopWallpaper):
        // Unter Windows 11 merkt er sich die Farbe auch selbst und schrieb beim
        // Neustart sonst die alte zurueck. Sonst bzw. wenn das nicht geht:
        // ohne Bild zeigt der Desktop die Farbe der Sitzung.
        if (!(eigenerBenutzer && UeberExplorer(() => Schnittstelle.FarbeSetzen(farbe, abschalten: false))))
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "", Flags(eigenerBenutzer));
        bool angezeigt = SitzungsFarbe(farbe);

        bool eingetragen = IstFarbeEingetragen(wurzel, farbe);
        if (!eingetragen)
        {
            FarbeEintragen(wurzel, farbe);
            eingetragen = IstFarbeEingetragen(wurzel, farbe);
        }
        var datei = DesignDateiAngleichen(wurzel, profil, t => DesignDatei.FuerFarbe(t, farbe));
        return new HintergrundGesetzt(eingetragen, angezeigt, vd, datei);
    }

    /// <summary>true, wenn der Aufruf ueber IDesktopWallpaper geklappt hat.</summary>
    private static bool UeberExplorer(Action tun)
    {
        try { tun(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Traegt eine Aenderung auch in die aktuelle Design-Datei des Benutzers ein
    /// (CurrentTheme, meist ...\Themes\Custom.theme). Windows uebernimmt beim
    /// Anmelden Werte daraus wieder in die Registry - ohne diesen Schritt war
    /// eine Volltonfarbe nach dem Neustart wieder die alte. Angefasst wird nur
    /// eine Datei im Profil des Benutzers, nie ein Windows-Design unter
    /// C:\Windows.
    /// </summary>
    /// <param name="profil">Profilordner des Benutzers; null: der des Prozesses.</param>
    public static DesignDateiErgebnis DesignDateiAngleichen(RegistryKey wurzel, string? profil, Func<string, string> anpassen)
    {
        profil ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = profil.TrimEnd('\\', '/');
        string? pfad;
        try { pfad = DesignDateiPfad(wurzel, profil); }
        catch (Exception ex) { return new DesignDateiErgebnis(null, false, ex.Message); }
        if (pfad is null) return new DesignDateiErgebnis(null, false, "kein aktuelles Design eingetragen");
        if (!pfad.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
            return new DesignDateiErgebnis(pfad, false, "Windows-Design außerhalb des Profils - bleibt unverändert");
        if (!System.IO.File.Exists(pfad)) return new DesignDateiErgebnis(pfad, false, "Datei fehlt");
        try
        {
            var (inhalt, kodierung) = DesignDatei.DateiLesen(pfad);
            var neu = anpassen(inhalt);
            if (neu == inhalt) return new DesignDateiErgebnis(pfad, false, null);
            DesignDatei.DateiSchreiben(pfad, neu, kodierung);
            return new DesignDateiErgebnis(pfad, true, null);
        }
        catch (Exception ex) { return new DesignDateiErgebnis(pfad, false, ex.Message); }
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

    /// <summary>
    /// Zeigt der Desktop, was <paramref name="soll"/> sagt? Dafuer wird das
    /// Desktop-Fenster selbst abgebildet (auch unter anderen Fenstern).
    /// Volltonfarbe: Ueberwiegt die Farbe? Bild: Ist der Desktop nicht schwarz?
    /// </summary>
    /// <returns>null, wenn ja oder wenn es sich nicht pruefen laesst - sonst, was nicht stimmt.</returns>
    public static string? Kontrolle(HintergrundStand soll, Func<string, string, string> tr)
    {
        var bild = DesktopScreenshot.CaptureDesktopWindow();
        if (bild is null) return null;
        var (meist, anteil) = DesktopScreenshot.MostCommonColor(bild);
        // Bleibt das Abbild des Desktop-Fensters schwarz, kann das auch an
        // PrintWindow liegen - dann zaehlt es nur, wenn auch der Bildschirm
        // ueberwiegend schwarz ist.
        bool schwarz = DesktopScreenshot.IsBlank(bild);
        if (soll.Bild is null)
        {
            if (Hintergrund.FarbeLesen(soll.Farbe) is not { } f) return null;
            if (DesktopScreenshot.ColorShare(bild, f) >= 0.5) return null;
            if (schwarz && !SchirmSchwarz()) return null;
            return tr($"Der Desktop zeigt nicht die Farbe {Hintergrund.Hex(f)}, sondern meist {meist} ({anteil:P0})",
                      $"The desktop does not show the color {Hintergrund.Hex(f)} but mostly {meist} ({anteil:P0})");
        }
        return schwarz && SchirmSchwarz()
            ? tr($"Der Desktop ist schwarz statt des Bildes {soll.Bild}", $"The desktop is black instead of the image {soll.Bild}")
            : null;
    }

    private static bool SchirmSchwarz()
    {
        try { return DesktopScreenshot.ColorShare(DesktopScreenshot.CaptureVirtualScreen().Bgra, (0, 0, 0), 10) >= 0.5; }
        catch { return false; }
    }

    /// <summary>Was sich in der Registry gegenueber <paramref name="soll"/> geaendert hat - null, wenn nichts.</summary>
    public static string? Unterschied(HintergrundStand soll, HintergrundStand jetzt, Func<string, string, string> tr)
    {
        if (!string.Equals(soll.Bild ?? "", jetzt.Bild ?? "", StringComparison.OrdinalIgnoreCase))
            return tr($"Windows hat das Bild geändert: {soll.Bild ?? "(keins)"} -> {jetzt.Bild ?? "(keins)"}",
                      $"Windows changed the image: {soll.Bild ?? "(none)"} -> {jetzt.Bild ?? "(none)"}");
        if (soll.Bild is null && Hintergrund.FarbeLesen(soll.Farbe) != Hintergrund.FarbeLesen(jetzt.Farbe))
            return tr($"Windows hat die Farbe geändert: {soll.Farbe} -> {jetzt.Farbe}", $"Windows changed the color: {soll.Farbe} -> {jetzt.Farbe}");
        if (soll.Typ is { } t && jetzt.Typ != t)
            return tr($"Windows hat die Art geändert: {t} -> {jetzt.Typ?.ToString() ?? "-"} (0 Bild, 1 Farbe, 2 Diashow, 3 Blickpunkt)",
                      $"Windows changed the type: {t} -> {jetzt.Typ?.ToString() ?? "-"} (0 image, 1 color, 2 slideshow, 3 Spotlight)");
        return null;
    }

    /// <summary>Den Stand <paramref name="soll"/> noch einmal setzen.</summary>
    public static HintergrundGesetzt Erneut(RegistryKey wurzel, bool eigenerBenutzer, HintergrundStand soll, string? profil)
        => soll.Bild is { } b
            ? BildSetzen(wurzel, eigenerBenutzer, b, new HintergrundEinstellung { Darstellung = soll.Stil, Kacheln = soll.Kacheln }, profil)
            : FarbeSetzen(wurzel, eigenerBenutzer, Hintergrund.FarbeLesen(soll.Farbe) ?? ((byte)0, (byte)0, (byte)0), profil);

    /// <summary>Was den Hintergrund von aussen festlegen oder wechseln kann - Richtlinien, Blickpunkt, Diashow.</summary>
    /// <param name="mitWechsel">Auch Blickpunkt und Diashow nennen (die schaltet Windows beim Setzen normalerweise selbst ab).</param>
    public static List<string> Einfluesse(RegistryKey wurzel, bool mitWechsel, Func<string, string, string> tr)
    {
        var l = new List<string>();
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
            if (k?.GetValue("Wallpaper") is string w)
                l.Add(tr($"Eine Gruppenrichtlinie legt den Hintergrund fest: \"{w}\" - sie gewinnt bei jeder Anmeldung",
                         $"A group policy sets the background: \"{w}\" - it wins at every sign-in"));
        }
        catch { /* weiter */ }
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop");
            if (k?.GetValue("NoChangingWallPaper") is int n && n != 0)
                l.Add(tr("Eine Richtlinie verbietet das Ändern des Hintergrunds", "A policy prevents changing the background"));
        }
        catch { /* weiter */ }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP");
            if ((k?.GetValue("DesktopImagePath") ?? k?.GetValue("DesktopImageUrl")) is string p)
                l.Add(tr($"Die Geräteverwaltung (Intune/MDM) legt das Desktopbild fest: {p}", $"Device management (Intune/MDM) sets the desktop image: {p}"));
        }
        catch { /* weiter */ }
        if (!mitWechsel) return l;
        try
        {
            using var k = wurzel.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings");
            if (k?.GetValue("EnabledState") is int e && e != 0)
                l.Add(tr("Windows-Blickpunkt für den Desktop ist noch eingeschaltet", "Windows Spotlight for the desktop is still on"));
        }
        catch { /* weiter */ }
        if (Schnittstelle.Diashow() == true)
            l.Add(tr("Eine Diashow wechselt den Hintergrund", "A slideshow changes the background"));
        return l;
    }

    /// <summary>
    /// Die aktuelle Design-Datei des Benutzers (CurrentTheme), ausgeschrieben -
    /// %LOCALAPPDATA% &amp; Co. bezogen auf SEIN Profil, nicht das des Prozesses.
    /// null, wenn keine eingetragen ist.
    /// </summary>
    public static string? DesignDateiPfad(RegistryKey wurzel, string? profil)
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

    // ------------------------------------------------------------ IDesktopWallpaper

    /// <summary>
    /// Die Schnittstelle, ueber die auch die Einstellungen-App den Hintergrund
    /// setzt (Explorer). Wirkt fuer die laufende Sitzung und den Benutzer,
    /// unter dem das Tool laeuft.
    /// </summary>
    public static class Schnittstelle
    {
        /// <summary>Zustand laut Explorer, als Text - fuer Protokoll und Pruefung; null, wenn nicht erreichbar.</summary>
        public static string? Stand()
        {
            try
            {
                return Sta(dw =>
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

        /// <summary>Laeuft eine Diashow? null, wenn nicht erreichbar.</summary>
        public static bool? Diashow()
        {
            try { return Sta(dw => (dw.GetStatus() & 2) != 0); }   // DSS_SLIDESHOW
            catch { return null; }
        }

        /// <summary>Ein Bild fuer alle Monitore, samt Darstellung.</summary>
        public static void BildSetzen(string pfad, HintergrundEinstellung? e)
            => Sta(dw =>
            {
                if (Position(e) is { } pos) dw.SetPosition(pos);
                if (Hintergrund.FarbeLesen(e?.Farbe) is { } rand) dw.SetBackgroundColor((uint)Hintergrund.ColorRef(rand));
                dw.SetWallpaper(null, pfad);
                return true;
            });

        /// <summary>Volltonfarbe: Farbe setzen, Bild leeren (wie die Einstellungen-App).</summary>
        public static void FarbeSetzen((byte R, byte G, byte B) farbe, bool abschalten)
            => Sta(dw =>
            {
                dw.SetBackgroundColor((uint)Hintergrund.ColorRef(farbe));
                if (abschalten) dw.Enable(false);
                else dw.SetWallpaper(null, "");
                return true;
            });

        /// <summary>Hintergrundbild wieder einschalten (nach <see cref="FarbeSetzen"/> mit abschalten).</summary>
        public static void Einschalten() => Sta(dw => { dw.Enable(true); return true; });

        /// <summary>WallpaperStyle/TileWallpaper -> DESKTOP_WALLPAPER_POSITION.</summary>
        public static int? Position(HintergrundEinstellung? e)
        {
            if (e?.Kacheln == "1") return 1;               // Kacheln
            return e?.Darstellung switch
            {
                "0" => 0,   // Zentriert
                "2" => 2,   // Strecken
                "6" => 3,   // Anpassen
                "10" => 4,  // Fuellen
                "22" => 5,  // Strecken ueber Bildschirme
                _ => null,
            };
        }

        private static T Sta<T>(Func<IDesktopWallpaper, T> tun)
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
