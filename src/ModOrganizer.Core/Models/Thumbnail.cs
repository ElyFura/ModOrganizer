namespace ModOrganizer.Core.Models;

public sealed class Thumbnail
{
    public long ModFileId { get; set; }
    public required string CachePath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
