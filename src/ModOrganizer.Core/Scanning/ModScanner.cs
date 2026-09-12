using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Pmp;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Scanning;

/// <summary>
/// Walks the mod roots and reconciles them with the database.
///
/// Everything here is batched, because the database is remote: the previous per-mod
/// implementation issued roughly six round trips per mod, so a 300-mod library spent
/// ~1800 × RTT (about a minute over a 35 ms link) waiting on the network. This version
/// reads the filesystem first, diffs it against the DB in memory, and then writes only
/// what actually changed — around a dozen round trips in total, and near-zero writes
/// when nothing changed on disk.
/// </summary>
public sealed class ModScanner
{
    private readonly DatabaseStore _store;
    private readonly PmpInspector _pmpInspector;
    private readonly IUserContext? _user;
    private readonly ILogger<ModScanner> _log;

    public ModScanner(DatabaseStore store, PmpInspector? pmpInspector = null,
        IUserContext? user = null, ILogger<ModScanner>? log = null)
    {
        _store = store;
        _pmpInspector = pmpInspector ?? new PmpInspector();
        _user = user;
        _log = log ?? NullLogger<ModScanner>.Instance;
    }

    // ---------------- filesystem snapshot ----------------

    private sealed record DiskFile(string Rel, string AbsPath, ModFileKind Kind, long Size, string Mtime);

    private sealed record DiskMod(string FolderName, string AbsPath, string? Ctime, string? Mtime, List<DiskFile> Files);

    private sealed record DiskCategory(string Name, string AbsPath, List<DiskMod> Mods);

    public ScanSummary Scan(long rootId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var conn = _store.Open();

        // The path is resolved for the CURRENT user: the same logical root lives at a
        // different mount point on every machine that syncs the folder.
        var root = conn.QuerySingleOrDefault<(long Id, string? Path, string DisplayName)>(
            """
            SELECT id AS Id, mo_root_path(id, @uid) AS Path, display_name AS DisplayName
            FROM roots WHERE id=@id
            """,
            new { id = rootId, uid = _user?.UserId });

        if (root.Id == 0)
            throw new InvalidOperationException($"Root {rootId} not found.");

        if (string.IsNullOrWhiteSpace(root.Path))
            throw new RootNotMappedException(rootId, root.DisplayName);

        if (!Directory.Exists(root.Path))
            throw new DirectoryNotFoundException(
                $"Der Ordner für „{root.DisplayName}\" existiert auf diesem PC nicht:\n{root.Path}\n\n" +
                "Unter Einstellungen kannst du den Ordner neu zuordnen.");

        // 1. Read the whole tree first. No DB work while we are I/O bound on the disk.
        var disk = ReadFromDisk(root.Path!, progress, ct);

        var filesSeen = disk.Sum(c => c.Mods.Sum(m => m.Files.Count));
        var modsSeen = disk.Sum(c => c.Mods.Count);

        // 2. Reconcile. One transaction, but now it holds only a handful of statements
        //    instead of staying open for the entire filesystem walk.
        using var tx = conn.BeginTransaction();

        var categoryIds = SyncCategories(conn, tx, rootId, disk, ct);
        var modIds = SyncMods(conn, tx, categoryIds, disk, ct);

        // Resolve each folder on disk to its database id once, so the file and metadata
        // passes below never have to search for it.
        var resolved = new List<(long ModId, DiskMod Mod)>(modsSeen);
        foreach (var category in disk)
        {
            if (!categoryIds.TryGetValue(category.Name, out var categoryId)) continue;
            foreach (var mod in category.Mods)
            {
                if (modIds.TryGetValue((categoryId, mod.FolderName), out var modId))
                    resolved.Add((modId, mod));
            }
        }

        var hashesComputed = SyncFiles(conn, tx, resolved, progress, ct);

        InspectNewPmps(conn, tx, resolved, ct);

        var modsMissing = MarkMissingMods(conn, tx, rootId, modIds.Values);
        var catsMissing = MarkMissingCategories(conn, tx, rootId, categoryIds.Values);

        tx.Commit();

        return new ScanSummary(
            disk.Count, modsSeen, filesSeen, hashesComputed,
            catsMissing, modsMissing, sw.Elapsed);
    }

