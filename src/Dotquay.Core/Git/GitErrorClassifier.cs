namespace Dotquay.Core.Git;

/// <summary>What git was actually complaining about, as far as its stderr can be read.</summary>
/// <remarks>
/// Ordered from most specific to least: <see cref="Classify"/> returns the first match, and several
/// of these co-occur in one message. An SSH host-key rejection, for instance, ends with git's generic
/// "make sure you have the correct access rights", which reads as an authentication problem and is
/// not one -- no credential will ever fix it.
/// </remarks>
public enum GitFailureKind
{
    /// <summary>Nothing recognised. Reported as a plain git error with git's own words.</summary>
    Unknown,

    /// <summary>
    /// The server's host key is not in <c>known_hosts</c>, or no longer matches what is.
    /// </summary>
    /// <remarks>
    /// Distinguished from authentication because the remedies have nothing in common and the wrong
    /// one wastes real time: this is about trusting the <em>server</em>, and dotquay's credential
    /// store, a token, or an SSH agent are all irrelevant to it.
    /// </remarks>
    HostKeyVerification,

    /// <summary>
    /// The SSH client and server share no acceptable key exchange, host key, cipher or MAC
    /// algorithm.
    /// </summary>
    /// <remarks>
    /// Nearly always an old OpenSSH talking to a new server -- on Windows, the 8.1p1 in
    /// <c>System32\OpenSSH</c> shadowing the 9.x that Git for Windows ships. Worth its own kind
    /// because the message names an algorithm and reads like a server misconfiguration, sending
    /// people to change the server rather than their PATH.
    /// </remarks>
    SshAlgorithmMismatch,

    /// <summary>Credentials were missing or rejected.</summary>
    Authentication,

    /// <summary>The host could not be reached at all: DNS, routing, or a refused connection.</summary>
    HostUnreachable,

    /// <summary>
    /// The server answered and reported no such repository.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="Authentication"/>, even though GitHub returns exactly
    /// this for a private repository fetched without credentials. A typo in the URL produces the same
    /// message and is at least as likely, and labelling it an auth failure sends someone to configure
    /// a token when what they need is to fix a character.
    /// </remarks>
    RepositoryNotFound,

    /// <summary>
    /// git could not talk to the remote and did not say why.
    /// </summary>
    /// <remarks>
    /// Its own transport said something; git just did not pass it on. Observed while verifying this
    /// against a real binary: when git runs with a redirected stderr -- which is to say, whenever
    /// dotquay runs it -- ssh's diagnostic can go to the terminal instead of the pipe, so
    /// "Host key verification failed." never arrives and all that is left is git's generic advice
    /// about access rights. Whether it arrives is machine-dependent, which is worse than either
    /// consistent outcome: the same underlying fault produces a precise message on one developer's
    /// PC and a useless one on the next.
    /// <para>
    /// So this kind means "the cause is real but off-screen", and its remedy is to say where to look
    /// rather than to guess.
    /// </para>
    /// </remarks>
    RemoteUnreadable,
}

/// <summary>
/// Reads <c>git</c>'s stderr well enough to say what went wrong and what to do about it.
/// </summary>
/// <remarks>
/// String matching on another program's output is unpleasant, and it is the only option: git exits
/// 128 for almost everything and has no machine-readable failure categories. It is done here, at the
/// point that still holds the exit code and stderr, rather than in the presentation layer -- so there
/// is exactly one list to correct when a git release rewords a message, and nothing downstream is
/// re-parsing prose.
/// </remarks>
public static class GitErrorClassifier
{
    // Matched in this order. Each group is checked in full before the next, so a message carrying
    // markers from two groups is reported as the more specific one.
    private static readonly string[] HostKeyMarkers =
    [
        "host key verification failed",
        "remote host identification has changed",
        "no ecdsa host key is known",
        "no rsa host key is known",
        "no ed25519 host key is known",
    ];

