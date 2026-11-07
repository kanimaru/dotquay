using Dotquay.Core.Auth;

namespace Dotquay.Tests.Support;

/// <summary>Ready-made <see cref="CredentialResolver"/>s for tests whose subject isn't
/// authentication — so a workflow test says "no credentials configured" in one word instead of
/// assembling an environment reader and a store it doesn't care about.</summary>
public static class TestCredentials {
    /// <summary>Nothing configured anywhere: every lookup comes back anonymous.</summary>
    public static CredentialResolver None() =>
        new(new FakeEnvironmentReader(), new InMemoryCredentialStore());

    /// <summary>One host-wide stored credential, as <c>dotquay login &lt;host&gt;</c> would leave
    /// it.</summary>
    public static CredentialResolver ForHost(string host, string username, string token) =>
        new(new FakeEnvironmentReader(), new InMemoryCredentialStore().Add(host, username, token));
}
