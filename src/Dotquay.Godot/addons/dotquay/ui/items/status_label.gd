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


## The width a wrapping label declares as its floor.
##
## It cannot be zero. An autowrapping [Label] reports its minimum *width* as its longest word, and its
## minimum *height* as the text wrapped to that width — so a paragraph with no declared width claims
## to need about eighty pixels and several hundred lines. Since [AcceptDialog] grows to its contents'
## minimum, that is a dialog 1600px tall.
##
## It also cannot be large: whatever is declared here is a width no dialog containing one of these can
## be resized below. Small enough to shrink, wide enough that the height it implies is a sentence
## rather than a column.
const WRAP_MIN_WIDTH := 240.0

## [param wrap] turns the label into a paragraph. It still expands to fill, so it uses whatever width
## the container actually has — [constant WRAP_MIN_WIDTH] only bounds the minimum.
func setup(text_value: String, color: Color, tooltip: String = "", wrap: bool = false) -> DotquayStatusLabel:
	text = text_value
	tooltip_text = tooltip
	add_theme_color_override("font_color", color)

	if wrap:
		autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		size_flags_horizontal = Control.SIZE_EXPAND_FILL
		custom_minimum_size = Vector2(WRAP_MIN_WIDTH, 0)

	return self
