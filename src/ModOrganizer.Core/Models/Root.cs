namespace ModOrganizer.Core.Models;

public sealed class Root
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
