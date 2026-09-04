using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Pmp;

public sealed class PmpMetaResult
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string RawJson { get; set; } = "";
    public List<PmpGroupEntry> Groups { get; } = new();
    public List<string> GamePaths { get; } = new();
    public string? ExtractedPreviewPath { get; set; }
}

public sealed record PmpGroupEntry(string Name, string? Type, string OptionJson);

public sealed class PmpInspector
{
    private static readonly string[] PreviewExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    private readonly string _previewCacheRoot;

    public PmpInspector(string? previewCacheRoot = null)
    {
        _previewCacheRoot = previewCacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer", "pmp_previews");
    }

    public PmpMetaResult? Inspect(string pmpPath, long modFileId)
    {
        if (!File.Exists(pmpPath)) return null;
        if (!pmpPath.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            using var archive = ZipFile.OpenRead(pmpPath);
            var result = new PmpMetaResult();

            ParseMeta(archive, result);
            ParseGroups(archive, result);
            result.ExtractedPreviewPath = ExtractPreview(archive, modFileId);

            return result;
        }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
    }

    private static void ParseMeta(ZipArchive archive, PmpMetaResult result)
    {
        var metaEntry = archive.GetEntry("meta.json");
        if (metaEntry is null) return;

        using var reader = new StreamReader(metaEntry.Open());
        var json = reader.ReadToEnd();
        result.RawJson = json;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return;
            result.Name = node["Name"]?.GetValue<string>();
            result.Author = node["Author"]?.GetValue<string>();
            result.Version = node["Version"]?.GetValue<string>();
            result.Description = node["Description"]?.GetValue<string>();
            result.Website = node["Website"]?.GetValue<string>();
        }
        catch (JsonException) { }
    }

    private static void ParseGroups(ZipArchive archive, PmpMetaResult result)
    {
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!name.StartsWith("group_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();

            try
            {
                var node = JsonNode.Parse(json);
                if (node is null) continue;

                var gName = node["Name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(name);
                var gType = node["Type"]?.GetValue<string>();

                result.Groups.Add(new PmpGroupEntry(gName, gType, json));

                CollectGamePaths(node, result.GamePaths);
            }
            catch (JsonException) { }
        }

        var defaultEntry = archive.GetEntry("default_mod.json");
        if (defaultEntry is not null)
        {
            using var reader = new StreamReader(defaultEntry.Open());
            try
            {
                var node = JsonNode.Parse(reader.ReadToEnd());
                if (node is not null) CollectGamePaths(node, result.GamePaths);
            }
            catch (JsonException) { }
        }
    }

    private static void CollectGamePaths(JsonNode node, List<string> paths)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Key is "GamePath" or "GamePaths" && kv.Value is not null)
                {
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                        paths.Add(s);
                    else if (kv.Value is JsonArray arr)
                    {
                        foreach (var item in arr)
                            if (item is JsonValue iv && iv.TryGetValue<string>(out var si))
                                paths.Add(si);
                    }
                }
                else if (kv.Key is "Files" or "FileSwaps" or "Manipulations" && kv.Value is JsonObject sub)
                {
                    foreach (var child in sub)
                        paths.Add(child.Key);
                }
                else if (kv.Value is not null)
                {
                    CollectGamePaths(kv.Value, paths);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) CollectGamePaths(item, paths);
        }
    }

    private string? ExtractPreview(ZipArchive archive, long modFileId)
    {
        Directory.CreateDirectory(_previewCacheRoot);

        var imageEntry = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Where(e => PreviewExtensions.Contains(
                Path.GetExtension(e.Name).ToLowerInvariant()))
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();

        if (imageEntry is null) return null;

        var ext = Path.GetExtension(imageEntry.Name).ToLowerInvariant();
        var dest = Path.Combine(_previewCacheRoot, $"{modFileId}{ext}");
        try
        {
            using var zipStream = imageEntry.Open();
            using var fs = File.Create(dest);
            zipStream.CopyTo(fs);
            return dest;
        }
        catch (IOException) { return null; }
    }

    public void PersistToDb(NpgsqlConnection conn, NpgsqlTransaction? tx,
        long modFileId, PmpMetaResult result)
    {
        conn.Execute("DELETE FROM pmp_meta WHERE mod_file_id=@m", new { m = modFileId }, tx);
        conn.Execute("DELETE FROM pmp_groups WHERE mod_file_id=@m", new { m = modFileId }, tx);
        conn.Execute("DELETE FROM pmp_game_paths WHERE mod_file_id=@m", new { m = modFileId }, tx);
        conn.Execute("DELETE FROM pmp_previews WHERE mod_file_id=@m", new { m = modFileId }, tx);

        conn.Execute(
            """
            INSERT INTO pmp_meta(mod_file_id, name, author, version, description, website, raw_json, parsed_at)
            VALUES (@m, @n, @a, @v, @d, @w, @r, @t)
            """,
            new
            {
                m = modFileId, n = result.Name, a = result.Author, v = result.Version,
                d = result.Description, w = result.Website, r = result.RawJson,
                t = DateTimeOffset.UtcNow.ToString("o")
            }, tx);

        foreach (var g in result.Groups)
        {
            conn.Execute(
                "INSERT INTO pmp_groups(mod_file_id, name, type, option_json) VALUES (@m, @n, @t, @o)",
                new { m = modFileId, n = g.Name, t = g.Type, o = g.OptionJson }, tx);
        }

        foreach (var path in result.GamePaths.Distinct())
        {
            conn.Execute(
                "INSERT INTO pmp_game_paths(mod_file_id, game_path) VALUES (@m, @p)",
                new { m = modFileId, p = path }, tx);
        }

        if (result.ExtractedPreviewPath is not null)
        {
            conn.Execute(
                "INSERT INTO pmp_previews(mod_file_id, cache_path, extracted_at) VALUES (@m, @p, @t)",
                new { m = modFileId, p = result.ExtractedPreviewPath, t = DateTimeOffset.UtcNow.ToString("o") }, tx);
        }
    }

    public sealed record PmpDetail(
        long ModFileId, string FilePath,
        string? Name, string? Author, string? Version, string? Description, string? Website,
        IReadOnlyList<PmpGroupEntry> Groups, IReadOnlyList<string> GamePaths,
        string? PreviewCachePath);

    public static IReadOnlyList<PmpDetail> GetForMod(DatabaseStore store, long modId)
    {
        using var conn = store.Open();

        var files = conn.Query<(long Id, string RelativePath)>(
            "SELECT id AS Id, relative_path AS RelativePath FROM mod_files WHERE mod_id=@m AND kind=1",
            new { m = modId }).ToList();

        if (files.Count == 0) return Array.Empty<PmpDetail>();

        var result = new List<PmpDetail>();
        foreach (var f in files)
        {
            var meta = conn.QuerySingleOrDefault<(string? Name, string? Author, string? Version,
                string? Description, string? Website)>(
                "SELECT name AS Name, author AS Author, version AS Version, description AS Description, website AS Website FROM pmp_meta WHERE mod_file_id=@m",
                new { m = f.Id });

            var groups = conn.Query<(string Name, string? Type, string OptionJson)>(
                "SELECT name AS Name, type AS Type, option_json AS OptionJson FROM pmp_groups WHERE mod_file_id=@m ORDER BY id",
                new { m = f.Id })
                .Select(r => new PmpGroupEntry(r.Name, r.Type, r.OptionJson))
                .ToList();

            var paths = conn.Query<string>(
                "SELECT game_path FROM pmp_game_paths WHERE mod_file_id=@m ORDER BY game_path",
                new { m = f.Id }).ToList();

            var preview = conn.QuerySingleOrDefault<string?>(
                "SELECT cache_path FROM pmp_previews WHERE mod_file_id=@m",
                new { m = f.Id });

            result.Add(new PmpDetail(f.Id, f.RelativePath,
                meta.Name, meta.Author, meta.Version, meta.Description, meta.Website,
                groups, paths, preview));
        }
        return result;
    }
}
