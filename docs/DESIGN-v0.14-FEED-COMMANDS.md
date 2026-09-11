# Design — v0.14: declaring feeds and package metadata without a text editor

Status: **implemented and verified against a real NativeAOT binary**, 2026-09-11.
941 C# tests green. See *As built* at the end for where the implementation departed from this design.

Motivating case: adding `Dev.Kani.Godot.Generator` (a Roslyn source generator for Godot, published
to a private GitLab feed at `gitlab.kemomi.de`, project 130) to a Godot project.

The complete sequence today:

```
$EDITOR dotquay.toml          # write [nuget.sources.lazy_o] — no command can do this
dotquay add Dev.Kani.Godot.Generator --version 0.0.5
$EDITOR dotquay.toml          # write source = "lazy_o", private_assets = [...]
dotquay update
```

Two of the four steps are a text editor, and the first one is unavoidable: `add` cannot resolve the
package at all until the feed exists. A package manager whose primary flow opens with "now hand-edit
the manifest" has an interface problem, not a documentation problem.

## The problem

Three distinct gaps land in that one sequence. They look like one complaint and have three different
answers, so this document separates them before deciding anything.

| # | Gap | Status today |
|---|---|---|
| 1 | Declaring a NuGet feed | **No CLI surface at all** |
| 2 | Pinning a package to a feed | No flag; the `source` key is hand-written |
| 3 | Package asset metadata | No flag; deliberately so (v0.10 D8) |

Gap 1 is the defect. Grepping every writer of `[nuget.sources.*]` finds exactly two: `ManifestWriter`
scaffolding one feed during `init --nuget-source`, and `SourceAdopter` (v0.8) writing feeds an
*addon* declared, behind the `trusted_hosts`/approval gate. Neither is user-initiated after init.
The manifest section that a user is most likely to need to change after `init` is the only one with
no command behind it — while `override` (v0.5) and `link` (v0.6) both exist precisely because SPEC
decided hand-editing TOML was the wrong answer for their sections.

## Why a command, and not a prompt

`add` could prompt: *"no feed declares this package — register one?"* §6.3 already rules that out.
The editor plugin drives dotquay through captured stdout and **cannot answer a prompt**; that is why
v0.9 built `--preview` and `--adopt-source` instead of asking. Anything that exists only as an
interactive flow is invisible to the plugin, and feed management is exactly the thing the add form
(§18.2) will want to reach for. It has to be a command with a `--json` surface, or the plugin can
never grow a feed picker.

Gap 3 is the one exception, and §"Gap 3" below explains why a question is right there and wrong
here — with the same rule respected: the question has flags behind it, so the plugin never has to
answer a prompt either.

## Decisions

