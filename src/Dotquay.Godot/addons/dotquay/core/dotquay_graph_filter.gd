@tool
class_name DotquayGraphFilter
extends RefCounted

## Which kinds of node the dependency graph shows (DESIGN-v0.19 §1).
##
## Packages replaced the NuGet sidebar tab, so a project with many packages and few addons would
## otherwise have its addons buried under a column of mostly unconnected nodes. The filter gives back
## what the tab's flat list was good for without bringing back a second display of the same data.

enum Mode { ALL, ADDONS, PACKAGES }

## In toolbar order. The [OptionButton]'s item ids are these values, so reordering the list can't
## change what a selection means.
const MODES := [Mode.ALL, Mode.ADDONS, Mode.PACKAGES]


static func shows_addons(mode: Mode) -> bool:
	return mode != Mode.PACKAGES


static func shows_packages(mode: Mode) -> bool:
	return mode != Mode.ADDONS


static func label(mode: Mode) -> String:
	match mode:
		Mode.ADDONS:
			return "Addons"
		Mode.PACKAGES:
			return "NuGet"
		_:
			return "All"


static func tooltip(mode: Mode) -> String:
	match mode:
		Mode.ADDONS:
			return "Show only git addons."
		Mode.PACKAGES:
			return "Show only NuGet packages, as a flat list."
		_:
			return "Show addons and the NuGet packages they declare, with an edge from each addon to its packages."


## What the empty-canvas hint says when [param graph] has nothing to draw under [param mode]. Empty
## means "don't show the hint".
static func empty_hint(graph: DotquayGraph, mode: Mode) -> String:
	if graph == null or graph.is_empty():
		return "No dependencies yet — use Add."

	var has_addons := not graph.addons.is_empty()
	var has_packages := not graph.dotnet.is_empty()

	if mode == Mode.ADDONS and not has_addons:
		return "No git addons. Switch the filter to All to see the NuGet packages."
	if mode == Mode.PACKAGES and not has_packages:
		return "No NuGet packages."
	return ""
