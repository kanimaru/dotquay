using Dotquay.Cli;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// <see cref="TtyGuard"/>'s decision is a pure function of one bool specifically so it can be
/// unit-tested at all — a real interactive TTY is not something an automated test can reproduce
/// (the test runner's own stdin is typically redirected too, so "spawn the real binary and check"
/// would just prove the test harness's stdin, not a genuine terminal). This is the same tiering the
/// project already applies elsewhere (docs/DESIGN-v0.18-MCP.md §8): verify what fakes/pure logic
/// can see here, leave what only a real OS console can show to manual/real-process verification.
/// </summary>
public sealed class TtyGuardTests {
    [Fact]
    public void Refuses_when_stdin_is_not_redirected() {
        Assert.True(TtyGuard.ShouldRefuse(isInputRedirected: false));
    }

    [Fact]
    public void Allows_when_stdin_is_redirected() {
        Assert.False(TtyGuard.ShouldRefuse(isInputRedirected: true));
    }

    [Fact]
    public void Exit_code_matches_the_design_doc() {
        // docs/DESIGN-v0.18-MCP.md §12 OQ-2 names exit 2 specifically — pin it so a future refactor
        // can't silently drift to some other code an MCP client's launcher doesn't expect.
        Assert.Equal(2, TtyGuard.ExitCode);
    }

    [Fact]
    public void Message_names_the_flag_a_confused_user_actually_needs() {
        Assert.Contains("dotquay mcp --project", TtyGuard.Message);
    }
}
