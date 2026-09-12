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
    Console.WriteLine("  ModOrganizer.Cli missing  <connection-string> [--purge] [--days N]");
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

if (args[0] == "missing")
{
    if (args.Length < 2) { Console.Error.WriteLine("missing needs a connection string"); return 2; }
    var rest = args.Skip(2).ToArray();
    var daysArg = rest.SkipWhile(a => a != "--days").Skip(1).FirstOrDefault();
    return Missing(args[1],
        purge: rest.Contains("--purge"),
        olderThanDays: int.TryParse(daysArg, out var d) ? d : 0);
}

if (args[0] == "roots")
{
    if (args.Length < 2) { Console.Error.WriteLine("roots needs a connection string"); return 2; }

    // "--as <user-id|email>" reports the library exactly as that user's app would see it,
    // which is how the per-user path mapping gets verified without two PCs.
    if (args.Length > 3 && args[2] == "--as")
    {
        var rest = args.Skip(4).ToArray();
        var under = rest.SkipWhile(a => a != "--map-under").Skip(1).FirstOrDefault();
        return RootsAsUser(args[1], args[3],
            adopt: rest.Contains("--adopt"),
            mapUnder: under,
            applyMapping: rest.Contains("--apply"));
    }

    return ListRoots(args[1]);
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
        // An empty root proves nothing about escaping - 0 < 0 is false but correct.
        var wildcardOk = total == 0 || wildcard.Count < total;
        var percentOk = total == 0 || percent.Count < total;
        Console.WriteLine($"  search '_' literal : {wildcard.Count,4} rows (of {total})" +
                          (total == 0 ? "  (leer, uebersprungen)" : wildcardOk ? "  OK" : "  <-- WILDCARD NOT ESCAPED"));
        Console.WriteLine($"  search '%' literal : {percent.Count,4} rows (of {total})" +
                          (total == 0 ? "  (leer, uebersprungen)" : percentOk ? "  OK" : "  <-- WILDCARD NOT ESCAPED"));
        if (!wildcardOk || !percentOk) failures++;

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

/// <summary>Lists every root, its stored path, whether that path exists here, and its content.</summary>
static int ListRoots(string connectionString)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    using var conn = store.Open();

    // Ratings, tags and comments are the work that a DeleteRoot would destroy, so they
    // belong next to the counts whenever someone is deciding what to clean up.
    var rows = conn.Query<(long Id, string Path, string DisplayName, bool Enabled,
                           long Cats, long Mods, long Rated, long Tagged, long Commented)>(
        """
        SELECT r.id AS Id, r.path AS Path, r.display_name AS DisplayName, r.enabled AS Enabled,
               (SELECT COUNT(*) FROM categories c WHERE c.root_id = r.id) AS Cats,
               (SELECT COUNT(*) FROM mods m JOIN categories c ON c.id = m.category_id
                 WHERE c.root_id = r.id AND m.deleted_at IS NULL) AS Mods,
               (SELECT COUNT(*) FROM mods m JOIN categories c ON c.id = m.category_id
                 WHERE c.root_id = r.id AND m.deleted_at IS NULL AND m.rating > 0) AS Rated,
               (SELECT COUNT(DISTINCT m.id) FROM mods m
                  JOIN categories c ON c.id = m.category_id
                  JOIN mod_tags mt ON mt.mod_id = m.id
                 WHERE c.root_id = r.id AND m.deleted_at IS NULL) AS Tagged,
               (SELECT COUNT(DISTINCT m.id) FROM mods m
                  JOIN categories c ON c.id = m.category_id
                  JOIN mod_comments cm ON cm.mod_id = m.id
                 WHERE c.root_id = r.id AND m.deleted_at IS NULL) AS Commented
        FROM roots r ORDER BY r.id
        """).ToList();

    foreach (var r in rows)
    {
        var here = Directory.Exists(r.Path) ? "vorhanden" : "FEHLT auf diesem PC";
        Console.WriteLine($"#{r.Id}  {r.DisplayName}");
        Console.WriteLine($"     path    : {r.Path}   [{here}]");
        Console.WriteLine($"     enabled : {r.Enabled}   Kategorien: {r.Cats}   Mods: {r.Mods}");
        Console.WriteLine($"     gepflegt: {r.Rated} bewertet, {r.Tagged} getaggt, {r.Commented} kommentiert" +
                          (r.Rated + r.Tagged + r.Commented == 0
                              ? "   -> nichts, was beim Loeschen verloren ginge"
                              : "   -> beim Loeschen weg"));
    }

    Console.WriteLine();
    Console.WriteLine("== Inhalt je Bibliothek ==");
    foreach (var r in rows)
    {
        var content = conn.Query<(string Cat, string Folder)>(
            """
            SELECT c.name AS Cat, m.folder_name AS Folder
            FROM mods m JOIN categories c ON c.id = m.category_id
            WHERE c.root_id = @r AND m.deleted_at IS NULL
            ORDER BY c.name, m.folder_name
            """, new { r = r.Id }).ToList();

        Console.WriteLine($"  #{r.Id} {r.DisplayName}: {content.Count} Mods");
        foreach (var c in content.Take(8)) Console.WriteLine($"       {c.Cat}/{c.Folder}");
        if (content.Count > 8) Console.WriteLine($"       ... und {content.Count - 8} weitere");
    }

    Console.WriteLine();
    var users = conn.Query<(Guid Id, string? Email, string? Name)>(
        "SELECT id AS Id, email AS Email, display_name AS Name FROM users ORDER BY created_at").ToList();
    Console.WriteLine($"users: {users.Count}");

    // Per-user mappings: the whole point is that the same library resolves to a
    // different folder for every user.
    foreach (var u in users)
    {
        Console.WriteLine($"  {u.Name} <{u.Email}>  {u.Id}");
        var maps = conn.Query<(long RootId, string Name, string Path, bool Enabled)>(
            """
            SELECT rp.root_id AS RootId, r.display_name AS Name, rp.path AS Path, rp.enabled AS Enabled
            FROM root_paths rp JOIN roots r ON r.id = rp.root_id
            WHERE rp.user_id = @u ORDER BY rp.root_id
            """, new { u = u.Id }).ToList();

        if (maps.Count == 0) { Console.WriteLine("      (keine Zuordnungen)"); continue; }
        foreach (var m in maps)
            Console.WriteLine($"      #{m.RootId} {m.Name,-26} -> {m.Path}   enabled={m.Enabled}");
    }

    return 0;
}


