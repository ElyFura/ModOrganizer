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

    private readonly Dictionary<string, List<PenumbraEntry>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PenumbraEntry>> _byNormalized = new(StringComparer.Ordinal);
    private readonly List<(HashSet<string> Tokens, PenumbraEntry Entry)> _byTokens = new();
    private bool _indexed;

    private static void Add(Dictionary<string, List<PenumbraEntry>> index, string key, PenumbraEntry entry)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<PenumbraEntry>();
            index[key] = list;
        }
        if (!list.Contains(entry)) list.Add(entry);
    }

    private void EnsureIndex()
    {
        if (_indexed) return;
        foreach (var e in Entries)
        {
            // Every key maps to a LIST. Users routinely keep several Penumbra copies of the
            // same mod — "[Nimpy] Sphynx revamped" plus "… (2)", or two Aerin variants under
            // different folder names — and those normalize to the same string. Keeping only
            // the first made the other copy unreachable, so a collection that contained only
            // that copy looked empty.
            if (!string.IsNullOrEmpty(e.FolderName)) Add(_byFolder, e.FolderName, e);
            if (!string.IsNullOrEmpty(e.MetaName)) Add(_byFolder, e.MetaName!, e);

            foreach (var key in new[] { e.FolderName, e.MetaName })
            {
                if (string.IsNullOrEmpty(key)) continue;
                var n = PenumbraService.Normalize(key!);
                if (n.Length < 4) continue;
                Add(_byNormalized, n, e);
                var toks = PenumbraService.Tokenize(n);
                if (toks.Count >= 1) _byTokens.Add((toks, e));
            }
        }
        _indexed = true;
    }

    /// <summary>Highest status among all matches; drives the card badge.</summary>
    public PenumbraEntry? Lookup(string name) =>
        LookupAll(name).OrderByDescending(e => (int)e.Status).FirstOrDefault();

    /// <summary>
    /// Every Penumbra entry that plausibly is this library mod. Returning all of them is
    /// what lets the collection filter see a mod whose *other* copy is the enabled one.
    /// </summary>
    public IReadOnlyList<PenumbraEntry> LookupAll(string name)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<PenumbraEntry>();
        EnsureIndex();

        if (_byFolder.TryGetValue(name, out var exact)) return exact;

        var norm = PenumbraService.Normalize(name);
        if (norm.Length < 4) return Array.Empty<PenumbraEntry>();
        if (_byNormalized.TryGetValue(norm, out var n)) return n;

        // Substring (both directions, shorter side ≥ 6).
        var subs = new List<PenumbraEntry>();
        foreach (var (other, entries) in _byNormalized)
        {
            if (Math.Min(norm.Length, other.Length) < 6) continue;
            if (norm.Contains(other) || other.Contains(norm)) subs.AddRange(entries);
        }
        if (subs.Count > 0) return subs.Distinct().ToList();

        // Token-set containment, asymmetric on purpose.
        //
        // query ⊆ entry: the library name is the shorter one. A single token is allowed if
        //   it is distinctive (≥ 4 chars), which is what lets "Anpu" reach Penumbra's
        //   "Anpu Helm - Ears Only".
        //
        // entry ⊆ query: the Penumbra name is the shorter one. Here a single token is NOT
        //   enough — "aerin" alone would otherwise swallow every library mod that merely
        //   mentions Aerin, such as "Lashes and Brows for Aerin PACK".
        var queryTokens = PenumbraService.Tokenize(norm);
        if (queryTokens.Count == 0) return Array.Empty<PenumbraEntry>();

        var querySingleTooShort = queryTokens.Count == 1 && queryTokens.First().Length < 4;

        var toks = new List<PenumbraEntry>();
        foreach (var (tokens, entry) in _byTokens)
        {
            if (tokens.Count == 0) continue;

            if (!querySingleTooShort && queryTokens.IsSubsetOf(tokens)) { toks.Add(entry); continue; }
            if (tokens.Count >= 2 && tokens.IsSubsetOf(queryTokens)) toks.Add(entry);
        }
        return toks.Distinct().ToList();
    }

    /// <summary>All matches for the folder name, falling back to the display name.</summary>
    public IReadOnlyList<PenumbraEntry> MatchesFor(string folderName, string? displayName = null)
    {
        var hits = LookupAll(folderName);
        if (hits.Count > 0) return hits;
        return string.IsNullOrEmpty(displayName)
            ? Array.Empty<PenumbraEntry>()
            : LookupAll(displayName!);
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

    /// <summary>
    /// The folder Penumbra writes its config into. Public so the app can watch it and
    /// re-share the snapshot when mods are toggled while the app is running.
    /// </summary>
    public static string PluginConfigsPath => PluginConfigsRoot;

    private static string MainConfigPath        => Path.Combine(PluginConfigsRoot, "Penumbra.json");

    /// <summary>
    /// Penumbra keeps a backup next to its config and, at least while the game is running,
    /// the main file can be absent for long stretches - leaving only this one. Reading it
    /// costs nothing and is far better than reporting "Penumbra not installed".
    /// </summary>
    private static string MainConfigBackupPath  => Path.Combine(PluginConfigsRoot, "Penumbra.json.bak");
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

    /// <summary>ModDirectory out of a Penumbra config file, or null if unreadable.</summary>
    private static string? ReadModDirectory(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("ModDirectory", out var md) ? md.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public PenumbraSnapshot Read()
    {
        try
        {
            // The main config is only needed for ModDirectory. Everything the other user
            // actually sees - collections and what is enabled in them - lives in the
            // Penumbra subfolder, so a missing main config must not abort the whole read.
            var modDir = ReadModDirectory(MainConfigPath) ?? ReadModDirectory(MainConfigBackupPath);

            var haveModDir = !string.IsNullOrWhiteSpace(modDir) && Directory.Exists(modDir);
            if (!haveModDir && !Directory.Exists(CollectionsDir))
                return new PenumbraSnapshot { IsAvailable = false };

            // --- imported mods (folder + meta.Name) ---
            // Without a ModDirectory we cannot see which mods are *imported*, but the
            // collections below still tell us what is enabled where.
            var entriesByFolder = new Dictionary<string, EntryBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in haveModDir
                         ? Directory.EnumerateDirectories(modDir!)
                         : Enumerable.Empty<string>())
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
