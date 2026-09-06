@tool
class_name DotquayExplainChangeRow
extends HBoxContainer

## One file that differs from the addon's locked commit, and whether losing it would matter.
##
## The row's whole job is to make the one real edit findable among forty harmless rewrites — so the
## classification is a coloured column, not a tooltip, and a benign row is deliberately dimmer than
## the text above it.

## Bound on first use rather than with `@onready`, matching [DotquayExplainDialog]: a scene-unique
## name resolves through the owner the instant the scene is instantiated, while `@onready` waits for
## the tree. The dialog fills its list before that happens in the test suite, and would in any caller
## that builds rows before adding the container.
var _marker: Label
var _kind: Label
var _path: Label
var _class: Label


func setup(change: DotquayExplain.Change) -> void:
	if _marker == null:
		_marker = %Marker
		_kind = %ChangeKind
		_path = %ChangePath
		_class = %ChangeClass

	_marker.text = change.marker()
	_kind.text = change.kind
	_path.text = change.path
	_class.text = "[%s]" % change.describe_class()

	var color := DotquayPalette.MUTED if change.is_benign() else DotquayPalette.ERROR

	for label in [_marker, _path, _class]:
		label.add_theme_color_override("font_color", color)

	_kind.add_theme_color_override("font_color", DotquayPalette.MUTED)

	tooltip_text = change.tooltip()
