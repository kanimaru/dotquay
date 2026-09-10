# ADR / Design — v0.15: Git transport, URL identity, and why a fetch failed

**Status:** **Implemented and verified against a real NativeAOT binary**, 2026-09-10. 900 C# tests and
413 GDScript assertions green. CLI only — the Godot plugin needed no change (see *In the Godot
plugin*).
**Date:** 2026-09-10
**Deciders:** Kani

Motivating case: another developer set a project up for the first time and could not install it.

```
dotquay add https://gitlab.kemomi.de/wild-alliance/vbeami/ --path godot/addons/vbeami
| resolving the dependency graph
Failed to fetch dd4dd78... from git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git.
Shallow fetch: Host key verification failed. fatal: Could not read from remote repository.
Please make sure you have the correct access rights and the repository exists..
Full fetch: Host key verification failed. fatal: Could not read from remote repository.
Please make sure you have the correct access rights and the repository exists.
```

Three separate failures are visible in those five lines, and this doc addresses all three.

## Context

**They asked for HTTPS and got SSH.** The URL on the command line is `https://`. The URL that failed
is `git@`. Nothing was wrong with what they typed: `mtoon-shader` is a transitive dependency, and
`vbeami`'s own package manifest declares it as `git@gitlab.kemomi.de:...` because that is how its
author works. `DependencyGraphResolver.WalkAsync` enqueues each discovered `AddonSpec` verbatim, so
the author's transport propagates to every consumer downstream. A developer with no SSH key, no
agent, or — as here — no `known_hosts` entry cannot install a project every URL of which they wrote
as HTTPS, and there is nothing in *their* manifest they can edit to fix it. The SSH URL lives in
somebody else's repository.

**The message says everything twice and explains nothing.** `FetchCommitAsync` tries a shallow fetch
and falls back to a full one, so a failure carries two stderrs. They are the same words twice
whenever the cause is not specific to shallow fetching, which is nearly always. Worse, the one
sentence that matters — `Host key verification failed.` — sits in the middle, followed by git's
boilerplate `Please make sure you have the correct access rights`, which reads as an authentication
problem and is not one. `GitErrorClassifier` agreed with that misreading: `"permission denied"` and
`"access denied"` were auth markers, so a host-key rejection could be reported as
`error.kind = "auth"` and put a login prompt in front of a problem no credential can solve.

**Nothing told them beforehand.** The host key was missing before the first fetch and would have
stayed missing through every retry. `doctor` had no check for it.

There is also a fourth problem, latent rather than hit: `AddonUnifier` rejects a name required from
"different git repositories" by comparing `Spec.Git` **as strings, ordinally**. Two requirers
spelling one repository `https://host/g/r` and `git@host:g/r.git` were a hard error whose suggested
fix — add an `[overrides]` entry — asked the user to pick a winner between two identical things.

## Decision

### 1. A git URL is a value, not a string — `GitUrl`

`Dotquay.Core.Git.GitUrl` parses both of git's URL grammars (`scheme://` and the scp-like
`user@host:path`), normalising away everything that is spelling rather than meaning: transport, user,
default port, trailing slash, `.git` suffix, host casing. `Identity` is host plus path; two URLs with
the same `Identity` are the same repository.

Parsing follows git's rules, not `Uri`'s, because the scp-like form is not a URI and `Uri` mis-parses
it as scheme `git` with an opaque body. The Windows drive-letter ambiguity this creates (`C:\repos\x`
reads as host `C`) is resolved the way git resolves it: a single-letter authority followed by a
separator is a drive.

Two comparison decisions worth recording:

- **Host case-insensitive, path case-sensitive.** GitHub and GitLab both treat paths
  case-insensitively, so this is stricter than they need. Chosen anyway because the failure modes are
  not symmetric: comparing too strictly reports a conflict the user resolves with one `[overrides]`
  line, while comparing too loosely silently merges two distinct repositories into one install
  folder.
- **A local path is never any remote.** `file://` and filesystem paths get an identity nothing else
  can collide with. Deciding a working copy "is" its origin is what would let a graph walk substitute
  somebody's uncommitted edits for a pinned dependency.

`AddonUnifier` now compares `Identity` instead of raw strings. First-declared spelling is what gets
locked, consistent with how it already treats `path` and `install_as`.

### 2. Transport belongs to the consumer — `GitTransportPreference`

The addon author's URL is authoritative about **which repository** (host and path, preserved
exactly). The consuming project's own declarations are authoritative about **how to reach it**.
`GitTransportPreference.FromProjectUrls` builds a per-host transport map from the project's
`[addons.*]` and `[overrides.*]` entries; `DependencyGraphResolver` applies it to every
transitively-discovered URL before enqueueing it.

Rejected alternative: **an explicit `[git.transport]` table** mapping host to `ssh`/`https`. It is
greppable and unambiguous, and it is configuration every project must remember to write — so a fresh
clone on a new developer's PC hits exactly this wall until somebody adds it. Inference needs nothing:
a project that writes HTTPS URLs has already said everything required, and a clone inherits the
answer with it.

Four things it deliberately will not do:

- **Rewrite the project's own root URLs.** They are the evidence; rewriting them is circular.
- **Touch a host the project never described.** Speaking HTTPS to gitlab.kemomi.de is not evidence
  about github.com. Keys, credentials and firewall rules are per-host.
- **Guess when a host is described two ways.** A mirror, or a read-only HTTPS remote beside a
  push-capable SSH one, are both deliberate setups. A majority vote would invent an answer the
  project did not give, so such a host is left entirely alone.
