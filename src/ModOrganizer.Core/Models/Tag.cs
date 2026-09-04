namespace ModOrganizer.Core.Models;

public sealed class Tag
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? ColorHex { get; set; }
    public string? Description { get; set; }
}
