extends RefCounted

## Loads every scene the plugin ships and checks that each one contains the nodes its own script
## reaches for.
##
## Both halves catch a failure mode that is invisible until an editor user clicks the tab. A
## hand-written `.tscn` fails at *load* time rather than compile time, so a mistyped node type is a
## runtime surprise; and a `%UniqueName` that no node in the scene declares compiles fine, then
## dereferences null the first time `_ready` runs.
##
## The expected names are read out of the script's own source rather than listed here. A list would
## be a second copy of the truth, and the copy would go stale the first time someone adds a node —
## which is exactly the case this suite exists to catch.

var runner: Object

const SCENES := [
	"res://addons/dotquay/ui/dotquay_panel.tscn",
	"res://addons/dotquay/ui/dependency_graph_view.tscn",
	"res://addons/dotquay/ui/dependency_node.tscn",
	"res://addons/dotquay/ui/add_dependency_dialog.tscn",
	"res://addons/dotquay/ui/conflict_dialog.tscn",
	"res://addons/dotquay/ui/install_preview_dialog.tscn",
	"res://addons/dotquay/ui/items/status_label.tscn",
	"res://addons/dotquay/ui/items/conflict_card.tscn",
	"res://addons/dotquay/ui/items/candidate_row.tscn",
	"res://addons/dotquay/ui/items/dotnet_row.tscn",
	"res://addons/dotquay/ui/items/preview_addon_row.tscn",
	"res://addons/dotquay/ui/items/preview_source_card.tscn",
]

## Node names the panel reaches by plain path rather than by `%`, because they are looked up through
## a dictionary of handlers and so never appear as literals a scan could find.
const PANEL_TOOLBAR_BUTTONS := [
	"AddButton", "InstallButton", "UpdateAllButton", "OutdatedButton",
	"ResolveButton", "RefreshButton", "DoctorButton",
]


func test_every_scene_loads_and_instantiates() -> void:
	for path in SCENES:
		var scene: PackedScene = load(path)
		runner.check(scene != null, "%s failed to load" % path)

		if scene == null:
			continue

		runner.check(scene.can_instantiate(), "%s loaded but cannot be instantiated" % path)

		var instance: Node = scene.instantiate()
		runner.check(instance != null, "%s instantiated to null" % path)

		if instance != null:
			instance.free()


## Every `%Name` in a scene's script must resolve inside that scene.
func test_unique_names_referenced_by_scripts_exist() -> void:
	for path in SCENES:
		var scene: PackedScene = load(path)
		if scene == null:
			continue

		var instance: Node = scene.instantiate()
		var script: GDScript = instance.get_script()

		if script == null:
			instance.free()
			continue

		for unique_name in _unique_names_in(script.source_code):
			runner.check(
				instance.get_node_or_null("%" + unique_name) != null,
				"%s references %%%s, which no node in the scene declares" % [path, unique_name])

		instance.free()


func test_panel_toolbar_buttons_exist_under_the_names_the_panel_binds() -> void:
	var panel: Node = load("res://addons/dotquay/ui/dotquay_panel.tscn").instantiate()
	var toolbar: Node = panel.get_node_or_null("%Toolbar")

	runner.check(toolbar != null, "the panel scene has no %Toolbar")

	if toolbar != null:
		for button_name in PANEL_TOOLBAR_BUTTONS:
			var button: Node = toolbar.get_node_or_null(button_name)
			runner.check(button is Button, "the toolbar has no Button named %s" % button_name)

	panel.free()


## Scene roots that are always instanced into a container, and therefore live or die by their size
## flags rather than their anchors.
const CONTAINER_HOSTED_ROOTS := [
	# The editor's main screen is a VBoxContainer (EditorInterface.get_editor_main_screen()).
	"res://addons/dotquay/ui/dotquay_panel.tscn",
	# Instanced into the panel's VSplitContainer.
	"res://addons/dotquay/ui/dependency_graph_view.tscn",
]


## A container recomputes its children's anchors every layout pass, so `anchors_preset = 15` on a
## container-hosted root is decoration — the size *flags* are what decide whether it gets the whole
## area or only its minimum size. Missing EXPAND_FILL on the panel hands it a ~180px strip.
func test_container_hosted_roots_expand_to_fill() -> void:
	for path in CONTAINER_HOSTED_ROOTS:
		var instance: Control = load(path).instantiate()

		runner.check(
			(instance.size_flags_horizontal & Control.SIZE_EXPAND_FILL) == Control.SIZE_EXPAND_FILL,
			"%s is laid out by a container and must set size_flags_horizontal to EXPAND_FILL" % path)
		runner.check(
			(instance.size_flags_vertical & Control.SIZE_EXPAND_FILL) == Control.SIZE_EXPAND_FILL,
			"%s is laid out by a container and must set size_flags_vertical to EXPAND_FILL" % path)

		instance.free()


