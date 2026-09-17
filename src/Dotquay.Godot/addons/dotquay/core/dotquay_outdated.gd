@tool
class_name DotquayOutdated
extends RefCounted

## A typed view over the `outdated --json` payload (SPEC.md §6.1).
##
## Same job as [DotquayGraph], for a different question: not what the project has, but whether
## anything upstream has moved past it. Addons and NuGet packages share one entry shape, since
## "current version, latest version, is it behind" means the same thing for both — `outdated` checks
## `version`/`branch`-sourced addons via `git ls-remote` and dotnet packages via a NuGet metadata
## query, but reports both through the identical fields.


class Entry extends RefCounted:
	var name: String = ""
	var current: String = ""
	var latest: String = ""
	var is_outdated: bool = false

	static func from_payload(payload: Dictionary) -> Entry:
		var entry := Entry.new()
		entry.name = DotquayWire.str_or_empty(payload.get("name"))
		entry.current = DotquayWire.str_or_empty(payload.get("current"))
		entry.latest = DotquayWire.str_or_empty(payload.get("latest"))
		entry.is_outdated = bool(payload.get("is_outdated", false))
		return entry


var outdated_count: int = 0
var addons: Array = []   ## Array[Entry]
var dotnet: Array = []   ## Array[Entry]


static func from_payload(payload: Dictionary) -> DotquayOutdated:
	var outdated := DotquayOutdated.new()
	outdated.outdated_count = int(payload.get("outdated_count", 0))

	for entry in DotquayWire.array_or_empty(payload.get("addons")):
		if typeof(entry) == TYPE_DICTIONARY:
			outdated.addons.append(Entry.from_payload(entry))

	for entry in DotquayWire.array_or_empty(payload.get("dotnet")):
		if typeof(entry) == TYPE_DICTIONARY:
			outdated.dotnet.append(Entry.from_payload(entry))

	return outdated


## How many entries to announce. The CLI derives `outdated_count` from the same `is_outdated`
## predicate [method outdated_entries] filters on, so for any compliant dotquay the two agree. If they
## ever don't — an older CLI that sends the count without the per-entry flag, say — the larger wins:
## announcing more than is listed reads as an obvious oddity someone can report, while announcing
## fewer would silently hide a real entry from anyone scanning the list.
func reported_count() -> int:
	return maxi(outdated_count, outdated_entries().size())


func is_all_up_to_date() -> bool:
	return reported_count() == 0


## Every entry actually behind, addons before packages — what the panel logs, so no call site filters
## `is_outdated` by hand.
func outdated_entries() -> Array:
	var result: Array = []

	for entry in addons:
		if entry.is_outdated:
			result.append(entry)

	for entry in dotnet:
		if entry.is_outdated:
			result.append(entry)

	return result
