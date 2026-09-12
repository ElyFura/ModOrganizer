using System.Text.RegularExpressions;
using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Duplicates;

public sealed class DuplicateModRow
{
    public long ModId { get; set; }
    public long RootId { get; set; }
    public long CategoryId { get; set; }
    public string FolderName { get; set; } = "";
    public string CategoryName { get; set; } = "";

    /// <summary>Root path, needed to compose the absolute folder.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>Absolute folder on disk, so the view can offer "show in Explorer".</summary>
    public string FolderAbsPath { get; set; } = "";

    public long TotalSizeBytes { get; set; }
    public int Rating { get; set; }
    public int FileCount { get; set; }

    /// <summary>Folder mtime, for telling an old copy from a new one.</summary>
    public string? FolderMtime { get; set; }
}

public sealed class DuplicateGroup
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required List<DuplicateModRow> Mods { get; init; }
}

public sealed class DuplicateFinder
{
    private static readonly Regex VersionSuffix =
        new(@"\s*(\[[^\]]+\]|\(v?\d+(\.\d+)*\)|v\d+(\.\d+)*|dt|update\s*\d*|bibo\+?|tbse|rue|gen\s*\d+)\s*$",
            RegexOptions.IgnoreCase);

    private readonly DatabaseStore _store;
    private readonly IUserContext? _user;

    public DuplicateFinder(DatabaseStore store, IUserContext? user = null)
    {
        _store = store;
        _user = user;
    }

    public IReadOnlyList<DuplicateGroup> FindAll(long? rootId = null)
    {
        var result = new List<DuplicateGroup>();
        result.AddRange(FindByName(rootId));
        result.AddRange(FindByHash(rootId));
        return result;
    }

    /// <summary>
    /// Every live mod with the detail the duplicates view needs to compare candidates.
    /// One query for both the name and the hash pass.
    /// </summary>
    private const string CandidateSql =
        """
        SELECT m.id AS ModId, c.root_id AS RootId, m.category_id AS CategoryId,
               m.folder_name AS FolderName, c.name AS CategoryName,
               mo_root_path(c.root_id, @uid) AS RootPath, m.rating AS Rating, m.folder_mtime AS FolderMtime,
               COALESCE(f.total, 0) AS TotalSizeBytes,
               COALESCE(f.cnt, 0) AS FileCount
        FROM mods m
        JOIN categories c ON c.id = m.category_id
        LEFT JOIN (
            SELECT mod_id, SUM(size_bytes) AS total, COUNT(*) AS cnt
            FROM mod_files GROUP BY mod_id
        ) f ON f.mod_id = m.id
        WHERE m.deleted_at IS NULL AND m.is_missing = FALSE
          AND (@r::bigint IS NULL OR c.root_id = @r::bigint)
        """;

    private List<DuplicateModRow> LoadCandidates(long? rootId)
    {
        using var conn = _store.Open();
        var rows = conn.Query<DuplicateModRow>(CandidateSql,
            new { r = rootId, uid = _user?.UserId }).ToList();

        foreach (var row in rows)
            row.FolderAbsPath = Path.Combine(row.RootPath, row.CategoryName, row.FolderName);

        return rows;
    }

    public IReadOnlyList<DuplicateGroup> FindByName(long? rootId = null)
    {
        return LoadCandidates(rootId)
            .GroupBy(m => Normalize(m.FolderName))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                Key = g.Key,
                Kind = "name",
                Mods = g.OrderBy(m => m.CategoryName).ThenBy(m => m.FolderName).ToList()
            })
            .OrderBy(g => g.Key)
            .ToList();
    }

    public IReadOnlyList<DuplicateGroup> FindByHash(long? rootId = null)
    {
        using var conn = _store.Open();

        // Mods sharing an identical .pmp/.ttmp2 payload.
        var pairs = conn.Query<(long Hash, long ModId)>(
            """
            SELECT f.xxhash64 AS Hash, f.mod_id AS ModId
            FROM mod_files f
            JOIN mods m ON m.id = f.mod_id
            JOIN categories c ON c.id = m.category_id
            WHERE f.xxhash64 IS NOT NULL
              AND m.deleted_at IS NULL AND m.is_missing = FALSE
              AND (@r::bigint IS NULL OR c.root_id = @r::bigint)
            """, new { r = rootId }).ToList();

        var byId = LoadCandidates(rootId).ToDictionary(m => m.ModId);

        return pairs
            .GroupBy(p => p.Hash)
            .Select(g => new
            {
                Hash = g.Key,
                ModIds = g.Select(p => p.ModId).Distinct().ToList()
            })
            .Where(g => g.ModIds.Count > 1)
            .Select(g => new DuplicateGroup
            {
                Key = g.Hash.ToString("X"),
                Kind = "hash",
                Mods = g.ModIds
                    .Where(byId.ContainsKey)
                    .Select(id => byId[id])
                    .OrderBy(m => m.CategoryName).ThenBy(m => m.FolderName)
                    .ToList()
            })
            .Where(g => g.Mods.Count > 1)
            .ToList();
    }

    public static string Normalize(string name)
    {
        var n = name.ToLowerInvariant().Trim();
        while (true)
        {
            var m = VersionSuffix.Match(n);
            if (!m.Success || m.Index == 0) break;
            n = n.Substring(0, m.Index).Trim();
        }
        return Regex.Replace(n, @"[\s\-_]+", "");
    }
}
