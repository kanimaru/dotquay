# DESIGN v0.17 — dotquay updates itself

Status: **shipped 2026-09-11**, verified against a real NativeAOT binary. The contract now lives in SPEC.md §19.

## The problem

Every dotquay a user has was downloaded by hand from a GitLab release page. CI cuts a release on
every green push to `main` that touches `src/**` (docs/../.gitlab-ci.yml), so "the version people
are running" and "the version that exists" drift apart within a day, and the only signal anyone gets
that they have drifted is a bug that was fixed three releases ago. The complaint that prompted this
is not "updating is hard", it is "I did not know there was anything to update to".

That framing decides the shape: the **notice** is the feature, and `self update` is what the notice
tells you to run. A tool that updates silently would fix the same complaint and introduce a worse
one — a binary that changed underneath a CI job nobody asked to change.

## Commands

| Command | What it does | Exit |
|---|---|---|
| `dotquay self check [--json]` | Ask the channel what the newest release is and compare it to this binary. Writes nothing but the cache. | 0 current, 3 update available, 1 error |
| `dotquay self update [--to <version>] [--dry-run] [--force] [--json]` | Download, verify, and swap this executable. | 0 done/already current, 1 error |
| `dotquay self rollback [--json]` | Put back the binary the last update retired. | 0 done, 1 nothing to roll back / error |
| `dotquay self notify <on\|off>` | Turn the passive notice on or off without a text editor. | 0 |

`self check`'s exit 3 exists so a script can branch without parsing anything. It is deliberately not
1: "there is a newer dotquay" is not a failure, and a pipeline that runs `self check` should not go
red for it. `self notify` exists for the reason `dotquay source add` exists (v0.14): a setting whose
only interface is a text editor is a setting most people never touch.

## Where releases come from — `IReleaseChannel`

```csharp
public interface IReleaseChannel
{
    string Describe { get; }
    Task<ReleaseManifest?> GetLatestAsync(CancellationToken ct);
    Task<ReleaseManifest?> GetVersionAsync(string version, CancellationToken ct);
    Task<byte[]> DownloadAsync(ReleaseAsset asset, CancellationToken ct);
}
```

The seam is the point. dotquay's own releases live in a **private** GitLab project today
(`wild-alliance/dotquay`, project 128 on gitlab.kemomi.de — an unauthenticated API call gets a 404,
the web URL redirects to sign-in), so the shipping channel authenticates. But "private GitLab" is a
fact about this year, not about the design: a public mirror, a plain static file host, or a
`file://` channel for testing must all be reachable without touching the updater. Everything above
`IReleaseChannel` deals in `ReleaseManifest`/`ReleaseAsset` and has never heard of GitLab.

`GitLabReleaseChannel` resolves a release in two requests:

1. `GET <base>/releases?per_page=1` (or `<base>/releases/v<version>` for `--to`), which yields the
   tag and `assets.links[]`.
2. `GET` the link named `manifest.json`, which is the per-version document CI publishes:

```json
{
  "schema": 1,
  "version": "0.17.0",
  "released_at": "2026-09-11T10:04:11Z",
  "assets": [
    { "rid": "linux-x64", "file_name": "dotquay-linux-x64",
      "url": "https://.../packages/generic/dotquay/0.17.0/dotquay-linux-x64",
      "sha256": "…", "size": 11534336 }
  ]
}
```

The manifest is what makes "what is newest, for my platform, and what should it hash to" one
round-trip instead of three, and it is immutable: it is uploaded per version to the generic package
registry, never overwritten, so there is no `latest.json` to race a publish against. "Latest" is
answered by the Releases API, which is already the authority on what has actually shipped.

**No checksum, no update.** A release whose manifest is missing or whose asset carries no `sha256`
is refused with a message saying so. There is no `--allow-unverified`: the whole value of a
self-updater is that it is less risky than downloading a binary by hand, and an unverified download
is exactly as risky as downloading it by hand.

Credentials come from the existing stack — `CredentialResolver` over `~/.dotquay/credentials.toml`
plus the `DOTQUAY_NUGET_*` environment pairs (SPEC.md §11.2), scoped by host. Someone who already
ran `dotquay login gitlab.kemomi.de` for the private NuGet feed is already logged in for updates.
Someone who has not gets a `JsonErrorKind.Auth` failure naming the command to run, not a 404.

## Configuration

`~/.dotquay/config.toml` — new, user-level, alongside `credentials.toml` and `cache/`:

```toml
[update]
channel = "https://gitlab.kemomi.de/api/v4/projects/128"
check = true
interval_hours = 24
```

Precedence, highest first: `DOTQUAY_UPDATE_URL` / `DOTQUAY_NO_UPDATE_CHECK=1`, then the file, then
the built-in defaults. Environment beats file for the same reason it does for credentials — CI sets
variables, and a CI image should not have to write a config file to stop a tool from phoning home.

## The notice

Printed after the command, from cache only:

```
dotquay 0.18.0 is available (you have 0.17.0) — run 'dotquay self update'
```

Four rules make this safe to have on by default:

- **It goes to stderr.** stdout is a contract: `list --json` is parsed by the editor plugin and
  `source list` is parsed by shell scripts. A human-readable line on stdout would break both, and
  "only when not `--json`" is not enough, because a plain `dotquay list | grep` is just as parseable.
