# DESIGN v0.20 — guessing an addon's subtree, and asking before using the guess

Status: shipped 2026-09-21 (all three phases), verified against a real NativeAOT binary and the live
`SproutyLabs/SproutyDialogs` repository — see §10.

## 1. The gap

`dotquay add <git-url>` derives the addon's name from the repository's last URL segment and guesses
its subtree as `addons/<name>` (`AddonSpec.EffectivePath`). Two corrections already exist for when
that guess is wrong, and between them they cover exactly one kind of wrongness:

- `AddonInstaller.CorrectFolderCase` / `SubtreeExtractor.ResolveSubtreePath` repair **capitalisation**
  (`bitwes/Gut` → `addons/gut`). A segment with no case-insensitive match is left alone on purpose,
  so dotquay never infers a layout it wasn't told (SPEC.md §7.1, OQ-2).
- `GodotProjectRootChecker` reports a subtree that **exists but is a Godot project** rather than an
  addon, and names where the real addon sits.

Neither covers a repository whose addon folder spells the same name in a different **word-separator
convention**. Reported case:

```
$ dotquay add https://github.com/SproutyLabs/SproutyDialogs
Configured path 'addons/SproutyDialogs' does not exist in the fetched repository at
C:\Users\kani\.dotquay\cache\...
```

The repository holds `addons/sprouty_dialogs`. `SproutyDialogs` and `sprouty_dialogs` differ by more
than case, so the case correction declines — correctly, by its own contract — and the user is left
with an error that names the path dotquay guessed and says nothing about the path that would have
worked. The fix is `--path addons/sprouty_dialogs --install-as sprouty_dialogs`, which is knowable
from the fetched repository and is not knowable from the message.

This is not a rare shape. PascalCase repository names over snake_case addon folders are the majority
convention split in the Godot ecosystem, because GDScript style is snake_case and repository names
follow the project's branding.

## 2. What this does not become

dotquay does not start inferring layout. The rule that produced `CorrectFolderCase`'s narrowness is
still the right rule; what changes is that a guess dotquay is confident about is **offered** rather
than discarded, and a human says yes before anything is written.

So: never silent, never on `install`, and the answer is recorded in `dotquay.toml` so it is asked
once and then reproducible for everyone else. A caller that cannot be asked — `--json`, CI, the
editor plugin, `install` replaying a lockfile — gets a message naming the exact flags instead, which
is itself the whole improvement for those paths.

## 3. Candidate rules

Two, both deliberately narrow. A candidate is only ever a **directory that exists in the fetched
repository**; nothing is synthesised.

### Rule 1 — the same words in a different convention

Both names are reduced to their letters and digits, lowercased, with every separator dropped. Equal
normalisations match:

| guess | repository folder | normalised | match |
|---|---|---|---|
| `SproutyDialogs` | `sprouty_dialogs` | `sproutydialogs` | yes |
| `SproutyDialogs` | `sprouty-dialogs` | `sproutydialogs` | yes |
| `phantom_camera` | `PhantomCamera` | `phantomcamera` | yes |
| `godot_dialogue_manager` | `dialogue_manager` | `godotdialoguemanager` / `dialoguemanager` | no |

The last row is the point of the table. Dropping a leading `godot_` would catch a common habit, and
it was considered and declined: "the candidate's words are a run of the guess's words" also matches
`core` against `vbeami_core` and `ui` against `ui_kit`, which is a different addon, not the same one
spelled differently. Rule 2 usually catches those repositories anyway, and when it doesn't, `--path`
is one flag.

Fuzzy/edit-distance scoring was declined for the same reason, one step further along: a suggestion
that is merely near is a suggestion whose confirmation prompt cannot be honest about what it found.

### Rule 2 — the repository holds exactly one addon

If exactly one directory in the repository holds a `plugin.cfg`, that is the addon, whatever it is
called. Same test Godot itself uses, and the same "the only candidate" fallback
`GodotProjectRootChecker.Check` already applies. Three shapes are searched, in this order, and only
the first that yields anything is used:

