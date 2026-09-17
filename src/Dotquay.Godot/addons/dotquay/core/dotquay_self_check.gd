@tool
class_name DotquaySelfCheck
extends RefCounted

## A typed view over the `self-check --json` payload (C-24) — whether a newer dotquay release exists.
##
## Deliberately not folded into [DotquayOperation]: this payload never reports `changes`, and its two
## version strings mean something [DotquayOperation.Change] doesn't model. `self-update`, the command
## that actually performs the download, *does* report `messages` in the same shape every other
## mutating command does, so [method DotquayPanel._report_self_update] reads that one through
## [DotquayOperation] instead of a parser of its own.


var current_version: String = ""
var latest_version: String = ""
var update_available: bool = false


static func from_payload(payload: Dictionary) -> DotquaySelfCheck:
	var check := DotquaySelfCheck.new()
	check.current_version = DotquayWire.str_or_empty(payload.get("current_version"))
	check.latest_version = DotquayWire.str_or_empty(payload.get("latest_version"))
	check.update_available = bool(payload.get("update_available", false))
	return check


## True only when there is both a real latest-version string and the CLI's own verdict that it is
## newer — an update_available flag with no version to show would be a banner with nothing to say.
func has_available_update() -> bool:
	return update_available and not latest_version.is_empty()


func describe_update() -> String:
	return "dotquay %s is available — you have %s." % [latest_version, current_version]
