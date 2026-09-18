@tool
extends SceneTree

## A dependency-free headless test runner for the Dotquay plugin.
##
## Deliberately not GUT: this suite must run in CI next to `dotnet test` without adding a git
## submodule or an addon download to the build. Mostly parsing and layout — the classes with no
## scene tree, no editor, and no CLI in them — but not only that (C-19): [code]test_scenes.gd[/code]
## loads every hand-written `.tscn` (they fail at load time, not compile time),
## [code]test_dotquay_process_runner.gd[/code] spawns real short-lived OS processes to prove the
## wall-clock timeout actually cuts one off, and [code]test_cli_contract.gd[/code] runs the real
## published `dotquay` binary end to end when `DOTQUAY_TEST_CLI` is set. Anything that genuinely
## needs a live editor is still verified by hand against a real project instead (that verification
## habit predates this file and isn't automatable here).
##
## Run with `run_tests.ps1`, not this script directly — it does two things this header would
## otherwise have to repeat: an `--import` pass first (skipping it makes every `class_name` global
## below fail to resolve, since that cache only exists once Godot has scanned the project), then:
##   godot --headless --path src/Dotquay.Godot --script tests/test_runner.gd

const SUITES := [
	preload("res://tests/test_dotquay_result.gd"),
	# Wire-literal display translations (P1-21) — read by DotquayGraph's own describe()/display_label()
	# below, so it comes first.
	preload("res://tests/test_dotquay_wire.gd"),
	# Generic text helpers (pluralisation, BBCode escaping) the P2 wording pass introduced — no wire
	# or colour dependency of their own, so they come right after the two literal-translation suites.
	preload("res://tests/test_dotquay_text.gd"),
	preload("res://tests/test_dotquay_palette.gd"),
	preload("res://tests/test_dotquay_graph.gd"),
	# Both node kinds' menus and their disabled-item explanations (P1-19, DESIGN-v0.19 §5), as plain
	# data so they're testable without a GraphNode in a tree.
	preload("res://tests/test_dotquay_node_actions.gd"),
	preload("res://tests/test_dotquay_graph_filter.gd"),
	preload("res://tests/test_dependency_node.gd"),
	preload("res://tests/test_package_node.gd"),
	# The graph view's manual-position bookkeeping (P1-22) — driven against the real, detached scene
	# rather than pulled apart into static functions, since what's under test is render()'s own
	# stateful behaviour across two calls, not a pure mapping of plain data.
	preload("res://tests/test_dependency_graph_view.gd"),
	# The panel's own pure static helper (P1-30) — see the suite's own header for why this doesn't
	# instantiate the panel itself.
	preload("res://tests/test_dotquay_panel.gd"),
	preload("res://tests/test_dotquay_preview.gd"),
	preload("res://tests/test_dotquay_explain.gd"),
	# Typed views over the remaining raw payloads (C-24) — outdated/doctor/operation, matching the
	# graph/preview/explain pattern above rather than the ad-hoc dict reads `dotquay_panel.gd` used to do.
	preload("res://tests/test_dotquay_outdated.gd"),
	preload("res://tests/test_dotquay_doctor.gd"),
	preload("res://tests/test_dotquay_operation.gd"),
	preload("res://tests/test_dotquay_self_check.gd"),
	preload("res://tests/test_dotquay_add_request.gd"),
	preload("res://tests/test_dotquay_rescan.gd"),
	preload("res://tests/test_dotquay_log_buffer.gd"),
	preload("res://tests/test_dotquay_nodes.gd"),
	preload("res://tests/test_dotquay_refresh_tracker.gd"),
	# Runs real (short-lived) OS processes to prove the wall-clock timeout in P0-7 actually cuts off
	# a hung one rather than just being plumbing that looks right. Unlike test_cli_contract.gd below,
	# this doesn't need dotquay itself, so it always runs rather than skipping when unconfigured.
	preload("res://tests/test_dotquay_process_runner.gd"),
	# Pure banner-state decisions (P0-2) — no Control, no EditorSettings, no CLI needed.
	preload("res://tests/test_dotquay_setup_banner.gd"),
	# Breadcrumb building and path-mistake detection (P1-1, P1-2).
	preload("res://tests/test_dotquay_settings.gd"),
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
