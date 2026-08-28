# Design — v0.7: multiple NuGet feeds and a shared credential store

Status: **implemented and verified against a real NativeAOT binary, 2026-08-28.**

All of §2–§6 is built, the suite is green (551 tests), and the AOT checklist below passed against a
published `win-x64` binary. `docs/SPEC.md` describes the end state (§6, §7.1, §7.3, §11.1, the new
§11.2, §12, §13 P1-9/P1-10, §17).

### Building the AOT binary on this machine

Two environment quirks block `dotnet publish -p:PublishAot=true`, neither of which is a property of
this change. Both have to be worked around together:

1. **`Cross-OS native compilation is not supported`.** The ILCompiler guard is
   `'$(OS)' != 'Windows_NT' and '$(_targetOS)' == 'win'`, and MSBuild takes `$(OS)` from the
   environment variable of the same name — which some shells do not set. `set OS=Windows_NT` clears
   it. Nothing is actually cross-compiling.
2. **`Platform linker not found`.** ILCompiler's own `findvcvarsall.bat` fails to discover the VS
   Build Tools install even though `link.exe`, the MSVC toolset, and the Windows SDK are all present.
   Run inside a `vcvars64.bat` shell and pass `-p:IlcUseEnvironmentalTools=true`, which tells
   ILCompiler to use the tools already on PATH instead of rediscovering them.

A third quirk follows from the fix for the second: **`vcvars64.bat` exports `Platform=x64`**, which
MSBuild folds into the output path, quietly redirecting the whole build into `bin\x64\Release\…`
instead of `bin\Release\…`. That is a second, parallel output tree that no other command writes to or
cleans, and it is easy to miss because the build still succeeds. Clear it.

```bat
call "…\VC\Auxiliary\Build\vcvars64.bat"
set "Platform="
set "OS=Windows_NT"
dotnet publish src\Dotquay.Cli\Dotquay.Cli.csproj -c Release -r win-x64 ^
  -p:PublishAot=true -p:IlcUseEnvironmentalTools=true -tl:off > build.log 2>&1
```

