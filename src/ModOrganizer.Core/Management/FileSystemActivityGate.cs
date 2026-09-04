namespace ModOrganizer.Core.Management;

/// <summary>
/// Marks the windows in which the app itself is writing to a mod root, so the
/// filesystem watcher can tell "the user changed something in Explorer" apart from
/// "we just renamed a folder ourselves". Without this every rename/move/delete/import
/// triggers a full auto-rescan of its own making.
///
/// Reference-counted, so nested scopes (a merge that runs many moves) behave.
/// A cooldown keeps the gate closed briefly after the last scope ends, because
/// FileSystemWatcher events arrive after the operation has finished.
/// </summary>
public sealed class FileSystemActivityGate
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private int _depth;
    private DateTimeOffset _releasedAt = DateTimeOffset.MinValue;

    /// <summary>True while the app is (or just was) touching the filesystem itself.</summary>
    public bool IsSuppressed
    {
        get
        {
            lock (_lock)
            {
                if (_depth > 0) return true;
                return DateTimeOffset.UtcNow - _releasedAt < Cooldown;
            }
        }
    }

    /// <summary>Opens a suppression scope; dispose it when the file operation is done.</summary>
    public IDisposable Suppress()
    {
        lock (_lock) _depth++;
        return new Scope(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_depth > 0) _depth--;
            if (_depth == 0) _releasedAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed class Scope : IDisposable
    {
        private FileSystemActivityGate? _gate;
        public Scope(FileSystemActivityGate gate) => _gate = gate;

        public void Dispose()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
        }
    }
}
