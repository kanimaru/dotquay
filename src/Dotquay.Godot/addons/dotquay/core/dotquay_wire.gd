@tool
class_name DotquayWire
extends RefCounted

## Every literal that crosses the CLI boundary, in one place — every flag and subcommand word a call
## site chooses, and every value a payload can carry (C-16). The two flags every single call gets
## regardless of what it's asking for, `--json` and `--project`, are the one exception: [DotquayCli]
## appends those itself as part of running anything at all, not as a choice any call site makes, so
## they stay where that's done instead of turning into a constant nobody chooses between.
##
## These mirror `JsonMapper`'s explicit enum-to-string mappings on the C# side (SPEC.md §17). Kept in
## their own script rather than as constants on [DotquayGraph] for a concrete reason: [DotquayGraph]'s
## inner classes would otherwise have to name their own enclosing global class in default values,
## which GDScript resolves as a cyclic reference while the script is still being parsed.

## `addons[].status`
const STATUS_CLEAN := "clean"
const STATUS_PATCHED := "patched"
const STATUS_MODIFIED := "modified"
const STATUS_MISSING := "missing"
const STATUS_LINKED := "linked"

## `addons[].origin`
const ORIGIN_ROOT := "root"
const ORIGIN_TRANSITIVE := "transitive"
const ORIGIN_ORPHAN := "orphan"

## `addons[].source_kind`, `conflicts[].candidates[].kind`
const SOURCE_VERSION := "version"
const SOURCE_BRANCH := "branch"
const SOURCE_REV := "rev"

## `conflicts[].kind`
const CONFLICT_DIFFERENT_GIT_URLS := "different_git_urls"
const CONFLICT_DISJOINT_PINS := "disjoint_pins"
const CONFLICT_NO_COMMON_VERSION := "no_common_version"
const CONFLICT_INVALID_CONSTRAINT := "invalid_constraint"
const CONFLICT_DISAGREEING_INSTALL_FOLDERS := "disagreeing_install_folders"

## `changes[].action` on every mutating command's payload (`JsonMapper.ChangeAction`). "unchanged" is
## the only one of these the panel treats as "nothing reached disk", which is why the set is spelled
## out here rather than that one literal being tested for in isolation — a new action added on the C#
## side has to be added here too, and the compiler-less wire contract gives no other warning.
const ACTION_ADDED := "added"
const ACTION_UPDATED := "updated"
const ACTION_REMOVED := "removed"
const ACTION_INSTALLED := "installed"
const ACTION_UNCHANGED := "unchanged"

## `addons[].explanation.verdict` on `status --explain` — the answer to "would forcing cost me
## anything?" "unavailable" is not an error: it means the locked commit could not be fetched to
## compare against, which is what being offline looks like.
const VERDICT_SAFE_TO_FORCE := "safe_to_force"
const VERDICT_REVIEW_NEEDED := "review_needed"
const VERDICT_UNAVAILABLE := "unavailable"

## `addons[].explanation.changes[].kind`, from the installed tree's point of view.
##
## Spelled separately from [constant ACTION_ADDED] and friends despite sharing two literals: those
## describe what a command did to a dependency, these describe how one file differs from its locked
## commit. Collapsing them would make a rename on either side look safe.
const CHANGE_KIND_ADDED := "added"
const CHANGE_KIND_REMOVED := "removed"
const CHANGE_KIND_CHANGED := "changed"

## `addons[].explanation.changes[].class`. Only [constant CHANGE_CLASS_CONTENT] is a reason not to
## force — the rest is churn the editor regenerates on its own.
const CHANGE_CLASS_CONTENT := "content"
const CHANGE_CLASS_UID_ONLY := "uid_only"
const CHANGE_CLASS_WHITESPACE_ONLY := "whitespace_only"
const CHANGE_CLASS_LINE_ENDINGS_ONLY := "line_endings_only"
const CHANGE_CLASS_EDITOR_ARTIFACT := "editor_artifact"

## `graph.source`
const GRAPH_SOURCE_LOCKFILE := "lockfile"
const GRAPH_SOURCE_RESOLVED := "resolved"

## The synthetic requirer name the resolver uses for the project's own [addons.*] entries.
const PROJECT_NODE := "<project>"

## Envelope `command` values the panel routes on. `add --preview` reports itself under its own name
## rather than as "add", because it emits a different payload shape — handing an add's parser a
## preview would half-read it instead of rejecting it.
const COMMAND_ADD := "add"
const COMMAND_ADD_PREVIEW := "add-preview"
const COMMAND_GRAPH := "graph"
const COMMAND_OUTDATED := "outdated"
const COMMAND_DOCTOR := "doctor"
const COMMAND_STATUS := "status"

## The CLI maintaining its own installation. A wire contract like every other name here: these
## strings are what the envelope's `command` field carries, and the panel dispatches on them.
const COMMAND_SELF_CHECK := "self-check"
const COMMAND_SELF_UPDATE := "self-update"

## The remaining mutating commands the panel dispatches on. Same wire contract as every other
## `COMMAND_*` above: these strings are what the envelope's `command` field carries.
const COMMAND_INSTALL := "install"
const COMMAND_UPDATE := "update"
const COMMAND_OVERRIDE := "override"
const COMMAND_PATCH := "patch"
const COMMAND_REMOVE := "remove"
const COMMAND_INIT := "init"

