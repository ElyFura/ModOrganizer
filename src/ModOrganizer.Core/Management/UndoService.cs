using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.Core.Management;

/// <summary>
/// Reverses a single action_log transaction. Only the kinds where we have
/// enough information to revert are supported.
/// </summary>
public sealed class UndoService
{
    private readonly DatabaseStore _store;
    private readonly RenameService _rename;
    private readonly TagService _tags;

    public UndoService(DatabaseStore store, RenameService rename, TagService tags)
    {
        _store = store;
        _rename = rename;
        _tags = tags;
    }

    public static bool CanUndo(ActionKind kind) => kind switch
    {
        ActionKind.Rename or ActionKind.Move or
        ActionKind.TagAdd or ActionKind.TagRemove or
        ActionKind.CategoryRename => true,
        _ => false
    };

    public bool Undo(string txId)
    {
        using var conn = _store.Open();
        var entries = ActionLog.GetTransaction(conn, txId);
        if (entries.Count == 0) return false;
        var first = entries[0];
        var kind = (ActionKind)first.Action;

        switch (kind)
        {
            case ActionKind.Rename:
                _rename.Undo(txId);
                return true;

            case ActionKind.Move:
                return UndoMove(conn, entries);

            case ActionKind.TagAdd:
                return UndoTag(conn, entries, isAdd: true);

            case ActionKind.TagRemove:
                return UndoTag(conn, entries, isAdd: false);

            case ActionKind.CategoryRename:
                return UndoCategoryRename(conn, entries);
        }
        return false;
    }

    private static bool UndoMove(Npgsql.NpgsqlConnection conn, IReadOnlyList<ActionLog.Entry> entries)
    {
        var moveEntries = entries.Where(e => e.Action == (int)ActionKind.Move
                                          && !string.IsNullOrEmpty(e.FromPath)
                                          && !string.IsNullOrEmpty(e.ToPath))
                                 .Reverse()
                                 .ToList();
        if (moveEntries.Count == 0) return false;

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var e in moveEntries)
            {
                if (System.IO.Directory.Exists(e.ToPath!))
                {
                    System.IO.Directory.Move(e.ToPath!, e.FromPath!);
                }
                if (e.ModId.HasValue)
                {
                    // Restore category_id by looking at FromPath's parent
                    var fromCategory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(e.FromPath!));
                    var fromCatId = conn.QuerySingleOrDefault<long?>(
                        """
                        SELECT c.id FROM categories c
                        JOIN mods m ON m.id=@m
                        WHERE c.root_id=(SELECT root_id FROM categories WHERE id=m.category_id)
                          AND LOWER(c.name)=LOWER(@n)
                        """, new { m = e.ModId.Value, n = fromCategory }, tx);
                    if (fromCatId.HasValue)
                    {
                        conn.Execute(
                            "UPDATE mods SET category_id=@c, updated_at=@t WHERE id=@m",
                            new { c = fromCatId.Value, m = e.ModId.Value, t = DateTimeOffset.UtcNow.ToString("o") }, tx);
                    }
                }
            }
            conn.Execute("DELETE FROM action_log WHERE tx_id=@x", new { x = entries[0].TxId }, tx);
            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            return false;
        }
    }

    private bool UndoTag(Npgsql.NpgsqlConnection conn, IReadOnlyList<ActionLog.Entry> entries, bool isAdd)
    {
        // Each entry has payload_json {"tag_id":N} and modId. For TagAdd-undo, remove. For TagRemove-undo, add.
        var pattern = new System.Text.RegularExpressions.Regex(@"""tag_id""\s*:\s*(\d+)");
        var ops = entries.Where(e => e.PayloadJson is not null && e.ModId.HasValue)
            .Select(e =>
            {
                var m = pattern.Match(e.PayloadJson ?? "");
                return m.Success
                    ? (long.Parse(m.Groups[1].Value), e.ModId!.Value)
                    : ((long, long)?)null;
            })
            .Where(o => o.HasValue)
            .Select(o => o!.Value)
            .ToList();
        if (ops.Count == 0) return false;

        foreach (var (tagId, modId) in ops)
        {
            if (isAdd) _tags.RemoveTagFromMods(tagId, new[] { modId });
            else _tags.AddTagToMods(tagId, new[] { modId });
        }
        conn.Execute("DELETE FROM action_log WHERE tx_id=@x", new { x = entries[0].TxId });
        return true;
    }

    private static bool UndoCategoryRename(Npgsql.NpgsqlConnection conn, IReadOnlyList<ActionLog.Entry> entries)
    {
        var e = entries.FirstOrDefault(x => x.Action == (int)ActionKind.CategoryRename
                                          && !string.IsNullOrEmpty(x.FromPath)
                                          && !string.IsNullOrEmpty(x.ToPath));
        if (e is null) return false;

        var oldName = System.IO.Path.GetFileName(e.FromPath!);
        var newName = System.IO.Path.GetFileName(e.ToPath!);

        using var tx = conn.BeginTransaction();
        try
        {
            if (System.IO.Directory.Exists(e.ToPath!))
                System.IO.Directory.Move(e.ToPath!, e.FromPath!);

            conn.Execute(
                "UPDATE categories SET name=@n WHERE LOWER(name)=LOWER(@cur)",
                new { n = oldName, cur = newName }, tx);
            conn.Execute("DELETE FROM action_log WHERE tx_id=@x", new { x = e.TxId }, tx);
            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            return false;
        }
    }
}