Produces an 11 MB `dotquay.exe` in `src\Dotquay.Cli\bin\Release\net10.0\win-x64\publish\` — inside
`bin/`, which is already gitignored, so there is nothing to clean up afterwards. Do **not** publish to
an `artifacts/` directory: it isn't in `.gitignore` and shows up as 60 MB of untracked files.

Notes on capturing `build.log`: `-tl:on` writes ANSI escape sequences into the file, and `dotnet`
emits a few OSC progress sequences even without it — strip them before committing, or the log is
unreadable in a diff. `--no-incremental` is not a valid `dotnet publish` switch; to force the full
rebuild that makes the log a complete record (all 20 trim/AOT warnings rather than three "up-to-date"
lines), delete `bin\Release` and `obj\Release` for both projects first.

### Verified against the real NativeAOT binary (2026-08-28)

A clean build is not evidence in this project — Spectre.Console.Cli and NuGet.Protocol both linked
fine and failed only once published. Each of this change's AOT-specific risks was exercised against
`artifacts/aot/dotquay.exe`:

- **`Environment.ProcessPath` resolves** (the risk: `Assembly.Location` is empty under AOT, which
  would have left git with no credential helper and no error to say so). Proved with a `git.exe` shim
  placed ahead of real git on PATH, recording what the AOT binary handed its child:

  ```
  ARGS: ls-remote --heads https://gitlab.example.com/kani/addon main
    GIT_TERMINAL_PROMPT=0
    GIT_CONFIG_COUNT=1
    GIT_CONFIG_KEY_0=credential.helper
    GIT_CONFIG_VALUE_0=!"F:/projects/godot/dotquay/artifacts/aot/dotquay.exe" credential-helper
  ```

- **Real git accepts that config value and invokes the AOT helper.** `git credential fill` against a
  host with a stored credential returned `username=…` / `password=…` from dotquay — and did so
  *after* Git Credential Manager ran first and declined, which is the append-not-reset ordering of D6
  demonstrated rather than asserted.
- **`JsonEnvelope<LoginJson>` is registered.** `login --list --json` emits a full envelope; a missing
  `JsonTypeInfo` fails only in the published binary, which is why this is on the list.
- **stdin decoding and the TOML store round-trip** under AOT, including the BOM case below.

Two items could not be exercised here and remain open, both narrow:

- `File.SetUnixFileMode` — a no-op on Windows by design, so it needs a Linux publish to mean
  anything.
- `Console.ReadKey(intercept: true)` — the interactive no-echo prompt needs a real TTY, which the
  automation shell isn't. The scripted path (`--token-stdin`) is what CI uses and is verified.

### Verified end to end under JIT (2026-08-28, real CLI binary)

- `login --token-stdin`, `login --list`, `logout` — round-trip through a real
  `~/.dotquay/credentials.toml`, token never printed.
- `credential-helper get` — real git protocol on stdin/stdout, correct answer for a known host,
  empty output and exit 0 for an unknown one, hidden from `--help`.
- **The hang is fixed.** `add` against a private repository that refuses access now terminates with a
  JSON envelope instead of waiting forever on a prompt nobody can see. It reports `kind: "git"`, not
  `"auth"` — correct per §4.3, since GitLab answers "not found or you don't have permission" and a
  mistyped URL produces the same words.
- **The manifest-rewrite trap (§2.7) is closed.** A hand-written second `[nuget.sources.*]` block and
  a `source = "..."` pin both survive a mutating command's wholesale rewrite.
- `doctor --json` reports `secret_in_manifest` for a token pasted into `dotquay.toml`, without
  quoting the token back.

### Verified against a real self-hosted GitLab (gitlab.kemomi.de, 2026-08-28)

- **Git already works, and `dotquay login` is not what makes it work.** Git Credential Manager holds
  a credential for that host and answers first, so `git ls-remote` through dotquay authenticates
  successfully with no dotquay login at all. Confirmed by `add --branch <nonexistent>`, which has to
  authenticate to answer and reports "branch not found" rather than a permission error. A
  `dotquay login` for a host GCM already covers has **no effect on git** — by design (D6 appends), but
  worth stating, because "I logged in and nothing changed" is otherwise a mystery.
- **The NuGet side is where login matters**, since GCM is not involved in either dotquay's own HTTP
  client or `dotnet restore`'s `NuGetPackageSourceCredentials_*` variables.
- **GitLab returns 404, not 401/403, for a project you may not see — proven, after first being
  asserted on too little evidence.** The initial claim rested on one observation (`projects/1` →
  404) that was fully explained by "project 1 doesn't exist", and was rightly challenged. The
  discriminating test: `wild-alliance/dotquay` is *known* to exist — the machine's git remote points
  at it and `ls-remote` authenticates against it — yet unauthenticated it returns **404**, identical
  to a project name known not to exist. Consistent with GitLab's documented practice of denying with
  a generic 404 rather than disclosing existence.

  Consequences, both now in the code:
  - This falsifies the naive reading of §2.3's "404 means not here, try the next". What saves ordered
    resolution is that the failure lands on the **service index**, which is fatal for that feed rather
    than a fall-through — so a private feed you aren't logged into stops the operation instead of
    silently resolving the package from nuget.org.
  - Because 404 is genuinely ambiguous, the message must not assert one reading. It branches on
    whether a credential was sent: unauthenticated, "log in" is worth suggesting; **authenticated, it
    is the wrong advice** — an admin token that can see everything still gets 404 for a URL that names
    no project, so the message blames the URL instead.
  - Separately discovered: a GitLab NuGet feed needs the project's **numeric ID**. A URL-encoded
    project path — accepted almost everywhere else in the v4 API — is rejected by the package registry
    endpoint with 400, which now has its own hint.

### Three bugs found by running it, not by testing it

Both were invisible to the unit suite and are recorded because the class of mistake will recur.

1. **A piped token was being stored with a UTF-8 BOM glued to the front.** Windows PowerShell 5.1
   emits UTF-8 with a BOM, and `Console.In` decodes the pipe using the console's OEM codepage, so the
   three BOM bytes arrived as three ordinary characters. The stored credential then looks perfectly
   correct in `login --list` — which prints no token, by design — and is rejected by every server with
   nothing anywhere to explain why. Fixed by reading `Console.OpenStandardInput()` with
   `detectEncodingFromByteOrderMarks`, which also stops a non-ASCII token being mangled by a codepage
   that cannot represent it. `--token-stdin` is the documented path for scripts and CI, so this was
   the common case.
2. **Non-ASCII punctuation in generated files renders as mojibake.** The credential file's header and
   the `init` scaffold's comment both used an em-dash; anything reading them as ANSI shows `â€"`. Now
   plain ASCII, matching the reasoning already recorded for the `.gitignore` writer. Asserted by a
   test rather than left to review.
3. **Any non-401 HTTP failure on a feed crashed the CLI.** `NuGetV3Client` used
   `EnsureSuccessStatusCode()`, whose `HttpRequestException` nothing caught: pointing a project at a
   real GitLab feed URL for a project that doesn't exist printed a stack trace and — under `--json` —
   produced **no envelope at all**, silently breaking the contract the editor plugin depends on
   (SPEC.md §17). Now a typed `NuGetSourceException` (plus a transport-error wrap for DNS/TLS/refused
   connections), translated by the workflow, with a `HttpRequestException` backstop in
   `JsonOutput.Fail` so nothing of this shape can escape again.

Two problems, one root cause: dotquay assumes *one* private feed and *one* credential, held in
environment variables, scoped to nothing.

1. **One feed per project.** `[project] nuget_source` is a single string
   (`ProjectManifest.NuGetSource`), `NuGetConfigWriter` owns exactly one `<add key="dotquay">` entry,
   and `DotnetInstaller.BuildCredentialEnvironment` emits exactly one
   `NuGetPackageSourceCredentials_dotquay`. GitLab publishes a NuGet feed **per project** — a
   `.../api/v4/projects/<id>/packages/nuget/index.json` per repository. Any Godot project consuming
   C# packages from more than one GitLab repo is already outside what dotquay can express.
2. **No credential store.** `NuGetCredentials.FromEnvironment()` reads `DOTQUAY_NUGET_USERNAME` and
   `DOTQUAY_NUGET_TOKEN` — a single pair, re-exported per shell, with no host scoping. Git gets
   nothing at all: `ProcessGitClient` passes no environment to its child, so a private addon repo
   works only if the machine's git is already configured, and an interactive credential prompt
   **hangs the process** (stdout/stderr are redirected, stdin is not, and nothing sets
   `GIT_TERMINAL_PROMPT`).

