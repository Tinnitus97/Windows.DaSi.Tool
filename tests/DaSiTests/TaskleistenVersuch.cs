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
/// Versuch am echten Windows: Was macht der Explorer mit zurueckgeschriebenen
/// Taskleisten-Symbolen? Anlass: Nach einer Wiederherstellung (Sicherung von
/// einem anderen PC) war die Taskleiste leer, obwohl Verknuepfungen und
/// Taskband-Werte zurueckgeschrieben waren und das Protokoll "Erfolg" meldete.
///
/// Gibt den Zustand aus (Taskband-Werte, Verknuepfungen samt Ziel, sichtbare
/// Knoepfe der Taskleiste) und spielt durch: sichern, leeren, wiederherstellen
/// - einmal unveraendert, einmal mit einer Verknuepfung, deren Ziel es nicht
/// gibt. Am Ende kommt der Ausgangszustand zurueck.
/// </summary>
internal static partial class Program
{
    [SupportedOSPlatform("windows")]
    private static void TaskleisteVersuch(string root)
    {
        Console.WriteLine("\n== Taskleiste: Versuch am echten Windows ==");
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            Console.WriteLine($"         Windows: {nt?.GetValue("ProductName")} {nt?.GetValue("DisplayVersion")}, Build {nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")}");
        if (!ExplorerNeustart.TaskleisteDa())
        {
            Console.WriteLine("         (keine Taskleiste auf diesem Laeufer - uebersprungen)");
            return;
        }

        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pinOrdner = Taskleiste.PinOrdner(profil);
        Directory.CreateDirectory(pinOrdner);
        var original = TaskbandLesen();
        var originalLnk = Path.Combine(root, "tl-original");
        Directory.CreateDirectory(originalLnk);
        foreach (var n in Taskleiste.Verknuepfungen(pinOrdner)) File.Copy(Path.Combine(pinOrdner, n), Path.Combine(originalLnk, n));

        try
        {
            var knoepfeVorher = TaskleisteZeigen("Ausgangslage", pinOrdner, mitInhalt: true);

            // 1. Sichern ueber den Dienst.
            var c = new CancellationTokenState();
            var log = new Protokoll();
            var sicherung = Path.Combine(root, "tl-sicherung");
            new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung }.BackupDesktopIcons().Wait();
            foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("         Dienst: " + z.Trim());
            // Fuer einen anderen Laeufer ablegen: Wiederherstellen auf einem anderen PC.
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

            // 2. Leeren und den Explorer neu starten.
            Leeren(pinOrdner);
            TaskleisteZeigen("geleert", pinOrdner, mitInhalt: false);

