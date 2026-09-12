using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Queries;

/// <summary>How this user appears to everyone else in the shared library.</summary>
public sealed record UserProfile(Guid Id, string? Email, string DisplayName, string? ColorHex);

/// <summary>
/// The name and colour shown next to anything this user touches: presence chips, the
/// editor badge on a card, comments, the activity feed.
///
/// The row is created by a trigger on sign-up and falls back to the email address when
/// Supabase carries no display_name - which is why a second user shows up as
/// "info@example.com" everywhere until they pick a name here.
/// </summary>
public sealed class UserProfileService
{
    private readonly DatabaseStore _store;
    private readonly IUserContext _user;

    public UserProfileService(DatabaseStore store, IUserContext user)
    {
        _store = store;
        _user = user;
    }

    public UserProfile? GetCurrent()
    {
        if (_user.UserId is not Guid uid) return null;

        using var conn = _store.Open();
        return conn.QuerySingleOrDefault<UserProfile>(
            """
            SELECT id AS Id, email AS Email,
                   COALESCE(display_name, email, '?') AS DisplayName,
                   color_hex AS ColorHex
            FROM users WHERE id = @u
            """, new { u = uid });
    }

    /// <summary>
    /// Writes the name. Empty falls back to the email, so clearing the field restores the
    /// previous behaviour rather than leaving a nameless user.
    /// </summary>
    public void SetDisplayName(string? name)
    {
        if (_user.UserId is not Guid uid) return;

        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is { Length: > 60 }) trimmed = trimmed[..60];

        using var conn = _store.Open();
        conn.Execute(
            "UPDATE users SET display_name = COALESCE(@n, email) WHERE id = @u",
            new { n = trimmed, u = uid });
    }

    public void SetColor(string? colorHex)
    {
        if (_user.UserId is not Guid uid) return;
        using var conn = _store.Open();
        conn.Execute("UPDATE users SET color_hex = @c WHERE id = @u",
            new { c = colorHex, u = uid });
    }
}