The fix is one idea applied twice: **credentials are keyed by host, not by project**, and live in a
shared user-level store the way `docker login` works. Feeds then become a list, because there is no
longer a reason for there to be one.

---

## 1. Decisions

| # | Decision | Alternatives rejected |
|---|---|---|
| D1 | Named sources: `[nuget.sources.<name>] url = "..."`, ordered, with an optional per-package `source = "<name>"` pin | A bare `nuget_sources = [url, ...]` list (no way to pin, so a name present on two feeds resolves by accident of order); per-package inline URLs (the same GitLab URL repeated on every package from that repo) |
| D2 | Unpinned packages resolve **first-match in declared order**, and the winning source is written to the lockfile | "Query all, take the highest version" — that is a dependency-confusion substitution attack with extra steps: a public package can outrank a private one of the same name |
| D3 | dotquay writes `<packageSourceMapping>` into `NuGet.Config`, so `dotnet restore` is bound to the same feed the resolver chose | Leaving restore to search every source. Without mapping, multi-feed support is a lie: dotquay would report resolving `Kani.Core` from `core` while restore silently pulled a same-named package from nuget.org |
| D4 | Shared credential file at `~/.dotquay/credentials.toml`, plaintext, `0600` on Unix | OS keychain (DPAPI/libsecret/Keychain) — needs P/Invoke or shelling out per platform, which fights the NativeAOT single-binary goal; env-var references only — then `login` isn't a login |
| D5 | Credentials keyed by `host[:port]` with an optional path prefix; **longest prefix wins** | Keyed by source name (a GitLab PAT is per-host and serves every project feed on it — per-name keying would make the user log in N times for one token) |
| D6 | dotquay registers **itself** as a git credential helper on its own git child processes, appended after the user's existing helpers | URL rewriting (`https://user:token@host/...` — the token lands in the cache's `.git/config` and in every error message); `-c http.<url>.extraheader=` (the token becomes a command-line argument, visible in `ps` to every user on the box) |
| D7 | Precedence: per-source env > legacy global env > credential file > anonymous | File over env — CI injects secrets as env vars, and a repo-level store would have to be written at pipeline start just to be read back |
| D8 | Environment access moves behind an `IEnvironmentReader` seam | Continuing to mutate process environment variables in tests. This is already a latent flake (`NuGetV3ClientFactoryTests`/`DotnetInstallerTests` save-set-restore process state and rely on xunit running facts within a class sequentially — xunit parallelises across *classes* by default) |

---

## 2. Multiple NuGet feeds

### 2.1 Manifest schema

```toml
[nuget.sources.core]
url = "https://gitlab.example.com/api/v4/projects/123/packages/nuget/index.json"

[nuget.sources.tools]
url = "https://gitlab.example.com/api/v4/projects/456/packages/nuget/index.json"

[dotnet.Kani.Core]
version = "1.2.*"
source = "core"          # optional pin; unpinned searches sources in declared order

[dotnet.Newtonsoft.Json]
version = "13.0.*"       # unpinned → found on nuget.org
```

`[project] nuget_source = "..."` still parses and is desugared to a source named `default`. Nothing
in the wild has to change.

**Ordering is load-bearing** (D2), so sources are a list, not a dictionary:

```csharp
public sealed record NuGetSourceSpec(string Name, string Url);

// ProjectManifest
public IReadOnlyList<NuGetSourceSpec> NuGetSources { get; init; } = [];
```

`ProjectManifest.NuGetSource` (the single string) is removed; `ManifestReader` builds the list from
`[nuget.sources.*]` in file order plus the legacy key. `Dictionary<string, T>` does not guarantee
enumeration order by contract, and this is not a place to depend on an implementation detail.

Source names are validated at parse time against `^[A-Za-z0-9][A-Za-z0-9._-]*$`. Not cosmetic: the
name is interpolated into a `NuGetPackageSourceCredentials_<key>` environment variable name and into
an XML attribute. Note the limit of what that buys — `NuGetPackageSourceCredentials_dotquay-core` is
a legal .NET environment-variable name (`ProcessStartInfo.Environment` sets it fine, which is the
only path that matters here) but **not** a portable POSIX shell one, so it cannot be `export`ed by
hand. The regex prevents genuinely broken names; it does not make the variable shell-settable. Anyone
wanting to set it manually should use `DOTQUAY_NUGET_TOKEN_<SOURCE>` (§3.3), which is normalized to
`[A-Z0-9_]`.

**Duplicate `default`.** A manifest carrying both `[project] nuget_source` and an explicit
`[nuget.sources.default]` is a parse error naming both, not a silent precedence rule. The two mean
the same thing and disagreeing about the URL has no correct answer.

### 2.2 The `source` key vs. dotted package IDs

`ManifestReader.CollectDotnetSpecs` walks nested tables because `[dotnet.Newtonsoft.Json]` parses as
`dotnet → Newtonsoft → Json`. It currently throws on any non-table value whose key isn't `version`.
That guard must also admit `source`.

The two are told apart **by TOML type, not by name**:

