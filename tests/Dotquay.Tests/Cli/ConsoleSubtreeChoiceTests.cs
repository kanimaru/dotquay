using Dotquay.Cli.Commands;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Sync;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// Covers the terminal prompt that confirms a guessed subtree (v0.20).
/// </summary>
/// <remarks>
/// The fakes-based suite structurally cannot see a real console, and a real console cannot be driven
/// from a test host — so what is pinned here is the part that is genuinely decidable in-process: what
/// each answer means. The gating that decides whether anyone is asked at all
/// (<see cref="ConsoleSubtreeChoice.For"/>) is the same <c>Console.IsInputRedirected</c> check
/// <c>ConsoleSourceApproval</c> already uses and is exercised by running the published binary with a
/// piped stdin, which is what every non-interactive caller is.
/// <para>
/// <see cref="Console.SetIn"/> and <see cref="Console.SetOut"/> are process-global, so these run
/// inside a collection of their own and restore both afterwards. xUnit parallelises across classes,
/// and a test that redirects the console without putting it back takes the whole run with it.
/// </para>
/// </remarks>
[Collection(nameof(ConsoleSubtreeChoiceTests))]
[CollectionDefinition(nameof(ConsoleSubtreeChoiceTests), DisableParallelization = true)]
public class ConsoleSubtreeChoiceTests {
    private static readonly SubtreeCandidate Suggestion =
        new("addons/sprouty_dialogs", "sprouty_dialogs", SubtreeMatchKind.NameShape);

    private static readonly SubtreeChoiceRequest Request = new(
        "SproutyDialogs",
        "https://github.com/SproutyLabs/SproutyDialogs",
        new SubtreeFinding("addons/SproutyDialogs", [Suggestion], Suggestion),
        Suggestion);

    private static (bool Accepted, string Output) Answer(string? typed) {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var captured = new StringWriter();

        try {
            // A null reader stands in for end-of-input: nothing was typed because there was nobody
            // and nothing left to type.
            Console.SetIn(new StringReader(typed ?? string.Empty));
            Console.SetOut(captured);

            return (new ConsoleSubtreeChoice().Accept(Request), captured.ToString());
        } finally {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData(" y ")]
    public void Accept_Yes(string typed) => Assert.True(Answer(typed).Accepted);

    [Fact]
    public void Accept_BareEnter_IsYes() {
        // Default yes, unlike the NuGet feed prompt's default no. That question grants a third party
        // the right to serve code into the project; this one only picks which folder of a repository
        // the user just named is the addon, and getting it wrong costs a `dotquay remove`.
        Assert.True(Answer("\n").Accepted);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("addons/something_else")]
    public void Accept_AnythingElse_IsNo(string typed) => Assert.False(Answer(typed).Accepted);

    [Fact]
    public void Accept_EndOfInput_IsNo() {
        // Not the same as a bare Enter, and the distinction matters: a console whose input has closed
        // answered nothing, and reading silence as a yes would lose the "nowhere that cannot ask
        // adopts on the user's behalf" rule on the one path that is allowed to ask.
        Assert.False(Answer(null).Accepted);
    }

    [Fact]
    public void Accept_AsksWithTheEvidenceAndTheDestination() {
        // A prompt that says only "use it?" is a prompt people answer without reading. Both halves of
        // the answer have to be on screen: the subtree, and the folder it lands in — the second is
        // what Godot's res:// paths are written against, and the one nobody thinks to check.
        var output = Answer("y").Output;

        Assert.Contains("addons/SproutyDialogs", output);
        Assert.Contains("addons/sprouty_dialogs", output);
        Assert.Contains("separator convention", output);
        Assert.Contains("dotquay.toml", output);
    }

    [Fact]
    public void For_Json_NeverAsks() =>
        Assert.IsType<DeclineSubtreeChoice>(ConsoleSubtreeChoice.For(json: true));
}
