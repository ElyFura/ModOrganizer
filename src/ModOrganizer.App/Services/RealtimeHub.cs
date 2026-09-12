using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

namespace ModOrganizer.App.Services;

/// <summary>
/// Subscribes to Supabase Realtime postgres_changes for the relevant tables and
/// triggers a debounced refresh on the UI thread. One global handler — calls all
/// registered callbacks. Coarse-grained but simple and covers the multi-user
/// "see partner's changes immediately" need.
/// </summary>
public sealed class RealtimeHub : IAsyncDisposable
{
    private readonly SupabaseClientProvider _supabase;
    private readonly ILogger<RealtimeHub> _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debouncer;
    private readonly List<RealtimeChannel> _channels = new();
    private readonly List<Action> _handlers = new();

    private static readonly string[] Tables =
    {
        "mods", "categories", "tags", "mod_tags",
        "mod_links", "mod_comments", "action_log", "penumbra_user_state"
    };

    /// <summary>
    /// False while the socket is down. Without this the app looked identical whether it was
    /// receiving the other user's changes or had silently stopped hours ago - the one thing
    /// a two-person setup must not leave ambiguous.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="IsConnected"/> changes.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    private int _reconnectAttempt;
    private bool _reconnecting;
    private bool _disposed;

    public RealtimeHub(SupabaseClientProvider supabase, ILogger<RealtimeHub> log)
    {
        _supabase = supabase;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _debouncer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debouncer.Tick += (_, _) =>
        {
            _debouncer.Stop();
            foreach (var h in _handlers)
            {
                try { h(); } catch (Exception ex) { _log.LogWarning(ex, "Realtime handler failed"); }
            }
        };
    }

    /// <summary>Register a callback fired (debounced) when any subscribed table changes.</summary>
    public void OnAnyChange(Action handler) => _handlers.Add(handler);

    /// <summary>
    /// Connects the realtime socket and subscribes the tables we sync. Returns false if
    /// the socket could not be established — callers that need their own channels
    /// (mentions, presence) must not proceed in that case, because
    /// <c>Realtime.Channel(...)</c> throws "Socket must exist, was `Connect` called?".
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_supabase.Client?.Realtime is null) return false;

        var realtime = _supabase.Client.Realtime;

        // The socket drops on standby, a WLAN hiccup or a Supabase restart. Nothing in the
        // library reconnects by itself, so listen for the state change and do it here.
        realtime.AddStateChangedHandler((_, state) => OnSocketState(state));

        try { await realtime.ConnectAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Realtime connect failed"); return false; }

        await SubscribeTablesAsync().ConfigureAwait(false);
        SetConnected(true);
        return true;
    }

    private async Task SubscribeTablesAsync()
    {
        var realtime = _supabase.Client?.Realtime;
        if (realtime is null) return;

        foreach (var table in Tables)
        {
            try
            {
                var channel = realtime.Channel("realtime", "public", table);
                channel.AddPostgresChangeHandler(ListenType.All, (_, _) => Schedule());
                await channel.Subscribe();
                _channels.Add(channel);
                _log.LogInformation("Subscribed realtime: public.{Table}", table);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Realtime subscribe failed for {Table}", table);
            }
        }
    }

    private void OnSocketState(Supabase.Realtime.Constants.SocketState state)
    {
        switch (state)
        {
            case Supabase.Realtime.Constants.SocketState.Open:
                _reconnectAttempt = 0;
                SetConnected(true);
                break;

            case Supabase.Realtime.Constants.SocketState.Close:
            case Supabase.Realtime.Constants.SocketState.Error:
                SetConnected(false);
                _ = ReconnectLoopAsync();
                break;
        }
    }

    /// <summary>
    /// Reconnects with a capped backoff. Runs at most once at a time: several Close events
    /// can arrive together, and a burst of parallel reconnects is how you get duplicate
    /// channels and a refresh storm.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        if (_disposed || _reconnecting) return;
        _reconnecting = true;

        try
        {
            while (!_disposed && !IsConnected)
            {
                _reconnectAttempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, _reconnectAttempt))));
                _log.LogInformation("Realtime down, reconnect attempt {N} in {Sec}s",
                    _reconnectAttempt, delay.TotalSeconds);

                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed || IsConnected) break;

                try
                {
                    var realtime = _supabase.Client?.Realtime;
                    if (realtime is null) break;

                    // Drop the old channels first; resubscribing on a fresh socket without
                    // this leaves the previous ones registered and every change arrives twice.
                    foreach (var ch in _channels)
                    {
                        try { ch.Unsubscribe(); } catch { }
                    }
                    _channels.Clear();

                    await realtime.ConnectAsync().ConfigureAwait(false);
                    await SubscribeTablesAsync().ConfigureAwait(false);
                    SetConnected(true);

                    // Whatever changed while we were away is not replayed, so pull once.
                    Schedule();
                    _log.LogInformation("Realtime reconnected");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Realtime reconnect attempt {N} failed", _reconnectAttempt);
                }
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        _dispatcher.BeginInvoke(() => ConnectionChanged?.Invoke(this, value));
    }

    private void Schedule()
    {
        _dispatcher.BeginInvoke(() =>
        {
            _debouncer.Stop();
            _debouncer.Start();
        });
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _debouncer.Stop();
        foreach (var ch in _channels)
        {
            try { ch.Unsubscribe(); } catch { }
        }
        _channels.Clear();
        await Task.CompletedTask;
    }
}
