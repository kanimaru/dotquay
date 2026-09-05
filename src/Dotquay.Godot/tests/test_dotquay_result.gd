extends RefCounted

## Covers [DotquayResult] — the envelope parser (SPEC.md §17).
##
## Every case here is a real way the CLI boundary goes wrong in practice: a wrong path pointed at
## some other program, a shell profile that prints a banner before the JSON, a plugin newer than the
## CLI. Each has to produce a usable message rather than a stack trace, because the person seeing it
## has a misconfiguration, not a bug.

const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")

var runner: Object


func _envelope(overrides: Dictionary = {}) -> String:
	var envelope := {
		"schema": 1,
		"tool": "dotquay",
		"version": "0.5.0",
		"command": "graph",
		"ok": true,
		"data": {"source": "lockfile", "addons": []},
		"error": null,
		"warnings": [],
	}
	envelope.merge(overrides, true)
	return JSON.stringify(envelope)


func test_parses_a_success_envelope() -> void:
	var result: DotquayResult = RESULT.parse(_envelope(), 0, "graph")

	runner.check(result.ok, "a well-formed success envelope should parse as ok")
	runner.equal(result.command, "graph", "command should come from the envelope")
	runner.equal(result.cli_version, "0.5.0", "cli version should be carried through")
	runner.equal(result.data.get("source"), "lockfile", "data payload should be preserved")


func test_parses_a_failure_envelope() -> void:
	var json := _envelope({
		"ok": false,
		"data": null,
		"error": {"kind": "no_manifest", "message": "No dotquay.toml found."},
	})

	var result: DotquayResult = RESULT.parse(json, 1, "graph")

	runner.check(not result.ok, "a failure envelope should not parse as ok")
	runner.equal(result.error_kind, RESULT.KIND_NO_MANIFEST, "error kind should be carried through verbatim")
	runner.equal(result.exit_code, 1, "the process exit code should be recorded")


func test_carries_warnings() -> void:
	var result: DotquayResult = RESULT.parse(_envelope({"warnings": ["no tags found"]}), 0, "add")

	runner.equal(result.warnings.size(), 1, "warnings should survive parsing")
	runner.equal(result.warnings[0], "no tags found", "warning text should be preserved")


func test_recovers_from_leading_noise() -> void:
	# A shell profile or a wrapper script printing a line before the JSON is common enough that
	# failing on it would strand users on a problem that has nothing to do with dotquay.
	var result: DotquayResult = RESULT.parse("Setting up environment...\n" + _envelope(), 0, "graph")

	runner.check(result.ok, "leading non-JSON noise should be skipped, not fatal")


func test_recovers_from_trailing_noise() -> void:
	# The runner folds stderr into the same buffer as stdout, so a .NET runtime or git warning can land
	# *after* the envelope. That used to fail the parse outright.
	var result: DotquayResult = RESULT.parse(
		_envelope() + "\nwarning: redirecting to https://github.com/x/y.git/\n", 0, "graph")

	runner.check(result.ok, "trailing non-JSON noise should be skipped, not fatal")
	runner.equal(result.data.get("source"), "lockfile", "the payload should survive the trim intact")


func test_recovers_from_noise_on_both_sides() -> void:
	var result: DotquayResult = RESULT.parse("banner\n" + _envelope() + "\ntrailer", 0, "graph")

	runner.check(result.ok, "noise before and after the envelope should both be skipped")


func test_extract_json_object_leaves_braceless_input_alone() -> void:
	runner.equal(RESULT.extract_json_object("no json here"), "no json here",
		"a buffer with no object should be returned untouched so the caller can show the real bytes")
	runner.equal(RESULT.extract_json_object("} {"), "} {",
		"a closing brace before the opening one is not an object and must not be cut into one")


func test_ticket_defaults_to_zero_outside_the_runner() -> void:
	var result: DotquayResult = RESULT.parse(_envelope(), 0, "graph")
	runner.equal(result.ticket, 0, "a result parsed outside DotquayCli has no ticket")


func test_rejects_non_json_output() -> void:
	var result: DotquayResult = RESULT.parse("bash: dotquay: command not found", 127, "graph")

	runner.check(not result.ok, "non-JSON output must not be treated as success")
	runner.equal(result.error_kind, RESULT.KIND_BAD_OUTPUT, "non-JSON output should be reported as bad output")


func test_rejects_json_from_another_tool() -> void:
	# The single most likely misconfiguration is a CLI path pointing at a different executable that
	# happens to speak JSON. Saying so beats an empty graph with no explanation.
	var result: DotquayResult = RESULT.parse('{"tool":"npm","ok":true}', 0, "graph")

	runner.check(not result.ok, "another tool's JSON must not be accepted")
	runner.check(result.error_message.contains("not dotquay"), "the message should name the actual problem")


func test_rejects_empty_output() -> void:
	var result: DotquayResult = RESULT.parse("", 0, "graph")

	runner.check(not result.ok, "empty output must not parse as success")
	runner.equal(result.error_kind, RESULT.KIND_BAD_OUTPUT, "empty output should be reported as bad output")


func test_rejects_an_unknown_schema() -> void:
	var result: DotquayResult = RESULT.parse(_envelope({"schema": 99}), 0, "graph")

	runner.check(not result.ok, "an unknown payload schema must not be half-parsed")
	runner.equal(result.error_kind, RESULT.KIND_SCHEMA_MISMATCH, "a schema mismatch should be its own error kind")
	runner.check(result.error_message.contains("99"), "the message should name the schema it saw")


func test_failure_helper_needs_no_output() -> void:
	var result: DotquayResult = RESULT.failure(RESULT.KIND_NOT_CONFIGURED, "no path", "graph")

	runner.check(not result.ok, "a synthesised failure is still a failure")
	runner.equal(result.command, "graph", "the command should be recorded even when the CLI never ran")
