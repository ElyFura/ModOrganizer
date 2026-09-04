using Dapper;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Queries;

public enum ModSort
{
    CategoryThenName = 0,
    Name = 1,
    AddedNewest = 2,
    UpdatedNewest = 3,
    LastViewed = 4,
    SizeDesc = 5,
    RatingDesc = 6
}

public sealed class RootInfo
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class CategoryInfo
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public string? IconName { get; set; }
    public string? ColorHex { get; set; }
    public int ModCount { get; set; }
    public bool IsMissing { get; set; }
}

public sealed class ModCard
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public long RootId { get; set; }
    public string FolderName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string FolderAbsPath { get; set; } = "";
    public string? PrimaryImageAbsPath { get; set; }
    public int PmpCount { get; set; }
    public int TtmpCount { get; set; }
    public int ImageCount { get; set; }
    public bool IsMissing { get; set; }
    public int Rating { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long TotalSizeBytes { get; set; }
    public string? UpdatedByName { get; set; }
    public string? UpdatedByColor { get; set; }
    public IReadOnlyList<ModTagRef> Tags { get; set; } = Array.Empty<ModTagRef>();
}

/// <summary>A tag as shown on a gallery card.</summary>
public sealed record ModTagRef(long Id, string Name, string? ColorHex);

/// <summary>
/// Everything the gallery filters on. A record rather than more positional parameters —
/// with tag include/exclude sets this had grown past what a parameter list can carry
/// readably.
/// </summary>
public sealed record ModQuery
{
    public long RootId { get; init; }
    public long? CategoryId { get; init; }
    public string? SearchText { get; init; }
    public ModSort Sort { get; init; } = ModSort.CategoryThenName;
    public int MinRating { get; init; }

    /// <summary>Mod must carry every one of these (the AND mode).</summary>
    public IReadOnlyList<long> TagsAll { get; init; } = Array.Empty<long>();

    /// <summary>Mod must carry at least one of these (the OR mode).</summary>
    public IReadOnlyList<long> TagsAny { get; init; } = Array.Empty<long>();

    /// <summary>Mod must carry none of these (the -tag negation).</summary>
    public IReadOnlyList<long> TagsNone { get; init; } = Array.Empty<long>();

    /// <summary>True when no tag has been assigned at all.</summary>
    public bool OnlyUntagged { get; init; }
}

public sealed class ModLibraryService
{
    private readonly DatabaseStore _store;

    public ModLibraryService(DatabaseStore store) => _store = store;

    private const string RootsSql =
        "SELECT id AS Id, path AS Path, display_name AS DisplayName, enabled AS Enabled FROM roots ORDER BY added_at";

    public IReadOnlyList<RootInfo> GetRoots()
    {
        using var conn = _store.Open();
        return conn.Query<RootInfo>(RootsSql).ToList();
    }