- **It is rendered from the cache file**, `~/.dotquay/update-check.json`, never from a live request.
  A command's latency never depends on a network the user did not ask to touch.
- **The refresh is detached.** When the cache is older than `interval_hours`, a request is started
  at process start and given at most 500 ms at exit to finish and write the cache. If it does not
  finish, nothing is printed and nothing is written; the next invocation tries again. It can never
  fail a command — every path is wrapped, and a throwing notice is a swallowed notice.
- **It stays quiet where it is noise**: under `--json`, when stderr is redirected, when `CI` is set,
  when `[update] check = false`, and when `DOTQUAY_NO_UPDATE_CHECK` is set.

## Replacing a running executable

One code path for both operating systems, because the rename dance Windows *requires* is also legal
on Linux, and two paths would mean the one nobody develops on is the one that breaks.

Let `P` be `Environment.ProcessPath` and `D` its directory.

1. **Guards, before anything is downloaded** (below).
2. Download to `D/dotquay.new-<version>[.exe]`.
3. Hash it and compare with the manifest. Mismatch: delete, refuse, exit 1.
4. `chmod +x` on Unix, then **run the staged binary** with `--version` and require it to print the
   version the manifest promised. This is the project's own verification rule (a binary that builds
   is not a binary that runs — docs/SPRINT-01.md) applied to the one case where the binary being
   checked is the one about to replace the checker.
5. Move `P` → `D/dotquay.old-<currentversion>[.exe]`. A running image cannot be deleted on Windows
   but can be renamed, which is what makes this work at all.
6. Move the staged file → `P`. If this fails, move the retired file back and refuse; the window
   between 5 and 6 is two renames inside one directory, which is as small as this gets without
   kernel support.
7. Sweep: keep the newest `dotquay.old-*`, delete any older ones. The newest is what `self rollback`
   restores; the rest are litter.

Rollback is the same dance with the two files swapped, and it re-runs step 4 against the retired
binary first — a retired binary that no longer starts is not a rollback target.

### Guards

`self update` refuses, before touching the network, when:

- **This is a build output, not an installed binary** — `D` contains a `*.pdb` or `*.deps.json`, or
  the path has a `bin`, `obj`, or `publish` segment. Overwriting `bin/Release/.../dotquay.exe` with
  a release build is never what anyone means, and the version a dev build reports is the csproj's,
  which is usually behind. `--force` overrides.
- **`D` is not writable** — probed by writing and deleting a temp file, so the failure is "your
  install is in a place you cannot write to, here is where it is" instead of a half-finished swap.
- **This platform has no asset in the release** — the message lists the RIDs the release does have.
- **It is already current** and `--force` was not given. `--to` an older version is allowed and says
  out loud that it is a downgrade.

## CI

`build:*` additionally writes `<artifact>.sha256`. A new `manifest:` job (needing both builds) emits
`dotquay-<version>.json` from those two checksums. `upload:*` pushes the binaries, their `.sha256`
files, and the manifest to the generic package registry; both release jobs gain asset links for
`manifest.json` and the two checksum files. `smoke:linux-x64` gains `self check --json` — the new
payload is a new closed generic in `DotquayJsonContext`, which is precisely the class of thing that
builds clean, passes every unit test, and fails only in the published binary.

The win-x64 artifact CI ships is still trimmed rather than AOT (no Windows runner — the known,
accepted hole documented in the pipeline header). Self-update does not change that, but it does
raise the stakes: a trimming regression now reaches users through a mechanism that installs it for
them. This is the strongest argument yet for a Windows runner, and it is worth writing down here
rather than rediscovering it.

## The editor plugin

`self check --json` runs when the Dotquay panel loads. An available update shows a banner scene with
one button that runs `self update --json`. The plugin is replacing the CLI it talks to, not itself,
so no editor restart is needed — but the plugin must re-read the version afterwards, because every
envelope carries it and a stale one is what a "your dotquay is older than this plugin" message would
be based on.

## Install scripts

`scripts/install.sh` and `scripts/install.ps1`: download the newest release for the current
platform, verify its sha256 against the same manifest, and place it at `~/.local/bin/dotquay`. They
exist so that the first install and every subsequent update put the binary in the same place, which
is what makes `self update`'s writability guard a non-event for anyone who used them. Both accept a
token (`DOTQUAY_TOKEN`, or `-Token`) because the channel is private.

## What this deliberately does not do

- **No automatic install.** `[update] auto = true` was considered and left out: a package manager
  that changes its own version without being asked is a package manager whose bug reports cannot be
  reproduced. The notice is the feature.
- **No signature verification.** A sha256 from the same host that served the binary proves
  integrity, not provenance; real provenance needs a signing key with an owner, a rotation story, and
  somewhere to publish the public half. Worth doing when dotquay has users outside one GitLab
  instance, not before, and the manifest has room for a `signature` field when it is.
- **No package-manager backends.** No Homebrew tap, no winget, no apt repo. Each is a distribution
  channel with its own release cadence and its own way of being wrong; `IReleaseChannel` is where
  they would attach if they ever earn their keep.