| # | Decision | Alternatives rejected |
|---|---|---|
| D1 | A `dotquay source add \| list \| remove` command group | `add --nuget-source <url>` — ties a feed's lifetime to one package add, when a feed outlives every package in it and is also read by `outdated`, `list` and restore. Re-running `init --nuget-source` — `init` scaffolds a manifest, it does not amend one, and conflating the two makes a destructive command look idempotent |
| D2 | The user names the feed; dotquay never derives a name from the URL | Deriving `lazy_o_godot` from a URL path. §11.1 puts the feed name in the *consuming project's* namespace, and a derived name is a name nobody chose showing up in `source =` pins forever. Also rejected outright: recognising a forge project URL and resolving the feed from its API — host-specific logic in a tool whose entire feed abstraction is "a V3 service index, just a different host" |
| D3 | `add --source <name>` | Leaving it hand-written per v0.10's D8. D8's reasoning — "three flags taking repeatable tag values would be an awkward CLI surface" — is sound for the asset arrays and does not transfer to a single string that mirrors the `--version` flag already there. `source` was swept along by analogy rather than judged on its own |
| D4 | `source add` verifies the service index before writing; `--no-verify` skips | Writing blind. §11.1 already makes a service index that fails to answer *fatal at resolve time*; failing at declare time is the same rule, applied earlier and more cheaply, and it mirrors `login`'s verify-before-save (v0.7) |
| D5 | `source remove` refuses while any manifest pin or lockfile entry names the feed; `--force` drops the pins and reports each one | Silent removal, which turns every pinned package into a first-match resolution against whatever feed remains — the exact substitution §11.1 exists to prevent |
| D6 | New feeds are **appended**, never prepended | An `--first`/`--before` ordering flag. Search order is declaration order, and first-match ordering is a security property, not a preference: a newly added feed that silently outranks an existing one is dependency confusion arriving through the convenience command. Reordering stays a deliberate hand-edit |
| D7 | For an analyzer package, `add` asks **at most two questions in domain terms** and writes at most one key | Printing a hint that names the TOML keys and lets the user decide. The user this feature exists for wrote the generator, owns the feed, and still could not evaluate the options — a hint that assumes the reader can reason about NuGet asset flow is a hint for someone who does not need it. Also rejected: a `doctor` check, which cannot know intent and would nag about a choice already settled at add time |
| D8 | The detection signal is the package's own `developmentDependency`, read from the `.nuspec` | Sniffing the package's folder layout, which requires downloading the `.nupkg`. The read-side client (`NuGetV3Client`) reads metadata and never downloads packages; teaching it to is a real complexity increase for a heuristic that is strictly weaker than an authored declaration |
| D9 | The only asset value dotquay ever writes is `private_assets = ["all"]` | Writing `include_assets`, `exclude_assets`, or `private_assets = ["none"]`. Measured below: sharing a generator onward is what NuGet already does with no declaration at all, so "share" needs no key. One branch, one key, or nothing |
| D10 | The questions are skipped entirely when the project is not itself published as a package | Always asking. For a Godot game nothing consumes the project, so the question has no consequence and no right answer to get wrong |

## Evidence: measured, not assumed

Both of these were measured because the first draft of this design asserted them from memory and got
them backwards. SDK 10.0.101, 2026-09-10.

### `developmentDependency` is inert for PackageReference

Two packages identical except for `<DevelopmentDependency>true</DevelopmentDependency>`. A consumer
references both and packs:

```xml
<dependency id="Probe.Dev"    version="1.0.0" exclude="Build,Analyzers" />
<dependency id="Probe.Normal" version="1.0.0" exclude="Build,Analyzers" />
```

Identical, and `project.assets.json` gives both the same `compile` and `runtime` entries. The flag
reaches the nuspec via `dotnet pack` and then changes nothing about how a PackageReference consumer
treats the package.

That is exactly what makes it a good *detection* input (D8): an authored declaration of intent, one
extra request against the already-discovered `PackageBaseAddress` resource, with no behaviour of its
own to second-guess.

### A generator flows transitively unless you stop it

Three levels — generator package, a mid library that references it, an app that references the mid
library. The generator emits an **internal** marker type, so the app can only compile against it if
the generator ran *in the app*; a public type would have been visible through the ordinary compile
reference and proved nothing.

| Mid's declaration | Mid's nuspec | Generator runs in App? |
|---|---|---|
| plain `PackageReference` | `exclude="Build,Analyzers"` | **Yes** — `GenMarker.g.cs` emitted in App |
| `<PrivateAssets>all</PrivateAssets>` | dependency absent | No — App fails `CS0122`, nothing generated |

So NuGet's documented `PrivateAssets` default (`contentfiles;analyzers;build`) does **not** keep an
analyzer out of a downstream consumer, despite the nuspec faithfully recording `exclude`. Only an
explicit `PrivateAssets=all` contains it.

Two consequences drive D9 and D10:

- **"Share it onward" is the status quo and needs no declaration.** gdUnit4's documented
  `PrivateAssets=none` is belt-and-braces rather than the thing that makes sharing work.
- **"Keep it to myself" is the only intent that needs writing**, and it needs writing *explicitly* —
  a user who assumes the default protects them is wrong.

One further measured detail, for the hint text and for anyone tempted to copy a template: the six-tag
`include_assets = ["runtime", "build", "native", "contentFiles", "analyzers", "buildTransitive"]` is
the default `all` minus `compile`, and it exists to stop a consumer compiling against a package
shipping **both** `lib/` and `analyzers/`. `Dev.Kani.Godot.Generator` ships only
`analyzers/dotnet/cs/Generators.dll` and no `lib/`, so excluding `compile` is a no-op there.

