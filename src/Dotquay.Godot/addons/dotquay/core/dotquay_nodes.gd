@tool
class_name DotquayNodes
extends RefCounted

## Scene-tree helpers shared by every list-rendering script in the plugin.


## Removes and frees every child of [param container].
##
## Both steps, always: `queue_free()` alone leaves the child in the tree until the end of the frame,
## so a render that runs twice in one frame lays out stale rows beside new ones, and a lookup by name
## (the graph view's `connect_node`) can still find a node that is about to vanish. Every list-
## rendering script in the plugin clears its container through here rather than writing this loop
## itself, so the `remove_child` step can't quietly go missing from just one of them.
static func clear_children(container: Node) -> void:
	for child in container.get_children():
		container.remove_child(child)
		child.queue_free()


## Scales [param window]'s authored [member Window.size] (and [member Window.min_size], if set) by the
## editor's current UI scale, once.
##
## Every dialog this plugin ships is authored at 100% scale with `wrap_controls = false` (P1-31): the
## editor already multiplies every control's fonts and theme metrics by [method
## EditorInterface.get_editor_scale] at 150-200%, but a fixed-size [Window] does not grow to match, so
## the last form row or a blocker message ends up clipped behind the OK/Cancel buttons. Multiplying
## the window's own authored size by the same factor is what keeps the two in step, rather than
## guessing a second one independently. Callers are expected to call this at most once per instance —
## from a one-shot bind, the same guard every dialog already uses for its `%Name` lookups — so this
## does not re-guard idempotency itself.
static func scale_dialog_size(window: Window) -> void:
	# EditorInterface is only really populated inside a running editor. The headless test runner
	# executes this same @tool code as a plain script instead (`godot --headless --script ...`), where
	# [method Engine.is_editor_hint] is false and [method EditorInterface.get_editor_scale] isn't bound
	# at all ("Nonexistent function"). Skipping there is correct, not just convenient — a scale
	# multiplier only means something when a real editor UI is actually being scaled.
	if not Engine.is_editor_hint():
		return

	var scale := EditorInterface.get_editor_scale()
	window.size = Vector2i(Vector2(window.size) * scale)
	if window.min_size != Vector2i.ZERO:
		window.min_size = Vector2i(Vector2(window.min_size) * scale)
