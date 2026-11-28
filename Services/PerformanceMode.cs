using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Temporarily requests a "performance mode": keeps the computer from going
/// to standby (or the display from sleeping) while a backup is running, and
/// raises the process priority.
///
/// IMPORTANT: Dispose() fully undoes every change. Technically, the standby
/// block only lasts as long as the process runs - when the program exits,
/// Windows lifts it automatically anyway. Nothing in the user's global power
/// plan settings is changed.
/// </summary>
public sealed class PerformanceMode : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_CONTINUOUS       = 0x80000000,
        ES_SYSTEM_REQUIRED  = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private readonly Action<string> _log;
    private readonly ProcessPriorityClass _originalPriority;
    private bool _priorityChanged;
    private bool _executionStateSet;
    private bool _disposed;

    public PerformanceMode(Action<string> log)
    {
        _log = log;
        _originalPriority = Process.GetCurrentProcess().PriorityClass;
    }

    /// <summary>Enables the standby block and a higher process priority.</summary>
    public void Enable()
    {
        // 1) Prevent standby/display sleep (for this process only).
        try
        {
            var res = SetThreadExecutionState(
                ExecutionState.ES_CONTINUOUS |
                ExecutionState.ES_SYSTEM_REQUIRED |
                ExecutionState.ES_DISPLAY_REQUIRED);
            _executionStateSet = res != 0;
            if (_executionStateSet)
                _log(Loc.Tr("[INFO] Leistungsmodus aktiv: Standby während des Backups unterdrückt.",
                            "[INFO] Performance mode on: standby suppressed during the backup."));
        }
        catch { /* not critical */ }

        // 2) Raise the process priority (AboveNormal - deliberately not
        //    High/RealTime, so the system stays responsive).
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            _priorityChanged = true;
        }
        catch { /* e.g. missing rights - not critical */ }
    }

    /// <summary>Fully undoes all changes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Reset the priority to its original value.
        if (_priorityChanged)
        {
            try { Process.GetCurrentProcess().PriorityClass = _originalPriority; }
            catch { /* ignore */ }
        }

        // Lift the standby block: back to ES_CONTINUOUS without further flags.
        if (_executionStateSet)
        {
            try { SetThreadExecutionState(ExecutionState.ES_CONTINUOUS); }
            catch { /* ignore */ }
        }

        try
        {
            _log(Loc.Tr("[INFO] Leistungsmodus beendet: Energieeinstellungen und Priorität zurückgesetzt.",
                        "[INFO] Performance mode ended: power settings and priority reset."));
        }
        catch { /* ignore */ }
    }
}
