@tool
class_name DotquayOperation
extends RefCounted

## A typed view over the generic operation payload shared by install/update/remove/override/patch and
## every other mutating command that isn't add/add-preview/graph/status/outdated/doctor (SPEC.md §17)
## — whatever [DotquayPanel._run] falls through its command match to at the `_:` case.
##
## Every mutating command reports the same two things: free-form messages the CLI wants surfaced
## verbatim (a warning about an untagged repo, a note that a patch reapplied cleanly), and the concrete
## per-addon changes it made. Nothing here is addon-graph shaped — this answers "what did the disk just
## do", not "what does the project look like now" ([DotquayGraph]'s job).


class Change extends RefCounted:
	var name: String = ""
	var action: String = ""
	var to: String = ""

	static func from_payload(payload: Dictionary) -> Change:
		var change := Change.new()
		change.name = DotquayWire.str_or_empty(payload.get("name"))
		change.action = DotquayWire.str_or_empty(payload.get("action"))
		change.to = DotquayWire.str_or_empty(payload.get("to"))
		return change

	func is_unchanged() -> bool:
		return action == DotquayWire.ACTION_UNCHANGED


var messages: PackedStringArray = PackedStringArray()
var changes: Array = []   ## Array[Change]


static func from_payload(payload: Dictionary) -> DotquayOperation:
	var operation := DotquayOperation.new()

	for message in DotquayWire.array_or_empty(payload.get("messages")):
		operation.messages.append(String(message))

	for entry in DotquayWire.array_or_empty(payload.get("changes")):
		if typeof(entry) == TYPE_DICTIONARY:
			operation.changes.append(Change.from_payload(entry))

	return operation


## Changes worth telling the user about — everything except entries the CLI itself reports as
## untouched.
func meaningful_changes() -> Array:
	return changes.filter(func(change: Change) -> bool: return not change.is_unchanged())


## True when the command touched nothing at all: no messages of its own, and every reported change (if
## any) was `unchanged`. Distinguishes a command that quietly did nothing from one that merely finished
## fast (P1-27) — the caller decides how to word that, this just answers the question.
func did_nothing() -> bool:
	return messages.is_empty() and meaningful_changes().is_empty()
