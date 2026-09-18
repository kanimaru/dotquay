@tool
class_name DotquayDoctorGroup
extends PanelContainer

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
var _copy_fixes: Button

var _group: DotquayDoctor.Group

## Restored on _copy_fixes after the "Copied!" acknowledgement, so a second click starts from the
## same label rather than accumulating "Copied!" from the first.
const COPY_LABEL := "Copy fix list"
const COPIED_LABEL := "Copied!"


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
		_copy_fixes = %CopyFixes
		_header.toggled.connect(_on_toggled)
		_copy_fixes.pressed.connect(_on_copy_fixes_pressed)

	_group = group

	_detail.text = group.detail()
	_detail.visible = not group.detail().is_empty()

	# A resolved fix per issue when the CLI sent one, names when it only sent those, whole sentences
	# otherwise — an unrecognised kind, or a message that opens some other way, still reads correctly,
	# just less compactly.
	var lines: PackedStringArray
	if group.has_uid_fixes():
		lines = group.fix_lines()
	elif group.has_subjects():
		lines = group.subjects()
	else:
		lines = group.messages()
	_subjects.text = "\n".join(lines)

	# dotquay never writes a project's own scripts (SPEC.md §18) -- copying the fix to the clipboard
	# is the button this dialog can honestly offer instead of applying it for you.
	_copy_fixes.visible = group.has_uid_fixes()
	_copy_fixes.text = COPY_LABEL

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


## Puts every issue's ile | uid -> res_path line on the system clipboard, one per line, so the
## fix can be pasted straight into an editor's search-and-replace, an issue tracker, or a message to
## whoever owns the script -- without dotquay itself ever opening that script to edit it.
func _on_copy_fixes_pressed() -> void:
	if _group == null:
		return

	DisplayServer.clipboard_set(_group.copy_text())

	_copy_fixes.text = COPIED_LABEL
	if is_inside_tree():
		await get_tree().create_timer(1.5).timeout
		_copy_fixes.text = COPY_LABEL