    public async Task<IReadOnlyList<RootInfo>> GetRootsAsync(CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RootInfo>(new CommandDefinition(RootsSql, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.ToList();
    }

    private const string CategoriesSql =
        """
        SELECT c.id AS Id, c.root_id AS RootId, c.name AS Name, c.sort_order AS SortOrder,
               c.icon_name AS IconName, c.color_hex AS ColorHex,
               COALESCE(mc.cnt, 0) AS ModCount,
               c.is_missing AS IsMissing
        FROM categories c
        LEFT JOIN (
            SELECT category_id, COUNT(*) AS cnt
            FROM mods
            WHERE is_missing = FALSE AND deleted_at IS NULL
            GROUP BY category_id
        ) mc ON mc.category_id = c.id
        WHERE c.root_id = @r
        ORDER BY c.sort_order, c.name
        """;

    public IReadOnlyList<CategoryInfo> GetCategories(long rootId)
    {
        using var conn = _store.Open();
        return conn.Query<CategoryInfo>(CategoriesSql, new { r = rootId }).ToList();
    }

    public async Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(long rootId, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<CategoryInfo>(
            new CommandDefinition(CategoriesSql, new { r = rootId }, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.ToList();
    }

    private static string OrderByFor(ModSort sort) => sort switch
    {
        ModSort.Name          => "t.folder_name",
        ModSort.AddedNewest   => "t.folder_ctime IS NULL, t.folder_ctime DESC, t.created_at DESC",
        ModSort.UpdatedNewest => "t.folder_mtime IS NULL, t.folder_mtime DESC, t.updated_at DESC",
        ModSort.LastViewed    => "t.last_viewed_at IS NULL, t.last_viewed_at DESC",
        ModSort.SizeDesc      => "COALESCE(a.total_size, 0) DESC",
        ModSort.RatingDesc    => "t.rating DESC, t.folder_name",
        _                     => "t.sort_order, t.cat_name, t.folder_name"
    };

    /// <summary>
    /// The filtered mod set. Shared verbatim by the card query and the tag query so both
    /// see exactly the same rows, which is what lets them travel in one round trip.
    /// </summary>
    private const string TargetCte = """
        WITH target AS (
            SELECT m.id, m.category_id, m.folder_name, m.display_name, m.rating,
                   m.created_at, m.updated_at, m.last_viewed_at,
                   m.folder_ctime, m.folder_mtime, m.is_missing, m.updated_by,
                   c.name AS cat_name, c.root_id, c.sort_order
            FROM mods m
            JOIN categories c ON c.id = m.category_id
            WHERE c.root_id = @r
              AND m.deleted_at IS NULL
              AND (@cat::bigint IS NULL OR m.category_id = @cat::bigint)
              AND (@minRating::int = 0 OR m.rating >= @minRating::int)
              AND (@qLike::text IS NULL
                   OR m.folder_name ILIKE @qLike::text ESCAPE '\'
                   OR m.display_name ILIKE @qLike::text ESCAPE '\')
              -- AND mode: the mod carries every selected tag.
              AND (cardinality(@tagsAll::bigint[]) = 0 OR (
                    SELECT COUNT(DISTINCT mt.tag_id) FROM mod_tags mt
                    WHERE mt.mod_id = m.id AND mt.tag_id = ANY(@tagsAll::bigint[])
                  ) = cardinality(@tagsAll::bigint[]))
              -- OR mode: at least one.
              AND (cardinality(@tagsAny::bigint[]) = 0 OR EXISTS (
                    SELECT 1 FROM mod_tags mt
                    WHERE mt.mod_id = m.id AND mt.tag_id = ANY(@tagsAny::bigint[])))
              -- Negation: none of these.
              AND (cardinality(@tagsNone::bigint[]) = 0 OR NOT EXISTS (
                    SELECT 1 FROM mod_tags mt
                    WHERE mt.mod_id = m.id AND mt.tag_id = ANY(@tagsNone::bigint[])))
              -- "ohne Tags" status filter.
              AND (@onlyUntagged::boolean = FALSE OR NOT EXISTS (
                    SELECT 1 FROM mod_tags mt WHERE mt.mod_id = m.id))
        )
        """;

    /// <summary>
    /// One round trip for the cards and their tags: one pass over mod_files instead of six
    /// correlated subqueries per row. The old shape ran ~1800 subquery executions for a
    /// 300-mod library.
    /// </summary>
    private static string ModsSql(ModSort sort) => $"""
        {TargetCte},
        agg AS (
            SELECT f.mod_id,
                   COUNT(*) FILTER (WHERE f.kind = 1) AS pmp_count,
                   COUNT(*) FILTER (WHERE f.kind = 2) AS ttmp_count,
                   COUNT(*) FILTER (WHERE f.kind = 3) AS image_count,
                   COALESCE(SUM(f.size_bytes), 0)     AS total_size
            FROM mod_files f
            WHERE f.mod_id IN (SELECT id FROM target)
            GROUP BY f.mod_id
        )
        SELECT t.id AS Id, t.category_id AS CategoryId, t.cat_name AS CategoryName,
               t.root_id AS RootId,
               t.folder_name AS FolderName, t.display_name AS DisplayName,
               t.rating AS Rating,
               t.created_at AS CreatedAt, t.updated_at AS UpdatedAt,
               t.last_viewed_at AS LastViewedAt,
               t.folder_ctime AS FolderCreatedAt,
               t.folder_mtime AS FolderUpdatedAt,
               img.relative_path AS PrimaryImageRel,
               COALESCE(a.pmp_count, 0)  AS PmpCount,
               COALESCE(a.ttmp_count, 0) AS TtmpCount,
               COALESCE(a.image_count, 0) AS ImageCount,
               COALESCE(a.total_size, 0) AS TotalSizeBytes,
               pv.cache_path AS PmpPreviewCache,
               t.is_missing AS IsMissing,
               COALESCE(uu.display_name, uu.email) AS UpdatedByName,
               uu.color_hex AS UpdatedByColor
        FROM target t
        LEFT JOIN agg a ON a.mod_id = t.id
        LEFT JOIN LATERAL (
            SELECT f.relative_path
            FROM mod_files f
            WHERE f.mod_id = t.id AND f.kind = 3
            ORDER BY (CASE WHEN position('/' in f.relative_path) > 0 THEN 1 ELSE 0 END),
                     f.relative_path
            LIMIT 1
        ) img ON TRUE
        LEFT JOIN LATERAL (
            SELECT p.cache_path
            FROM pmp_previews p
            JOIN mod_files f ON f.id = p.mod_file_id
            WHERE f.mod_id = t.id
            LIMIT 1
        ) pv ON TRUE
        LEFT JOIN users uu ON uu.id = t.updated_by
        ORDER BY {OrderByFor(sort)};

        {TargetCte}
        SELECT mt.mod_id AS ModId, tg.id AS Id, tg.name AS Name, tg.color_hex AS ColorHex
        FROM mod_tags mt
        JOIN tags tg ON tg.id = mt.tag_id
        WHERE mt.mod_id IN (SELECT id FROM target)
        ORDER BY mt.mod_id, LOWER(tg.name)
        """;

    private static object ModsParams(ModQuery q) => new
    {
        r = q.RootId,
        cat = q.CategoryId,
        qLike = BuildSearchPattern(q.SearchText),
        minRating = q.MinRating,
        tagsAll = q.TagsAll.Distinct().ToArray(),
        tagsAny = q.TagsAny.Distinct().ToArray(),
        tagsNone = q.TagsNone.Distinct().ToArray(),
        onlyUntagged = q.OnlyUntagged
    };

    /// <summary>
    /// Wraps the term in wildcards, escaping the ones the user typed. Without this a mod
    /// actually named "foo_bar" cannot be searched for literally, because '_' is ILIKE's
    /// single-character wildcard.
    /// </summary>
    private static string? BuildSearchPattern(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return null;

        var escaped = searchText.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return "%" + escaped + "%";
    }

    /// <summary>Convenience overload for the call sites that do not filter by tag.</summary>
    public IReadOnlyList<ModCard> GetMods(
        long rootId,
        long? categoryId = null,
        string? searchText = null,
        ModSort sort = ModSort.CategoryThenName,
        int minRating = 0) =>
        GetMods(new ModQuery
        {
            RootId = rootId,
            CategoryId = categoryId,
            SearchText = searchText,
            Sort = sort,
            MinRating = minRating
        });

    public IReadOnlyList<ModCard> GetMods(ModQuery query)
    {
        using var conn = _store.Open();
        var rootPath = conn.QuerySingle<string>("SELECT path FROM roots WHERE id=@r", new { r = query.RootId });

        using var multi = conn.QueryMultiple(ModsSql(query.Sort), ModsParams(query));
        var rows = multi.Read<ModCardRow>().ToList();
        var tags = multi.Read<ModTagRow>().ToList();

        return Project(rows, tags, rootPath);
    }

    public async Task<IReadOnlyList<ModCard>> GetModsAsync(ModQuery query, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);

        var rootPath = await conn.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT path FROM roots WHERE id=@r", new { r = query.RootId }, cancellationToken: ct))
            .ConfigureAwait(false);

        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            ModsSql(query.Sort), ModsParams(query), cancellationToken: ct)).ConfigureAwait(false);

        var rows = (await multi.ReadAsync<ModCardRow>().ConfigureAwait(false)).ToList();
        var tags = (await multi.ReadAsync<ModTagRow>().ConfigureAwait(false)).ToList();

        return Project(rows, tags, rootPath);
    }

    private static List<ModCard> Project(List<ModCardRow> rows, List<ModTagRow> tagRows, string rootPath)
    {
        var tagsByMod = tagRows
            .GroupBy(t => t.ModId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ModTagRef>)g
                    .Select(t => new ModTagRef(t.Id, t.Name, t.ColorHex)).ToList());

        var cards = new List<ModCard>(rows.Count);
        foreach (var row in rows)
        {
            var modFolder = Path.Combine(rootPath, row.CategoryName, row.FolderName);
            string? abs = null;
            if (row.PrimaryImageRel is not null)
                abs = Path.Combine(modFolder, row.PrimaryImageRel.Replace('/', Path.DirectorySeparatorChar));
            else if (!string.IsNullOrEmpty(row.PmpPreviewCache) && File.Exists(row.PmpPreviewCache))
                abs = row.PmpPreviewCache;

            cards.Add(new ModCard
            {
                Id = row.Id,
                CategoryId = row.CategoryId,
                CategoryName = row.CategoryName,
                RootId = row.RootId,
                FolderName = row.FolderName,
                DisplayName = row.DisplayName,
                FolderAbsPath = modFolder,
                PrimaryImageAbsPath = abs,
                PmpCount = row.PmpCount,
                TtmpCount = row.TtmpCount,
                ImageCount = row.ImageCount,
                IsMissing = row.IsMissing,
                Rating = row.Rating,
                CreatedAt = string.IsNullOrEmpty(row.FolderCreatedAt) ? ParseDate(row.CreatedAt) : ParseDate(row.FolderCreatedAt),
                UpdatedAt = string.IsNullOrEmpty(row.FolderUpdatedAt) ? ParseDate(row.UpdatedAt) : ParseDate(row.FolderUpdatedAt),
                LastViewedAt = string.IsNullOrEmpty(row.LastViewedAt) ? null : ParseDate(row.LastViewedAt),
                TotalSizeBytes = row.TotalSizeBytes,
                UpdatedByName = row.UpdatedByName,
                UpdatedByColor = row.UpdatedByColor,
                Tags = tagsByMod.TryGetValue(row.Id, out var t) ? t : Array.Empty<ModTagRef>()
            });
        }
        return cards;
    }