/// <summary>
/// Lists the mods that scans could not find on disk any more, and optionally clears them
/// into the trash. This is the headless twin of the banner in the gallery.
/// </summary>
static int Missing(string connectionString, bool purge, int olderThanDays)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));
    store.Initialize();

    var library = new ModLibraryService(store);
    var roots = library.GetRoots();
    if (roots.Count == 0) { Console.Error.WriteLine("no roots configured"); return 2; }

    var total = 0;
    foreach (var root in roots)
    {
        var count = library.CountMissing(root.Id);
        Console.WriteLine($"== #{root.Id} {root.DisplayName} ==  {count} nicht mehr im Ordner");
        total += count;

        var gone = library.GetMods(new ModQuery { RootId = root.Id, Missing = MissingFilter.Only });
        foreach (var m in gone.OrderBy(m => m.CategoryName).ThenBy(m => m.FolderName))
        {
            var age = m.MissingSince is { } s
                ? $"seit {(int)(DateTimeOffset.UtcNow - s).TotalDays}d"
                : "seit unbekannt";
            Console.WriteLine($"   {m.CategoryName}/{m.FolderName}  ({age})");
        }

        if (purge)
        {
            var moved = library.TrashMissing(root.Id, olderThanDays);
            Console.WriteLine($"   -> {moved} in den Papierkorb verschoben" +
                              (olderThanDays > 0 ? $" (nur aelter als {olderThanDays} Tage)" : ""));
        }
    }

    Console.WriteLine();
    Console.WriteLine($"gesamt: {total}");
    if (!purge && total > 0)
        Console.WriteLine("(Probelauf - mit --purge wandern sie in den Papierkorb)");
    return 0;
}

