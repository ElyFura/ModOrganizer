using System.Collections.Concurrent;

namespace ModOrganizer.Core.Scanning;

/// <summary>
/// Lets one scan per root run at a time, and refuses the rest rather than queueing them.
///
/// Refusing matters: the folder watcher fires every few seconds while Nextcloud syncs, and
/// a scan of a large library runs for minutes. Queued scans would each repeat the same work
/// and fight over the same rows; a caller that is told "already running" can simply try
/// again later.
/// </summary>
public sealed class ScanGate
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    /// <summary>True if this caller now owns the root and must call <see cref="Exit"/>.</summary>
    public bool TryEnter(long rootId) =>
        _locks.GetOrAdd(rootId, _ => new SemaphoreSlim(1, 1)).Wait(0);

    public void Exit(long rootId)
    {
        if (_locks.TryGetValue(rootId, out var gate)) gate.Release();
    }

    /// <summary>Whether a scan of this root is in progress.</summary>
    public bool IsBusy(long rootId) =>
        _locks.TryGetValue(rootId, out var gate) && gate.CurrentCount == 0;
}
