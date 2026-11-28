using System;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Decides whether a log line reports a failure.
///
/// Kept in its own file so the rule can be tested without the UI - it
/// decides whether an action shows up as [OK] or [FEHLER] in the summary
/// and in the log file.
/// </summary>
public static class ActionOutcome
{
    /// <summary>
    /// true for every line a service marks with "[FEHLER]" or "[ERROR]" - also
    /// when indented ("    -> [FEHLER] ...") or preceded by a line break.
    ///
    /// Lines from robocopy's own output ("FEHLER 5 (0x00000005) ...") carry no
    /// square brackets and deliberately do NOT count: a few locked files are
    /// normal for a profile. If robocopy really fails, the service reports the
    /// exit code itself with "[FEHLER]".
    /// </summary>
    public static bool IsErrorLine(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("[FEHLER]", StringComparison.Ordinal)
               || text.Contains("[ERROR]", StringComparison.Ordinal));
}