- `source = "core"` (a string) under a table that also has `version` → a pin on that package.
- `[dotnet.Foo.source] version = "1.0"` (a table) → a package literally named `Foo.source`.

A table and a string cannot share a key in TOML, so this is unambiguous. It is also obscure enough
to deserve a test and a comment. The accepted consequence: a package literally named `Foo.source`
can never carry a pin, because `[dotnet.Foo] source = ...` would then be ambiguous with it. That is
a price worth one sentence in the error message if anyone ever hits it.

### 2.3 Read side — `MultiSourceNuGetClient`

`INuGetClientFactory.Create(string? sourceUrl)` becomes:

```csharp
INuGetClient Create(NuGetSourceSet sources, string? pinnedSourceName = null);
```

`NuGetSourceSet` is the resolved, ordered set (declared sources, plus nuget.org appended as an
implicit last resort unless a source already points at it). A pin narrows the set to one entry; an
unknown pin name is a manifest error naming the sources that do exist.

`MultiSourceNuGetClient : INuGetClient` fans out over the set:

- `GetVersionsAsync` queries in order and returns the **first** source with a non-empty result.
- A package present on more than one source produces a warning (`§17` `warnings[]`) naming both and
  suggesting a `source =` pin. It does not fail — that would break the common case where a private
  feed mirrors a public package.
- A 404 means "not here, try the next". A **401/403 does not** — it means "this feed might have it
  and won't say", and silently falling through to a public feed is exactly the substitution risk D2
  exists to prevent. Auth failures abort with `error.kind = "auth"` naming the source and how to
  authenticate it.

**Two seam changes this needs, which are not optional.**

*Telling 401 from 404 needs a typed exception.* `NuGetV3Client.GetVersionsAsync` collapses 404 into
`[]` and calls `EnsureSuccessStatusCode()` for everything else
(`NuGetV3Client.cs:80-85`), so a 401 arrives as a bare `HttpRequestException`. Worse,
`GetPackageBaseAddressAsync` calls `EnsureSuccessStatusCode()` on the **service index**
(`NuGetV3Client.cs:102-103`) — on a private feed a bad credential fails *there*, before any package
query runs. Branching on `HttpRequestException.StatusCode` from the aggregator would be guessing at
another class's internals, so `NuGetV3Client` throws `NuGetAuthenticationException(sourceName, url)`
on 401/403 at both points, and `MultiSourceNuGetClient` catches that one type.
`PackageExistsAsync` delegates to `GetVersionsAsync` (`NuGetV3Client.cs:92-93`) and inherits it.

*Recording the winning source needs a return-shape change.* `INuGetClient.GetVersionsAsync` returns
`IReadOnlyList<string>` (`Abstractions/INuGetClient.cs:14`), which has nowhere to put a source name,
and `LockedDotnet` is constructed in exactly one production place — `DotnetInstaller.cs:56`, which
never sees a NuGet client. So the interface returns a small record instead:

```csharp
public sealed record NuGetVersionQuery(IReadOnlyList<string> Versions, string? SourceName);
```

`NuGetV3Client` returns its own name; `MultiSourceNuGetClient` returns the winner's. The workflow
threads `SourceName` into `DotnetInstaller.InstallAsync`, which writes it onto `LockedDotnet`. This
touches `INuGetClient`, `NuGetV3Client`, `FakeNuGetClient`, and `DotnetInstaller.InstallAsync`'s
signature. Worth stating plainly: the earlier claim that aggregation behind `INuGetClient` leaves the
workflow untouched was wrong — it keeps the *resolution* logic out of the workflow, which is the
actual benefit, but the workflow still has to carry a source name it did not carry before.

**Every `manifest.NuGetSource` site**, not just the three read-side ones:

| `DependencyWorkflow.cs` | Today | After |
|---|---|---|
| 282, 1087, 1407 | `_nuGetClientFactory.Create(manifest.NuGetSource)` | `Create(sourceSet, spec.Source)` |
| 285, 339, 1420 | `InstallDotnetOrThrowAsync(..., manifest.NuGetSource, ...)` | takes the `NuGetSourceSet` |
| 344, 1455 | `RestoreDotnetOrThrowAsync(..., manifest.NuGetSource, ...)` | takes the `NuGetSourceSet` |
| 1185-1186, 1198 | private helpers taking `string? nuGetSourceUrl` | take the `NuGetSourceSet` |

`InitService.cs:20` mentions the property in a doc comment and needs rewording.

### 2.4 Write side — `NuGet.Config`

`NuGetConfigWriter.EnsureSource` becomes `EnsureSources(IFileSystem, projectDirectory,
NuGetSourceSet)` and:

- writes one `<add key="dotquay-<name>" value="<url>" />` per declared source,
- **prunes** `dotquay-*` keys it owns that are no longer declared (a removed feed must stop being
  searched), and touches nothing else — same "only what you own" rule as
  `CsprojPackageReferenceWriter`,
- grandfathers exactly one key: the source named `default` (i.e. a legacy `[project] nuget_source`)
  keeps the bare `dotquay` key it already has on disk, so no existing project's `NuGet.Config` is
  rewritten by upgrading,
- writes credentials **never** — unchanged, and now enforced by a test (§5).

