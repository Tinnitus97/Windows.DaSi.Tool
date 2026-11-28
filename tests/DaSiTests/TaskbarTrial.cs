using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32;
using WindowsDaSiTool.Services;

namespace DaSiTests;

/// <summary>
/// Trial on real Windows: what does Explorer do with restored taskbar
/// icons? Reason: after a restore (backup from another PC) the taskbar was
/// empty, although shortcuts and Taskband values had been restored and the
/// log reported "success".
///
/// Prints the state (Taskband values, shortcuts with their targets, visible
/// taskbar buttons) and plays through: back up, clear, restore - once
/// unchanged, once with a shortcut whose target does not exist. At the end
/// the original state comes back.
/// </summary>
internal static partial class Program
{
    [SupportedOSPlatform("windows")]
    private static void TaskbarTrial(string root)
    {
        Console.WriteLine("\n== Taskleiste: Versuch am echten Windows ==");
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            Console.WriteLine($"         Windows: {nt?.GetValue("ProductName")} {nt?.GetValue("DisplayVersion")}, Build {nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")}");
        if (!ExplorerRestart.TaskbarPresent())
        {
            Console.WriteLine("         (keine Taskleiste auf diesem Laeufer - uebersprungen)");
            return;
        }

        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pinOrdner = Taskbar.PinFolder(profil);
        Directory.CreateDirectory(pinOrdner);
        var original = ReadTaskband();
        var originalLnk = Path.Combine(root, "tl-original");
        Directory.CreateDirectory(originalLnk);
        foreach (var n in Taskbar.Shortcuts(pinOrdner)) File.Copy(Path.Combine(pinOrdner, n), Path.Combine(originalLnk, n));

        try
        {
            var knoepfeVorher = DumpTaskbar("Ausgangslage", pinOrdner, mitInhalt: true);

            // 1. Back up through the service.
            var c = new CancellationTokenState();
            var log = new Protokoll();
            var sicherung = Path.Combine(root, "tl-sicherung");
            new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = sicherung }.BackupDesktopIcons().Wait();
            foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("         Dienst: " + z.Trim());
            // Keep it for another runner: restore on another PC.
            if (Environment.GetEnvironmentVariable("DASI_TASKLEISTE_ABLAGE") is { Length: > 0 } ablage && Directory.Exists(sicherung))
            {
                foreach (var datei in Directory.GetFiles(sicherung, "*", SearchOption.AllDirectories))
                {
                    var ziel = Path.Combine(ablage, Path.GetRelativePath(sicherung, datei));
                    Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
                    File.Copy(datei, ziel, true);
                }
                Console.WriteLine($"         Sicherung abgelegt: {ablage}");
            }

            // 2. Clear and restart Explorer.
            ClearTaskbar(pinOrdner);
            DumpTaskbar("geleert", pinOrdner, mitInhalt: false);