## GROW_DIRECTION_BOTH means a control whose minimum size exceeds its assigned rect expands in both
## directions — so an undersized panel grows *upward*, over the editor's own tab bars.
##
## Worth asserting rather than fixing once, because the editor writes it back: it is part of the
## "Full Rect" anchor preset, so merely opening and saving one of these scenes reintroduces it. The
## standing fix is to give a container-hosted root no anchors at all, and this test is what notices
## when that erodes.
func test_container_hosted_roots_do_not_grow_over_their_host() -> void:
	for path in CONTAINER_HOSTED_ROOTS:
		var instance: Control = load(path).instantiate()

		runner.check(
			instance.grow_vertical != Control.GROW_DIRECTION_BOTH,
			"%s must not grow vertically in both directions — overflow would cover the editor chrome above it" % path)

		instance.free()


const DIALOGS := [
	"res://addons/dotquay/ui/add_dependency_dialog.tscn",
	"res://addons/dotquay/ui/conflict_dialog.tscn",
	"res://addons/dotquay/ui/install_preview_dialog.tscn",
]


## `min_size` is a hard floor the user cannot drag past. Setting it equal to the dialog's default
## `size` — which reads as "open at this size" but means "and never get smaller" — makes every resize
## attempt snap straight back.
func test_dialogs_can_be_resized_smaller_than_they_open() -> void:
	for path in DIALOGS:
		var dialog: Window = load(path).instantiate()

		runner.check(
			dialog.min_size.x < dialog.size.x and dialog.min_size.y < dialog.size.y,
			"%s pins min_size (%s) to its opening size (%s), so it cannot be resized smaller"
				% [path, dialog.min_size, dialog.size])

		dialog.free()


## The floor a wrapping label must declare, and the ceiling on how much it may ask for.
##
## An autowrapping [Label] reports its minimum width as its longest word and its minimum height as the
## text wrapped to *that* width — so with no declared width it claims to need eighty pixels and
## several hundred lines. [AcceptDialog] grows to its contents' minimum, which turns that into a
## 1600px-tall dialog. Declaring too much is the opposite failure: the number becomes a width no
## dialog containing the label can be resized below.
const WRAP_MIN := 1.0
const WRAP_MAX := 320.0


## Both failure modes are the same assertion from opposite sides, so they are one test: a wrapping
## label must name a width, and it must be a modest one.
func test_wrapping_labels_declare_a_modest_width() -> void:
	for path in SCENES:
		var instance: Node = load(path).instantiate()
		_check_wrapping_labels(instance, instance, path)
		instance.free()


func _check_wrapping_labels(node: Node, root: Node, path: String) -> void:
	if node is Label and node.autowrap_mode != TextServer.AUTOWRAP_OFF:
		var declared: float = node.custom_minimum_size.x

		runner.check(
			declared >= WRAP_MIN,
			"%s: %s wraps but declares no minimum width, so its minimum *height* becomes the text "
				% [path, root.get_path_to(node)]
				+ "wrapped to its longest word — hundreds of pixels the dialog then grows to fit.")

		runner.check(
			declared <= WRAP_MAX,
			"%s: %s declares custom_minimum_size.x = %d, which is a width the containing dialog can "
				% [path, root.get_path_to(node), int(declared)]
				+ "never be resized below. Keep it modest and let EXPAND_FILL do the widening.")

	for child in node.get_children():
		_check_wrapping_labels(child, root, path)


## `AcceptDialog` sets `wrap_controls = true`, which makes the window continuously resize itself to
## its contents' minimum — the "it jumps back the moment I let go" behaviour. These dialogs scroll
## their contents instead, so the size the user picks is the size that sticks.
func test_dialogs_do_not_resize_themselves_to_their_contents() -> void:
	for path in DIALOGS:
		var dialog: Window = load(path).instantiate()

		runner.check(
			not dialog.wrap_controls,
			"%s leaves wrap_controls on, so it will grow to whatever its contents claim to need" % path)

		dialog.free()


## Pulls `%Name` tokens out of GDScript source.
##
## Requires an uppercase first letter, which is what separates a unique node name from a format
## specifier: every `%s`, `%d`, and `%%` in this codebase is lowercase, and every node name is
## PascalCase. A looser pattern would report `"%d addon(s)"` as a missing node.
static func _unique_names_in(source: String) -> PackedStringArray:
	var found := PackedStringArray()

	var regex := RegEx.new()
	regex.compile("%([A-Z][A-Za-z0-9_]*)")

	for match_result in regex.search_all(source):
		var name := match_result.get_string(1)
		if not found.has(name):
			found.append(name)

	return found
