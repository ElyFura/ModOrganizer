using FluentAssertions;
using ModOrganizer.Core.Queries;

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
