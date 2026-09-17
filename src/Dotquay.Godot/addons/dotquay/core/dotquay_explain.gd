@tool
class_name DotquayExplain
extends RefCounted

## A typed view over the `status --explain --json` payload
## (docs/DESIGN-v0.12-MODIFIED-REASON.md).
##
## Same job as [DotquayPreview], for a different question. `graph` says what the project has and
## `add --preview` says what an add would change; this says why an addon reads as `modified`, and
## whether forcing an update over it would destroy anything the user would miss.
##
## Nothing here is computed by the plugin — every classification is one the CLI made by diffing the
## installed tree against a fresh copy of its own locked commit. A UI that guessed at "safe to force"
## would be worse than no UI, because the user would have acted on the guess.


## One differing file.
class Change extends RefCounted:
	## Relative to the addon folder, forward-slashed.
	var path: String = ""
	var kind: String = ""
	var change_class: String = ""

	static func from_payload(payload: Dictionary) -> Change:
		var change := Change.new()
		change.path = String(payload.get("path", ""))
		change.kind = String(payload.get("kind", ""))
		# `class` is a GDScript keyword, so the field cannot carry the wire name.
		change.change_class = String(payload.get("class", ""))
		return change

	## The classes whose loss costs the user nothing — named exhaustively, never derived by excluding
	## [constant DotquayWire.CHANGE_CLASS_CONTENT].
	##
	## An allowlist is the only form of this check that fails in the safe direction. A newer CLI can
	## add a class this build has never heard of, and "not content, therefore harmless" would hand it
	## an enabled Force update button on the strength of not recognising it.
	const BENIGN_CLASSES := [
		DotquayWire.CHANGE_CLASS_UID_ONLY,
		DotquayWire.CHANGE_CLASS_LINE_ENDINGS_ONLY,
		DotquayWire.CHANGE_CLASS_EDITOR_ARTIFACT,
	]

	## True when discarding this file would cost the user nothing.
	func is_benign() -> bool:
		return BENIGN_CLASSES.has(change_class)

	## The leading marker, kept beside the states it describes so a new kind cannot be added without
	## deciding how it reads.
	func marker() -> String:
		match kind:
			DotquayWire.CHANGE_KIND_ADDED:
				return "+"
			DotquayWire.CHANGE_KIND_REMOVED:
				return "-"
			DotquayWire.CHANGE_KIND_CHANGED:
				return "~"
			_:
				return "?"

	func describe_class() -> String:
		match change_class:
			DotquayWire.CHANGE_CLASS_CONTENT:
				return "real content"
			DotquayWire.CHANGE_CLASS_UID_ONLY:
				return "uid only"
			DotquayWire.CHANGE_CLASS_LINE_ENDINGS_ONLY:
				return "line endings only"
			DotquayWire.CHANGE_CLASS_EDITOR_ARTIFACT:
				return "editor artifact"
			_:
				return change_class

	func tooltip() -> String:
		match change_class:
			DotquayWire.CHANGE_CLASS_CONTENT:
				return ("A real difference from the locked commit. A forced update overwrites the whole "
					+ "folder, so this would be gone.")
			DotquayWire.CHANGE_CLASS_UID_ONLY:
				return ("Only a uid:// value differs. Godot rewrites these itself from the path beside "
					+ "them, so a reinstall regenerates it.")
			DotquayWire.CHANGE_CLASS_LINE_ENDINGS_ONLY:
				return "Only the line endings differ (CRLF vs LF). The text is identical."
			DotquayWire.CHANGE_CLASS_EDITOR_ARTIFACT:
				return ("Editor-generated (.uid / .import / .godot). Not part of the addon's content, and "
					+ "not why this addon reads as modified.")
			_:
				return ("dotquay classified this as '%s', which this version of the plugin does not "
					% change_class + "recognise. Treated as a real change.")


class Counts extends RefCounted:
	var content: int = 0
	var uid_only: int = 0
	var line_endings_only: int = 0
	var editor_artifact: int = 0
	var total: int = 0

	static func from_payload(payload: Dictionary) -> Counts:
		var counts := Counts.new()
		counts.content = int(payload.get("content", 0))
		counts.uid_only = int(payload.get("uid_only", 0))
		counts.line_endings_only = int(payload.get("line_endings_only", 0))
		counts.editor_artifact = int(payload.get("editor_artifact", 0))
		counts.total = int(payload.get("total", 0))
		return counts

	## Names the benign changes, so "safe" is a conclusion the reader can check rather than one they
	## have to take on trust.
	func describe_benign() -> String:
		var parts := PackedStringArray()

		if uid_only > 0:
			parts.append(DotquayText.plural(uid_only, "%d uid-only rewrite", "%d uid-only rewrites"))
		if line_endings_only > 0:
			parts.append(DotquayText.plural(line_endings_only, "%d line-ending change", "%d line-ending changes"))
		if editor_artifact > 0:
			parts.append(DotquayText.plural(editor_artifact, "%d editor artifact", "%d editor artifacts"))

		return "No real content changes" if parts.is_empty() else ", ".join(parts)


