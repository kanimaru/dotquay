@tool
class_name DotquayPreviewAddonRow
extends HBoxContainer

## One addon the install preview would bring in, and why.
##
## The row's whole job is to answer "where did this come from?" before the folder appears rather than
## after — so the origin is a column, not a tooltip.

@onready var _marker: Label = %Marker
@onready var _name: Label = %AddonName
@onready var _version: Label = %Version
@onready var _origin: Label = %Origin
@onready var _folder: Label = %Folder


func setup(addon: DotquayPreview.Addon) -> void:
	_marker.text = addon.marker()
	_name.text = addon.name
	_version.text = addon.display_version()
	_version.tooltip_text = addon.resolved
	_origin.text = addon.describe_origin()

	# Shown only when install_as sends the addon somewhere other than addons/<name>, because that
	# difference is exactly when someone goes looking for a folder and can't find it.
	_folder.visible = addon.folder != addon.name
	_folder.text = "→ addons/%s" % addon.folder

	var color := _color_for(addon)
	for label in [_marker, _name, _version]:
		label.add_theme_color_override("font_color", color)

	_origin.add_theme_color_override(
		"font_color", DotquayPalette.MUTED if addon.is_requested() else DotquayPalette.WARN)
	_folder.add_theme_color_override("font_color", DotquayPalette.OVERRIDE)

	tooltip_text = _describe(addon)


## Green for something arriving, amber for something already installed that would move, muted for a
## row that is only there for context. "This add moves an addon you didn't mention" is the outcome
## people most need warning about, so it gets the colour that means "read this".
static func _color_for(addon: DotquayPreview.Addon) -> Color:
	if not addon.is_new:
		return DotquayPalette.MUTED
	return DotquayPalette.WARN if addon.is_move() else DotquayPalette.OK


static func _describe(addon: DotquayPreview.Addon) -> String:
	if not addon.is_new:
		return "Already installed at this commit — this add would leave it alone."
	if addon.is_move():
		return "Already installed at %s, and this add would move it to %s." % [
			addon.previous_resolved.substr(0, 7), addon.short_sha]
	return "%s\nWould be installed into addons/%s at %s." % [addon.git, addon.folder, addon.short_sha]
