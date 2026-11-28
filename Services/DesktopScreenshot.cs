using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>
/// A screenshot as raw pixels (B, G, R, A; rows top-down) - the whole
/// virtual screen, plus the areas of the individual monitors.
/// </summary>
public sealed record ScreenCapture(int Left, int Top, int Width, int Height, IReadOnlyList<MonitorArea> Areas, byte[] Bgra)
{
    public int Monitors => Areas.Count;
}

/// <summary>
/// A monitor, in pixels of the virtual screen (it can sit left of or above
/// the primary screen; then Left/Top are negative).
/// </summary>
/// <param name="Device">Windows device name, e.g. "\\.\DISPLAY2".</param>
public sealed record MonitorArea(int Left, int Top, int Width, int Height, bool Primary, string Device);

/// <summary>A monitor and the name of its image.</summary>
public sealed record MonitorImage(MonitorArea Area, int Number, string FileName);

/// <summary>The user signed in at this screen.</summary>
/// <param name="Sid">The user's security ID - for the user's registry key (HKEY_USERS\&lt;SID&gt;).</param>
public sealed record InteractiveUser(string Name, string? ProfilePath, string? Sid = null);

/// <summary>
/// Takes a picture of the desktop, so after a fresh install the user can
/// look up which icons were where.
///
/// Three things a plain "copy the screen" would fail on:
///
/// 1. SEVERAL MONITORS. The whole virtual screen is captured in one go -
///    all monitors show the same moment. From that, one image PER MONITOR
///    is saved, cropped exactly to its area. A single combined image had
///    black areas where there is no monitor when the monitors differed in
///    size, and was awkward to look at.
///
/// 2. SCALING. If a monitor is set to 150 %, Windows hands a thread that is
///    not DPI-aware scaled-down measurements: the picture would be blurry or
///    cropped. So for the capture, the thread is explicitly set to "per
///    monitor" and reset afterwards.
///
/// 3. WINDOWS IN FRONT. Left alone, the picture would mostly show the tool
///    itself. The program windows are moved out of the way briefly and
///    brought back exactly as they were - see HideWindows/RestoreWindows.
/// </summary>
public static class DesktopScreenshot
{
    /// <summary>Subfolder in the backup target.</summary>
    public const string FolderName = "Desktop-Screenshot";

    /// <summary>
    /// Earlier version: all monitors in one image. Still shown on restore if a
    /// backup contains only that one.
    /// </summary>
    public const string LegacyFileName = "Desktop.png";

    /// <summary>Start of the image names: "Bildschirm-1.png", "Bildschirm-2.png" ...</summary>
    public const string FilePrefix = "Bildschirm-";

    // ------------------------------------------------------------ One image per monitor

    /// <summary>
    /// Number and file name per monitor, sorted by number.
    ///
    /// The number is Windows' own ("\\.\DISPLAY2" -> 2) - usually the same one
    /// "Identify" shows in the display settings. If it is missing or appears
    /// twice, the monitors are counted: primary screen first, then left to
    /// right. With several monitors, the primary screen's image says so in its
    /// name.
    /// </summary>
    public static IReadOnlyList<MonitorImage> PlanImages(IReadOnlyList<MonitorArea> areas)
    {
        var nummern = areas.Select(a => DisplayNumber(a.Device)).ToList();
        bool windowsNummern = nummern.All(n => n > 0) && nummern.Distinct().Count() == nummern.Count;

        var reihe = windowsNummern
            ? areas.Select((a, i) => (a, nr: nummern[i])).ToList()
            : areas.OrderByDescending(a => a.Primary).ThenBy(a => a.Left).ThenBy(a => a.Top)
                   .Select((a, i) => (a, nr: i + 1)).ToList();

        bool mehrere = areas.Count > 1;
        return reihe.OrderBy(x => x.nr)
            .Select(x => new MonitorImage(x.a, x.nr,
                FilePrefix + x.nr.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (mehrere && x.a.Primary ? "-Hauptbildschirm" : "") + ".png"))
            .ToList();
    }

