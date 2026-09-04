namespace ModOrganizer.Core.Models;

public enum HealthIssueKind
{
    NoImage = 1,
    MultiImage = 2,
    BrokenImage = 3,
    OrphanImage = 4
}

public enum HealthSeverity
{
    Info = 0,
    Warn = 1,
    Error = 2
}

public sealed class HealthIssue
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public HealthIssueKind Kind { get; set; }
    public HealthSeverity Severity { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
