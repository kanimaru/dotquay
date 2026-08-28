@tool
extends EditorPlugin

## Registers Dotquay as a main-screen plugin, so it appears as a tab alongside 2D, 3D, Script, Game,
## and AssetLib (SPEC.md §18).
##
## A main screen rather than a bottom dock or a toolbar button: a dependency graph is a full-canvas,
## pan-and-zoom view of the whole project, which is exactly what the main screen area is for and
## exactly what a 200px-tall dock cannot show.

const PANEL_SCENE := preload("res://addons/dotquay/ui/dotquay_panel.tscn")
const SETTINGS := preload("res://addons/dotquay/core/dotquay_settings.gd")

var _panel: DotquayPanel
var _was_visible := false


func _enter_tree() -> void:
	SETTINGS.register(get_editor_interface().get_editor_settings())

	_panel = PANEL_SCENE.instantiate()
	_panel.name = "Dotquay"
	_panel.visible = false

	# Parented to the main screen container, not added as a dock: this is what makes Godot treat the
	# plugin as a main-screen tab and drive it through _make_visible.
	get_editor_interface().get_editor_main_screen().add_child(_panel)


func _exit_tree() -> void:
	if is_instance_valid(_panel):
		_panel.queue_free()
	_panel = null


func _has_main_screen() -> bool:
	return true


func _get_plugin_name() -> String:
	return "Dotquay"


func _get_plugin_icon() -> Texture2D:
	# Borrowing an editor icon rather than shipping one: it inherits the current theme automatically,
	# so the tab never looks out of place in light mode or at a non-default editor scale.
	return get_editor_interface().get_base_control().get_theme_icon("NodeInfo", "EditorIcons")


func _make_visible(visible: bool) -> void:
	if not is_instance_valid(_panel):
		return

	_panel.visible = visible

	# Refresh on the transition into view, not on every call: switching tabs back and forth is
	# common, and re-reading the lockfile each time is cheap but re-rendering the graph is not.
	if visible and not _was_visible and SETTINGS.refresh_on_focus(get_editor_interface().get_editor_settings()):
		_panel.refresh()

	_was_visible = visible
