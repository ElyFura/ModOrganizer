using FluentAssertions;
using ModOrganizer.Core.Categories;
using ModOrganizer.Core.Duplicates;
using ModOrganizer.Core.Links;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Models;

namespace ModOrganizer.Core.Tests;

public sealed class UrlNormalizerTests
{
    [Fact]
    public void StripsUtmParams()
    {
        UrlNormalizer.Normalize("https://example.com/x?utm_source=foo&x=1&utm_medium=bar&y=2")
            .Should().Be("https://example.com/x?x=1&y=2");
    }

    [Fact]
    public void StripsFbclid()
    {
        UrlNormalizer.Normalize("https://example.com/x?fbclid=abc")
            .Should().Be("https://example.com/x");
    }
}

public sealed class DomainKindResolverTests
{
    [Theory]
    [InlineData("www.xivmodarchive.com", ModLinkKind.Source)]
    [InlineData("www.patreon.com", ModLinkKind.Patreon)]
    [InlineData("ko-fi.com", ModLinkKind.Kofi)]
    [InlineData("twitter.com", ModLinkKind.Twitter)]
    [InlineData("x.com", ModLinkKind.Twitter)]
    [InlineData("discord.gg", ModLinkKind.Discord)]
    [InlineData("github.com", ModLinkKind.Github)]
    [InlineData("www.nexusmods.com", ModLinkKind.Nexus)]
    [InlineData("totally-unknown-site.foo", ModLinkKind.Other)]
    public void MapsDomains(string domain, ModLinkKind expected)
    {
        DomainKindResolver.Resolve(domain).Should().Be(expected);
    }
}

public sealed class DuplicateFinderNormalizeTests
{
    [Theory]
    [InlineData("Empress - Makeup", "EmpressMakeup")]
    [InlineData("Akari Catsuit [1.0]", "akaricatsuit")]
    [InlineData("Bibo+ Mod v2", "bibo+mod")]
    [InlineData("Cool Mod for Bibo+", "coolmodfor")]
    public void StripsVersionAndSeparators(string input, string expected)
    {
        DuplicateFinder.Normalize(input).Should().Be(expected.ToLowerInvariant());
    }
}

public sealed class RenameServiceStaticTests
{
    [Fact]
    public void StemEqualsOldFolder_Renames()
    {
        RenameService.ComputeNewFileName("Akari.pmp", "Akari", "Akari DT")
            .Should().Be("Akari DT.pmp");
    }

    [Fact]
    public void StemStartsWithOldFolderSeparator_Renames()
    {
        RenameService.ComputeNewFileName("AVALON REDUX - Tube Top.pmp", "AVALON REDUX", "AVALON")
            .Should().Be("AVALON - Tube Top.pmp");
    }

    [Fact]
    public void ArchiveImagePattern_Renames()
    {
        RenameService.ComputeNewFileName("mod_119906_c0536743-e367-4921-ae1c-bab0e3dd3837.jpg",
                                          "AVALON REDUX", "AVALON REDUX DT")
            .Should().Be("AVALON REDUX DT.jpg");
    }

    [Fact]
    public void UnrelatedFile_ReturnsNull()
    {
        RenameService.ComputeNewFileName("Readme.txt", "Akari", "Akari DT")
            .Should().BeNull();
    }
}