    public IReadOnlyList<ModCard> GetDeletedMods()
    {
        using var conn = _store.Open();

        var rows = conn.Query<(long Id, long CategoryId, string CategoryName, long RootId, string RootPath,
                               string FolderName, string? DisplayName, int Rating,
                               string CreatedAt, string UpdatedAt, string? DeletedAt, long Size)>(
            """
            SELECT m.id AS Id, m.category_id AS CategoryId, c.name AS CategoryName,
                   c.root_id AS RootId, r.path AS RootPath,
                   m.folder_name AS FolderName, m.display_name AS DisplayName, m.rating AS Rating,
                   m.created_at AS CreatedAt, m.updated_at AS UpdatedAt, m.deleted_at AS DeletedAt,
                   (SELECT COALESCE(SUM(size_bytes),0) FROM mod_files WHERE mod_id=m.id) AS Size
            FROM mods m
            JOIN categories c ON c.id=m.category_id
            JOIN roots r ON r.id=c.root_id
            WHERE m.deleted_at IS NOT NULL
            ORDER BY m.deleted_at DESC
            """).ToList();

        var result = new List<ModCard>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new ModCard
            {
                Id = row.Id, CategoryId = row.CategoryId, CategoryName = row.CategoryName,
                RootId = row.RootId,
                FolderName = row.FolderName, DisplayName = row.DisplayName,
                FolderAbsPath = Path.Combine(row.RootPath, row.CategoryName, row.FolderName),
                Rating = row.Rating,
                CreatedAt = ParseDate(row.CreatedAt),
                UpdatedAt = ParseDate(row.UpdatedAt),
                DeletedAt = string.IsNullOrEmpty(row.DeletedAt) ? null : ParseDate(row.DeletedAt),
                TotalSizeBytes = row.Size
            });
        }
        return result;
    }

    public void SetRating(long modId, int rating)
    {
        if (rating < 0 || rating > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be 0..5");
        using var conn = _store.Open();
        conn.Execute(
            "UPDATE mods SET rating=@r, updated_at=@t WHERE id=@m",
            new { r = rating, t = DateTimeOffset.UtcNow.ToString("o"), m = modId });
    }

    public async Task SetRatingAsync(long modId, int rating, CancellationToken ct = default)
    {
        if (rating < 0 || rating > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be 0..5");
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE mods SET rating=@r, updated_at=@t WHERE id=@m",
            new { r = rating, t = DateTimeOffset.UtcNow.ToString("o"), m = modId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public void SetLastViewed(long modId)
    {
        using var conn = _store.Open();
        conn.Execute(
            "UPDATE mods SET last_viewed_at=@t WHERE id=@m",
            new { t = DateTimeOffset.UtcNow.ToString("o"), m = modId });
    }

    public async Task SetLastViewedAsync(long modId, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE mods SET last_viewed_at=@t WHERE id=@m",
            new { t = DateTimeOffset.UtcNow.ToString("o"), m = modId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public void RestoreDeleted(long modId)
    {
        using var conn = _store.Open();
        conn.Execute(
            "UPDATE mods SET deleted_at=NULL, is_missing=TRUE, updated_at=@t WHERE id=@m",
            new { m = modId, t = DateTimeOffset.UtcNow.ToString("o") });
    }

    public void HardDelete(long modId)
    {
        using var conn = _store.Open();
        conn.Execute("DELETE FROM mods WHERE id=@m AND deleted_at IS NOT NULL", new { m = modId });
    }

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.MinValue;

    private sealed class ModTagRow
    {
        public long ModId { get; set; }
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? ColorHex { get; set; }
    }

    private sealed class ModCardRow
    {
        public long Id { get; set; }
        public long CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        public long RootId { get; set; }
        public string FolderName { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? PrimaryImageRel { get; set; }
        public string? PmpPreviewCache { get; set; }
        public int PmpCount { get; set; }
        public int TtmpCount { get; set; }
        public int ImageCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public bool IsMissing { get; set; }
        public int Rating { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? FolderCreatedAt { get; set; }
        public string? FolderUpdatedAt { get; set; }
        public string? LastViewedAt { get; set; }
        public string? UpdatedByName { get; set; }
        public string? UpdatedByColor { get; set; }
    }

    public long EnsureRoot(string path, string displayName)
    {
        using var conn = _store.Open();
        var existing = conn.QuerySingleOrDefault<long?>(
            "SELECT id FROM roots WHERE path=@p", new { p = path });
        if (existing.HasValue) return existing.Value;

        return conn.ExecuteScalar<long>(
            """
            INSERT INTO roots(path, display_name, enabled, added_at)
            VALUES (@p, @n, TRUE, @t)
            RETURNING id
            """,
            new { p = path, n = displayName, t = DateTimeOffset.UtcNow.ToString("o") });
    }
}
