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

## `graph.source`
const GRAPH_SOURCE_LOCKFILE := "lockfile"
const GRAPH_SOURCE_RESOLVED := "resolved"

## The synthetic requirer name the resolver uses for the project's own [addons.*] entries.
const PROJECT_NODE := "<project>"


## JSON nulls arrive as `null`, and `String(null)` yields "<null>" rather than "". Every optional
## field goes through here, so a missing value renders as absent instead of as that literal text.
static func str_or_empty(value: Variant) -> String:
	return "" if value == null else String(value)


## Turns a wire requirer name into something worth showing a person.
static func display_requirer(requirer: String) -> String:
	return "this project" if requirer == PROJECT_NODE else requirer
