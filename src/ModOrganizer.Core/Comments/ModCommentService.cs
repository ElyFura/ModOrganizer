using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Comments;

public sealed class ModCommentEntry
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public Guid? UserId { get; set; }
    public string BodyMd { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserColor { get; set; }
    public string? UserEmail { get; set; }
}

/// <summary>
/// Threaded chat-style comments per mod, separate from `mods.comment_md`
/// (which remains the single shared "Anleitung" field).
/// </summary>
public sealed class ModCommentService
{
    private readonly DatabaseStore _store;
    private readonly IUserContext _user;

    public ModCommentService(DatabaseStore store, IUserContext user)
    {
        _store = store;
        _user = user;
    }

    public IReadOnlyList<ModCommentEntry> GetForMod(long modId)
    {
        using var conn = _store.Open();
        var rows = conn.Query<(long Id, long ModId, Guid? UserId, string BodyMd,
                              DateTime CreatedAt, DateTime UpdatedAt,
                              string? Email, string? DisplayName, string? Color)>(
            """
            SELECT mc.id AS Id, mc.mod_id AS ModId, mc.user_id AS UserId,
                   mc.body_md AS BodyMd, mc.created_at AS CreatedAt, mc.updated_at AS UpdatedAt,
                   u.email AS Email, u.display_name AS DisplayName, u.color_hex AS Color
            FROM mod_comments mc
            LEFT JOIN users u ON u.id = mc.user_id
            WHERE mc.mod_id = @m
            ORDER BY mc.id ASC
            """, new { m = modId }).ToList();

        return rows.Select(r => new ModCommentEntry
        {
            Id = r.Id,
            ModId = r.ModId,
            UserId = r.UserId,
            BodyMd = r.BodyMd,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)),
            UserDisplayName = r.DisplayName ?? r.Email,
            UserColor = r.Color,
            UserEmail = r.Email
        }).ToList();
    }

    public long Post(long modId, string bodyMd)
    {
        if (string.IsNullOrWhiteSpace(bodyMd))
            throw new ArgumentException("Comment body cannot be empty.", nameof(bodyMd));

        using var conn = _store.Open();
        var id = conn.ExecuteScalar<long>(
            """
            INSERT INTO mod_comments(mod_id, user_id, body_md, created_at, updated_at)
            VALUES (@m, @u, @b, NOW(), NOW())
            RETURNING id
            """,
            new { m = modId, u = _user.UserId, b = bodyMd.Trim() });

        // Bumps mods.updated_by/at and writes to action_log
        Management.ActionLog.Record(conn, null, ActionKind.CommentUpdate,
            Guid.NewGuid().ToString("N"), modId: modId);
        return id;
    }

    public void Edit(long commentId, string bodyMd)
    {
        if (string.IsNullOrWhiteSpace(bodyMd))
            throw new ArgumentException("Comment body cannot be empty.", nameof(bodyMd));

        using var conn = _store.Open();
        var (modId, ownerId) = conn.QuerySingleOrDefault<(long ModId, Guid? UserId)>(
            "SELECT mod_id AS ModId, user_id AS UserId FROM mod_comments WHERE id=@id",
            new { id = commentId });

        if (modId == 0) return;
        if (ownerId.HasValue && ownerId != _user.UserId)
            throw new UnauthorizedAccessException("Cannot edit someone else's comment.");

        conn.Execute(
            "UPDATE mod_comments SET body_md=@b, updated_at=NOW() WHERE id=@id",
            new { id = commentId, b = bodyMd.Trim() });

        Management.ActionLog.Record(conn, null, ActionKind.CommentUpdate,
            Guid.NewGuid().ToString("N"), modId: modId);
    }

    public void Delete(long commentId)
    {
        using var conn = _store.Open();
        var (modId, ownerId) = conn.QuerySingleOrDefault<(long ModId, Guid? UserId)>(
            "SELECT mod_id AS ModId, user_id AS UserId FROM mod_comments WHERE id=@id",
            new { id = commentId });

        if (modId == 0) return;
        if (ownerId.HasValue && ownerId != _user.UserId)
            throw new UnauthorizedAccessException("Cannot delete someone else's comment.");

        conn.Execute("DELETE FROM mod_comments WHERE id=@id", new { id = commentId });

        Management.ActionLog.Record(conn, null, ActionKind.CommentUpdate,
            Guid.NewGuid().ToString("N"), modId: modId);
    }
}
