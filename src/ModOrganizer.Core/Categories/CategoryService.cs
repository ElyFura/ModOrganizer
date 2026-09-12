using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.Core.Categories;

public sealed class CategoryMergeConflict
{
    public required long SourceModId { get; init; }
    public required string FolderName { get; init; }
    public required string TargetModPath { get; init; }
}

public sealed class CategoryMergePlan
{
    public string TxId { get; init; } = Guid.NewGuid().ToString("N");
    public long SourceCategoryId { get; init; }
    public long TargetCategoryId { get; init; }
    public string SourceCategoryPath { get; init; } = "";
    public string TargetCategoryPath { get; init; } = "";
    public List<long> ModsToMove { get; } = new();
    public List<CategoryMergeConflict> Conflicts { get; } = new();
}

public sealed class CategoryService
{
    private readonly DatabaseStore _store;
    private readonly Management.FileSystemActivityGate? _gate;
    private readonly IUserContext? _user;

    public CategoryService(DatabaseStore store, Management.FileSystemActivityGate? gate = null,
        IUserContext? user = null)
    {
        _store = store;
        _gate = gate;
        _user = user;
    }

    /// <summary>Resolves a root to the folder THIS user has it mounted at.</summary>
    private string RootPathOf(Npgsql.NpgsqlConnection conn, long rootId) =>
        conn.QuerySingleOrDefault<string>("SELECT mo_root_path(@r, @uid)",
            new { r = rootId, uid = _user?.UserId })
        ?? throw new InvalidOperationException(
            "Für diese Bibliothek ist auf diesem PC kein Ordner zugeordnet. " +
            "Ordne sie unter Einstellungen zu.");

    public long Add(long rootId, string name, string? iconName = null, string? colorHex = null)
    {
        ValidateName(name);
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();

        var rootPath = RootPathOf(conn, rootId);
        var target = Path.Combine(rootPath, name);
        if (Directory.Exists(target))
            throw new InvalidOperationException($"Folder '{target}' already exists.");

        var createdDirectory = false;
        try
        {
            using var tx = conn.BeginTransaction();

            // Insert before creating the folder. The unique index on (root_id, LOWER(name))
            // is the real gatekeeper — adding "gear" next to an existing "Gear" fails here.
            // Creating the folder first meant that rejection rolled back the row but left
            // the folder behind, and the Directory.Exists guard above then blocked every
            // retry with no way to recover from the UI.
            var id = conn.ExecuteScalar<long>(
                """
                INSERT INTO categories(root_id, name, sort_order, icon_name, color_hex, is_missing)
                VALUES (@r, @n, (SELECT COALESCE(MAX(sort_order),0)+1 FROM categories WHERE root_id=@r), @i, @c, FALSE)
                RETURNING id
                """,
                new { r = rootId, n = name, i = iconName, c = colorHex }, tx);

            Directory.CreateDirectory(target);
            createdDirectory = true;

            Management.ActionLog.Record(conn, tx, ActionKind.CategoryAdd,
                Guid.NewGuid().ToString("N"), toPath: target);
            tx.Commit();
            return id;
        }
        catch
        {
            // The transaction rolls back on its own; the folder will not.
            if (createdDirectory) TryDeleteEmptyDirectory(target);
            throw;
        }
    }

    /// <summary>
    /// Returns the id of the category with this name in the root, creating it if needed.
    /// <see cref="Add"/> deliberately refuses an existing name; callers that just want a
    /// destination to exist (the duplicates view's archive category) need this instead.
    /// </summary>
    public long EnsureCategory(long rootId, string name)
    {
        ValidateName(name);

        using (var conn = _store.Open())
        {
            var existing = conn.QuerySingleOrDefault<long?>(
                "SELECT id FROM categories WHERE root_id=@r AND LOWER(name)=LOWER(@n)",
                new { r = rootId, n = name.Trim() });

            if (existing.HasValue)
            {
                // The row may exist while the folder does not (a category marked missing).
                var rootPath = RootPathOf(conn, rootId);
                var folder = Path.Combine(rootPath, name.Trim());

                if (!Directory.Exists(folder))
                {
                    using var _suppress = _gate?.Suppress();
                    Directory.CreateDirectory(folder);
                    conn.Execute("UPDATE categories SET is_missing=FALSE WHERE id=@id",
                        new { id = existing.Value });
                }
                return existing.Value;
            }
        }

        return Add(rootId, name);
    }

