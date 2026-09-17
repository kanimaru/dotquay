extends RefCounted

## Covers [DotquayOperation] — the typed view over the generic install/update/remove/override/patch
## payload (C-24). Previously read ad hoc inside `dotquay_panel.gd`'s `_report_operation`, with no
## fixture test of its own, unlike graph/preview/explain.

const OPERATION := preload("res://addons/dotquay/core/dotquay_operation.gd")

var runner: Object


func _payload() -> Dictionary:
	return {
		"messages": ["'leaf' has no git tags — pinned to its current default-branch commit"],
		"changes": [
			{"name": "core", "action": "updated", "to": "v1.2.0"},
			{"name": "leaf", "action": "unchanged", "to": "v2.0.0"},
			{"name": "shared", "action": "installed", "to": "v3.0.0"},
		],
	}


func test_parses_the_envelope_payload() -> void:
	var operation: DotquayOperation = OPERATION.from_payload(_payload())

	runner.equal(operation.messages.size(), 1, "the message is kept")
	runner.equal(operation.changes.size(), 3, "every change is kept, unchanged included")


func test_meaningful_changes_excludes_unchanged_entries() -> void:
	var operation: DotquayOperation = OPERATION.from_payload(_payload())
	var meaningful: Array = operation.meaningful_changes()

	runner.equal(meaningful.size(), 2, "the unchanged entry is excluded")
	runner.equal(meaningful[0].name, "core", "the updated entry is kept, in order")
	runner.equal(meaningful[1].name, "shared", "the installed entry is kept, in order")


func test_did_nothing_is_false_when_there_are_messages_or_meaningful_changes() -> void:
	var operation: DotquayOperation = OPERATION.from_payload(_payload())

	runner.check(not operation.did_nothing(), "messages and real changes both count as doing something")


func test_did_nothing_is_true_for_an_empty_no_op_run() -> void:
	# P1-27: a command that changed nothing (every entry unchanged, no messages of its own) used to end
	# silently, reading as hung rather than finished.
	var operation: DotquayOperation = OPERATION.from_payload({
		"messages": [],
		"changes": [{"name": "leaf", "action": "unchanged", "to": "v2.0.0"}],
	})

	runner.check(operation.did_nothing(), "an all-unchanged, message-free run did nothing")


func test_null_optional_fields_parse_as_empty_not_a_crash() -> void:
	# SPEC.md §17: an absent optional array is emitted as JSON null, not an omitted key — this exact
	# payload used to raise "Unable to iterate on object of type 'Nil'" on the worker thread (P0-8).
	var operation: DotquayOperation = OPERATION.from_payload({"messages": null, "changes": null})

	runner.equal(operation.messages.size(), 0, "a null messages array should parse as empty, not raise")
	runner.equal(operation.changes.size(), 0, "a null changes array should parse as empty, not raise")
	runner.check(operation.did_nothing(), "a null-everywhere payload still reads as having done nothing")

	var change := DotquayOperation.Change.from_payload({"name": null, "action": null, "to": null})
	runner.equal(change.name, "", "a null change name parses as empty, not '<null>'")
	runner.equal(change.action, "", "a null change action parses as empty, not '<null>'")
	runner.equal(change.to, "", "a null change 'to' parses as empty, not '<null>'")


func test_missing_fields_do_not_break_the_parse() -> void:
	var operation: DotquayOperation = OPERATION.from_payload({})

	runner.equal(operation.messages.size(), 0, "missing messages parse as empty")
	runner.equal(operation.changes.size(), 0, "missing changes parse as empty")
	runner.check(operation.did_nothing(), "an entirely empty payload did nothing")
