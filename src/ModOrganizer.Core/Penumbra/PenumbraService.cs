using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModOrganizer.Core.Penumbra;

public enum PenumbraStatus
{
    NotInstalled = 0,
    Imported = 1,
    ActiveDefault = 2  // active in at least one currently-active collection
}

public sealed class PenumbraCollection
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"" if not currently active; otherwise one of "default", "interface", "current", or "individual:&lt;display&gt;".</summary>
    public string Role { get; init; } = "";
    public bool IsActive => !string.IsNullOrEmpty(Role);
}

public sealed class PenumbraEntry
{
    public string FolderName { get; init; } = "";
    public string? MetaName { get; init; }
    public IReadOnlyList<string> ActiveInCollections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllInCollections { get; init; } = Array.Empty<string>();
    public PenumbraStatus Status =>
        ActiveInCollections.Count > 0 ? PenumbraStatus.ActiveDefault : PenumbraStatus.Imported;
}

public sealed class PenumbraSnapshot
{
    public bool IsAvailable { get; init; }
    public string? ModDirectory { get; init; }
    public IReadOnlyList<PenumbraCollection> Collections { get; init; } = Array.Empty<PenumbraCollection>();
    public IReadOnlyList<PenumbraEntry> Entries { get; init; } = Array.Empty<PenumbraEntry>();

    private readonly Dictionary<string, PenumbraEntry> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PenumbraEntry> _byNormalized = new(StringComparer.Ordinal);
    private readonly List<(HashSet<string> Tokens, PenumbraEntry Entry)> _byTokens = new();
    private bool _indexed;

    private void EnsureIndex()
    {
        if (_indexed) return;
        foreach (var e in Entries)
        {
            if (!string.IsNullOrEmpty(e.FolderName)) _byFolder[e.FolderName] = e;
            if (!string.IsNullOrEmpty(e.MetaName)) _byFolder.TryAdd(e.MetaName!, e);

            foreach (var key in new[] { e.FolderName, e.MetaName })
            {
                if (string.IsNullOrEmpty(key)) continue;
                var n = PenumbraService.Normalize(key!);
                if (n.Length < 4) continue;
                _byNormalized.TryAdd(n, e);
                var toks = PenumbraService.Tokenize(n);
                if (toks.Count >= 2) _byTokens.Add((toks, e));
            }
        }
        _indexed = true;
    }

    public PenumbraEntry? Lookup(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureIndex();

        if (_byFolder.TryGetValue(name, out var exact)) return exact;

        var norm = PenumbraService.Normalize(name);
        if (norm.Length < 4) return null;
        if (_byNormalized.TryGetValue(norm, out var n)) return n;

        // Substring (both directions, shorter side ≥ 6).
        PenumbraEntry? bestSub = null;
        foreach (var (other, entry) in _byNormalized)
        {
            if (Math.Min(norm.Length, other.Length) < 6) continue;
            if (norm.Contains(other) || other.Contains(norm))
            {
                if (bestSub is null || (int)entry.Status > (int)bestSub.Status) bestSub = entry;
            }
        }
        if (bestSub is not null) return bestSub;

        // Token-set fallback (≥ 2 tokens of length ≥ 3).
        var queryTokens = PenumbraService.Tokenize(norm);
        if (queryTokens.Count < 2) return null;

        PenumbraEntry? bestTok = null;
        foreach (var (tokens, entry) in _byTokens)
        {
            if (queryTokens.IsSubsetOf(tokens) || tokens.IsSubsetOf(queryTokens))
                if (bestTok is null || (int)entry.Status > (int)bestTok.Status) bestTok = entry;
        }
        return bestTok;
    }

    public PenumbraStatus StatusFor(string folderName, string? displayName = null)
    {
        var e = Lookup(folderName);
        if (e is null && !string.IsNullOrEmpty(displayName)) e = Lookup(displayName!);
        return e?.Status ?? PenumbraStatus.NotInstalled;
    }
}

/// <summary>
/// Read-only inspection of the local Penumbra plugin install. Never writes.
/// Layout (XIVLauncher/Dalamud):
///   %APPDATA%\XIVLauncher\pluginConfigs\Penumbra.json                    — main config (ModDirectory)
///   %APPDATA%\XIVLauncher\pluginConfigs\Penumbra\active_collections.json — Default / Interface / Current / Individuals[]
///   %APPDATA%\XIVLauncher\pluginConfigs\Penumbra\collections\&lt;guid&gt;.json — Settings dict keyed by mod folder name
/// </summary>
public sealed class PenumbraService
{
    private static string PluginConfigsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs");

    private static string MainConfigPath        => Path.Combine(PluginConfigsRoot, "Penumbra.json");
    private static string PenumbraSubdir        => Path.Combine(PluginConfigsRoot, "Penumbra");
    private static string ActiveCollectionsPath => Path.Combine(PenumbraSubdir, "active_collections.json");
    private static string CollectionsDir        => Path.Combine(PenumbraSubdir, "collections");

