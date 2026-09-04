using System.Collections.Concurrent;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModOrganizer.App.Services;

/// <summary>Decode sizes we cache. Fixed tiers so the size slider does not thrash the cache.</summary>
public enum ThumbTier
{
    /// <summary>Folder-tree rows — 36 px on screen.</summary>
    Tiny = 0,
    /// <summary>Gallery cards — the size slider tops out at 440 px.</summary>
    Card = 1,
    /// <summary>Detail-view hero image.</summary>
    Large = 2
}

/// <summary>
/// Decodes preview images off the UI thread and caches them twice: in memory for the
/// session, and on disk as small JPEGs so a restart does not re-decode multi-megabyte
/// originals.
///
/// The old ThumbnailLoader decoded every image synchronously inside the ModCardViewModel
/// constructor at a fixed 512 px — 300 mods meant 300 blocking decodes per refresh, plus
/// 300 more for the folder tree, all at 512 px even for 36 px tiles.
/// </summary>
public sealed class ThumbnailCache
{
    private static int WidthFor(ThumbTier tier) => tier switch
    {
        ThumbTier.Tiny => 64,
        ThumbTier.Card => 448,
        _ => 1400
    };

    /// <summary>Above this we keep the disk cache; below it decoding the original is cheap enough.</summary>
    private const long DiskCacheMinSourceBytes = 256 * 1024;

    /// <summary>
    /// How many decoded bitmaps we keep per tier. Card bitmaps are ~600 KB each, so an
    /// unbounded cache would grow to ~190 MB after scrolling a 300-mod library once.
    /// Evicted entries stay on the disk cache, which makes re-loading them cheap.
    /// </summary>
    private static int MemoryBudgetFor(ThumbTier tier) => tier switch
    {
        ThumbTier.Tiny => 1500,
        ThumbTier.Card => 150,
        _ => 8
    };

    private readonly ConcurrentDictionary<string, ImageSource> _memory = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new();

    /// <summary>Per-tier LRU recency, newest last. Guarded by <see cref="_lruLock"/>.</summary>
    private readonly Dictionary<ThumbTier, LinkedList<string>> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    private readonly object _lruLock = new();

    /// <summary>Bounds concurrent decodes so a refresh does not saturate the disk or the CPU.</summary>
    private readonly SemaphoreSlim _decodeLimit =
        new(Math.Max(2, Math.Min(6, Environment.ProcessorCount / 2)));

    private readonly string _diskRoot;

    public ThumbnailCache()
    {
        _diskRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer", "thumbs");
        try { Directory.CreateDirectory(_diskRoot); } catch { /* cache is optional */ }
    }

    /// <summary>
    /// Returns the already-decoded image, or null. Never touches the disk — safe to call
    /// from a data-binding getter or an item-container realization.
    /// </summary>
    public ImageSource? PeekMemory(string? absolutePath, ThumbTier tier)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        if (!_memory.TryGetValue(MemoryKey(absolutePath, tier), out var img)) return null;
        Touch(MemoryKey(absolutePath, tier), tier);
        return img;
    }

    /// <summary>
    /// Decodes (or loads from cache) on a background thread. Concurrent callers for the
    /// same key share one decode. The returned ImageSource is frozen, so it is safe to
    /// hand to the UI thread.
    /// </summary>
    public Task<ImageSource?> GetAsync(string? absolutePath, ThumbTier tier)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Task.FromResult<ImageSource?>(null);

        var key = MemoryKey(absolutePath, tier);
        if (_memory.TryGetValue(key, out var cached))
        {
            Touch(key, tier);
            return Task.FromResult<ImageSource?>(cached);
        }

        return _inFlight.GetOrAdd(key, cacheKey => Task.Run(async () =>
        {
            await _decodeLimit.WaitAsync().ConfigureAwait(false);
            try
            {
                var img = LoadCore(absolutePath, tier);
                if (img is not null)
                {
                    _memory[key] = img;
                    Touch(key, tier);
                }
                return img;
            }
            finally
            {
                _decodeLimit.Release();
                _inFlight.TryRemove(cacheKey, out _);
            }
        }));
    }

    /// <summary>Drops memoized entries for a path — call after the file on disk changed.</summary>
    public void Invalidate(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        foreach (var tier in Enum.GetValues<ThumbTier>())
        {
            var key = MemoryKey(absolutePath, tier);
            _memory.TryRemove(key, out _);
            lock (_lruLock)
            {
                if (_lruNodes.Remove(key, out var node) && _lru.TryGetValue(tier, out var list))
                    list.Remove(node);
            }
        }
    }

    public void ClearMemory()
    {
        _memory.Clear();
        lock (_lruLock)
        {
            _lru.Clear();
            _lruNodes.Clear();
        }
    }

    /// <summary>Marks a key as most-recently-used and evicts past the tier budget.</summary>
    private void Touch(string key, ThumbTier tier)
    {
        List<string>? evict = null;

        lock (_lruLock)
        {
            if (!_lru.TryGetValue(tier, out var list))
            {
                list = new LinkedList<string>();
                _lru[tier] = list;
            }

            if (_lruNodes.TryGetValue(key, out var existing))
            {
                list.Remove(existing);
                list.AddLast(existing);
            }
            else
            {
                _lruNodes[key] = list.AddLast(key);
            }

            var budget = MemoryBudgetFor(tier);
            while (list.Count > budget && list.First is { } oldest)
            {
                list.RemoveFirst();
                _lruNodes.Remove(oldest.Value);
                (evict ??= new List<string>()).Add(oldest.Value);
            }
        }

        if (evict is null) return;
        foreach (var k in evict) _memory.TryRemove(k, out _);
    }

    private static string MemoryKey(string path, ThumbTier tier) => $"{tier}|{path}";

    private ImageSource? LoadCore(string path, ThumbTier tier)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists) return null;
        }
        catch { return null; }

        var width = WidthFor(tier);

        // Small originals: decoding straight from the file beats a disk-cache round trip.
        var useDisk = fi.Length >= DiskCacheMinSourceBytes && tier != ThumbTier.Large;
        string? cachePath = null;

        if (useDisk)
        {
            cachePath = DiskCachePath(path, fi, tier);
            var fromDisk = TryDecode(cachePath, width);
            if (fromDisk is not null) return fromDisk;
        }

        var decoded = TryDecode(path, width);
        if (decoded is null) return null;

        if (useDisk && cachePath is not null)
            TryWriteDiskCache(cachePath, decoded);

        return decoded;
    }

    private static ImageSource? TryDecode(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // Read into memory first: BitmapImage with a UriSource keeps a file handle and
            // an internal WPF-wide cache we cannot control, which is what made repeated
            // refreshes so expensive.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize: 65536, FileOptions.SequentialScan);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void TryWriteDiskCache(string cachePath, ImageSource decoded)
    {
        if (decoded is not BitmapSource bs) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(bs));

            // Write to a temp file and move into place so a crash cannot leave a
            // half-written cache entry that then decodes as garbage.
            var tmp = cachePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(fs);
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch { /* cache is optional */ }
    }

    /// <summary>
    /// Cache key covers path + size + mtime + tier, so editing or replacing a preview
    /// image invalidates its entry on its own.
    /// </summary>
    private string DiskCachePath(string path, FileInfo fi, ThumbTier tier)
    {
        var material = $"{path.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{WidthFor(tier)}";
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(material));
        var name = hash.ToString("x16");
        // Shard by first byte — a few hundred mods is fine flat, but this keeps the
        // directory usable if the library grows into the thousands.
        return Path.Combine(_diskRoot, name.Substring(0, 2), name + ".jpg");
    }
}
