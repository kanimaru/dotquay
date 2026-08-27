using Dotquay.Core.Lockfile;
using Xunit;

namespace Dotquay.Tests.Lockfile;

/// <summary>
/// Covers the short-read guard in <see cref="LockfileReader"/>.
/// </summary>
/// <remarks>
/// Found by accident while writing an unrelated doctor test with a deliberately odd fixture: a
/// lockfile whose <c>[[addon]]</c> blocks are interrupted by a <c>[[dotnet]]</c> block parses
/// "successfully" and silently returns only the addons that came *after* the interruption. Tomlyn
/// replaces the earlier array instead of appending to it; TOML itself says both groups belong to one
/// array. dotquay never writes such a file, but <c>dotquay.lock</c> is committed (SPEC.md §7.3) and a
/// hand-resolved merge conflict can easily produce one — and silently losing entries from the
/// artifact that exists to guarantee reproducibility (goal #1) is the worst possible failure mode.
/// </remarks>
public class LockfileInterleavingTests
{
    private static string Sha(char c) => new(c, 40);

    private static readonly string Contiguous = $"""
        [[addon]]
        name = "root"
        git = "https://example.com/root.git"
        resolved = "{Sha('a')}"
        path = "addons/root"

        [[addon]]
        name = "child"
        git = "https://example.com/child.git"
        resolved = "{Sha('b')}"
        path = "addons/child"

        [[dotnet]]
        name = "Newtonsoft.Json"
        resolved = "13.0.3"
        """;

    private static readonly string Interleaved = $"""
        [[addon]]
        name = "root"
        git = "https://example.com/root.git"
        resolved = "{Sha('a')}"
        path = "addons/root"

        [[dotnet]]
        name = "Newtonsoft.Json"
        resolved = "13.0.3"

        [[addon]]
        name = "child"
        git = "https://example.com/child.git"
        resolved = "{Sha('b')}"
        path = "addons/child"
        """;

    [Fact]
    public void Read_ContiguousBlocks_ReadsEverything()
    {
        var parsed = LockfileReader.Read(Contiguous);

        Assert.Equal(["root", "child"], parsed.Addons.Select(a => a.Name));
        Assert.Single(parsed.Dotnet);
    }

    [Fact]
    public void Read_InterleavedBlocks_ThrowsInsteadOfDroppingEntries()
    {
        var exception = Assert.Throws<LockfileParseException>(() => LockfileReader.Read(Interleaved));

        // Before this guard, the same input returned exactly one addon ("child") and no error at all.
        Assert.Contains("2 [[addon]] block(s)", exception.Message);
        Assert.Contains("could only read 1", exception.Message);
    }

    [Fact]
    public void Read_InterleavedBlocks_ExplainsHowToFixIt()
    {
        var exception = Assert.Throws<LockfileParseException>(() => LockfileReader.Read(Interleaved));

        Assert.Contains("merge conflict", exception.Message);
        Assert.Contains("Group all [[addon]] blocks together", exception.Message);
    }

    [Fact]
    public void Read_InterleavedDotnetBlocks_AreCaughtToo()
    {
        var text = $"""
            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"

            [[addon]]
            name = "root"
            git = "https://example.com/root.git"
            resolved = "{Sha('a')}"
            path = "addons/root"

            [[dotnet]]
            name = "Serilog"
            resolved = "3.1.1"
            """;

        var exception = Assert.Throws<LockfileParseException>(() => LockfileReader.Read(text));

        Assert.Contains("[[dotnet]]", exception.Message);
    }

    [Fact]
    public void Read_EmptyLockfile_IsStillValid()
    {
        var parsed = LockfileReader.Read("");

        // A project that has been initialised but never installed has an empty lockfile; the guard
        // must not turn "nothing to read" into an error.
        Assert.Empty(parsed.Addons);
        Assert.Empty(parsed.Dotnet);
    }

    [Fact]
    public void Read_IndentedHeaders_AreCounted()
    {
        // TOML permits leading whitespace before a header. The guard counts what the parser would
        // accept, or it would report a phantom mismatch on a perfectly valid file.
        var text = $"""
              [[addon]]
              name = "root"
              git = "https://example.com/root.git"
              resolved = "{Sha('a')}"
              path = "addons/root"
            """;

        var parsed = LockfileReader.Read(text);

        Assert.Single(parsed.Addons);
    }

    [Fact]
    public void Read_RoundTripsWhatTheWriterProduces()
    {
        // The guard's core assumption: dotquay's own writer always groups blocks, so a file dotquay
        // wrote can never trip it.
        var original = LockfileReader.Read(Contiguous);

        var reparsed = LockfileReader.Read(LockfileWriter.Serialize(original));

        // Compared as sets: the writer sorts entries by name so its output is deterministic, so a
        // round trip preserves which entries exist, not the order they were written in.
        Assert.Equal(
            original.Addons.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal),
            reparsed.Addons.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(
            original.Dotnet.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal),
            reparsed.Dotnet.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}
