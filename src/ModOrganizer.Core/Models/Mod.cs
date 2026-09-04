namespace ModOrganizer.Core.Models;

public sealed class Mod
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public required string FolderName { get; set; }
    public string? DisplayName { get; set; }
    public string? CommentMd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsMissing { get; set; }
}
