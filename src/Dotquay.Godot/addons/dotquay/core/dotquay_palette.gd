@tool
class_name DotquayPalette
extends RefCounted

## The panel's status colours, in one place.
##
## Deliberately not the editor theme's accent colours: this panel's whole job is to make "something is
## wrong here" visible at a glance across a graph, and a theme-derived tint is too subtle to carry that
## at node size.
##
## Lives in [code]core/[/code] rather than on a UI script because several scenes need the same five
## colours, and a palette owned by one of them would make every other scene depend on that one for a
## reason that has nothing to do with what it draws.

const OK := Color(0.36, 0.72, 0.44)
const WARN := Color(0.92, 0.71, 0.24)
const ERROR := Color(0.88, 0.32, 0.32)
const MUTED := Color(0.58, 0.60, 0.65)
const OVERRIDE := Color(0.47, 0.62, 0.94)

## The same colours as BBCode, for the log's [RichTextLabel]. Derived rather than retyped so a colour
## can only be changed in one place.
static func bbcode(color: Color, text: String) -> String:
	return "[color=#%s]%s[/color]" % [color.to_html(false), text]
