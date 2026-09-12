using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Links;
using ModOrganizer.Core.Pmp;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.Core.Queries;

public sealed class ModDetailSnapshot
{
    public long ModId { get; init; }
    public string FolderName { get; init; } = "";
    public string? DisplayName { get; init; }
    public string CategoryName { get; init; } = "";
    public string RootPath { get; init; } = "";
    public int Rating { get; init; }
    public string CommentMarkdown { get; init; } = "";

    public IReadOnlyList<ModDetailFile> Files { get; init; } = Array.Empty<ModDetailFile>();
    public IReadOnlyList<TagInfo> Tags { get; init; } = Array.Empty<TagInfo>();
    public IReadOnlyList<TagInfo> AllTags { get; init; } = Array.Empty<TagInfo>();
    public IReadOnlyList<LinkInfo> Links { get; init; } = Array.Empty<LinkInfo>();
    public IReadOnlyList<PmpInspector.PmpDetail> PmpDetails { get; init; } = Array.Empty<PmpInspector.PmpDetail>();
}

public sealed record ModDetailFile(string RelativePath, int Kind, long SizeBytes);

/// <summary>
/// Fetches everything the detail view needs in a single round trip.
///
/// Opening a mod previously issued roughly a dozen sequential queries — header, files,
/// comment, tags, all tags, links, link suggestions — plus four more per .pmp file from
/// PmpInspector.GetForMod. A mod with seven .pmp files cost ~35 round trips, which over a
/// remote database is well over a second of dead time on the UI thread.
/// </summary>
public sealed class ModDetailQuery
{
    private readonly DatabaseStore _store;
    private readonly IUserContext? _user;

    public ModDetailQuery(DatabaseStore store, IUserContext? user = null)
    {
        _store = store;
        _user = user;
    }

    private const string Sql = """
        SELECT m.folder_name AS FolderName, m.display_name AS DisplayName,
               c.name AS CategoryName, mo_root_path(c.root_id, @uid) AS RootPath, m.rating AS Rating,
               COALESCE(m.comment_md, '') AS CommentMarkdown
        FROM mods m
        JOIN categories c ON c.id = m.category_id
        WHERE m.id = @m;

        SELECT relative_path AS RelativePath, kind AS Kind, size_bytes AS SizeBytes
        FROM mod_files WHERE mod_id = @m ORDER BY relative_path;

        SELECT t.id AS Id, t.name AS Name, t.color_hex AS ColorHex,
               t.description AS Description, 0 AS UsageCount
        FROM tags t JOIN mod_tags mt ON mt.tag_id = t.id
        WHERE mt.mod_id = @m
        ORDER BY LOWER(t.name);

        SELECT t.id AS Id, t.name AS Name, t.color_hex AS ColorHex, t.description AS Description,
               COALESCE(u.cnt, 0) AS UsageCount
        FROM tags t
        LEFT JOIN (SELECT tag_id, COUNT(*) AS cnt FROM mod_tags GROUP BY tag_id) u
               ON u.tag_id = t.id
        ORDER BY LOWER(t.name);

        SELECT id AS Id, mod_id AS ModId, url AS Url, title AS Title,
               domain AS Domain, kind AS Kind
        FROM mod_links WHERE mod_id = @m ORDER BY added_at;

        SELECT f.id AS FileId, f.relative_path AS RelativePath,
               pm.name AS Name, pm.author AS Author, pm.version AS Version,
               pm.description AS Description, pm.website AS Website,
               pv.cache_path AS PreviewCachePath
        FROM mod_files f
        LEFT JOIN pmp_meta pm ON pm.mod_file_id = f.id
        LEFT JOIN pmp_previews pv ON pv.mod_file_id = f.id
        WHERE f.mod_id = @m AND f.kind = 1
        ORDER BY f.relative_path;

        SELECT g.mod_file_id AS FileId, g.name AS Name, g.type AS Type, g.option_json AS OptionJson
        FROM pmp_groups g
        JOIN mod_files f ON f.id = g.mod_file_id
        WHERE f.mod_id = @m
        ORDER BY g.mod_file_id, g.id;

        SELECT p.mod_file_id AS FileId, p.game_path AS GamePath
        FROM pmp_game_paths p
        JOIN mod_files f ON f.id = p.mod_file_id
        WHERE f.mod_id = @m
        ORDER BY p.mod_file_id, p.game_path;
        """;

    public async Task<ModDetailSnapshot?> LoadAsync(long modId, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(Sql, new { m = modId, uid = _user?.UserId }, cancellationToken: ct))
            .ConfigureAwait(false);

        var header = (await multi.ReadAsync<HeaderRow>().ConfigureAwait(false)).SingleOrDefault();
        if (header is null) return null;

        var files = (await multi.ReadAsync<ModDetailFile>().ConfigureAwait(false)).ToList();
        var tags = (await multi.ReadAsync<TagInfo>().ConfigureAwait(false)).ToList();
        var allTags = (await multi.ReadAsync<TagInfo>().ConfigureAwait(false)).ToList();
        var links = (await multi.ReadAsync<LinkInfo>().ConfigureAwait(false)).ToList();
        var pmpFiles = (await multi.ReadAsync<PmpFileRow>().ConfigureAwait(false)).ToList();
        var groups = (await multi.ReadAsync<GroupRow>().ConfigureAwait(false)).ToList();
        var paths = (await multi.ReadAsync<PathRow>().ConfigureAwait(false)).ToList();

        var groupsByFile = groups
            .GroupBy(g => g.FileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PmpGroupEntry>)g
                .Select(x => new PmpGroupEntry(x.Name, x.Type, x.OptionJson)).ToList());

        var pathsByFile = paths
            .GroupBy(p => p.FileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.GamePath).ToList());

        var pmpDetails = pmpFiles.Select(f => new PmpInspector.PmpDetail(
            f.FileId, f.RelativePath,
            f.Name, f.Author, f.Version, f.Description, f.Website,
            groupsByFile.TryGetValue(f.FileId, out var g) ? g : Array.Empty<PmpGroupEntry>(),
            pathsByFile.TryGetValue(f.FileId, out var p) ? p : Array.Empty<string>(),
            f.PreviewCachePath)).ToList();

        return new ModDetailSnapshot
        {
            ModId = modId,
            FolderName = header.FolderName,
            DisplayName = header.DisplayName,
            CategoryName = header.CategoryName,
            RootPath = header.RootPath,
            Rating = header.Rating,
            CommentMarkdown = header.CommentMarkdown,
            Files = files,
            Tags = tags,
            AllTags = allTags,
            Links = links,
            PmpDetails = pmpDetails
        };
    }

    private sealed class HeaderRow
    {
        public string FolderName { get; set; } = "";
        public string? DisplayName { get; set; }
        public string CategoryName { get; set; } = "";
        public string RootPath { get; set; } = "";
        public int Rating { get; set; }
        public string CommentMarkdown { get; set; } = "";
    }

    private sealed class PmpFileRow
    {
        public long FileId { get; set; }
        public string RelativePath { get; set; } = "";
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Website { get; set; }
        public string? PreviewCachePath { get; set; }
    }

    private sealed class GroupRow
    {
        public long FileId { get; set; }
        public string Name { get; set; } = "";
        public string? Type { get; set; }
        public string OptionJson { get; set; } = "";
    }

    private sealed class PathRow
    {
        public long FileId { get; set; }
        public string GamePath { get; set; } = "";
    }
}
