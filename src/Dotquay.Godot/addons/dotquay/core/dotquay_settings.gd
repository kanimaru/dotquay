@tool
class_name DotquaySettings
extends RefCounted

## Reads and registers the plugin's editor settings.
##
## Only one setting really matters: where the dotquay executable lives. It's an *editor* setting
## rather than a project setting on purpose — the CLI is a tool installed on this machine, not a
## property of the project, and committing a machine-specific absolute path into
## `project.godot` would break every teammate on a different OS.

const CLI_PATH_SETTING := "dotquay/general/cli_path"
const AUTO_REFRESH_SETTING := "dotquay/general/refresh_on_focus"
const RESOLVE_TIMEOUT_SETTING := "dotquay/general/resolve_timeout_seconds"

## Names to look for on PATH when no explicit path is configured, so the common case (dotquay
## installed normally) needs no setup at all.
const EXECUTABLE_NAMES := ["dotquay", "dotquay.exe"]


static func register(editor_settings: EditorSettings) -> void:
	_register_one(editor_settings, CLI_PATH_SETTING, "", {
		"name": CLI_PATH_SETTING,
		"type": TYPE_STRING,
		"hint": PROPERTY_HINT_GLOBAL_FILE,
		# No filter: the executable has no extension on Linux/macOS and .exe on Windows, and a
		# filter that's right on one platform hides the file on the others.
		"hint_string": "",
	})

	_register_one(editor_settings, AUTO_REFRESH_SETTING, true, {
		"name": AUTO_REFRESH_SETTING,
		"type": TYPE_BOOL,
	})

	_register_one(editor_settings, RESOLVE_TIMEOUT_SETTING, 120, {
		"name": RESOLVE_TIMEOUT_SETTING,
		"type": TYPE_INT,
		"hint": PROPERTY_HINT_RANGE,
		"hint_string": "5,900,5",
	})


static func _register_one(editor_settings: EditorSettings, key: String, default_value: Variant, info: Dictionary) -> void:
	if not editor_settings.has_setting(key):
		editor_settings.set_setting(key, default_value)

	# Always re-set the initial value and property info: they're not persisted, so a plugin reload
	# without this leaves the setting showing as a bare untyped field with no reset arrow.
	editor_settings.set_initial_value(key, default_value, false)
	editor_settings.add_property_info(info)


## The configured path, or the first match found on PATH. Empty means "not configured and not
## discoverable" — the panel treats that as a first-run state with a button, not as an error.
static func cli_path(editor_settings: EditorSettings) -> String:
	var configured := String(editor_settings.get_setting(CLI_PATH_SETTING)) if editor_settings.has_setting(CLI_PATH_SETTING) else ""
	configured = configured.strip_edges()

	if not configured.is_empty():
		return configured

	return discover_on_path()


## Asks the OS where dotquay is, using the platform's own lookup so PATH, shims, and symlinks all
## behave the way they do in a terminal. Returns "" when nothing is found.
static func discover_on_path() -> String:
	var output: Array = []
	var locator := "where" if OS.get_name() == "Windows" else "which"

	for name in EXECUTABLE_NAMES:
		output.clear()
		var code := OS.execute(locator, [name], output, false, false)
		if code != 0 or output.is_empty():
			continue

		# `where` can report several matches, one per line; the first is what a shell would run.
		var first := String(output[0]).split("\n")[0].strip_edges()
		if not first.is_empty() and FileAccess.file_exists(first):
			return first

	return ""


static func refresh_on_focus(editor_settings: EditorSettings) -> bool:
	if not editor_settings.has_setting(AUTO_REFRESH_SETTING):
		return true
	return bool(editor_settings.get_setting(AUTO_REFRESH_SETTING))


static func resolve_timeout_seconds(editor_settings: EditorSettings) -> int:
	if not editor_settings.has_setting(RESOLVE_TIMEOUT_SETTING):
		return 120
	return int(editor_settings.get_setting(RESOLVE_TIMEOUT_SETTING))
