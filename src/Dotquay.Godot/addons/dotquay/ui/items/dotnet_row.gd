@tool
class_name DotquayDotnetRow
extends HBoxContainer

## One NuGet package in the sidebar.

signal remove_requested(package_name: String)

@onready var _label: Label = %PackageLabel
@onready var _remove_button: Button = %RemoveButton

var _package_name := ""


func _ready() -> void:
	_remove_button.pressed.connect(func() -> void: remove_requested.emit(_package_name))


func setup(entry: DotquayGraph.DotnetEntry) -> void:
	_package_name = entry.name
	_label.text = "%s  %s" % [entry.name, entry.display_version()]

	# A package an addon unioned in has no manifest entry to delete (SPEC.md P1-5), so offering
	# "Remove" would be offering an action that cannot succeed.
	_remove_button.visible = entry.declared_by_project

	if entry.declared_by_project:
		return

	_label.add_theme_color_override("font_color", DotquayPalette.MUTED)
	_label.tooltip_text = "Required by an addon, not declared in this project's manifest (SPEC.md P1-5)."
	_label.mouse_filter = Control.MOUSE_FILTER_STOP
