using FluentAssertions;
using ModOrganizer.App.Services;

namespace ModOrganizer.Core.Tests;

/// <summary>
/// Reading the version out of a release tag. A wrong comparison here is silent: everyone
/// simply stops being offered updates, and nobody notices for months.
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("V2.0.1", "2.0.1")]
    [InlineData(" v1.0.0 ", "1.0.0")]
    [InlineData("v1.2", "1.2")]
    public void ParsesUsualTagShapes(string tag, string expected)
    {
        UpdateService.TryParseVersion(tag, out var version).Should().BeTrue();
        version.Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release")]
    [InlineData("v1.2.0-beta")]
    public void RejectsWhatItCannotUnderstand(string? tag)
    {
        UpdateService.TryParseVersion(tag, out _).Should().BeFalse();
    }

    [Fact]
    public void ComparesNumericallyNotAlphabetically()
    {
        // "1.10.0" sorts before "1.9.0" as text; as a Version it must win.
        UpdateService.TryParseVersion("v1.10.0", out var newer).Should().BeTrue();
        UpdateService.TryParseVersion("v1.9.0", out var older).Should().BeTrue();

        (newer > older).Should().BeTrue();
    }

    [Fact]
    public void AnEqualVersionIsNotAnUpdate()
    {
        UpdateService.TryParseVersion("v1.0.0", out var released).Should().BeTrue();

        // The app compares with <=, so the same version must not trigger anything.
        (released > new Version(1, 0, 0, 0)).Should().BeFalse();
    }
}
