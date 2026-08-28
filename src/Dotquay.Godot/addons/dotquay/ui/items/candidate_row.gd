@tool
class_name DotquayCandidateRow
extends HBoxContainer

## One version the conflict picker offers, with the reason it is or isn't the right answer.

const CUSTOM_INDEX := -1

@onready var radio: CheckBox = %Radio
@onready var _detail: Label = %Detail


## [param index] is the candidate's position in the conflict's ranked list, stashed on the radio so
## the dialog can recover the choice from whichever button ended up pressed — the button group hands
## back a [CheckBox], not a row.
func setup(candidate: DotquayGraph.Candidate, index: int, group: ButtonGroup) -> void:
	radio.button_group = group
	radio.text = candidate.display_label()
	radio.set_meta("candidate_index", index)

	_detail.text = candidate.describe_satisfies()
	_detail.tooltip_text = (candidate.resolved if not candidate.resolved.is_empty()
		else "This candidate does not resolve to a commit.")
	_detail.add_theme_color_override(
		"font_color", DotquayPalette.OK if candidate.satisfies_all else DotquayPalette.MUTED)
