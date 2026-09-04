namespace ModOrganizer.Core.Models;

public enum ActionKind
{
    Rename = 1,
    Move = 2,
    Delete = 3,
    CategoryAdd = 10,
    CategoryRename = 11,
    CategoryMerge = 12,
    CategoryDelete = 13,
    TagAdd = 20,
    TagRemove = 21,
    LinkAdd = 30,
    LinkRemove = 31,
    CommentUpdate = 40
}

public sealed class ActionLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public ActionKind Action { get; set; }
    public long? ModId { get; set; }
    public string? FromPath { get; set; }
    public string? ToPath { get; set; }
    public required string TxId { get; set; }
    public string? PayloadJson { get; set; }
}
