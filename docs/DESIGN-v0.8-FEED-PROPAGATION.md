# Design — v0.8: addon-declared NuGet feeds

Status: **implemented and verified against a real NativeAOT binary**, 2026-08-28. 577 tests green.

Build recipe and its three environment gotchas: `DESIGN-v0.7-AUTH.md`.

### Verified end to end (native binary, real git repo, real GitLab host)

A local git addon declaring its own feed, consumed by a project that declares no feed at all:

- **Trusted host → adopted silently**, and written into the committed manifest with the attribution
  comment, `declared_by`, and the owned-ID patterns intact.
- **Untrusted host, non-interactive → refused**, manifest untouched, and the failure explains itself:
  *"'VBeami.Core' has no stable release on NuGet — … Addon 'vbeami' declares the NuGet feed …, which
  was not adopted. Add its host to [nuget] trusted_hosts, or declare the feed yourself."*
- **`install` never adopts**, even from a trusted host.
- **Mapping scoping** writes the owned `VBeami.*` pattern plus the mandatory `*` catch-all.

### Three bugs found by running it, not by testing it

1. **Adoption was persisted only after dotnet resolution.** A resolution failure discarded a decision
   the user had already made, so a retry re-adopted — and, on an untrusted host, re-prompted every
   single time. Now written the moment it is decided, in both `add` and `update`.
2. **`InvalidOperationException` escaped unhandled** when a feed answered 200 without advertising a
   `PackageBaseAddress/3.0.0` resource — a stack trace on stderr and, under `--json`, no envelope at
   all. Same class as the `HttpRequestException` bug in v0.7, in code v0.7 didn't touch. Now a typed
   `NuGetSourceException` naming both GitLab feed URL shapes.
3. **A `JsonException` had the same hole** — a sign-in page answering 200 with HTML is the realistic
   way to hit it. Also typed now.

The lesson generalises: every path out of `NuGetV3Client` needs to be a typed exception the CLI maps,
because anything else becomes an unhandled crash that silently breaks the `--json` contract the editor
plugin depends on.

## The problem

An addon declares a `[dotnet]` dependency; the package lives on a private feed; **only the consuming
project may declare feeds** (v0.7, SPEC.md §11.1). So adding one addon means tracing its NuGet
dependencies to their feeds and declaring each one by hand — and again for every addon it pulls in
transitively. That is exactly the work a package manager exists to absorb.

The reason v0.7 refused to let an addon inject a feed is not squeamishness. A third-party addon that
can add a source can shadow a public package name for the whole project — textbook dependency
confusion. The goal here is to remove the manual work **without** handing that capability to any
addon that asks.

## Before building this: check GitLab group feeds

For an estate on a single GitLab (the motivating case), this feature may be unnecessary. GitLab 17.4+
serves a **group-level** NuGet feed at
`https://<host>/api/v4/groups/<group_id>/-/packages/nuget/index.json`, aggregating every project's
packages in that group. One declared feed then covers every addon in the group, forever, with no new
dotquay machinery at all.

Limits: group endpoints don't permit anonymous downloads (irrelevant with a PAT) and publishing still
targets the project endpoint. This does **not** help for addons hosted somewhere else, which is what
the rest of this document is for.

## Decisions

