using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Queries;

/// <summary>A root as it looks to one particular user.</summary>
public sealed class RootMapping
{
    public long Id { get; set; }

    /// <summary>Logical name of the library, shared by all users ("Dawntrail").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Where THIS user has it mounted; null when they have not mapped it yet.</summary>
    public string? Path { get; set; }

    /// <summary>The path the root was originally created with, used as an adoption hint.</summary>
    public string OriginalPath { get; set; } = "";

    /// <summary>This user's own on/off switch for the root.</summary>
    public bool Enabled { get; set; }

    public int CategoryCount { get; set; }
    public int ModCount { get; set; }

    /// <summary>How many users have mapped this root; tells a shared root from a private one.</summary>
    public int MappedUserCount { get; set; }

    public bool IsMapped => !string.IsNullOrWhiteSpace(Path);

    /// <summary>Mapped and actually present on this machine right now.</summary>
    public bool PathExists => IsMapped && Directory.Exists(Path!);

    /// <summary>Usable for scanning and browsing.</summary>
    public bool IsUsable => Enabled && PathExists;

    public string StatusText =>
        !IsMapped ? "nicht zugeordnet"
        : !PathExists ? "Ordner fehlt"
        : !Enabled ? "deaktiviert"
        : "bereit";
}

/// <summary>
/// Owns the mapping between a logical root and the folder each user has it mounted at.
///
/// Before this existed a root was one absolute path for everyone, so a second user on a
/// different PC inherited the first user's drive letters and every scan failed. The mods,
/// categories, tags and comments are deliberately still shared — only the mount point is
/// per user.
/// </summary>
public sealed class RootService
{
    private readonly DatabaseStore _store;
    private readonly IUserContext? _user;

    public RootService(DatabaseStore store, IUserContext? user = null)
    {
        _store = store;
        _user = user;
    }

    private Guid? UserId => _user?.UserId;

    private const string MappingSql =
        """
        SELECT r.id AS Id,
               r.display_name AS DisplayName,
               rp.path AS Path,
               r.path AS OriginalPath,
               COALESCE(rp.enabled, TRUE) AS Enabled,
               (SELECT COUNT(*) FROM categories c WHERE c.root_id = r.id) AS CategoryCount,
               (SELECT COUNT(*) FROM mods m
                  JOIN categories c ON c.id = m.category_id
                 WHERE c.root_id = r.id AND m.deleted_at IS NULL) AS ModCount,
               (SELECT COUNT(*) FROM root_paths x WHERE x.root_id = r.id) AS MappedUserCount
        FROM roots r
        LEFT JOIN root_paths rp ON rp.root_id = r.id AND rp.user_id = @uid
        ORDER BY r.added_at, r.id
        """;

    /// <summary>Every root in the library, annotated with this user's mapping.</summary>
    public IReadOnlyList<RootMapping> GetAll()
    {
        using var conn = _store.Open();
        var rows = conn.Query<RootMapping>(MappingSql, new { uid = UserId }).ToList();
        ApplyOfflineFallback(rows);
        return rows;
    }

