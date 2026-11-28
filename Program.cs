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
        // Code page 850 (used by robocopy, among others) is no longer available
        // by default in modern .NET. Register the provider once, otherwise
        // Encoding.GetEncoding(850) throws.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Global crash handlers: write every unhandled exception to a log file
        // on the desktop instead of letting the app vanish without a word.
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
    /// Writes a crash report to %USERPROFILE%\Desktop\WindowsDaSiTool_Absturz.log
    /// (falls back to the TEMP folder).
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
            // If even writing fails, there is nothing left to do.
        }
    }
}
