using System.Collections.Concurrent;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ModOrganizer.Core.Auth;
using Supabase.Realtime.Models;

namespace ModOrganizer.App.Services;

/// <summary>
/// Tracks online users in a shared "mo-users" Presence channel and exposes
/// who's online + which mod they're viewing.
/// </summary>
public sealed class PresenceService : IAsyncDisposable
{
    private readonly SupabaseClientProvider _supabase;
    private readonly IUserContext _user;
    private readonly ILogger<PresenceService> _log;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<Guid, PresenceState> _users = new();

    private Supabase.Realtime.RealtimeChannel? _channel;
    private dynamic? _presence; // typed wrapper varies across supabase-csharp versions
    private long? _viewingModId;
    private DispatcherTimer? _pollTimer;

    public event EventHandler? StateChanged;

    public PresenceService(SupabaseClientProvider supabase, IUserContext user, ILogger<PresenceService> log)
    {
        _supabase = supabase;
        _user = user;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public IReadOnlyDictionary<Guid, PresenceState> OnlineUsers => _users;

    public async Task StartAsync()
    {
        if (_supabase.Client?.Realtime is null || _user.UserId is not Guid uid) return;
        try
        {
            _channel = _supabase.Client.Realtime.Channel("mo-users");
            _presence = _channel.Register<UserPresence>(uid.ToString());
            await _channel.Subscribe();
            await TrackSelfAsync();

            // Simple poll — for 2 users, every 3s is plenty and avoids version-drift in event API
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _pollTimer.Tick += (_, _) => Sync();
            _pollTimer.Start();
            Sync();
            _log.LogInformation("Presence joined for user {Uid}", uid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Presence start failed");
        }
    }

    public async Task SetViewingModAsync(long? modId)
    {
        if (_viewingModId == modId) return;
        _viewingModId = modId;
        await TrackSelfAsync();
    }

    private async Task TrackSelfAsync()
    {
        if (_presence is null) return;
        try
        {
            var self = new UserPresence
            {
                UserId = _user.UserId?.ToString() ?? "",
                DisplayName = _user.DisplayName ?? _user.Email ?? "?",
                Email = _user.Email ?? "",
                ViewingModId = _viewingModId
            };
            await _presence.Track(self);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Presence track failed");
        }
    }

    private void Sync()
    {
        if (_presence is null) return;
        try
        {
            var snapshot = _presence.CurrentState as System.Collections.IDictionary;
            if (snapshot is null) return;

            var fresh = new Dictionary<Guid, PresenceState>();
            foreach (System.Collections.DictionaryEntry kv in snapshot)
            {
                if (kv.Value is not System.Collections.IEnumerable list) continue;
                UserPresence? p = null;
                foreach (var item in list) p = item as UserPresence;
                if (p is null) continue;
                if (!Guid.TryParse(p.UserId, out var uid)) continue;
                fresh[uid] = new PresenceState(uid, p.DisplayName, p.Email, p.ViewingModId);
            }

            _users.Clear();
            foreach (var (k, v) in fresh) _users[k] = v;

            _dispatcher.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Presence sync failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollTimer?.Stop();
        try { if (_presence is not null) await _presence.Untrack(); } catch { }
        try { _channel?.Unsubscribe(); } catch { }
    }

    public sealed class UserPresence : BasePresence
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public long? ViewingModId { get; set; }
    }
}

public sealed record PresenceState(Guid UserId, string DisplayName, string Email, long? ViewingModId);
