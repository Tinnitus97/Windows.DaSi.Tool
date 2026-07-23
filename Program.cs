using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;

namespace WindowsDaSiTool;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Codepage 850 (u.a. von Robocopy genutzt) ist in modernem .NET nicht
        // mehr standardmaessig verfuegbar. Provider einmalig registrieren,
        // sonst wirft Encoding.GetEncoding(850) eine Exception.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Globale Absturz-Faenger: schreiben jede unbehandelte Exception in
        // eine Logdatei auf dem Desktop, statt die App kommentarlos zu beenden.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Schreibt einen Absturzbericht nach %USERPROFILE%\Desktop\WindowsDaSiTool_Absturz.log
    /// (mit Fallback ins TEMP-Verzeichnis).
    /// </summary>
    public static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"Zeit:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Quelle:  {source}");
            sb.AppendLine($"Typ:     {ex?.GetType().FullName ?? "(unbekannt)"}");
            sb.AppendLine($"Meldung: {ex?.Message}");
            sb.AppendLine("StackTrace:");
            sb.AppendLine(ex?.StackTrace ?? "(keiner)");
            if (ex?.InnerException is { } inner)
            {
                sb.AppendLine("--- InnerException ---");
                sb.AppendLine($"Typ:     {inner.GetType().FullName}");
                sb.AppendLine($"Meldung: {inner.Message}");
                sb.AppendLine(inner.StackTrace);
            }
            sb.AppendLine();

            string path;
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                path = Path.Combine(desktop, "WindowsDaSiTool_Absturz.log");
            }
            catch
            {
                path = Path.Combine(Path.GetTempPath(), "WindowsDaSiTool_Absturz.log");
            }
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Wenn selbst das Schreiben scheitert, gibt es nichts mehr zu tun.
        }
    }
}
