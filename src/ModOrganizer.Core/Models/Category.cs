namespace ModOrganizer.Core.Models;

public sealed class Category
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public string? IconName { get; set; }
    public string? ColorHex { get; set; }
    public string? Description { get; set; }
    public bool IsMissing { get; set; }
}
