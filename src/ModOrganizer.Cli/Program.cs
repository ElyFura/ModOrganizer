using System.Diagnostics;
using Dapper;
using ModOrganizer.Core.Health;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;
using ModOrganizer.Core.Storage;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  ModOrganizer.Cli scan   <root-path> <connection-string>");
    Console.WriteLine("  ModOrganizer.Cli verify   <connection-string>");
    Console.WriteLine("  ModOrganizer.Cli selftest <connection-string>");
    Console.WriteLine("  ModOrganizer.Cli tag      <connection-string> <tag> [<mod-substring>...]");
    Console.WriteLine("  ModOrganizer.Cli untag    <connection-string> <tag> <mod-substring>...");
    Console.WriteLine();
    Console.WriteLine("  scan    Scan a root folder into the database");
    Console.WriteLine("  verify    Apply migrations and time every hot query (read-only apart");
    Console.WriteLine("            from health_issues, which is derived data)");
    Console.WriteLine("  selftest  Round-trip add/rename/delete on a throwaway category and");
    Console.WriteLine("            clean up after itself; never touches existing categories");
    return 0;
}

if (args[0] == "verify")
{
    if (args.Length < 2) { Console.Error.WriteLine("verify needs a connection string"); return 2; }
    return Verify(args[1]);
}

if (args[0] == "selftest")
{
    if (args.Length < 2) { Console.Error.WriteLine("selftest needs a connection string"); return 2; }
    return SelfTest(args[1]);
}

if (args[0] == "tag")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("tag needs <connection-string> <tag-name> [mod-name-substring...]");
        return 2;
    }
    // With no substrings it lists the tag's current carriers instead of assigning.
    return args.Length == 3
        ? ListTagged(args[1], args[2])
        : ApplyTag(args[1], args[2], args.Skip(3).ToArray(), remove: false);
}

if (args[0] == "penumbra")
{
    if (args.Length < 2) { Console.Error.WriteLine("penumbra needs a connection string [collection name]"); return 2; }
    if (args.Length > 3 && args[2] == "--probe") return PenumbraProbe(args.Skip(3).ToArray());
    return PenumbraReport(args[1], args.Length > 2 ? args[2] : null);
}

if (args[0] == "untag")
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("untag needs <connection-string> <tag-name> <mod-name-substring> [more...]");
        return 2;
    }
    return ApplyTag(args[1], args[2], args.Skip(3).ToArray(), remove: true);
}

var scanArgs = args[0] == "scan" ? args.Skip(1).ToArray() : args;
if (scanArgs.Length < 2)
{
    Console.Error.WriteLine("scan needs <root-path> <connection-string>");
    return 2;
}

// TrimEnd first: GetFullPath keeps a trailing separator, which makes GetFileName
// below return "" and would store a root with a blank display name.
var rootPath = Path.GetFullPath(scanArgs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
var connectionString = scanArgs[1];

if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"ERROR: root path does not exist: {rootPath}");
    return 2;
}

Console.WriteLine($"Root: {rootPath}");
Console.WriteLine();

var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
store.Initialize();

long rootId;
using (var conn = store.Open())
{
    var existing = conn.QuerySingleOrDefault<long?>(
        "SELECT id FROM roots WHERE path=@p", new { p = rootPath });
    if (existing is null)
    {
        rootId = conn.ExecuteScalar<long>(
            """
            INSERT INTO roots(path, display_name, enabled, added_at)
            VALUES (@p, @n, TRUE, @t)
            RETURNING id
            """,
            new { p = rootPath, n = Path.GetFileName(rootPath), t = DateTimeOffset.UtcNow.ToString("o") });
        Console.WriteLine($"Added new root #{rootId}");
    }
    else
    {
        rootId = existing.Value;
        Console.WriteLine($"Using existing root #{rootId}");
    }
}

var scanner = new ModScanner(store);

var lastReport = DateTime.UtcNow;
var progress = new Progress<ScanProgress>(p =>
{
    if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 100) return;
    lastReport = DateTime.UtcNow;
    Console.Write($"\r  {p.CurrentCategory,-20} {p.ModsDone,4}/{p.ModsTotal,-4}  files:{p.FilesDone,-6} {p.CurrentMod}                ");
});

