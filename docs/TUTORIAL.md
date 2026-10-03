# Tutorial: a new Godot project with dotquay

A worked end-to-end example: a fresh Godot project that consumes **vbeami** — a private addon on
`gitlab.kemomi.de` that also pulls a private NuGet package (`VBeami.Core`) behind a login. It touches
every interesting part of dotquay at once: `login`, addon-declared feeds, transitive addons, and the
`.csproj` side.

Substitute your own host/addon anywhere below; nothing here is vbeami-specific except the URLs.

## What you need first

- `dotquay` on `PATH` (`dotquay --version`).
- Godot **4.7 .NET** and the .NET SDK (`dotnet --info`). vbeami is a C# addon.
- `git` on `PATH`, with access to the addon's repo (SSH key or an HTTPS credential).
- A GitLab personal access token with the **`read_api`** scope — see [§ Why `read_api`](#why-read_api).

## 1. Create the project, then create the C# solution

Make the Godot project as usual, then — **before** running dotquay — turn on C#:

> Godot → **Project → Tools → C# → Create C# solution**

dotquay writes `PackageReference` entries into your project's `.csproj`. With no `.csproj` in the
project root it stops with `No .csproj found in <dir>`. A GDScript-only project is fine and skips
this step entirely; a project consuming vbeami is not.

## 2. Scaffold the manifest

```bash
cd path/to/MyGame
dotquay init
```

That writes a minimal `dotquay.toml`. You do **not** need `init --nuget-source` here: vbeami declares
its own feed and hands it to you in step 4.

## 3. Log in to the host

```bash
dotquay login gitlab.kemomi.de
```

It prompts for username and token and verifies the credential against the server before storing it,
keyed by host — one login covers both git-over-HTTPS and the NuGet feed on that host. For scripts and
CI, pipe it instead:

```bash
echo "$GITLAB_TOKEN" | dotquay login gitlab.kemomi.de -u "$GITLAB_USER" --token-stdin
```

`dotquay login --list` shows stored hosts and usernames, never tokens.

> The username must be a **real** account name. The NuGet endpoints authenticate over HTTP Basic and
> reject a placeholder.

## 4. Decide how the addon's feed gets adopted

`VBeami.Core` lives on a private feed that *the addon itself* declares, in its own package manifest:

```toml
[nuget.sources.vbeami]
url = "https://gitlab.kemomi.de/api/v4/projects/124/packages/nuget/index.json"
packages = ["VBeami.*"]
```

Your project decides whether to accept that feed. Two ways:

**a. Trust the host up front** — add this to your `dotquay.toml` before step 5:

```toml
[nuget]
trusted_hosts = ["gitlab.kemomi.de"]
```

Feeds declared by addons on a trusted host are adopted silently. Trust is matched on exact host+port
— no subdomain globbing.

**b. Answer the prompt** — with no trust configured, `add` asks, once, and writes the accepted feed
into your committed `dotquay.toml` with a `declared_by = "vbeami"` marker.

Want to see what you're agreeing to first? `add --preview` writes nothing at all:

```bash
dotquay add https://gitlab.kemomi.de/wild-alliance/vbeami/ --path godot/addons/vbeami --version "~0.7" --preview
```

It reports the full addon graph and every feed that would be adopted; exit code `2` means "this needs
your approval". Re-run the real `add` with `--adopt-source <url>` per feed you accept.

## 5. Add the addon

```bash
dotquay add https://gitlab.kemomi.de/wild-alliance/vbeami/ \
  --path godot/addons/vbeami \
  --version "~0.7"
```

- The addon name is derived from the URL (`vbeami`).
- `--path` is the subtree **inside the upstream repo** to extract — vbeami's repo is a whole
  application, and only `godot/addons/vbeami/` is the addon.
- `--version` is a constraint matched against the repo's git **tags**: `1.2.3` is exact, `~1.2` is
  `1.2.x`, `^1.2` is anything below `2.0.0`, and `>=1.2 <2` works too (the full grammar is in the
  README). Use `--branch main` to track a branch, or `--rev <sha>` to pin a commit.

When it finishes you have:

```
MyGame/
├─ dotquay.toml          your addon + the adopted [nuget.sources.vbeami] feed
├─ dotquay.lock          every addon pinned to an exact commit, with content hashes
├─ NuGet.Config          feed URLs only — never credentials; committable
├─ MyGame.csproj         + <PackageReference Include="VBeami.Core" ... />
└─ addons/
   ├─ vbeami/
   ├─ vrm/                 } transitive: vbeami's own package manifest declares these,
   └─ Godot-MToon-Shader/  } so you never had to know about them
```

Commit all of it except `addons/` if you prefer lockfile-driven checkouts; `dotquay.toml` and
`dotquay.lock` are the two files that must be committed.

## 6. Build, then enable the plugin

vbeami's plugin script is C#, so the assembly has to exist before Godot can load it:

1. Build the solution (Godot's **MSBuild** panel, or `dotnet build`).
2. Reopen the project if the editor still shows the plugin as broken.
3. **Project → Project Settings → Plugins →** enable **VBeami**.

## 7. Use it

Instance `res://addons/vbeami/vbeami_rig.tscn` in your scene. The `VBeamiRig` node is the whole
"connect + track + present" baseline; the exports that matter first:

| Export | Meaning |
|---|---|
| `Source` | `IFacialMocap` (default), `Vmc`, or `Network` |
| `Port` | UDP port to listen on — iFacialMocap defaults to `49983` |
| `AutoStart` | start listening on `_Ready` |
| `EnableMultiplayer` | off means no socket is ever opened |

Point your phone/tracker at the machine, run the scene, and the avatar tracks. For anything beyond
the baseline, `IVBeamiApi` (`LoadAvatar`, `AddSource`, `JoinSession`) is the stable integration
surface — see vbeami's `docs/CUSTOM_NODES.md`.

## 8. Day-to-day commands

| Command | What it does |
|---|---|
| `dotquay install` | Replays the lockfile exactly. Reproducible; **never** re-resolves and never adopts a feed. This is the CI/teammate command. |
| `dotquay update [name]` | Re-resolves constraints, writes a new lockfile, installs. |
| `dotquay status --explain` | Which addons are clean/modified/linked/patched — and *why* something reads as modified. |
| `dotquay outdated` | Newer tags/versions available inside your constraints, the ones `update` would pick (an exact pin is never outdated). |
| `dotquay doctor` | Health check: folder-case mismatches, `install_as` drift, unusable trusted hosts, fragile `uid://` references. |
| `dotquay link <name> <path>` | Point an addon at a local checkout for development — live edits, no commit, no update. |
| `dotquay patch <name>` | Capture local edits to an addon as a patch that survives updates. |
| `dotquay remove <name>` | Drop it from the manifest, lockfile and disk. |

A new teammate cloning your repo needs exactly: `dotquay login gitlab.kemomi.de`, then
`dotquay install`.

## Troubleshooting

**`No .csproj found in <dir>`** — C# support isn't enabled in the Godot project. See step 1.

<a id="why-read_api"></a>**`insufficient_scope`, or 401/403 during restore** — the token needs
**`read_api`**. `read_package_registry` looks like the least-privilege answer and *is* accepted for
npm/Maven/PyPI on this instance, but the NuGet endpoints reject it. Test the token the way NuGet
actually authenticates (HTTP Basic, not a `PRIVATE-TOKEN` header):

```bash
curl -u "<your-username>:<token>" \
  "https://gitlab.kemomi.de/api/v4/projects/124/packages/nuget/query?q=VBeami"
```

JSON back (even `{"data":[]}`) means auth is fine.

**"no stable release on NuGet" for `VBeami.Core`** — the error names which feeds were actually
queried. If your private feed is among them, GitLab answered *empty*, and it answers identically
(404) for "never published" and "your token can't see the registry". Check both: the project's
**Deploy → Package registry** page, and the token scope above.

**The feed prompt never appeared** — that's correct if the host is in `trusted_hosts`; trust
short-circuits the prompt by design. It's also correct under `--json`, in CI, from the editor plugin,
and on `install`: a run that can't ask refuses rather than adopting silently.

**A feed URL 400s** — GitLab's NuGet registry endpoint needs the **numeric** project ID
(`/projects/124/...`), not the URL-encoded path.

**An addon reads `modified` right after a clean install** — open `dotquay status --explain`. Godot's
own `.uid`/`.import` output is excluded from the content hash, so this is usually a folder-casing
mismatch instead; `doctor` names the exact `install_as` line to add.

**A build outside dotquay can't restore the package** — the restore dotquay runs threads credentials
in as environment variables and writes none to disk, so it populates your global package cache
without leaving a token anywhere. That's enough for later editor builds of the same version. If you
need `dotnet restore` to work on its own with a cold cache, add the feed to your own NuGet
credentials as well — vbeami's `docs/PACKAGING.md` §1 covers both ways.
