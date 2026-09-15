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
## How long a single CLI call may run before [DotquayCli] kills it (P0-7). Generous by default: a
## `dotquay update` can mean several git fetches over a slow connection, and killing a legitimately
## slow-but-working clone is worse than the hang this exists to prevent. What it actually guards
## against is a call that will never finish on its own — an interactive prompt the CLI can't answer
## non-interactively, a half-open connection — where any finite number beats forever.
const COMMAND_TIMEOUT_SETTING := "dotquay/general/command_timeout_seconds"
const DEFAULT_COMMAND_TIMEOUT_SECONDS := 300
## Below this, a fat-fingered "2" in the settings field would fail nearly every real command instead
## of only the ones that are actually stuck.
const MIN_COMMAND_TIMEOUT_SECONDS := 10

## A now-meaningless setting from an earlier version, back when `OS.execute` truly had no timeout to
## give it to — kept only so [method register] can erase it from an editor config that still carries
## it. [constant COMMAND_TIMEOUT_SETTING] is its unrelated, working replacement, not a renamed
## continuation of it: the two were never live at the same time, so there is nothing to migrate.
const RETIRED_SETTINGS := ["dotquay/general/resolve_timeout_seconds"]

## Where the "Get dotquay…" button (P0-2) opens — this plugin's own repo's release page, the
## canonical place to download the CLI binary this tab needs.
const RELEASES_URL := "https://gitlab.kemomi.de/wild-alliance/dotquay/-/releases"

## Names to look for on PATH when no explicit path is configured, so the common case (dotquay
## installed normally) needs no setup at all. Platform-specific: `where` on Windows already resolves
## the bare name via PATHEXT, so trying `dotquay.exe` there is just a documented order-of-preference,
## but on Unix trying `dotquay.exe` is a guaranteed-miss extra process spawn on every call while
## unconfigured.
const EXECUTABLE_NAMES_WINDOWS := ["dotquay.exe", "dotquay"]
const EXECUTABLE_NAMES_UNIX := ["dotquay"]


## The executable names to probe for the current platform. See [constant EXECUTABLE_NAMES_WINDOWS]
## and [constant EXECUTABLE_NAMES_UNIX].
static func _executable_names() -> PackedStringArray:
	if OS.get_name() == "Windows":
		return PackedStringArray(EXECUTABLE_NAMES_WINDOWS)
	return PackedStringArray(EXECUTABLE_NAMES_UNIX)


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

	_register_one(editor_settings, COMMAND_TIMEOUT_SETTING, DEFAULT_COMMAND_TIMEOUT_SECONDS, {
		"name": COMMAND_TIMEOUT_SETTING,
		"type": TYPE_INT,
		"hint": PROPERTY_HINT_RANGE,
		"hint_string": "%d,3600,1,suffix:s" % MIN_COMMAND_TIMEOUT_SECONDS,
	})

	for retired in RETIRED_SETTINGS:
		if editor_settings.has_setting(retired):
			editor_settings.erase(retired)


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
	configured = _normalize_configured_path(configured)

	if not configured.is_empty():
		return configured

	return discover_on_path()


## Cleans up the couple of things people commonly end up with in the CLI Path setting, so a value
## that looks right in the text field actually resolves.
static func _normalize_configured_path(path: String) -> String:
	if path.length() >= 2 and path.begins_with("\"") and path.ends_with("\""):
		# Windows Explorer's "Copy as path" wraps the result in double quotes.
		path = path.substr(1, path.length() - 2)

	return _expand_home(path)


## Expands a leading `~` to the user's home directory. Unix only — on Windows `~` isn't a shell
## convention users reach for in a file-path setting, so a literal `~` there is left alone.
static func _expand_home(path: String) -> String:
	if OS.get_name() == "Windows":
		return path
	if path != "~" and not path.begins_with("~/"):
		return path

	var home := OS.get_environment("HOME")
	if home.is_empty():
		return path

	return home + path.substr(1)


## Asks the OS where dotquay is, using the platform's own lookup so PATH, shims, and symlinks all
## behave the way they do in a terminal. Returns "" when nothing is found.
static func discover_on_path() -> String:
	var output: Array = []
	var locator := "where" if OS.get_name() == "Windows" else "which"

	for name in _executable_names():
		output.clear()
		var code := OS.execute(locator, [name], output, false, false)
		if code != 0 or output.is_empty():
			continue

		# `where` can report several matches, one per line; the first is what a shell would run.
		var first := String(output[0]).split("\n")[0].strip_edges()
		if not first.is_empty() and FileAccess.file_exists(first):
			return first

	# Not on PATH according to the shell locator — try the well-known spots dotquay commonly ends up
	# installed to anyway. The editor doesn't necessarily inherit the same PATH a login shell would
	# (notably on macOS, an app launched from Finder/Dock), so `which`/`where` coming up empty doesn't
	# mean dotquay isn't there.
	for candidate in _well_known_install_locations():
		if not candidate.is_empty() and FileAccess.file_exists(candidate):
			return candidate

	return ""


## Fixed list of places dotquay is commonly installed to, checked after PATH lookup fails. Built
## per-platform so we never check a path (or environment variable) that only makes sense elsewhere.
static func _well_known_install_locations() -> PackedStringArray:
	var candidates := PackedStringArray()

	if OS.get_name() == "Windows":
		var local_app_data := OS.get_environment("LOCALAPPDATA")
		if not local_app_data.is_empty():
			candidates.append(local_app_data + "\\dotquay\\dotquay.exe")
			candidates.append(local_app_data + "\\Programs\\dotquay\\dotquay.exe")
		return candidates

	var home := OS.get_environment("HOME")
	if not home.is_empty():
		candidates.append(home + "/.local/bin/dotquay")
		candidates.append(home + "/.dotnet/tools/dotquay")

	candidates.append("/usr/local/bin/dotquay")

	if OS.get_name() == "macOS":
		candidates.append("/opt/homebrew/bin/dotquay")

	return candidates


static func refresh_on_focus(editor_settings: EditorSettings) -> bool:
	if not editor_settings.has_setting(AUTO_REFRESH_SETTING):
		return true
	return bool(editor_settings.get_setting(AUTO_REFRESH_SETTING))


## In milliseconds, for direct use against [method Time.get_ticks_msec]. Clamped to
## [constant MIN_COMMAND_TIMEOUT_SECONDS] so a stray "0" or a negative value typed into the settings
## field can't turn every command into an instant, confusing timeout.
static func command_timeout_ms(editor_settings: EditorSettings) -> int:
	var seconds := DEFAULT_COMMAND_TIMEOUT_SECONDS
	if editor_settings.has_setting(COMMAND_TIMEOUT_SETTING):
		seconds = int(editor_settings.get_setting(COMMAND_TIMEOUT_SETTING))

	return maxi(seconds, MIN_COMMAND_TIMEOUT_SECONDS) * 1000
