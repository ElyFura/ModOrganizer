using System.Text.RegularExpressions;
using Dapper;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Management;

public sealed class RenamePlan
{
    public string TxId { get; init; } = Guid.NewGuid().ToString("N");
    public long ModId { get; init; }
    public string OldFolderPath { get; init; } = "";
    public string NewFolderPath { get; init; } = "";
    public List<RenameStep> FileSteps { get; } = new();
    public List<RenameConflict> Conflicts { get; } = new();
    public bool CanExecute => Conflicts.Count == 0 && HasEnabledSteps;
    public bool HasEnabledSteps => FileSteps.Any(s => s.Enabled) || OldFolderPath != NewFolderPath;
}

public sealed class RenameStep
{
    public required string FromPath { get; init; }
    public required string ToPath { get; init; }
    public bool Enabled { get; set; } = true;
}

public sealed class RenameConflict
{
    public required string Path { get; init; }
    public required string Reason { get; init; }
}

public sealed class RenameService
{
    private static readonly Regex ArchiveImagePattern =
        new(@"^mod_\d+_[a-f0-9\-]+\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase);

    private readonly DatabaseStore _store;
    private readonly FileSystemActivityGate? _gate;

    public RenameService(DatabaseStore store, FileSystemActivityGate? gate = null)
    {
        _store = store;
        _gate = gate;
    }

    public RenamePlan CreatePlan(long modId, string newFolderName)
    {
        if (string.IsNullOrWhiteSpace(newFolderName))
            throw new ArgumentException("Name cannot be empty.", nameof(newFolderName));
        if (newFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Name contains invalid characters.", nameof(newFolderName));

        using var conn = _store.Open();
        var row = conn.QuerySingle<(string RootPath, string CatName, string OldFolder)>(
            """
            SELECT r.path AS RootPath, c.name AS CatName, m.folder_name AS OldFolder
            FROM mods m JOIN categories c ON c.id=m.category_id
                        JOIN roots r ON r.id=c.root_id
            WHERE m.id=@m
            """, new { m = modId });

        var oldFolderPath = Path.Combine(row.RootPath, row.CatName, row.OldFolder);
        var newFolderPath = Path.Combine(row.RootPath, row.CatName, newFolderName);

        var plan = new RenamePlan
        {
            ModId = modId,
            OldFolderPath = oldFolderPath,
            NewFolderPath = newFolderPath
        };

        if (!Directory.Exists(oldFolderPath))
        {
            plan.Conflicts.Add(new RenameConflict { Path = oldFolderPath, Reason = "Source folder missing" });
            return plan;
        }

        if (oldFolderPath != newFolderPath && Directory.Exists(newFolderPath))
        {
            plan.Conflicts.Add(new RenameConflict { Path = newFolderPath, Reason = "Target folder already exists" });
        }

        foreach (var file in Directory.EnumerateFiles(oldFolderPath))
        {
            var fileName = Path.GetFileName(file);
            var newName = ComputeNewFileName(fileName, row.OldFolder, newFolderName);
            if (newName is null) continue;

            var fromAfterFolderRename = Path.Combine(newFolderPath, fileName);
            var toAfterFolderRename = Path.Combine(newFolderPath, newName);

            plan.FileSteps.Add(new RenameStep
            {
                FromPath = fromAfterFolderRename,
                ToPath = toAfterFolderRename
            });
        }

        return plan;
    }

    public static string? ComputeNewFileName(string fileName, string oldFolder, string newFolder)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        if (ArchiveImagePattern.IsMatch(fileName))
            return newFolder + ext;

        if (stem.Equals(oldFolder, StringComparison.OrdinalIgnoreCase))
            return newFolder + ext;

        if (stem.StartsWith(oldFolder + " ", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith(oldFolder + "-", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith(oldFolder + "_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith(oldFolder + " -", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = stem.Substring(oldFolder.Length);
            return newFolder + suffix + ext;
        }

        return null;
    }

    public void Execute(RenamePlan plan)
    {
        if (!plan.CanExecute)
            throw new InvalidOperationException("Plan has conflicts or no steps.");

        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();

        var renamed = new List<(string From, string To, bool IsFolder)>();
        bool folderRenamed = false;

        try
        {
            using var tx = conn.BeginTransaction();

            if (!string.Equals(plan.OldFolderPath, plan.NewFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(plan.OldFolderPath, plan.NewFolderPath);
                renamed.Add((plan.OldFolderPath, plan.NewFolderPath, true));
                folderRenamed = true;
                ActionLog.Record(conn, tx, ActionKind.Rename, plan.TxId,
                    modId: plan.ModId, fromPath: plan.OldFolderPath, toPath: plan.NewFolderPath,
                    payloadJson: """{"kind":"folder"}""");
            }

            foreach (var step in plan.FileSteps.Where(s => s.Enabled))
            {
                if (!File.Exists(step.FromPath)) continue;
                if (string.Equals(step.FromPath, step.ToPath, StringComparison.OrdinalIgnoreCase)) continue;

                File.Move(step.FromPath, step.ToPath);
                renamed.Add((step.FromPath, step.ToPath, false));
                ActionLog.Record(conn, tx, ActionKind.Rename, plan.TxId,
                    modId: plan.ModId, fromPath: step.FromPath, toPath: step.ToPath,
                    payloadJson: """{"kind":"file"}""");
            }

            if (folderRenamed)
            {
                var newFolderName = Path.GetFileName(plan.NewFolderPath);
                conn.Execute(
                    "UPDATE mods SET folder_name=@n, updated_at=@t WHERE id=@m",
                    new { n = newFolderName, t = DateTimeOffset.UtcNow.ToString("o"), m = plan.ModId }, tx);
            }

            tx.Commit();
        }
        catch
        {
            for (int i = renamed.Count - 1; i >= 0; i--)
            {
                var (from, to, isFolder) = renamed[i];
                try
                {
                    if (isFolder) Directory.Move(to, from);
                    else File.Move(to, from);
                }
                catch { }
            }
            throw;
        }
    }

    public void Undo(string txId)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();
        var entries = ActionLog.GetTransaction(conn, txId)
            .Where(e => e.Action == (int)ActionKind.Rename && e.FromPath != null && e.ToPath != null)
            .ToList();

        if (entries.Count == 0) return;

        // Same tracked rollback as Execute. Without it a half-finished Undo left some
        // files renamed back, the DB rolled forward, and the action_log rows still in
        // place — so pressing Undo again dug the inconsistency deeper.
        var reverted = new List<(string From, string To, bool IsFolder)>();

        try
        {
            using var tx = conn.BeginTransaction();

            // Files first: their recorded paths sit inside the renamed folder, so they
            // have to move back before the folder itself does.
            foreach (var e in entries.Where(e => e.PayloadJson?.Contains("\"file\"") == true)
                                     .OrderByDescending(e => e.Id))
            {
                if (!File.Exists(e.ToPath!)) continue;
                if (File.Exists(e.FromPath!))
                    throw new IOException($"Cannot undo: '{e.FromPath}' already exists.");

                File.Move(e.ToPath!, e.FromPath!);
                reverted.Add((e.ToPath!, e.FromPath!, false));
            }

            var folderEntry = entries.FirstOrDefault(e => e.PayloadJson?.Contains("\"folder\"") == true);
            if (folderEntry is not null)
            {
                if (Directory.Exists(folderEntry.ToPath!))
                {
                    if (Directory.Exists(folderEntry.FromPath!))
                        throw new IOException($"Cannot undo: '{folderEntry.FromPath}' already exists.");

                    Directory.Move(folderEntry.ToPath!, folderEntry.FromPath!);
                    reverted.Add((folderEntry.ToPath!, folderEntry.FromPath!, true));
                }

                if (folderEntry.ModId is long modId)
                {
                    conn.Execute(
                        "UPDATE mods SET folder_name=@n, updated_at=@t WHERE id=@m",
                        new
                        {
                            n = Path.GetFileName(folderEntry.FromPath!),
                            t = DateTimeOffset.UtcNow.ToString("o"),
                            m = modId
                        }, tx);
                }
            }

            conn.Execute("DELETE FROM action_log WHERE tx_id=@x", new { x = txId }, tx);
            tx.Commit();
        }
        catch
        {
            // Undo the undo, newest move first, so the transaction rollback leaves the
            // disk matching the database again.
            for (int i = reverted.Count - 1; i >= 0; i--)
            {
                var (from, to, isFolder) = reverted[i];
                try
                {
                    if (isFolder) Directory.Move(to, from);
                    else File.Move(to, from);
                }
                catch { }
            }
            throw;
        }
    }

    public BulkRenamePlan CreateBulkPlan(IEnumerable<long> modIds, string pattern, BulkPatternKind kind)
    {
        var plan = new BulkRenamePlan { Pattern = pattern, Kind = kind };

        using var conn = _store.Open();
        int index = 0;
        foreach (var modId in modIds)
        {
            index++;
            var row = conn.QuerySingleOrDefault<(string RootPath, string CatName, string Folder)>(
                """
                SELECT r.path AS RootPath, c.name AS CatName, m.folder_name AS Folder
                FROM mods m JOIN categories c ON c.id=m.category_id
                            JOIN roots r ON r.id=c.root_id
                WHERE m.id=@m
                """, new { m = modId });
            if (string.IsNullOrEmpty(row.Folder)) continue;

            string newName;
            try
            {
                newName = kind == BulkPatternKind.Template
                    ? ApplyTemplate(pattern, row.Folder, row.CatName, index)
                    : ApplyRegex(pattern, row.Folder);
            }
            catch (Exception ex)
            {
                plan.Entries.Add(new BulkRenameEntry(modId, row.Folder, row.Folder, false, ex.Message));
                continue;
            }

            if (string.IsNullOrWhiteSpace(newName) ||
                newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                plan.Entries.Add(new BulkRenameEntry(modId, row.Folder, newName, false, "Invalid name"));
                continue;
            }

            if (newName == row.Folder)
            {
                plan.Entries.Add(new BulkRenameEntry(modId, row.Folder, newName, false, "unchanged"));
                continue;
            }

            var newPath = Path.Combine(row.RootPath, row.CatName, newName);
            string? conflict = null;
            if (Directory.Exists(newPath)) conflict = "target exists";

            plan.Entries.Add(new BulkRenameEntry(modId, row.Folder, newName, conflict is null, conflict));
        }
        return plan;
    }

    /// <summary>
    /// Renames each entry independently and reports what did not work. Swallowing the
    /// per-entry exceptions and returning void meant the dialog announced success even
    /// when every single rename had failed.
    /// </summary>
    public BulkRenameResult ExecuteBulk(BulkRenamePlan plan)
    {
        using var _suppress = _gate?.Suppress();

        var result = new BulkRenameResult();
        foreach (var entry in plan.Entries.Where(e => e.Enabled && e.Conflict is null))
        {
            try
            {
                var p = CreatePlan(entry.ModId, entry.NewFolderName);
                if (!p.CanExecute)
                {
                    var reason = p.Conflicts.Count > 0
                        ? string.Join("; ", p.Conflicts.Select(c => $"{c.Reason}: {c.Path}"))
                        : "nothing to rename";
                    result.Failures.Add(new BulkRenameFailure(entry.ModId, entry.NewFolderName, reason));
                    continue;
                }

                Execute(p);
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failures.Add(new BulkRenameFailure(entry.ModId, entry.NewFolderName, ex.Message));
            }
        }
        return result;
    }

    private static string ApplyTemplate(string template, string folder, string category, int index)
    {
        return template
            .Replace("{original}", folder)
            .Replace("{name}", folder)
            .Replace("{category}", category)
            .Replace("{index}", index.ToString("D2"));
    }

    private static string ApplyRegex(string pattern, string input)
    {
        if (pattern.StartsWith("/") && pattern.Length > 2)
        {
            var parts = pattern.Substring(1).Split('/');
            if (parts.Length >= 2)
            {
                var regex = new Regex(parts[0]);
                return regex.Replace(input, parts[1]);
            }
        }
        throw new ArgumentException("Regex pattern must be /find/replace/");
    }
}

public enum BulkPatternKind { Template, Regex }

public sealed class BulkRenamePlan
{
    public required string Pattern { get; init; }
    public required BulkPatternKind Kind { get; init; }
    public List<BulkRenameEntry> Entries { get; } = new();
    public bool HasConflicts => Entries.Any(e => e.Conflict is not null);
}

public sealed record BulkRenameEntry(
    long ModId, string OldFolderName, string NewFolderName, bool Enabled, string? Conflict);


public sealed record BulkRenameFailure(long ModId, string AttemptedName, string Reason);

public sealed class BulkRenameResult
{
    public int Succeeded { get; set; }
    public List<BulkRenameFailure> Failures { get; } = new();
    public bool AllSucceeded => Failures.Count == 0;
}
