@tool
class_name DotquayDoctorDialog
extends AcceptDialog

## What `dotquay doctor` found, grouped by kind.
##
## Doctor's output is a checklist, not a stream of events, and it is the one command whose result is
## routinely dozens of lines long. Sent to the panel log it did two bad things at once: it buried
## whatever the log already held, and it printed the same long explanation once per finding, so the
## names — the only part that differs — were the hardest thing on screen to pick out.
##
## Here each kind is one heading carrying its count and the CLI's own `kind`, with the explanation
## said once and the subjects listed under it. Nothing is summarised away: every issue the CLI sent is
## in exactly one group, and a kind this plugin has no wording for still gets its own heading and its
## messages verbatim.

## Groups no larger than this open expanded; anything longer starts collapsed.
##
## The heading is usually the whole answer ("38 folders dotquay doesn't track" is actionable without
## reading 38 names), while a group of two or three is shorter open than the click it would cost to
## look. Pitched at the size where scrolling starts to cost more than it tells you.
const EXPAND_UP_TO := 5

const GROUP_ROW := preload("res://addons/dotquay/ui/items/doctor_group.tscn")

## Bound on first use rather than with `@onready`, matching [DotquayExplainDialog]: it makes the
## dialog behave the same whichever order it is instantiated and added in, and lets the render be
## asserted without a SceneTree.
var _summary: Label
var _healthy: Label
var _group_list: VBoxContainer


func _ready() -> void:
	_bind()


func _bind() -> void:
	if _summary != null:
		return

	DotquayNodes.scale_dialog_size(self)

	_summary = %Summary
	_healthy = %Healthy
	_group_list = %GroupList


func show_report(doctor: DotquayDoctor) -> void:
	_bind()

	_summary.text = doctor.summarize()
	_summary.add_theme_color_override(
		"font_color", DotquayPalette.OK if doctor.healthy else DotquayPalette.WARN)

	# Shown rather than left blank: a dialog that opens empty reads as one that failed to render. This
	# says the check ran and found nothing, which is the actual result.
	_healthy.visible = doctor.healthy

	DotquayNodes.clear_children(_group_list)

	for group in doctor.groups():
		var row: DotquayDoctorGroup = GROUP_ROW.instantiate()
		_group_list.add_child(row)
		row.setup(group, group.count() <= EXPAND_UP_TO)

	# Rendering is the contract; popping up is what being in a tree adds. Guarded so the same call is
	# valid on a detached instance, which is how the render is asserted without a SceneTree.
	if is_inside_tree():
		popup_centered()