## Error kinds the CLI itself puts on the wire, mirroring JsonErrorKind in Dotquay.Core (C-1). Kept
## here rather than beside [DotquayResult]'s own failure kinds, because those are ones this addon
## synthesises for a call that never reached the CLI at all (not configured, launch failed, bad
## output) — nothing on the other side of the process boundary knows those exist, so they stay put.
const KIND_WORKFLOW := "workflow"
const KIND_NO_MANIFEST := "no_manifest"
const KIND_CONFLICT := "conflict"
const KIND_GIT := "git"
const KIND_AUTH := "auth"
## dotquay.toml, dotquay.lock or dotquay.local.toml is not valid TOML or not the shape dotquay
## writes. The message names the file and the parser's (line,column); nothing works until it is fixed.
const KIND_INVALID_PROJECT_FILE := "invalid_project_file"
const KIND_UNEXPECTED := "unexpected"

## CLI flags a call site chooses to include, spelled once so "--" + something typed by hand can't
## drift from what a sibling call site spells for the same flag (C-21). Not every flag below is a
## bare literal a caller appends verbatim — [constant FLAG_VERSION]/[constant FLAG_BRANCH]/
## [constant FLAG_REV] are also reached through [method flag_for_source_kind] wherever the flag is
## chosen *by* a source kind rather than typed directly.
const FLAG_PREVIEW := "--preview"
const FLAG_ADOPT_SOURCE := "--adopt-source"
const FLAG_RESOLVE := "--resolve"
const FLAG_EXPLAIN := "--explain"
const FLAG_CLEAR := "--clear"
const FLAG_FORCE := "--force"
const FLAG_GIT := "--git"
const FLAG_INSTALL_AS := "--install-as"
const FLAG_PATH := "--path"
const FLAG_VERSION := "--version"
const FLAG_BRANCH := "--branch"
const FLAG_REV := "--rev"


## JSON nulls arrive as `null`, and `String(null)` yields "<null>" rather than "". Every optional
## field goes through here, so a missing value renders as absent instead of as that literal text.
static func str_or_empty(value: Variant) -> String:
	return "" if value == null else String(value)


## Optional array fields go through here for the same reason [method str_or_empty] exists for
## strings: SPEC.md §17 has the CLI emit them as JSON `null` rather than omitting the key, so
## `payload.get(key, [])` only supplies its default when the key is *absent* — a present-but-null
## value sails past that default and blows up the first `for x in null` that touches it.
static func array_or_empty(value: Variant) -> Array:
	return value if value is Array else []


## Turns a wire requirer name into something worth showing a person.
static func display_requirer(requirer: String) -> String:
	return "this project" if requirer == PROJECT_NODE else requirer


## Maps a wire command name — what [method DotquayPanel._run] labels a call, and what the envelope's
## own `command` field echoes back — to the CLI subcommand word(s) that belong at the front of the
## argument list (C-20).
##
## Almost always the same string repeated (`COMMAND_INSTALL == "install"`, and `"install"` is also
## `args[0]`), which used to be spelled out twice at every call site — once as the label, once inside
## a literal `PackedStringArray`. The two names that don't reduce to their own literal stay exceptions
## here rather than becoming a rule every call site has to remember: `self-check`/`self-update` are
## two words on the command line (`self check`) but one hyphenated wire name, and `add-preview` isn't
## a subcommand at all — it's `add ... --preview`, reported under its own wire name only because it
## returns a different payload shape than a plain `add`.
static func cli_command_words(command_name: String) -> PackedStringArray:
	match command_name:
		COMMAND_SELF_CHECK:
			return PackedStringArray(["self", "check"])
		COMMAND_SELF_UPDATE:
			return PackedStringArray(["self", "update"])
		COMMAND_ADD_PREVIEW:
			return PackedStringArray([COMMAND_ADD])
		_:
			return PackedStringArray([command_name])


## The `--<kind>` flag naming a source kind, for `override <addon> --<kind> <value>`. Two call sites —
## the override picker's [DotquayGraph.Candidate] and the panel's own inline override flow — used to
## build "--" + kind by hand (C-10, C-21); both go through this now. Matches [constant FLAG_VERSION]
## and friends when the kind is one this plugin recognises, and falls back to the same "--" + kind
## construction for one it doesn't — a picker built from a real `graph --resolve` payload should not
## break outright just because a newer CLI reports a kind this build has never heard of.
static func flag_for_source_kind(kind: String) -> String:
	match kind:
		SOURCE_VERSION:
			return FLAG_VERSION
		SOURCE_BRANCH:
			return FLAG_BRANCH
		SOURCE_REV:
			return FLAG_REV
		_:
			return "--" + kind


## Turns a wire source-kind literal ("version"/"branch"/"rev") into the word a person would use
## (P1-21). "version" and "branch" are already plain English; "rev" is git's own jargon for a commit,
## so it's the one substitution this makes — everywhere a kind is shown, not just one label, or the
## wording would drift the next time a new display site is added.
static func display_source_kind(kind: String) -> String:
	return "commit" if kind == SOURCE_REV else kind
