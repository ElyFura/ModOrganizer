using Dapper;
using Npgsql;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Models;

namespace ModOrganizer.Core.Management;

public static class ActionLog
{
    /// <summary>Set at app startup to inject the current user into all action_log writes.</summary>
    public static IUserContext? CurrentUser { get; set; }

    public static void Record(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        ActionKind action, string txId,
        long? modId = null, string? fromPath = null, string? toPath = null,
        string? payloadJson = null)
    {
        var userId = CurrentUser?.UserId;
        var ts = DateTimeOffset.UtcNow.ToString("o");

        conn.Execute(
            """
            INSERT INTO action_log(ts, action, mod_id, user_id, from_path, to_path, tx_id, payload_json)
            VALUES (@ts, @a, @m, @u, @f, @t, @x, @p)
            """,
            new
            {
                ts,
                a = (int)action,
                m = modId,
                u = userId,
                f = fromPath,
                t = toPath,
                x = txId,
                p = payloadJson
            }, tx);

        // Also stamp the affected mod's updated_by so Avatar-Chips show who
        // last touched it. Not for delete/scanner-internal actions.
        if (modId.HasValue && userId.HasValue && action != ActionKind.Delete)
        {
            conn.Execute(
                "UPDATE mods SET updated_by=@u, updated_at=@t WHERE id=@m",
                new { m = modId.Value, u = userId.Value, t = ts }, tx);
        }
    }

    /// <summary>
    /// Records the same action for many mods in two statements instead of two per mod.
    /// Bulk-tagging 50 mods used to cost 100 round trips to a remote database.
    /// </summary>
    public static void RecordMany(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        ActionKind action, string txId,
        IReadOnlyList<long> modIds, string? payloadJson = null)
    {
        if (modIds.Count == 0) return;

        var userId = CurrentUser?.UserId;
        var ts = DateTimeOffset.UtcNow.ToString("o");
        var ids = modIds.ToArray();

        conn.Execute(
            """
            INSERT INTO action_log(ts, action, mod_id, user_id, from_path, to_path, tx_id, payload_json)
            SELECT @ts, @a, m, @u, NULL, NULL, @x, @p
            FROM unnest(@ids::bigint[]) AS m
            """,
            new { ts, a = (int)action, u = userId, x = txId, p = payloadJson, ids }, tx);

        if (userId.HasValue && action != ActionKind.Delete)
        {
            conn.Execute(
                """
                UPDATE mods SET updated_by=@u, updated_at=@t
                WHERE id = ANY(@ids::bigint[])
                """,
                new { u = userId.Value, t = ts, ids }, tx);
        }
    }

    public sealed class Entry
    {
        public long Id { get; set; }
        public int Action { get; set; }
        public long? ModId { get; set; }
        public string? FromPath { get; set; }
        public string? ToPath { get; set; }
        public string TxId { get; set; } = "";
        public string? PayloadJson { get; set; }
    }

    public static IReadOnlyList<Entry> GetTransaction(NpgsqlConnection conn, string txId) =>
        conn.Query<Entry>(
            """
            SELECT id AS Id, action AS Action, mod_id AS ModId,
                   from_path AS FromPath, to_path AS ToPath, tx_id AS TxId,
                   payload_json AS PayloadJson
            FROM action_log WHERE tx_id=@x ORDER BY id
            """, new { x = txId }).ToList();

    public static string? GetLatestTxId(NpgsqlConnection conn) =>
        conn.QuerySingleOrDefault<string?>(
            "SELECT tx_id FROM action_log ORDER BY id DESC LIMIT 1");
}
