using Dapper;
using ModOrganizer.Core.Archive;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.Core.Management;

public sealed class DeleteService
{
    private readonly DatabaseStore _store;
    private readonly ArchiveService _archive;
    private readonly FileSystemActivityGate? _gate;
    private readonly IUserContext? _user;

    public DeleteService(DatabaseStore store, ArchiveService archive,
        FileSystemActivityGate? gate = null, IUserContext? user = null)
    {
        _store = store;
        _archive = archive;
        _gate = gate;
        _user = user;
    }

    public bool DeleteMod(long modId, bool archiveBeforeDelete = false)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();
        var row = conn.QuerySingle<(string FolderName, string CatName, string RootPath)>(
            """
            SELECT m.folder_name AS FolderName, c.name AS CatName,
                   mo_root_path(c.root_id, @uid) AS RootPath
            FROM mods m JOIN categories c ON c.id=m.category_id
            WHERE m.id=@m
            """, new { m = modId, uid = _user?.UserId });

        var folderPath = Path.Combine(RootNotMappedException.Require(row.RootPath), row.CatName, row.FolderName);

        if (archiveBeforeDelete && Directory.Exists(folderPath))
        {
            try { _archive.ArchiveFolder(folderPath); }
            catch { return false; }
        }

        bool recycled = true;
        if (Directory.Exists(folderPath))
            recycled = RecycleBin.SendToRecycleBin(folderPath);

        if (!recycled) return false;

        using var tx = conn.BeginTransaction();
        conn.Execute(
            "UPDATE mods SET deleted_at=@t, is_missing=FALSE WHERE id=@m",
            new { t = DateTimeOffset.UtcNow.ToString("o"), m = modId }, tx);

        ActionLog.Record(conn, tx, ActionKind.Delete,
            Guid.NewGuid().ToString("N"), modId: modId, fromPath: folderPath);
        tx.Commit();
        return true;
    }

    public bool DeleteCategory(long categoryId)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();
        var row = conn.QuerySingle<(string Name, string RootPath)>(
            """
            SELECT c.name AS Name, mo_root_path(c.root_id, @uid) AS RootPath
            FROM categories c WHERE c.id=@id
            """, new { id = categoryId, uid = _user?.UserId });

        var folderPath = Path.Combine(RootNotMappedException.Require(row.RootPath), row.Name);
        bool recycled = true;
        if (Directory.Exists(folderPath))
            recycled = RecycleBin.SendToRecycleBin(folderPath);

        if (!recycled) return false;

        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM mods WHERE category_id=@c", new { c = categoryId }, tx);
        conn.Execute("DELETE FROM categories WHERE id=@c", new { c = categoryId }, tx);
        ActionLog.Record(conn, tx, ActionKind.CategoryDelete,
            Guid.NewGuid().ToString("N"), fromPath: folderPath);
        tx.Commit();
        return true;
    }
}
