@tool
class_name DotquayExplainDialog
extends ConfirmationDialog

## Why one addon reads as `modified`, and whether forcing an update over it would cost anything
## (docs/DESIGN-v0.12-MODIFIED-REASON.md).
##
## The panel's `modified` badge states a fact and offers no way to act on it, which leaves exactly one
## move: force the update and hope. This dialog turns that into a decision — it lists what actually
## differs from the locked commit, says which of it the Godot editor generated and would regenerate,
## and only then offers the button.
##
## It decides nothing itself. Confirming emits a request the panel turns into `dotquay update <name>
## --force`, which is the same command the same user would have typed. The dialog's contribution is
## that by then they know what it discards.

## Emitted when the user confirms a force update from here.
signal force_update_requested(addon_name: String)

## Emitted from the "Keep my edits (save as patch)" button (P1-32) — the same `dotquay patch <name>`
## a real content change's blocker used to only describe in prose, telling a GUI user to run it "at a
## terminal" instead of just doing it.
signal capture_patch_requested(addon_name: String)

const CHANGE_ROW := preload("res://addons/dotquay/ui/items/explain_change_row.tscn")

const ACTION_CAPTURE_PATCH := "capture_patch"

## Bound on first use rather than with `@onready`.
##
## `@onready` only fires once the node is in the tree, and a scene-unique name resolves through the
## instance's owner, which is set the moment it is instantiated. Binding lazily makes the dialog
## behave identically whichever order it gets — and, not incidentally, makes it assertable without a
## SceneTree, which is what lets the Force-update button's disabled state be a test rather than a
## code review. [DotquayDependencyNode] solves the same both-orders problem with a deferred render;
## this is the same idea with less state.
var _summary: Label
var _changes_heading: Label
var _change_list: VBoxContainer
var _truncated: Label
var _patch_note: Label
var _blocker: Label

## Not scene-owned like the labels above: `add_button()` is how [ConfirmationDialog] itself grows an
## extra action (its own OK/Cancel are not ordinary scene children either), so adding this one in code
## is consistent with the dialog's own built-in buttons rather than an exception to the
## scenes-not-code convention the rest of the panel follows.
var _patch_button: Button

var _addon_name := ""


func _ready() -> void:
	_bind()


func _bind() -> void:
	if _summary != null:
		return

	DotquayNodes.scale_dialog_size(self)

	_summary = %Summary
	_changes_heading = %ChangesHeading
	_change_list = %ChangeList
	_truncated = %Truncated
	_patch_note = %PatchNote
	_blocker = %Blocker

	if not confirmed.is_connected(_on_confirmed):
		confirmed.connect(_on_confirmed)

	if _patch_button == null:
		_patch_button = add_button("Keep my edits (save as patch)", true, ACTION_CAPTURE_PATCH)
		custom_action.connect(_on_custom_action)


func show_explanation(addon_name: String, explanation: DotquayExplain.Explanation) -> void:
	_bind()
	_addon_name = addon_name

	title = "'%s' is modified — why?" % addon_name
	_summary.text = explanation.summarize(addon_name)
	_summary.add_theme_color_override("font_color", _summary_color(explanation))

	_render_changes(explanation)
	_render_notes(explanation)
	_update_ok_state(explanation)

	# Rendering is the contract; popping up is what being in a tree adds. Guarded so the same call is
	# valid on a detached instance, which is how the render is asserted without a SceneTree.
	if is_inside_tree():
		popup_centered()


## Green only when the CLI actually said "safe to force". An unavailable comparison is amber, not
## green: "I could not check" and "I checked and it is fine" must never look alike here.
static func _summary_color(explanation: DotquayExplain.Explanation) -> Color:
	if explanation.is_unavailable():
		return DotquayPalette.WARN
	return DotquayPalette.OK if explanation.is_safe_to_force() else DotquayPalette.ERROR


func _render_changes(explanation: DotquayExplain.Explanation) -> void:
	DotquayNodes.clear_children(_change_list)

	# An unavailable comparison has no list, and an addon that differs in nothing has an empty one.
	# Hiding the heading is what keeps either from reading as "the list failed to render".
	var has_changes := not explanation.changes.is_empty()
	_changes_heading.visible = has_changes

	if not has_changes:
		return

	# The CLI already orders real content changes first, so a truncated list keeps the rows that
	# decide the verdict. Preserved rather than re-sorted here.
	for change in explanation.changes:
		var row: DotquayExplainChangeRow = CHANGE_ROW.instantiate()
		_change_list.add_child(row)
		row.setup(change)


func _render_notes(explanation: DotquayExplain.Explanation) -> void:
	_truncated.visible = explanation.is_truncated()
	_truncated.text = ("... and %d more, not listed. The counts above cover all of them."
		% explanation.hidden_change_count())

	# Said only when a patch was declared and did not make it into the comparison — never for an
	# ordinary unpatched addon, which is most of them.
	_patch_note.visible = explanation.patch_could_not_be_applied
	_patch_note.text = ("This addon's captured patch could not be reconstructed, so its own edits are "
		+ "listed above too and the comparison is incomplete.")


## The OK button is the enforcement point. Offering "Force update" next to a list of real edits the
## user would lose is how a guard becomes a formality — the same reason the CLI refuses rather than
## warning (SPEC.md requirement P0-6).
func _update_ok_state(explanation: DotquayExplain.Explanation) -> void:
	var safe := explanation.is_safe_to_force()
	var unavailable := explanation.is_unavailable()

	get_ok_button().disabled = not safe
	_blocker.visible = not safe

	# Only shown when there's something real to capture and dotquay actually knows it (P1-32): a safe
	# verdict has nothing worth patching, and an unavailable comparison hasn't established that
	# anything differs at all — offering the button there would be acting on a guess.
	_patch_button.visible = not safe and not unavailable

	if unavailable:
		_blocker.text = ("dotquay could not reach the locked commit, so it cannot say what would be lost:\n%s"
			% explanation.unavailable_reason
			+ "\n\nCheck your connection, then choose \"Why is this modified?\" again to retry.")
	else:
		_blocker.text = ("Keep my edits (save as patch) below captures these first — dotquay then "
			+ "re-applies them on every future update automatically.")


func _on_confirmed() -> void:
	if not _addon_name.is_empty():
		force_update_requested.emit(_addon_name)


func _on_custom_action(action: StringName) -> void:
	if String(action) == ACTION_CAPTURE_PATCH and not _addon_name.is_empty():
		capture_patch_requested.emit(_addon_name)
		hide()
