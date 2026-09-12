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

    private RootNotMappedException()
        : base("Diese Bibliothek ist auf diesem PC keinem Ordner zugeordnet.\n\n" +
               "Öffne Einstellungen und wähle den lokalen Ordner dafür.")
    {
        RootName = "";
    }

    /// <summary>
    /// Guards a path that came out of <c>mo_root_path</c>. That function returns NULL for a
    /// library the current user has not mapped, and every caller then builds a filesystem
    /// path from it - so without this the failure surfaces deep inside Path.Combine as an
    /// ArgumentNullException instead of as the one thing the user can actually fix.
    /// </summary>
    public static string Require(string? rootPath) =>
        string.IsNullOrEmpty(rootPath) ? throw new RootNotMappedException() : rootPath;
}