    /// <summary>"\\.\DISPLAY2" -> 2; 0 if it contains no number.</summary>
    public static int DisplayNumber(string? device)
    {
        if (string.IsNullOrEmpty(device)) return 0;
        int ende = device.Length;
        while (ende > 0 && char.IsAsciiDigit(device[ende - 1])) ende--;
        return ende < device.Length
               && int.TryParse(device.AsSpan(ende), System.Globalization.NumberStyles.None,
                               System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : 0;
    }

    /// <summary>
    /// Crops a rectangle out of a BGRA image. Whatever sticks out over the edge
    /// is dropped; if nothing lies inside the image, the result is empty (0 x 0).
    /// </summary>
    public static byte[] Crop(ReadOnlySpan<byte> bgra, int width, int height,
                              int x, int y, int w, int h, out int outWidth, out int outHeight)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(width, x + w), y1 = Math.Min(height, y + h);
        outWidth = Math.Max(0, x1 - x0);
        outHeight = Math.Max(0, y1 - y0);
        var ziel = new byte[(long)outWidth * outHeight * 4];
        int zeile = outWidth * 4;
        for (int r = 0; r < outHeight; r++)
            bgra.Slice(((y0 + r) * width + x0) * 4, zeile).CopyTo(ziel.AsSpan(r * zeile, zeile));
        return ziel;
    }

    /// <summary>
    /// The images of a backed-up desktop screenshot, in number order. If the
    /// folder holds only the earlier combined image, it is that one.
    /// </summary>
    public static IReadOnlyList<string> FindImages(string folder)
    {
        if (!System.IO.Directory.Exists(folder)) return Array.Empty<string>();
        var bilder = System.IO.Directory.GetFiles(folder, FilePrefix + "*.png")
            .Select(p => (p, nr: LeadingNumber(System.IO.Path.GetFileName(p)[FilePrefix.Length..])))
            .Where(x => x.nr > 0)
            .OrderBy(x => x.nr).ThenBy(x => x.p, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.p).ToList();
        if (bilder.Count > 0) return bilder;
        var alt = System.IO.Path.Combine(folder, LegacyFileName);
        return System.IO.File.Exists(alt) ? new[] { alt } : Array.Empty<string>();
    }

    private static int LeadingNumber(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i > 0 && int.TryParse(s.AsSpan(0, i), System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // ------------------------------------------------------------ Capture

    /// <summary>Captures all monitors in one go and remembers their areas.</summary>
    [SupportedOSPlatform("windows")]
    public static ScreenCapture CaptureVirtualScreen()
    {
        var vorher = IntPtr.Zero;
        try { vorher = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2); }
        catch (EntryPointNotFoundException) { /* older Windows: the manifest has to do */ }

        try
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Windows meldet keinen Bildschirm (kein angemeldeter Desktop?).");

            // In the same DPI state as the capture - otherwise the areas would not
            // match the pixels on scaled monitors.
            var monitore = EnumMonitors();
            if (monitore.Count == 0)
                monitore.Add(new MonitorArea(left, top, width, height, true, ""));

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
                        biHeight = -height,        // negative = rows top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0          // BI_RGB
                    }
                };
                bitmap = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new InvalidOperationException($"Speicher fuer {width} x {height} Bildpunkte nicht verfuegbar.");
                alt = SelectObject(memDc, bitmap);

                // CAPTUREBLT includes semi-transparent windows as well (taskbar).
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException($"Bildschirm konnte nicht gelesen werden (Win32-Fehler {Marshal.GetLastWin32Error()}).");

