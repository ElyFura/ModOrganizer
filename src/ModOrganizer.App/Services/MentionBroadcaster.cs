using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Comments;
using Supabase.Realtime.Broadcast;
using Supabase.Realtime.Models;

namespace ModOrganizer.App.Services;

/// <summary>
/// Sends and receives @mention broadcasts via Supabase Realtime broadcast channels.
/// Channel naming: "mentions:{userId}". One channel per user.
/// </summary>
public sealed class MentionBroadcaster : IAsyncDisposable
{
    private readonly SupabaseClientProvider _supabase;
    private readonly IUserContext _user;
    private readonly ILogger<MentionBroadcaster> _log;
    private Supabase.Realtime.RealtimeChannel? _myChannel;

    public event EventHandler<MentionPayload>? MentionReceived;

    public MentionBroadcaster(SupabaseClientProvider supabase, IUserContext user, ILogger<MentionBroadcaster> log)
    {
        _supabase = supabase;
        _user = user;
        _log = log;
    }

    public async Task StartAsync()
    {
        if (_supabase.Client?.Realtime is null || _user.UserId is not Guid uid) return;
        try
        {
            _myChannel = _supabase.Client.Realtime.Channel($"mentions:{uid}");
            var broadcast = _myChannel.Register<BroadcastPayload>();
            broadcast.AddBroadcastEventHandler((sender, ev) =>
            {
                try
                {
                    var current = broadcast.Current();
                    if (current is null) return;
                    MentionReceived?.Invoke(this, new MentionPayload
                    {
                        FromUserId = current.FromUserId,
                        FromName = current.FromName,
                        ModId = current.ModId,
                        ModName = current.ModName,
                        Snippet = current.Snippet,
                        At = current.At
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to handle mention payload");
                }
            });
            await _myChannel.Subscribe();
            _log.LogInformation("Listening for mentions on channel mentions:{Uid}", uid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Mention channel subscribe failed");
        }
    }

    public async Task SendAsync(IEnumerable<MentionedUser> targets, MentionPayload payload)
    {
        if (_supabase.Client?.Realtime is null) return;
        var body = new BroadcastPayload
        {
            FromUserId = payload.FromUserId,
            FromName = payload.FromName,
            ModId = payload.ModId,
            ModName = payload.ModName,
            Snippet = payload.Snippet,
            At = payload.At
        };

        foreach (var t in targets)
        {
            if (t.UserId == _user.UserId) continue; // don't notify yourself
            try
            {
                var ch = _supabase.Client.Realtime.Channel($"mentions:{t.UserId}");
                var bc = ch.Register<BroadcastPayload>();
                if (ch.IsJoined == false)
                    await ch.Subscribe();
                await bc.Send("mention", body);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Mention send failed for {User}", t.DisplayName);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_myChannel is not null)
        {
            try { _myChannel.Unsubscribe(); } catch { }
        }
        await Task.CompletedTask;
    }

    public sealed class BroadcastPayload : BaseBroadcast
    {
        public string FromUserId { get; set; } = "";
        public string FromName { get; set; } = "";
        public long ModId { get; set; }
        public string ModName { get; set; } = "";
        public string Snippet { get; set; } = "";
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    }
}

public sealed class MentionPayload
{
    public string FromUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public long ModId { get; set; }
    public string ModName { get; set; } = "";
    public string Snippet { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}