            // 3. Restore through the service.
            log.Clear();
            new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = sicherung }.RestoreDesktopIcons().Wait();
            foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) || z.Contains("Explorer"))) Console.WriteLine("         Dienst: " + z.Trim());
            var geschrieben = ReadTaskband();
            var knoepfeDanach = DumpTaskbar("wiederhergestellt", pinOrdner, mitInhalt: false);
            System.Threading.Thread.Sleep(10000);
            var spaeter = ReadTaskband();
            Console.WriteLine($"         Favorites 10 s spaeter: {(SameValue(geschrieben, spaeter, "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
            DumpTaskbar("10 s spaeter", pinOrdner, mitInhalt: false);
            Console.WriteLine($"         Knoepfe wie vorher: {(knoepfeVorher.SequenceEqual(knoepfeDanach) ? "ja" : "NEIN")}");

            // 4. A shortcut whose target does not exist here (like Firefox from
            //    "Program Files (x86)" on a PC with 64-bit Firefox).
            var tlSicherung = Taskbar.BackupFolder(sicherung);
            var tlJson = Taskbar.Load(sicherung);
            if (Directory.Exists(tlSicherung) && tlJson is not null)
            {
                string? umgebogen = null;
                foreach (var lnk in Directory.GetFiles(tlSicherung, "*.lnk"))
                {
                    var ziel = LinkTarget(lnk, mitArgumenten: false);
                    if (string.IsNullOrEmpty(ziel) || !File.Exists(ziel)) continue;
                    SetLinkTarget(lnk, Path.Combine(@"C:\Program Files (x86)\DaSi-gibt-es-nicht", Path.GetFileName(ziel)));
                    umgebogen = Path.GetFileName(lnk);
                    Console.WriteLine($"         {umgebogen}: Ziel umgebogen auf {LinkTarget(lnk)}");
                    break;
                }
                ClearTaskbar(pinOrdner);
                log.Clear();
                new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = sicherung }.RestoreDesktopIcons().Wait();
                var geschrieben2 = ReadTaskband();
                DumpTaskbar("mit fehlendem Ziel", pinOrdner, mitInhalt: false);
                System.Threading.Thread.Sleep(10000);
                Console.WriteLine($"         Favorites 10 s spaeter: {(SameValue(geschrieben2, ReadTaskband(), "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
                DumpTaskbar("mit fehlendem Ziel, 10 s spaeter", pinOrdner, mitInhalt: false);

                // 4b. The shortcut itself is missing (Taskband points to nothing).
                if (umgebogen is not null)
                {
                    File.Delete(Path.Combine(tlSicherung, umgebogen));
                    ClearTaskbar(pinOrdner);
                    log.Clear();
                    new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = sicherung }.RestoreDesktopIcons().Wait();
                    foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) || z.Contains("fehlen"))) Console.WriteLine("         Dienst: " + z.Trim());
                    var geschrieben3 = ReadTaskband();
                    DumpTaskbar("Verknuepfung fehlt", pinOrdner, mitInhalt: false);
                    System.Threading.Thread.Sleep(10000);
                    var danach3 = ReadTaskband();
                    Console.WriteLine($"         Favorites 10 s spaeter: {(SameValue(geschrieben3, danach3, "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
                    DumpTaskbar("Verknuepfung fehlt, 10 s spaeter", pinOrdner, mitInhalt: true);
                }
            }

            // 6. Does the taskbar match by the app ID (AppUserModelID) in Favorites?
            //    Bend the Edge entry to an unknown ID (same length), then add a
            //    program with that ID.
            var tlJson2 = Taskbar.Load(sicherung);
            if (tlJson2 is not null && tlJson2.Taskband.Any(e => e.Name == "Favorites"))
            {
                var unbekannt = Path.Combine(root, "tl-unbekannt");
                foreach (var datei in Directory.GetFiles(sicherung, "*", SearchOption.AllDirectories))
                {
                    var z = Path.Combine(unbekannt, Path.GetRelativePath(sicherung, datei));
                    Directory.CreateDirectory(Path.GetDirectoryName(z)!);
                    File.Copy(datei, z, true);
                }
                var umgebaut = Taskbar.Load(unbekannt)!;
                foreach (var e in umgebaut.Taskband.Where(e => e.Typ == "Binary"))
                {
                    var b = Convert.FromBase64String(e.Wert);
                    var alt2 = System.Text.Encoding.Unicode.GetBytes("MSEdge");
                    var neu2 = System.Text.Encoding.Unicode.GetBytes("DaSiXY");
                    int n2 = 0;
                    for (int i = 0; i + alt2.Length <= b.Length; i++)
                        if (b.AsSpan(i, alt2.Length).SequenceEqual(alt2)) { neu2.CopyTo(b, i); n2++; }
                    e.Wert = Convert.ToBase64String(b);
                    Console.WriteLine($"         {e.Name}: MSEdge -> DaSiXY ({n2}x)");
                }
                Taskbar.Save(unbekannt, umgebaut);
                // The Edge shortcut itself is missing since step 4b - Edge hangs on the ID alone.
                ClearTaskbar(pinOrdner);
                log.Clear();
                new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = unbekannt }.RestoreDesktopIcons().Wait();
                foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("         Dienst: " + z.Trim());
                var geschrieben6 = ReadTaskband();
                DumpTaskbar("App-Kennung unbekannt", pinOrdner, mitInhalt: false);
                System.Threading.Thread.Sleep(10000);
                var danach6 = ReadTaskband();
                var fav6 = danach6.FirstOrDefault(e => e.Name == "Favorites");
                Console.WriteLine($"         Favorites 10 s spaeter: {(SameValue(geschrieben6, danach6, "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}, "
                    + $"nennt DaSiXY: {(fav6 is not null && System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(fav6.Wert)).Contains("DaSiXY"))}");
                DumpTaskbar("App-Kennung unbekannt, 10 s spaeter", pinOrdner, mitInhalt: true);

                // A program with that ID in the Start menu.
                var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DaSi Probe.lnk");
                var edge = new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }.FirstOrDefault(File.Exists);
                if (edge is not null)
                {
                    Shortcut.Create(start, edge, "DaSiXY");
                    Console.WriteLine($"         Startmenue: {start} -> {edge}, Kennung {Shortcut.Read(start)?.AppId}");
                    System.Threading.Thread.Sleep(5000);
                    DumpTaskbar("Programm mit der Kennung nachgereicht (ohne Neustart)", pinOrdner, mitInhalt: false);
                    ExplorerRestart.Restart(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                    System.Threading.Thread.Sleep(5000);
                    DumpTaskbar("Programm mit der Kennung nachgereicht (nach Explorer-Neustart)", pinOrdner, mitInhalt: false);
                    File.Delete(start);
                }
            }

            // 7. Entries in the format of an old Windows 10 (root without the
            //    extension block, 20 instead of 58 bytes) - does Windows take them?
            if (Taskbar.Load(sicherung) is { } tl7 && Taskbar.Favorites(tl7.Taskband) is { } fav7)
            {
                var alt7 = new List<byte> { };
                int pos = 0, umgebaut7 = 0;
                while (pos + 5 <= fav7.Length && fav7[pos] == 0x00)
                {
                    var laenge = (int)BitConverter.ToUInt32(fav7, pos + 1);
                    var daten = fav7.AsSpan(pos + 5, laenge).ToArray();
                    if (daten.Length > 20 && daten[0] == 0x3A && daten[1] == 0 && daten[2] == 0x1F)
                    {
                        var kurz = new List<byte> { 0x14, 0x00 };
                        kurz.AddRange(daten.AsSpan(2, 18).ToArray());
                        kurz.AddRange(daten.AsSpan(0x3A).ToArray());
                        daten = kurz.ToArray();
                        umgebaut7++;
                    }
                    alt7.Add(0x00);
                    alt7.AddRange(BitConverter.GetBytes((uint)daten.Length));
                    alt7.AddRange(daten);
                    pos += 5 + laenge;
                }
                alt7.AddRange(fav7.AsSpan(pos).ToArray());
                var ordner7 = Path.Combine(root, "tl-altes-format");
                foreach (var datei in Directory.GetFiles(sicherung, "*", SearchOption.AllDirectories))
                {
                    var z = Path.Combine(ordner7, Path.GetRelativePath(sicherung, datei));
                    Directory.CreateDirectory(Path.GetDirectoryName(z)!);
                    File.Copy(datei, z, true);
                }
                tl7.Taskband.First(e => e.Name == "Favorites").Wert = Convert.ToBase64String(alt7.ToArray());
                Taskbar.Save(ordner7, tl7);
                Console.WriteLine($"   == altes Format: {umgebaut7} Eintraege umgebaut, {fav7.Length} -> {alt7.Count} Bytes ==");
                ClearTaskbar(pinOrdner);
                log.Clear();
                new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = ordner7 }.RestoreDesktopIcons().Wait();
                foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("         Dienst: " + z.Trim());
                DumpTaskbar("altes Format", pinOrdner, mitInhalt: false);
            }

            // 5. The backup of another PC (another runner, another Windows).
            if (Environment.GetEnvironmentVariable("DASI_TASKLEISTE_FREMD") is { Length: > 0 } fremd && Directory.Exists(fremd))
            {
                Console.WriteLine($"   == Sicherung eines anderen PCs: {fremd} ==");
                var tl = Taskbar.Load(fremd);
                Console.WriteLine($"         gesichert auf {tl?.Rechner}: {string.Join(", ", tl?.Verknuepfungen ?? new List<string>())}, Taskband: {string.Join(", ", tl?.Taskband.Select(e => e.Name) ?? Array.Empty<string>())}");
                ClearTaskbar(pinOrdner);
                log.Clear();
                new BackupService(new ProcessRunner(c), c, log.Write) { SourcePath = profil, BackupPath = fremd }.RestoreDesktopIcons().Wait();
                foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) || z.Contains("Explorer"))) Console.WriteLine("         Dienst: " + z.Trim());
                var geschriebenFremd = ReadTaskband();
                DumpTaskbar("fremde Sicherung wiederhergestellt", pinOrdner, mitInhalt: true);
                System.Threading.Thread.Sleep(10000);
                Console.WriteLine($"         Favorites 10 s spaeter: {(SameValue(geschriebenFremd, ReadTaskband(), "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
                DumpTaskbar("fremde Sicherung, 10 s spaeter", pinOrdner, mitInhalt: true);
                ExplorerRestart.Restart(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(5000);
                DumpTaskbar("fremde Sicherung, nach noch einem Explorer-Neustart", pinOrdner, mitInhalt: false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("         Versuch abgebrochen: " + ex);
        }
        finally
        {
            // Back to the original state.
            try
            {
                foreach (var alt in Directory.GetFiles(pinOrdner, "*.lnk")) File.Delete(alt);
                foreach (var n in Directory.GetFiles(originalLnk)) File.Copy(n, Path.Combine(pinOrdner, Path.GetFileName(n)), true);
                using (var k = Registry.CurrentUser.CreateSubKey(Taskbar.TaskbandSchluessel))
                    foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
                foreach (var e in original) DesignRegistry.Write(Registry.CurrentUser, e);
                ExplorerRestart.Restart(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                DumpTaskbar("Ausgangslage zurueck", pinOrdner, mitInhalt: false);
            }
            catch (Exception ex) { Console.WriteLine("         Ausgangslage NICHT zurueck: " + ex.Message); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<DesignEintrag> ReadTaskband()
    {
        var erg = new List<DesignEintrag>();
        using var k = Registry.CurrentUser.OpenSubKey(Taskbar.TaskbandSchluessel);
        foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
            if (DesignRegistry.Read(Registry.CurrentUser, Taskbar.TaskbandSchluessel, name) is { } v)
                erg.Add(new DesignEintrag { Gruppe = "Taskleiste", Schluessel = Taskbar.TaskbandSchluessel, Name = name, Typ = v.Typ, Wert = v.Wert });
        return erg;
    }

    private static bool SameValue(List<DesignEintrag> a, List<DesignEintrag> b, string name)
        => a.FirstOrDefault(e => e.Name == name)?.Wert == b.FirstOrDefault(e => e.Name == name)?.Wert;

    [SupportedOSPlatform("windows")]
    private static void ClearTaskbar(string pinOrdner)
    {
        foreach (var alt in Directory.GetFiles(pinOrdner, "*.lnk")) File.Delete(alt);
        using (var k = Registry.CurrentUser.CreateSubKey(Taskbar.TaskbandSchluessel))
            foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
        var (ok, _) = ExplorerRestart.Restart(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Console.WriteLine($"         geleert, Explorer neu gestartet: {ok}");
        System.Threading.Thread.Sleep(3000);
    }

    /// <summary>Prints the state and returns the names of the visible app buttons.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> DumpTaskbar(string wann, string pinOrdner, bool mitInhalt)
    {
        Console.WriteLine($"   -- {wann} --");
        foreach (var e in ReadTaskband())
        {
            var laenge = e.Typ == "Binary" ? Convert.FromBase64String(e.Wert).Length + " Bytes" : e.Wert;
            Console.WriteLine($"         Taskband {e.Name} ({e.Typ}): {laenge}");
            if (mitInhalt && e.Typ == "Binary")
            {
                var b = Convert.FromBase64String(e.Wert);
                Console.WriteLine($"           Texte (UTF-16): {string.Join(" | ", ExtractStrings(b, unicode: true))}");
                Console.WriteLine($"           Texte (ASCII):  {string.Join(" | ", ExtractStrings(b, unicode: false))}");
                for (int i = 0; i < Math.Min(b.Length, 1024); i += 32)
                    Console.WriteLine($"           {i:X4}: {Convert.ToHexString(b, i, Math.Min(32, b.Length - i))}");
            }
        }
        foreach (var n in Taskbar.Shortcuts(pinOrdner))
            Console.WriteLine($"         Verknuepfung {n} -> {LinkTarget(Path.Combine(pinOrdner, n))}");
        var knoepfe = new List<string>();
        try
        {
            var tray = AutomationElement.FromHandle(FindWindowW("Shell_TrayWnd", null));
            var alle = tray.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            Console.WriteLine($"         UIA: {alle.Count} Elemente in der Taskleiste");
            foreach (AutomationElement el in alle)
            {
                var info = el.Current;
                if (info.ControlType != ControlType.Button && info.ControlType != ControlType.ListItem && info.ControlType != ControlType.MenuItem) continue;
                Console.WriteLine($"         {info.ControlType.ProgrammaticName} \"{info.Name}\" [{info.ClassName}] {info.AutomationId}");
                if (info.ClassName.Contains("TaskListButton", StringComparison.OrdinalIgnoreCase)
                    || info.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase))
                    knoepfe.Add(info.Name);
            }
        }
        catch (Exception ex) { Console.WriteLine("         UIA: " + ex.Message); }
        TaskbarScreenshot(wann);
        knoepfe.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"         App-Knoepfe: {(knoepfe.Count == 0 ? "keine" : string.Join(", ", knoepfe))}");
        var com = TaskbarView.AppIds();
        Console.WriteLine($"         Kennungen (TaskbarView): {(com is null ? "nicht lesbar" : string.Join(", ", com.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)))}");
        return knoepfe;
    }

    /// <summary>Takes a picture of the taskbar and prints it as PNG (Base64) on one line.</summary>
    [SupportedOSPlatform("windows")]
    private static void TaskbarScreenshot(string name)
    {
        try
        {
            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out var r)) { Console.WriteLine("         (keine Taskleiste fuers Foto)"); return; }
            var bild = DesktopScreenshot.CaptureVirtualScreen();
            var teil = DesktopScreenshot.Crop(bild.Bgra, bild.Width, bild.Height, r.Left - bild.Left, r.Top - bild.Top,
                                              r.Right - r.Left, r.Bottom - r.Top, out var w, out var h);
            if (w == 0 || h == 0) { Console.WriteLine("         (Taskleiste ausserhalb des Bildschirms)"); return; }
            Console.WriteLine($"DASI-FOTO {name.Replace(' ', '_')} {Convert.ToBase64String(PngWriter.EncodeBgra(w, h, teil))}");
        }
        catch (Exception ex) { Console.WriteLine("         Foto: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private static IEnumerable<string> ExtractStrings(byte[] b, bool unicode)
    {
        var erg = new List<string>();
        var sb = new StringBuilder();
        int schritt = unicode ? 2 : 1;
        for (int i = 0; i + schritt <= b.Length; i += schritt)
        {
            char ch = unicode ? (char)(b[i] | (b[i + 1] << 8)) : (char)b[i];
            if (ch >= 0x20 && ch < 0x7F) sb.Append(ch);
            else { if (sb.Length >= 4) erg.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= 4) erg.Add(sb.ToString());
        return erg.Distinct().Take(60);
    }

    [SupportedOSPlatform("windows")]
    private static string LinkTarget(string lnk, bool mitArgumenten = true)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            return sb + (mitArgumenten && args.Length > 0 ? " " + args : "");
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    [SupportedOSPlatform("windows")]
    private static void SetLinkTarget(string lnk, string ziel)
    {
        var link = (IShellLinkW)new CShellLink();
        ((IPersistFile)link).Load(lnk, 2 /* STGM_READWRITE */);
        link.SetPath(ziel);
        ((IPersistFile)link).Save(lnk, true);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
