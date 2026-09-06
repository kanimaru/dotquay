# ADR / Design — v0.12: Why an addon is modified

**Status:** **Implemented and verified against a real NativeAOT binary**, 2026-09-06. 756 C# tests and
435 GDScript assertions green. CLI and Godot plugin both shipped — see *In the Godot plugin* and
*As built*.
**Date:** 2026-09-06
**Deciders:** Kani

Motivating case: `dotquay status` reports an addon as `modified` and the user has not knowingly
touched it. The only decision available is "run `dotquay update --force` and hope nothing of mine is
lost" or "leave it modified forever", and today dotquay gives no information on which to base that
choice. This doc follows that conversation.

## Context

`AddonStatusEvaluator.Evaluate` classifies an installed addon by comparing one SHA-256 digest of the
installed tree (`ContentHasher.HashDirectory`) against the lockfile's `content_hash`
(`DirtyChecker.Check`). That comparison is deliberately opaque: a digest is a single number, and the
question "which files differ, and do they matter?" is not answerable from it *even in principle*.

Everything downstream inherits the opacity:

- `AddonStatusInfo` is `record AddonStatusInfo(string Name, AddonStatusKind Kind)` — a name and a
  five-valued enum, nothing else. `DirtyChecker.Result` does carry `ActualContentHash`, but
  `AddonStatusEvaluator` reads only `.IsDirty` and drops the rest.
- `StatusCommand.Print` emits a fixed string, `"{name}  modified (hand-edited, not captured as a
  patch)"`, whose parenthetical is an *assumption* about the cause, not a finding.
- `StatusEntryJson(string Name, string Status)` gives the Godot plugin exactly what the CLI text has.
- `--force` on `update` is a blunt bypass. `GuardOldFolderBeforeRename` and `InstallAsLink` re-run
  `DirtyChecker` and throw unless forced; forcing skips the check and overwrites. Nothing anywhere
  shows what would be destroyed.

So the guard that exists to protect hand-edits (requirement P0-6) presents, to the user, as an
unexplained refusal with one documented escape hatch. `GodotArtifacts`' own doc comment already
identifies why that is dangerous:

> A guard that fires on every project teaches everyone to bypass it, and then it is not protecting the
> hand-edits it exists for.

That comment was written about the `.uid`/`.import` churn, which was fixed by excluding those paths
from the hash. But the same dynamic returns in weaker form for every *other* cause of an unexplained
`modified`, and the escape hatch is the same one.

### What actually causes a surprising `modified`

Worth stating precisely, because it narrows the design. Editor artifacts are already excluded from
the hash, and `DirtyChecker` additionally accepts the pre-v0.9 include-everything digest — so
`.uid`/`.import`/`.godot/` churn is, by construction, *not* the cause of a present-day surprise.
What remains:

1. **Folder-casing drift.** Installed at `addons/Gut` while the addon's own references say
   `addons/gut`; Godot rewrites `.tres`/`.tscn`/`.import` on every open to repair the reference.
   Real content changes to real files. `doctor`'s `addon_folder_case_mismatch` already detects the
   *cause* — but only if the user thinks to run `doctor`, and it says nothing about the tree.
2. **UID repair.** A `.tscn`/`.tres` `ext_resource` line carries both `uid=` and `path=`; when the
   uid is stale Godot rewrites it from the path on next save. Self-healing and harmless, but it is a
   byte change to a tracked file, so it is a real hash mismatch.
3. **Line-ending normalization** on `.gd`/`.tscn` files, from git config or an editor.
4. **A genuine hand-edit** the user forgot, or made months ago.

Cases 2 and 3 are safe to force. Case 1 is safe to force *and* has a better fix (`install_as`). Case
4 is the one `--force` must not be used blindly on. They are currently indistinguishable.

### The technique already exists — as prose

