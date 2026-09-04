using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Comments;

public sealed class CommentService
{
    private readonly DatabaseStore _store;
    private readonly string _assetRoot;

    public CommentService(DatabaseStore store, string? assetRoot = null)
    {
        _store = store;
        _assetRoot = assetRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer", "comment_assets");
    }

    public string? GetComment(long modId)
    {
        using var conn = _store.Open();
        return conn.QuerySingleOrDefault<string?>(
            "SELECT comment_md FROM mods WHERE id=@m", new { m = modId });
    }

    public void SetComment(long modId, string? markdown)
    {
        using var conn = _store.Open();
        conn.Execute(
            "UPDATE mods SET comment_md=@c, updated_at=@t WHERE id=@m",
            new { m = modId, c = markdown, t = DateTimeOffset.UtcNow.ToString("o") });

        Management.ActionLog.Record(conn, null, ActionKind.CommentUpdate,
            Guid.NewGuid().ToString("N"), modId: modId);
    }

    public string ImportAsset(long modId, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Asset not found.", sourcePath);

        var modFolder = Path.Combine(_assetRoot, modId.ToString());
        Directory.CreateDirectory(modFolder);

        var ext = Path.GetExtension(sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var destName = $"{baseName}{ext}";
        var dest = Path.Combine(modFolder, destName);
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(modFolder, $"{baseName} ({i}){ext}");

        File.Copy(sourcePath, dest);

        return new Uri(dest).AbsoluteUri;
    }

    public IReadOnlyList<long> SearchByComment(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<long>();

        using var conn = _store.Open();
        try
        {
            return conn.Query<long>(
                "SELECT rowid FROM mod_comments_fts WHERE mod_comments_fts MATCH @q",
                new { q = query }).ToList();
        }
        catch
        {
            return conn.Query<long>(
                "SELECT id FROM mods WHERE comment_md LIKE @q",
                new { q = "%" + query + "%" }).ToList();
        }
    }
}
