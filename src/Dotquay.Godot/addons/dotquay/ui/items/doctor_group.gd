@tool
class_name DotquayDoctorGroup
extends VBoxContainer

## One doctor issue kind: a heading you can open, the shared explanation, and what it applies to.
##
## Doctor's findings repeat. Thirty-eight orphaned folders are thirty-eight copies of one long
## sentence, and reading them as a flat list means reading the same explanation thirty-eight times to
## find the one folder name that matters. So the sentence is said once, the heading carries the count
## and the CLI's own `kind` to scan for, and the subjects are a plain list underneath.
##
## Collapsed by default for anything long ([constant DotquayDoctorDialog.EXPAND_UP_TO]), because the
## heading alone is usually the whole answer: "38 folders dotquay doesn't track" is a decision you can
## make without reading 38 names.

## Bound on first use rather than with `@onready`, matching every other item scene in this plugin: a
## scene-unique name resolves through the owner the moment the scene is instantiated, while `@onready`
## waits for the tree, and the dialog fills its list before that in the test suite.
var _header: Button
var _body: MarginContainer
var _detail: Label
var _subjects: Label

var _group: DotquayDoctor.Group


## Shown as the heading's open/closed marker. A toggled [Button] gives no affordance of its own that
## survives the editor's theme, and "is this openable" is the one thing the heading has to answer
## before anything else it says matters.
const MARKER_OPEN := "▾"
const MARKER_CLOSED := "▸"


func setup(group: DotquayDoctor.Group, start_expanded: bool) -> void:
	if _header == null:
		_header = %Header
		_body = %Body
		_detail = %Detail
		_subjects = %Subjects
		_header.toggled.connect(_on_toggled)

	_group = group

	_detail.text = group.detail()
	_detail.visible = not group.detail().is_empty()

	# Names when the CLI gave us names to show, whole sentences when it did not — an unrecognised
	# kind, or a message that opens some other way, still reads correctly, just less compactly.
	var lines := group.subjects() if group.has_subjects() else group.messages()
	_subjects.text = "\n".join(lines)

	_header.button_pressed = start_expanded
	_apply_expanded(start_expanded)


func _apply_expanded(expanded: bool) -> void:
	_body.visible = expanded
	_header.text = "%s  %s" % [MARKER_OPEN if expanded else MARKER_CLOSED, _heading()]


## The kind is kept in front of the wording rather than replaced by it. It is the CLI's own stable
## name for the check, so it is what a search, an issue report, or `dotquay doctor` at a terminal all
## agree on — and reading it first is faster than reading a sentence when you already know what it
## means.
func _heading() -> String:
	if _group == null:
		return ""

	if _group.kind.is_empty():
		return _group.headline()

	return "[%s]  %s" % [_group.kind, _group.headline()]


func _on_toggled(pressed: bool) -> void:
	_apply_expanded(pressed)