| # | Decision | Alternatives rejected |
|---|---|---|
| D1 | An addon's package manifest may declare `[nuget.sources.*]`, as a **suggestion** with no authority of its own | Silent adoption (hands every addon the confusion attack); no declaration at all (leaves the user hunting for URLs) |
| D2 | `[nuget] trusted_hosts` auto-adopts feeds on hosts the project already trusts; every other host needs approval | Approval for everything (a prompt per feed on a single-host estate is friction with no security gain — the host is already trusted for the addon's *code*, which is strictly more dangerous than its feed) |
| D3 | Approval is an `ISourceApproval` seam, not a console call inside `Dotquay.Core` | Prompting from the workflow (unreachable from the editor plugin and untestable); returning "pending" and re-running (two-phase, and the second run has to re-walk the graph) |
| D4 | Adopted feeds are written into the project's **committed `dotquay.toml`**, attributed to the addon that asked | A separate lockfile section — feed URLs would then live in two files, and the one people review in a PR wouldn't be the one that matters |
| D5 | `install` **never** adopts, prompts, or consults `trusted_hosts` | Letting install adopt would mean CI silently gaining feeds; SPEC.md §5 already says install reads the lockfile and never resolves |
| D6 | Adopted feeds are bound by `<packageSourceMapping>` to the IDs the addon owns | Joining the normal search order — ordering is a weaker guarantee than mapping, and dotquay already writes mapping |
| D7 | An addon declares which IDs it owns via `packages = ["VBeami.*"]`; absent, the exact IDs it declared | Exact IDs only — a private package's own transitive private dependencies would fall to the `*` catch-all and be looked for on nuget.org |

## Schema

**Addon package manifest** (`addons/vbeami/dotquay.toml`) — new section, ignored by pre-v0.8 dotquay:

```toml
[package]
name = "vbeami"

[nuget.sources.vbeami]
url = "https://gitlab.kemomi.de/api/v4/groups/7/-/packages/nuget/index.json"
packages = ["VBeami.*"]        # IDs this feed owns; default: the IDs declared below

[dotnet.VBeami.Core]
version = "1.0.0"
```

**Consuming project** (`botv2/dotquay.toml`):

```toml
[nuget]
trusted_hosts = ["gitlab.kemomi.de"]

[addons.vbeami]
git = "https://gitlab.kemomi.de/wild-alliance/vbeami.git"
version = "~1"

# Adopted from addon 'vbeami' by dotquay. Delete this block to stop trusting it.
[nuget.sources.vbeami]
url = "https://gitlab.kemomi.de/api/v4/groups/7/-/packages/nuget/index.json"
packages = ["VBeami.*"]
```

A `source = "..."` pin inside an addon's package manifest stays ignored (SPEC.md §11.1) — the name
belongs to the consumer's namespace. The addon expresses ownership through `packages` patterns on its
own declared source instead, which is namespaced by URL rather than by a name it can't control.

## Trust evaluation

Host comparison reuses `CredentialScope.Parse(url).Host` — deliberately the same normaliser the
credential store uses (lowercased host, port only when non-default), because two normalisers that
disagree about what "the same host" means is precisely the bug that turns a security boundary into a
suggestion. Exact host match; no subdomain wildcards, because `*.example.com` matching an attacker's
`evil.example.com` is a trap that reads as safe.

Adoption order for each addon-declared source not already in the project manifest:

1. Already declared by the project (same URL) → nothing to do.
2. Host in `trusted_hosts` → adopt.
3. Otherwise → `ISourceApproval.Approve(...)`. The CLI implements it as a console prompt naming the
   addon, the URL, and the package IDs at stake. `--json` and any non-TTY run use
   `DenyAllSourceApproval`.
4. Denied → the operation fails naming the addon, the feed, and the two ways forward (add the host to
   `trusted_hosts`, or declare the source by hand).

## Scoping

`<packageSourceMapping>` currently receives only the project's explicit `[dotnet.*] source =` pins.
It gains two more inputs, both of which improve the pre-existing behaviour independently of adoption:

- **Every package whose resolved feed is known**, from `dotquay.lock`'s `source` field — so restore is
  bound to the feed resolution actually chose, not merely to the ones a human pinned by hand.
- **Owned-ID patterns** from `packages = [...]`, so a private package's private transitive
  dependencies resolve from the same feed instead of falling to the `*` catch-all and being looked
  for on nuget.org.

Guard: an adopted feed may not claim a package ID the project itself already declares, nor one
already resolved from a different source in the lockfile. That is the case where an addon tries to
take over `Newtonsoft.Json`, and it is a hard error naming both claimants rather than a warning.

## Work

| Area | Change |
|---|---|
| `PackageManifest` | `NuGetSources`; `ManifestReader.ReadPackage` reads `[nuget.sources.*]` (no legacy key, no `source_mapping`) |
| `NuGetSourceSpec` | `Packages` (owned ID patterns) |
| `ProjectManifest` | `TrustedHosts`; reader + writer, round-trip test |
| `DependencyGraphResolver` | walk collects `AddonDeclaredSources` alongside `AddonDeclaredDotnet` |
| `Dotquay.Core.Auth`/`Dotnet` | `ISourceApproval`, `DenyAllSourceApproval`, `SourceAdoptionRequest`, host-trust evaluation |
| `DependencyWorkflow` | adoption during `add`/`update`; never during `install`; adopted sources written to the manifest |
| `NuGetConfigWriter` | mapping from lockfile `source` + owned patterns; the same-ID guard |
| `Dotquay.Cli` | `ConsoleSourceApproval`; `--json`/non-TTY denies |
| Tests | trust matching, adoption, denial, install-never-adopts, mapping scoping, the take-over guard, manifest round-trip |
| Docs | SPEC.md §7.1/§7.2/§11.1 |

## Risks

- **The manifest gains machine-written blocks.** `ProjectFileStore.WriteManifest` already rewrites the
  file wholesale (see `DESIGN-v0.7-AUTH.md` §2.7), so this is not new, but the attribution comment
  must survive a rewrite — meaning the comment has to be generated from the model, not preserved as
  text.
- **`trusted_hosts` is a real security control** and will be copied between projects by hand.
  **Addressed:** `doctor` now reports it, in two deliberately separate channels. An entry that
  *cannot do what it reads as* — a wildcard (matches nothing), a typo, a path (trusts the whole host
  anyway), plain HTTP — is a `trusted_host_unusable` **issue**. The mere presence of trust is
  **not** an issue but an informational summary (`data.trust` in JSON, printed in human output
  whether or not anything is wrong), listing the trusted hosts and every adopted feed with its
  declaring addon. The split matters: making trust itself an issue would mark every project using the
  feature permanently unhealthy and fail CI, which is precisely how a checker trains people to ignore
  it.
- **Mapping every known package to its feed is stricter than today.** A package whose feed later stops
  serving it fails restore instead of silently coming from elsewhere. That is the intent, and it is a
  behaviour change worth calling out in the release note.
