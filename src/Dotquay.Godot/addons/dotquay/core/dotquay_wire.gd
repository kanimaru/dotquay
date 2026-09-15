@tool
class_name DotquayWire
extends RefCounted

## Every literal that crosses the CLI boundary, in one place.
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
## anything?" (docs/DESIGN-v0.12-MODIFIED-REASON.md). "unavailable" is not an error: it means the
## locked commit could not be fetched to compare against, which is what being offline looks like.
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

## The CLI maintaining its own installation (v0.17). A wire contract like every other name here:
## these strings are what the envelope's `command` field carries, and the panel dispatches on them.
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
