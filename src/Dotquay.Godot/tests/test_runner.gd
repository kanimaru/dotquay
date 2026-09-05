@tool
extends SceneTree

## A dependency-free headless test runner for the Dotquay plugin's pure logic.
##
## Deliberately not GUT: this suite must run in CI next to `dotnet test` without adding a git
## submodule or an addon download to the build, and it only ever tests parsing and layout — the
## classes with no scene tree, no editor, and no CLI in them. Anything that genuinely needs the
## editor is verified by hand against a real project (see docs/SPRINT-01.md's verification habit).
##
## Run with:
##   godot --headless --path src/Dotquay.Godot --script tests/test_runner.gd

const SUITES := [
	preload("res://tests/test_dotquay_result.gd"),
	preload("res://tests/test_dotquay_graph.gd"),
	preload("res://tests/test_dotquay_preview.gd"),
	preload("res://tests/test_dotquay_add_request.gd"),
	preload("res://tests/test_dotquay_rescan.gd"),
	preload("res://tests/test_dotquay_log_buffer.gd"),
	preload("res://tests/test_dotquay_refresh_tracker.gd"),
	# Hand-written .tscn files fail at load time, not compile time, so the only way to know they are
	# valid is to load them.
	preload("res://tests/test_scenes.gd"),
	# Runs the real binary. Skips itself when DOTQUAY_TEST_CLI isn't set, so a checkout with nothing
	# built still goes green — but CI sets it, because a fixture-only suite cannot catch the CLI
	# changing its own wire format.
	preload("res://tests/test_cli_contract.gd"),
]

var _passed := 0
var _failed := 0
var _current := ""


func _initialize() -> void:
	for suite_script in SUITES:
		var suite: Object = suite_script.new()
		suite.set("runner", self)

		for method in suite.get_method_list():
			var method_name: String = method["name"]
			if not method_name.begins_with("test_"):
				continue

			_current = "%s.%s" % [suite_script.resource_path.get_file(), method_name]
			suite.call(method_name)

	print("")
	if _failed == 0:
		print("PASSED: %d assertion(s), 0 failures." % _passed)
	else:
		printerr("FAILED: %d failure(s), %d passing assertion(s)." % [_failed, _passed])

	quit(0 if _failed == 0 else 1)


func check(condition: bool, message: String) -> void:
	if condition:
		_passed += 1
		return

	_failed += 1
	printerr("  FAIL  %s — %s" % [_current, message])


func equal(actual: Variant, expected: Variant, message: String) -> void:
	check(actual == expected, "%s (expected %s, got %s)" % [message, expected, actual])
