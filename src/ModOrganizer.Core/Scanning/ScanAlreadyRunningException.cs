namespace ModOrganizer.Core.Scanning;

/// <summary>
/// Thrown when a scan of the same library is already in progress in this process.
///
/// Two scans of one root do the same work twice and fight over the same rows, and on a
/// large library the loser used to sit in a lock wait until the command timeout fired -
/// surfacing as Npgsql's unhelpful "Exception while reading from stream". Refusing the
/// second scan outright is both faster and explainable.
/// </summary>
public sealed class ScanAlreadyRunningException : InvalidOperationException
{
    public long RootId { get; }

    public ScanAlreadyRunningException(long rootId)
        : base("Für diese Bibliothek läuft bereits ein Scan. " +
               "Warte, bis er fertig ist, und starte ihn dann neu.")
    {
        RootId = rootId;
    }
}
