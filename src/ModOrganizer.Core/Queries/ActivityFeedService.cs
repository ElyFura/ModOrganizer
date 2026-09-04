using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Queries;

public sealed class ActivityEntry
{
    public long Id { get; set; }
    public string TxId { get; set; } = "";
    public string Ts { get; set; } = "";
    public int Action { get; set; }
    public long? ModId { get; set; }
    public string? FromPath { get; set; }
    public string? ToPath { get; set; }
    public string? UserId { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserColor { get; set; }
    public string? ModFolderName { get; set; }
    public string? ModCategory { get; set; }

    public ActionKind Kind => (ActionKind)Action;
    public DateTimeOffset When =>
        DateTimeOffset.TryParse(Ts, out var dt) ? dt : DateTimeOffset.MinValue;

    public string Summary => Kind switch
    {
        ActionKind.Rename          => $"hat umbenannt: {Path.GetFileName(FromPath ?? "")} → {Path.GetFileName(ToPath ?? "")}",
        ActionKind.Move            => $"hat verschoben: {Path.GetFileName(FromPath ?? "")} → {ToPath}",
        ActionKind.Delete          => $"hat gelöscht: {Path.GetFileName(FromPath ?? "")}",
        ActionKind.CategoryAdd     => $"hat Kategorie angelegt: {Path.GetFileName(ToPath ?? "")}",
        ActionKind.CategoryRename  => $"hat Kategorie umbenannt: {Path.GetFileName(FromPath ?? "")} → {Path.GetFileName(ToPath ?? "")}",
        ActionKind.CategoryMerge   => $"hat Kategorie zusammengeführt: {Path.GetFileName(FromPath ?? "")} → {Path.GetFileName(ToPath ?? "")}",
        ActionKind.CategoryDelete  => $"hat Kategorie gelöscht: {Path.GetFileName(FromPath ?? "")}",
        ActionKind.TagAdd          => $"hat Tag hinzugefügt zu '{ModFolderName}'",
        ActionKind.TagRemove       => $"hat Tag entfernt von '{ModFolderName}'",
        ActionKind.LinkAdd         => $"hat Link hinzugefügt zu '{ModFolderName}': {ToPath}",
        ActionKind.LinkRemove      => $"hat Link entfernt von '{ModFolderName}'",
        ActionKind.CommentUpdate   => $"hat Kommentar bei '{ModFolderName}' aktualisiert",
        _                          => Kind.ToString()
    };
}

public sealed class ActivityFeedService
{
    private readonly DatabaseStore _store;
    public ActivityFeedService(DatabaseStore store) => _store = store;

    public IReadOnlyList<ActivityEntry> GetRecent(int limit = 200, Guid? filterUserId = null)
    {
        using var conn = _store.Open();
        var sql = $"""
            SELECT a.id AS Id, a.tx_id AS TxId, a.ts AS Ts, a.action AS Action, a.mod_id AS ModId,
                   a.from_path AS FromPath, a.to_path AS ToPath,
                   a.user_id::text AS UserId,
                   COALESCE(u.display_name, u.email, '?') AS UserDisplayName,
                   u.color_hex AS UserColor,
                   m.folder_name AS ModFolderName,
                   c.name AS ModCategory
            FROM action_log a
            LEFT JOIN users u ON u.id = a.user_id
            LEFT JOIN mods m ON m.id = a.mod_id
            LEFT JOIN categories c ON c.id = m.category_id
            { (filterUserId.HasValue ? "WHERE a.user_id = @uid" : "") }
            ORDER BY a.id DESC
            LIMIT @limit
            """;
        return conn.Query<ActivityEntry>(sql,
            new { limit, uid = filterUserId }).ToList();
    }
}