SPEC.md §257, `AddonFolderCaseChecker.cs:16-18`, and `DependencyWorkflow.cs:1109` all describe the
same manual diagnostic: *diff the installed tree against a fresh clone of its own locked commit, and
split the differences into artifact vs. non-artifact*. It is documented because it works. It is
manual because nobody automated it.

`DependencyWorkflow.PatchAsync` already performs its mechanical half — `RepoCache.GetOrFetchAsync`
→ `SubtreeExtractor.Extract` into a scratch staging folder → diff → `finally` delete. This design
reuses that shape and replaces "diff into a patch file" with "diff into a classified change list".

## Decision

Add an **opt-in explanation layer** over the existing status check: `dotquay status --explain`.

Three commitments frame everything below.

**1. The hash stays the authority.** `DirtyChecker`, `ContentHasher`, and `AddonStatusEvaluator` are
not modified. Nothing in this feature can change whether an addon is `Modified`, whether
`install`/`update` refuses, or what `--force` does. The explanation is a *report about* a verdict
that was already reached, computed on a separate path. A bug in the classifier can therefore mislead,
but it cannot destroy anything or silently weaken P0-6.

**2. It is opt-in.** Reconstructing the expected tree may hit the network. Plain `dotquay status`
stays local, offline, and fast — it gains one extra sentence pointing at `--explain`, nothing more.

**3. It is best-effort and says so.** Offline with a cold cache, a deleted upstream commit, or a
patch that will not reconstruct all mean "no explanation available", reported as such with a reason.
An honest `unavailable` is the correct output; a guess is not.

### Command surface

```
dotquay status --explain [--json]
```

Explains every addon whose status is `Modified`, and only those. In a healthy project that is zero
addons and the flag costs nothing; the work is bounded by the number of things actually wrong.

No new top-level command, and no positional addon argument. `status` already answers "what state is
everything in"; `--explain` deepens the same answer rather than introducing a second, separately
discoverable place to ask. A `dotquay explain <name>` would be a command whose entire purpose is to
elaborate on another command's output, which is where the flag belongs.

### The expected tree

Reconstructed exactly the way `AddonInstaller` would produce it, so "expected" means the same thing
to `--explain` as it does to the installer:

1. `RepoCache.GetOrFetchAsync(locked.Git, locked.Resolved)` — shared, content-addressed, and usually
   already warm from the install that put the addon there.
2. `SubtreeExtractor.Extract(cachedRepo, locked.Path, scratch)` — `locked.Path` is the **source
   subtree in the upstream repo**, never the install destination.
3. If `locked.PatchHash`/`PatchBaseSha` are set and the patch file exists, `PatchReapplier.ReapplyAsync`
   on the scratch tree — because `PatchAsync` records the *patched* tree as `content_hash`, so a
   pristine baseline would report the user's own captured patch as unexplained modification.

Scratch lives at `.dotquay/staging/<name>-explain-base`, deleted in a `finally`, following
`PatchAsync`'s precedent of a diff-only staging folder distinct from the installer's stage-and-swap
target. The installed side is located through `InstallPaths.For` — never a hand-built
`addons/<name>`.

If step 1 or 2 throws, or step 3 conflicts, the addon's explanation is `unavailable` with the reason
attached, and the other addons are still explained.

### Classification

Each differing path gets a **kind** (`Added` / `Removed` / `Changed`, from the installed tree's point
of view) and a **class**:

| Class | Meaning | Benign? |
|---|---|---|
| `EditorArtifact` | `GodotArtifacts.IsArtifact(path)` | yes |
| `LineEndingsOnly` | byte-differs; identical after CRLF/LF normalization | yes |
| `UidOnly` | identical after line-ending normalization **and** masking `uid://…` values | yes |
| `Content` | everything else, and every file that is not decodable text | **no** |

`EditorArtifact` cannot by itself cause `Modified` — those paths are excluded from the hash — but it
is retained because the explanation walks the *whole* tree, and reporting "9 artifact files also
differ, which is why they are excluded" is useful context rather than noise to be suppressed.