    private static List<DiskCategory> ReadFromDisk(
        string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var result = new List<DiskCategory>();

        foreach (var categoryDir in Directory.EnumerateDirectories(rootPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var categoryName = Path.GetFileName(categoryDir);

            var modDirs = Directory.EnumerateDirectories(categoryDir)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mods = new List<DiskMod>(modDirs.Count);
            var filesDone = 0;

            for (var i = 0; i < modDirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var modDir = modDirs[i];
                var folderName = Path.GetFileName(modDir);

                var files = new List<DiskFile>();
                foreach (var p in EnumerateModFiles(modDir))
                {
                    ct.ThrowIfCancellationRequested();
                    FileInfo fi;
                    try { fi = new FileInfo(p); if (!fi.Exists) continue; }
                    catch { continue; }

                    files.Add(new DiskFile(
                        Path.GetRelativePath(modDir, p).Replace('\\', '/'),
                        p,
                        FileClassifier.Classify(fi.Name),
                        fi.Length,
                        new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToString("o")));
                }
                filesDone += files.Count;

                string? ctime = null, mtime = null;
                try
                {
                    var info = new DirectoryInfo(modDir);
                    ctime = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToString("o");
                    mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToString("o");
                }
                catch { /* folder times are advisory */ }

                mods.Add(new DiskMod(folderName, modDir, ctime, mtime, files));
                progress?.Report(new ScanProgress(categoryName, folderName, i + 1, modDirs.Count, filesDone));
            }

            result.Add(new DiskCategory(categoryName, categoryDir, mods));
        }

        return result;
    }

    private static IEnumerable<string> EnumerateModFiles(string modDir)
    {
        return Directory.EnumerateFiles(modDir, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        });
    }

    // ---------------- categories ----------------

    private static Dictionary<string, long> SyncCategories(
        NpgsqlConnection conn, NpgsqlTransaction tx, long rootId, List<DiskCategory> disk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var existing = conn.Query<(long Id, string Name, bool IsMissing)>(
            "SELECT id AS Id, name AS Name, is_missing AS IsMissing FROM categories WHERE root_id=@r",
            new { r = rootId }, tx)
            .ToList();

        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var missingIds = new List<long>();
        foreach (var e in existing)
        {
            byName[e.Name] = e.Id;
            if (e.IsMissing) missingIds.Add(e.Id);
        }

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var toInsert = new List<string>();

        foreach (var c in disk)
        {
            if (byName.TryGetValue(c.Name, out var id)) result[c.Name] = id;
            else toInsert.Add(c.Name);
        }

        if (toInsert.Count > 0)
        {
            var inserted = conn.Query<(long Id, string Name)>(
                """
                INSERT INTO categories(root_id, name, sort_order, is_missing)
                SELECT @r, n.name, base.next + n.ord, FALSE
                FROM unnest(@names::text[]) WITH ORDINALITY AS n(name, ord)
                CROSS JOIN (SELECT COALESCE(MAX(sort_order), 0) AS next
                            FROM categories WHERE root_id=@r) base
                RETURNING id AS Id, name AS Name
                """,
                new { r = rootId, names = toInsert.ToArray() }, tx)
                .ToList();

            foreach (var row in inserted) result[row.Name] = row.Id;
        }

        // Only un-mark the ones that were actually flagged missing — a blind UPDATE over
        // every category would wake every realtime subscriber on every scan.
        var reappeared = result.Values.Where(missingIds.Contains).ToArray();
        if (reappeared.Length > 0)
        {
            conn.Execute(
                "UPDATE categories SET is_missing=FALSE WHERE id = ANY(@ids)",
                new { ids = reappeared }, tx);
        }

        return result;
    }

    // ---------------- mods ----------------