    /// <summary>
    /// Best-effort cleanup of a folder this service just created. Only removes it when it
    /// is still empty, so a failure can never take user data with it.
    /// </summary>
    internal static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { /* leaving an empty folder behind is the lesser evil */ }
    }

    public void Rename(long categoryId, string newName)
    {
        ValidateName(newName);
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();

        var row = conn.QuerySingle<(long RootId, string OldName, string RootPath)>(
            """
            SELECT c.root_id AS RootId, c.name AS OldName,
                   mo_root_path(c.root_id, @uid) AS RootPath
            FROM categories c
            WHERE c.id=@id
            """, new { id = categoryId, uid = _user?.UserId });

        if (string.Equals(row.OldName, newName, StringComparison.Ordinal)) return;

        var rootPath = RootNotMappedException.Require(row.RootPath);
        var oldPath = Path.Combine(rootPath, row.OldName);
        var newPath = Path.Combine(rootPath, newName);

        // "Gear" -> "gear" is a real rename, but Windows paths are case-insensitive, so
        // Directory.Exists(newPath) is true for the very folder we are renaming. Without
        // this distinction a case-only rename failed with a misleading
        // "target already exists".
        var caseOnly = string.Equals(row.OldName, newName, StringComparison.OrdinalIgnoreCase);

        if (!caseOnly && Directory.Exists(newPath))
            throw new InvalidOperationException($"Target folder '{newPath}' already exists.");

        var txId = Guid.NewGuid().ToString("N");

        var movedFrom = (string?)null;
        var createdDirectory = false;

        try
        {
            using var tx = conn.BeginTransaction();

            conn.Execute("UPDATE categories SET name=@n, is_missing=FALSE WHERE id=@id",
                new { n = newName, id = categoryId }, tx);

            if (Directory.Exists(oldPath))
            {
                MoveDirectory(oldPath, newPath, caseOnly);
                movedFrom = oldPath;
            }
            else
            {
                // The category row existed without a folder (marked missing) — renaming
                // it is also how the user repairs that.
                Directory.CreateDirectory(newPath);
                createdDirectory = true;
            }

            Management.ActionLog.Record(conn, tx, ActionKind.CategoryRename, txId,
                fromPath: oldPath, toPath: newPath);
            tx.Commit();
        }
        catch
        {
            // Put the folder back, or the DB and the disk disagree about the name and the
            // category shows up as missing on the next scan.
            if (movedFrom is not null)
            {
                try { MoveDirectory(newPath, movedFrom, caseOnly); } catch { }
            }
            else if (createdDirectory)
            {
                TryDeleteEmptyDirectory(newPath);
            }
            throw;
        }
    }

    /// <summary>
    /// Directory.Move refuses a source and destination that differ only in case, so a
    /// case-only rename goes through a temporary name.
    /// </summary>
    internal static void MoveDirectory(string from, string to, bool caseOnly)
    {
        if (!caseOnly)
        {
            Directory.Move(from, to);
            return;
        }

        var parent = Path.GetDirectoryName(from)!;
        string staging;
        do
        {
            staging = Path.Combine(parent, "__case_" + Guid.NewGuid().ToString("N")[..8]);
        }
        while (Directory.Exists(staging));

        Directory.Move(from, staging);
        try
        {
            Directory.Move(staging, to);
        }
        catch
        {
            // Never leave the folder parked under the staging name.
            try { Directory.Move(staging, from); } catch { }
            throw;
        }
    }

    public int CountMods(long categoryId)
    {
        using var conn = _store.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM mods WHERE category_id=@c AND deleted_at IS NULL",
            new { c = categoryId });
    }

    public bool DeleteIfEmpty(long categoryId)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();

        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM mods WHERE category_id=@c AND deleted_at IS NULL",
            new { c = categoryId });
        if (count > 0) return false;

        var row = conn.QuerySingle<(string RootPath, string Name)>(
            """
            SELECT mo_root_path(c.root_id, @uid) AS RootPath, c.name AS Name
            FROM categories c
            WHERE c.id=@id
            """, new { id = categoryId, uid = _user?.UserId });

        var folderPath = Path.Combine(RootNotMappedException.Require(row.RootPath), row.Name);

        // Check emptiness before opening the transaction — bailing out from inside one
        // just to roll it back is pointless work against a remote database.
        var folderExists = Directory.Exists(folderPath);
        if (folderExists && Directory.EnumerateFileSystemEntries(folderPath).Any())
            return false;

        var deletedDirectory = false;
        try
        {
            using var tx = conn.BeginTransaction();

            conn.Execute("DELETE FROM mods WHERE category_id=@c", new { c = categoryId }, tx);
            conn.Execute("DELETE FROM categories WHERE id=@id", new { id = categoryId }, tx);

            if (folderExists)
            {
                Directory.Delete(folderPath);
                deletedDirectory = true;
            }

            Management.ActionLog.Record(conn, tx, ActionKind.CategoryDelete,
                Guid.NewGuid().ToString("N"), fromPath: folderPath);
            tx.Commit();
            return true;
        }
        catch
        {
            // The folder was verified empty, so recreating it restores the exact prior
            // state. Previously a failed DELETE rolled the rows back but left the folder
            // gone, and the category then read as missing.
            if (deletedDirectory)
            {
                try { Directory.CreateDirectory(folderPath); } catch { }
            }
            throw;
        }
    }

    public CategoryMergePlan CreateMergePlan(long sourceCategoryId, long targetCategoryId)
    {
        if (sourceCategoryId == targetCategoryId)
            throw new ArgumentException("Source and target must differ.");

        using var conn = _store.Open();
        var source = conn.QuerySingle<(long RootId, string Name, string RootPath)>(
            """
            SELECT c.root_id AS RootId, c.name AS Name, mo_root_path(c.root_id, @uid) AS RootPath
            FROM categories c WHERE c.id=@id
            """, new { id = sourceCategoryId, uid = _user?.UserId });
        var target = conn.QuerySingle<(long RootId, string Name, string RootPath)>(
            """
            SELECT c.root_id AS RootId, c.name AS Name, mo_root_path(c.root_id, @uid) AS RootPath
            FROM categories c WHERE c.id=@id
            """, new { id = targetCategoryId, uid = _user?.UserId });

        if (source.RootId != target.RootId)
            throw new InvalidOperationException("Cross-root merge not supported.");

        var sourcePath = Path.Combine(RootNotMappedException.Require(source.RootPath), source.Name);
        var targetPath = Path.Combine(RootNotMappedException.Require(target.RootPath), target.Name);

        var plan = new CategoryMergePlan
        {
            SourceCategoryId = sourceCategoryId,
            TargetCategoryId = targetCategoryId,
            SourceCategoryPath = sourcePath,
            TargetCategoryPath = targetPath
        };

        var sourceMods = conn.Query<(long Id, string FolderName)>(
            "SELECT id AS Id, folder_name AS FolderName FROM mods WHERE category_id=@c AND deleted_at IS NULL",
            new { c = sourceCategoryId }).ToList();

        var targetFolderNames = new HashSet<string>(
            conn.Query<string>(
                "SELECT folder_name FROM mods WHERE category_id=@c AND deleted_at IS NULL",
                new { c = targetCategoryId }),
            StringComparer.OrdinalIgnoreCase);

        foreach (var m in sourceMods)
        {
            plan.ModsToMove.Add(m.Id);
            if (targetFolderNames.Contains(m.FolderName))
            {
                plan.Conflicts.Add(new CategoryMergeConflict
                {
                    SourceModId = m.Id,
                    FolderName = m.FolderName,
                    TargetModPath = Path.Combine(targetPath, m.FolderName)
                });
            }
        }

        return plan;
    }

    public enum MergeConflictStrategy { Skip, RenameWithSuffix }

    public void ExecuteMerge(CategoryMergePlan plan, MergeConflictStrategy strategy)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();
        Directory.CreateDirectory(plan.TargetCategoryPath);

        var conflictSet = plan.Conflicts.ToDictionary(c => c.SourceModId);
        var moved = new List<(string From, string To, long ModId, string NewFolderName)>();

        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var modId in plan.ModsToMove)
            {
                var folderName = conn.QuerySingle<string>(
                    "SELECT folder_name FROM mods WHERE id=@m", new { m = modId }, tx);

                var from = Path.Combine(plan.SourceCategoryPath, folderName);

                string newFolderName = folderName;
                if (conflictSet.ContainsKey(modId))
                {
                    if (strategy == MergeConflictStrategy.Skip) continue;
                    newFolderName = GenerateUniqueName(plan.TargetCategoryPath, folderName);
                }

                var to = Path.Combine(plan.TargetCategoryPath, newFolderName);
                if (Directory.Exists(from))
                    Directory.Move(from, to);

                conn.Execute(
                    """
                    UPDATE mods SET category_id=@tc, folder_name=@n, updated_at=@t
                    WHERE id=@m
                    """,
                    new
                    {
                        tc = plan.TargetCategoryId,
                        n = newFolderName,
                        t = DateTimeOffset.UtcNow.ToString("o"),
                        m = modId
                    }, tx);

                Management.ActionLog.Record(conn, tx, ActionKind.Move, plan.TxId,
                    modId: modId, fromPath: from, toPath: to);

                moved.Add((from, to, modId, newFolderName));
            }

            conn.Execute("DELETE FROM categories WHERE id=@c AND NOT EXISTS (SELECT 1 FROM mods WHERE category_id=@c AND deleted_at IS NULL)",
                new { c = plan.SourceCategoryId }, tx);

            Management.ActionLog.Record(conn, tx, ActionKind.CategoryMerge, plan.TxId,
                fromPath: plan.SourceCategoryPath, toPath: plan.TargetCategoryPath);

            tx.Commit();
        }
        catch
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!Directory.Exists(moved[i].To)) continue;

                    // Recreate the source category folder if it is gone: without its
                    // parent, every Directory.Move below throws and the rollback silently
                    // does nothing, leaving mods physically in the target while the DB has
                    // rolled them back to a source category that no longer has a folder.
                    var parent = Path.GetDirectoryName(moved[i].From);
                    if (parent is not null) Directory.CreateDirectory(parent);

                    Directory.Move(moved[i].To, moved[i].From);
                }
                catch { }
            }
            throw;
        }

        // Only now, with the transaction committed, is it safe to drop the source folder.
        // Deleting it before the commit broke the rollback path above.
        try
        {
            if (Directory.Exists(plan.SourceCategoryPath) &&
                !Directory.EnumerateFileSystemEntries(plan.SourceCategoryPath).Any())
            {
                Directory.Delete(plan.SourceCategoryPath);
            }
        }
        catch { /* an empty leftover folder reappears as a category on the next scan */ }
    }

    /// <summary>
    /// Writes the whole ordering in one statement. One UPDATE per category meant every
    /// click of the Up/Down arrows cost N round trips to a remote database.
    /// </summary>
    public void Reorder(long rootId, IReadOnlyList<long> orderedCategoryIds)
    {
        if (orderedCategoryIds.Count == 0) return;

        var ids = orderedCategoryIds.ToArray();
        var orders = Enumerable.Range(1, ids.Length).ToArray();

        using var conn = _store.Open();
        conn.Execute(
            """
            UPDATE categories c SET sort_order = d.ord
            FROM unnest(@ids::bigint[], @orders::int[]) AS d(id, ord)
            WHERE c.id = d.id AND c.root_id = @r
            """,
            new { ids, orders, r = rootId });
    }

    public void SetIconAndColor(long categoryId, string? iconName, string? colorHex)
    {
        using var conn = _store.Open();
        conn.Execute(
            "UPDATE categories SET icon_name=@i, color_hex=@c WHERE id=@id",
            new { i = iconName, c = colorHex, id = categoryId });
    }

    private static string GenerateUniqueName(string parentPath, string baseName)
    {
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Directory.Exists(Path.Combine(parentPath, candidate)))
                return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N})";
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Name contains invalid characters.", nameof(name));
    }
}