`UidOnly` is detected by masking rather than by diffing: normalize line endings, then replace every
`uid://<token>` occurrence with a fixed placeholder in both sides and compare the results. This needs
no diff algorithm, cannot mis-attribute a hunk, and directly encodes the fact that makes uid churn
safe — Godot rewrites the uid, and the `path=` fallback beside it is what actually resolves.

Masking is deliberately narrow. `uid://abc` is a string prefix of `uid://abc1`, so the mask matches a
complete token (`uid://` plus the following run of base-identifier characters) and never a substring.
The same hazard sank the rejected project-wide UID rewrite; the difference is that here a
mis-classification produces a wrong *label*, not a corrupted file.

A file that is not valid UTF-8, or contains NUL bytes, skips normalization entirely and is `Content`.
Binary assets have no benign-rewrite story, and guessing at one over arbitrary bytes is how a real
change gets labelled harmless.

### Verdict

Derived, not stored: `SafeToForce` when every change is benign, `ReviewNeeded` otherwise,
`Unavailable` when the baseline could not be reconstructed.

`Added` + `Content` counts as **not** safe — an added file is exactly the thing a force-update
deletes without trace. The verdict answers one question and it is not "is this addon healthy": it is
*"would `--force` destroy anything you would miss?"*.

The verdict is advice. It does not gate, pre-authorize, or auto-apply `--force`; the user still types
it. Deciding for the user is how a guard becomes a formality — the same failure mode this feature
exists to reverse.

## Shape of the code

New, in `src/Dotquay.Core/Sync/`:

- `TreeComparer` — `(IFileSystem, expectedPath, actualPath) → IReadOnlyList<TreeChange>`. Pure, no
  network, no git.
- `ChangeClassifier` — `(relativePath, expectedBytes, actualBytes) → ChangeClass`. Pure, static,
  total.
- `TreeChange` / `ChangeKind` / `ChangeClass`.

New, in `src/Dotquay.Core/Workflows/`:

- `DependencyWorkflow.Explain.cs` — the partial split, following `DependencyWorkflow.Preview.cs`.
  It owns the fetch → extract → re-patch → compare orchestration. `_repoCache` and `_patchTool` are
  already fields; no constructor changes.
- `AddonExplanation` / `ExplainVerdict`.

Changed:

- `AddonStatusInfo` gains `AddonExplanation? Explanation = null` — additive, so every existing
  construction site compiles untouched.
- `DependencyWorkflow` gains `Task<StatusResult> StatusAsync(projectDirectory, bool explain, CancellationToken)`.
  The existing synchronous `Status(projectDirectory)` stays exactly as it is and remains what
  `graph`, `doctor`, and plain `status` call — this feature adds a path, it does not reroute one.
- `StatusEntryJson` gains an optional `Explanation` object.
- `StatusCommand` gains `--explain` and the detail printer.

### JSON contract

Purely additive: a new optional object on an existing entry. No existing field changes type or
meaning, and no `AddonStatusKind` or `DoctorIssueKind` member is added — the two things that would
make this a breaking wire change for `core/dotquay_wire.gd`.

```jsonc
{ "name": "gut", "status": "modified",
  "explanation": {
    "verdict": "safe_to_force",
    "counts": { "content": 0, "uid_only": 3, "line_endings_only": 0, "editor_artifact": 9 },
    "changes": [ { "path": "gut.tscn", "kind": "changed", "class": "uid_only" } ]
  } }
```

The editor plugin was out of scope for the first pass and unaffected by it — it reads `name` and
`status`, and an unknown field is ignored by its parser. It has since been wired up; see *In the
Godot plugin* below.

`changes` is capped (100 entries) with the counts remaining exact, so a catastrophically drifted
addon cannot produce an unbounded JSON document or an unreadable terminal wall.

## In the Godot plugin

