using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Health;

public sealed class HealthIssueRow
{
    public long ModId { get; set; }
    public string FolderName { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public int Kind { get; set; }
    public int Severity { get; set; }
    public string? Detail { get; set; }
}

public sealed class HealthChecker
{
    private readonly DatabaseStore _store;
    public HealthChecker(DatabaseStore store) => _store = store;

    /// <summary>
    /// Recomputes every screenshot issue in a single statement.
    ///
    /// The previous version queried mod_files once per mod and inserted one row per issue —
    /// roughly 600 round trips for a 300-mod library, which over a remote Postgres meant
    /// the window froze for the better part of a minute.
    /// </summary>
    private static string RunSql(bool byRoot) => $"""
        WITH target AS (
            SELECT m.id
            FROM mods m
            JOIN categories c ON c.id = m.category_id
            WHERE m.deleted_at IS NULL AND m.is_missing = FALSE
              {(byRoot ? "AND c.root_id = @r" : "")}
        ),
        cleared AS (
            DELETE FROM health_issues
            WHERE mod_id IN (
                SELECT m.id FROM mods m JOIN categories c ON c.id = m.category_id
                {(byRoot ? "WHERE c.root_id = @r" : "")}
            )
            RETURNING 1
        ),
        counts AS (
            SELECT t.id AS mod_id,
                   COUNT(f.id) FILTER (
                       WHERE f.kind = @imageKind AND position('/' in f.relative_path) = 0
                   ) AS top_images,
                   COUNT(f.id) FILTER (
                       WHERE f.kind = @pmpKind OR f.kind = @ttmpKind
                   ) AS mod_files,
                   -- The scanner hashes every archive it can open and stores NULL when the
                   -- read failed, so a missing hash is the cheapest available signal for
                   -- "this file is not usable". A zero-byte archive is the other one.
                   COUNT(f.id) FILTER (
                       WHERE (f.kind = @pmpKind OR f.kind = @ttmpKind)
                         AND (f.xxhash64 IS NULL OR f.size_bytes = 0)
                   ) AS broken_files
            FROM target t
            LEFT JOIN mod_files f ON f.mod_id = t.id
            GROUP BY t.id
        ),
        issues AS (
            SELECT mod_id, @noImageKind::int AS kind, @warn::int AS severity,
                   'Kein Vorschaubild im Mod-Ordner'::text AS detail
            FROM counts WHERE top_images = 0

            UNION ALL

            SELECT mod_id, @multiImageKind::int, @info::int,
                   top_images || ' Vorschaubilder'
            FROM counts WHERE top_images > 1

            UNION ALL

            SELECT mod_id, @orphanImageKind::int, @info::int,
                   'Bild ohne .pmp/.ttmp2'::text
            FROM counts WHERE top_images > 0 AND mod_files = 0

            UNION ALL

            SELECT mod_id, @brokenArchiveKind::int, @error::int,
                   broken_files || ' Archiv(e) nicht lesbar oder leer'
            FROM counts WHERE broken_files > 0
        )
        INSERT INTO health_issues(mod_id, kind, severity, detail)
        SELECT mod_id, kind, severity, detail FROM issues
        """;

    public int Run(long? rootId = null)
    {
        using var conn = _store.Open();
        return conn.Execute(RunSql(rootId.HasValue), Params(rootId));
    }

    public async Task<int> RunAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            RunSql(rootId.HasValue), Params(rootId), cancellationToken: ct)).ConfigureAwait(false);
    }

    private static object Params(long? rootId) => new
    {
        r = rootId,
        imageKind = (int)ModFileKind.Image,
        pmpKind = (int)ModFileKind.Pmp,
        ttmpKind = (int)ModFileKind.Ttmp2,
        noImageKind = (int)HealthIssueKind.NoImage,
        multiImageKind = (int)HealthIssueKind.MultiImage,
        orphanImageKind = (int)HealthIssueKind.OrphanImage,
        brokenArchiveKind = (int)HealthIssueKind.BrokenArchive,
        warn = (int)HealthSeverity.Warn,
        info = (int)HealthSeverity.Info,
        error = (int)HealthSeverity.Error
    };

    private static string IssuesSql(bool byRoot) => $"""
        SELECT h.mod_id AS ModId, m.folder_name AS FolderName, c.name AS CategoryName,
               h.kind AS Kind, h.severity AS Severity, h.detail AS Detail
        FROM health_issues h
        JOIN mods m ON m.id = h.mod_id
        JOIN categories c ON c.id = m.category_id
        {(byRoot ? "WHERE c.root_id = @r" : "")}
        ORDER BY h.severity DESC, c.name, m.folder_name
        """;

    public IReadOnlyList<HealthIssueRow> GetIssues(long? rootId = null)
    {
        using var conn = _store.Open();
        return conn.Query<HealthIssueRow>(IssuesSql(rootId.HasValue), new { r = rootId }).ToList();
    }

    public async Task<IReadOnlyList<HealthIssueRow>> GetIssuesAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<HealthIssueRow>(new CommandDefinition(
            IssuesSql(rootId.HasValue), new { r = rootId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }
}
