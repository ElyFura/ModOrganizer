using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ModOrganizer.App.Services;

/// <summary>
/// Persists Supabase tokens (access + refresh) DPAPI-encrypted under the current Windows user.
/// </summary>
public sealed class TokenStore
{
    private readonly string _path;

    public TokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer", "token.dat");
    }

    public void Save(string accessToken, string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var payload = $"{accessToken}\n{refreshToken}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var protect = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protect);
    }

    public (string? Access, string? Refresh) TryLoad()
    {
        if (!File.Exists(_path)) return (null, null);
        try
        {
            var protect = File.ReadAllBytes(_path);
            var bytes = ProtectedData.Unprotect(protect, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(bytes);
            var parts = s.Split('\n', 2);
            if (parts.Length != 2) return (null, null);
            return (parts[0], parts[1]);
        }
        catch
        {
            try { File.Delete(_path); } catch { }
            return (null, null);
        }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