Shipped 2026-09-06 alongside the CLI. 435 GDScript assertions green, including the live CLI contract
run against the real NativeAOT binary.

The plugin never ran `status` at all — its `modified` badge comes from `graph`'s per-node `status`
field. So the explanation is **not** folded into `graph`: that payload is re-read on every refresh and
after every mutating command, and putting a per-addon fetch behind it would make the panel's cheapest,
most frequent call its slowest. Instead it follows the same shape as `add --preview`:

- **A node menu item, "Why is this modified?"**, offered only on a modified addon and listed *above*
  "Capture edits as a patch". The honest order is find out what changed, then decide what to do about
  it — a user who already knows loses one menu position, a user who doesn't is handed the answer
  before the option that acts on it.
- **It runs `status --explain --json`** as one CLI invocation, matched back to the addon the user
  asked about by the run's ticket (`status` reports every addon, so the reply alone doesn't say).
- **`ui/explain_dialog.tscn`** lists the changes, colours real content red and benign churn grey, and
  puts the verdict in a headline sentence above the list.

The dialog's OK button is **"Force update"**, and it is disabled unless the CLI said `safe_to_force`.
This is the one place the design's "the verdict is advice, it never auto-forces" line gets tested:
offering the button *after* the user has read what it discards is not the same as acting on the
verdict, and disabling it next to a list of real edits is the UI form of the CLI's own refusal. An
unavailable comparison is amber and also disabled — "I could not check" and "I checked and it is
fine" must never look alike here.

Two forward-compatibility rules, both asserted:

- `is_benign()` is an **allowlist** of the three benign classes, never `!= content`. A newer CLI can
  add a class this build has never heard of, and "not content, therefore harmless" would hand it an
  enabled Force update button on the strength of not recognising it. The first implementation got this
  wrong and the test caught it.
- `is_safe_to_force()` requires the verdict to be *exactly* `safe_to_force`, so an unrecognised
  verdict reads as unsafe.

One deviation from house style, deliberate: `explain_dialog.gd` and `explain_change_row.gd` bind their
nodes lazily on first use rather than with `@onready`. A scene-unique name resolves through the owner
the moment the scene is instantiated, while `@onready` waits for the tree — and a node added during
`SceneTree._initialize` never reaches `_ready`, so an `@onready` dialog cannot be asserted headlessly.
Binding lazily is what makes the Force-button's disabled state a test rather than a code review, on
the one control in this plugin that can destroy work.

## Alternatives considered

**Record per-file hashes in the lockfile.** Makes `--explain` instant and fully offline. Rejected:
`dotquay.lock` is committed and code-reviewed, and an addon of a few hundred files would add a few
hundred lines of noise per entry, permanently, to serve a diagnostic that runs rarely. It also would
not help *existing* projects until every addon was reinstalled — precisely the projects that have
already drifted and need explaining today.

**A local sidecar manifest** (`.dotquay/state/<name>.files`) written at install time. Same speed, no
lockfile noise. Rejected as the primary mechanism for the same migration reason, plus it introduces a
second thing that can be stale, deleted, or disagree with the lockfile — a new class of "dotquay says
X, disk says Y" bug in a feature whose entire purpose is answering that question truthfully. The
cache-based baseline is derived from the lockfile's own SHA and cannot drift from it. Worth
revisiting only if `--explain`'s latency turns out to matter, which for a hand-run diagnostic on a
warm cache it should not.

**Always explain, no flag.** Rejected: it puts a potential network fetch behind a command the editor
plugin polls and CI runs, to answer a question that is only asked when something is already wrong.

**Let the verdict drive `--force` automatically** (e.g. `update --force-if-safe`). Rejected for now.
It is a small step from there to a flag people put in scripts, at which point the P0-6 guard is
decorative again. The verdict earns that trust by being right in practice first; nothing here
prevents adding it later.