- **Do it quietly.** A rewrite emits `GraphWarningKind.GitTransportRewritten`. The lockfile ends up
  naming a URL that appears in no manifest anywhere, and finding that out by diffing the lockfile
  against somebody else's `dotquay.toml` is not a reasonable ask.

Because `AddonSpec` needed a copy-with-one-field-changed, it became a `record`. Not a hand-written
copy helper: `LockedAddon` was a class with those, and they silently dropped `Folder`.

### 3. `doctor` checks host-key trust, offline — `KnownHostsReader`

A new `DoctorIssueKind.SshHostNotTrusted` reports every SSH host in the lockfile that this machine
has no trusted key for, grouped per host (the entry is per host; ten addons on one GitLab need one
fix). A `@revoked` key shares the kind and says so in the message — the remedy there is the opposite,
and the message must not send anyone to trust it harder.

Rejected alternative: **shell out to `ssh-keygen -F`.** It looks simpler and it inherits the exact
ambiguity being diagnosed. A machine can have two OpenSSH installs — on Windows the 8.1p1 in
`System32\OpenSSH` and the 9.x Git for Windows ships — and probing through one of them can fail at
key exchange before ever reaching the question. Both read the same `~/.ssh/known_hosts`, so the file
is the more reliable witness as well as the cheaper one.

Reading it means implementing OpenSSH's matching rules properly: comma-separated aliases, `!`
negation, `*`/`?` wildcards, `[host]:port` for non-default ports, `@cert-authority` and `@revoked`
markers, and **hashed `|1|salt|hash` entries** via HMAC-SHA1. Hashing is on by default on most Linux
distributions, so skipping the HMAC would report every correctly-trusted host on such a machine as
untrusted — a check confidently wrong exactly where it is most likely to run, which is how a check
earns a reputation for noise and stops being read.

### 4. The failure message says what happened

`GitErrorClassifier` grew from one boolean into `GitFailureKind`, matched most-specific-first:
`HostKeyVerification`, `SshAlgorithmMismatch`, `Authentication`, `HostUnreachable`,
`RepositoryNotFound`, `RemoteUnreadable`, `Unknown`. Each carries a remediation sentence naming the
host. `LooksLikeAuthenticationFailure` — the wire contract behind `error.kind = "auth"` — is now
exactly `Classify(...) == Authentication`, so a host-key rejection no longer masquerades as one.

`DescribeFetchFailure` reports the two stderrs separately only when they genuinely differ.

`SshAlgorithmMismatch` earns its own kind because of the second half of the report:

```
choose_kex: unsupported KEX method sntrup761x25519-sha512@openssh.com
```

That is an OpenSSH older than 8.5 talking to a 9.6 server. The message names an algorithm and reads
like a server misconfiguration, sending people to change the server rather than their PATH.

## Verified against a real binary

Two things the 900-test suite structurally could not see, both found by running the published
`dotquay.exe`:

1. **`DefaultPaths` named a Unix path on Windows.** It keyed the platform off whether the
   `ProgramData` variable was set. That variable is present in every normal Windows session and empty
   in some stripped-down ones — so a Windows machine was told its `known_hosts` lived in `/etc/ssh`.
   Now decided by `OperatingSystem.IsWindows()`, passed as a parameter so both branches are testable
   from either platform.

2. **ssh's diagnostic often never reaches dotquay at all.** When git runs with a redirected stderr —
   which is to say, whenever dotquay runs it — ssh's `Host key verification failed.` can go to the
   terminal instead of the pipe. Confirmed by driving raw `git fetch` through a redirected pipe
   against an untrusted host: all that arrives is `fatal: Could not read from remote repository`
   plus the boilerplate. It reached us in the original report and not on the machine this was built
   on, which is worse than either consistent outcome: the same fault yields a precise message on one
   developer's PC and a useless one on the next.

   Hence `GitFailureKind.RemoteUnreadable` — "the cause is real but off-screen". For an SSH URL its
   remedy points at `dotquay doctor`, which answers the likeliest question without a network, and at
   `ssh -T git@<host>` to see what git swallowed. It is checked last, so any more specific marker
   anywhere in the message still wins.

A third finding was about the machine rather than the code, and is why the BOM guard exists: the
`known_hosts` on the development machine begins `ef bf bd ef bf bd`, two replacement characters,
costing its first entry that host's trust. That is `ssh-keyscan host >> known_hosts` run in
PowerShell 5.1, whose `>>` writes UTF-16. `LookupIn` now strips a leading BOM — it cannot rescue an
already-mangled file, but it stops the next one being created.

## In the Godot plugin

Nothing to do. `dotquay_panel.gd` renders doctor issues by their `message` and never switches on
`kind`, so `ssh_host_not_trusted` displays correctly with no plugin change — the usual
`DoctorIssueKind`-is-a-wire-contract caution applies to consumers that branch on the value, and this
one does not. `JsonContractTests` pins the new wire name, and a new guard fact asserts that *every*
enum member maps to something other than `"unknown"`, since the mapping's `_ =>` arm means a
forgotten member compiles, runs and ships.

## Consequences

- A project whose manifest is consistently HTTPS installs on a machine with no SSH key at all, even
  when its dependencies' authors use SSH — and the reverse.
- A lockfile can name a URL no manifest contains. Warned about, never silent.
- Transport inference is evidence-based, so a project that mixes transports for one host gets no
  inference rather than a guess. `[overrides]` remains the explicit escape hatch.
- `doctor` gained a check that needs no network and predicts a failure that otherwise surfaces deep
  inside a fetch, to the person least equipped to diagnose it.
- `error.kind = "auth"` got narrower. A UI that offered a login prompt on a host-key failure will
  stop doing so, which is the point.
