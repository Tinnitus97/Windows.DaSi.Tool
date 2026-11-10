using System;
using System.Collections.Generic;
using System.Linq;
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
///    Die Programmfenster werden kurz beiseitegelegt und danach genau so
///    zurueckgeholt, wie sie waren - siehe HideWindows/RestoreWindows.
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

    /// <summary>Was sich ueber ein Fenster sagen laesst, ohne es anzufassen.</summary>
    public sealed record WindowInfo(
        string ClassName, bool Visible, bool Minimized, bool Cloaked,
        bool HasOwner, bool ToolWindow, bool AppWindow, int Width, int Height);

    /// <summary>Die beiseitegelegten Fenster, von oben nach unten, und welches aktiv war.</summary>
    public sealed record HiddenWindows(IReadOnlyList<IntPtr> TopToBottom, IntPtr Foreground);

    /// <summary>Fenster der Windows-Oberflaeche selbst - die gehoeren zum Desktop.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"
    };

    /// <summary>
    /// Soll dieses Fenster fuer das Foto beiseite? Dieselbe Auswahl, die Windows
    /// fuer Alt+Tab trifft: sichtbare, nicht minimierte Programmfenster.
    /// Bewusst NICHT dabei:
    ///  - Fenster anderer virtueller Desktops (Windows "verhuellt" sie, cloaked),
    ///  - untergeordnete Fenster (Dialoge, Paletten) - sie folgen ihrem Hauptfenster,
    ///  - Werkzeugfenster ohne eigenen Taskleisten-Eintrag (Tray-Menues u. a.),
    ///  - Desktop und Taskleiste selbst.
    /// </summary>
    public static bool ShouldHide(WindowInfo w)
    {
        if (!w.Visible || w.Minimized || w.Cloaked) return false;
        if (w.Width <= 0 || w.Height <= 0) return false;
        if (ShellClasses.Contains(w.ClassName)) return false;
        if (w.AppWindow) return true;
        if (w.HasOwner || w.ToolWindow) return false;
        return true;
    }

    /// <summary>
    /// Legt alle Programmfenster beiseite (minimiert) und merkt sich Reihenfolge
    /// und aktives Fenster.
    ///
    /// Frueher: "Desktop anzeigen" (Win+D) und danach erneut. Das ist ein
    /// Umschalter - aendert sich zwischendurch der Fokus, holt der zweite Aufruf
    /// die Fenster nicht zurueck, sondern blendet erneut aus. Hier wird genau
    /// das zurueckgeholt, was vorher beiseitegelegt wurde.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static HiddenWindows HideWindows()
    {
        var vordergrund = GetForegroundWindow();
        var liste = new List<IntPtr>();
        // EnumWindows liefert die Fenster von oben nach unten.
        EnumWindows((h, _) =>
        {
            try { if (ShouldHide(Describe(h)) && !IsHungAppWindow(h)) liste.Add(h); }
            catch { /* ein seltsames Fenster darf die Aufnahme nicht verhindern */ }
            return true;
        }, IntPtr.Zero);

        // Asynchron: Ein haengendes Programm darf das Tool nicht mit anhalten.
        foreach (var h in liste) ShowWindowAsync(h, SW_MINIMIZE);
        return new HiddenWindows(liste, vordergrund);
    }

    /// <summary>
    /// Holt die Fenster zurueck: maximierte wieder maximiert, in der alten
    /// Stapelreihenfolge, und das vorher aktive Fenster wieder im Vordergrund.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RestoreWindows(HiddenWindows versteckt)
    {
        var fenster = versteckt.TopToBottom;

        // 1. Aufdecken, von unten nach oben. SW_RESTORE stellt ein Fenster so
        //    wieder her, wie es vor dem Minimieren war - maximiert bleibt maximiert.
        for (int i = fenster.Count - 1; i >= 0; i--)
            if (IsWindow(fenster[i]) && IsIconic(fenster[i])) ShowWindowAsync(fenster[i], SW_RESTORE);

        // Die Aufrufe oben laufen in den Programmen selbst, jedes in seinem
        // Tempo. Kurz warten, sonst kaeme Schritt 2 dem Aufdecken zuvor.
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        while (uhr.ElapsedMilliseconds < 3000 && fenster.Any(h => IsWindow(h) && IsIconic(h) && !IsHungAppWindow(h)))
            Thread.Sleep(50);

        // 2. Stapelreihenfolge wiederherstellen: von unten nach oben jeweils
        //    nach oben legen - am Ende liegt das oberste wieder oben. Auf einem
        //    eigenen Thread mit Zeitlimit, falls ein Programm nicht antwortet.
        var t = new Thread(() =>
        {
            for (int i = fenster.Count - 1; i >= 0; i--)
            {
                var h = fenster[i];
                if (!IsWindow(h) || IsHungAppWindow(h)) continue;
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            // 3. Das vorher aktive Fenster wieder aktiv.
            if (versteckt.Foreground != IntPtr.Zero && IsWindow(versteckt.Foreground))
                SetForegroundWindow(versteckt.Foreground);
        }) { IsBackground = true, Name = "FensterZurueck" };
        t.Start();
        t.Join(10_000);
    }

    [SupportedOSPlatform("windows")]
    private static WindowInfo Describe(IntPtr h)
    {
        var klasse = new System.Text.StringBuilder(256);
        GetClassName(h, klasse, klasse.Capacity);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool verhuellt = DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        GetWindowRect(h, out var r);
        return new WindowInfo(
            klasse.ToString(), IsWindowVisible(h), IsIconic(h), verhuellt,
            GetWindow(h, GW_OWNER) != IntPtr.Zero,
            (ex & WS_EX_TOOLWINDOW) != 0, (ex & WS_EX_APPWINDOW) != 0,
            r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Fuer die Pruefungen: Ist das Fenster gerade minimiert / maximiert?</summary>
    [SupportedOSPlatform("windows")]
    internal static (bool Minimiert, bool Maximiert) State(IntPtr h) => (IsIconic(h), IsZoomed(h));

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

    private const int SW_MINIMIZE = 6, SW_RESTORE = 9, GWL_EXSTYLE = -20, GW_OWNER = 4, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
}
