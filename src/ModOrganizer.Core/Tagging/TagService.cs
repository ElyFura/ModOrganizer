using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Tagging;

public sealed class TagInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? ColorHex { get; set; }
    public string? Description { get; set; }
    public int UsageCount { get; set; }
}

public sealed class TagService
{
    private readonly DatabaseStore _store;
    public TagService(DatabaseStore store) => _store = store;

    /// <summary>
    /// Default chip colours, handed out round-robin by name hash so a new tag is never
    /// invisible. The user can override any of them in the tag manager.
    /// </summary>
    private static readonly string[] Palette =
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5"
    };

    public static string DefaultColorFor(string name)
    {
        unchecked
        {
            var hash = 0;
            foreach (var c in name.ToLowerInvariant()) hash = hash * 31 + c;
            return Palette[Math.Abs(hash) % Palette.Length];
        }
    }

    private const string AllTagsSql =
        """
        SELECT t.id AS Id, t.name AS Name, t.color_hex AS ColorHex, t.description AS Description,
               COALESCE(u.cnt, 0) AS UsageCount
        FROM tags t
        LEFT JOIN (SELECT tag_id, COUNT(*) AS cnt FROM mod_tags GROUP BY tag_id) u
               ON u.tag_id = t.id
        ORDER BY LOWER(t.name)
        """;

    public IReadOnlyList<TagInfo> GetAllTags()
    {
        using var conn = _store.Open();
        return conn.Query<TagInfo>(AllTagsSql).ToList();
    }

    public async Task<IReadOnlyList<TagInfo>> GetAllTagsAsync(CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TagInfo>(
            new CommandDefinition(AllTagsSql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public IReadOnlyList<TagInfo> GetTagsForMod(long modId)
    {
        using var conn = _store.Open();
        return conn.Query<TagInfo>(
            """
            SELECT t.id AS Id, t.name AS Name, t.color_hex AS ColorHex, t.description AS Description, 0 AS UsageCount
            FROM tags t JOIN mod_tags mt ON mt.tag_id=t.id
            WHERE mt.mod_id=@m
            ORDER BY LOWER(t.name)
            """, new { m = modId }).ToList();
    }

    public long CreateTag(string name, string? colorHex = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));
        name = name.Trim();

        using var conn = _store.Open();

        // Idempotent by design: typing an existing name in the detail view should attach
        // that tag, not fail on the unique index.
        return conn.ExecuteScalar<long>(
            """
            INSERT INTO tags(name, color_hex, description)
            VALUES (@n, COALESCE(@c, @fallback), @d)
            ON CONFLICT (LOWER(name)) DO UPDATE SET name = tags.name
            RETURNING id
            """,
            new { n = name, c = colorHex, fallback = DefaultColorFor(name), d = description });
    }

    public void RenameTag(long tagId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Name cannot be empty.", nameof(newName));
        newName = newName.Trim();

        using var conn = _store.Open();

        // Report the collision instead of letting the unique index surface as a raw
        // Postgres error.
        var clash = conn.QuerySingleOrDefault<long?>(
            "SELECT id FROM tags WHERE LOWER(name)=LOWER(@n) AND id<>@id",
            new { n = newName, id = tagId });
        if (clash.HasValue)
            throw new InvalidOperationException($"A tag named '{newName}' already exists.");

        conn.Execute("UPDATE tags SET name=@n WHERE id=@id", new { n = newName, id = tagId });
    }

    public void SetColor(long tagId, string? colorHex)
    {
        using var conn = _store.Open();
        conn.Execute("UPDATE tags SET color_hex=@c WHERE id=@id", new { c = colorHex, id = tagId });
    }

    public void SetDescription(long tagId, string? description)
    {
        using var conn = _store.Open();
        conn.Execute("UPDATE tags SET description=@d WHERE id=@id",
            new { d = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), id = tagId });
    }

    /// <summary>
    /// Removes the tag and every assignment of it. Mods are untouched — mod_tags cascades
    /// from tags, so only the associations go.
    /// </summary>
    public void DeleteTag(long tagId)
    {
        using var conn = _store.Open();
        conn.Execute("DELETE FROM tags WHERE id=@id", new { id = tagId });
    }

    /// <summary>
    /// Assigns a tag to many mods in one round trip. ON CONFLICT matters: without it,
    /// bulk-tagging a selection where even one mod already carried the tag threw on the
    /// (mod_id, tag_id) primary key and lost the whole batch.
    /// </summary>
    public int AddTagToMods(long tagId, IEnumerable<long> modIds)
    {
        var ids = modIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;

        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();

        var added = conn.Execute(
            """
            INSERT INTO mod_tags(mod_id, tag_id, added_at, added_by)
            SELECT m, @t, @ts, @u
            FROM unnest(@ids::bigint[]) AS m
            ON CONFLICT (mod_id, tag_id) DO NOTHING
            """,
            new
            {
                ids,
                t = tagId,
                ts = DateTimeOffset.UtcNow.ToString("o"),
                u = Management.ActionLog.CurrentUser?.UserId
            }, tx);

        Management.ActionLog.RecordMany(conn, tx, ActionKind.TagAdd,
            Guid.NewGuid().ToString("N"), ids, payloadJson: $"{{\"tag_id\":{tagId}}}");

        tx.Commit();
        return added;
    }

    public int RemoveTagFromMods(long tagId, IEnumerable<long> modIds)
    {
        var ids = modIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;

        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();

        var removed = conn.Execute(
            "DELETE FROM mod_tags WHERE tag_id=@t AND mod_id = ANY(@ids::bigint[])",
            new { t = tagId, ids }, tx);

        Management.ActionLog.RecordMany(conn, tx, ActionKind.TagRemove,
            Guid.NewGuid().ToString("N"), ids, payloadJson: $"{{\"tag_id\":{tagId}}}");

        tx.Commit();
        return removed;
    }

    public IReadOnlyList<long> GetModsWithAllTags(IEnumerable<long> tagIds)
    {
        var ids = tagIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<long>();
        using var conn = _store.Open();
        return conn.Query<long>(
            """
            SELECT mod_id FROM mod_tags
            WHERE tag_id = ANY(@ids::bigint[])
            GROUP BY mod_id HAVING COUNT(DISTINCT tag_id) = cardinality(@ids::bigint[])
            """, new { ids }).ToList();
    }

    public IReadOnlyList<long> GetModsWithAnyTags(IEnumerable<long> tagIds)
    {
        var ids = tagIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<long>();
        using var conn = _store.Open();
        return conn.Query<long>(
            "SELECT DISTINCT mod_id FROM mod_tags WHERE tag_id = ANY(@ids::bigint[])",
            new { ids }).ToList();
    }

    private static readonly (string Keyword, string Tag)[] Heuristics =
    {
        ("Bibo+", "Bibo+"),
        ("TBSE",  "TBSE"),
        ("Rue",   "Rue"),
        ("TreYab","TreYab"),
        ("Lavabod","Lavabod"),
        ("YAB",   "YAB"),
        ("Muse",  "Muse"),
        ("Gen3",  "Gen3"),
    };

    public IReadOnlyList<string> SuggestTagsForMod(string modName, string? categoryName)
    {
        var result = new List<string>();
        foreach (var (kw, tag) in Heuristics)
        {
            if (modName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                result.Add(tag);
        }
        if (!string.IsNullOrEmpty(categoryName))
            result.Add(categoryName);
        return result.Distinct().ToList();
    }
}