    private static readonly Regex BracketChunk = new(@"\[[^\]]*\]|\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex VersionToken = new(@"\bv?\d+(\.\d+)*[a-z]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace   = new(@"\s+", RegexOptions.Compiled);

    public static HashSet<string> Tokenize(string normalized) =>
        new(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3), StringComparer.Ordinal);

    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = BracketChunk.Replace(s, " ");
        t = VersionToken.Replace(t, " ");
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else sb.Append(' ');
        }
        return Whitespace.Replace(sb.ToString(), " ").Trim();
    }

    public PenumbraSnapshot Read()
    {
        try
        {
            if (!File.Exists(MainConfigPath))
                return new PenumbraSnapshot { IsAvailable = false };

            string? modDir;
            using (var doc = JsonDocument.Parse(File.ReadAllText(MainConfigPath)))
            {
                modDir = doc.RootElement.TryGetProperty("ModDirectory", out var md) ? md.GetString() : null;
            }
            if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
                return new PenumbraSnapshot { IsAvailable = false };

            // --- imported mods (folder + meta.Name) ---
            var entriesByFolder = new Dictionary<string, EntryBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(modDir))
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) continue;
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(folderName)) continue;

                var b = new EntryBuilder { FolderName = folderName };
                try
                {
                    using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                    if (meta.RootElement.TryGetProperty("Name", out var nm))
                        b.MetaName = nm.GetString();
                }
                catch { /* meta unreadable */ }
                entriesByFolder[folderName] = b;
            }

            // --- which collections are currently active, and what is each one's role ---
            var roleByCollectionId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(ActiveCollectionsPath))
            {
                try
                {
                    using var ac = JsonDocument.Parse(File.ReadAllText(ActiveCollectionsPath));
                    var root = ac.RootElement;
                    if (root.TryGetProperty("Default", out var def) && def.GetString() is { } d)
                        roleByCollectionId[d] = "default";
                    if (root.TryGetProperty("Interface", out var iface) && iface.GetString() is { } i)
                        roleByCollectionId.TryAdd(i, "interface");
                    if (root.TryGetProperty("Current", out var cur) && cur.GetString() is { } c)
                        roleByCollectionId.TryAdd(c, "current");
                    if (root.TryGetProperty("Individuals", out var inds) && inds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ind in inds.EnumerateArray())
                        {
                            var id = ind.TryGetProperty("Collection", out var col) ? col.GetString() : null;
                            var disp = ind.TryGetProperty("Display", out var dd) ? dd.GetString() : null;
                            if (string.IsNullOrEmpty(id)) continue;
                            roleByCollectionId.TryAdd(id, "individual:" + (disp ?? "?"));
                        }
                    }
                }
                catch { /* ignore malformed */ }
            }

            // --- iterate every collection file, attach enabled mods to entries ---
            var collections = new List<PenumbraCollection>();
            if (Directory.Exists(CollectionsDir))
            {
                foreach (var collectionPath in Directory.EnumerateFiles(CollectionsDir, "*.json"))
                {
                    try
                    {
                        using var cdoc = JsonDocument.Parse(File.ReadAllText(collectionPath));
                        var root = cdoc.RootElement;
                        var id = root.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                        var name = root.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                        var role = roleByCollectionId.TryGetValue(id!, out var r) ? r : "";
                        collections.Add(new PenumbraCollection { Id = id!, Name = name!, Role = role });

                        if (!root.TryGetProperty("Settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                            continue;

                        foreach (var prop in settings.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                            if (!prop.Value.TryGetProperty("Enabled", out var en) || en.ValueKind != JsonValueKind.True)
                                continue;
                            if (!entriesByFolder.TryGetValue(prop.Name, out var entry)) continue;

                            entry.AllIn.Add(name!);
                            if (!string.IsNullOrEmpty(role)) entry.ActiveIn.Add(name!);
                        }
                    }
                    catch { /* skip malformed */ }
                }
            }

            var entries = entriesByFolder.Values
                .Select(b => new PenumbraEntry
                {
                    FolderName = b.FolderName,
                    MetaName = b.MetaName,
                    ActiveInCollections = b.ActiveIn.Distinct().OrderBy(s => s).ToList(),
                    AllInCollections = b.AllIn.Distinct().OrderBy(s => s).ToList()
                })
                .ToList();

            return new PenumbraSnapshot
            {
                IsAvailable = true,
                ModDirectory = modDir,
                Collections = collections,
                Entries = entries
            };
        }
        catch
        {
            return new PenumbraSnapshot { IsAvailable = false };
        }
    }

    private sealed class EntryBuilder
    {
        public string FolderName { get; set; } = "";
        public string? MetaName { get; set; }
        public HashSet<string> ActiveIn { get; } = new();
        public HashSet<string> AllIn { get; } = new();
    }
}