class Explanation extends RefCounted:
	var verdict: String = ""
	## Never null after [method from_payload]. Left null as a *default* rather than constructed here
	## because an inner class naming a sibling inner class in a field initialiser is resolved while the
	## script is still being parsed — the same cycle [DotquayWire]'s own doc comment describes.
	var counts: Counts = null
	var changes: Array = []   ## Array[Change]
	var unavailable_reason: String = ""
	var baseline_includes_patch: bool = false
	## A patch was declared and could not be reconstructed, so [member changes] includes the patch's
	## own edits. Distinct from [member baseline_includes_patch] being false, which is also just what
	## an ordinary unpatched addon looks like.
	var patch_could_not_be_applied: bool = false

	static func from_payload(payload: Dictionary) -> Explanation:
		var explanation := Explanation.new()
		explanation.verdict = String(payload.get("verdict", ""))
		explanation.unavailable_reason = DotquayWire.str_or_empty(payload.get("unavailable_reason"))
		explanation.baseline_includes_patch = bool(payload.get("baseline_includes_patch", false))
		explanation.patch_could_not_be_applied = bool(payload.get("patch_could_not_be_applied", false))

		# Always constructed, even from an absent or malformed field: every caller reads
		# `counts.total`, and a null here would turn a shrugging CLI into a crashed dialog.
		var counts: Variant = payload.get("counts")
		explanation.counts = Counts.from_payload(counts if typeof(counts) == TYPE_DICTIONARY else {})

		for entry in DotquayWire.array_or_empty(payload.get("changes")):
			if typeof(entry) == TYPE_DICTIONARY:
				explanation.changes.append(Change.from_payload(entry))

		return explanation

	## The CLI caps the reported list while keeping the counts exact, so the two disagreeing is
	## truncation rather than a bug.
	func is_truncated() -> bool:
		return counts.total > changes.size()

	func hidden_change_count() -> int:
		return maxi(0, counts.total - changes.size())

	## Deliberately requires the verdict to be *exactly* "safe to force". An unrecognised verdict from
	## a newer CLI reads as unsafe, which is the only direction this check is allowed to fail in.
	func is_safe_to_force() -> bool:
		return verdict == DotquayWire.VERDICT_SAFE_TO_FORCE

	func is_unavailable() -> bool:
		return verdict == DotquayWire.VERDICT_UNAVAILABLE

	## The headline. Answers the question the dialog was opened to ask, in one sentence, before any
	## file list is read.
	func summarize(addon_name: String) -> String:
		if is_unavailable():
			return ("dotquay couldn't download the recorded version of '%s' to compare against. This "
				% addon_name + "usually means you're offline.")

		if counts.total == 0:
			return ("Nothing in addons/%s differs from its locked commit. The \"modified\" flag comes "
				% addon_name + "from an older way of checking; Force update will clear it without changing "
				+ "any files.")

		if is_safe_to_force():
			return "%s in addons/%s. These are all generated by the editor, so Force update won't lose anything." % [
				counts.describe_benign(), addon_name]

		var content_phrase := DotquayText.plural(counts.content, "%d file has", "%d files have")
		return "%s real edits in addons/%s. Force update would delete those edits." % [content_phrase, addon_name]


## One row of `status --json`.
class Entry extends RefCounted:
	var name: String = ""
	var status: String = ""
	## Null unless `--explain` ran and this addon was modified.
	var explanation: Explanation = null

	static func from_payload(payload: Dictionary) -> Entry:
		var entry := Entry.new()
		entry.name = String(payload.get("name", ""))
		entry.status = String(payload.get("status", ""))

		var explanation: Variant = payload.get("explanation")
		if typeof(explanation) == TYPE_DICTIONARY:
			entry.explanation = Explanation.from_payload(explanation)

		return entry


var addons: Array = []   ## Array[Entry]
var all_clean: bool = false


static func from_payload(payload: Dictionary) -> DotquayExplain:
	var status := DotquayExplain.new()
	status.all_clean = bool(payload.get("all_clean", false))

	for entry in DotquayWire.array_or_empty(payload.get("addons")):
		if typeof(entry) == TYPE_DICTIONARY:
			status.addons.append(Entry.from_payload(entry))

	return status


## The entry for [param addon_name], or null. `status` reports every addon, and the dialog was opened
## about one of them.
func find(addon_name: String) -> Entry:
	for entry in addons:
		if entry.name == addon_name:
			return entry
	return null


## The explanation for [param addon_name], or null when that addon is absent, is not modified, or
## was reported by a `status` run that had no `--explain`.
func explanation_for(addon_name: String) -> Explanation:
	var entry := find(addon_name)
	return null if entry == null else entry.explanation
