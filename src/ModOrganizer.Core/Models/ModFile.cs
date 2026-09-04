namespace ModOrganizer.Core.Models;

public enum ModFileKind
{
    Other = 0,
    Pmp = 1,
    Ttmp2 = 2,
    Image = 3,
    Doc = 4
}

public sealed class ModFile
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public required string RelativePath { get; set; }
    public ModFileKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public long? XxHash64 { get; set; }
    public DateTimeOffset Mtime { get; set; }
}