1. `addons/*` at the repository root — the ordinary layout.
2. the repository root itself, when it holds a `plugin.cfg` — the addon *is* the repository, for
   which the fix is the `path = "."` SPEC.md §7.1 already requires to be stated explicitly.
3. `*/addons/*` one level down — a repository that keeps its Godot project in a subfolder
   (`godot/addons/vbeami`).

Rule 1 wins over Rule 2 where both fire. Two or more candidates under either rule means no
suggestion: the message lists them and stops, because "several plugins and none is obviously this
one" is a question dotquay cannot answer for the user (again matching `GodotProjectRootChecker`).

## 4. The confirmation seam

`ISubtreeChoice` in `Dotquay.Core.Abstractions`, alongside `ISourceApproval` and
`IGeneratorAssetDecision`, for the same reason those are seams: `Dotquay.Core` holds no console, the
editor plugin drives dotquay through captured stdout and cannot answer a prompt, and a workflow that
blocks on `Console.ReadLine` is untestable.

- `DeclineSubtreeChoice` — refuses every request. The default, so nothing adopts a guess on a
  user's behalf where nobody can be asked.
- `ConsoleSubtreeChoice` (`Dotquay.Cli`) — the prompt. Constructed only for an interactive,
  non-`--json` run, exactly like `ConsoleSourceApproval`.

There is deliberately **no** pre-approved implementation mirroring `PreApprovedSourceApproval`. The
replay channel for a confirmed guess already exists and is better: `--path` and `--install-as` are
what the user is agreeing to, so a caller that has shown the suggestion and got a yes passes those
two flags, and the probe never runs at all (an explicit `path` is never second-guessed). One code
path, and the manifest records the same two values either way.

## 5. Where the probe runs

In `PrepareAddSpecAsync`, which `AddAsync` and `PreviewAddAsync` already share verbatim — the same
reason that sharing exists: a preview that guessed differently from the add it previews is the exact
failure a preview is for.

```
options.Path is not null                    -> no probe. A stated choice is never second-guessed.
guessed subtree exists (case-resolved)      -> no finding. Nothing to guess.
finding with no suggestion                  -> WorkflowException listing the candidates.
finding with a suggestion, previewing       -> apply it, report it, ask nothing.
finding with a suggestion, adding           -> ISubtreeChoice; yes applies it, no throws.
```

Applying it means `Path = <candidate>` **and** `InstallAs = <candidate's folder name>`, both written
into `[addons.<name>]`. `install_as` is not optional here: a Godot addon refers to its own files by
absolute `res://addons/<folder>/` paths, which are case- and word-sensitive strings even where the
filesystem is not, so installing `sprouty_dialogs`' content into `addons/SproutyDialogs` reproduces
the Gut bug in full (SPEC.md §7.1.1) — permanently `modified` on Windows, broken outright on Linux.
The manifest key stays the repository-derived name: identity is not what is being corrected.

The probe needs a fetched repository, so it resolves the spec and calls `RepoCache.GetOrFetchAsync`
before the graph walk does. That is one extra ref query; the fetch itself is shared through the
cache, and the graph walk is about to want the same commit. A probe that throws is swallowed — the
resolver reports resolution and transport failures properly a moment later, and a probe is not
allowed to be the thing that reports them badly.

`update` needs none of this: a successful `add` has already written `path` into the manifest, so the
guess never runs twice.

## 6. Transitive addons

