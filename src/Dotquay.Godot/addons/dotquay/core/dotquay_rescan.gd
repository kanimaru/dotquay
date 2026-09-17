@tool
class_name DotquayRescan
extends RefCounted

## Asks the Godot editor to re-import whatever the CLI just wrote to `addons/`.
##
## dotquay writes through a child process, behind the editor's back. Godot's own file watcher only
## notices out-of-band writes when the editor window regains OS focus, so without this an install
## looks like it did nothing until you click into another window and back again — which is the whole
## bug this class removes.
##
## The [EditorFileSystem] is injected rather than reached for through [EditorInterface]. Two reasons,
## both practical: [EditorInterface] does not exist in the headless test runner, and the parts of this
## worth testing — when *not* to scan, and what to do about a scan already in flight — are pure state
## machine with no editor in them.

## Emitted the moment a scan is actually handed to the editor, which is not necessarily when it was
## requested (see [method pump]).
signal scan_started

## The injected editor filesystem. Anything answering `is_scanning() -> bool` and `scan() -> void`
## will do; the real one is `EditorInterface.get_resource_filesystem()`. Null is a valid value — the
## panel is a `@tool` script and can be instantiated outside a running editor, where the right
## behaviour is to do nothing rather than to crash.
var _filesystem: Object

var _pending := false


func _init(editor_filesystem: Object = null) -> void:
	_filesystem = editor_filesystem


## Whether a mutating command's payload describes something that actually reached disk.
##
## Every change reported as `unchanged` means the sync engine looked and left the folder alone, so a
## payload made entirely of those is a command that wrote nothing — rescanning after it would walk
## the whole project for no reason.
##
## An unrecognised payload shape scans. Not scanning after a write is exactly the bug being fixed,
## and a needless scan costs a second; the asymmetry decides the unknown case.
static func payload_touched_disk(payload: Dictionary) -> bool:
	var changes: Variant = payload.get("changes")

	if not (changes is Array):
		return true

	for change in changes:
		if typeof(change) != TYPE_DICTIONARY:
			return true
		if String(change.get("action", "")) != DotquayWire.ACTION_UNCHANGED:
			return true

	return false


## Queues a scan and starts it if the editor is free. Safe to call repeatedly: several commands
## finishing in the same frame collapse into one scan.
func request() -> void:
	_pending = true
	pump()


## Starts the queued scan when the editor is no longer busy. Returns [code]true[/code] while a scan
## is still owed, so a caller can drive this from `_process` and stop polling the moment it isn't.
##
## Deferring around an in-flight scan matters for correctness, not just politeness: a scan that
## started before the CLI finished writing may already have walked past `addons/`, so its completion
## is no evidence the new files were seen. Waiting it out and scanning again is the only ordering
## that cannot miss them.
func pump() -> bool:
	if not _pending:
		return false

	if _filesystem == null:
		_pending = false
		return false

	if _filesystem.is_scanning():
		return true

	_pending = false
	_filesystem.scan()
	scan_started.emit()
	return false


func is_pending() -> bool:
	return _pending
