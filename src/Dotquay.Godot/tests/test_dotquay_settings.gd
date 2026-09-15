extends RefCounted

## Covers the pure/near-pure parts of [DotquaySettings]: the Editor Settings breadcrumb builder
## (P1-1) and the path-mistake checker (P1-2). Both are plain string/filesystem logic with no
## EditorSettings object needed, so they're covered directly rather than through [DotquayCli].
##
## [method DotquaySettings.path_problem] does touch the real filesystem (it has to — a folder and a
## `.dll` are filesystem facts, not string facts), so these tests create and clean up real files and
## folders under `user://`, which works headlessly with no special permissions.

const SETTINGS := preload("res://addons/dotquay/core/dotquay_settings.gd")

var runner: Object


func test_breadcrumb_title_cases_each_segment_of_the_real_setting_key() -> void:
	var breadcrumb := SETTINGS.breadcrumb("dotquay/general/cli_path")

	runner.equal(breadcrumb, "Editor Settings -> Dotquay -> General -> Cli Path",
		"the breadcrumb should match Godot's own category-tree rendering of the setting key")


func test_breadcrumb_matches_the_real_command_timeout_setting_key() -> void:
	var breadcrumb := SETTINGS.breadcrumb(SETTINGS.COMMAND_TIMEOUT_SETTING)

	runner.equal(breadcrumb, "Editor Settings -> Dotquay -> General -> Command Timeout Seconds",
		"the timeout setting's breadcrumb should also be derived, not hand-typed")


func test_missing_path_reports_it_does_not_exist() -> void:
	var missing := ProjectSettings.globalize_path("user://dotquay_test_definitely_missing")

	var message := SETTINGS.path_problem(missing)

	runner.check(message.contains("does not exist"), "a missing path should say it does not exist")


func test_a_folder_is_reported_as_a_folder_not_the_executable() -> void:
	var dir_path := ProjectSettings.globalize_path("user://dotquay_test_folder")
	DirAccess.make_dir_absolute(dir_path)

	var message := SETTINGS.path_problem(dir_path)

	DirAccess.remove_absolute(dir_path)
	runner.check(message.contains("folder"), "a folder should be called out as a folder, not treated as missing")


func test_a_dll_is_reported_with_a_hint_to_pick_the_executable() -> void:
	var dll_path := ProjectSettings.globalize_path("user://dotquay_test.dll")
	var f := FileAccess.open(dll_path, FileAccess.WRITE)
	f.store_string("not a real dll")
	f.close()

	var message := SETTINGS.path_problem(dll_path)

	DirAccess.remove_absolute(dll_path)
	runner.check(message.contains(".dll"), "a .dll should be called out specifically, not just \"does not exist\"")


func test_an_ordinary_existing_file_has_no_problem() -> void:
	var exe_path := ProjectSettings.globalize_path("user://dotquay_test_fake_exe")
	var f := FileAccess.open(exe_path, FileAccess.WRITE)
	f.store_string("pretend binary")
	f.close()

	var message := SETTINGS.path_problem(exe_path)

	DirAccess.remove_absolute(exe_path)
	runner.equal(message, "", "an ordinary existing file with none of the three red flags should report no problem")