var summary = scanner.Scan(rootId, progress);
Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"Scan done in {summary.Duration.TotalSeconds:F1}s");
Console.WriteLine($"  Categories      : {summary.CategoriesSeen}");
Console.WriteLine($"  Mods            : {summary.ModsSeen}");
Console.WriteLine($"  Files           : {summary.FilesSeen}");
Console.WriteLine($"  Hashes computed : {summary.HashesComputed}");
Console.WriteLine($"  Missing cats    : {summary.CategoriesMarkedMissing}");
Console.WriteLine($"  Missing mods    : {summary.ModsMarkedMissing}");

return 0;


static int Verify(string connectionString)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));

    Console.WriteLine("== migrations ==");
    var sw = Stopwatch.StartNew();
    store.Initialize();
    Console.WriteLine($"  applied, schema v{store.SchemaVersion()}  ({sw.ElapsedMilliseconds} ms)");

    var library = new ModLibraryService(store);
    var detail = new ModDetailQuery(store);
    var health = new HealthChecker(store);
    var stats = new StatsService(store);

    Console.WriteLine();
    Console.WriteLine("== roots ==");
    sw.Restart();
    var roots = library.GetRoots();
    Console.WriteLine($"  {roots.Count} root(s)  ({sw.ElapsedMilliseconds} ms)");

    var failures = 0;

    foreach (var root in roots)
    {
        Console.WriteLine();
        Console.WriteLine($"== root #{root.Id} {root.DisplayName} ==");

        sw.Restart();
        var categories = library.GetCategories(root.Id);
        Console.WriteLine($"  GetCategories      : {categories.Count,4} rows  {sw.ElapsedMilliseconds,5} ms");

        // Every sort order compiles a different ORDER BY - exercise them all.
        foreach (var sort in Enum.GetValues<ModSort>())
        {
            sw.Restart();
            try
            {
                var mods = library.GetMods(root.Id, sort: sort);
                Console.WriteLine($"  GetMods/{sort,-16}: {mods.Count,4} rows  {sw.ElapsedMilliseconds,5} ms");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  GetMods/{sort,-16}: FAILED - {ex.Message}");
            }
        }

        // Case-insensitive search used to be a case-sensitive LIKE.
        sw.Restart();
        var lower = library.GetMods(root.Id, searchText: "a");
        var upper = library.GetMods(root.Id, searchText: "A");
        Console.WriteLine($"  search 'a' vs 'A'  : {lower.Count} / {upper.Count} rows  {sw.ElapsedMilliseconds,5} ms" +
                          (lower.Count == upper.Count ? "  (case-insensitive OK)" : "  <-- MISMATCH"));
        if (lower.Count != upper.Count) failures++;

        // Wildcards the user types must be literal: '_' is ILIKE's any-single-char.
        var wildcard = library.GetMods(root.Id, searchText: "_");
        var percent = library.GetMods(root.Id, searchText: "%");
        var total = library.GetMods(root.Id).Count;
        Console.WriteLine($"  search '_' literal : {wildcard.Count,4} rows (of {total})" +
                          (wildcard.Count < total ? "  OK" : "  <-- WILDCARD NOT ESCAPED"));
        Console.WriteLine($"  search '%' literal : {percent.Count,4} rows (of {total})" +
                          (percent.Count < total ? "  OK" : "  <-- WILDCARD NOT ESCAPED"));
        if (wildcard.Count >= total || percent.Count >= total) failures++;

        sw.Restart();
        var rated = library.GetMods(root.Id, minRating: 3);
        Console.WriteLine($"  minRating=3        : {rated.Count,4} rows  {sw.ElapsedMilliseconds,5} ms");

        if (categories.Count > 0)
        {
            sw.Restart();
            var inCat = library.GetMods(root.Id, categoryId: categories[0].Id);
            Console.WriteLine($"  category filter    : {inCat.Count,4} rows  {sw.ElapsedMilliseconds,5} ms");
        }

        var all = library.GetMods(root.Id);
        if (all.Count > 0)
        {
            // Pick the mod with the most files - the worst case for the detail query.
            var sample = all.OrderByDescending(m => m.PmpCount).First();
            sw.Restart();
            try
            {
                var snapshot = detail.LoadAsync(sample.Id).GetAwaiter().GetResult();
                Console.WriteLine($"  ModDetailQuery     : '{sample.FolderName}' " +
                                  $"{snapshot?.Files.Count ?? 0} files, {snapshot?.PmpDetails.Count ?? 0} pmp, " +
                                  $"{snapshot?.Tags.Count ?? 0} tags, {snapshot?.Links.Count ?? 0} links  " +
                                  $"{sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  ModDetailQuery     : FAILED - {ex.Message}");
            }
        }

        sw.Restart();
        try
        {
            var created = health.Run(root.Id);
            Console.WriteLine($"  HealthChecker.Run  : {created,4} issues  {sw.ElapsedMilliseconds,5} ms");
            sw.Restart();
            var issues = health.GetIssues(root.Id);
            Console.WriteLine($"  HealthChecker.Get  : {issues.Count,4} rows  {sw.ElapsedMilliseconds,5} ms");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  HealthChecker      : FAILED - {ex.Message}");
        }

        // Reorder in its current order: renumbers sort_order to 1..N without changing
        // the visible ordering, and proves the batched UPDATE ... FROM unnest is right.
        try
        {
            var before = categories.Select(c => c.Id).ToList();
            sw.Restart();
            new ModOrganizer.Core.Categories.CategoryService(store).Reorder(root.Id, before);
            var elapsed = sw.ElapsedMilliseconds;
            var after = library.GetCategories(root.Id).Select(c => c.Id).ToList();
            var same = before.SequenceEqual(after);
            Console.WriteLine($"  Reorder (no-op)    : {before.Count,4} cats  {elapsed,5} ms" +
                              (same ? "  order preserved OK" : "  <-- ORDER CHANGED"));
            if (!same) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  Reorder            : FAILED - {ex.Message}");
        }

        sw.Restart();
        try
        {
            var s = stats.Compute(root.Id);
            Console.WriteLine($"  StatsService       : {s.TotalMods} mods, {s.TotalFiles} files  {sw.ElapsedMilliseconds,5} ms");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  StatsService       : FAILED - {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("== integrity ==");
    using (var conn = store.Open())
    {
        var row = conn.QuerySingle<(long Mods, long Files, long Hashable, long Hashed, long OrphanFiles, long DupFiles)>(
            """
            SELECT
              (SELECT COUNT(*) FROM mods)                                            AS Mods,
              (SELECT COUNT(*) FROM mod_files)                                       AS Files,
              (SELECT COUNT(*) FROM mod_files WHERE kind IN (1,2))                   AS Hashable,
              (SELECT COUNT(*) FROM mod_files WHERE kind IN (1,2) AND xxhash64 IS NOT NULL) AS Hashed,
              (SELECT COUNT(*) FROM mod_files f
                 LEFT JOIN mods m ON m.id = f.mod_id WHERE m.id IS NULL)             AS OrphanFiles,
              (SELECT COUNT(*) FROM (
                 SELECT mod_id, relative_path FROM mod_files
                 GROUP BY mod_id, relative_path HAVING COUNT(*) > 1) d)              AS DupFiles
            """);

        Console.WriteLine($"  mods                    : {row.Mods}");
        Console.WriteLine($"  mod_files               : {row.Files}");

        // Informational, not a failure: a root that has never been scanned has no hashes
        // yet, and the scanner deliberately leaves a null hash for an unreadable file.
        var missing = row.Hashable - row.Hashed;
        Console.WriteLine($"  pmp/ttmp2 with a hash   : {row.Hashed}/{row.Hashable}" +
                          (missing == 0 ? "  OK" : $"  ({missing} not hashed yet - scan to fill)"));

        // These two are real corruption: the scanner must never produce either.
        Console.WriteLine($"  orphaned file rows      : {row.OrphanFiles}" + (row.OrphanFiles == 0 ? "  OK" : "  <-- BAD"));
        Console.WriteLine($"  duplicate (mod,path)    : {row.DupFiles}" + (row.DupFiles == 0 ? "  OK" : "  <-- BAD"));

        if (row.OrphanFiles != 0) failures++;
        if (row.DupFiles != 0) failures++;
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL QUERIES OK" : $"{failures} QUERY FAILURE(S)");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Exercises the category lifecycle against the real database and disk using a
/// uniquely-named throwaway category, then removes every trace of it. Existing
/// categories and mods are never touched.
/// </summary>
static int SelfTest(string connectionString)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    store.Initialize();

    var gate = new ModOrganizer.Core.Management.FileSystemActivityGate();
    var catSvc = new ModOrganizer.Core.Categories.CategoryService(store, gate);
    var library = new ModLibraryService(store);

    var roots = library.GetRoots();
    if (roots.Count == 0) { Console.Error.WriteLine("no roots configured"); return 2; }

    var root = roots[0];
    Console.WriteLine($"Root #{root.Id} {root.DisplayName}  ({root.Path})");
    Console.WriteLine();

    var stamp = Guid.NewGuid().ToString("N")[..6];
    var name = "_selftest_" + stamp;
    var failures = 0;
    long? categoryId = null;
    var currentName = name;

    void Check(string label, bool ok, string detail = "")
    {
        Console.WriteLine($"  {label,-42}{(ok ? "OK" : "FAILED")}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) failures++;
    }

    try
    {
        // --- add ---
        categoryId = catSvc.Add(root.Id, name);
        var folder = Path.Combine(root.Path, name);
        Check("Add creates folder + row", Directory.Exists(folder) && categoryId > 0, $"id={categoryId}");

        // --- add a duplicate differing only in case ---
        // Must be rejected by the unique index, and must not leave a folder behind.
        var beforeDirs = Directory.GetDirectories(root.Path).Length;
        var rejected = false;
        try { catSvc.Add(root.Id, name.ToUpperInvariant()); }
        catch { rejected = true; }
        var afterDirs = Directory.GetDirectories(root.Path).Length;
        Check("Duplicate name rejected", rejected);
        Check("Rejection leaves no orphan folder", afterDirs == beforeDirs,
              $"{beforeDirs} -> {afterDirs}");

        // --- retry after the rejection must still work ---
        // This is what the old ordering made impossible: the orphaned folder tripped the
        // Directory.Exists guard forever.
        var retryName = name + "_b";
        long retryId = 0;
        var retryOk = true;
        try { retryId = catSvc.Add(root.Id, retryName); }
        catch (Exception ex) { retryOk = false; Console.WriteLine("    retry threw: " + ex.Message); }
        Check("Add still works after a rejection", retryOk && retryId > 0);
        if (retryId > 0) catSvc.DeleteIfEmpty(retryId);

        // --- case-only rename ---
        var upper = name.ToUpperInvariant();
        catSvc.Rename(categoryId.Value, upper);
        currentName = upper;
        var onDisk = Directory.GetDirectories(root.Path)
            .Select(Path.GetFileName)
            .FirstOrDefault(d => string.Equals(d, upper, StringComparison.OrdinalIgnoreCase));
        var inDb = library.GetCategories(root.Id).FirstOrDefault(c => c.Id == categoryId.Value)?.Name;
        Check("Case-only rename on disk", onDisk == upper, $"disk='{onDisk}'");
        Check("Case-only rename in DB", inDb == upper, $"db='{inDb}'");

        // --- normal rename ---
        var renamed = name + "_renamed";
        catSvc.Rename(categoryId.Value, renamed);
        currentName = renamed;
        Check("Rename folder + row",
              Directory.Exists(Path.Combine(root.Path, renamed)) &&
              library.GetCategories(root.Id).Any(c => c.Id == categoryId.Value && c.Name == renamed));

        // --- EnsureCategory: used by the duplicates view's archive action ---
        var ensureName = "_selftest_ensure_" + stamp;
        long ensured = 0;
        try
        {
            ensured = catSvc.EnsureCategory(root.Id, ensureName);
            var ensureDir = Path.Combine(root.Path, ensureName);
            Check("EnsureCategory creates", ensured > 0 && Directory.Exists(ensureDir), $"id={ensured}");

            var again = catSvc.EnsureCategory(root.Id, ensureName);
            Check("EnsureCategory is idempotent", again == ensured, $"{ensured} vs {again}");

            // Different casing must resolve to the same category, not a second one.
            var cased = catSvc.EnsureCategory(root.Id, ensureName.ToUpperInvariant());
            Check("EnsureCategory ignores case", cased == ensured, $"{ensured} vs {cased}");

            // A row whose folder was deleted behind our back must be repaired.
            Directory.Delete(ensureDir);
            var repaired = catSvc.EnsureCategory(root.Id, ensureName);
            Check("EnsureCategory repairs a missing folder",
                  repaired == ensured && Directory.Exists(ensureDir));
        }
        finally
        {
            if (ensured > 0) { try { catSvc.DeleteIfEmpty(ensured); } catch { } }
        }

        // --- tags: create / assign / filter / remove ---
        var tagSvc = new ModOrganizer.Core.Tagging.TagService(store);
        var tagName = "selftest_tag_" + stamp;
        long tagId = 0;
        try
        {
            tagId = tagSvc.CreateTag(tagName);
            Check("CreateTag", tagId > 0, $"id={tagId}");

            // Must be idempotent, including a different casing.
            var again = tagSvc.CreateTag(tagName.ToUpperInvariant());
            Check("CreateTag is idempotent", again == tagId, $"{tagId} vs {again}");

            var mods = library.GetMods(root.Id).Take(3).Select(m => m.Id).ToList();
            if (mods.Count > 0)
            {
                var added = tagSvc.AddTagToMods(tagId, mods);
                Check("AddTagToMods (batch)", added == mods.Count, $"{added} of {mods.Count}");

                // Re-adding must not throw on the (mod_id, tag_id) primary key.
                var dupes = tagSvc.AddTagToMods(tagId, mods);
                Check("Re-add is a no-op", dupes == 0, $"{dupes} inserted");

                // Cards must carry their tags.
                var withTag = library.GetMods(new ModQuery { RootId = root.Id, TagsAll = new[] { tagId } });
                Check("Filter AND", withTag.Count == mods.Count, $"{withTag.Count} rows");
                Check("Card carries its tags",
                      withTag.All(c => c.Tags.Any(t => t.Id == tagId)));

                var anyTag = library.GetMods(new ModQuery { RootId = root.Id, TagsAny = new[] { tagId } });
                Check("Filter OR", anyTag.Count == mods.Count, $"{anyTag.Count} rows");

                var total = library.GetMods(new ModQuery { RootId = root.Id }).Count;
                var without = library.GetMods(new ModQuery { RootId = root.Id, TagsNone = new[] { tagId } });
                Check("Filter NOT", without.Count == total - mods.Count,
                      $"{without.Count} of {total}");

                var untagged = library.GetMods(new ModQuery { RootId = root.Id, OnlyUntagged = true });
                Check("Filter untagged excludes tagged",
                      untagged.All(c => c.Tags.Count == 0) && untagged.Count <= total - mods.Count,
                      $"{untagged.Count} rows");

                var unassigned = tagSvc.RemoveTagFromMods(tagId, mods);
                Check("RemoveTagFromMods (batch)", unassigned == mods.Count, $"{unassigned} rows");

                var afterRemove = library.GetMods(new ModQuery { RootId = root.Id, TagsAll = new[] { tagId } });
                Check("Filter empty after removal", afterRemove.Count == 0, $"{afterRemove.Count} rows");
            }

            // Rename collision must be reported, not leak a Postgres error.
            var other = tagSvc.CreateTag("selftest_other_" + stamp);
            var clashed = false;
            try { tagSvc.RenameTag(other, tagName); }
            catch (InvalidOperationException) { clashed = true; }
            Check("RenameTag reports a collision", clashed);
            tagSvc.DeleteTag(other);
        }
        finally
        {
            if (tagId > 0) { try { tagSvc.DeleteTag(tagId); } catch { } }
        }
        Check("Tag cleaned up", !tagSvc.GetAllTags().Any(t => t.Name.StartsWith("selftest_")));

        // --- delete ---
        var removed = catSvc.DeleteIfEmpty(categoryId.Value);
        var goneFromDisk = !Directory.Exists(Path.Combine(root.Path, renamed));
        var goneFromDb = !library.GetCategories(root.Id).Any(c => c.Id == categoryId.Value);
        Check("DeleteIfEmpty removes folder + row", removed && goneFromDisk && goneFromDb);
        if (removed) categoryId = null;

        // The category count must be back where it started.
        var finalDirs = Directory.GetDirectories(root.Path).Length;
        Check("Folder count restored", finalDirs == beforeDirs - 1,
              $"{beforeDirs - 1} expected, {finalDirs} found");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine();
        Console.WriteLine("EXCEPTION: " + ex);
    }
    finally
    {
        // Leave nothing behind even if an assertion above bailed out.
        if (categoryId is long leftover)
        {
            try { catSvc.DeleteIfEmpty(leftover); } catch { }
            foreach (var candidate in new[] { name, name.ToUpperInvariant(), currentName, name + "_renamed" })
            {
                try
                {
                    var dir = Path.Combine(root.Path, candidate);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
            Console.WriteLine();
            Console.WriteLine("  (cleaned up leftover test category)");
        }
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"{failures} SELFTEST FAILURE(S)");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Assigns a tag to every mod whose folder name contains one of the given substrings.
/// Handy for seeding tags without clicking through the gallery.
/// </summary>
static int ApplyTag(string connectionString, string tagName, string[] patterns, bool remove)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    store.Initialize();

    var library = new ModLibraryService(store);
    var tagSvc = new ModOrganizer.Core.Tagging.TagService(store);

    var roots = library.GetRoots();
    if (roots.Count == 0) { Console.Error.WriteLine("no roots configured"); return 2; }

    var matches = new List<ModCard>();
    foreach (var root in roots)
    {
        foreach (var card in library.GetMods(root.Id))
        {
            var name = card.DisplayName ?? card.FolderName;
            if (patterns.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                matches.Add(card);
        }
    }

    if (matches.Count == 0)
    {
        Console.WriteLine("no mods matched " + string.Join(", ", patterns));
        return 1;
    }

    long tagId;
    int changed;
    if (remove)
    {
        var existing = tagSvc.GetAllTags().FirstOrDefault(t =>
            string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { Console.WriteLine($"no tag named '{tagName}'"); return 1; }
        tagId = existing.Id;
        changed = tagSvc.RemoveTagFromMods(tagId, matches.Select(m => m.Id));
    }
    else
    {
        tagId = tagSvc.CreateTag(tagName);
        changed = tagSvc.AddTagToMods(tagId, matches.Select(m => m.Id));
    }

    var verb = remove ? "untagged" : "newly tagged";
    Console.WriteLine($"'{tagName}' (id {tagId}) -> {changed} {verb} of {matches.Count} matched:");
    foreach (var m in matches.Take(15))
        Console.WriteLine($"  {m.CategoryName}/{m.FolderName}");
    if (matches.Count > 15) Console.WriteLine($"  ... and {matches.Count - 15} more");

    return 0;
}

/// <summary>Lists every mod currently carrying a tag, across all roots.</summary>
static int ListTagged(string connectionString, string tagName)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    var library = new ModLibraryService(store);
    var tagSvc = new ModOrganizer.Core.Tagging.TagService(store);

    var tag = tagSvc.GetAllTags().FirstOrDefault(t =>
        string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
    if (tag is null) { Console.WriteLine($"no tag named '{tagName}'"); return 1; }

    Console.WriteLine($"'{tag.Name}' (id {tag.Id}) · usage count {tag.UsageCount}");

    var total = 0;
    foreach (var root in library.GetRoots())
    {
        var carriers = library.GetMods(new ModQuery { RootId = root.Id, TagsAll = new[] { tag.Id } });
        if (carriers.Count == 0) continue;

        Console.WriteLine($"  root #{root.Id} {root.DisplayName}: {carriers.Count}");
        foreach (var c in carriers) Console.WriteLine($"    {c.CategoryName}/{c.FolderName}");
        total += carriers.Count;
    }

    Console.WriteLine($"  visible in gallery: {total}");
    return 0;
}

/// <summary>
/// Explains how the local Penumbra state maps onto the library: which collections exist,
/// which of their enabled mods the app can even see, and which library folders they
/// resolve to. Read-only.
/// </summary>
static int PenumbraReport(string connectionString, string? collectionFilter)
{
    var svc = new ModOrganizer.Core.Penumbra.PenumbraService();
    var snap = svc.Read();

    Console.WriteLine($"Available     : {snap.IsAvailable}");
    Console.WriteLine($"ModDirectory  : {snap.ModDirectory}");
    Console.WriteLine($"Imported mods : {snap.Entries.Count}   (folders with meta.json)");
    Console.WriteLine();

    Console.WriteLine("== collections ==");
    foreach (var c in snap.Collections.OrderBy(c => c.Name))
    {
        var inCollection = snap.Entries.Count(e => e.AllInCollections.Contains(c.Name));
        Console.WriteLine($"  {(c.IsActive ? "[active]" : "[      ]")} {c.Name,-34} role='{c.Role}'  visible mods: {inCollection}");
    }
    Console.WriteLine();

    if (collectionFilter is null) return 0;

    var target = snap.Collections.FirstOrDefault(c =>
        string.Equals(c.Name, collectionFilter, StringComparison.OrdinalIgnoreCase));
    if (target is null) { Console.WriteLine($"no collection named '{collectionFilter}'"); return 1; }

    Console.WriteLine($"== '{target.Name}' ==");
    var members = snap.Entries
        .Where(e => e.AllInCollections.Contains(target.Name))
        .OrderBy(e => e.FolderName)
        .ToList();
    Console.WriteLine($"Penumbra entries in this collection: {members.Count}");
    foreach (var m in members)
        Console.WriteLine($"  '{m.FolderName}'  meta='{m.MetaName}'  status={m.Status}");
    Console.WriteLine();

    // Now the other direction: which library mods does the app resolve into this collection?
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    var library = new ModLibraryService(store);

    Console.WriteLine("== library mods the app maps into this collection ==");
    var matchedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var total = 0;
    foreach (var root in library.GetRoots())
    {
        foreach (var card in library.GetMods(root.Id))
        {
            // MatchesFor, exactly like the gallery does - a mod can have several copies
            // in Penumbra and only one of them may be the enabled one.
            var hits = snap.MatchesFor(card.FolderName, card.DisplayName)
                .Where(e => e.AllInCollections.Contains(target.Name))
                .ToList();
            if (hits.Count == 0) continue;

            Console.WriteLine($"  {card.CategoryName}/{card.FolderName}  ->  " +
                              string.Join(", ", hits.Select(h => "'" + h.FolderName + "'")));
            foreach (var h in hits) matchedEntries.Add(h.FolderName);
            total++;
        }
    }
    Console.WriteLine($"  total: {total}");
    Console.WriteLine();

    Console.WriteLine("== Penumbra entries with NO library match ==");
    foreach (var m in members.Where(m => !matchedEntries.Contains(m.FolderName)))
        Console.WriteLine($"  '{m.FolderName}'  (normalized: '{ModOrganizer.Core.Penumbra.PenumbraService.Normalize(m.FolderName)}')");

    return 0;
}

/// <summary>Shows exactly how one name normalizes and what the matcher resolves it to.</summary>
static int PenumbraProbe(string[] names)
{
    var snap = new ModOrganizer.Core.Penumbra.PenumbraService().Read();
    foreach (var name in names)
    {
        var norm = ModOrganizer.Core.Penumbra.PenumbraService.Normalize(name);
        var hit = snap.Lookup(name);
        Console.WriteLine($"'{name}'");
        Console.WriteLine($"   normalized : '{norm}' (len {norm.Length})");
        Console.WriteLine($"   lookup     : {(hit is null ? "<no match>" : "'" + hit.FolderName + "' status=" + hit.Status)}");
        if (hit is not null)
            Console.WriteLine($"   collections: {string.Join(", ", hit.AllInCollections)}");
        Console.WriteLine();
    }
    return 0;
}
