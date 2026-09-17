extends RefCounted

## Covers [DotquayNodes]'s shared scene-tree helpers.

const NODES := preload("res://addons/dotquay/core/dotquay_nodes.gd")

var runner: Object


## This suite runs via `godot --headless --script`, never inside a real running editor —
## [method Engine.is_editor_hint] is false here, the same condition [method
## DotquayNodes.scale_dialog_size] checks before touching [EditorInterface], which isn't fully bound
## outside a real editor process (P1-31). Asserting the no-op here is what makes that guard a test
## instead of something only ever exercised by hand in a real editor.
func test_scale_dialog_size_is_a_no_op_outside_a_running_editor() -> void:
	var window := Window.new()
	window.size = Vector2i(620, 340)

	NODES.scale_dialog_size(window)

	runner.equal(window.size, Vector2i(620, 340),
		"outside a real editor, scale_dialog_size must leave the window's authored size alone")

	window.free()
