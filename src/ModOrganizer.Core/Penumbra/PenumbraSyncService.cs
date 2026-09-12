using System.Text.Json;
using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Penumbra;

public sealed class PenumbraUserSnapshot
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? ColorHex { get; set; }
    public bool IsSelf { get; set; }
    public PenumbraSnapshot Snapshot { get; set; } = new();

    /// <summary>
    /// When this user last uploaded their Penumbra state. Shown in the UI because a
    /// snapshot that is days old looks exactly like a current one otherwise - and acting
    /// on stale "is enabled" information is worse than having none.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Short, human wording for <see cref="UpdatedAt"/>.</summary>
    public string AgeText
    {
        get
        {
            if (IsSelf) return "jetzt";
            if (UpdatedAt is not { } at) return "unbekannt";

            var age = DateTimeOffset.UtcNow - at;
            if (age < TimeSpan.FromMinutes(2)) return "gerade eben";
            if (age < TimeSpan.FromHours(1)) return $"vor {(int)age.TotalMinutes} Min.";
            if (age < TimeSpan.FromDays(1)) return $"vor {(int)age.TotalHours} Std.";
            return $"vor {(int)age.TotalDays} Tag(en)";
        }
    }

    /// <summary>Old enough that the user should be told rather than quietly trusting it.</summary>
    public bool IsStale => !IsSelf && (UpdatedAt is null || DateTimeOffset.UtcNow - UpdatedAt > TimeSpan.FromDays(1));
}

/// <summary>
/// Pushes the current user's local Penumbra snapshot up to Supabase and
/// reads back every user's snapshot for cross-user inspection.
/// One row per user; payload is the full snapshot serialized as JSON.
/// </summary>
public sealed class PenumbraSyncService
{
    private readonly DatabaseStore _store;
    private readonly IUserContext _user;

    public PenumbraSyncService(DatabaseStore store, IUserContext user)
    {
        _store = store;
        _user = user;
    }

    public bool IsLoggedIn => _user.UserId.HasValue;

    public void Push(PenumbraSnapshot snapshot)
    {
        if (_user.UserId is not Guid uid) return;
        if (!snapshot.IsAvailable) return;

        var dto = SnapshotDto.From(snapshot);
        var json = JsonSerializer.Serialize(dto);

        using var conn = _store.Open();
        conn.Execute(
            """
            INSERT INTO penumbra_user_state (user_id, payload_json, updated_at)
            VALUES (@u, @p, NOW())
            ON CONFLICT (user_id) DO UPDATE
                SET payload_json = EXCLUDED.payload_json, updated_at = NOW()
            """,
            new { u = uid, p = json });
    }

    public IReadOnlyList<PenumbraUserSnapshot> LoadAll()
    {
        var selfId = _user.UserId;
        using var conn = _store.Open();
        var rows = conn.Query<(Guid UserId, string DisplayName, string? ColorHex, string PayloadJson, DateTime UpdatedAt)>(
            """
            SELECT s.user_id    AS UserId,
                   COALESCE(u.display_name, u.email, '?') AS DisplayName,
                   u.color_hex  AS ColorHex,
                   s.payload_json AS PayloadJson,
                   s.updated_at AS UpdatedAt
            FROM penumbra_user_state s
            LEFT JOIN users u ON u.id = s.user_id
            ORDER BY s.updated_at DESC
            """).ToList();

        var result = new List<PenumbraUserSnapshot>(rows.Count);
        foreach (var r in rows)
        {
            PenumbraSnapshot snap;
            try
            {
                var dto = JsonSerializer.Deserialize<SnapshotDto>(r.PayloadJson) ?? new SnapshotDto();
                snap = dto.ToSnapshot();
            }
            catch
            {
                continue;
            }
            result.Add(new PenumbraUserSnapshot
            {
                UserId = r.UserId,
                DisplayName = r.DisplayName,
                ColorHex = r.ColorHex,
                IsSelf = selfId == r.UserId,
                UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)),
                Snapshot = snap
            });
        }
        return result;
    }

    // -- DTO: PenumbraSnapshot has internal lookup state we don't want to round-trip. --
    private sealed class SnapshotDto
    {
        public bool IsAvailable { get; set; }
        public string? ModDirectory { get; set; }
        public List<CollectionDto> Collections { get; set; } = new();
        public List<EntryDto> Entries { get; set; } = new();

        public static SnapshotDto From(PenumbraSnapshot s) => new()
        {
            IsAvailable = s.IsAvailable,
            ModDirectory = s.ModDirectory,
            Collections = s.Collections.Select(c => new CollectionDto
            {
                Id = c.Id, Name = c.Name, Role = c.Role
            }).ToList(),
            Entries = s.Entries.Select(e => new EntryDto
            {
                FolderName = e.FolderName,
                MetaName = e.MetaName,
                ActiveInCollections = e.ActiveInCollections.ToList(),
                AllInCollections = e.AllInCollections.ToList()
            }).ToList()
        };

        public PenumbraSnapshot ToSnapshot() => new()
        {
            IsAvailable = IsAvailable,
            ModDirectory = ModDirectory,
            Collections = Collections.Select(c => new PenumbraCollection
            {
                Id = c.Id, Name = c.Name, Role = c.Role
            }).ToList(),
            Entries = Entries.Select(e => new PenumbraEntry
            {
                FolderName = e.FolderName,
                MetaName = e.MetaName,
                ActiveInCollections = e.ActiveInCollections,
                AllInCollections = e.AllInCollections
            }).ToList()
        };
    }

    private sealed class CollectionDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
    }

    private sealed class EntryDto
    {
        public string FolderName { get; set; } = "";
        public string? MetaName { get; set; }
        public List<string> ActiveInCollections { get; set; } = new();
        public List<string> AllInCollections { get; set; } = new();
    }
}