public sealed class FileSystemActivityGateTests
{
    [Fact]
    public void NotSuppressed_ByDefault()
    {
        new FileSystemActivityGate().IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void SuppressedInsideScope()
    {
        var gate = new FileSystemActivityGate();
        using (gate.Suppress())
        {
            gate.IsSuppressed.Should().BeTrue();
        }
    }

    [Fact]
    public void StaysSuppressedDuringCooldownAfterScope()
    {
        // Filesystem events arrive after the operation finishes, so the gate has to stay
        // closed briefly or the watcher still rescans our own writes.
        var gate = new FileSystemActivityGate();
        using (gate.Suppress()) { }
        gate.IsSuppressed.Should().BeTrue();
    }

    [Fact]
    public void NestedScopes_AreRefCounted()
    {
        var gate = new FileSystemActivityGate();
        var outer = gate.Suppress();
        var inner = gate.Suppress();

        inner.Dispose();
        gate.IsSuppressed.Should().BeTrue("the outer scope is still open");

        outer.Dispose();
        gate.IsSuppressed.Should().BeTrue("cooldown keeps it closed after the last scope");
    }

    [Fact]
    public void DoubleDispose_DoesNotUnderflowRefCount()
    {
        var gate = new FileSystemActivityGate();
        var outer = gate.Suppress();
        var inner = gate.Suppress();

        inner.Dispose();
        inner.Dispose();   // a second dispose must be a no-op

        gate.IsSuppressed.Should().BeTrue();
        outer.Dispose();
    }
}

/// <summary>
/// Filesystem behaviour of the category helpers, against real temp folders.
/// </summary>
public sealed class CategoryFolderTests : IDisposable
{
    private readonly string _root;

    public CategoryFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mo-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CaseOnlyRename_ChangesTheNameOnDisk()
    {
        // Directory.Move refuses a same-but-differently-cased target, so this goes
        // through a staging name. "Gear" -> "gear" used to fail outright.
        var from = Path.Combine(_root, "Gear");
        var to = Path.Combine(_root, "gear");
        Directory.CreateDirectory(from);
        File.WriteAllText(Path.Combine(from, "mod.pmp"), "x");

        CategoryService.MoveDirectory(from, to, caseOnly: true);

        // The path is case-insensitive on Windows, so assert on the actual entry name.
        Directory.GetDirectories(_root)
            .Select(Path.GetFileName)
            .Should().ContainSingle().Which.Should().Be("gear");

        // The rename must not lose the folder's contents.
        File.Exists(Path.Combine(to, "mod.pmp")).Should().BeTrue();
    }

    [Fact]
    public void CaseOnlyRename_LeavesNoStagingFolderBehind()
    {
        var from = Path.Combine(_root, "Hair");
        Directory.CreateDirectory(from);

        CategoryService.MoveDirectory(from, Path.Combine(_root, "HAIR"), caseOnly: true);

        Directory.GetDirectories(_root).Should().HaveCount(1);
        Directory.GetDirectories(_root).Single().Should().NotContain("__case_");
    }

    [Fact]
    public void NormalRename_MovesTheFolder()
    {
        var from = Path.Combine(_root, "Chi Gear");
        var to = Path.Combine(_root, "Chinese Gear");
        Directory.CreateDirectory(from);

        CategoryService.MoveDirectory(from, to, caseOnly: false);

        Directory.Exists(to).Should().BeTrue();
        Directory.GetDirectories(_root).Select(Path.GetFileName)
            .Should().ContainSingle().Which.Should().Be("Chinese Gear");
    }

    [Fact]
    public void TryDeleteEmptyDirectory_RemovesAnEmptyFolder()
    {
        var dir = Path.Combine(_root, "_Tmp");
        Directory.CreateDirectory(dir);

        CategoryService.TryDeleteEmptyDirectory(dir);

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void TryDeleteEmptyDirectory_KeepsAFolderThatHasContent()
    {
        // This runs on the rollback path, so it must never be able to delete user data.
        var dir = Path.Combine(_root, "_Archiv");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.pmp"), "x");

        CategoryService.TryDeleteEmptyDirectory(dir);

        Directory.Exists(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, "keep.pmp")).Should().BeTrue();
    }

    [Fact]
    public void TryDeleteEmptyDirectory_IsSilentOnAMissingFolder()
    {
        var act = () => CategoryService.TryDeleteEmptyDirectory(Path.Combine(_root, "nope"));
        act.Should().NotThrow();
    }
}
