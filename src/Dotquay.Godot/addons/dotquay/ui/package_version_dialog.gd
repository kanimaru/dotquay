@tool
class_name DotquayPackageVersionDialog
extends ConfirmationDialog

## Picks an exact version for one NuGet package: a pin for a package an addon brought in, or a change
## for one the project declares (DESIGN-v0.19 §7a.3).
##
## Like every dialog here, it never runs anything itself. It emits the choice, and the panel turns it
## into `dotquay add <id> --version <v>` through [DotquayAddRequest], the same place the Add form's
## arguments are built.

signal version_chosen(package_id: String, version: String)

## Bound on first use rather than with `@onready`, for the reason [method DotquayExplainDialog._bind]
## gives: a detached instance, as the scene tests use, never reaches `_ready`.
var _explanation: Label
var _current: Label
var _version: LineEdit
var _hint: Label

var _model: DotquayGraph.PackageNode


func _ready() -> void:
	_bind()


func _bind() -> void:
	if _explanation != null:
		return

	DotquayNodes.scale_dialog_size(self)

	_explanation = %Explanation
	_current = %Current
	_version = %Version
	_hint = %Hint

	_current.add_theme_color_override("font_color", DotquayPalette.MUTED)
	_hint.add_theme_color_override("font_color", DotquayPalette.WARN)

	_version.text_changed.connect(func(_text: String) -> void: _update_ok_state())
	# Enter in the field confirms, as it would in any one-field form, but only when OK would.
	_version.text_submitted.connect(func(_text: String) -> void:
		if not get_ok_button().disabled:
			_on_confirmed()
			hide())

	if not confirmed.is_connected(_on_confirmed):
		confirmed.connect(_on_confirmed)


func open_for(model: DotquayGraph.PackageNode) -> void:
	_bind()
	_model = model

	title = DotquayPackageVersionChoice.title(model)
	_explanation.text = DotquayPackageVersionChoice.explanation(model)
	_current.text = "Locked now: %s" % model.display_version()
	_version.text = DotquayPackageVersionChoice.initial_version(model)
	get_ok_button().text = DotquayPackageVersionChoice.ok_label(model)
	_update_ok_state()

	if is_inside_tree():
		popup_centered()
		_version.grab_focus()
		_version.select_all()


func _update_ok_state() -> void:
	if _model == null:
		return

	var reason := DotquayPackageVersionChoice.blocking_reason(_model, _version.text)
	get_ok_button().disabled = not reason.is_empty()
	get_ok_button().tooltip_text = reason
	_hint.text = reason


func _on_confirmed() -> void:
	if _model == null or not DotquayPackageVersionChoice.blocking_reason(_model, _version.text).is_empty():
		return
	version_chosen.emit(_model.name, _version.text.strip_edges())
