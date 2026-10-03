# DESIGN v0.22 — Git LFS

**Status: shipped on branch `feature/lfs-safeguard`.** Written as a separate file rather than as a SPEC.md
edit; fold it into SPEC.md when the branch is merged.

## Problem

An addon repository can keep large binaries in Git LFS. dotquay fetches with a plain `git fetch --depth 1`
and `git checkout` (SPEC.md §9), so whether the files arrive as real files depends on the machine, and git does
not say when they did not:

| Machine | Result before this change |
|---|---|
| git-lfs installed **and activated** (`git lfs install`) | real files |
| git-lfs missing, **or installed but never activated** | git writes ~130-byte pointer text files and carries on |

dotquay then copied the pointers in as the addon, hashed them into the lockfile, and reported the addon
`clean`; `doctor` reported nothing. Measured with the real 0.18 binary on a fixture of three LFS-tracked
libraries: `status` said `clean`, `doctor` said "No issues found", and each "library" was 132 bytes of text
(`version https://git-lfs.github.com/spec/v1 / oid sha256:… / size …`). The first symptom is a native library
Godot cannot load, which reads as a platform problem.

"Installed but never activated" is the common one: a fresh Linux box, a CI image, a container.

## Behaviour

After every checkout (`LfsResolver`, called from `ProcessGitClient.FetchCommitAsync`):

1. `git grep -l -I -z` for the pointer's version line in the working tree. Each hit is confirmed by reading it
   (at most 1 KB, version line first, an `oid sha256:` line present), so a document that merely quotes the
   format is never mistaken for a pointer. **No pointers: nothing else runs.** A repository without LFS, or one
   where git already downloaded everything, costs one search.
2. Pointers present and `git lfs version` fails: stop with a message that names the files and the fix
   (install git-lfs, run `git lfs install`). Nothing is installed.
3. Pointers present and git-lfs usable: `git lfs install --local --skip-repo`, then `git lfs pull origin`, then
   look again. Anything still a pointer is an error naming `git lfs push --all` (an LFS server that answers but
   lacks the objects can exit 0 and leave pointers behind).

`--local`: the cache checkout is its own repository, and installing an addon must never change a user's global git
configuration. Verified by a test that compares the global config file before and after. `--skip-repo`: nothing
to hook into; no commits happen here.

A failed search (`git grep` itself erroring) is treated as "nothing to act on". This is a safeguard, not a gate: it
must never turn a working install into a failing one.

### The cache

`RepoCache` trusted any checkout once its completion marker existed. An entry written before this change can hold
pointers and would have been handed out forever, on every project, without touching the network. Fresh markers now
carry a `lfs-verified` line. An older marker (the SHA alone) is verified in place on its next use and then
rewritten: no re-download, and no new marker *name*, which would have discarded every cache entry on every machine
to catch a few. An entry that cannot be verified fails loudly and is not marked.

### Doctor

New `DoctorIssueKind.LfsPointerFile`, wire value `lfs_pointer_file` (append-only; the editor plugin has wording for
it). `LfsPointerScanner` is purely local: it reads only files of at most 1 KB, so an addon full of large binaries
costs nothing. It exists because an addon installed by an older dotquay, or copied from another machine, is already
on disk as placeholders with a lockfile hash computed over them. The message says to install git-lfs, run
`git lfs install`, delete the addon folder and run `dotquay install`; installing into a missing folder records the
hash of the real files, which refreshes the lockfile entry (one lockfile change to commit in projects that track it).

`IFileSystem` gained `GetFileLength` for this.

## Verification

* 1300 tests (`dotnet test`), including `ProcessGitClientLfsRealProcessTests` against a real repository with real
  LFS. Machine states are made deterministic with `GIT_CONFIG_GLOBAL`: one file carrying the LFS filter, one empty.
  Those tests return early (pass) where git-lfs is not installed, like the other real-process tests. With the
  repair disabled the two tests that exist for this bug fail; with it restored all six pass.
* Editor plugin suite: 1131 assertions, 0 failures.
* **Published NativeAOT binary, run for real** (the publish succeeded and the binary ran; trimming warnings were not compared against `main`): `doctor` against an addon of
  placeholders (old binary: clean; new: names the problem, text and `--json`); repair of a stale placeholder cache
  entry via the documented remediation; a fresh fetch on a machine with LFS installed but not activated; and a
  machine where git-lfs is unusable, which stops with the message, exit code 1, nothing installed, no half-done
  cache entry, and installs normally once git-lfs is back.
* Against the real LFS server (GitLab, `wild-alliance/vbeami_gdmp`, ~143 MB over ssh): real files, 14 s,
  hash-identical to the originals.

## Not covered

* **Hosts other than that GitLab.** LFS authentication goes through the same ssh or credential-helper path as the
  fetch; nothing here was tried against GitHub or a self-hosted server with different LFS auth.
* **Linux and macOS.** Everything above ran on Windows. The logic is plain git with `\0`-separated output and no
  OS-specific calls, but it has not been run elsewhere.
* **`git lfs pull` bandwidth**: pulls everything in the commit (the whole package), not just the subtree dotquay
  extracts. Acceptable for a package whose subtree is the repository; a monorepo with a large LFS history would want
  `--include` scoped to `path`.