## Gap 3: one question, not a hint

v0.10's D1 rejected a `kind = "analyzer"` preset because gdUnit4 wants the opposite of the
convention. That argument is about the *manifest format* and it holds: the manifest keeps storing
three verbatim arrays and dotquay invents no vocabulary. What D1 did not settle is where the
*decision* gets made, and the answer is not "print the keys and hope".

The decision, stated without any NuGet vocabulary in it:

> When something else depends on your project, should it get this generator too?

That is answerable by someone who has never heard of `PrivateAssets`. And for most dotquay projects
it does not even arise, because nothing depends on a Godot game. So:

```
Dev.Kani.Godot.Generator 0.0.5 is a source generator - it ships compiler
plugins rather than library code.

Is this project published as a package that other projects depend on?  [y/N] n

Nothing to configure. Added Dev.Kani.Godot.Generator 0.0.5 (NuGet, lazy_o).
```

and, on the branch where it matters:

```
Is this project published as a package that other projects depend on?  [y/N] y
Should projects that depend on yours also get this generator?          [Y/n] n

Added Dev.Kani.Godot.Generator 0.0.5 (NuGet, lazy_o).
  dotquay.toml  private_assets = ["all"]
  .csproj       <PrivateAssets>all</PrivateAssets>
```

Every other path writes nothing, because every other path *is* the default.

The first question is skipped when the project is plainly not packable — no explicit `PackageId`, no
`GeneratePackageOnBuild`, `IsPackable` not set true. The heuristic decides only whether to **ask**,
never what to write, so guessing wrong costs one skipped question and leaves today's behaviour
exactly as it is.

Both answers have flags, so the plugin and CI never meet a prompt (§6.3):

```
dotquay add <id> [--keep-generator-private | --share-generator]
```

`--keep-generator-private` writes `private_assets = ["all"]`; `--share-generator` writes nothing and
suppresses the questions. `add --preview` reports the analyzer detection and whether the project
looks packable, so the add form (§18.2) can render this as a checkbox that appears only when it
applies.

## Command surface

```
dotquay source add <name> <url> [--no-verify]
dotquay source list [--json]
dotquay source remove <name> [--force]
dotquay add <id> [--source <name>] [--keep-generator-private | --share-generator]
```

- `<name>` is validated at parse time as a TOML bare key, for the same reason `install_as` is
  validated as a plain folder name (§7.1): it is written into a file read by third parties.
- `source add` appends (D6), refuses a duplicate name, and refuses a duplicate *URL* under a
  different name — two names for one feed makes the first-match rule meaningless, and
  `SourceAdopter` already treats duplicate URLs as the collision worth naming.
- `source add` verifies by fetching the service index with whatever credential `ICredentialStore`
  resolves for that host (§11.2), and reports the 401-vs-404 distinction §11.1 already draws: with a
  credential sent, a 404 means the URL is wrong; without one, it is ambiguous.
- `source list` shows name, URL, whether a credential resolves, and any `packages` patterns — never
  the credential itself, per §11.2's "tokens are never printed back".

The motivating case becomes two commands, one question, and no editor:

```
dotquay source add lazy_o https://gitlab.kemomi.de/api/v4/projects/130/packages/nuget/index.json
dotquay add Dev.Kani.Godot.Generator --version 0.0.5 --source lazy_o
```

## Manifest, lockfile, and JSON

No format change to either file. Everything these commands write is already specified in §7.1 and
§7.3 — that is the point of the feature, and most of why it is cheap.

`source list` gets a new envelope command (`source-list`); `add --preview` gains two booleans
(`is_generator`, `project_is_packable`) so the plugin can decide whether to show the checkbox. Both
are new members on existing records, so per the v0.9 lesson they need serialisation checked from a
**published** binary, not `dotnet run`.

No new `DoctorIssueKind`, and therefore no `JsonMapper` string and no GDScript wire-contract update:
intent is captured once, at add time, by someone who was asked a question they could answer.

## Test plan

Against the existing fakes in `tests/Dotquay.Tests/Support/`, matching the no-real-I/O convention:

