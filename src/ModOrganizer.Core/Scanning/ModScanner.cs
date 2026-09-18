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

    /// <summary>
    /// Bulk statements move six-figure row counts over a WAN link, so they get their own
    /// budget. The 30 s default is right for the gallery, where a hang has to surface
    /// fast, and far too short here.
    /// </summary>
    private const int BulkCommandTimeoutSeconds = 300;

    // ---------------- filesystem snapshot ----------------

    private sealed record DiskFile(string Rel, string AbsPath, ModFileKind Kind, long Size, string Mtime);

    private sealed record DiskMod(string FolderName, string AbsPath, string? Ctime, string? Mtime, List<DiskFile> Files);

    private sealed record DiskCategory(string Name, string AbsPath, List<DiskMod> Mods);

    /// <summary>
    /// One scan per root at a time. The folder watcher fires every couple of seconds while
    /// Nextcloud syncs, and a scan of a large library runs for minutes, so without this the
    /// scans stack up and block each other on the same rows.
    /// </summary>
    private static readonly ScanGate Gate = new();

    /// <summary>True while a scan of this root is running in this process.</summary>
    public static bool IsScanning(long rootId) => Gate.IsBusy(rootId);

    public ScanSummary Scan(long rootId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Gate.TryEnter(rootId)) throw new ScanAlreadyRunningException(rootId);

        try
        {
            return ScanCore(rootId, progress, ct);
        }
        finally
        {
            Gate.Exit(rootId);
        }
    }

    private ScanSummary ScanCore(long rootId, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var conn = _store.Open();

        // The path is resolved for the CURRENT user: the same logical root lives at a
        // different mount point on every machine that syncs the folder.
        var root = conn.QuerySingleOrDefault<(long Id, string? Path, string DisplayName, string ScanMode, bool ScanModeDirty)>(
            """
            SELECT id AS Id, mo_root_path(id, @uid) AS Path, display_name AS DisplayName,
                   scan_mode AS ScanMode, scan_mode_dirty AS ScanModeDirty
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
        var nested = string.Equals(root.ScanMode, "auto", StringComparison.OrdinalIgnoreCase);
        var disk = ReadFromDisk(root.Path!, nested, progress, ct);

        var filesSeen = disk.Sum(c => c.Mods.Sum(m => m.Files.Count));
        var modsSeen = disk.Sum(c => c.Mods.Count);

        // 2. Work out what needs hashing and do it BEFORE any transaction is open.
        //
        //    Hashing reads whole .pmp/.ttmp2 archives, and on a Nextcloud-synced drive a
        //    single archive can take a second or more. Doing that inside the write
        //    transaction kept a session idle-in-transaction for 25 minutes on a 1200-file
        //    library, held locks on every row of the root, and made a second scan of the
        //    same root (the other user, or this app's own folder watcher) fail with
        //    "Exception while reading from stream" once the 30 s command timeout expired.
        var known = ReadKnownFiles(conn, rootId, ct);
        var hashes = ComputeHashes(disk, known, progress, ct, out var hashesComputed);

        // 3. Reconcile. Every write in one transaction, and nothing slow inside it.
        using var tx = conn.BeginTransaction();

        var categoryIds = SyncCategories(conn, tx, rootId, disk, ct);
        var modIds = SyncMods(conn, tx, categoryIds, disk, ct);

        // Resolve each folder on disk to its database id once, so the file and metadata
        // passes below never have to search for it.
        var resolved = new List<(long ModId, string Category, DiskMod Mod)>(modsSeen);
        foreach (var category in disk)
        {
            if (!categoryIds.TryGetValue(category.Name, out var categoryId)) continue;
            foreach (var mod in category.Mods)
            {
                if (modIds.TryGetValue((categoryId, mod.FolderName), out var modId))
                    resolved.Add((modId, category.Name, mod));
            }
        }

        SyncFiles(conn, tx, resolved, known, hashes, ct);

        // A library whose structure was just reinterpreted legitimately loses most of its
        // old rows, so the sync guard has to stand down for exactly this one scan.
        var missing = MarkMissingMods(conn, tx, rootId, root.Path!, modIds.Values,
            ignoreSyncGuard: root.ScanModeDirty);

        // If the mod-level check refused to flag, the category-level one must not flag
        // either — otherwise a half-synced root loses its whole category list.
        var catsMissing = missing.Skipped
            ? 0
            : MarkMissingCategories(conn, tx, rootId, categoryIds.Values);

        if (root.ScanModeDirty)
            conn.Execute("UPDATE roots SET scan_mode_dirty = FALSE WHERE id = @r", new { r = rootId }, tx);

        tx.Commit();

        // 4. PMP metadata last, outside the transaction, for the same reason: inspecting
        //    an archive means unzipping it. A failure here costs nothing - the next scan
        //    picks up whatever still has no metadata row.
        InspectNewPmps(conn, resolved, ct);

        return new ScanSummary(
            disk.Count, modsSeen, filesSeen, hashesComputed,
            catsMissing, missing.Marked, sw.Elapsed,
            missing.Vanished, missing.Skipped, missing.Obsolete);
    }

    /// <summary>
    /// How deep a mod sits below its library.
    ///
    /// Fixed is the original model - one level of categories, one level of mods - and it
    /// stays the default because that is how the gear libraries are laid out. Nested walks
    /// down until it reaches folders that actually hold files, which is what a pose library
    /// needs: Solo/NSFW/Sitzend/&lt;pose&gt;.
    /// </summary>
    private const int MaxNestedDepth = 6;

    /// <summary>
    /// A folder holds a mod rather than more folders. Files are the signal: a pose or an
    /// archive lives next to its preview image, while a grouping folder holds only folders.
    /// A folder with no children at all counts as a mod so that empty ones stay visible
    /// instead of silently disappearing.
    /// </summary>
    public static bool LooksLikeMod(string dir)
    {
        try
        {
            using var files = Directory.EnumerateFiles(dir).GetEnumerator();
            if (files.MoveNext()) return true;

            using var dirs = Directory.EnumerateDirectories(dir).GetEnumerator();
            return !dirs.MoveNext();
        }
        catch
        {
            return true;   // unreadable: treat as a leaf rather than descending blindly
        }
    }

    /// <summary>
    /// Collects (category path, mod folder) pairs below one top-level folder. The top level
    /// is always a category, never a mod - that keeps the sidebar meaningful and matches
    /// what the fixed mode did.
    /// </summary>
    private static void CollectNested(
        string rootPath, string dir, int depth, List<(string Category, string ModDir)> into, CancellationToken ct)
    {
        foreach (var child in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (depth >= MaxNestedDepth || LooksLikeMod(child))
            {
                var parent = Path.GetDirectoryName(child)!;
                into.Add((Path.GetRelativePath(rootPath, parent), child));
            }
            else
            {
                CollectNested(rootPath, child, depth + 1, into, ct);
            }
        }
    }

    private static List<DiskCategory> ReadFromDisk(
        string rootPath, bool nested, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var result = new List<DiskCategory>();

        // Category folder -> the mod folders belonging to it. In fixed mode that is simply
        // one level down; in nested mode the category is the whole path above the mod.
        var byCategory = new List<(string Name, string AbsPath, List<string> ModDirs)>();

        foreach (var topDir in Directory.EnumerateDirectories(rootPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!nested)
            {
                byCategory.Add((Path.GetFileName(topDir), topDir,
                    Directory.EnumerateDirectories(topDir)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
                continue;
            }

            var found = new List<(string Category, string ModDir)>();
            CollectNested(rootPath, topDir, 1, found, ct);

            foreach (var group in found.GroupBy(f => f.Category, StringComparer.OrdinalIgnoreCase))
            {
                byCategory.Add((group.Key, Path.Combine(rootPath, group.Key),
                    group.Select(g => g.ModDir)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }

        foreach (var (categoryName, categoryDir, modDirs) in byCategory)
        {
            ct.ThrowIfCancellationRequested();

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
                    missing_since = NULL,
                    missing_by = NULL,
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
                    missing_since = NULL,
                    missing_by = NULL,
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

    /// <summary>Key of a file as it exists independently of database ids.</summary>
    private readonly record struct FileKey(string Category, string Mod, string Rel);

    private readonly record struct KnownFile(long Size, string? Mtime, long? Hash);

    /// <summary>
    /// The root's current file state, keyed by path rather than by mod id, so it can be
    /// read before the mods are synced - which is what lets hashing happen outside the
    /// write transaction.
    /// </summary>
    private static Dictionary<FileKey, KnownFile> ReadKnownFiles(
        NpgsqlConnection conn, long rootId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var rows = conn.Query<(string Cat, string Mod, string Rel, long Size, string? Mtime, long? Hash)>(
            new CommandDefinition(
                """
                SELECT c.name AS Cat, m.folder_name AS Mod, f.relative_path AS Rel,
                       f.size_bytes AS Size, f.mtime AS Mtime, f.xxhash64 AS Hash
                FROM mod_files f
                JOIN mods m ON m.id = f.mod_id
                JOIN categories c ON c.id = m.category_id
                WHERE c.root_id = @r
                """,
                new { r = rootId },
                commandTimeout: BulkCommandTimeoutSeconds,
                cancellationToken: ct));

        var map = new Dictionary<FileKey, KnownFile>();
        foreach (var r in rows)
            map[new FileKey(r.Cat, r.Mod, r.Rel)] = new KnownFile(r.Size, r.Mtime, r.Hash);
        return map;
    }

    /// <summary>
    /// Hashes every archive whose size or timestamp changed, plus any that never got a
    /// hash. Runs with no transaction open and no database connection in use.
    /// </summary>
    private static Dictionary<string, long?> ComputeHashes(
        List<DiskCategory> disk, Dictionary<FileKey, KnownFile> known,
        IProgress<ScanProgress>? progress, CancellationToken ct, out int hashesComputed)
    {
        var todo = new List<DiskFile>();

        foreach (var category in disk)
        {
            foreach (var mod in category.Mods)
            {
                foreach (var file in mod.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!FileClassifier.ShouldHash(file.Kind)) continue;

                    known.TryGetValue(new FileKey(category.Name, mod.FolderName, file.Rel), out var prev);
                    var unchanged = prev.Mtime is not null && prev.Size == file.Size && prev.Mtime == file.Mtime;

                    if (!unchanged || prev.Hash is null) todo.Add(file);
                }
            }
        }

        var result = new Dictionary<string, long?>();
        hashesComputed = 0;
        if (todo.Count == 0) return result;

        var bag = new ConcurrentBag<(string Path, long? Hash)>();
        var computed = 0;
        var done = 0;

        Parallel.ForEach(todo,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
            },
            file =>
            {
                long? hash = null;
                try { if (File.Exists(file.AbsPath)) hash = ComputeXxHash64(file.AbsPath); }
                catch { /* unreadable file keeps a null hash */ }

                if (hash.HasValue) Interlocked.Increment(ref computed);
                bag.Add((file.AbsPath, hash));

                var n = Interlocked.Increment(ref done);
                if (n % 16 == 0)
                    progress?.Report(new ScanProgress("Hashing", file.Rel, n, todo.Count, n));
            });

        hashesComputed = computed;
        foreach (var (path, hash) in bag) result[path] = hash;
        return result;
    }

    /// <summary>
    /// Writes the file rows. Pure database work: everything expensive already happened in
    /// <see cref="ComputeHashes"/> before the transaction was opened.
    /// </summary>
    private static void SyncFiles(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        List<(long ModId, string Category, DiskMod Mod)> resolved,
        Dictionary<FileKey, KnownFile> known,
        Dictionary<string, long?> hashes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (resolved.Count == 0) return;

        // The category travels with each mod: in nested mode it is a multi-segment
        // path ("Solo\\NSFW\\Sitzend"), so deriving it from the folder name would be wrong.
        var upsert = new List<(long ModId, DiskFile File, long? Hash)>();
        var present = new HashSet<(long, string)>();
        var modKeys = new Dictionary<long, (string Category, string Mod)>();

        foreach (var (modId, categoryName, mod) in resolved)
        {
            modKeys[modId] = (categoryName, mod.FolderName);

            foreach (var file in mod.Files)
            {
                ct.ThrowIfCancellationRequested();
                present.Add((modId, file.Rel));

                known.TryGetValue(new FileKey(categoryName, mod.FolderName, file.Rel), out var prev);
                var unchanged = prev.Mtime is not null && prev.Size == file.Size && prev.Mtime == file.Mtime;

                // Presence in the dictionary is the signal, not the value: a file that was
                // hashed but could not be read maps to null, and must overwrite the stale
                // hash rather than keep it.
                var rehashed = hashes.TryGetValue(file.AbsPath, out var freshHash);

                // Nothing to write when the file is untouched and already carries whatever
                // hash it is supposed to have.
                if (unchanged && !rehashed) continue;

                upsert.Add((modId, file, rehashed ? freshHash : prev.Hash));
            }
        }

        if (upsert.Count > 0)
            BulkUpsertModFiles(conn, tx, upsert);

        // Prune only rows that really vanished, and only for mods still on disk: a mod
        // that is merely flagged missing keeps its file rows.
        var knownByMod = new Dictionary<(string, string), List<string>>();
        foreach (var key in known.Keys)
        {
            var bucket = (key.Category, key.Mod);
            if (!knownByMod.TryGetValue(bucket, out var list))
                knownByMod[bucket] = list = new List<string>();
            list.Add(key.Rel);
        }

        var stale = new List<(long ModId, string Rel)>();
        foreach (var (modId, key) in modKeys)
        {
            if (!knownByMod.TryGetValue((key.Category, key.Mod), out var rels)) continue;
            foreach (var rel in rels)
                if (!present.Contains((modId, rel))) stale.Add((modId, rel));
        }

        if (stale.Count > 0)
        {
            conn.Execute(new CommandDefinition(
                """
                DELETE FROM mod_files f
                USING unnest(@ids::bigint[], @rels::text[]) AS d(mod_id, rel)
                WHERE f.mod_id = d.mod_id AND f.relative_path = d.rel
                """,
                new
                {
                    ids = stale.Select(x => x.ModId).ToArray(),
                    rels = stale.Select(x => x.Rel).ToArray()
                },
                tx, commandTimeout: BulkCommandTimeoutSeconds, cancellationToken: ct));
        }
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
        cmd.CommandTimeout = BulkCommandTimeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    // ---------------- pmp metadata ----------------

    /// <summary>
    /// Inspects only .pmp files that have no metadata row yet. Previously this ran a
    /// COUNT(*) against pmp_meta for every .pmp on every scan — 293 round trips to learn
    /// that nothing had changed.
    /// </summary>
    private void InspectNewPmps(
        NpgsqlConnection conn,
        List<(long ModId, string Category, DiskMod Mod)> resolved, CancellationToken ct)
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
            new { ids = modIdArray, kind = (int)ModFileKind.Pmp }).ToList();

        if (todo.Count == 0) return;

        // mod id -> folder on disk, so we can turn a relative path into an absolute one.
        var modDirs = new Dictionary<long, string>();
        foreach (var (modId, _, mod) in resolved) modDirs[modId] = mod.AbsPath;

        foreach (var f in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (!modDirs.TryGetValue(f.ModId, out var modDir)) continue;

            var abs = Path.Combine(modDir, f.Rel.Replace('/', Path.DirectorySeparatorChar));

            PmpMetaResult? result;
            try
            {
                // Unzipping happens here, with no transaction open.
                result = _pmpInspector.Inspect(abs, f.FileId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PMP inspection failed for {Path}", abs);
                continue;
            }

            if (result is null) continue;

            // One short transaction per archive: the metadata for a single file is written
            // all-or-nothing, but a slow archive never blocks anyone else's scan.
            try
            {
                using var metaTx = conn.BeginTransaction();
                _pmpInspector.PersistToDb(conn, metaTx, f.FileId, result);
                metaTx.Commit();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Storing PMP metadata failed for {Path}", abs);
            }
        }
    }

    // ---------------- missing bookkeeping ----------------

    /// <summary>What one scan decided about the mods it did not find on disk.</summary>
    private readonly record struct MissingResult(int Marked, int Vanished, bool Skipped, int Obsolete);

    /// <summary>
    /// A scan that suddenly cannot find most of the library is far more likely to be a
    /// half-synced Nextcloud folder (or the wrong folder mapped) than a real mass
    /// deletion. Flagging in that case would hide a working library from BOTH users,
    /// since is_missing is shared, so nothing is flagged and the caller reports it.
    /// </summary>
    public static bool LooksLikeIncompleteSync(int liveInDb, int vanished) =>
        liveInDb >= 20 && vanished > liveInDb / 2;

    private MissingResult MarkMissingMods(
        NpgsqlConnection conn, NpgsqlTransaction tx, long rootId, string rootPath,
        IEnumerable<long> seenModIds, bool ignoreSyncGuard = false)
    {
        var seen = seenModIds.Distinct().ToArray();

        // Rows this scan did not produce, with enough to rebuild their path on disk.
        var candidates = conn.Query<(long Id, string Category, string Folder, int Rating)>(
            """
            SELECT m.id AS Id, c.name AS Category, m.folder_name AS Folder, m.rating AS Rating
            FROM mods m
            JOIN categories c ON c.id = m.category_id
            WHERE m.deleted_at IS NULL AND c.root_id = @r AND NOT (m.id = ANY(@seen))
            """, new { r = rootId, seen }, tx).ToList();

        // A folder that is still on disk is not missing - it simply stopped being a mod,
        // which is what happens to a grouping folder when a library switches to the nested
        // model. Calling that "gone from disk" sent people looking for files that are right
        // where they always were.
        var obsolete = new List<long>();
        var reallyGone = new List<long>();

        foreach (var c in candidates)
        {
            var path = Path.Combine(rootPath, c.Category, c.Folder);
            if (Directory.Exists(path)) obsolete.Add(c.Id);
            else reallyGone.Add(c.Id);
        }

        // Obsolete rows are dropped, but only when nothing of the user's own work hangs off
        // them. Anything rated, tagged or commented is left for a human to look at.
        var removed = 0;
        if (obsolete.Count > 0)
        {
            removed = conn.Execute(
                """
                DELETE FROM mods m
                WHERE m.id = ANY(@ids)
                  AND m.rating = 0
                  AND NOT EXISTS (SELECT 1 FROM mod_tags     t WHERE t.mod_id = m.id)
                  AND NOT EXISTS (SELECT 1 FROM mod_comments k WHERE k.mod_id = m.id)
                """, new { ids = obsolete.ToArray() }, tx);
        }

        var live = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(*)::int FROM mods m
            JOIN categories c ON c.id = m.category_id
            WHERE m.deleted_at IS NULL AND c.root_id = @r
            """, new { r = rootId }, tx);

        var counts = (Live: live, Vanished: reallyGone.Count);

        if (!ignoreSyncGuard && LooksLikeIncompleteSync(counts.Live, counts.Vanished))
        {
            _log.LogWarning(
                "Root {Root}: {Vanished} of {Live} mods not found on disk - not flagging, " +
                "this looks like an incomplete sync rather than a deletion.",
                rootId, counts.Vanished, counts.Live);
            return new MissingResult(0, counts.Vanished, Skipped: true, Obsolete: removed);
        }

        // missing_since is set once and then left alone, so "gone for two weeks" stays
        // distinguishable from "gone since the last scan" across any number of scans.
        var marked = reallyGone.Count == 0 ? 0 : conn.Execute(
            """
            UPDATE mods SET is_missing=TRUE, missing_since=NOW(), missing_by=@u
            WHERE is_missing=FALSE AND deleted_at IS NULL AND id = ANY(@ids)
            """, new { ids = reallyGone.ToArray(), u = _user?.UserId }, tx);

        return new MissingResult(marked, counts.Vanished, Skipped: false, Obsolete: removed);
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
