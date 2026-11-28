using System.Collections.Generic;
using System.Diagnostics;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Cancellation state shared by all services, so that Cancel and closing
/// the window can kill the processes that are still running.
/// </summary>
public sealed class CancellationTokenState
{
    private readonly object _lock = new();
    private readonly HashSet<int> _activePids = new();

    public bool IsCancelled { get; private set; }

    public bool FastMode { get; set; } = true;

    public void RequestCancel() => IsCancelled = true;
    public void Reset() { lock (_lock) { IsCancelled = false; } }

    public void Register(int pid)   { lock (_lock) { _activePids.Add(pid); } }
    public void Unregister(int pid) { lock (_lock) { _activePids.Remove(pid); } }

    public void KillAll()
    {
        int[] pids;
        lock (_lock) { pids = new int[_activePids.Count]; _activePids.CopyTo(pids); }
        foreach (var pid in pids)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
        }
        // To be safe, also kill orphaned winget processes
        foreach (var name in new[] { "winget" })
        {
            try { foreach (var p in Process.GetProcessesByName(name)) p.Kill(true); } catch { }
        }
    }
}
