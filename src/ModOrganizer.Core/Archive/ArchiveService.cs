using System.IO.Compression;

namespace ModOrganizer.Core.Archive;

/// <summary>
/// Zips a mod folder into a date-stamped archive directory before destructive
/// operations. Independent of Recycle Bin so survives bin emptying.
/// </summary>
public sealed class ArchiveService
{
    private readonly string _archiveRoot;

    public ArchiveService(string? archiveRoot = null)
    {
        _archiveRoot = archiveRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer", "archive");
    }

    public string ArchiveRoot => _archiveRoot;

    public string ArchiveFolder(string sourceFolderPath)
    {
        if (!Directory.Exists(sourceFolderPath))
            throw new DirectoryNotFoundException(sourceFolderPath);

        var folderName = Path.GetFileName(sourceFolderPath.TrimEnd(Path.DirectorySeparatorChar));
        var dateDir = Path.Combine(_archiveRoot, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);

        var dest = Path.Combine(dateDir, $"{folderName}.zip");
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(dateDir, $"{folderName} ({i}).zip");

        ZipFile.CreateFromDirectory(sourceFolderPath, dest, CompressionLevel.Optimal, includeBaseDirectory: true);
        return dest;
    }

    public IReadOnlyList<ArchiveEntry> List()
    {
        var result = new List<ArchiveEntry>();
        if (!Directory.Exists(_archiveRoot)) return result;

        foreach (var dateDir in Directory.EnumerateDirectories(_archiveRoot).OrderByDescending(d => d))
        {
            foreach (var file in Directory.EnumerateFiles(dateDir, "*.zip"))
            {
                var fi = new FileInfo(file);
                result.Add(new ArchiveEntry(
                    fi.FullName,
                    Path.GetFileNameWithoutExtension(fi.Name),
                    fi.Length,
                    fi.CreationTimeUtc));
            }
        }
        return result;
    }

    public void Restore(string zipPath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        ZipFile.ExtractToDirectory(zipPath, destinationFolder, overwriteFiles: false);
    }
}

public sealed record ArchiveEntry(string ZipPath, string ModName, long SizeBytes, DateTime CreatedUtc);