**Package source mapping (D3).** When at least one source is declared, the writer also emits:

```xml
<packageSourceMapping>
  <packageSource key="dotquay-core">
    <package pattern="Kani.Core" />
  </packageSource>
  <packageSource key="nuget.org">
    <package pattern="*" />
  </packageSource>
</packageSourceMapping>
```

Only **pinned** packages get an explicit pattern; everything else falls to the `*` catch-all, which
preserves today's behaviour for every unpinned dependency.

Two consequences that must be handled, not discovered:

- `packageSourceMapping` is **all-or-nothing per config**: once the element exists, a package
  matching no pattern fails restore with `NU1100`. The `*` catch-all is therefore mandatory, not a
  convenience.
- A mapping key must name a source that exists in the same config. So when mapping is written,
  dotquay also writes an explicit `<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />`
  rather than relying on machine-level inheritance.

Escape hatch for projects with their own mapping strategy: `[nuget] source_mapping = false`, which
suppresses the element entirely and accepts D3's risk knowingly.

### 2.5 Restore credentials

`DotnetInstaller.BuildCredentialEnvironment` returns one variable **per authenticated source**:

```
NuGetPackageSourceCredentials_dotquay-core = Username=kani;Password=<token>
NuGetPackageSourceCredentials_dotquay-tools = Username=kani;Password=<token>
```

Still env-only, still never on disk. Sources that resolve to anonymous contribute no variable.

Known follow-up, not built now: some servers only accept Basic when it is sent preemptively, which
needs `;ValidAuthenticationTypes=basic` appended. GitLab challenges with a 401 and therefore works
without it (verified 2026-08-21, SPEC.md §11.1). Left as a per-source
`[nuget.sources.<name>] auth = "basic"` opt-in if a feed ever needs it.

### 2.6 Lockfile

`[[dotnet]]` gains one optional key:

```toml
[[dotnet]]
name = "Kani.Core"
resolved = "1.2.7"
source = "core"        # omitted when resolved from nuget.org
```

`LockedDotnet.Source { get; init; }` — nullable, omitted when null, so existing lockfiles read
unchanged. This is what makes D2 stick: a package that resolved from `core` on one machine cannot
quietly come from somewhere else on the next `install`.

`LockfileReader.ReadDotnet` (`LockfileReader.cs:125-130`) and the writer's dotnet block
(`LockfileWriter.cs:57-63`) gain the key. `VerifyNothingWasDropped` counts raw `[[dotnet]]` headers
by regex (`LockfileReader.cs:53`) and is unaffected.

### 2.7 The manifest writer — a data-loss trap

`ProjectFileStore.WriteManifest` (`Workflows/ProjectFileStore.cs:67-68`) rewrites `dotquay.toml`
**wholesale** from the model via `ManifestWriter.Serialize`. Anything the model doesn't carry and the
writer doesn't emit is deleted on the next write.

So if `[nuget.sources.*]` and per-package `source =` are only taught to the *reader*, the first
`dotquay add`, `remove`, or `override` on a multi-feed project **silently erases every feed
declaration and every pin from the user's manifest** — a config the user hand-wrote, destroyed by an
unrelated command, with the failure surfacing later as a mystery `NU1101`. This is the single most
dangerous consequence of the change and is not optional to handle.

Required, together:

- `ManifestWriter.Serialize` (`ManifestWriter.cs:67-91`) emits `[nuget]` and every
  `[nuget.sources.<name>]` block, and `AppendDotnet` (`ManifestWriter.cs:128-142`) emits `source =`
  alongside `version =`.
- **Emission order matters.** Writing `[nuget.sources.core]` first implicitly defines `[nuget]`, and
  a later explicit `[nuget]` header is a redefinition Tomlyn rejects. Bare `[nuget]` keys
  (`source_mapping`) must be written *before* any `[nuget.sources.*]` sub-table. Hand-editing users
  can trip the same wire, so the parse error should say so.
- A round-trip test — `Serialize(ReadProject(x)) == x` for a manifest with two sources and a pin — is
  the guard, and it belongs in the suite before any of this ships.

`ManifestWriter.ScaffoldProjectToml(string? nuGetSource)` (`ManifestWriter.cs:18-43`) hardcodes the
`[project] nuget_source` scaffold plus a comment naming the two legacy environment variables
(`:31-32`). **Decision:** `dotquay init --nuget-source <url>` starts scaffolding
`[nuget.sources.default]`, and the comment points at `dotquay login` instead. Reading the legacy key
stays supported forever; writing it stops now, so no project created after v0.7 has to be migrated
twice. `InitCommand.cs:25-30` and `InitService.cs:31` pass through unchanged.

---

## 3. `dotquay login`

### 3.1 The file

`~/.dotquay/credentials.toml`, next to the existing `~/.dotquay/cache` (`CachePaths.Root()`,
`InspectorFactory.cs:19-23`). Never inside a project, never committed, never templated.

```toml
version = 1

[credential."gitlab.example.com"]
username = "kani"
token = "glpat-xxxxxxxxxxxx"

[credential."gitlab.example.com/api/v4/projects/123"]
username = "ci-readonly"
token = "glpat-yyyyyyyyyyyy"
```

