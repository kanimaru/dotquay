# ADR / Design — v0.16: A junction is a claim, and dotquay must check it before writing

**Status:** **Proposed, not implemented.**
**Date:** 2026-09-10
**Deciders:** Kani

Motivating case, 2026-09-10. `F:\projects\godot\BotV2\addons\vbeami` is a junction into
`F:\projects\godot\vbeami\godot\addons\vbeami` — the vbeami *source repository*, created with
`dotquay link vbeami F:\projects\godot\vbeami\godot\addons\vbeami --force`. At 22:24:39, five files
inside that source folder were rewritten, all sharing one mtime to the second:

```
godot/addons/vbeami/VBeamiRig.cs
godot/addons/vbeami/multiplayer/MultiplayerSessionNode.cs
godot/addons/vbeami/pipeline/RelayConnectionNode.cs
godot/addons/vbeami/plugin.cfg
godot/addons/vbeami/dotquay.toml
```

`git diff --stat 6643754 -- godot/addons/vbeami` in that repo comes back **empty**: the folder had
become, byte for byte, the tree of tag `v0.7.18` — which is exactly what BotV2's lockfile pinned at
that moment (`resolved = "6643754…", version = "v0.7.18"`). A commit made nine minutes earlier
(`0816c20`, *fix(avatar): join the peer's config blob instead of dropping it*) was silently undone in
the working tree, and the author spent the next hour debugging a bug whose fix `git log` said was
already in.

Nothing else on that machine holds the `v0.7.18` published tree. The extracted subtree from the
shared cache does. The write landed inside a junction target.

## Context

### Why this is the worst possible place to be wrong

Every other mistake dotquay can make is confined to `addons/`, a folder it owns and can rebuild from
the lockfile. Writing *through* a junction leaves that blast radius entirely: the bytes land in
somebody's source repository, in a working tree, under a path the consuming project's git never
watches and the producing project's git only notices later, as unexplained "uncommitted changes"
that look like the developer's own edits. Nothing warns, and the natural reaction — "I must have
been mid-refactor" — is wrong in the most expensive direction.

It also breaks the one promise that makes `link` worth having. A link exists so that edits in a
checkout are live in a consumer with no commit and no `update`. If an install can travel the link
backwards, the consumer can rewrite the producer, and "live" becomes bidirectional in a way nobody
asked for.

### What the code already does, and why that is not enough

`AddonInstaller` is not naive about this. On the install path it checks
`_fileSystem.IsDirectoryLink(destinationPath)` and, when the destination is a link, deletes the link
before swapping — the comment says so plainly: *"leaving it would make the dirty check hash their
working copy and then the swap overwrite through it."* `SystemFileSystem.DeleteDirectory` unlinks a
reparse point rather than recursing into it, and `DeleteTree` refuses to descend one.
`CreateDirectoryLink` deletes the destination and junctions; it never seeds content into a target.
`dotquay status` in BotV2 reports `vbeami: linked` today, so `LocalLinks` is reading that project's
v0.6-shaped `[link.vbeami]` section correctly.

So on the source as it stands, no path writes through a junction — and yet one did. The exact route
is **not reconstructed**, and that is the point of this ADR rather than an argument against it. A
protection whose bypass cannot be explained after the fact is not a protection; it is a coincidence
that has held so far. Candidates that remain open — an older binary than the one now on `PATH`
(`dotquay.exe` there is `0.9.0+0c75a94`, built 19:46 the same day), a `--force` route, or an ordering
where the link is re-created after a real folder was installed — all reduce to the same structural
gap: **the decision "is it safe to write here?" is made from `dotquay.local.toml` and a boolean, at
one call site, and the actual write happens somewhere else.**

### A junction is a claim that can go stale

`dotquay.local.toml` says *"vbeami lives at `F:\…\vbeami\godot\addons\vbeami`"*. The junction on disk
says *"`addons/vbeami` is that folder"*. These are two independent statements about the same thing,
maintained by different acts at different times, and nothing checks that they still agree. They stop
agreeing routinely: the checkout gets moved or renamed; the link is created for one project and the
declaration lives in another; `install_as` changes the folder so the declaration now points at a name
nothing installs; the local file is deleted (it is gitignored, so a fresh clone or a cleanup drops it)
while the junction survives. In every one of those states the current code takes the *silent* branch —
install over it, or unlink and install — when the honest answer is that dotquay has been handed two
contradictory statements and should not guess which one the human meant.

## Decision

### 1. The filesystem is consulted before every write, not just at one call site

Add to `IFileSystem`:

```csharp
/// <summary>The absolute path a directory link points at, or null when it is not a link.</summary>
string? ResolveDirectoryLinkTarget(string path);
```

`IsDirectoryLink` answers *whether*; installing safely needs *where*. `SystemFileSystem` implements it
with `Directory.ResolveLinkTarget(path, returnFinalTarget: true)`, normalised through `Path.GetFullPath`;
`InMemoryFileSystem` gains a link table so the fakes can express every case below.

### 2. Three states, and only one of them writes

Before any addon write, `AddonInstaller` classifies the destination:

| destination | `local.toml` | action |
|---|---|---|
| not a link | — | install normally (unchanged) |
| link, target **equals** the declared link path | declares this addon | linked: re-assert the junction, write no content (unchanged) |
| link, target **differs** from the declared path | declares this addon | **error — `LinkTargetMismatch`** |
| link | declares nothing for this addon | **error — `UndeclaredLink`** |

Comparison is by resolved absolute path, case-insensitively on Windows, after normalising separators
and trailing slashes — the same "spelling is not meaning" discipline `GitUrl` applies to remotes
(v0.15). A link pointing at a path that no longer exists is `LinkTargetMissing` and is also an error,
not a silent reinstall.

The removed behaviour is the `wasLinked → DeleteDirectory → install` path for an **undeclared** link.
Deleting a junction is safe for its target, but it silently converts a developer's live link into a
frozen copy, and the developer finds out when their edits stop showing up. That is the same category
of surprise as the write itself, one degree quieter.

### 3. The error names both statements and the two ways out

```
addons/vbeami is a link to
  F:\projects\godot\vbeami\godot\addons\vbeami
but dotquay.local.toml declares this project's 'vbeami' link as
  F:\projects\godot\vbeami-old\godot\addons\vbeami

Installing would write v0.7.18 into a folder dotquay does not own. Nothing was changed.
Update the link to match:   dotquay link vbeami F:\projects\godot\vbeami\godot\addons\vbeami
Or drop it and install:     dotquay unlink vbeami
```

Both paths are printed because either can be the right one and dotquay cannot tell: the junction may
be current and the declaration stale, or the reverse. It states what it found, and stops. The
`UndeclaredLink` variant says the same with *"declares no link for 'vbeami'"* on the second line.

Nothing is written before the classification, so a refusal leaves the project exactly as it was —
the same rule `GuardOldFolderBeforeRename` already follows.

### 4. `--force` does not cover this

`--force` means *"discard local modifications in a folder dotquay owns"* (requirement P0-6). This
folder is not one dotquay owns, and no consent was expressed about it: the flag is typed to get past
a dirty check in the consumer, not to authorise writing into a repository somewhere else on disk. It
stays a hard error under `--force`; the way past it is `dotquay link` or `dotquay unlink`, both of
which say what they do.

### 5. `SwapAndHash` asserts, as the last gate

`SyncEngine.SwapAndHash` throws if its destination is a reparse point, or if any ancestor within the
project's `addons/` is. It is the single method through which addon content reaches disk, so the
invariant belongs there as well as at the classification site — the classification can be bypassed by
a future call path; the assertion cannot. A thrown `SyncException` here is a dotquay bug, and its
message says so.

### 6. `doctor` reports it before an install trips over it

New `DoctorIssueKind.AddonLinkMismatch`, wire string `addon_link_mismatch`: every `addons/<folder>`
that is a link whose target disagrees with `dotquay.local.toml`, in either direction. `doctor` is
where a project's disagreements with itself are already reported, and this one is silent until the
moment it is expensive. Because `DoctorIssueKind` is a wire contract with the editor plugin,
`core/dotquay_wire.gd` gets the new string and the plugin renders it in the same pass — a C# enum
member alone would be dropped on the floor.

## Consequences

- **A link becomes a declaration you have to keep current.** Moving a linked checkout now fails the
  next install instead of quietly reinstalling over it. That is the intended trade: the failure is one
  command to fix and says which command.
- **A gitignored file can now block an install.** Deleting `dotquay.local.toml` while junctions remain
  puts the project in `UndeclaredLink` until either the links or the file are restored. `dotquay unlink`
  removing a junction it has no record of must therefore keep working — it is the escape hatch.
- **The fakes gain a filesystem feature.** `InMemoryFileSystem` has to model link targets, not just
  link-ness. That is a small cost and overdue: the current suite cannot express the bug this ADR is
  about, which is why 900 green tests said nothing on the day it happened.
- **`unchanged`-skipping still needs care.** An addon dotquay decides not to touch never reaches the
  classification. That is acceptable — not writing is always safe — but `doctor` covers the reporting
  gap so a mismatched link is visible without an install being attempted.
- **This does not explain the incident**, and does not pretend to. It makes the incident's outcome
  unreachable from the classification site and, independently, from the write site, which is worth
  more than a root cause for a path that may no longer exist.

## Testing

Unit, against the fakes, one per row of the table plus:

- target missing → `LinkTargetMissing`, nothing written;
- declared and matching, but spelled with a different separator/case/trailing slash → installs as
  linked, no error (the normalisation is the thing under test);
- `--force` on a mismatch → still errors, and the message names both paths;
- `SwapAndHash` called directly with a reparse-point destination → throws;
- `doctor` on a mismatched junction → exactly one `addon_link_mismatch`, naming the addon, the
  junction target and the declared path.

Then, per this repo's standing rule, a **real-binary** run: a scratch project with a genuine `mklink /J`
junction, `dotquay install` against it, and the junction's target inspected afterwards to confirm it
is byte-identical to before. The fakes cannot see a reparse point; that is precisely the gap this ADR
closes, so the verification cannot live only in them.
