@tool
class_name DotquayPackageGlyph
extends Control

## A small box glyph that marks a NuGet package node (DESIGN-v0.19).
##
## Drawn rather than shipped as an image. A texture would bring an `.import` file into `addons/` for
## every consumer's editor to regenerate, which is exactly the churn [code]GodotArtifacts[/code]
## exists to paper over. A few lines of `_draw` scale cleanly at any editor zoom and take their colour
## from [DotquayPalette] like everything else. It's a generic box, not the NuGet logo.

@export var color: Color = DotquayPalette.PACKAGE:
	set(value):
		color = value
		queue_redraw()

const LINE_WIDTH := 1.5


func _draw() -> void:
	# An isometric box: a top face over two side faces, outlined, inside a square that fits the
	# control whichever way it has been stretched.
	var side := minf(size.x, size.y)
	var origin := (size - Vector2(side, side)) * 0.5
	var top := origin + Vector2(side * 0.5, side * 0.06)
	var left := origin + Vector2(side * 0.06, side * 0.3)
	var right := origin + Vector2(side * 0.94, side * 0.3)
	var centre := origin + Vector2(side * 0.5, side * 0.54)
	var bottom := origin + Vector2(side * 0.5, side * 0.94)
	var bottom_left := origin + Vector2(side * 0.06, side * 0.7)
	var bottom_right := origin + Vector2(side * 0.94, side * 0.7)

	draw_colored_polygon(PackedVector2Array([top, right, centre, left]), Color(color, 0.35))
	draw_polyline(PackedVector2Array([top, right, bottom_right, bottom, bottom_left, left, top]), color, LINE_WIDTH, true)
	draw_polyline(PackedVector2Array([left, centre, right]), color, LINE_WIDTH, true)
	draw_line(centre, bottom, color, LINE_WIDTH, true)