- **Key** is `host[:port]` with an optional path prefix. **Longest prefix wins**, so a host-wide
  token is the default and a single feed can be overridden without disturbing it. Host compares
  case-insensitively; path compares ordinally.
- **`0600` on Unix**, via `File.SetUnixFileMode` (AOT-safe, no P/Invoke). On Windows the file
  inherits `%USERPROFILE%` ACLs, which are already user-only; `doctor` verifies rather than assumes.
  `IFileSystem` (`Abstractions/IFileSystem.cs`) has no permission member, so this needs one —
  `SetOwnerOnlyPermissions(string path)`, a no-op on Windows and in `InMemoryFileSystem` beyond
  recording the call. Without it the store would have to bypass the filesystem abstraction, and the
  `0600` test could not run against a fake at all.
- **Plaintext, and said so out loud.** This is Docker's default tradeoff. `ICredentialStore` is an
  interface precisely so a DPAPI/keychain backend can be added later without touching a caller, and
  `login` prints where the token was written so nobody is surprised.
- **HTTPS only.** A token is never sent over plain `http://`, except to `localhost`/`127.0.0.1`.
  Refusing at the store boundary means no call site can get this wrong.

### 3.2 Commands

```
dotquay login [<host|url>] [--username <u>] [--token <t> | --token-stdin] [--no-verify] [--json]
dotquay logout <host>
dotquay login --list
```

- **`login` works outside a project.** It writes user-level state and must not require a
  `dotquay.toml`. `--list`, `logout`, and `login <host>` never read a manifest.
- **Host inference** is the one manifest-dependent path, and it is a convenience only: with no
  argument, if there *is* a manifest in the current `--project` directory and it declares exactly one
  non-default NuGet source, that host is used. No manifest, or more than one source, and the command
  exits asking for a host explicitly — listing the sources when it has them.
- **Token input.** `--token-stdin` is the scriptable path and the one CI should use. Interactive is
  a no-echo prompt (`Console.ReadKey(intercept: true)`). `--token` is accepted and **warned about**,
  because it lands in shell history. With `--json`, prompting is never attempted — a missing token
  is an error, since a GUI caller (§18) cannot answer a prompt.
- **Verification before saving.** A NuGet URL gets a service-index GET; a git URL gets
  `git ls-remote`. A 401 refuses to save and says so, rather than storing a typo that surfaces three
  commands later. `--no-verify` skips it for offline setup.
- **`--list` never prints tokens** — host, username, and `token = <set>`. Not even truncated: a
  truncated `glpat-` prefix plus a host is already useful to an attacker and useless to the user.
- **`login` warns if an env var already shadows the host** (D7), because otherwise a fresh login
  appears to do nothing.

`logout` removes the exact key given; it does not prefix-match, because deleting more credentials
than were named is not a thing a `logout` should ever do.

### 3.3 Precedence — `CredentialResolver`

One class, one method, four steps, in order:

1. `DOTQUAY_NUGET_USERNAME_<SOURCE>` / `DOTQUAY_NUGET_TOKEN_<SOURCE>`, where `<SOURCE>` is the source
   name upper-cased with every non-alphanumeric replaced by `_`.
2. `DOTQUAY_NUGET_USERNAME` / `DOTQUAY_NUGET_TOKEN` — the existing pair, unchanged, applying to every
   non-default source exactly as it does today. **Nothing that works today stops working.**
3. `~/.dotquay/credentials.toml`, longest-prefix match on the source URL.
4. Anonymous.

A half-configured pair (username without token, or the reverse) stays "not configured" and falls
through to the next step, keeping `NuGetCredentials`'s current rule.

`NuGetCredentials` is absorbed into `CredentialResolver`; both of its current consumers
(`NuGetV3ClientFactory`, `DotnetInstaller`) take the resolver instead, so there is still exactly one
place that decides what a credential is.

---

## 4. Git authentication

### 4.1 dotquay as a credential helper

`ProcessGitClient.RunGitAsync` currently passes no environment at all. It gains a computed one:

```
GIT_TERMINAL_PROMPT=0
GIT_CONFIG_COUNT=1
GIT_CONFIG_KEY_0=credential.helper
GIT_CONFIG_VALUE_0=!"C:/path/to/dotquay.exe" credential-helper
```

Why each part:

- **`GIT_TERMINAL_PROMPT=0` is a bug fix independent of the rest.** Today a private repo with no
  configured credential makes git prompt on a stdin that is never redirected and never answered —
  dotquay hangs. With this, git fails immediately with a message dotquay can turn into
  `error.kind = "auth"`.
- **`GIT_CONFIG_*` appends, it does not reset.** `credential.helper` is multi-valued and git tries
  helpers in order; env-supplied config is applied last, so the user's system helper (Git Credential
  Manager, osxkeychain, SSH agent) is tried **first** and dotquay is the fallback. Setting the value
  to empty would clear the list and break every setup that currently works — deliberately not done.
- **The leading `!` and the quoting matter.** Git runs a helper value containing a space through
  `sh`, so an unquoted `C:\Program Files\...` path splits into arguments. `!` marks the value as a
  shell command; the path is quoted and forward-slashed.
- **`Environment.ProcessPath`, not `Assembly.Location`.** Under NativeAOT `Assembly.Location` returns
  an empty string. If `ProcessPath` is null, helper registration is skipped entirely rather than
  emitting a config line that resolves to nothing.

