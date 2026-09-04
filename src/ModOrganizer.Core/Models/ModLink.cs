namespace ModOrganizer.Core.Models;

public enum ModLinkKind
{
    Other = 0,
    Source = 1,
    Patreon = 2,
    Kofi = 3,
    Twitter = 4,
    Discord = 5,
    Nexus = 6,
    Github = 7,
    Gumroad = 8
}

public sealed class ModLink
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? Domain { get; set; }
    public ModLinkKind Kind { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
