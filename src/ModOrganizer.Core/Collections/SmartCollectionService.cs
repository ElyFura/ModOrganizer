using System.Text.Json;
using Dapper;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Collections;

public sealed class SmartCollectionFilter
{
    public long? CategoryId { get; set; }

    /// <summary>Tags the mod must carry — combined per <see cref="TagsAnyMode"/>.</summary>
    public List<long> TagIds { get; set; } = new();

    /// <summary>Tags the mod must not carry.</summary>
    public List<long> ExcludedTagIds { get; set; } = new();

    /// <summary>False = every tag in <see cref="TagIds"/> (AND), true = at least one (OR).</summary>
    public bool TagsAnyMode { get; set; }

    /// <summary>Restrict to mods with no tags at all.</summary>
    public bool OnlyUntagged { get; set; }

    public int MinRating { get; set; }
    public string? Search { get; set; }
}

public sealed class SmartCollection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FilterJson { get; set; } = "";
    public SmartCollectionFilter Filter =>
        JsonSerializer.Deserialize<SmartCollectionFilter>(FilterJson) ?? new SmartCollectionFilter();
}

public sealed class SmartCollectionService
{
    private readonly DatabaseStore _store;
    public SmartCollectionService(DatabaseStore store) => _store = store;

    public IReadOnlyList<SmartCollection> List()
    {
        using var conn = _store.Open();
        return conn.Query<SmartCollection>(
            "SELECT id AS Id, name AS Name, filter_json AS FilterJson FROM smart_collections ORDER BY name")
            .ToList();
    }

    public long Create(string name, SmartCollectionFilter filter)
    {
        using var conn = _store.Open();
        return conn.QuerySingle<long>(
            "INSERT INTO smart_collections (name, filter_json) VALUES (@n, @f) RETURNING id",
            new { n = name, f = JsonSerializer.Serialize(filter) });
    }

    public void Delete(long id)
    {
        using var conn = _store.Open();
        conn.Execute("DELETE FROM smart_collections WHERE id=@i", new { i = id });
    }
}
