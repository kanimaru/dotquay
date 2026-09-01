@tool
class_name DotquayConflictCard
extends VBoxContainer

## One conflict in the sidebar: what broke, who disagrees, and the two things you can do about it.

signal resolve_requested(addon_name: String)
signal focus_requested(addon_name: String)

const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

@onready var _headline: Label = %Headline
@onready var _requirements: VBoxContainer = %Requirements
@onready var _resolve_button: Button = %ResolveButton
@onready var _focus_button: Button = %FocusButton

var _addon_name := ""


func _ready() -> void:
	_resolve_button.pressed.connect(func() -> void: resolve_requested.emit(_addon_name))
	_focus_button.pressed.connect(func() -> void: focus_requested.emit(_addon_name))


func setup(conflict: DotquayGraph.Conflict) -> void:
	_addon_name = conflict.name

	_headline.text = conflict.headline()
	_headline.add_theme_color_override("font_color", DotquayPalette.ERROR)

	for child in _requirements.get_children():
		child.queue_free()

	for requirement in conflict.requirements:
		var line: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_requirements.add_child(line)
		line.setup("  • " + requirement.describe(), DotquayPalette.MUTED, requirement.git, true)