- `source add` appends rather than prepends; round-trips byte-stable through `ManifestWriter`
- duplicate name, and duplicate URL under a different name, both rejected with the offending name
- verification failure aborts **before** writing; `--no-verify` writes with no network call
- `remove` refuses with a manifest pin, refuses with a lockfile-only entry (a transitive package),
  and `--force` reports every pin it dropped
- `add --source` with an undeclared name fails at parse time, naming the declared sources
- generator detection: `developmentDependency` present → the flow engages; absent → silent
- packable detection across `PackageId` / `GeneratePackageOnBuild` / `IsPackable` / none of them
- `--keep-generator-private` writes `private_assets = ["all"]` to both manifest and `.csproj`;
  `--share-generator` writes neither and leaves existing metadata untouched (v0.10 D4)
- the two flags are mutually exclusive, and either one suppresses both questions

## Verification

A green suite is the starting bar here, not the finish line. Before this ships:

- publish a real NativeAOT `win-x64` binary and run `source add`, `list --json`, `remove`, and both
  `add` flag paths against the real GitLab feed (project 130 is the live case behind this document)
- inspect the written `dotquay.toml` **as raw bytes** — v0.7 shipped a BOM glued onto a stored token
  and mojibake em-dashes in generated files, both invisible to the entire unit suite
- confirm `source list --json` and the two new preview booleans serialise from the published binary:
  a payload missing from `DotquayJsonContext` builds clean, passes tests, and fails only in the real
  binary
- end-to-end, confirm the generator actually *runs* in a real Godot project after `add` — "restore
  succeeded" is not evidence a source generator loaded, which is the whole reason lazy-o-godot's own
  CI builds a throwaway consumer and compiles against generated members

## Non-goals

Reordering feeds (D6); per-feed credentials in the manifest (§11.2 — credentials never touch a
project file); a command for addon-declared feeds (that is `--adopt-source`, v0.9); Central Package
Management; and everything v0.10's non-goals already exclude (`Condition`, `GeneratePathProperty`,
`Aliases`, `NoWarn`).

`include_assets` and `exclude_assets` stay hand-written. The case that needs them — a package
shipping both `lib/` and `analyzers/`, where excluding `compile` is real — is exactly the case D8's
nuspec-only detection cannot see, and inventing a third question for it would be guessing.

## Resolved since drafting

**Adopted feeds are left alone** (2026-09-10). `source add` does not offer to rename, absorb or take
ownership of a feed that arrived through v0.8 adoption. The two paths stay separate: adoption is
approval-gated and keyed by URL, `source add` is user-initiated and keyed by a name the user chose,
and a command that quietly reached across into the other would blur a boundary v0.8 drew on purpose.
A user who wants an adopted feed under their own name removes it and adds it back.

**The "I know, leave me alone" acknowledgement is unnecessary** (2026-09-10). It was only needed
because the earlier draft reported a condition repeatedly via `doctor`. With intent captured once at
add time, and with "share it onward" being the no-declaration default (D9), there is nothing to
acknowledge and no state to record. The question dissolved rather than being answered.

## Cost

Small, and mostly wiring that already exists. One new command file in `Commands/` following the
`OverrideCommand`/`LinkCommand` shape; a reader and writer that already handle every field these
commands touch; two flags and two questions on `add`; one nuspec fetch on a resource
`NuGetV3Client` already discovers; a `.csproj` property probe next to the one
`CsprojPackageReferenceWriter` already opens. No new dependency, no new file format, no change to
any existing default, and nothing new for the Godot plugin to parse.

## As built

Shipped 2026-09-11 across three commits: `36a46a7` (the `source` group and `--source`), `76d55d1`
(the generator question), and `dc10ea9` (a pre-existing bug the verification turned up). Four
decisions this design did not settle, and one failure it did not predict.

**D11 — `DotnetSpec` and `LockedDotnet` gained `WithoutSource()` rather than being copied at the call
site.** Both are classes with init-only properties, not records, so `with` does not compile on them.
The copy lives on the type on purpose: `LockedAddon` was a class with hand-written copy helpers that
silently dropped `Folder` when that field was added, and an inline copy in `SourceWorkflow` would have
been the same trap one property later.

