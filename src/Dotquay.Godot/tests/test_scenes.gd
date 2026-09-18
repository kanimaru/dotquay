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
	"res://addons/dotquay/ui/package_node.tscn",
	"res://addons/dotquay/ui/package_version_dialog.tscn",
	"res://addons/dotquay/ui/add_dependency_dialog.tscn",
	"res://addons/dotquay/ui/conflict_dialog.tscn",
	"res://addons/dotquay/ui/install_preview_dialog.tscn",
	"res://addons/dotquay/ui/explain_dialog.tscn",
	"res://addons/dotquay/ui/legend_dialog.tscn",
	"res://addons/dotquay/ui/doctor_dialog.tscn",
	"res://addons/dotquay/ui/items/status_label.tscn",
	"res://addons/dotquay/ui/items/conflict_card.tscn",
	"res://addons/dotquay/ui/items/candidate_row.tscn",
	"res://addons/dotquay/ui/items/preview_addon_row.tscn",
	"res://addons/dotquay/ui/items/preview_source_card.tscn",
	"res://addons/dotquay/ui/items/explain_change_row.tscn",
	"res://addons/dotquay/ui/items/doctor_group.tscn",
]

## Node names the panel reaches by plain path rather than by `%`, because they are looked up through
## a dictionary of handlers and so never appear as literals a scan could find.
const PANEL_TOOLBAR_BUTTONS := [
	"AddButton", "InstallButton", "UpdateAllButton", "OutdatedButton",
	"RefreshButton", "ToolsButton", "LegendButton",
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
	"res://addons/dotquay/ui/doctor_dialog.tscn",
	"res://addons/dotquay/ui/conflict_dialog.tscn",
	"res://addons/dotquay/ui/install_preview_dialog.tscn",
	"res://addons/dotquay/ui/explain_dialog.tscn",
	"res://addons/dotquay/ui/legend_dialog.tscn",
	"res://addons/dotquay/ui/package_version_dialog.tscn",
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


## The explain dialog, actually rendering something.
##
## Every other test here proves the scene loads and its `%Name`s resolve. This one proves the script
## and the scene agree once data arrives — the `@onready` bindings are only assigned on entering the
## tree, so a renamed node passes every static check and then nulls out on the first real use.
##
## The assertion that earns the setup cost is the OK button: it says "Force update", and it must be
## disabled for anything the CLI did not call safe. A disabled-state bug here destroys work.
func test_explain_dialog_renders_and_gates_its_force_button() -> void:
	var safe := _explanation_payload(DotquayWire.VERDICT_SAFE_TO_FORCE, "uid_only", 1)
	var unsafe := _explanation_payload(DotquayWire.VERDICT_REVIEW_NEEDED, "content", 1)
	var unavailable := DotquayExplain.Explanation.from_payload({"verdict": DotquayWire.VERDICT_UNAVAILABLE,
		"unavailable_reason": "offline"})

	# Detached on purpose. A node added during `SceneTree._initialize` never reaches `_ready`, so an
	# `@onready`-bound dialog would null out here — which is exactly why this one binds lazily.
	var dialog: Window = load("res://addons/dotquay/ui/explain_dialog.tscn").instantiate()

	dialog.show_explanation("gut", safe)
	runner.equal(dialog.get_node("%ChangeList").get_child_count(), 1, "one change should render one row")
	runner.check(not dialog.get_ok_button().disabled, "a safe_to_force verdict should enable Force update")
	runner.check(not dialog.get_node("%Blocker").visible, "and should show no blocker")
	runner.check(not dialog._patch_button.visible,
		"nothing risky to capture when force is already safe — no patch button needed (P1-32)")

	# Reused rather than re-instantiated: the dialog is a long-lived child of the panel, so "shown a
	# second time with different data" is the normal case and the one a stale row would survive into.
	dialog.show_explanation("gut", unsafe)
	runner.equal(dialog.get_node("%ChangeList").get_child_count(), 1, "reopening must not accumulate rows")
	runner.check(dialog.get_ok_button().disabled, "a real content change must disable Force update")
	runner.check(dialog.get_node("%Blocker").visible, "and must say why")
	runner.check(dialog._patch_button.visible,
		"a real content change offers 'Keep my edits (save as patch)' directly, not just prose (P1-32)")

	# The unavailable case gets neither: dotquay never established what would actually be lost, so
	# offering to capture a patch would be acting on a guess (P1-32).
	dialog.show_explanation("gut", unavailable)
	runner.check(dialog.get_ok_button().disabled, "an unchecked comparison must not enable Force update")
	runner.check(dialog.get_node("%Blocker").visible, "and must say why")
	runner.check(not dialog._patch_button.visible,
		"nothing is known to be worth capturing when the comparison itself never ran")
	runner.check(dialog.get_node("%Blocker").text.contains("retry"),
		"the unavailable case must name a next step, not just state the problem")

	dialog.free()


static func _explanation_payload(verdict: String, change_class: String, count: int) -> DotquayExplain.Explanation:
	return DotquayExplain.Explanation.from_payload({
		"verdict": verdict,
		"counts": {change_class: count, "total": count},
		"changes": [{"path": "a.gd", "kind": "changed", "class": change_class}],
	})


## The conflict dialog's pin/conflict-mode wiring (P1-18): title, headline, candidates heading, and
## OK button label all follow whether [DotquayGraph.Conflict] is a real one or a manual pin, and the
## "Who needs it" block must disappear when there is nothing in it to show.
func test_conflict_dialog_uses_pin_wording_and_hides_empty_requirements() -> void:
	var dialog: Window = load("res://addons/dotquay/ui/conflict_dialog.tscn").instantiate()

	var pin := DotquayGraph.Conflict.new()
	pin.name = "gut"
	pin.is_manual_pin = true
	pin.message = "No conflict was reported for 'gut'."

	dialog.show_conflict(pin)
	runner.equal(dialog.title, "Pin 'gut' to a version", "the window title uses pin wording")
	runner.equal(dialog.get_ok_button().text, "Pin", "the OK button says Pin, not Apply override")
	runner.check(not dialog.get_node("%RequirementsHeading").visible,
		"nothing was passed in requirements, so the 'Who needs it' block should hide")
	runner.check(not dialog.get_node("%Requirements").visible, "and its body along with it")

	var real := DotquayGraph.Conflict.from_payload({
		"name": "core", "kind": "no_common_version", "message": "no tag satisfies everyone",
		"requirements": [{"requirer": "a", "kind": "version", "constraint": "~1", "git": ""}],
		"candidates": [],
	})

	# Reused rather than re-instantiated, matching how the panel actually owns one long-lived dialog.
	dialog.show_conflict(real)
	runner.equal(dialog.title, "Resolve dependency conflict", "a real conflict keeps the generic title")
	runner.equal(dialog.get_ok_button().text, "Apply override", "and the CLI's own operation name")
	runner.check(dialog.get_node("%RequirementsHeading").visible,
		"a real conflict with requirements should show who needs it")
	runner.check(dialog.get_node("%Requirements").visible, "and its body")

	dialog.free()


## Enter in any of the add-dialog's four fields must confirm it like clicking Add (P1-13) — previously
## true of none of them, since `register_text_enter` was never called at all. `text_submitted` is a
## real Godot signal, so a missing registration shows up as no connection on it, without needing to
## drive an actual keypress.
func test_add_dependency_dialog_registers_enter_to_confirm_on_every_field() -> void:
	var dialog: ConfirmationDialog = load("res://addons/dotquay/ui/add_dependency_dialog.tscn").instantiate()
	dialog._bind()

	for field_name in ["_source", "_value", "_path", "_install_as"]:
		var field: LineEdit = dialog.get(field_name)
		runner.check(not field.get_signal_connection_list("text_submitted").is_empty(),
			"%s has no register_text_enter binding, so Enter does nothing there (P1-13)" % field_name)

	dialog.free()


## P1-13: pasting a git URL while "NuGet" is selected switches Kind to Git — already true before this
## pass. Editing that URL back down to something that no longer looks like git now switches Kind back
## too, but only when the dialog made the original guess; a Git the user picked from the dropdown
## themselves is never second-guessed by what they go on to type.
func test_add_dependency_dialog_switches_kind_back_only_when_it_auto_switched() -> void:
	var dialog: ConfirmationDialog = load("res://addons/dotquay/ui/add_dependency_dialog.tscn").instantiate()
	dialog.open()

	dialog._kind.select(DotquayAddDependencyDialog.SourceKind.NUGET)
	dialog._on_source_changed("https://github.com/nathanhoad/godot_dialogue_manager.git")
	runner.equal(dialog._kind.selected, DotquayAddDependencyDialog.SourceKind.GIT,
		"a git-looking URL should switch Kind to Git")

	dialog._on_source_changed("Newtonsoft.Json")
	runner.equal(dialog._kind.selected, DotquayAddDependencyDialog.SourceKind.NUGET,
		"editing the URL away from looking like git should switch Kind back, since the dialog made the original guess")

	# Now the opposite: a Git the user picked themselves is never overridden by what they type next.
	dialog._kind.select(DotquayAddDependencyDialog.SourceKind.GIT)
	dialog._on_kind_selected(0)
	dialog._on_source_changed("Newtonsoft.Json")
	runner.equal(dialog._kind.selected, DotquayAddDependencyDialog.SourceKind.GIT,
		"a Git the user picked manually from the dropdown must not be overridden by what they type next")

	dialog.free()


## P1-13: the NuGet hint used to say "latest stable" regardless of Pin mode, which is simply wrong
## once "A specific version…" is selected.
func test_add_dependency_dialog_nuget_hint_varies_with_pin_mode() -> void:
	var dialog: ConfirmationDialog = load("res://addons/dotquay/ui/add_dependency_dialog.tscn").instantiate()
	dialog.open()
	dialog._kind.select(DotquayAddDependencyDialog.SourceKind.NUGET)
	dialog._on_source_changed("Newtonsoft.Json")

	dialog._select_mode(DotquayAddRequest.Source.LATEST)
	dialog._refresh()
	var latest_hint: String = dialog._hint.text

	dialog._select_mode(DotquayAddRequest.Source.VERSION)
	dialog._refresh()
	var version_hint: String = dialog._hint.text

	runner.check(latest_hint != version_hint,
		"the NuGet hint must change with Pin mode instead of always saying 'latest stable' (P1-13)")
	runner.check(version_hint.contains("exact version"),
		"the Version hint should say what pinning a NuGet package to a version means")

	dialog.free()


## P1-12: a placeholder that reads like a real value ("13.0.3", "main") is indistinguishable from
## something pre-filled once OK is disabled — prefixing "e.g." is what tells them apart.
func test_add_dependency_dialog_value_placeholders_read_as_examples() -> void:
	var dialog: ConfirmationDialog = load("res://addons/dotquay/ui/add_dependency_dialog.tscn").instantiate()
	dialog.open()

	dialog._kind.select(DotquayAddDependencyDialog.SourceKind.NUGET)
	dialog._on_source_changed("Newtonsoft.Json")
	dialog._select_mode(DotquayAddRequest.Source.VERSION)
	dialog._refresh()
	runner.check(dialog._value.placeholder_text.begins_with("e.g. "),
		"a bare-looking NuGet version placeholder must read as an example (P1-12)")

	dialog._kind.select(DotquayAddDependencyDialog.SourceKind.GIT)
	dialog._on_source_changed("https://github.com/x/y.git")
	dialog._select_mode(DotquayAddRequest.Source.BRANCH)
	dialog._refresh()
	runner.check(dialog._value.placeholder_text.begins_with("e.g. "),
		"a bare branch placeholder like 'main' must read as an example too")

	dialog.free()


## P1-15: a preview that would change nothing on disk still offered an enabled Install, teaching a
## user that clicking through it does something when it does not.
func test_install_preview_dialog_disables_ok_for_a_no_op_preview() -> void:
	var dialog: ConfirmationDialog = load("res://addons/dotquay/ui/install_preview_dialog.tscn").instantiate()
	var preview := DotquayPreview.from_payload({"name": "gut", "is_no_op": true})
	var request := DotquayAddRequest.for_git("https://github.com/bitwes/Gut.git")

	dialog.show_preview(preview, request)

	runner.check(dialog.get_ok_button().disabled,
		"a no-op preview has nothing to add, so OK must stay disabled (P1-15)")
	runner.check(not dialog.get_ok_button().tooltip_text.is_empty(),
		"the disabled OK button should say why, the same way every other disabled OK in this plugin does")

	dialog.free()


## P1-16: a checkbox next to a URL and a mention of `trusted_hosts` explains nothing to someone who
## has never had to know what a NuGet feed is. The card must say so in plain language, not just in a
## tooltip nobody hovers.
func test_preview_source_card_explains_what_a_feed_is() -> void:
	var card: DotquayPreviewSourceCard = load("res://addons/dotquay/ui/items/preview_source_card.tscn").instantiate()
	var source := DotquayPreview.Source.from_payload({
		"name": "kemomi", "url": "https://gitlab.kemomi.de/api/v4/projects/1/packages/nuget/index.json",
		"declared_by_addon": "gut",
	})

	card.setup(source, true)

	var explained := false
	for child in card.get_node("%Detail").get_children():
		if (child as Label).text.contains("NuGet feed is a server"):
			explained = true

	runner.check(explained,
		"the card must explain what a NuGet feed is in plain language, not rely on trusted_hosts jargon (P1-16)")

	card.free()


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


## The doctor dialog, actually rendering something.
##
## The regression it guards is the one that made doctor unusable in the first place: 44 findings as 44
## log lines. One group per kind, the long explanation said once, and the count in the heading is the
## whole point — so it is asserted on rendered nodes rather than trusted from the data layer.
func test_doctor_dialog_groups_by_kind_and_collapses_long_groups() -> void:
	var issues: Array = []
	for folder in ["a", "b", "c", "d", "e", "f"]:
		issues.append({"kind": "orphaned_addon_folder",
			"message": "'addons/%s' is on disk but isn't tracked by any locked addon." % folder})
	issues.append({"kind": "plugin_cfg_version_drift", "message": "'vbeami': its plugin.cfg disagrees."})

	var doctor := DotquayDoctor.from_payload({"healthy": false, "issues": issues})

	# Detached on purpose: a node added during `SceneTree._initialize` never reaches `_ready`, so an
	# `@onready`-bound dialog would null out here, which is why this one binds lazily.
	var dialog: Window = load("res://addons/dotquay/ui/doctor_dialog.tscn").instantiate()

	dialog.show_report(doctor)

	var list: Node = dialog.get_node("%GroupList")
	runner.equal(list.get_child_count(), 2, "seven issues of two kinds should render two groups, not seven rows")
	runner.check(dialog.get_node("%Summary").text.contains("7 issues"), "the header counts every issue")
	runner.check(not dialog.get_node("%Healthy").visible, "a project with findings is not healthy")

	var big: Node = list.get_child(0)
	var small: Node = list.get_child(1)
	runner.check(big.get_node("%Header").text.contains("6 folders"),
		"the big group's heading carries its count instead of repeating its message six times")
	runner.check(not big.get_node("%Body").visible,
		"a group larger than EXPAND_UP_TO starts collapsed, so the headings stay scannable")
	runner.check(small.get_node("%Body").visible, "a short group is shorter open than the click to open it")

	# Reused rather than re-instantiated: the dialog is a long-lived child of the panel, so "shown a
	# second time with different data" is the normal case and the one a stale row would survive into.
	dialog.show_report(DotquayDoctor.from_payload({"healthy": true, "issues": []}))
	runner.equal(list.get_child_count(), 0, "reopening on a healthy project must not leave the old groups behind")
	runner.check(dialog.get_node("%Healthy").visible, "and must say the check ran and found nothing")

	dialog.free()


## Opening a collapsed group has to actually reveal it — the toggle is the only way to reach the
## names, so a disconnected signal would hide them permanently.
func test_doctor_group_toggles_open() -> void:
	var doctor := DotquayDoctor.from_payload({"healthy": false, "issues": [
		{"kind": "orphaned_addon_folder", "message": "'addons/a' is on disk."},
	]})
	var row: Node = load("res://addons/dotquay/ui/items/doctor_group.tscn").instantiate()

	row.setup(doctor.groups()[0], false)
	runner.check(not row.get_node("%Body").visible, "starts collapsed when asked to")

	row.get_node("%Header").button_pressed = true
	runner.check(row.get_node("%Body").visible, "pressing the heading opens it")
	runner.check(row.get_node("%Subjects").text.contains("addons/a"), "and the subject is what it reveals")

	row.free()


## dotquay never edits a project's own scripts, so a resolved fix only ever offers a copy button —
## and only when the whole group is resolved, matching Group.has_uid_fixes()'s all-or-nothing rule.
func test_doctor_group_shows_the_copy_button_only_when_every_issue_is_fixable() -> void:
	var fixable := DotquayDoctor.from_payload({"healthy": false, "issues": [
		{"kind": "fragile_uid_reference", "message": "a.gd loads it.",
			"file": "a.gd", "uid": "uid://one", "res_path": "res://addons/x/one.gd"},
	]})
	var mixed := DotquayDoctor.from_payload({"healthy": false, "issues": [
		{"kind": "fragile_uid_reference", "message": "a.gd loads it.",
			"file": "a.gd", "uid": "uid://one", "res_path": "res://addons/x/one.gd"},
		{"kind": "fragile_uid_reference", "message": "an older dotquay sent this with no fix fields."},
	]})

	var row: Node = load("res://addons/dotquay/ui/items/doctor_group.tscn").instantiate()

	row.setup(fixable.groups()[0], true)
	runner.check(row.get_node("%CopyFixes").visible, "every issue resolved means the copy button shows")

	row.setup(mixed.groups()[0], true)
	runner.check(not row.get_node("%CopyFixes").visible, "one unresolved issue means no copy button at all")

	row.free()


func _package(origin: String, resolved: String = "0.4.7") -> DotquayGraph.PackageNode:
	return DotquayGraph.PackageNode.from_payload({
		"name": "VBeami.Core", "resolved": resolved, "origin": origin,
		"required_by": ["vbeami"] if origin == "transitive" else ["<project>"],
	})


## DESIGN-v0.19 §7a.3: a pin opens on the locked version with OK enabled (pinning it as-is is a real
## choice), and says what it does in the dialog's own words.
func test_package_version_dialog_pin_opens_ready_to_pin_the_locked_version() -> void:
	var dialog: DotquayPackageVersionDialog = load("res://addons/dotquay/ui/package_version_dialog.tscn").instantiate()

	dialog.open_for(_package("transitive"))

	runner.equal(dialog._version.text, "0.4.7", "the field starts at what's locked now")
	runner.equal(dialog.get_ok_button().text, "Pin", "a transitive package is pinned, not changed")
	runner.check(not dialog.get_ok_button().disabled, "pinning the locked version is allowed")
	runner.check(dialog._explanation.text.contains("vbeami"), "names the addon it takes over from: %s" % dialog._explanation.text)

	dialog.free()


func test_package_version_dialog_change_needs_a_different_valid_version() -> void:
	var dialog: DotquayPackageVersionDialog = load("res://addons/dotquay/ui/package_version_dialog.tscn").instantiate()
	var chosen: Array = []
	dialog.version_chosen.connect(func(id: String, version: String) -> void: chosen.append([id, version]))

	dialog.open_for(_package("root"))
	runner.check(dialog.get_ok_button().disabled, "changing to the version already locked does nothing")

	dialog._version.text = "0.4.*"
	dialog._update_ok_state()
	runner.check(dialog.get_ok_button().disabled, "a range isn't an exact version")
	runner.check(dialog._hint.text.contains("range"), "and the hint says why: %s" % dialog._hint.text)

	dialog._version.text = " 0.4.9 "
	dialog._update_ok_state()
	runner.check(not dialog.get_ok_button().disabled, "a different exact version enables Change")

	dialog._on_confirmed()
	runner.equal(chosen, [["VBeami.Core", "0.4.9"]], "confirming emits the trimmed version once")

	dialog.free()