The helper never sees the token on a command line or in a file git reads — git writes the request to
the helper's stdin and reads the answer from its stdout. That is the whole reason D6 chose it over
the two obvious alternatives.

### 4.2 The hidden command

`dotquay credential-helper <get|store|erase>` speaks git's key/value protocol:

```
in:  protocol=https\nhost=gitlab.example.com\npath=kani/addon.git\n\n
out: username=kani\npassword=glpat-xxxx\n\n
```

- `get` looks the host up through the same `CredentialResolver` and prints nothing (exit 0) when
  there is no match, which is how a helper says "I don't know this one" without failing the clone.
- `store` and `erase` exit 0 and do nothing. dotquay's store is written by `login`, not by git
  silently caching whatever a prompt produced.
- `Hidden = true` in System.CommandLine — it is a protocol endpoint, not a user command.
- **It must never emit the JSON envelope.** Its stdout is git's, and a `{"schema":1,...}` document
  there is a parse failure inside git. `--json` is per-command in this CLI (`JsonOutput.CreateOption`;
  only `--project` is `Recursive`, `GlobalOptions.cs:21`), so simply not registering the option is
  sufficient. Note the editor plugin appends `--json --project <dir>` to every invocation
  (`core/dotquay_cli.gd:66-69`) — another reason this command must never be reachable from the
  plugin's button list.

### 4.3 Reporting an auth failure as one

`JsonErrorKind` (`Json/JsonEnvelope.cs:50-67`) gains `Auth = "auth"`. That alone is not enough:
`JsonOutput.Fail`'s exception switch (`JsonOutput.cs:54-66`) maps every `GitCommandException` to
`JsonErrorKind.Git`, so a git *auth* failure would keep reporting as `"git"`. The switch gains a case
for `NuGetAuthenticationException` and for a `GitCommandException` carrying an auth flag — git's
stderr on a rejected credential is recognisable (`Authentication failed`, `403`,
`terminal prompts disabled`), and `ProcessGitClient` classifies it at the point where it still has
the exit code and stderr in hand, rather than leaving a string match to the presentation layer.

The Godot plugin is unaffected: `core/dotquay_result.gd:125` reads `error.kind` generically with a
default, and `ui/dotquay_panel.gd:370-371` branches on only two kinds, so `"auth"` falls through to
the generic error path. Adding a login affordance there is a follow-up, not a break.

`doctor` gains two checks and therefore two `DoctorIssueKind` cases — which is a **JSON contract
change** (the enum is explicitly mapped, `JsonContractTests.cs:119`), not an internal detail:

- credential file permissions (Unix `0600`; on Windows, that the file sits under the user profile),
- per declared source: reachable, and *where* its credential came from (per-source env / global env /
  credential file / anonymous). The token itself is never printed.

A third, cheap and worth it: warn if `dotquay.toml` contains something shaped like a token
(`glpat-`, `ghp_`, `github_pat_`, a `//user:pass@` authority). It catches the exact mistake this
whole design exists to make unnecessary.

---

## 5. Testing

Following the existing conventions (xunit, `Support/` fakes, `Method_Scenario_Expected` names).

**New fakes.** `FakeEnvironmentReader` (D8) and `InMemoryCredentialStore`.

D8 is a real flake, not a theoretical one, and it is worth being precise about why. Two classes
mutate the same two process-wide variables through save/set/restore helpers —
`NuGetV3ClientFactoryTests.WithEnvironment` (`:18-33`) and
`DotnetInstallerTests.WithCredentialsAsync` (`:29-44`). `NuGetV3ClientFactoryTests`' own class
comment (`:9-11`) reasons that xunit runs facts within one class sequentially, which is true and
does not cover the cross-*class* case. Confirmed there is nothing suppressing it: no
`xunit.runner.json`, no `CollectionBehavior`/`DisableTestParallelization` attribute, no
`[Collection]`, no `Directory.Build.props` — xunit 2.9.2's default is one collection per class, run
in parallel. The seam removes the shared state rather than serialising around it.

**Fakes that must be rewritten, not added.** `FakeNuGetClientFactory`
(`tests/Dotquay.Tests/Support/FakeNuGetClientFactory.cs:24`) is keyed entirely on a URL string
(`ClientsBySource`, `RequestedSources`, nulls normalised to `"(default)"`), which is exactly the
model being replaced. It becomes keyed on the source *set* and pin, and the two tests asserting
URL-keyed routing (`DependencyWorkflowTests.cs:499, 525, 529`) move with it. `FakeNuGetClient` also
changes shape for `NuGetVersionQuery` (§2.3).

