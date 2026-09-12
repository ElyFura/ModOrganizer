using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ModOrganizer.Core.Penumbra;

namespace ModOrganizer.App.Services;

/// <summary>
/// Keeps the Penumbra snapshot this user shares up to date.
///
/// Pushing once at startup was not enough: people enable and disable mods while the game
/// runs, so the shared state was as old as the last app start - in practice days. This
/// watches Penumbra's own config folder and also polls, because Penumbra rewrites those
/// files while the app is running and a watcher alone misses the odd change.
///
/// Only an actually different snapshot is uploaded. Writing an unchanged one would wake
/// every other client through realtime for nothing.
/// </summary>
public sealed class PenumbraWatcher : IDisposable
{
    private readonly PenumbraService _penumbra;
    private readonly PenumbraSyncService _sync;
    private readonly ILogger<PenumbraWatcher> _log;
    private readonly Dispatcher _dispatcher;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _poll;
    private string? _lastPushedFingerprint;

    /// <summary>Raised after a push actually changed something, so the UI can reload.</summary>
    public event EventHandler? SnapshotPushed;

    public PenumbraWatcher(PenumbraService penumbra, PenumbraSyncService sync,
        ILogger<PenumbraWatcher> log)
    {
        _penumbra = penumbra;
        _sync = sync;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        if (!_sync.IsLoggedIn) return;   // offline session shares nothing

        // Penumbra writes collection files one after another when you toggle a mod, so
        // wait for the burst to settle instead of parsing a half-written folder.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _debounce.Tick += (_, _) => { _debounce!.Stop(); PushIfChanged(); };

        // The safety net: covers changes the watcher misses and the case where Penumbra
        // was not installed yet when the app started.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _poll.Tick += (_, _) => PushIfChanged();
        _poll.Start();

        try
        {
            var dir = PenumbraService.PluginConfigsPath;
            if (Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    Filter = "*.json"
                };
                _watcher.Changed += (_, _) => Schedule();
                _watcher.Created += (_, _) => Schedule();
                _watcher.Deleted += (_, _) => Schedule();
                _watcher.Renamed += (_, _) => Schedule();
                _watcher.EnableRaisingEvents = true;
                _log.LogInformation("Watching Penumbra config at {Dir}", dir);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not watch the Penumbra config folder");
        }

        PushIfChanged();
    }

    private void Schedule() => _dispatcher.BeginInvoke(() =>
    {
        _debounce?.Stop();
        _debounce?.Start();
    });

    /// <summary>Reads, compares, and uploads only on a real change. Safe to call often.</summary>
    public void PushIfChanged()
    {
        if (!_sync.IsLoggedIn) return;

        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = _penumbra.Read();
                if (!snapshot.IsAvailable) return;

                var fingerprint = Fingerprint(snapshot);
                if (fingerprint == _lastPushedFingerprint) return;

                _sync.Push(snapshot);
                _lastPushedFingerprint = fingerprint;
                _log.LogInformation("Penumbra snapshot pushed ({Collections} collections)",
                    snapshot.Collections.Count);

                _dispatcher.BeginInvoke(() => SnapshotPushed?.Invoke(this, EventArgs.Empty));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Penumbra push failed");
            }
        });
    }

    /// <summary>
    /// Covers what the other user actually sees: which collection holds which mod, and
    /// whether it is enabled there.
    /// </summary>
    private static string Fingerprint(PenumbraSnapshot snapshot)
    {
        var sb = new StringBuilder();
        foreach (var c in snapshot.Collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(c.Name).Append('|').Append(c.Role).Append(';');
        sb.Append('\n');

        foreach (var e in snapshot.Entries.OrderBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(e.FolderName).Append(':');
            foreach (var c in e.ActiveInCollections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append('+').Append(c);
            foreach (var c in e.AllInCollections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append('-').Append(c);
            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public void Dispose()
    {
        try { _watcher!.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
        _debounce?.Stop();
        _poll?.Stop();
    }
}
