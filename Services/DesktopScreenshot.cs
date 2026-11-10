using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>Ein Bildschirmfoto als rohe Bildpunkte (B, G, R, A; Zeilen von oben).</summary>
public sealed record ScreenCapture(int Left, int Top, int Width, int Height, int Monitors, byte[] Bgra);

/// <summary>Der an diesem Bildschirm angemeldete Benutzer.</summary>
public sealed record InteractiveUser(string Name, string? ProfilePath);

/// <summary>
/// Fotografiert den Desktop, damit sich nach einer Neuinstallation nachsehen
/// laesst, wo welche Symbole lagen.
///
/// Drei Dinge, an denen ein schlichtes "Bildschirm kopieren" scheitern wuerde:
///
/// 1. MEHRERE MONITORE. Aufgenommen wird der ganze virtuelle Bildschirm in
///    seiner echten Anordnung - ein Bild, auf dem man sieht, was auf welchem
///    Monitor lag.
///
/// 2. SKALIERUNG. Ist ein Monitor auf 150 % gestellt, liefert Windows einem
///    nicht DPI-bewussten Thread verkleinerte Masse: Das Foto waere unscharf
///    oder abgeschnitten. Fuer die Aufnahme wird der Thread deshalb
///    ausdruecklich auf "pro Monitor" gestellt und danach zurueckgesetzt.
///
/// 3. FENSTER DAVOR. Ohne Weiteres zeigte das Foto vor allem das Tool selbst.
///    Vorher wird "Desktop anzeigen" ausgeloest (wie Win+D) und danach erneut,
///    womit alle Fenster an ihren Platz zurueckkehren.
/// </summary>
public static class DesktopScreenshot
{
    /// <summary>Unterordner im Backup-Ziel.</summary>
    public const string FolderName = "Desktop-Screenshot";

    /// <summary>Dateiname des Fotos. Eine neue Sicherung ersetzt das alte Foto.</summary>
    public const string FileName = "Desktop.png";

    // ------------------------------------------------------------ Aufnahme

    /// <summary>Nimmt alle Monitore als ein Bild auf.</summary>
    [SupportedOSPlatform("windows")]
    public static ScreenCapture CaptureVirtualScreen()
    {
        var vorher = IntPtr.Zero;
        try { vorher = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2); }
        catch (EntryPointNotFoundException) { /* aelteres Windows: Manifest muss reichen */ }

        try
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int monitors = Math.Max(1, GetSystemMetrics(SM_CMONITORS));
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Windows meldet keinen Bildschirm (kein angemeldeter Desktop?).");

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("Kein Zugriff auf den Bildschirm.");
            var memDc = IntPtr.Zero; var bitmap = IntPtr.Zero; var alt = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                var info = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,        // negativ = Zeilen von oben nach unten
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0          // BI_RGB
                    }
                };
                bitmap = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new InvalidOperationException($"Speicher fuer {width} x {height} Bildpunkte nicht verfuegbar.");
                alt = SelectObject(memDc, bitmap);

                // CAPTUREBLT nimmt auch halbdurchsichtige Fenster mit (Taskleiste).
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException($"Bildschirm konnte nicht gelesen werden (Win32-Fehler {Marshal.GetLastWin32Error()}).");

                var pixel = new byte[(long)width * height * 4];
                Marshal.Copy(bits, pixel, 0, pixel.Length);
                return new ScreenCapture(left, top, width, height, monitors, pixel);
            }
            finally
            {
                if (alt != IntPtr.Zero) SelectObject(memDc, alt);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally
        {
            if (vorher != IntPtr.Zero)
            {
                try { SetThreadDpiAwarenessContext(vorher); } catch { /* egal */ }
            }
        }
    }

    /// <summary>
    /// true, wenn das Bild durchgehend schwarz ist - so sieht es aus, wenn der
    /// Bildschirm gesperrt ist oder gerade eine UAC-Abfrage den Desktop belegt.
    /// Geprueft wird eine Stichprobe, nicht jeder Bildpunkt.
    /// </summary>
    public static bool IsBlank(ReadOnlySpan<byte> bgra)
    {
        int pixel = bgra.Length / 4;
        if (pixel == 0) return true;
        int schritt = Math.Max(1, pixel / 4000);
        for (int i = 0; i < pixel; i += schritt)
        {
            int o = i * 4;
            if (bgra[o] > 8 || bgra[o + 1] > 8 || bgra[o + 2] > 8) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ Fenster beiseite

    /// <summary>
    /// "Desktop anzeigen" (wie Win+D). Ein zweiter Aufruf holt alle Fenster
    /// zurueck. Laeuft auf einem eigenen STA-Thread, weil Shell.Application ein
    /// COM-Objekt der Oberflaeche ist; nach 10 s wird aufgegeben.
    /// </summary>
    /// <returns>true, wenn der Aufruf bei Windows angekommen ist.</returns>
    [SupportedOSPlatform("windows")]
    public static bool ToggleDesktop()
    {
        bool ok = false;
        var t = new Thread(() =>
        {
            object? shell = null;
            try
            {
                var typ = Type.GetTypeFromProgID("Shell.Application");
                if (typ is null) return;
                shell = Activator.CreateInstance(typ);
                if (shell is null) return;
                typ.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
                ok = true;
            }
            catch { /* ok bleibt false */ }
            finally
            {
                if (shell is not null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        })
        { IsBackground = true, Name = "DesktopAnzeigen" };
        try
        {
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t.Join(10_000) && ok;
        }
        catch
        {
            // Ohne COM (oder ohne Thread) bleibt es beim Foto mit Fenstern davor.
            return false;
        }
    }

    // ------------------------------------------------------------ Wessen Desktop?

    /// <summary>
    /// Der Benutzer, dessen Desktop gerade zu sehen ist, samt Profilordner.
    ///
    /// Bewusst NICHT Environment.UserName: Das Tool laeuft mit
    /// Administratorrechten. Meldet sich ein Administrator per UAC-Abfrage an
    /// einem fremden Konto an, liefe es unter dessen Namen - zu sehen ist aber
    /// weiterhin der Desktop des angemeldeten Benutzers. Massgeblich ist
    /// deshalb die Sitzung, nicht der Prozess.
    /// </summary>
    /// <returns>null, wenn sich das nicht ermitteln laesst.</returns>
    [SupportedOSPlatform("windows")]
    public static InteractiveUser? GetInteractiveUser()
    {
        try
        {
            var name = QuerySession(WTSUserName);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var domain = QuerySession(WTSDomainName) ?? "";

            string? profil = null;
            try
            {
                var sid = new NTAccount(domain, name).Translate(typeof(SecurityIdentifier)).Value;
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
                if (key?.GetValue("ProfileImagePath") is string pfad)
                    profil = Environment.ExpandEnvironmentVariables(pfad);
            }
            catch { /* Name bekannt, Profil nicht - das reicht fuer die Meldung */ }

            return new InteractiveUser(string.IsNullOrEmpty(domain) ? name : $@"{domain}\{name}", profil);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? QuerySession(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, infoClass, out var puffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(puffer); }
        finally { WTSFreeMemory(puffer); }
    }

    // ------------------------------------------------------------ Win32

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                      SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79, SM_CMONITORS = 80;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private const int WTS_CURRENT_SESSION = -1, WTSUserName = 5, WTSDomainName = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int xSrc, int ySrc, uint rop);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
