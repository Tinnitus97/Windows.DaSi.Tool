using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Fordert temporaer einen "Leistungsmodus" an: verhindert, dass der Rechner
/// waehrend eines laufenden Backups in den Standby geht (bzw. der Bildschirm
/// schlafen legt), und hebt die Prozessprioritaet an.
///
/// WICHTIG: Alle Aenderungen werden ueber Dispose() vollstaendig zurueckgesetzt.
/// Der Standby-Schutz gilt technisch nur solange der Prozess laeuft - beendet
/// sich das Programm, hebt Windows die Blockade ohnehin automatisch auf.
/// Nichts an den globalen Energieplan-Einstellungen des Nutzers wird veraendert.
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

    /// <summary>Aktiviert Standby-Schutz und hoehere Prozessprioritaet.</summary>
    public void Enable()
    {
        // 1) Standby-/Bildschirm-Schlaf verhindern (nur fuer diesen Prozess).
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
        catch { /* nicht kritisch */ }

        // 2) Prozessprioritaet anheben (AboveNormal - bewusst nicht High/RealTime,
        //    um die Systembedienung nicht zu beeintraechtigen).
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            _priorityChanged = true;
        }
        catch { /* z.B. fehlende Rechte - nicht kritisch */ }
    }

    /// <summary>Setzt alle Aenderungen vollstaendig zurueck.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Prioritaet auf den urspruenglichen Wert zuruecksetzen.
        if (_priorityChanged)
        {
            try { Process.GetCurrentProcess().PriorityClass = _originalPriority; }
            catch { /* ignore */ }
        }

        // Standby-Schutz aufheben: zurueck auf ES_CONTINUOUS ohne weitere Flags.
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
