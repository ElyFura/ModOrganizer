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
    TimeSpan Duration);
