using FluentAssertions;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.Core.Tests;

/// <summary>
/// The two-PC case: the same Nextcloud tree sits under a different anchor on each machine,
/// so a mapping has to be found by matching the tail of the path, not the whole path.
/// </summary>
public sealed class RootMappingTests : IDisposable
{
    private readonly string _base;

    public RootMappingTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mo-roots-" + Guid.NewGuid().ToString("N"));
        // Fayne's side: D:\FFXIV Cloud Mod Ordner\FFXIV\FF14 Mods\Mods\Dawntrail
        Directory.CreateDirectory(Path.Combine(_base, "FFXIV", "FF14 Mods", "Mods", "Dawntrail"));
        Directory.CreateDirectory(Path.Combine(_base, "FFXIV", "FF14 Mods", "Mods", "Endwalker"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void FindsDeepTailUnderDifferentAnchor()
    {
        // The other user's path; only the tail below the anchor is shared.
        var mine = RootService.ResolveUnder(_base, @"E:\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail");

        mine.Should().Be(Path.Combine(_base, "FFXIV", "FF14 Mods", "Mods", "Dawntrail"));
    }

    [Fact]
    public void PrefersTheLongestMatchingTail()
    {
        // "Mods" alone would also resolve; the deeper match must win so two libraries
        // sharing a parent don't collapse onto the same folder.
        var a = RootService.ResolveUnder(_base, @"E:\a\b\FFXIV\FF14 Mods\Mods\Dawntrail");
        var b = RootService.ResolveUnder(_base, @"E:\a\b\FFXIV\FF14 Mods\Mods\Endwalker");

        a.Should().EndWith("Dawntrail");
        b.Should().EndWith("Endwalker");
        a.Should().NotBe(b);
    }

    [Fact]
    public void ResolvesToTheBaseWhenTheBaseIsTheLibrary()
    {
        var name = new DirectoryInfo(_base).Name;

        RootService.ResolveUnder(_base, @"E:\somewhere\else\" + name).Should().Be(_base);
    }

    [Fact]
    public void ReturnsNullWhenNothingMatches()
    {
        // Never guess: an unmatched library stays unmapped rather than pointing at a
        // folder that happens to exist.
        RootService.ResolveUnder(_base, @"E:\Textures\Faces").Should().BeNull();
    }

    [Fact]
    public void HandlesForwardSlashesAndTrailingSeparators()
    {
        RootService.ResolveUnder(_base, "E:/FFXIV/FF14 Mods/Mods/Dawntrail/")
            .Should().Be(Path.Combine(_base, "FFXIV", "FF14 Mods", "Mods", "Dawntrail"));
    }

    [Fact]
    public void DuplicateLibrariesResolvingToOneFolderAreSplitOff()
    {
        var target = Path.Combine(_base, "FFXIV", "FF14 Mods", "Mods", "Dawntrail");
        var proposals = new[]
        {
            new RootService.RootMapProposal(1, "Dawntrail", @"E:\x\Dawntrail", target, false),
            new RootService.RootMapProposal(5, "Dawntrail", @"D:\y\Dawntrail", target, false),
            new RootService.RootMapProposal(2, "Posen", @"E:\x\Posen",
                Path.Combine(_base, "FFXIV", "FF14 Mods"), false)
        };

        var (apply, conflicts) = RootService.SplitConflicts(proposals);

        apply.Select(p => p.RootId).Should().BeEquivalentTo(new long[] { 1, 2 });
        conflicts.Should().ContainSingle().Which.RootId.Should().Be(5);
    }

    [Fact]
    public void AnExistingMappingWinsOverANewOne()
    {
        var target = Path.Combine(_base, "FFXIV");
        var proposals = new[]
        {
            new RootService.RootMapProposal(1, "Neu", @"E:\a", target, false),
            new RootService.RootMapProposal(9, "Schon zugeordnet", @"E:\b", target, true)
        };

        var (apply, conflicts) = RootService.SplitConflicts(proposals);

        apply.Should().ContainSingle().Which.RootId.Should().Be(9);
        conflicts.Should().ContainSingle().Which.RootId.Should().Be(1);
    }
}

/// <summary>
/// The guard that keeps a half-synced folder from wiping a shared library out of the
/// gallery for everyone.
/// </summary>
public sealed class MissingGuardTests
{
    [Theory]
    [InlineData(390, 18)]    // a normal cleanup: a handful of mods deleted
    [InlineData(390, 195)]   // exactly half, still treated as a real deletion
    [InlineData(10, 10)]     // tiny library: too small to judge, let it through
    [InlineData(19, 19)]     // just under the size floor
    [InlineData(0, 0)]       // empty root
    public void FlagsMissingForPlausibleDeletions(int live, int vanished)
    {
        ModScanner.LooksLikeIncompleteSync(live, vanished).Should().BeFalse();
    }

    [Theory]
    [InlineData(390, 196)]   // more than half of a real library
    [InlineData(390, 390)]   // the folder came up empty - sync not started
    [InlineData(20, 11)]
    public void RefusesToFlagWhenMostOfTheLibraryIsGone(int live, int vanished)
    {
        ModScanner.LooksLikeIncompleteSync(live, vanished).Should().BeTrue();
    }
}

/// <summary>
/// The gate that stops the folder watcher from stacking scans on one root - the failure
/// that surfaced as Npgsql's "Exception while reading from stream".
/// </summary>
public sealed class ScanGateTests
{
    [Fact]
    public void SecondEntryOnTheSameRootIsRefused()
    {
        var gate = new ScanGate();

        gate.TryEnter(7).Should().BeTrue();
        gate.TryEnter(7).Should().BeFalse();
        gate.IsBusy(7).Should().BeTrue();

        gate.Exit(7);

        gate.IsBusy(7).Should().BeFalse();
        gate.TryEnter(7).Should().BeTrue();
    }

    [Fact]
    public void DifferentRootsScanIndependently()
    {
        var gate = new ScanGate();

        gate.TryEnter(1).Should().BeTrue();
        gate.TryEnter(2).Should().BeTrue();
        gate.IsBusy(3).Should().BeFalse();
    }

    [Fact]
    public void OnlyOneOfManyThreadsGetsIn()
    {
        var gate = new ScanGate();
        var winners = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.TryEnter(42)) Interlocked.Increment(ref winners);
        });

        winners.Should().Be(1);
    }
}

