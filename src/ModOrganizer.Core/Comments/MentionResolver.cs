using System.Text.RegularExpressions;
using Dapper;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Comments;

public sealed record MentionedUser(Guid UserId, string Handle, string DisplayName, string? Email);

public sealed class MentionResolver
{
    private static readonly Regex MentionPattern = new(@"@([\w.\-]+)", RegexOptions.Compiled);

    private readonly DatabaseStore _store;
    public MentionResolver(DatabaseStore store) => _store = store;

    /// <summary>
    /// Extracts @handle tokens from a comment body and resolves them against the
    /// users table. Match: case-insensitive on display_name OR email prefix.
    /// </summary>
    public IReadOnlyList<MentionedUser> Resolve(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<MentionedUser>();

        var handles = MentionPattern.Matches(body)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (handles.Count == 0) return Array.Empty<MentionedUser>();

        using var conn = _store.Open();
        // Match by display_name (lower) OR by part-before-@ of email (lower).
        var rows = conn.Query<(Guid Id, string? DisplayName, string? Email, string Handle)>(
            """
            SELECT u.id AS Id, u.display_name AS DisplayName, u.email AS Email, h.handle AS Handle
            FROM unnest(@handles::text[]) AS h(handle)
            JOIN users u ON
                LOWER(u.display_name) = LOWER(h.handle)
                OR LOWER(split_part(u.email, '@', 1)) = LOWER(h.handle)
            """, new { handles = handles.ToArray() }).ToList();

        return rows.Select(r => new MentionedUser(
            r.Id, r.Handle, r.DisplayName ?? r.Email ?? "?", r.Email)).ToList();
    }
}
