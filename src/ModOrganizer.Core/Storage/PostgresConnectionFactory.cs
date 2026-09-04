using Npgsql;

namespace ModOrganizer.Core.Storage;

public interface IDatabaseConnectionFactory
{
    string ConnectionString { get; }
    NpgsqlConnection Open();
    ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class PostgresConnectionFactory : IDatabaseConnectionFactory, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public string ConnectionString { get; }

    public PostgresConnectionFactory(string connectionString)
    {
        ConnectionString = Harden(connectionString);
        _dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    /// <summary>
    /// Applies resilience defaults for a remote (Supabase) Postgres without overriding
    /// anything the user set explicitly in appsettings.json.
    ///
    /// Why each one matters over a ~35 ms WAN link:
    /// - Keepalive/TcpKeepalive: Supabase drops silently idle sessions. Without keepalives
    ///   the client keeps a dead socket in the pool and the next query dies with
    ///   "Timeout during reading attempt".
    /// - ConnectionIdleLifetime: prune pooled connections before the server does it for us.
    /// - MaxAutoPrepare: Dapper sends the same SQL text over and over; server-side prepared
    ///   statements remove a parse+plan per call.
    /// - CommandTimeout/Timeout: fail in a bounded time instead of hanging the caller.
    /// </summary>
    internal static string Harden(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);

        if (!Has(connectionString, "Keepalive")) b.KeepAlive = 30;
        if (!Has(connectionString, "Tcp Keepalive", "TcpKeepalive")) b.TcpKeepAlive = true;
        if (!Has(connectionString, "Connection Idle Lifetime", "ConnectionIdleLifetime"))
            b.ConnectionIdleLifetime = 60;
        if (!Has(connectionString, "Connection Pruning Interval", "ConnectionPruningInterval"))
            b.ConnectionPruningInterval = 20;
        if (!Has(connectionString, "Command Timeout", "CommandTimeout")) b.CommandTimeout = 30;
        if (!Has(connectionString, "Timeout")) b.Timeout = 15;
        if (!Has(connectionString, "Max Auto Prepare", "MaxAutoPrepare")) b.MaxAutoPrepare = 20;
        if (!Has(connectionString, "Maximum Pool Size", "MaxPoolSize")) b.MaxPoolSize = 20;
        if (!Has(connectionString, "Minimum Pool Size", "MinPoolSize")) b.MinPoolSize = 1;

        return b.ConnectionString;
    }

    /// <summary>
    /// True if the raw connection string mentions one of these keys. We check the raw text
    /// rather than the builder because the builder cannot distinguish "user set the default"
    /// from "user set nothing".
    /// </summary>
    private static bool Has(string connectionString, params string[] keys)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part.Substring(0, eq).Trim().Replace(" ", "");
            foreach (var key in keys)
            {
                if (string.Equals(name, key.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public NpgsqlConnection Open() => _dataSource.OpenConnection();

    public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public void Dispose() => _dataSource.Dispose();
}