Not prompted for, and not guessed. A transitive addon's `path` comes from **someone else's** package
manifest, so the fix belongs either upstream or in this project's `[overrides]` — the same split
`GodotProjectRootChecker` already makes. `SubtreeExtractor.Extract`'s existing "does not exist in the
fetched repository" `SyncException` gains the candidate list and the `dotquay override <name> --rev
<sha> --path <path>` line, and that is the whole change on this path. It also covers a hand-written
bad `path` in the project's own manifest, which is the other way to reach that exception.

## 7. Wire contract

`add --preview --json` gains:

```jsonc
{
  "subtree": {
    "guessed_path": "addons/SproutyDialogs",
    "path": "addons/sprouty_dialogs",
    "folder": "sprouty_dialogs",
    "reason": "name_shape"          // or "only_addon"
  },
  "needs_subtree_confirmation": true
}
```

`subtree` is `null` when the guess was right, when `--path` was given, or when there is no
suggestion. `needs_subtree_confirmation` mirrors it as a boolean for the same reason
`needs_approval` mirrors `sources_needing_approval`: a UI reads one field per banner.

`reason` is a fixed string, so — like `DoctorIssueKind` — adding a third rule later is a contract
change the plugin has to be taught, not something that "just works". The plugin renders an unknown
reason as the generic sentence rather than dropping the row.

The preview's exit code is unchanged: `--preview` already returns 2 when it discovered a question,
and a subtree needing confirmation is one. A caller that only checks for zero still behaves.

## 8. The editor plugin

The two-phase flow already exists (`add --preview` → `install_preview_dialog` → `add` with the
answer), and the add form already has `--path` and `--install-as` fields. So this is a section in
the existing dialog, not a new dialog:

> **Where this addon lives**
> dotquay looked for `addons/SproutyDialogs` and found `addons/sprouty_dialogs` — the same name in
> snake_case. It will be installed into `addons/sprouty_dialogs`.

and on confirm, `DotquayAddRequest.path` / `.install_as` are filled from the payload before
`to_args()` runs. Declining is closing the dialog, as it already is for feeds.

## 9. Phasing

| Phase | Contents | Status |
|---|---|---|
| 1 | `SubtreeCandidates`, the improved `SyncException`, tests | shipped 2026-09-21 |
| 2 | `ISubtreeChoice`, the probe in `PrepareAddSpecAsync`, `ConsoleSubtreeChoice` | shipped 2026-09-21 |
| 3 | preview payload, plugin section | shipped 2026-09-21 |

## 10. Verification

`dotnet test` (1262 green) and `run_tests.ps1` (1122 assertions) are the starting bar. What was
actually run, against a NativeAOT publish of `src\Dotquay.Cli` and the live repository, in a throwaway
Godot project:

| Command | Result |
|---|---|
| `add <url> --json` | exit 1, refusal naming `--path addons/sprouty_dialogs --install-as sprouty_dialogs` |
| `add <url>` with stdin piped | same refusal — nothing that cannot ask adopts a guess |
| `add <url> --preview --json` | exit 2, `subtree` populated with `reason: "name_shape"`, graph resolved against the suggestion |
| `add <url> --preview` | the finding printed above the graph, with the re-run line under it |
| `add <url> --path addons/sprouty_dialogs --install-as sprouty_dialogs` | installed to `addons/sprouty_dialogs`; both keys written to `dotquay.toml` |
| `status` / `doctor --json` | `clean`, `healthy: true` — no folder-case mismatch and no install-folder drift, which is the end-to-end proof that the folder half of the answer was right |

The em-dash in the new messages was checked in real console output and in the JSON envelope
(`—`), since generated-file mojibake has bitten this project before.

**Not covered by any of the above:** the interactive `[Y/n]` prompt itself, which needs a real
console — a test host and a piped shell both report `Console.IsInputRedirected`, which is exactly
what makes them decline. `ConsoleSubtreeChoiceTests` pins what each answer means by swapping
`Console.In`/`Console.Out`, and the gating is the same check `ConsoleSourceApproval` already uses, but
the two have never been run together outside a terminal. Worth one hand-run in a real shell:

```
dotquay add https://github.com/SproutyLabs/SproutyDialogs --project <a throwaway Godot project>
```