            // 3. Wiederherstellen ueber den Dienst.
            log.Leeren();
            new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung }.RestoreDesktopIcons().Wait();
            foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) || z.Contains("Explorer"))) Console.WriteLine("         Dienst: " + z.Trim());
            var geschrieben = TaskbandLesen();
            var knoepfeDanach = TaskleisteZeigen("wiederhergestellt", pinOrdner, mitInhalt: false);
            System.Threading.Thread.Sleep(10000);
            var spaeter = TaskbandLesen();
            Console.WriteLine($"         Favorites 10 s spaeter: {(Gleich(geschrieben, spaeter, "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
            TaskleisteZeigen("10 s spaeter", pinOrdner, mitInhalt: false);
            Console.WriteLine($"         Knoepfe wie vorher: {(knoepfeVorher.SequenceEqual(knoepfeDanach) ? "ja" : "NEIN")}");

            // 4. Eine Verknuepfung, deren Ziel es hier nicht gibt (wie Firefox
            //    aus "Program Files (x86)" auf einem PC mit 64-Bit-Firefox).
            var tlSicherung = Taskleiste.SicherungsOrdner(sicherung);
            foreach (var lnk in Directory.GetFiles(tlSicherung, "*.lnk"))
            {
                var ziel = LinkZiel(lnk);
                if (string.IsNullOrEmpty(ziel) || !File.Exists(ziel)) continue;
                var falsch = Path.Combine(@"C:\Program Files (x86)\DaSi-gibt-es-nicht", Path.GetFileName(ziel));
                LinkZielSetzen(lnk, falsch);
                Console.WriteLine($"         {Path.GetFileName(lnk)}: Ziel umgebogen auf {LinkZiel(lnk)}");
                break;
            }
            Leeren(pinOrdner);
            log.Leeren();
            new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = profil, BackupPath = sicherung }.RestoreDesktopIcons().Wait();
            var geschrieben2 = TaskbandLesen();
            TaskleisteZeigen("mit fehlendem Ziel", pinOrdner, mitInhalt: false);
            System.Threading.Thread.Sleep(10000);
            Console.WriteLine($"         Favorites 10 s spaeter: {(Gleich(geschrieben2, TaskbandLesen(), "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
            TaskleisteZeigen("mit fehlendem Ziel, 10 s spaeter", pinOrdner, mitInhalt: false);

            // 5. Die Sicherung eines anderen PCs (anderer Laeufer, anderes Windows).
            if (Environment.GetEnvironmentVariable("DASI_TASKLEISTE_FREMD") is { Length: > 0 } fremd && Directory.Exists(fremd))
            {
                Console.WriteLine($"   == Sicherung eines anderen PCs: {fremd} ==");
                var tl = Taskleiste.Laden(fremd);
                Console.WriteLine($"         gesichert auf {tl?.Rechner}: {string.Join(", ", tl?.Verknuepfungen ?? new List<string>())}, Taskband: {string.Join(", ", tl?.Taskband.Select(e => e.Name) ?? Array.Empty<string>())}");
                Leeren(pinOrdner);
                log.Leeren();
                new BackupService(new ProcessRunner(c), c, log.Schreiben) { SourcePath = profil, BackupPath = fremd }.RestoreDesktopIcons().Wait();
                foreach (var z in log.Zeilen.Where(z => z.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) || z.Contains("Explorer"))) Console.WriteLine("         Dienst: " + z.Trim());
                var geschriebenFremd = TaskbandLesen();
                TaskleisteZeigen("fremde Sicherung wiederhergestellt", pinOrdner, mitInhalt: true);
                System.Threading.Thread.Sleep(10000);
                Console.WriteLine($"         Favorites 10 s spaeter: {(Gleich(geschriebenFremd, TaskbandLesen(), "Favorites") ? "unveraendert" : "VOM EXPLORER GEAENDERT")}");
                TaskleisteZeigen("fremde Sicherung, 10 s spaeter", pinOrdner, mitInhalt: true);
                ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(5000);
                TaskleisteZeigen("fremde Sicherung, nach noch einem Explorer-Neustart", pinOrdner, mitInhalt: false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("         Versuch abgebrochen: " + ex);
        }
        finally
        {
            // Ausgangszustand zurueck.
            try
            {
                foreach (var alt in Directory.GetFiles(pinOrdner, "*.lnk")) File.Delete(alt);
                foreach (var n in Directory.GetFiles(originalLnk)) File.Copy(n, Path.Combine(pinOrdner, Path.GetFileName(n)), true);
                using (var k = Registry.CurrentUser.CreateSubKey(Taskleiste.TaskbandSchluessel))
                    foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
                foreach (var e in original) DesignRegistry.Schreiben(Registry.CurrentUser, e);
                ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                TaskleisteZeigen("Ausgangslage zurueck", pinOrdner, mitInhalt: false);
            }
            catch (Exception ex) { Console.WriteLine("         Ausgangslage NICHT zurueck: " + ex.Message); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<DesignEintrag> TaskbandLesen()
    {
        var erg = new List<DesignEintrag>();
        using var k = Registry.CurrentUser.OpenSubKey(Taskleiste.TaskbandSchluessel);
        foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
            if (DesignRegistry.Lesen(Registry.CurrentUser, Taskleiste.TaskbandSchluessel, name) is { } v)
                erg.Add(new DesignEintrag { Gruppe = "Taskleiste", Schluessel = Taskleiste.TaskbandSchluessel, Name = name, Typ = v.Typ, Wert = v.Wert });
        return erg;
    }

    private static bool Gleich(List<DesignEintrag> a, List<DesignEintrag> b, string name)
        => a.FirstOrDefault(e => e.Name == name)?.Wert == b.FirstOrDefault(e => e.Name == name)?.Wert;

    [SupportedOSPlatform("windows")]
    private static void Leeren(string pinOrdner)
    {
        foreach (var alt in Directory.GetFiles(pinOrdner, "*.lnk")) File.Delete(alt);
        using (var k = Registry.CurrentUser.CreateSubKey(Taskleiste.TaskbandSchluessel))
            foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
        var (ok, _) = ExplorerNeustart.NeuStarten(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Console.WriteLine($"         geleert, Explorer neu gestartet: {ok}");
        System.Threading.Thread.Sleep(3000);
    }

    /// <summary>Gibt den Zustand aus und liefert die Namen der sichtbaren App-Knoepfe.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> TaskleisteZeigen(string wann, string pinOrdner, bool mitInhalt)
    {
        Console.WriteLine($"   -- {wann} --");
        foreach (var e in TaskbandLesen())
        {
            var laenge = e.Typ == "Binary" ? Convert.FromBase64String(e.Wert).Length + " Bytes" : e.Wert;
            Console.WriteLine($"         Taskband {e.Name} ({e.Typ}): {laenge}");
            if (mitInhalt && e.Typ == "Binary")
            {
                var b = Convert.FromBase64String(e.Wert);
                Console.WriteLine($"           Texte (UTF-16): {string.Join(" | ", Texte(b, unicode: true))}");
                Console.WriteLine($"           Texte (ASCII):  {string.Join(" | ", Texte(b, unicode: false))}");
                for (int i = 0; i < Math.Min(b.Length, 1024); i += 32)
                    Console.WriteLine($"           {i:X4}: {Convert.ToHexString(b, i, Math.Min(32, b.Length - i))}");
            }
        }
        foreach (var n in Taskleiste.Verknuepfungen(pinOrdner))
            Console.WriteLine($"         Verknuepfung {n} -> {LinkZiel(Path.Combine(pinOrdner, n))}");
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
        TaskleisteFoto(wann);
        knoepfe.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"         App-Knoepfe: {(knoepfe.Count == 0 ? "keine" : string.Join(", ", knoepfe))}");
        return knoepfe;
    }

    /// <summary>Fotografiert die Taskleiste und gibt das Bild als PNG (Base64) in einer Zeile aus.</summary>
    [SupportedOSPlatform("windows")]
    private static void TaskleisteFoto(string name)
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

    private static IEnumerable<string> Texte(byte[] b, bool unicode)
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
    private static string LinkZiel(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            return sb + (args.Length > 0 ? " " + args : "");
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    [SupportedOSPlatform("windows")]
    private static void LinkZielSetzen(string lnk, string ziel)
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
