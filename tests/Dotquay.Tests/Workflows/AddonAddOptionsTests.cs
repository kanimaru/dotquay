using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="AddonAddOptions"/> — how <c>dotquay add</c> states which source it wants
/// (SPEC.md §6, §7.1).
/// </summary>
public class AddonAddOptionsTests {
    [Fact]
    public void Validate_NothingNamed_IsFine() {
        // The default path: no source stated, so `add` falls back to picking the newest release tag.
        new AddonAddOptions().Validate();
        Assert.False(new AddonAddOptions().HasExplicitSource);
    }

    [Theory]
    [InlineData("~2.5", null, null)]
    [InlineData(null, "main", null)]
    [InlineData(null, null, "9f2c1ab")]
    public void Validate_ExactlyOneNamed_IsFine(string? version, string? branch, string? rev) {
        var options = new AddonAddOptions { Version = version, Branch = branch, Rev = rev };

        options.Validate();
        Assert.True(options.HasExplicitSource);
    }

    [Fact]
    public void Validate_TwoNamed_Throws() {
        var options = new AddonAddOptions { Branch = "main", Rev = "9f2c1ab" };

        // The manifest allows exactly one of version/branch/rev (SPEC.md §7.1). Catching it here means
        // the error names the flags the user typed, rather than surfacing later from the writer.
        var exception = Assert.Throws<WorkflowException>(options.Validate);
        Assert.Contains("--branch", exception.Message);
    }

    [Fact]
    public void WithSource_MapsEachKindToItsField() {
        var branch = new AddonAddOptions().WithSource(AddonSourceKind.Branch, "main");
        var rev = new AddonAddOptions().WithSource(AddonSourceKind.Rev, "9f2c1ab");
        var version = new AddonAddOptions().WithSource(AddonSourceKind.Version, "v1.0.0");

        Assert.Equal("main", branch.Branch);
        Assert.Equal("9f2c1ab", rev.Rev);
        Assert.Equal("v1.0.0", version.Version);
    }

    [Fact]
    public void WithSource_KeepsTheOtherOptions() {
        var options = new AddonAddOptions { Path = ".", InstallAs = "Godot-MToon-Shader" }
            .WithSource(AddonSourceKind.Branch, "main");

        // `#main` says which commit; --path and --install-as say which files and where. Resolving the
        // shorthand must not drop the other half of the request.
        Assert.Equal(".", options.Path);
        Assert.Equal("Godot-MToon-Shader", options.InstallAs);
        Assert.Equal("main", options.Branch);
    }
}