| Area | Cases |
|---|---|
| `NuGetSourceSetTests` | legacy `[project] nuget_source` desugars to `default`; declared order preserved; duplicate name rejected; **legacy key plus explicit `[nuget.sources.default]` rejected**; invalid name rejected; nuget.org appended once, not twice |
| `ManifestReaderTests` | `source = "core"` string parses as a pin; `[dotnet.Foo.source]` table parses as a package named `Foo.source`; unknown pin name errors naming known sources |
| **`ManifestWriterTests`** | **round-trip: `Serialize(ReadProject(x)) == x` for a manifest with two sources and a pin** — the guard against §2.7's wholesale-rewrite data loss; bare `[nuget]` keys emitted before `[nuget.sources.*]`; `ScaffoldProjectToml` writes the named form |
| `MultiSourceNuGetClientTests` | first-match wins over a higher version on a later source; 404 falls through; **401 aborts and does not fall through**; multi-source hit produces a warning; `NuGetVersionQuery.SourceName` names the winner |
| `NuGetV3ClientTests` | 401 on the **service index** throws `NuGetAuthenticationException`, not `HttpRequestException`; 401 on the package query does too; 404 still means empty (extends the existing `RecordingHandler`) |
| `NuGetConfigWriterTests` | N sources written; stale `dotquay-*` pruned; foreign `<add>` entries untouched; `default` keeps the bare `dotquay` key; mapping emits the `*` catch-all and an explicit `nuget.org` source; `source_mapping = false` emits no mapping |
| `CredentialStoreTests` | longest-prefix beats host-wide; host match is case-insensitive, path is not; `http://` refused, `http://localhost` allowed; round-trip; `0600` asserted on Unix |
| `CredentialResolverTests` | the full D7 chain, each step shadowing the next; half-configured pair falls through |
| `GitCredentialHelperTests` | stdin protocol parsed; output format exact; unknown host → empty stdout, exit 0; `store`/`erase` are no-ops |
| `GitEnvironmentTests` | env built by a pure `GitEnvironment.Build(processPath)` — asserts the append-not-reset shape, `GIT_TERMINAL_PROMPT=0`, quoting of a path with spaces, and that a null `ProcessPath` omits the helper entirely. Extracted specifically so this is testable without spawning git |
| `LockfileTests` | `source` round-trips; absent `source` reads as null; `VerifyNothingWasDropped` still counts `[[dotnet]]` correctly |
| `DoctorTests` | credential-file permission check; per-source "credential came from where"; the token-shaped-string-in-`dotquay.toml` warning; **and that no check ever prints a token** |
| `JsonContractTests` | `LoginJson`/`LogoutJson` payloads; the two new `DoctorIssueKind` wire strings; `JsonOutput.Fail` maps `NuGetAuthenticationException` and an auth-flagged `GitCommandException` to `"auth"`, not `"git"`; `credential-helper` registers no `--json` option |
| **`SecretLeakTests`** | with credentials configured, serialize the manifest, the lockfile, and `NuGet.Config`, and assert the token string appears in **none** of them. The invariant the whole design rests on, asserted rather than reviewed |

### AOT verification (owed before this is called done)

A clean build is not evidence — every prior swap in this project (Spectre.Console.Cli,
NuGet.Protocol) built cleanly and failed in the published binary. Against a real
`dotnet publish -p:PublishAot=true` binary and a real private GitLab instance:

1. `login --token-stdin`, then `login --list` — exercises `File.SetUnixFileMode`, Tomlyn write, and
   the no-echo console path.
2. `add` a package from each of two feeds — exercises `MultiSourceNuGetClient` and per-source restore
   credentials.
3. `install` from a two-feed lockfile with `obj/`/`bin/` deleted — proves `packageSourceMapping`
   didn't break restore.
4. `git clone` of a private repo driven through dotquay — proves `Environment.ProcessPath` resolves
   and real git actually invokes `dotquay credential-helper`.
5. Every new command with `--json` — a missing `JsonTypeInfo` registration fails **only** in the
   published binary.
6. `add` on a two-feed project, then confirm `dotquay.toml` still contains both
   `[nuget.sources.*]` blocks (§2.7). The unit round-trip test covers the writer; this covers the
   whole `WriteManifest` path on a real file.

---

## 6. Migration

Nothing breaks.

| Today | After |
|---|---|
| `[project] nuget_source = "<url>"` | Reads identically; becomes the source named `default` |
| `NuGet.Config` with `<add key="dotquay">` | Untouched — `default` is grandfathered to the bare key |
| `DOTQUAY_NUGET_USERNAME` / `DOTQUAY_NUGET_TOKEN` in CI | Still step 2 of the precedence chain; no pipeline changes |
| Existing `dotquay.lock` | Reads unchanged; `source` is absent and means nuget.org |
| Private git repo via system credential manager | Still tried first — dotquay's helper is appended, not substituted |
| `dotquay init --nuget-source <url>` | Scaffolds `[nuget.sources.default]` instead of `[project] nuget_source` (§2.7). New projects only; existing ones are never rewritten |
| Godot editor plugin | Unaffected — it reads no manifest, hardcodes no command list, and handles unknown `error.kind` and unknown `command` through default arms |

Behavioural changes to an existing project, all of them intended:

- `packageSourceMapping` appears in `NuGet.Config` once a source is declared (D3).
- `GIT_TERMINAL_PROMPT=0` turns a silent hang into an error. Today `ProcessGitClient` reads stderr to
  completion (`ProcessGitClient.cs:213`) without redirecting stdin, so git's credential prompt is
  *captured and never displayed* — the user sees a process that stops, with no message. That is
  strictly worse than the failure that replaces it.
- Manifests are rewritten with `[nuget.sources.*]` blocks preserved rather than dropped — which is
  only a "change" against the version of this design that didn't handle §2.7.
