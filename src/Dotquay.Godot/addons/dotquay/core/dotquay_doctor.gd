@tool
class_name DotquayDoctor
extends RefCounted

## A typed view over the `doctor --json` payload (SPEC.md §6.1, §7.1.1, §7.1.2, §7.3.1).
##
## One issue per row. `kind` is the CLI's own stable category for it — SPEC.md documents
## `addon_folder_case_mismatch` (§7.1.1), `install_folder_drift` and `package_asset_metadata_drift`
## (§7.1.2), and `fragile_uid_reference` (§7.3.1) among others, but this plugin does not maintain its
## own copy of that list: doctor can add a check dotquay's own docs haven't caught up to describing
## yet, and a UI that only recognised a fixed set would silently drop a real issue's category instead
## of just not having special-cased advice for it. [method Issue.describe] surfaces whatever `kind` the
## CLI actually sent, verbatim, ahead of the message it already wrote for a human.


class Issue extends RefCounted:
	var kind: String = ""
	var message: String = ""

	static func from_payload(payload: Dictionary) -> Issue:
		var issue := Issue.new()
		issue.kind = DotquayWire.str_or_empty(payload.get("kind"))
		issue.message = DotquayWire.str_or_empty(payload.get("message"))
		return issue

	## The log line for this issue. Leads with the kind only when the CLI actually sent one, so a
	## dotquay predating this field still reads as a plain sentence rather than "[] some message".
	func describe() -> String:
		return message if kind.is_empty() else "[%s] %s" % [kind, message]


var healthy: bool = false
var issues: Array = []   ## Array[Issue]


static func from_payload(payload: Dictionary) -> DotquayDoctor:
	var doctor := DotquayDoctor.new()
	doctor.healthy = bool(payload.get("healthy", false))

	for entry in DotquayWire.array_or_empty(payload.get("issues")):
		if typeof(entry) == TYPE_DICTIONARY:
			doctor.issues.append(Issue.from_payload(entry))

	return doctor