                var pixel = new byte[(long)width * height * 4];
                Marshal.Copy(bits, pixel, 0, pixel.Length);
                return new ScreenCapture(left, top, width, height, monitore, pixel);
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
                try { SetThreadDpiAwarenessContext(vorher); } catch { /* does not matter */ }
            }
        }
    }

    /// <summary>
    /// The desktop window itself (Progman, with the wallpaper) - via
    /// PrintWindow, so even when windows lie on top of it. null if the window
    /// does not exist or cannot be captured.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static byte[]? CaptureDesktopWindow()
    {
        var h = FindWindowW("Progman", null);
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
        int w = r.Right - r.Left, hoehe = r.Bottom - r.Top;
        if (w <= 0 || hoehe <= 0) return null;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = IntPtr.Zero; var bitmap = IntPtr.Zero; var alt = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -hoehe, biPlanes = 1, biBitCount = 32, biCompression = 0
                }
            };
            bitmap = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            alt = SelectObject(memDc, bitmap);
            if (!PrintWindow(h, memDc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            var pixel = new byte[(long)w * hoehe * 4];
            Marshal.Copy(bits, pixel, 0, pixel.Length);
            return pixel;
        }
        catch { return null; }
        finally
        {
            if (alt != IntPtr.Zero) SelectObject(memDc, alt);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Share of the pixels (sampled) that have roughly this color.</summary>
    public static double ColorShare(ReadOnlySpan<byte> bgra, (byte R, byte G, byte B) f, int toleranz = 24)
    {
        int pixel = bgra.Length / 4, schritt = Math.Max(1, pixel / 20000), treffer = 0, gesamt = 0;
        for (int i = 0; i < pixel; i += schritt)
        {
            int o = i * 4;
            gesamt++;
            if (Math.Abs(bgra[o] - f.B) <= toleranz && Math.Abs(bgra[o + 1] - f.G) <= toleranz && Math.Abs(bgra[o + 2] - f.R) <= toleranz)
                treffer++;
        }
        return gesamt == 0 ? 0 : (double)treffer / gesamt;
    }

    /// <summary>The most common color (rounded to 16 levels per channel) and its share, e.g. ("#6666DD", 0.98).</summary>
    public static (string Farbe, double Anteil) MostCommonColor(ReadOnlySpan<byte> bgra)
    {
        int pixel = bgra.Length / 4, schritt = Math.Max(1, pixel / 20000), gesamt = 0;
        var zaehler = new Dictionary<int, int>();
        for (int i = 0; i < pixel; i += schritt)
        {
            int o = i * 4;
            int k = ((bgra[o + 2] >> 4) << 8) | ((bgra[o + 1] >> 4) << 4) | (bgra[o] >> 4);
            zaehler[k] = zaehler.GetValueOrDefault(k) + 1;
            gesamt++;
        }
        if (gesamt == 0) return ("-", 0);
        var top = zaehler.OrderByDescending(x => x.Value).First();
        return ($"#{((top.Key >> 8) & 0xF) * 17:X2}{((top.Key >> 4) & 0xF) * 17:X2}{(top.Key & 0xF) * 17:X2}", (double)top.Value / gesamt);
    }

    /// <summary>All monitors with area, primary-screen flag and device name.</summary>
    [SupportedOSPlatform("windows")]
    private static List<MonitorArea> EnumMonitors()
    {
        var liste = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT __, IntPtr ___) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref info))
            {
                var r = info.rcMonitor;
                if (r.Right > r.Left && r.Bottom > r.Top)
                    liste.Add(new MonitorArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                                              (info.dwFlags & MONITORINFOF_PRIMARY) != 0, info.szDevice ?? ""));
            }
            return true;
        }, IntPtr.Zero);
        return liste;
    }

    /// <summary>
    /// true if the image is black throughout - which is what it looks like when
    /// the screen is locked or a UAC prompt holds the desktop. A sample is
    /// checked, not every pixel.
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

    // ------------------------------------------------------------ Windows out of the way

    /// <summary>What can be said about a window without touching it.</summary>
    public sealed record WindowInfo(
        string ClassName, bool Visible, bool Minimized, bool Cloaked,
        bool HasOwner, bool ToolWindow, bool AppWindow, int Width, int Height);

    /// <summary>The windows moved out of the way, top to bottom, and which one was active.</summary>
    public sealed record HiddenWindows(IReadOnlyList<IntPtr> TopToBottom, IntPtr Foreground);

    /// <summary>Windows of the Windows shell itself - they belong to the desktop.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"
    };

    /// <summary>
    /// Should this window be moved out of the way for the picture? The same
    /// selection Windows makes for Alt+Tab: visible program windows that are not
    /// minimized. Deliberately NOT included:
    ///  - windows of other virtual desktops (Windows "cloaks" them),
    ///  - owned windows (dialogs, palettes) - they follow their main window,
    ///  - tool windows without a taskbar entry of their own (tray menus etc.),
    ///  - the desktop and the taskbar themselves.
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
    /// Moves all program windows out of the way (minimized) and remembers their
    /// order and the active window.
    ///
    /// This used to be "Show desktop" (Win+D) and then once more. That is a
    /// toggle - if the focus changes in between, the second call does not bring
    /// the windows back but hides them again. Here exactly what was moved out of
    /// the way is brought back.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static HiddenWindows HideWindows()
    {
        var vordergrund = GetForegroundWindow();
        var liste = new List<IntPtr>();
        // EnumWindows returns the windows top to bottom.
        EnumWindows((h, _) =>
        {
            try { if (ShouldHide(Describe(h)) && !IsHungAppWindow(h)) liste.Add(h); }
            catch { /* one odd window must not prevent the capture */ }
            return true;
        }, IntPtr.Zero);

        // Asynchronous: a hung program must not stall the tool as well.
        foreach (var h in liste) ShowWindowAsync(h, SW_MINIMIZE);
        return new HiddenWindows(liste, vordergrund);
    }

    /// <summary>
    /// Brings the windows back: maximized ones maximized again, in the old
    /// stacking order, and the previously active window in the foreground
    /// again.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RestoreWindows(HiddenWindows versteckt)
    {
        var fenster = versteckt.TopToBottom;

        // 1. Uncover, bottom to top. SW_RESTORE restores a window to how it was
        //    before minimizing - maximized stays maximized.
        for (int i = fenster.Count - 1; i >= 0; i--)
            if (IsWindow(fenster[i]) && IsIconic(fenster[i])) ShowWindowAsync(fenster[i], SW_RESTORE);

        // The calls above run inside the programs themselves, each at its own
        // pace. Wait briefly, otherwise step 2 would come before the uncovering.
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        while (uhr.ElapsedMilliseconds < 3000 && fenster.Any(h => IsWindow(h) && IsIconic(h) && !IsHungAppWindow(h)))
            Thread.Sleep(50);

        // 2. Restore the stacking order: bring each one to the top, bottom to
        //    top - in the end the topmost one is on top again. On a thread of its
        //    own with a time limit, in case a program does not respond.
        var t = new Thread(() =>
        {
            for (int i = fenster.Count - 1; i >= 0; i--)
            {
                var h = fenster[i];
                if (!IsWindow(h) || IsHungAppWindow(h)) continue;
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            // 3. Make the previously active window active again.
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

    /// <summary>For the tests: is the window currently minimized / maximized?</summary>
    [SupportedOSPlatform("windows")]
    internal static (bool Minimiert, bool Maximiert) State(IntPtr h) => (IsIconic(h), IsZoomed(h));

    // ------------------------------------------------------------ Whose desktop?

    /// <summary>
    /// The user whose desktop is visible right now, with the profile folder.
    ///
    /// Deliberately NOT Environment.UserName: the tool runs with administrator
    /// rights. If an administrator enters another account in the UAC prompt, it
    /// would run under that name - but the desktop on screen is still the
    /// signed-in user's. So the session counts, not the process.
    /// </summary>
    /// <returns>null if it cannot be determined.</returns>
    [SupportedOSPlatform("windows")]
    public static InteractiveUser? GetInteractiveUser()
    {
        try
        {
            var name = QuerySession(WTSUserName);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var domain = QuerySession(WTSDomainName) ?? "";

            string? profil = null;
            string? sid = null;
            try
            {
                sid = new NTAccount(domain, name).Translate(typeof(SecurityIdentifier)).Value;
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
                if (key?.GetValue("ProfileImagePath") is string pfad)
                    profil = Environment.ExpandEnvironmentVariables(pfad);
            }
            catch { /* name known, profile not - enough for the message */ }

            return new InteractiveUser(string.IsNullOrEmpty(domain) ? name : $@"{domain}\{name}", profil, sid);
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
                      SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

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
