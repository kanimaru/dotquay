# DESIGN v0.23 — the recorded hash ignores line endings

**Status: shipped on branch `feature/lfs-safeguard`** (second commit). Fold into SPEC.md §7.3 on merge.

## Problem

`content_hash` in `dotquay.lock` was a hash of the addon's files **as git wrote them to disk**. Git rewrites line
endings per machine (`core.autocrlf`, `core.eol`, `.gitattributes`): the same commit is CRLF on a Windows box with
`autocrlf=true` and LF on Linux or macOS. So one addon at one commit had a different `content_hash` for each person
who installed it.

Consequences, all measured with the real binary on a fixture of eight text files and five binaries:

* **Lockfile churn.** Installing the lockfile a Windows author wrote, on a machine with LF endings, exited 0 and
  reported `clean`, but silently rewrote the entry (`01db1923…` → `16c03b27…`). The next machine flips it back.
  Every teammate on a different OS gets a modified `dotquay.lock` and a merge conflict waiting in every pull request.
  (Cause: `install` records the hash of what it just wrote, see below; it only shows when that differs.)
* **False "modified".** `status` hashes the folder on disk and compares it with the recorded value, so a lockfile
  from the other OS can read as hand-edited. v0.12 added `status --explain` to tell the user "line endings only,
  forcing is safe"; that explains the symptom but leaves the cause.

## What the hash is for

Worth stating, because it is what makes the fix safe. `content_hash` is a **local-edit detector for the folder on
disk**: `DirtyChecker` compares it with what is installed before an update overwrites anything (P0-6). It is not an
integrity check on what was downloaded: that is the commit SHA, which this change does not touch. And `install`
re-records it from what it wrote, rather than verifying the incoming files against it. So making it insensitive to line
endings loses no protection.

## Behaviour

**Canonical hash:** text files are hashed as if every line ending were LF. "Text" is git's own rule: no NUL byte in
the first 8000 bytes. Binary files are never touched, so a real change inside a binary still changes the hash. A lone
CR is content, not a line ending. Editor-generated files stay excluded (v0.9). **The files on disk are not changed**
by this: only the number is normalised, so patches, merges and Godot see exactly what git wrote.

**Reading old lockfiles** (`DirtyChecker`): clean when the recorded hash equals *any* form the tree can have, computed
in one pass: LF text, CRLF text, or as-written bytes, each with and without editor files. That covers every lockfile
an older dotquay wrote on any machine: no migration, no flag day. A real edit is still dirty against every one of them
(tested).

**Writing** (`SyncEngine.SwapAndHash`): a (re)install records the canonical hash, **unless the lockfile already holds a
value that still describes the installed tree, in which case the recorded value is kept.** A reinstall of the same
commit therefore changes nothing in `dotquay.lock`, whichever OS recorded it and whichever form it is in. A lockfile
line changes when the content does. (The one case where it should still change is a genuine content change, such as
the LFS remediation in v0.22, where the recorded hash was over placeholders and the files really were different.)

On Linux and macOS the canonical hash equals what older dotquay recorded there, so nothing there changes at all.

`status --explain`: a tree that differs from the lockfile only by line endings is now simply `clean`; the
`LineEndingsOnly` classification stays for line-ending churn that sits **beside** a real edit.

## Verification

* 1319 tests pass. `LineEndingHashTests` (unit); `ProcessGitClientLineEndingsRealProcessTests` fetches one commit under
  `core.autocrlf=true`, `core.eol=crlf`, and an empty config through a real git, asserts the bytes on disk differ
  (so the test is not vacuous) and the canonical hash does not. With the canonical form reverted to as-written bytes,
  six of them fail, both real-git ones included.
* Published NativeAOT binary, run for real against the real GitLab LFS remote, with a Windows-style
  (`autocrlf=true`) and a Linux-style (empty config) machine: (A) Linux-style install from the Windows-recorded
  lockfile leaves it **unchanged** and `clean` (it was rewritten before); (B) Windows-style install, which really
  wrote CRLF files, from the Linux-recorded lockfile: unchanged, `clean`; (C) a new project on each machine records the
  **same** hash; (D) flipping a file's line endings stays `clean`, a one-word edit and an edit inside a binary that
  contains CRLF bytes are both `modified`. 24 trim warnings, all in existing code; none from this change.

## Not covered

* A file marked `-text`/`binary` in a repo's `.gitattributes` that contains no NUL: git leaves its endings alone but
  the hash treats it as text. A lockfile from the other OS can then read as modified for that file; `update` is not
  blocked, because the incoming tree is compared byte for byte (`MatchesIncomingVersion`). Rare, and conservative
  (reports modified, never hides an edit).
* A text file whose only change is its line endings is, by design, not a modification any more.
* Git diffs of an addon *committed into a consumer repository* still follow that repository's own `.gitattributes`;
  this change does not touch what is written to disk. A consumer that commits addons (VBeami does) wants
  `* text=auto eol=lf` for them.
