@tool
class_name DotquayConflictDialog
extends ConfirmationDialog

## The version picker: turns an unresolvable conflict into one click that writes an
## `[overrides.<name>]` entry (SPEC.md §8's escape hatch, §18).
##
## The dialog never edits `dotquay.toml` itself. It emits the choice, and the panel runs
## `dotquay override` — so the manifest has exactly one writer, and a choice made here behaves
## identically to the same choice typed at a terminal.

## [param kind] is "version"/"branch"/"rev"; [param value] is the tag, branch, or SHA to pin to.
## [param install_as] is the folder to force, empty for "leave the folder alone".
signal override_chosen(addon_name: String, kind: String, value: String, git: String, install_as: String)

const CUSTOM_ID := -1

## Item id in the CustomKind [OptionButton] to the wire literal `override` expects. The scene's item
## *captions* happen to be the same words today, but a caption is something the editor invites you to
## retitle; a flag is not. The ids are what the scene must keep stable.
const CUSTOM_KIND_BY_ID := {
	0: DotquayWire.SOURCE_VERSION,
	1: DotquayWire.SOURCE_BRANCH,
	2: DotquayWire.SOURCE_REV,
}

const CANDIDATE_ROW := preload("res://addons/dotquay/ui/items/candidate_row.tscn")
const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

@onready var _headline: Label = %Headline
@onready var _detail: RichTextLabel = %Detail
@onready var _requirement_list: VBoxContainer = %Requirements
@onready var _candidate_list: VBoxContainer = %Candidates
@onready var _custom_radio: CheckBox = %CustomRadio
@onready var _custom_kind: OptionButton = %CustomKind
@onready var _custom_value: LineEdit = %CustomValue

var _conflict: DotquayGraph.Conflict
var _candidate_group: ButtonGroup


func _ready() -> void:
	# Typing into the free-text field is itself a choice; making the user also click the radio is a
	# step that only ever produces a wrong-looking dialog.
	_custom_value.text_changed.connect(func(_text: String) -> void:
		_custom_radio.button_pressed = true
		_update_ok_state())
	_custom_radio.set_meta("candidate_index", CUSTOM_ID)

	for label in [%RequirementsHeading, %CandidatesHeading]:
		(label as Label).add_theme_color_override("font_color", DotquayPalette.MUTED)

	confirmed.connect(_on_confirmed)


func show_conflict(conflict: DotquayGraph.Conflict) -> void:
	_conflict = conflict

	# A fresh group per showing: reusing one would keep every radio from the previous conflict
	# registered, so `get_pressed_button` could hand back a candidate that is no longer on screen.
	_candidate_group = ButtonGroup.new()
	_candidate_group.pressed.connect(func(_button: BaseButton) -> void: _update_ok_state())
	_custom_radio.button_group = _candidate_group
	_custom_radio.button_pressed = false
	_custom_value.text = ""

	_headline.text = conflict.headline()
	_detail.text = conflict.message

	DotquayNodes.clear_children(_requirement_list)

	for requirement in conflict.requirements:
		var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_requirement_list.add_child(label)
		label.setup("  • " + requirement.describe(), DotquayPalette.MUTED, requirement.git)

	DotquayNodes.clear_children(_candidate_list)

	var ranked := conflict.ranked_candidates()

	if ranked.is_empty():
		var empty: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_candidate_list.add_child(empty)
		empty.setup("dotquay found no published version to offer. Enter one below.", DotquayPalette.MUTED)
	else:
		for index in range(ranked.size()):
			var row: DotquayCandidateRow = CANDIDATE_ROW.instantiate()
			_candidate_list.add_child(row)
			row.setup(ranked[index], index, _candidate_group)

		# Preselect the first candidate that satisfies everyone; if none does, leave the choice open
		# rather than defaulting to something that quietly breaks a requirer.
		if ranked[0].satisfies_all:
			(_candidate_list.get_child(0) as DotquayCandidateRow).radio.button_pressed = true

	_update_ok_state()
	popup_centered()


## Whether confirming right now would actually emit a choice: a candidate is selected, or the custom
## radio is and its field has text.
func _has_choice() -> bool:
	if _candidate_group == null:
		return false

	var pressed := _candidate_group.get_pressed_button()
	if pressed == null:
		return false

	if int(pressed.get_meta("candidate_index", CUSTOM_ID)) == CUSTOM_ID:
		return not _custom_value.text.strip_edges().is_empty()

	return true


## Disabled rather than ignored on confirm — the dialog has already closed by the time `confirmed`
## fires, so bailing out there reads as "the button did nothing".
func _update_ok_state() -> void:
	get_ok_button().disabled = not _has_choice()


func _custom_kind_wire() -> String:
	return String(CUSTOM_KIND_BY_ID.get(_custom_kind.get_selected_id(), DotquayWire.SOURCE_VERSION))


func _on_confirmed() -> void:
	if _conflict == null or not _has_choice():
		return

	var pressed := _candidate_group.get_pressed_button()
	var index: int = int(pressed.get_meta("candidate_index", CUSTOM_ID))

	if index == CUSTOM_ID:
		# The custom path has no candidate to inherit a repository from, so it relies on the CLI
		# filling `git` in from the manifest or lockfile — the common case, since a version conflict
		# is a disagreement about which release, not about which repo.
		override_chosen.emit(_conflict.name, _custom_kind_wire(), _custom_value.text.strip_edges(), "", "")
		return

	var ranked := _conflict.ranked_candidates()
	if index < 0 or index >= ranked.size():
		return

	var candidate: DotquayGraph.Candidate = ranked[index]
	override_chosen.emit(_conflict.name, candidate.kind, candidate.value, candidate.git, candidate.install_as)
