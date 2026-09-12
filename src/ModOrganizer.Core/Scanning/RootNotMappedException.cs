namespace ModOrganizer.Core.Scanning;

/// <summary>
/// The current user has not told the app where this root lives on their machine.
///
/// Distinct from "the folder is gone": a root is shared between users, but each user
/// mounts it somewhere else (Nextcloud sync on different PCs). A second user seeing a
/// root for the first time simply has no mapping yet, which is normal and fixable in
/// Settings — not an error worth a stack trace.
/// </summary>
public sealed class RootNotMappedException : Exception
{
    public long RootId { get; }
    public string RootName { get; }

    public RootNotMappedException(long rootId, string rootName)
        : base($"Für „{rootName}\" ist auf diesem PC kein Ordner zugeordnet.\n\n" +
               "Öffne Einstellungen und wähle den lokalen Ordner für diese Bibliothek.")
    {
        RootId = rootId;
        RootName = rootName;
    }
}
