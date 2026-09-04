using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Management;

public sealed class MovePlan
{
    public string TxId { get; init; } = Guid.NewGuid().ToString("N");
    public List<MoveStep> Steps { get; } = new();
    public List<string> Conflicts { get; } = new();
    public bool CanExecute => Conflicts.Count == 0 && Steps.Count > 0;
}

public sealed class MoveStep
{
    public long ModId { get; init; }
    public long TargetCategoryId { get; init; }
    public required string FromPath { get; init; }
    public required string ToPath { get; init; }
}

public sealed class MoveService
{
    private readonly DatabaseStore _store;
    private readonly FileSystemActivityGate? _gate;

    public MoveService(DatabaseStore store, FileSystemActivityGate? gate = null)
    {
        _store = store;
        _gate = gate;
    }

    public MovePlan CreatePlan(IEnumerable<long> modIds, long targetCategoryId)
    {
        using var conn = _store.Open();
        var target = conn.QuerySingle<(long RootId, string Name, string RootPath)>(
            """
            SELECT c.root_id AS RootId, c.name AS Name, r.path AS RootPath
            FROM categories c JOIN roots r ON r.id=c.root_id WHERE c.id=@id
            """, new { id = targetCategoryId });

        var targetCategoryPath = Path.Combine(target.RootPath, target.Name);
        var plan = new MovePlan();

        foreach (var modId in modIds.Distinct())
        {
            var src = conn.QuerySingle<(string FromFolder, string CatName, string RootPath)>(
                """
                SELECT m.folder_name AS FromFolder, c.name AS CatName, r.path AS RootPath
                FROM mods m JOIN categories c ON c.id=m.category_id
                            JOIN roots r ON r.id=c.root_id
                WHERE m.id=@m
                """, new { m = modId });

            var from = Path.Combine(src.RootPath, src.CatName, src.FromFolder);
            var to = Path.Combine(targetCategoryPath, src.FromFolder);

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;

            if (Directory.Exists(to))
            {
                plan.Conflicts.Add($"Target already exists: {to}");
                continue;
            }

            plan.Steps.Add(new MoveStep
            {
                ModId = modId, TargetCategoryId = targetCategoryId,
                FromPath = from, ToPath = to
            });
        }
        return plan;
    }

    public void Execute(MovePlan plan)
    {
        using var _suppress = _gate?.Suppress();
        if (!plan.CanExecute)
            throw new InvalidOperationException("Plan has conflicts or no steps.");

        using var conn = _store.Open();

        Directory.CreateDirectory(Path.GetDirectoryName(plan.Steps[0].ToPath)!);

        var done = new List<(string From, string To)>();
        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var step in plan.Steps)
            {
                MoveDirectory(step.FromPath, step.ToPath);
                done.Add((step.FromPath, step.ToPath));

                conn.Execute(
                    "UPDATE mods SET category_id=@tc, updated_at=@t WHERE id=@m",
                    new { tc = step.TargetCategoryId, t = DateTimeOffset.UtcNow.ToString("o"), m = step.ModId }, tx);

                ActionLog.Record(conn, tx, ActionKind.Move, plan.TxId,
                    modId: step.ModId, fromPath: step.FromPath, toPath: step.ToPath);
            }
            tx.Commit();
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { MoveDirectory(done[i].To, done[i].From); } catch { }
            }
            throw;
        }
    }

    private static void MoveDirectory(string from, string to)
    {
        try { Directory.Move(from, to); }
        catch (IOException)
        {
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
