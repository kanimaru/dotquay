@tool
class_name DotquayLogBuffer
extends RefCounted

## A bounded list of log entries, oldest dropped first.
##
## The panel's log used to be the [RichTextLabel] itself: append until `get_line_count()` crossed a
## threshold, then wipe. That had two faults. `get_line_count()` counts *wrapped* visual lines, so on a
## narrow panel the wipe fired after a few dozen messages; and the wipe ran after the append, so the
## very line that tripped it — usually the error the user was about to read — vanished with everything
## else. Keeping the entries here, in message units, and re-rendering the label from them makes both
## impossible, and makes the trimming rule testable without a scene tree.

## Emitted whenever the rendered text changed, so a label can rebuild itself without polling.
signal changed

const DEFAULT_CAPACITY := 200

## Shown as the first line once anything has been dropped, so a reader knows the top isn't the start.
const TRIMMED_MARKER := "(earlier output trimmed)"

var _capacity: int
var _entries: PackedStringArray = PackedStringArray()
var _dropped := 0


func _init(capacity: int = DEFAULT_CAPACITY) -> void:
	_capacity = max(1, capacity)


## Appends one entry (already BBCode, one message — a newline is added on render, not stored).
func append(bbcode: String) -> void:
	_entries.append(bbcode)

	while _entries.size() > _capacity:
		_entries.remove_at(0)
		_dropped += 1

	changed.emit()


func clear() -> void:
	_entries.clear()
	_dropped = 0
	changed.emit()


func size() -> int:
	return _entries.size()


func dropped() -> int:
	return _dropped


func entries() -> PackedStringArray:
	return _entries.duplicate()


## The whole log as one BBCode string, newest last, prefixed with the trim marker once anything has
## been dropped. [param marker_bbcode] lets the caller colour the marker without this class knowing
## about palettes.
func render(marker_bbcode: String = TRIMMED_MARKER) -> String:
	var lines := PackedStringArray()

	if _dropped > 0:
		lines.append(marker_bbcode)

	lines.append_array(_entries)
	return "\n".join(lines) + ("\n" if not lines.is_empty() else "")
