@tool
class_name DotquayAddDependencyDialog
extends ConfirmationDialog

## The "add a dependency" form — the GUI equivalent of `dotquay add <git-url | nuget-id>`.
##
## Two source kinds share one dialog because they share one command: dotquay routes a git URL and a
## NuGet package ID through the same `add`, and splitting them into two dialogs would make a user
## choose a menu item before they've decided what they're adding.

signal add_requested(source: String, path_override: String)

enum SourceKind { GIT, NUGET }

var _kind: OptionButton
var _source: LineEdit
var _path_row: HBoxContainer
var _path: LineEdit
var _hint: Label


func _init() -> void:
	title = "Add a dependency"
	ok_button_text = "Add"
	min_size = Vector2i(560, 260)
	_build()


func _build() -> void:
	var root := VBoxContainer.new()
	root.add_theme_constant_override("separation", 10)
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	var kind_row := HBoxContainer.new()
	kind_row.add_theme_constant_override("separation", 8)
	kind_row.add_child(_label("Source"))

	_kind = OptionButton.new()
	_kind.add_item("Git repository (GDScript addon)", SourceKind.GIT)
	_kind.add_item("NuGet package (C#)", SourceKind.NUGET)
	_kind.item_selected.connect(_on_kind_selected)
	kind_row.add_child(_kind)
	root.add_child(kind_row)

	var source_row := HBoxContainer.new()
	source_row.add_theme_constant_override("separation", 8)
	source_row.add_child(_label("URL / ID"))

	_source = LineEdit.new()
	_source.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_source.text_changed.connect(_on_source_changed)
	source_row.add_child(_source)
	root.add_child(source_row)

	_path_row = HBoxContainer.new()
	_path_row.add_theme_constant_override("separation", 8)
	_path_row.add_child(_label("Subtree"))

	_path = LineEdit.new()
	_path.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_path.placeholder_text = "optional — defaults to addons/<repo name>; use \".\" if the addon is the repo root"
	_path_row.add_child(_path)
	root.add_child(_path_row)

	_hint = Label.new()
	_hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_hint.add_theme_font_size_override("font_size", 11)
	_hint.add_theme_color_override("font_color", DotquayDependencyNode.COLOR_MUTED)
	root.add_child(_hint)

	confirmed.connect(_on_confirmed)
	_on_kind_selected(SourceKind.GIT)


func open() -> void:
	_source.text = ""
	_path.text = ""
	_on_kind_selected(_kind.selected)
	popup_centered()
	_source.grab_focus()


func _on_kind_selected(index: int) -> void:
	var is_git := index == SourceKind.GIT

	# The subtree override is a fact about a git repo's layout (SPEC.md §7.1, OQ-2); NuGet has no
	# equivalent, so showing a disabled field would just invite the question.
	_path_row.visible = is_git

	_source.placeholder_text = (
		"https://github.com/nathanhoad/godot_dialogue_manager" if is_git else "Newtonsoft.Json")

	_hint.text = (
		"dotquay picks the latest release tag and pins it in dotquay.lock. A repository with no tags is pinned to its current default-branch commit instead."
		if is_git else
		"The latest stable release is written into your .csproj as a PackageReference and restored with dotnet.")


func _on_source_changed(text: String) -> void:
	# Pasting a git URL while "NuGet" is selected is a near-certain mis-click, and dotquay would
	# route it by shape anyway — switching the dropdown keeps the form honest about what will happen.
	if _looks_like_git_url(text) and _kind.selected != SourceKind.GIT:
		_kind.select(SourceKind.GIT)
		_on_kind_selected(SourceKind.GIT)


func _on_confirmed() -> void:
	var source := _source.text.strip_edges()
	if source.is_empty():
		return

	add_requested.emit(source, _path.text.strip_edges() if _path_row.visible else "")


## Mirrors AddCommand.LooksLikeGitUrl on the C# side: package IDs contain neither "://" nor an
## SSH-style "git@" prefix.
static func _looks_like_git_url(source: String) -> bool:
	return source.contains("://") or source.begins_with("git@")


func _label(text: String) -> Label:
	var label := Label.new()
	label.text = text
	label.custom_minimum_size = Vector2(70, 0)
	return label
