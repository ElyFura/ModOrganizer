using System.Text.RegularExpressions;
using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Links;

public sealed class LinkInfo
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Domain { get; set; }
    public int Kind { get; set; }
}

public static class UrlNormalizer
{
    private static readonly HashSet<string> TrackingPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_", "gclid", "fbclid", "mc_cid", "mc_eid", "yclid", "_ga"
    };

    public static string Normalize(string url)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        if (string.IsNullOrEmpty(uri.Query)) return uri.ToString();

        var kept = uri.Query.TrimStart('?').Split('&')
            .Where(p =>
            {
                var key = p.Split('=')[0];
                return !TrackingPrefixes.Any(pref => key.StartsWith(pref, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        var builder = new UriBuilder(uri)
        {
            Query = string.Join('&', kept)
        };
        return builder.Uri.ToString();
    }

    public static string? ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.ToLowerInvariant();
        return null;
    }
}

public static class DomainKindResolver
{
    public static ModLinkKind Resolve(string? domain)
    {
        if (string.IsNullOrEmpty(domain)) return ModLinkKind.Other;
        domain = domain.ToLowerInvariant();
        if (domain.Contains("xivmodarchive")) return ModLinkKind.Source;
        if (domain.Contains("nexusmods")) return ModLinkKind.Nexus;
        if (domain.Contains("patreon")) return ModLinkKind.Patreon;
        if (domain.Contains("ko-fi") || domain.Contains("kofi")) return ModLinkKind.Kofi;
        if (domain.Contains("twitter") || domain == "x.com" || domain.EndsWith(".x.com")) return ModLinkKind.Twitter;
        if (domain.Contains("discord")) return ModLinkKind.Discord;
        if (domain.Contains("github")) return ModLinkKind.Github;
        if (domain.Contains("gumroad")) return ModLinkKind.Gumroad;
        return ModLinkKind.Other;
    }
}

public sealed class LinkService
{
    private static readonly Regex ArchiveImageModIdPattern =
        new(@"^mod_(?<id>\d+)_", RegexOptions.IgnoreCase);

    private readonly DatabaseStore _store;
    public LinkService(DatabaseStore store) => _store = store;

    public IReadOnlyList<LinkInfo> GetLinksForMod(long modId)
    {
        using var conn = _store.Open();
        return conn.Query<LinkInfo>(
            """
            SELECT id AS Id, mod_id AS ModId, url AS Url, title AS Title,
                   domain AS Domain, kind AS Kind
            FROM mod_links WHERE mod_id=@m ORDER BY added_at
            """, new { m = modId }).ToList();
    }

    public long AddLink(long modId, string url, string? title = null)
    {
        var normalized = UrlNormalizer.Normalize(url);
        var domain = UrlNormalizer.ExtractDomain(normalized);
        var kind = DomainKindResolver.Resolve(domain);

        using var conn = _store.Open();
        var id = conn.ExecuteScalar<long>(
            """
            INSERT INTO mod_links(mod_id, url, title, domain, kind, added_at)
            VALUES (@m, @u, @t, @d, @k, @ts)
            RETURNING id
            """,
            new { m = modId, u = normalized, t = title, d = domain, k = (int)kind, ts = DateTimeOffset.UtcNow.ToString("o") });

        Management.ActionLog.Record(conn, null, ActionKind.LinkAdd,
            Guid.NewGuid().ToString("N"), modId: modId, toPath: normalized);
        return id;
    }

    public void UpdateLink(long linkId, string url, string? title)
    {
        var normalized = UrlNormalizer.Normalize(url);
        var domain = UrlNormalizer.ExtractDomain(normalized);
        var kind = DomainKindResolver.Resolve(domain);

        using var conn = _store.Open();
        conn.Execute(
            "UPDATE mod_links SET url=@u, title=@t, domain=@d, kind=@k WHERE id=@id",
            new { id = linkId, u = normalized, t = title, d = domain, k = (int)kind });
    }

    public void RemoveLink(long linkId)
    {
        using var conn = _store.Open();
        var row = conn.QuerySingleOrDefault<(long ModId, string Url)>(
            "SELECT mod_id AS ModId, url AS Url FROM mod_links WHERE id=@id", new { id = linkId });
        if (row.ModId == 0) return;
        conn.Execute("DELETE FROM mod_links WHERE id=@id", new { id = linkId });
        Management.ActionLog.Record(conn, null, ActionKind.LinkRemove,
            Guid.NewGuid().ToString("N"), modId: row.ModId, fromPath: row.Url);
    }

    public IReadOnlyList<string> SuggestLinksFromFiles(long modId)
    {
        using var conn = _store.Open();
        var fileNames = conn.Query<string>(
            "SELECT relative_path FROM mod_files WHERE mod_id=@m AND kind=3",
            new { m = modId }).ToList();

        return SuggestLinksFromFileNames(fileNames);
    }

    /// <summary>
    /// Same rule, but over a file list the caller already has. Lets the detail view derive
    /// suggestions from its single-round-trip snapshot instead of issuing another query.
    /// </summary>
    public static IReadOnlyList<string> SuggestLinksFromFileNames(IEnumerable<string> relativePaths)
    {
        var suggestions = new List<string>();
        foreach (var path in relativePaths)
        {
            var name = Path.GetFileName(path);
            var match = ArchiveImageModIdPattern.Match(name);
            if (match.Success)
                suggestions.Add($"https://www.xivmodarchive.com/modid/{match.Groups["id"].Value}");
        }
        return suggestions.Distinct().ToList();
    }
}
