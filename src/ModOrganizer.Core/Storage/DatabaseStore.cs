using Npgsql;

namespace ModOrganizer.Core.Storage;

public sealed class DatabaseStore
{
    private readonly IDatabaseConnectionFactory _factory;

    public DatabaseStore(IDatabaseConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Initialize()
    {
        using var conn = _factory.Open();
        Migrations.Apply(conn);
    }

    public NpgsqlConnection Open() => _factory.Open();

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        _factory.OpenAsync(ct);

    public int SchemaVersion()
    {
        using var conn = _factory.Open();
        return Migrations.CurrentVersion(conn);
    }
}