    private static Dictionary<(long CategoryId, string FolderName), long> SyncMods(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Dictionary<string, long> categoryIds, List<DiskCategory> disk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var categoryIdArray = categoryIds.Values.Distinct().ToArray();
        var existing = categoryIdArray.Length == 0
            ? new List<(long Id, long CategoryId, string FolderName, string? Ctime, string? Mtime, bool IsMissing, string? DeletedAt)>()
            : conn.Query<(long Id, long CategoryId, string FolderName, string? Ctime, string? Mtime, bool IsMissing, string? DeletedAt)>(
                """
                SELECT id AS Id, category_id AS CategoryId, folder_name AS FolderName,
                       folder_ctime AS Ctime, folder_mtime AS Mtime,
                       is_missing AS IsMissing, deleted_at AS DeletedAt
                FROM mods WHERE category_id = ANY(@ids)
                """, new { ids = categoryIdArray }, tx).ToList();

        var lookup = new Dictionary<(long, string), (long Id, string? Ctime, string? Mtime, bool IsMissing, string? DeletedAt)>();
        foreach (var e in existing)
            lookup[(e.CategoryId, e.FolderName)] = (e.Id, e.Ctime, e.Mtime, e.IsMissing, e.DeletedAt);

        var result = new Dictionary<(long, string), long>();
        var insertCatIds = new List<long>();
        var insertNames = new List<string>();
        var insertCtimes = new List<string?>();
        var insertMtimes = new List<string?>();

        var updateIds = new List<long>();
        var updateCtimes = new List<string?>();
        var updateMtimes = new List<string?>();

        foreach (var category in disk)
        {
            if (!categoryIds.TryGetValue(category.Name, out var categoryId)) continue;

            foreach (var mod in category.Mods)
            {
                var key = (categoryId, mod.FolderName);
                if (lookup.TryGetValue(key, out var prev))
                {
                    result[key] = prev.Id;

                    // Write only when something actually differs. The old scanner bumped
                    // updated_at on every mod on every scan, which triggered a realtime
                    // storm to every other client for no reason.
                    var changed = prev.IsMissing
                                  || prev.DeletedAt is not null
                                  || prev.Ctime != mod.Ctime
                                  || prev.Mtime != mod.Mtime;
                    if (changed)
                    {
                        updateIds.Add(prev.Id);
                        updateCtimes.Add(mod.Ctime);
                        updateMtimes.Add(mod.Mtime);
                    }
                }
                else
                {
                    insertCatIds.Add(categoryId);
                    insertNames.Add(mod.FolderName);
                    insertCtimes.Add(mod.Ctime);
                    insertMtimes.Add(mod.Mtime);
                }
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("o");

        if (insertNames.Count > 0)
        {
            var inserted = conn.Query<(long Id, long CategoryId, string FolderName)>(
                """
                INSERT INTO mods(category_id, folder_name, created_at, updated_at,
                                 is_missing, folder_ctime, folder_mtime)
                SELECT d.cat, d.name, @t, @t, FALSE, d.ctime, d.mtime
                FROM unnest(@cats::bigint[], @names::text[], @ctimes::text[], @mtimes::text[])
                     AS d(cat, name, ctime, mtime)
                ON CONFLICT (category_id, folder_name) DO UPDATE SET
                    is_missing = FALSE,
                    deleted_at = NULL,
                    folder_ctime = EXCLUDED.folder_ctime,
                    folder_mtime = EXCLUDED.folder_mtime,
                    updated_at = EXCLUDED.updated_at
                RETURNING id AS Id, category_id AS CategoryId, folder_name AS FolderName
                """,
                new
                {
                    t = now,
                    cats = insertCatIds.ToArray(),
                    names = insertNames.ToArray(),
                    ctimes = insertCtimes.ToArray(),
                    mtimes = insertMtimes.ToArray()
                }, tx).ToList();

            foreach (var row in inserted) result[(row.CategoryId, row.FolderName)] = row.Id;
        }

        if (updateIds.Count > 0)
        {
            conn.Execute(
                """
                UPDATE mods m SET
                    is_missing = FALSE,
                    deleted_at = NULL,
                    folder_ctime = d.ctime,
                    folder_mtime = d.mtime,
                    updated_at = @t
                FROM unnest(@ids::bigint[], @ctimes::text[], @mtimes::text[]) AS d(id, ctime, mtime)
                WHERE m.id = d.id
                """,
                new
                {
                    t = now,
                    ids = updateIds.ToArray(),
                    ctimes = updateCtimes.ToArray(),
                    mtimes = updateMtimes.ToArray()
                }, tx);
        }

        return result;
    }

    // ---------------- files ----------------

    private int SyncFiles(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        List<(long ModId, DiskMod Mod)> resolved,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var modIdArray = resolved.Select(r => r.ModId).Distinct().ToArray();
        if (modIdArray.Length == 0) return 0;

        // One SELECT for every file of every mod in this root.
        var existingRows = conn.Query<(long ModId, string Rel, long Size, string? Mtime, long? Hash)>(
            """
            SELECT mod_id AS ModId, relative_path AS Rel, size_bytes AS Size,
                   mtime AS Mtime, xxhash64 AS Hash
            FROM mod_files WHERE mod_id = ANY(@ids)
            """, new { ids = modIdArray }, tx).ToList();

        var existing = new Dictionary<(long, string), (long Size, string? Mtime, long? Hash)>();
        foreach (var r in existingRows)
            existing[(r.ModId, r.Rel)] = (r.Size, r.Mtime, r.Hash);

        // Flatten disk state and pair each file with its mod id.
        var diskPairs = new List<(long ModId, DiskFile File)>();
        var present = new HashSet<(long, string)>();

        foreach (var (modId, mod) in resolved)
        {
            foreach (var f in mod.Files)
            {
                diskPairs.Add((modId, f));
                present.Add((modId, f.Rel));
            }
        }

        // Decide what needs hashing, then hash in parallel — this is pure disk/CPU work
        // and used to run strictly one file at a time.
        var needsHash = new List<(long ModId, DiskFile File)>();
        var upsert = new List<(long ModId, DiskFile File, long? Hash)>();

        foreach (var (modId, file) in diskPairs)
        {
            ct.ThrowIfCancellationRequested();
            existing.TryGetValue((modId, file.Rel), out var prev);
            var unchanged = prev.Mtime is not null && prev.Size == file.Size && prev.Mtime == file.Mtime;

            if (unchanged && !(FileClassifier.ShouldHash(file.Kind) && prev.Hash is null))
                continue; // nothing to write for this file

            if (FileClassifier.ShouldHash(file.Kind) && (!unchanged || prev.Hash is null))
                needsHash.Add((modId, file));
            else
                upsert.Add((modId, file, unchanged ? prev.Hash : null));
        }

        var hashed = new ConcurrentBag<(long ModId, DiskFile File, long? Hash)>();
        var hashesComputed = 0;

        if (needsHash.Count > 0)
        {
            var done = 0;
            Parallel.ForEach(needsHash,
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
                },
                item =>
                {
                    long? hash = null;
                    try { if (File.Exists(item.File.AbsPath)) hash = ComputeXxHash64(item.File.AbsPath); }
                    catch { /* unreadable file keeps a null hash */ }

                    if (hash.HasValue) Interlocked.Increment(ref hashesComputed);
                    hashed.Add((item.ModId, item.File, hash));

                    var n = Interlocked.Increment(ref done);
                    if (n % 16 == 0)
                        progress?.Report(new ScanProgress("Hashing", item.File.Rel, n, needsHash.Count, n));
                });
        }

        upsert.AddRange(hashed);

        if (upsert.Count > 0)
            BulkUpsertModFiles(conn, tx, upsert);

        // Prune only rows that really vanished; usually none, so usually no statement.
        var stale = existing.Keys.Where(k => !present.Contains(k)).ToList();
        if (stale.Count > 0)
        {
            conn.Execute(
                """
                DELETE FROM mod_files f
                USING unnest(@ids::bigint[], @rels::text[]) AS d(mod_id, rel)
                WHERE f.mod_id = d.mod_id AND f.relative_path = d.rel
                """,
                new
                {
                    ids = stale.Select(s => s.Item1).ToArray(),
                    rels = stale.Select(s => s.Item2).ToArray()
                }, tx);
        }

        return hashesComputed;
    }

    private static void BulkUpsertModFiles(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        List<(long ModId, DiskFile File, long? Hash)> rows)
    {
        if (rows.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO mod_files(mod_id, relative_path, kind, size_bytes, xxhash64, mtime)
            SELECT mod_id, rel, kind, size, hash, mtime
            FROM unnest(@mods::bigint[], @rels::text[], @kinds::int[],
                        @sizes::bigint[], @hashes::bigint[], @mtimes::text[])
                 AS t(mod_id, rel, kind, size, hash, mtime)
            ON CONFLICT (mod_id, relative_path) DO UPDATE SET
                kind = EXCLUDED.kind,
                size_bytes = EXCLUDED.size_bytes,
                xxhash64 = COALESCE(EXCLUDED.xxhash64, mod_files.xxhash64),
                mtime = EXCLUDED.mtime
            """;

        cmd.Parameters.AddWithValue("mods", rows.Select(r => r.ModId).ToArray());
        cmd.Parameters.AddWithValue("rels", rows.Select(r => r.File.Rel).ToArray());
        cmd.Parameters.AddWithValue("kinds", rows.Select(r => (int)r.File.Kind).ToArray());
        cmd.Parameters.AddWithValue("sizes", rows.Select(r => r.File.Size).ToArray());

        // AddWithValue cannot infer the element type of an all-null long?[].
        var hashParam = cmd.CreateParameter();
        hashParam.ParameterName = "hashes";
        hashParam.Value = rows.Select(r => r.Hash).ToArray();
        cmd.Parameters.Add(hashParam);

        cmd.Parameters.AddWithValue("mtimes", rows.Select(r => r.File.Mtime).ToArray());
        cmd.ExecuteNonQuery();
    }

    // ---------------- pmp metadata ----------------

    /// <summary>
    /// Inspects only .pmp files that have no metadata row yet. Previously this ran a
    /// COUNT(*) against pmp_meta for every .pmp on every scan — 293 round trips to learn
    /// that nothing had changed.
    /// </summary>
    private void InspectNewPmps(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        List<(long ModId, DiskMod Mod)> resolved, CancellationToken ct)
    {
        var modIdArray = resolved.Select(r => r.ModId).Distinct().ToArray();
        if (modIdArray.Length == 0) return;

        var todo = conn.Query<(long FileId, long ModId, string Rel)>(
            """
            SELECT f.id AS FileId, f.mod_id AS ModId, f.relative_path AS Rel
            FROM mod_files f
            LEFT JOIN pmp_meta pm ON pm.mod_file_id = f.id
            WHERE f.mod_id = ANY(@ids) AND f.kind = @kind AND pm.mod_file_id IS NULL
            """,
            new { ids = modIdArray, kind = (int)ModFileKind.Pmp }, tx).ToList();

        if (todo.Count == 0) return;

        // mod id -> folder on disk, so we can turn a relative path into an absolute one.
        var modDirs = new Dictionary<long, string>();
        foreach (var (modId, mod) in resolved) modDirs[modId] = mod.AbsPath;

        foreach (var f in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (!modDirs.TryGetValue(f.ModId, out var modDir)) continue;

            var abs = Path.Combine(modDir, f.Rel.Replace('/', Path.DirectorySeparatorChar));
            var result = _pmpInspector.Inspect(abs, f.FileId);
            if (result is null) continue;

            _pmpInspector.PersistToDb(conn, tx, f.FileId, result);
        }
    }

    // ---------------- missing bookkeeping ----------------

    private static int MarkMissingMods(
        NpgsqlConnection conn, NpgsqlTransaction tx, long rootId, IEnumerable<long> seenModIds)
    {
        var seen = seenModIds.Distinct().ToArray();
        return conn.Execute(
            """
            UPDATE mods SET is_missing=TRUE
            WHERE is_missing=FALSE
              AND category_id IN (SELECT id FROM categories WHERE root_id=@r)
              AND NOT (id = ANY(@seen))
            """, new { r = rootId, seen }, tx);
    }

    private static int MarkMissingCategories(
        NpgsqlConnection conn, NpgsqlTransaction tx, long rootId, IEnumerable<long> seenCategoryIds)
    {
        var seen = seenCategoryIds.Distinct().ToArray();
        return conn.Execute(
            """
            UPDATE categories SET is_missing=TRUE
            WHERE is_missing=FALSE AND root_id=@r AND NOT (id = ANY(@seen))
            """, new { r = rootId, seen }, tx);
    }

    private static long ComputeXxHash64(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 1 << 20, FileOptions.SequentialScan);
        var hasher = new XxHash64();
        hasher.Append(fs);
        var bytes = hasher.GetCurrentHash();
        return BitConverter.ToInt64(bytes, 0);
    }
}
