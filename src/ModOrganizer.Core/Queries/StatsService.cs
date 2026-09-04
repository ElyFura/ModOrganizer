using Dapper;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Queries;

public sealed class CategoryStat
{
    public string Category { get; set; } = "";
    public int Count { get; set; }
    public long Size { get; set; }
}

public sealed class ModSizeStat
{
    public long ModId { get; set; }
    public string FolderName { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public long Size { get; set; }
}

public sealed class MonthStat
{
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

public sealed class TagStat
{
    public string TagName { get; set; } = "";
    public int Count { get; set; }
}

public sealed class FileKindStat
{
    public int Kind { get; set; }
    public int Count { get; set; }
    public long Size { get; set; }
}

public sealed class LibraryStats
{
    public int TotalMods { get; init; }
    public long TotalSizeBytes { get; init; }
    public int TotalFiles { get; init; }
    public int AvgRating { get; init; }
    public IReadOnlyList<CategoryStat> ByCategory { get; init; } = Array.Empty<CategoryStat>();
    public IReadOnlyList<ModSizeStat> TopBySize { get; init; } = Array.Empty<ModSizeStat>();
    public IReadOnlyList<MonthStat> AddedByMonth { get; init; } = Array.Empty<MonthStat>();
    public IReadOnlyList<TagStat> TopTags { get; init; } = Array.Empty<TagStat>();
    public IReadOnlyList<FileKindStat> ByFileKind { get; init; } = Array.Empty<FileKindStat>();
}

public sealed class StatsService
{
    private readonly DatabaseStore _store;
    public StatsService(DatabaseStore store) => _store = store;

    public LibraryStats Compute(long? rootId = null)
    {
        using var conn = _store.Open();

        var rootFilter = rootId.HasValue ? "AND c.root_id=@r" : "";
        var p = new { r = rootId };

        var totalMods = conn.ExecuteScalar<int>(
            $"""
            SELECT COUNT(*) FROM mods m JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            """, p);

        var totalFiles = conn.ExecuteScalar<int>(
            $"""
            SELECT COUNT(*) FROM mod_files f
            JOIN mods m ON m.id=f.mod_id JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            """, p);

        var totalSize = conn.ExecuteScalar<long?>(
            $"""
            SELECT COALESCE(SUM(f.size_bytes), 0) FROM mod_files f
            JOIN mods m ON m.id=f.mod_id JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            """, p) ?? 0;

        var avgRating = conn.ExecuteScalar<double?>(
            $"""
            SELECT AVG(m.rating) FROM mods m JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE AND m.rating>0 {rootFilter}
            """, p) ?? 0;

        var byCategory = conn.Query<CategoryStat>(
            $"""
            SELECT c.name AS Category, COUNT(DISTINCT m.id) AS Count,
                   COALESCE(SUM(f.size_bytes),0) AS Size
            FROM categories c
            LEFT JOIN mods m ON m.category_id=c.id AND m.deleted_at IS NULL AND m.is_missing=FALSE
            LEFT JOIN mod_files f ON f.mod_id=m.id
            WHERE 1=1 {(rootId.HasValue ? "AND c.root_id=@r" : "")}
            GROUP BY c.id, c.name
            HAVING COUNT(m.id) > 0
            ORDER BY Count DESC
            """, p).ToList();

        var topBySize = conn.Query<ModSizeStat>(
            $"""
            SELECT m.id AS ModId, m.folder_name AS FolderName, c.name AS CategoryName,
                   COALESCE(SUM(f.size_bytes),0) AS Size
            FROM mods m JOIN categories c ON c.id=m.category_id
            LEFT JOIN mod_files f ON f.mod_id=m.id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            GROUP BY m.id, m.folder_name, c.name
            ORDER BY Size DESC
            LIMIT 10
            """, p).ToList();

        var addedByMonth = conn.Query<MonthStat>(
            $"""
            SELECT substr(m.created_at, 1, 7) AS Month, COUNT(*) AS Count
            FROM mods m JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL {rootFilter}
            GROUP BY substr(m.created_at, 1, 7) ORDER BY substr(m.created_at, 1, 7)
            """, p).ToList();

        var topTags = conn.Query<TagStat>(
            $"""
            SELECT t.name AS TagName, COUNT(mt.mod_id) AS Count
            FROM tags t
            JOIN mod_tags mt ON mt.tag_id=t.id
            JOIN mods m ON m.id=mt.mod_id
            JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            GROUP BY t.id, t.name ORDER BY Count DESC LIMIT 10
            """, p).ToList();

        var byFileKind = conn.Query<FileKindStat>(
            $"""
            SELECT f.kind AS Kind, COUNT(*) AS Count, COALESCE(SUM(f.size_bytes),0) AS Size
            FROM mod_files f
            JOIN mods m ON m.id=f.mod_id JOIN categories c ON c.id=m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing=FALSE {rootFilter}
            GROUP BY f.kind ORDER BY Size DESC
            """, p).ToList();

        return new LibraryStats
        {
            TotalMods = totalMods,
            TotalFiles = totalFiles,
            TotalSizeBytes = totalSize,
            AvgRating = (int)Math.Round(avgRating),
            ByCategory = byCategory,
            TopBySize = topBySize,
            AddedByMonth = addedByMonth,
            TopTags = topTags,
            ByFileKind = byFileKind
        };
    }
}
