@tool
class_name DotquayStatusLabel
extends Label

## A small coloured label with a tooltip — the panel's badge and its detail line, which are the same
## control wearing two sizes.
##
## One scene rather than two because the difference between "conflict" on a graph node and
## "required by another addon" underneath it is a colour and a wrap setting. Two scenes would be two
## files to keep in sync for a distinction the user never sees.


func _ready() -> void:
	# Tooltips need the node to receive mouse events, and a Label ignores them by default. Set here
	# rather than in the scene so a caller can't accidentally clear it and lose every tooltip.
	mouse_filter = Control.MOUSE_FILTER_STOP


## [param wrap_width] > 0 turns the label into a wrapping paragraph of that width; 0 keeps it on one
## line, which is what a badge needs.
func setup(text_value: String, color: Color, tooltip: String = "", wrap_width: float = 0.0) -> DotquayStatusLabel:
	text = text_value
	tooltip_text = tooltip
	add_theme_color_override("font_color", color)

	if wrap_width > 0.0:
		autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		custom_minimum_size = Vector2(wrap_width, 0)

	return self
