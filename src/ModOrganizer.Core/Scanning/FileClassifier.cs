using ModOrganizer.Core.Models;

namespace ModOrganizer.Core.Scanning;

public static class FileClassifier
{
    public static ModFileKind Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pmp" => ModFileKind.Pmp,
            ".ttmp2" => ModFileKind.Ttmp2,
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif" => ModFileKind.Image,
            ".txt" or ".md" or ".pdf" or ".docx" or ".doc" or ".html" or ".htm" or ".rtf" => ModFileKind.Doc,
            _ => ModFileKind.Other
        };
    }

    public static bool ShouldHash(ModFileKind kind) =>
        kind is ModFileKind.Pmp or ModFileKind.Ttmp2;
}
