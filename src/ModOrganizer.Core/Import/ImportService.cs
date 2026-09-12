using Dapper;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.Core.Import;

public sealed record ImportCandidate(
    string SourcePath,
    string SuggestedFolderName,
    long? SuggestedCategoryId,
    string? SuggestedCategoryName);

public sealed class ImportService
{
    private readonly DatabaseStore _store;
    private readonly Management.FileSystemActivityGate? _gate;
    private readonly IUserContext? _user;

    public ImportService(DatabaseStore store, Management.FileSystemActivityGate? gate = null,
        IUserContext? user = null)
    {
        _store = store;
        _gate = gate;
        _user = user;
    }

    private static readonly (string Keyword, string Category)[] CategoryHints =
    {
        ("hair",      "Hair"),
        ("face",      "Face"),
        ("makeup",    "Face"),
        ("eyes",      "Face"),
        ("scales",    "Body-Scales-Skin"),
        ("skin",      "Body-Scales-Skin"),
        ("body",      "Body-Scales-Skin"),
        ("tattoo",    "Body-Scales-Skin"),
        ("shoes",     "Shoes"),
        ("heels",     "Shoes"),
        ("boots",     "Shoes"),
        ("ears",      "Ears-Horns-Tail"),
        ("horns",     "Ears-Horns-Tail"),
        ("tail",      "Ears-Horns-Tail"),
        ("ring",      "Accessory"),
        ("necklace",  "Accessory"),
        ("choker",    "Accessory"),
        ("bracelet",  "Accessory"),
        ("earring",   "Accessory"),
        ("vfx",       "VFX"),
        ("sfx",       "Animation-SFX"),
        ("sound",     "Animation-SFX"),
        ("animation", "Animation-SFX"),
        ("pose",      "Animation-SFX"),
        ("housing",   "Housing"),
        ("furniture", "Housing"),
    };

    public ImportCandidate Analyze(string sourcePath, long rootId)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var folderName = Sanitize(fileName);

        using var conn = _store.Open();
        var categories = conn.Query<(long Id, string Name)>(
            "SELECT id AS Id, name AS Name FROM categories WHERE root_id=@r",
            new { r = rootId }).ToList();

        var lowerFile = fileName.ToLowerInvariant();
        string? suggestedCat = null;
        foreach (var (kw, cat) in CategoryHints)
        {
            if (lowerFile.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                suggestedCat = cat;
                break;
            }
        }

        long? catId = null;
        string? catName = null;
        if (suggestedCat is not null)
        {
            var match = categories.FirstOrDefault(c =>
                c.Name.Equals(suggestedCat, StringComparison.OrdinalIgnoreCase));
            if (match.Id > 0) { catId = match.Id; catName = match.Name; }
        }
        if (catId is null)
        {
            var gear = categories.FirstOrDefault(c => c.Name.Equals("Gear", StringComparison.OrdinalIgnoreCase));
            if (gear.Id > 0) { catId = gear.Id; catName = gear.Name; }
        }

        return new ImportCandidate(sourcePath, folderName, catId, catName);
    }

    public string Import(string sourcePath, long categoryId, string folderName, bool copy)
    {
        using var _suppress = _gate?.Suppress();
        using var conn = _store.Open();
        var row = conn.QuerySingle<(string RootPath, string CatName)>(
            """
            SELECT mo_root_path(c.root_id, @uid) AS RootPath, c.name AS CatName
            FROM categories c WHERE c.id=@id
            """, new { id = categoryId, uid = _user?.UserId });

        var destFolder = Path.Combine(row.RootPath, row.CatName, folderName);
        Directory.CreateDirectory(destFolder);

        var destFile = Path.Combine(destFolder, Path.GetFileName(sourcePath));
        int i = 2;
        while (File.Exists(destFile))
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            destFile = Path.Combine(destFolder, $"{baseName} ({i++}){ext}");
        }

        if (copy) File.Copy(sourcePath, destFile);
        else File.Move(sourcePath, destFile);

        return destFolder;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Imported Mod" : cleaned;
    }
}
