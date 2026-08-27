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
signal override_chosen(addon_name: String, kind: String, value: String, git: String)

const CUSTOM_ID := -1

var _conflict: DotquayGraph.Conflict
var _headline: Label
var _detail: RichTextLabel
var _requirement_list: VBoxContainer
var _candidate_group: ButtonGroup
var _candidate_list: VBoxContainer
var _custom_kind: OptionButton
var _custom_value: LineEdit


func _init() -> void:
	title = "Resolve dependency conflict"
	ok_button_text = "Apply override"
	min_size = Vector2i(620, 520)
	_build()


func _build() -> void:
	var root := VBoxContainer.new()
	root.add_theme_constant_override("separation", 10)
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	_headline = Label.new()
	_headline.add_theme_font_size_override("font_size", 15)
	_headline.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	root.add_child(_headline)

	_detail = RichTextLabel.new()
	_detail.fit_content = true
	_detail.custom_minimum_size = Vector2(0, 60)
	_detail.selection_enabled = true  # the CLI's message is worth being able to copy into an issue
	root.add_child(_detail)

	root.add_child(_section_label("Who needs it"))
	_requirement_list = VBoxContainer.new()
	root.add_child(_requirement_list)

	root.add_child(_section_label("Pick a version to force"))

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.custom_minimum_size = Vector2(0, 200)
	root.add_child(scroll)

	_candidate_list = VBoxContainer.new()
	_candidate_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(_candidate_list)

	root.add_child(_custom_row())

	confirmed.connect(_on_confirmed)


func _custom_row() -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 6)

	var radio := CheckBox.new()
	radio.text = "Something else:"
	radio.tooltip_text = "For a version, branch, or commit that isn't offered above — including one that isn't tagged."
	row.add_child(radio)

	_custom_kind = OptionButton.new()
	_custom_kind.add_item("version", 0)
	_custom_kind.add_item("branch", 1)
	_custom_kind.add_item("rev", 2)
	row.add_child(_custom_kind)

	_custom_value = LineEdit.new()
	_custom_value.placeholder_text = "~1.4, main, or a commit SHA"
	_custom_value.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	# Typing into the free-text field is itself a choice; making the user also click the radio is a
	# step that only ever produces a wrong-looking dialog.
	_custom_value.text_changed.connect(func(_text: String) -> void: radio.button_pressed = true)
	row.add_child(_custom_value)

	# Registered last so the button group exists before candidates are added in show_conflict.
	radio.set_meta("candidate_index", CUSTOM_ID)
	row.set_meta("radio", radio)
	set_meta("custom_radio", radio)

	return row


func show_conflict(conflict: DotquayGraph.Conflict) -> void:
	_conflict = conflict
	_candidate_group = ButtonGroup.new()

	_headline.text = conflict.headline()
	_detail.text = conflict.message

	for child in _requirement_list.get_children():
		child.queue_free()

	for requirement in conflict.requirements:
		var label := Label.new()
		label.text = "  • " + requirement.describe()
		label.tooltip_text = requirement.git
		label.mouse_filter = Control.MOUSE_FILTER_STOP
		_requirement_list.add_child(label)

	for child in _candidate_list.get_children():
		child.queue_free()

	var ranked := conflict.ranked_candidates()

	if ranked.is_empty():
		var empty := Label.new()
		empty.text = "dotquay found no published version to offer. Enter one below."
		_candidate_list.add_child(empty)
	else:
		for index in range(ranked.size()):
			_candidate_list.add_child(_candidate_row(ranked[index], index))

	var custom_radio: CheckBox = get_meta("custom_radio")
	custom_radio.button_group = _candidate_group

	# Preselect the first candidate that satisfies everyone; if none does, leave the choice open
	# rather than defaulting to something that quietly breaks a requirer.
	if not ranked.is_empty() and ranked[0].satisfies_all:
		var first := _candidate_list.get_child(0)
		if first.has_meta("radio"):
			(first.get_meta("radio") as CheckBox).button_pressed = true

	popup_centered()


func _candidate_row(candidate: DotquayGraph.Candidate, index: int) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 8)

	var radio := CheckBox.new()
	radio.button_group = _candidate_group
	radio.text = "%s %s" % [candidate.kind, candidate.value]
	radio.set_meta("candidate_index", index)
	row.add_child(radio)

	var detail := Label.new()
	detail.text = candidate.describe_satisfies()
	detail.add_theme_color_override(
		"font_color",
		DotquayDependencyNode.COLOR_OK if candidate.satisfies_all else DotquayDependencyNode.COLOR_MUTED)
	detail.add_theme_font_size_override("font_size", 11)
	detail.tooltip_text = candidate.resolved if not candidate.resolved.is_empty() else "This candidate does not resolve to a commit."
	detail.mouse_filter = Control.MOUSE_FILTER_STOP
	row.add_child(detail)

	row.set_meta("radio", radio)
	return row


func _on_confirmed() -> void:
	if _conflict == null:
		return

	var pressed := _candidate_group.get_pressed_button() if _candidate_group != null else null
	if pressed == null:
		return

	var index: int = int(pressed.get_meta("candidate_index", CUSTOM_ID))

	if index == CUSTOM_ID:
		var value := _custom_value.text.strip_edges()
		if value.is_empty():
			return

		# The custom path has no candidate to inherit a repository from, so it relies on the CLI
		# filling `git` in from the manifest or lockfile — the common case, since a version conflict
		# is a disagreement about which release, not about which repo.
		override_chosen.emit(_conflict.name, _custom_kind.get_item_text(_custom_kind.selected), value, "")
		return

	var ranked := _conflict.ranked_candidates()
	if index < 0 or index >= ranked.size():
		return

	var candidate: DotquayGraph.Candidate = ranked[index]
	override_chosen.emit(_conflict.name, candidate.kind, candidate.value, candidate.git)


func _section_label(text: String) -> Label:
	var label := Label.new()
	label.text = text
	label.add_theme_font_size_override("font_size", 12)
	label.add_theme_color_override("font_color", DotquayDependencyNode.COLOR_MUTED)
	return label