**Extend `doctor` instead.** `doctor` reports project-level health and already owns the *cause* check
for folder casing. But "explain this one modified addon's tree" is a drill-down on a `status` verdict,
and `doctor` has no notion of per-addon detail output. The two stay complementary: `doctor` says
*your casing is wrong*, `--explain` says *and here are the 3 files it rewrote*.

## Consequences

- The first honest answer to "should I force this?" — the question this project has been silently
  asking users to guess at.
- `status --explain` may hit the network. Documented, opt-in, warm-cache in the common case.
- One new classification surface to keep correct. It is advisory-only and cannot corrupt or delete,
  which is why it is acceptable for it to be heuristic at all.
- `ChangeClass` is a wire enum from the day it ships. Adding a member later is a contract change for
  any consumer that switches on it — the same rule `DoctorIssueKind` lives under.
- The folder-casing case now has two detectors that must agree in spirit: `doctor`'s cause-level
  check and `--explain`'s effect-level one. They are independent by design, so a disagreement is
  informative rather than contradictory.

## As built

One departure, found by running the binary rather than the tests.

The design carried a single `baseline_includes_patch` flag, and the first implementation printed a
caveat whenever it was false. But false is also what an ordinary *unpatched* addon looks like — which
is most of them — so the caveat fired on nearly every explanation. A note that appears every time is
one the reader learns to skip, which is the same failure `GodotArtifacts` documents about a guard that
fires on every project.

Split into two: `baseline_includes_patch` (the patch was re-applied) and `patch_could_not_be_applied`
(a patch was *declared* and could not be reconstructed — the file is gone, or the re-apply
conflicted). Only the second is a caveat, and only it is printed. Both are on the wire.

Two smaller notes:

- `JsonMapper` already nests a static `ChangeKind` holding wire constants for `ChangeJson`, unrelated
  to this feature's `Sync.ChangeKind`. The mapper's overloads are fully qualified for that reason.
- `WorkflowFactory` gained `CreateQuiet()`. `status` needs a progress spinner only under `--explain`,
  and the existing `CreateWithProgress(json)` argument also selects NuGet source-approval behaviour —
  borrowing it to mean "quiet" is how the second meaning eventually gets used by accident.

## Verification — done

Per the project's standing discipline, no step below was sufficient alone.

1. **Unit tests** against `InMemoryFileSystem` and the existing fakes (`ChangeClassifierTests`,
   `ExplainModifiedTests`): uid-only rewrite, line-ending-only rewrite, added content file, removed
   content file, artifact drift, invalid UTF-8, NUL bytes, `uid://abc` vs `uid://abc1` non-collision,
   one real edit among benign churn, declared-patch-file-missing, `unavailable` on a fetch failure,
   staging cleanup, and the wire strings. 756 green.
2. **`dotnet build && dotnet test`** after deleting `obj/`/`bin/` recursively, given this repo's
   history of tests running against a stale `Dotquay.Core.dll`.
3. **A real NativeAOT binary** (`-p:PublishAot=true`, 12.3 MB) run against a real project with a real
   local git upstream, through every path this change added: uid-only churn (`safe_to_force`), a mixed
   real-edit case (`review_needed`), the plain-`status` hint, `--explain --json` with the document
   inspected field by field, and a cold cache against a vanished upstream (`unavailable`, with git's
   own reason). The JSON source generator picked up the new nested payload types without a
   `DotquayJsonContext` entry, since they are reachable from `JsonEnvelope<StatusJson>` — confirmed by
   the AOT run, not assumed from the JIT one.
4. **The GDScript suite** (`src/Dotquay.Godot/run_tests.ps1`): fixture tests for the parser, a
   headless render test for the dialog's Force-button gating, and — with `DOTQUAY_TEST_CLI` pointed at
   the published binary — a live contract test that runs `status --explain --json` for real and parses
   it. 435 assertions, 0 failures. The fixture suite alone would not have noticed the CLI rejecting
   the flag or renaming the envelope command.
