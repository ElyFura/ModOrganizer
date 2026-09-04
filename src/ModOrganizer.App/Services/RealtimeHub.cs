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
        try { await realtime.ConnectAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Realtime connect failed"); return false; }

        string[] tables =
        {
            "mods", "categories", "tags", "mod_tags",
            "mod_links", "mod_comments", "action_log", "penumbra_user_state"
        };

        foreach (var table in tables)
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

        return true;
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
        _debouncer.Stop();
        foreach (var ch in _channels)
        {
            try { ch.Unsubscribe(); } catch { }
        }
        _channels.Clear();
        await Task.CompletedTask;
    }
}
