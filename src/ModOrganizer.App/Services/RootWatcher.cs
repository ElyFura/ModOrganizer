using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.App.Services;

/// <summary>
/// Watches all configured root folders for filesystem changes and triggers a
/// debounced rescan. Useful when mods sync in via Nextcloud/Syncthing — the UI
/// catches the change without manual Rescan.
/// </summary>
public sealed class RootWatcher : IDisposable
{
    private readonly ModLibraryService _library;
    private readonly ModScanner _scanner;
    private readonly ILogger<RootWatcher> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<long, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<long, DispatcherTimer> _debouncers = new();
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);

    private readonly FileSystemActivityGate _gate;

    /// <summary>
    /// True while the app is performing its own file operations. Reads through to the
    /// shared gate, which the rename/move/delete/import/category services open around
    /// their own writes — without it, every operation the app performed kicked off a
    /// full rescan of its own making.
    /// </summary>
    public bool Suppress
    {
        get => _gate.IsSuppressed;
        set { if (value) _suppressScope ??= _gate.Suppress(); else { _suppressScope?.Dispose(); _suppressScope = null; } }
    }

    private IDisposable? _suppressScope;

    public event EventHandler? RescanCompleted;

    public RootWatcher(ModLibraryService library, ModScanner scanner,
        FileSystemActivityGate gate, ILogger<RootWatcher> log)
    {
        _library = library;
        _scanner = scanner;
        _gate = gate;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        Stop();
        foreach (var root in _library.GetRoots())
        {
            if (!root.Enabled) continue;
            if (!Directory.Exists(root.Path)) continue;
            try
            {
                var w = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                var rootId = root.Id;
                w.Created += (_, _) => Schedule(rootId);
                w.Deleted += (_, _) => Schedule(rootId);
                w.Renamed += (_, _) => Schedule(rootId);
                w.Changed += (_, _) => Schedule(rootId);
                w.EnableRaisingEvents = true;
                _watchers[root.Id] = w;
                _log.LogInformation("Watching {Path}", root.Path);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not start watcher for {Path}", root.Path);
            }
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers.Values)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        foreach (var t in _debouncers.Values) t.Stop();
        _debouncers.Clear();
    }

    private void Schedule(long rootId)
    {
        if (_gate.IsSuppressed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (!_debouncers.TryGetValue(rootId, out var timer))
            {
                timer = new DispatcherTimer { Interval = _debounce };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();

                    // Re-check: a long move may still have been running when we scheduled.
                    if (_gate.IsSuppressed)
                    {
                        timer.Start();
                        return;
                    }

                    _ = Task.Run(() => Rescan(rootId));
                };
                _debouncers[rootId] = timer;
            }
            timer.Stop();
            timer.Start();
        });
    }

    private void Rescan(long rootId)
    {
        try
        {
            _scanner.Scan(rootId);
            _dispatcher.BeginInvoke(() =>
                RescanCompleted?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auto-rescan for root {Id} failed", rootId);
        }
    }

    public void Dispose() => Stop();
}