/// <summary>Shows what one specific user's app would load, and can run the auto-adopt.</summary>
static int RootsAsUser(string connectionString, string who, bool adopt,
                       string? mapUnder = null, bool applyMapping = false)
{
    var store = new DatabaseStore(new PostgresConnectionFactory(connectionString));

    Guid uid;
    string? name;
    using (var conn = store.Open())
    {
        var row = conn.QuerySingleOrDefault<(Guid Id, string? Name)>(
            """
            SELECT id AS Id, COALESCE(display_name, email) AS Name FROM users
            WHERE id::text = @w OR email = @w OR display_name = @w
            """, new { w = who });
        if (row.Id == Guid.Empty) { Console.Error.WriteLine($"kein Benutzer '{who}'"); return 2; }
        uid = row.Id; name = row.Name;
    }

    var user = new FakeUser(uid, name);
    var roots = new RootService(store, user);
    var library = new ModLibraryService(store, user);

    Console.WriteLine($"== als {name} ({uid}) ==");

    if (adopt)
    {
        var n = roots.AdoptLocalRoots();
        Console.WriteLine($"auto-adopt: {n} Bibliothek(en) uebernommen (Pfad existiert auf diesem PC)");
        Console.WriteLine();
    }

    // Dry-run of the bulk mapping the settings window offers, so the resolver can be
    // checked against the real root list before anyone clicks anything.
    if (mapUnder is not null)
    {
        Console.WriteLine($"Sammel-Zuordnung unter: {mapUnder}");
        foreach (var prop in roots.ProposeMappingsUnder(mapUnder))
        {
            var mark = prop.ResolvedPath is null ? "NICHT GEFUNDEN" : prop.ResolvedPath;
            var note = prop.AlreadyMapped ? "  (war schon zugeordnet)" : "";
            Console.WriteLine($"  #{prop.RootId} {prop.DisplayName,-26} {mark}{note}");
            Console.WriteLine($"        Referenz: {prop.ReferencePath}");
        }

        var (toApply, dupes) = RootService.SplitConflicts(roots.ProposeMappingsUnder(mapUnder));
        foreach (var d in dupes)
            Console.WriteLine($"  KONFLIKT: #{d.RootId} {d.DisplayName} zeigt auf denselben Ordner - uebersprungen");

        if (applyMapping)
        {
            var applied = roots.ApplyMappings(toApply);
            Console.WriteLine($"  -> {applied} Zuordnung(en) geschrieben");
        }
        else
        {
            Console.WriteLine("  (Probelauf - mit --apply wird geschrieben)");
        }
        Console.WriteLine();
    }

    Console.WriteLine("alle Bibliotheken aus Sicht dieses Benutzers:");
    foreach (var r in roots.GetAll())
        Console.WriteLine($"  #{r.Id} {r.DisplayName,-26} {r.StatusText,-18} {r.Path ?? "-"}");

    Console.WriteLine();
    Console.WriteLine("was die App im Root-Auswahlfeld zeigt (nur zugeordnete):");
    var visible = library.GetRoots();
    if (visible.Count == 0) Console.WriteLine("  (keine)");

    var failures = 0;
    foreach (var r in visible)
    {
        Console.WriteLine($"  #{r.Id} {r.DisplayName,-26} {r.Path}   enabled={r.Enabled}");

        // Exercise the gallery query with this user's resolved path, and confirm the
        // absolute paths it builds actually point at this machine.
        try
        {
            var mods = library.GetMods(r.Id);
            var sample = mods.FirstOrDefault();
            var onDisk = sample is null ? 0 : mods.Count(m => Directory.Exists(m.FolderAbsPath));
            Console.WriteLine($"        GetMods: {mods.Count} Mods, davon {onDisk} auf der Platte gefunden");
            if (sample is not null)
                Console.WriteLine($"        Beispielpfad: {sample.FolderAbsPath}");
            if (mods.Count > 0 && onDisk == 0)
            {
                Console.WriteLine("        <-- KEIN Mod gefunden: Pfadaufloesung stimmt nicht");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        GetMods FAILED: {ex.Message}");
            failures++;
        }
    }

    // The regression that started all this: scanning a root another user created used to
    // die with "Root path 'E:\...' does not exist" on a PC that has no E: drive.
    Console.WriteLine();
    Console.WriteLine("Scan-Verhalten bei NICHT zugeordneter Bibliothek:");
    var unmapped = roots.GetAll().FirstOrDefault(r => !r.IsMapped);
    if (unmapped is null)
    {
        Console.WriteLine("  (dieser Benutzer hat alles zugeordnet)");
    }
    else
    {
        var scanner = new ModScanner(store, null, user);
        try
        {
            scanner.Scan(unmapped.Id);
            Console.WriteLine($"  #{unmapped.Id}: unerwartet durchgelaufen  <-- FALSCH");
            failures++;
        }
        catch (RootNotMappedException ex)
        {
            Console.WriteLine($"  #{unmapped.Id} {unmapped.DisplayName}: RootNotMappedException  OK");
            Console.WriteLine("     " + FirstLine(ex.Message));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  #{unmapped.Id}: {ex.GetType().Name} statt RootNotMappedException  <-- FALSCH");
            Console.WriteLine("     " + FirstLine(ex.Message));
            failures++;
        }
    }

    return failures == 0 ? 0 : 1;
}

static string FirstLine(string s) => s.Split('\n')[0].Trim();

/// <summary>Stand-in for a signed-in user, so the CLI can test per-user resolution.</summary>
sealed class FakeUser : ModOrganizer.Core.Auth.IUserContext
{
    public FakeUser(Guid id, string? name) { UserId = id; DisplayName = name; }
    public Guid? UserId { get; }
    public string? Email => null;
    public string? DisplayName { get; }
    public bool IsAuthenticated => true;
#pragma warning disable CS0067
    public event EventHandler? UserChanged;
#pragma warning restore CS0067
}
