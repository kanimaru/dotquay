extends RefCounted

## Covers [DotquayRescan] — the decision to re-import after a command, and the queue that survives a
## scan the editor started on its own.
##
## Worth its own suite because both halves fail silently in opposite directions: too eager and every
## no-op button press walks the whole project, too shy and the user is back to alt-tabbing to make an
## install show up. Neither shows as an error anywhere.

const RESCAN := preload("res://addons/dotquay/core/dotquay_rescan.gd")

var runner: Object


## Stands in for [EditorFileSystem], which does not exist headlessly. Only the two methods
## [DotquayRescan] calls, so a third one appearing there is a compile-time surprise rather than a
## silent no-op.
class FakeEditorFilesystem extends RefCounted:
	var scanning := false
	var scans := 0

	func is_scanning() -> bool:
		return scanning

	func scan() -> void:
		scans += 1


func _change(action: String) -> Dictionary:
	return {"name": "core", "kind": "addon", "action": action}


# -- deciding whether anything reached disk --------------------------------------------------

func test_a_real_change_scans() -> void:
	for action in [DotquayWire.ACTION_ADDED, DotquayWire.ACTION_UPDATED,
			DotquayWire.ACTION_REMOVED, DotquayWire.ACTION_INSTALLED]:
		runner.check(
			RESCAN.payload_touched_disk({"changes": [_change(action)]}),
			"'%s' is a write the editor has not seen" % action)


func test_an_all_unchanged_payload_does_not_scan() -> void:
	# `update` reports every addon it looked at, so this is the common case for a project that is
	# already up to date — the one where a full project scan would be pure cost.
	var payload := {"changes": [_change(DotquayWire.ACTION_UNCHANGED), _change(DotquayWire.ACTION_UNCHANGED)]}
	runner.check(not RESCAN.payload_touched_disk(payload), "nothing moved, so nothing to re-import")


func test_one_real_change_among_unchanged_ones_scans() -> void:
	var payload := {"changes": [
		_change(DotquayWire.ACTION_UNCHANGED),
		_change(DotquayWire.ACTION_UPDATED),
		_change(DotquayWire.ACTION_UNCHANGED)]}
	runner.check(RESCAN.payload_touched_disk(payload), "a single write is enough")


func test_an_empty_change_list_does_not_scan() -> void:
	runner.check(not RESCAN.payload_touched_disk({"changes": [], "messages": ["Already up to date."]}),
		"a command that reports no changes wrote nothing")


func test_a_payload_with_no_changes_key_scans() -> void:
	# An older or newer CLI, or a command whose payload is only messages. Scanning needlessly costs a
	# second; not scanning after a write is the bug this class exists to fix.
	runner.check(RESCAN.payload_touched_disk({"messages": ["Created dotquay.toml."]}),
		"an unrecognised payload gets the benefit of the doubt")


func test_a_malformed_change_scans() -> void:
	runner.check(RESCAN.payload_touched_disk({"changes": ["not a dictionary"]}),
		"a change that cannot be read is not evidence of nothing happening")
	runner.check(RESCAN.payload_touched_disk({"changes": [{}]}),
		"a change with no action is not evidence of nothing happening")


# -- the queue -------------------------------------------------------------------------------

func test_an_idle_editor_scans_immediately() -> void:
	var fake := FakeEditorFilesystem.new()
	var rescan: DotquayRescan = RESCAN.new(fake)

	rescan.request()

	runner.equal(fake.scans, 1, "the scan starts within the request")
	runner.check(not rescan.is_pending(), "and nothing is left owed")


func test_a_scan_already_running_is_waited_out() -> void:
	# The running scan may have walked past addons/ before the CLI finished writing, so its
	# completion is no evidence the new files were seen. Only a scan started afterwards is.
	var fake := FakeEditorFilesystem.new()
	fake.scanning = true
	var rescan: DotquayRescan = RESCAN.new(fake)

	rescan.request()
	runner.equal(fake.scans, 0, "nothing is started while the editor is busy")
	runner.check(rescan.pump(), "and the caller is told to keep polling")

	fake.scanning = false
	runner.check(not rescan.pump(), "the poll that starts the scan is the last one needed")
	runner.equal(fake.scans, 1, "exactly one scan runs once the editor frees up")


func test_repeated_requests_while_busy_collapse_into_one_scan() -> void:
	var fake := FakeEditorFilesystem.new()
	fake.scanning = true
	var rescan: DotquayRescan = RESCAN.new(fake)

	rescan.request()
	rescan.request()
	rescan.request()

	fake.scanning = false
	rescan.pump()

	runner.equal(fake.scans, 1, "three commands finishing together are still one scan")


func test_pumping_with_nothing_queued_does_nothing() -> void:
	var fake := FakeEditorFilesystem.new()
	var rescan: DotquayRescan = RESCAN.new(fake)

	runner.check(not rescan.pump(), "an unrequested pump asks for no more polling")
	runner.equal(fake.scans, 0, "and starts no scan")


func test_no_editor_filesystem_is_a_no_op_not_a_crash() -> void:
	# The panel is a @tool script and can be constructed outside a running editor.
	var rescan: DotquayRescan = RESCAN.new(null)

	rescan.request()
	runner.check(not rescan.is_pending(), "the request is dropped rather than queued forever")
	runner.check(not rescan.pump(), "and no polling is asked for")


func test_scan_started_fires_when_the_scan_actually_begins() -> void:
	# Deferred scans are the case a listener cares about: "requested" and "started" are the same
	# moment only when the editor happened to be idle.
	var fake := FakeEditorFilesystem.new()
	fake.scanning = true
	var rescan: DotquayRescan = RESCAN.new(fake)

	var started := [0]
	rescan.scan_started.connect(func() -> void: started[0] += 1)

	rescan.request()
	runner.equal(started[0], 0, "no signal while the scan is only queued")

	fake.scanning = false
	rescan.pump()
	runner.equal(started[0], 1, "one signal, when the editor took the scan")
