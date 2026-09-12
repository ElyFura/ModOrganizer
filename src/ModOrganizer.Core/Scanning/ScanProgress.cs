namespace ModOrganizer.Core.Scanning;

public sealed record ScanProgress(
    string CurrentCategory,
    string? CurrentMod,
    int ModsDone,
    int ModsTotal,
    int FilesDone);

public sealed record ScanSummary(
    int CategoriesSeen,
    int ModsSeen,
    int FilesSeen,
    int HashesComputed,
    int CategoriesMarkedMissing,
    int ModsMarkedMissing,
    TimeSpan Duration,

    /// <summary>Mods in the database that this scan did not find on disk.</summary>
    int ModsNotFound = 0,

    /// <summary>
    /// True when so much of the library was absent that flagging was refused — almost
    /// always a sync that has not finished, not a deletion.
    /// </summary>
    bool MissingMarkSkipped = false);