/// <summary>
/// Telling a mod folder from a grouping folder. This is what lets a pose library nest
/// deeper (Solo/NSFW/Sitzend/pose) without the gear libraries changing shape.
/// </summary>
public sealed class NestedScanTests : IDisposable
{
    private readonly string _root;

    public NestedScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mo-nest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private void File_(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), "x");

    [Fact]
    public void AFolderHoldingFilesIsAMod()
    {
        var mod = Dir("BDSM", "Guard Me");
        File_(mod, "pose.pose");

        ModScanner.LooksLikeMod(mod).Should().BeTrue();
    }

    [Fact]
    public void AFolderWithSubFoldersAndAPreviewStaysOneMod()
    {
        // A mega pack: sub-folders plus its own preview image. Splitting it would scatter
        // one download across dozens of entries.
        var pack = Dir("Packs", "Mega Pack");
        Dir("Packs", "Mega Pack", "Part 1");
        Dir("Packs", "Mega Pack", "Part 2");
        File_(pack, "preview.png");

        ModScanner.LooksLikeMod(pack).Should().BeTrue();
    }

    [Fact]
    public void AFolderWithOnlySubFoldersIsAGroup()
    {
        var group = Dir("Solo", "NSFW");
        Dir("Solo", "NSFW", "Sitzend");

        ModScanner.LooksLikeMod(group).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyFolderCountsAsAModSoItStaysVisible()
    {
        ModScanner.LooksLikeMod(Dir("Leer")).Should().BeTrue();
    }
}

/// <summary>Pose files are the unit of a pose library, so they need their own kind.</summary>
public sealed class FileClassifierPoseTests
{
    [Theory]
    [InlineData("Standing.pose")]
    [InlineData("QuickLunch(Sieann).POSE")]
    [InlineData("sub/dir/Anastasia (f).pose")]
    public void PoseFilesAreClassifiedAsPose(string name)
    {
        FileClassifier.Classify(name).Should().Be(ModFileKind.Pose);
    }

    [Fact]
    public void PoseFilesAreNotHashed()
    {
        // Hashing exists to spot duplicate archives; a pose is a few kilobytes of JSON.
        FileClassifier.ShouldHash(ModFileKind.Pose).Should().BeFalse();
    }

    [Fact]
    public void ArchivesKeepTheirKinds()
    {
        FileClassifier.Classify("mod.pmp").Should().Be(ModFileKind.Pmp);
        FileClassifier.Classify("mod.ttmp2").Should().Be(ModFileKind.Ttmp2);
        FileClassifier.Classify("preview.png").Should().Be(ModFileKind.Image);
    }
}
