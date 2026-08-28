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
