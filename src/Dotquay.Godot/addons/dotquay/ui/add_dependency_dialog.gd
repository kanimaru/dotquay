@tool
class_name DotquayAddDependencyDialog
extends ConfirmationDialog

## The "add a dependency" form — the GUI equivalent of `dotquay add <git-url | nuget-id>`.
##
## Two source kinds share one dialog because they share one command: dotquay routes a git URL and a
## NuGet package ID through the same `add`, and splitting them into two dialogs would make a user
## choose a menu item before they've decided what they're adding.

## The form is complete. For a git source the panel previews it first; a NuGet package has no graph
## to preview, so it goes straight to `add`.
signal add_requested(source: String, path_override: String, is_git: bool)

enum SourceKind { GIT, NUGET }

@onready var _kind: OptionButton = %Kind
@onready var _source: LineEdit = %Source
@onready var _path_row: HBoxContainer = %PathRow
@onready var _path: LineEdit = %Path
@onready var _hint: Label = %Hint


func _ready() -> void:
	_kind.item_selected.connect(_on_kind_selected)
	_source.text_changed.connect(_on_source_changed)
	confirmed.connect(_on_confirmed)

	_hint.add_theme_color_override("font_color", DotquayPalette.MUTED)
	_on_kind_selected(SourceKind.GIT)


func open() -> void:
	_source.text = ""
	_path.text = ""
	_kind.select(SourceKind.GIT)
	_on_kind_selected(SourceKind.GIT)
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
		"dotquay resolves the graph and shows you what it would install before anything is written."
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

	var is_git := _kind.selected == SourceKind.GIT
	add_requested.emit(source, _path.text.strip_edges() if is_git else "", is_git)


## Mirrors AddCommand.LooksLikeGitUrl on the C# side: package IDs contain neither "://" nor an
## SSH-style "git@" prefix.
static func _looks_like_git_url(source: String) -> bool:
	return source.contains("://") or source.begins_with("git@")