**D12 — `INuGetClient` gained `VerifySourceAsync`, not a sentinel package lookup.** "Ask for a package
nobody published and see whether it throws" conflates two different answers: a 404 for an absent
package is a perfectly healthy feed. Only the service index can say whether a URL is a feed at all.

**D13 — `CsprojPackable` returns the property *name*, not a bool.** The probe is a heuristic, and the
prompt names what it read ("This project sets PackageId"). A user asked an unexpected question about
their own project is owed the evidence behind it. Properties are collected before anything is decided,
so precedence does not depend on which line the author typed first, and an explicit
`IsPackable=false` wins outright.

**D14 — the two-question flow is one seam call, not two.** `IGeneratorAssetDecision.KeepPrivate` is
consulted once, only after both gates pass, and the console implementation asks both questions behind
it. Splitting the interface would have put the packable probe — a fact dotquay computes — on the same
footing as a question only the user can answer.

### The bug real-binary verification found

The unit suite was green at 937 tests and `add` still failed against the live GitLab feed:

```
NU1101: Unable to find package Dev.Kani.Godot.Generator ... PackageSourceMapping is
enabled, the following source(s) were not considered: dotquay-lazy_o.
```

The per-package install built `<packageSourceMapping>` from `PinnedPackages()` alone, read from the
manifest *as it is on disk* — and during `add` the package being added is not in the manifest yet,
because the manifest is written only after the install succeeds. So the file held nothing but the `*`
catch-all aimed at nuget.org. The resolved feed was already threaded through for the lockfile record;
it simply never reached the mapping. §11.1 had asked for this all along ("`dotnet restore` is bound to
the same feed via `<packageSourceMapping>`"); it was true of the final restore and not of the install.

Nothing in the suite could have caught it: `FakeDotnetCliRunner` does not read `NuGet.Config`, so no
test was looking at the file. `AddSourceMappingTests` now reads it. This is the same shape of finding
as v0.7's BOM-on-a-token and v0.10's JSON payload — the third time this project's "a green suite is
not evidence the CLI works" rule has paid for itself, and worth one more entry in the tally.

### Verified from the published `win-x64` AOT binary, against real feeds

- `source add` verified a live private GitLab feed (project 130) and wrote the manifest; `source list`
  and `source list --json` both correct, the new `SourceListJson` payload serialising cleanly — the
  AOT-critical check, since a payload missing from `DotquayJsonContext` builds and tests fine and
  fails only in the real binary.
- `add --version 0.0.5 --source lazy_o` restored from the private feed after the mapping fix.
- `add PolySharp --version 1.15.0 --keep-generator-private` into a packable project detected
  `developmentDependency` from the real nuspec on nuget.org and wrote `private_assets = ["all"]` to
  both `dotquay.toml` and the `.csproj`.
- The same command into a non-packable project wrote nothing, which is the quiet path that matters
  most.
- `source remove` refused while pinned and named the package; `--force` cleared the manifest pin and
  the lockfile record and reported both.
- Both generator flags together, and `--source` against a git URL, refused with the intended messages.
- Every written `dotquay.toml` inspected as raw bytes: no BOM, no non-ASCII.

### Deferred, with the reason it changed

**A NuGet `add --preview` is now worth having, and is not built.** v0.9 refused one on the grounds that
"a NuGet add pulls in no addons and declares no feeds, so a preview of one would be a dialog with
nothing in it" — and this feature is exactly what makes that false: there is now something to report,
namely whether the package is a generator and whether this project looks published. Without it the
editor plugin cannot know when to offer the choice, so the generator question is CLI-only for now.
The two booleans this design proposed for the preview payload (`is_generator`,
`project_is_packable`) were therefore not added; they belong with that preview when it is built.

**No `doctor` check, as designed.** Intent is captured once, at add time, by someone answering a
question they can answer. That also means no new `DoctorIssueKind`, no `JsonMapper` string, and nothing
new for `core/dotquay_wire.gd` to learn — the whole feature touches the Godot plugin not at all.

### One process note

v0.15 and a v0.16 design landed from another session between this document being written and being
implemented, so v0.14 ships after v0.15 did. The numbers identify design documents, not releases, and
nothing about the ordering mattered — but the shared file (`DependencyWorkflow.cs`) was checked against
both sets of changes rather than assumed, and both survived.