    public async Task<IReadOnlyList<RootMapping>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _store.OpenAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<RootMapping>(
            new CommandDefinition(MappingSql, new { uid = UserId }, cancellationToken: ct))
            .ConfigureAwait(false)).ToList();
        ApplyOfflineFallback(rows);
        return rows;
    }

    /// <summary>
    /// Without a signed-in user there is nobody to map roots for, so the app behaves like
    /// the single-user version it used to be and uses each root's original path.
    /// </summary>
    private void ApplyOfflineFallback(List<RootMapping> rows)
    {
        if (UserId is not null) return;
        foreach (var r in rows) r.Path ??= r.OriginalPath;
    }

    /// <summary>Only the roots this user can actually work with.</summary>
    public IReadOnlyList<RootMapping> GetUsable() =>
        GetAll().Where(r => r.IsUsable).ToList();

    public async Task<IReadOnlyList<RootMapping>> GetUsableAsync(CancellationToken ct = default) =>
        (await GetAllAsync(ct).ConfigureAwait(false)).Where(r => r.IsUsable).ToList();

    /// <summary>Points a root at a folder on this machine for the current user.</summary>
    public void SetPath(long rootId, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Pfad darf nicht leer sein.", nameof(path));

        path = NormalizePath(path);

        if (UserId is not Guid uid)
        {
            // Offline: there is no per-user row to write, so update the root itself.
            using var offlineConn = _store.Open();
            offlineConn.Execute("UPDATE roots SET path=@p WHERE id=@r", new { p = path, r = rootId });
            return;
        }

        using var conn = _store.Open();
        conn.Execute(
            """
            INSERT INTO root_paths (root_id, user_id, path, enabled)
            VALUES (@r, @u, @p, TRUE)
            ON CONFLICT (root_id, user_id) DO UPDATE
                SET path = EXCLUDED.path, enabled = TRUE
            """,
            new { r = rootId, u = uid, p = path });
    }

    /// <summary>This user's own enable switch; does not affect anyone else.</summary>
    public void SetEnabled(long rootId, bool enabled)
    {
        if (UserId is not Guid uid)
        {
            using var offlineConn = _store.Open();
            offlineConn.Execute("UPDATE roots SET enabled=@e WHERE id=@r", new { e = enabled, r = rootId });
            return;
        }

        using var conn = _store.Open();
        conn.Execute(
            "UPDATE root_paths SET enabled=@e WHERE root_id=@r AND user_id=@u",
            new { e = enabled, r = rootId, u = uid });
    }

    /// <summary>
    /// Forgets this user's mapping. The root and its mods stay — this is the "not my
    /// library" action, as opposed to deleting it for everyone.
    /// </summary>
    public void RemoveMapping(long rootId)
    {
        if (UserId is not Guid uid) return;
        using var conn = _store.Open();
        conn.Execute("DELETE FROM root_paths WHERE root_id=@r AND user_id=@u",
            new { r = rootId, u = uid });
    }

    /// <summary>
    /// Deletes the root for everyone, including its categories and mods (by cascade).
    /// Nothing on disk is touched.
    /// </summary>
    public void DeleteRoot(long rootId)
    {
        using var conn = _store.Open();
        conn.Execute("DELETE FROM roots WHERE id=@r", new { r = rootId });
    }

    /// <summary>
    /// Creates a logical root and maps it to this user's folder in one step. If a root
    /// with the same original path already exists it is reused, so adding the same folder
    /// twice does not fork the library.
    /// </summary>
    public long AddRoot(string path, string displayName)
    {
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = path;

        long rootId;
        using (var conn = _store.Open())
        {
            var existing = conn.QuerySingleOrDefault<long?>(
                "SELECT id FROM roots WHERE path=@p", new { p = path });

            rootId = existing ?? conn.ExecuteScalar<long>(
                """
                INSERT INTO roots(path, display_name, enabled, added_at)
                VALUES (@p, @n, TRUE, @t)
                RETURNING id
                """,
                new { p = path, n = displayName, t = DateTimeOffset.UtcNow.ToString("o") });
        }

        SetPath(rootId, path);
        return rootId;
    }

    /// <summary>Renames the logical library. Shared, so everyone sees the new name.</summary>
    public void Rename(long rootId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Name darf nicht leer sein.", nameof(displayName));

        using var conn = _store.Open();
        conn.Execute("UPDATE roots SET display_name=@n WHERE id=@r",
            new { n = displayName.Trim(), r = rootId });
    }

    /// <summary>
    /// Adopts roots whose original path happens to exist on this machine. That silently
    /// carries the user who created them over to the new per-user model, without ever
    /// handing someone else's drive letters to a second user.
    /// </summary>
    public int AdoptLocalRoots()
    {
        if (UserId is not Guid uid) return 0;

        using var conn = _store.Open();
        var candidates = conn.Query<(long Id, string Path)>(
            """
            SELECT r.id AS Id, r.path AS Path
            FROM roots r
            LEFT JOIN root_paths rp ON rp.root_id = r.id AND rp.user_id = @u
            WHERE rp.root_id IS NULL
            """, new { u = uid }).ToList();

        var adopted = 0;
        foreach (var (id, path) in candidates)
        {
            if (!Directory.Exists(path)) continue;
            conn.Execute(
                """
                INSERT INTO root_paths (root_id, user_id, path, enabled)
                VALUES (@r, @u, @p, TRUE)
                ON CONFLICT (root_id, user_id) DO NOTHING
                """,
                new { r = id, u = uid, p = path });
            adopted++;
        }
        return adopted;
    }

    /// <summary>
    /// What <see cref="ProposeMappingsUnder"/> worked out for one library.
    /// <paramref name="ResolvedPath"/> is null when nothing matching was found.
    /// </summary>
    public sealed record RootMapProposal(
        long RootId, string DisplayName, string ReferencePath, string? ResolvedPath, bool AlreadyMapped);

    /// <summary>
    /// Works out where each library sits under a base folder the user picked.
    ///
    /// Two people syncing the same Nextcloud folder end up with the same tree under a
    /// different anchor — "E:\FFXIV\…" here, "D:\FFXIV Cloud Mod Ordner\…" there. So for
    /// each library we take its reference path and try progressively shorter tails of it
    /// under the chosen base, longest first because that is the most specific match:
    ///
    ///   reference  E:\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail
    ///   base       D:\FFXIV Cloud Mod Ordner
    ///   tries      …\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail   (no)
    ///              …\FFXIV\FF14 Mods\Mods\Dawntrail         (yes)
    ///
    /// Nothing is written — the caller shows the result and then applies it.
    /// </summary>
    public IReadOnlyList<RootMapProposal> ProposeMappingsUnder(string basePath)
    {
        basePath = NormalizePath(basePath);

        var proposals = new List<RootMapProposal>();
        foreach (var root in GetAll())
        {
            var reference = string.IsNullOrWhiteSpace(root.Path) ? root.OriginalPath : root.Path!;
            proposals.Add(new RootMapProposal(
                root.Id, root.DisplayName, reference,
                ResolveUnder(basePath, reference), root.IsMapped));
        }
        return proposals;
    }

    /// <summary>Longest matching tail of <paramref name="reference"/> that exists under the base.</summary>
    public static string? ResolveUnder(string basePath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        // The base folder itself is a valid answer when the library IS the sync root.
        var segments = reference
            .Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0 && !s.EndsWith(':'))
            .ToArray();

        for (var take = segments.Length; take >= 1; take--)
        {
            var candidate = System.IO.Path.Combine(
                new[] { basePath }.Concat(segments.Skip(segments.Length - take)).ToArray());
            if (Directory.Exists(candidate)) return candidate;
        }

        // The base folder itself is the answer when it IS that library, i.e. when the
        // reference ends in the same folder name the user just picked.
        if (segments.Length > 0 && Directory.Exists(basePath))
        {
            var baseName = new DirectoryInfo(basePath).Name;
            if (string.Equals(baseName, segments[^1], StringComparison.OrdinalIgnoreCase))
                return basePath;
        }

        return null;
    }

    /// <summary>
    /// Two libraries can resolve to the same folder - usually a leftover duplicate root.
    /// Mapping both would list the same mods twice, so only one wins and the caller shows
    /// the rest to the user rather than writing them silently.
    /// </summary>
    public static (IReadOnlyList<RootMapProposal> Apply, IReadOnlyList<RootMapProposal> Conflicts)
        SplitConflicts(IEnumerable<RootMapProposal> proposals)
    {
        var apply = new List<RootMapProposal>();
        var conflicts = new List<RootMapProposal>();

        foreach (var group in proposals
                     .Where(p => p.ResolvedPath is not null)
                     .GroupBy(p => NormalizePath(p.ResolvedPath!), StringComparer.OrdinalIgnoreCase))
        {
            // An existing mapping wins over a new one; otherwise the older library does.
            var ordered = group.OrderByDescending(p => p.AlreadyMapped).ThenBy(p => p.RootId).ToList();
            apply.Add(ordered[0]);
            conflicts.AddRange(ordered.Skip(1));
        }

        return (apply, conflicts);
    }

    /// <summary>Writes the proposals that actually resolved. Returns how many were applied.</summary>
    public int ApplyMappings(IEnumerable<RootMapProposal> proposals)
    {
        var applied = 0;
        foreach (var p in proposals)
        {
            if (string.IsNullOrWhiteSpace(p.ResolvedPath)) continue;
            SetPath(p.RootId, p.ResolvedPath!);
            applied++;
        }
        return applied;
    }

    private static string NormalizePath(string path) =>
        System.IO.Path.GetFullPath(path.Trim().TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}