    private static readonly string[] AlgorithmMarkers =
    [
        "unsupported kex method",
        "no matching key exchange method found",
        "no matching host key type found",
        "no matching cipher found",
        "no matching mac found",
        "unable to negotiate with",
    ];

    private static readonly string[] AuthenticationMarkers =
    [
        "authentication failed",
        "could not read username",
        "could not read password",
        "terminal prompts disabled",
        "http basic: access denied",
        "invalid username or password",
        "permission denied",
        "access denied",
        "403 forbidden",
        "401 unauthorized",
    ];

    private static readonly string[] UnreachableMarkers =
    [
        "could not resolve hostname",
        "could not resolve host",
        "connection timed out",
        "connection refused",
        "network is unreachable",
        "no route to host",
        "operation timed out",
    ];

    // Checked last: git prints this alongside almost every transport failure, so any more specific
    // marker anywhere in the message is the better answer.
    private static readonly string[] UnreadableMarkers =
    [
        "could not read from remote repository",
    ];

    private static readonly string[] NotFoundMarkers =
    [
        "repository not found",
        "does not appear to be a git repository",
        "the project you were looking for could not be found",
    ];

    /// <summary>Reads <paramref name="stderr"/> as one <see cref="GitFailureKind"/>.</summary>
    public static GitFailureKind Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return GitFailureKind.Unknown;
        }

        var lowered = stderr.ToLowerInvariant();

        return Any(lowered, HostKeyMarkers) ? GitFailureKind.HostKeyVerification
            : Any(lowered, AlgorithmMarkers) ? GitFailureKind.SshAlgorithmMismatch
            : Any(lowered, AuthenticationMarkers) ? GitFailureKind.Authentication
            : Any(lowered, UnreachableMarkers) ? GitFailureKind.HostUnreachable
            : Any(lowered, NotFoundMarkers) ? GitFailureKind.RepositoryNotFound
            : Any(lowered, UnreadableMarkers) ? GitFailureKind.RemoteUnreadable
            : GitFailureKind.Unknown;
    }

    /// <summary>
    /// Whether <paramref name="stderr"/> reads as "your credentials were missing or rejected".
    /// </summary>
    /// <remarks>
    /// Kept as its own predicate because it is a wire contract: it decides
    /// <c>error.kind = "auth"</c> (SPEC.md section 17), which is what lets a UI offer a login prompt
    /// instead of a wall of text. Narrower than it used to be, and deliberately: a host-key rejection
    /// carries git's generic "correct access rights" wording and used to be reported as an auth
    /// failure, putting a login prompt in front of a problem no credential can solve.
    /// </remarks>
    public static bool LooksLikeAuthenticationFailure(string? stderr) =>
        Classify(stderr) == GitFailureKind.Authentication;

    /// <summary>
    /// A sentence saying what to do about <paramref name="kind"/> on <paramref name="repositoryUrl"/>'s
    /// host, or null when git's own message is already the best available answer.
    /// </summary>
    /// <remarks>
    /// Written for the person hitting this on a machine that has never talked to the host before --
    /// which is when all of these actually occur. The SSH hints name the two-OpenSSH trap explicitly
    /// because it is invisible from the error alone: <c>ssh-keyscan</c> can fail at key exchange
    /// while git, using a different binary, gets far enough to reject the host key, so the two
    /// symptoms look unrelated and the obvious fix appears not to work.
    /// </remarks>
    public static string? Remediation(GitFailureKind kind, string? repositoryUrl)
    {
        var host = GitUrl.TryParse(repositoryUrl, out var url) && url is not null && url.Host.Length > 0
            ? url.Host
            : null;
        var hostText = host ?? "the server";

        return kind switch
        {
            GitFailureKind.HostKeyVerification =>
                $"ssh has no trusted host key for {hostText}. Verify its fingerprint out of band, then add it: "
                + $"'ssh-keyscan {hostText}' prints the keys and 'ssh-keygen -F {hostText}' checks what is already "
                + "trusted. Note that git may use a different ssh than the first one on PATH, and only that one's "
                + "known_hosts counts.",

            GitFailureKind.SshAlgorithmMismatch =>
                $"the ssh client and {hostText} agree on no common algorithm, which usually means the ssh being "
                + "run is older than the server expects. Check with 'ssh -V' -- on Windows the OpenSSH in "
                + "System32 often shadows the newer one shipped with Git.",

            GitFailureKind.Authentication =>
                $"git was refused for want of credentials. 'dotquay login {hostText}' stores a token for this host; "
                + "for an ssh URL, check that your key is loaded and registered with the server.",

            GitFailureKind.HostUnreachable =>
                $"{hostText} could not be reached at all -- check the hostname, your network, and any VPN the host "
                + "sits behind.",

            GitFailureKind.RepositoryNotFound =>
                $"{hostText} answered but reported no such repository. That is the same answer it gives for a "
                + "private repository you are not allowed to see, so check the path for a typo first, then your access.",

            GitFailureKind.RemoteUnreadable when IsSsh(repositoryUrl) =>
                $"git did not report why. ssh's own explanation is often lost when git runs from a tool rather "
                + $"than a terminal, and the usual cause on a machine that has not reached {hostText} before is an "
                + $"untrusted host key. Run 'dotquay doctor', which checks that without needing the network, or "
                + $"reproduce it directly with 'ssh -T git@{hostText}'.",

            GitFailureKind.RemoteUnreadable =>
                $"git did not report why. Check that {hostText} is reachable and that your credentials for it are "
                + "current.",

            _ => null,
        };
    }

    private static bool IsSsh(string? repositoryUrl) =>
        GitUrl.TryParse(repositoryUrl, out var url) && url is { Transport: GitTransport.Ssh };

    /// <summary>
    /// The message for a failed fetch: what was being fetched, what git said, and what to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fetching tries shallow first and falls back to a full fetch, so a failure has two stderrs. They
    /// are usually the same words twice -- a host that refuses a connection refuses both attempts --
    /// and printing both taught readers to skim past the part that mattered. They are reported
    /// separately only when they genuinely differ, which is the case worth seeing: a shallow fetch
    /// refused for one reason and the fallback failing for another.
    /// </para>
    /// <para>
    /// Pure and public so it can be tested directly against real captured stderr. Every bug this is
    /// meant to fix showed up as unhelpful <em>text</em>, which nothing tests when the text is built
    /// inline at the throw site.
    /// </para>
    /// </remarks>
    public static string DescribeFetchFailure(
        string repositoryUrl, string commitSha, string? shallowStdErr, string? fullStdErr)
    {
        var shallow = (shallowStdErr ?? string.Empty).Trim();
        var full = (fullStdErr ?? string.Empty).Trim();

        var detail = string.Equals(shallow, full, StringComparison.Ordinal) || full.Length == 0
            ? shallow
            : shallow.Length == 0
                ? full
                : $"Shallow fetch: {shallow} Full fetch: {full}";

        // Classified from whichever attempt actually said something. The full fetch is the last word
        // on whether the repository is reachable at all, so it is preferred when the two disagree.
        var kind = Classify(full);
        if (kind == GitFailureKind.Unknown)
        {
            kind = Classify(shallow);
        }

        var message = $"Failed to fetch {commitSha} from {repositoryUrl}.";

        if (detail.Length > 0)
        {
            message += " " + detail;
            if (!detail.EndsWith('.'))
            {
                message += ".";
            }
        }

        return Remediation(kind, repositoryUrl) is { } remediation
            ? message + " " + char.ToUpperInvariant(remediation[0]) + remediation[1..]
            : message;
    }

    private static bool Any(string loweredStdErr, string[] markers) =>
        markers.Any(marker => loweredStdErr.Contains(marker, StringComparison.Ordinal));
}
