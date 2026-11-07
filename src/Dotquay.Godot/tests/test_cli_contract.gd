extends RefCounted

## Runs the real dotquay binary and parses its real output.
##
## The fixture suites prove the parser handles the payload this addon *expects*; only this one proves
## the CLI still emits it. That gap is exactly where a wire contract breaks — a renamed JSON key
## compiles, passes every fixture test, and produces an empty panel.
##
## Skipped unless DOTQUAY_TEST_CLI points at a dotquay executable, so a checkout with no built binary
## still runs the rest of the suite green:
##   $env:DOTQUAY_TEST_CLI = "…/dotquay.exe"; ./run_tests.ps1

const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")
const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const PREVIEW := preload("res://addons/dotquay/core/dotquay_preview.gd")
const EXPLAIN := preload("res://addons/dotquay/core/dotquay_explain.gd")
const DOCTOR := preload("res://addons/dotquay/core/dotquay_doctor.gd")
const OUTDATED := preload("res://addons/dotquay/core/dotquay_outdated.gd")
const OPERATION := preload("res://addons/dotquay/core/dotquay_operation.gd")

var runner: Object


func _cli_path() -> String:
	var configured := OS.get_environment("DOTQUAY_TEST_CLI").strip_edges()
	return configured if not configured.is_empty() and FileAccess.file_exists(configured) else ""


## A throwaway project directory. `user://` resolves to a real filesystem path the CLI can also see,
## which `res://` inside an exported game would not.
func _make_project() -> String:
	var path := ProjectSettings.globalize_path("user://dotquay_contract_test")
	DirAccess.make_dir_recursive_absolute(path)
	return path


func _run(cli: String, args: PackedStringArray, command_name: String) -> DotquayResult:
	var output: Array = []
	var code := OS.execute(cli, args, output, true, false)

	var joined := ""
	for line in output:
		joined += String(line)

	return RESULT.parse(joined, code, command_name)


func test_real_cli_emits_a_graph_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		print("  SKIP  DOTQUAY_TEST_CLI is not set — skipping the live CLI contract test.")
		return

	var project := _make_project()

	var init_result := _run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")
	# `init` emits a --json envelope like every other command (`InitCommand.cs`), verified live against
	# 0.18: it reports `"command":"init"` with an operation payload. Both halves must therefore hold —
	# the manifest on disk *and* an envelope this addon could parse.
	runner.check(
		FileAccess.file_exists(project.path_join("dotquay.toml")) and init_result.ok,
		"`init` should leave a dotquay.toml behind")

	var graph_result := _run(cli, PackedStringArray(["graph", "--project", project, "--json"]), "graph")

	runner.check(graph_result.ok, "the real CLI's graph output should parse as a success envelope")
	runner.equal(graph_result.schema, RESULT.SCHEMA_SUPPORTED, "the CLI's payload schema should match this addon's")
	runner.check(not graph_result.cli_version.is_empty(), "the envelope should report a CLI version")

	var graph: DotquayGraph = GRAPH.from_payload(graph_result.data)

	runner.check(graph.is_empty(), "a freshly initialised project has no dependencies")
	runner.equal(graph.source, DotquayWire.GRAPH_SOURCE_LOCKFILE, "the default graph mode is the offline lockfile read")
	runner.equal(graph.project_directory.replace("\\", "/"), project.replace("\\", "/"),
		"--project should be echoed back, confirming the CLI operated on the directory we asked for")


## v0.19's package fields, checked against the real binary. The package is written straight into the
## lockfile rather than added, so no NuGet feed is contacted: a locked package that nothing declares
## is an orphan, which the CLI must say without any network.
func test_real_cli_emits_package_origin_requirers_and_source() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := _make_project()
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	var lockfile := FileAccess.open(project.path_join("dotquay.lock"), FileAccess.WRITE)
	lockfile.store_string("[[dotnet]]\nname = \"Ghost.Package\"\nresolved = \"2.0.0\"\n")
	lockfile.close()

	var result := _run(cli, PackedStringArray(["graph", "--project", project, "--json"]), "graph")
	# Removed straight away, before any assertion can return early: the other contract tests share
	# this directory and expect a project with nothing in it.
	DirAccess.remove_absolute(project.path_join("dotquay.lock"))
	runner.check(result.ok, "graph should succeed on a project with a leftover package")

	var entries: Array = DotquayWire.array_or_empty(result.data.get("dotnet"))
	runner.equal(entries.size(), 1, "the leftover package should be reported")
	if entries.is_empty():
		return

	var raw: Dictionary = entries[0]
	# Key presence, not just values: a renamed key would parse to the fallback and still "work".
	for key in ["origin", "required_by", "source"]:
		runner.check(raw.has(key), "the CLI should emit dotnet[].%s (v0.19)" % key)

	var package := DotquayGraph.PackageNode.from_payload(raw)
	runner.check(package.is_orphan(), "a locked package nothing declares is an orphan")
	runner.check(package.is_on_public_feed(), "no feed recorded means nuget.org")


## The preview payload is a different shape from every other command's, reported under its own
## envelope command — so a rename on either side is invisible to the fixture suite and shows up as an
## empty confirmation dialog. This is the only test that would notice.
func test_real_cli_emits_a_preview_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := _make_project()
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	var result := _run(
		cli,
		PackedStringArray([
			"add", "https://github.com/nathanhoad/godot_dialogue_manager",
			"--preview", "--project", project, "--json"]),
		"add")

	# Network-dependent: a checkout behind a proxy shouldn't fail the suite over a fetch. What is
	# being asserted is the *shape* of a reply, not that GitHub answered.
	if not result.ok:
		print("  SKIP  `add --preview` did not reach the remote (%s) — skipping the payload check."
			% result.error_kind)
		return

	runner.equal(result.command, DotquayWire.COMMAND_ADD_PREVIEW,
		"a preview must report its own command name, or the panel routes it to the add parser")
	runner.equal(result.schema, RESULT.SCHEMA_SUPPORTED, "the preview payload schema should match this addon's")

	var preview: DotquayPreview = PREVIEW.from_payload(result.data)

	runner.equal(preview.name, "godot_dialogue_manager", "the preview names the addon that would be added")
	runner.check(not preview.addons.is_empty(), "the preview lists at least the addon being added")
	runner.check(preview.addons[0].is_requested(), "the requested addon is attributed to <project>")
	runner.check(not preview.addons[0].short_sha.is_empty(), "the CLI precomputes the short SHA")

	# The point of the whole feature: nothing was written, so the graph is still empty afterwards.
	var after := _run(cli, PackedStringArray(["graph", "--project", project, "--json"]), "graph")
	runner.check(GRAPH.from_payload(after.data).is_empty(), "a preview must not install anything")


## `status --explain` is the panel's only user of the `status` envelope, and the flag is what decides
## whether the payload carries an `explanation` object at all. A fixture suite cannot notice the CLI
## rejecting the flag, renaming the command, or dropping the field — this can.
##
## Deliberately run against a project with nothing installed: the assertion is that the *envelope and
## payload shape* survive the round trip. Producing a genuinely modified addon here would mean a
## network fetch and a hand-edit, which is what the C# suite and the by-hand verification already do.
func test_real_cli_accepts_explain_and_emits_a_status_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := _make_project()
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	var result := _run(
		cli, PackedStringArray(["status", "--explain", "--project", project, "--json"]), "status")

	runner.check(result.ok, "`status --explain` should succeed on an initialised project")
	runner.equal(result.command, DotquayWire.COMMAND_STATUS,
		"the status envelope must report the command name the panel routes on")
	runner.equal(result.schema, RESULT.SCHEMA_SUPPORTED, "the status payload schema should match this addon's")

	var status: DotquayExplain = EXPLAIN.from_payload(result.data)

	runner.check(status.addons.is_empty(), "a project with nothing installed has no status rows")
	runner.check(status.all_clean, "and nothing installed is trivially all clean")
	runner.check(status.explanation_for("anything") == null,
		"asking about an addon that isn't there is null, not a crash")


func test_real_cli_reports_a_missing_manifest_as_data() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var missing := ProjectSettings.globalize_path("user://dotquay_contract_test_missing")
	var result := _run(cli, PackedStringArray(["graph", "--project", missing, "--json"]), "graph")

	runner.check(not result.ok, "an uninitialised directory is a failure, not an empty graph")
	runner.equal(result.error_kind, DotquayWire.KIND_NO_MANIFEST,
		"the CLI should categorise it as no_manifest so the panel can offer to initialise the project")


## A malformed dotquay.lock used to crash `status --json` with a stack trace and nothing on stdout,
## which this addon could only show as "dotquay didn't answer as expected".
func test_real_cli_reports_a_malformed_lockfile_as_invalid_project_file() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := ProjectSettings.globalize_path("user://dotquay_contract_test_broken")
	DirAccess.make_dir_recursive_absolute(project)
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")
	var lockfile := FileAccess.open(project.path_join("dotquay.lock"), FileAccess.WRITE)
	lockfile.store_string("this is [ not toml\n")
	lockfile.close()

	var result := _run(cli, PackedStringArray(["status", "--project", project, "--json"]), "status")

	runner.check(not result.ok, "a lockfile that can't be read is a failure")
	runner.equal(result.error_kind, DotquayWire.KIND_INVALID_PROJECT_FILE,
		"the CLI should categorise it as invalid_project_file, not crash")
	runner.check(result.error_message.contains("dotquay.lock(1,6)"),
		"the message should name the file and the parser's position: %s" % result.error_message)

## A broken ~/.dotquay/credentials.toml used to come back as `auth`, so this addon suggested
## `dotquay login`, which reads the same file. DOTQUAY_HOME points the CLI at a throwaway home, never
## the developer's own.
func test_real_cli_reports_a_malformed_credentials_file_as_invalid_config_file() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var home := ProjectSettings.globalize_path("user://dotquay_contract_test_home")
	DirAccess.make_dir_recursive_absolute(home.path_join(".dotquay"))
	var credentials := FileAccess.open(home.path_join(".dotquay").path_join("credentials.toml"), FileAccess.WRITE)
	credentials.store_string("this is [ not toml\n")
	credentials.close()

	var previous_home := OS.get_environment("DOTQUAY_HOME")
	OS.set_environment("DOTQUAY_HOME", home)
	var result := _run(cli, PackedStringArray(["login", "--list", "--json"]), "login")
	if previous_home.is_empty():
		OS.unset_environment("DOTQUAY_HOME")
	else:
		OS.set_environment("DOTQUAY_HOME", previous_home)
	DirAccess.remove_absolute(home.path_join(".dotquay").path_join("credentials.toml"))

	runner.check(not result.ok, "a credentials file that can't be read is a failure")
	runner.equal(result.error_kind, DotquayWire.KIND_INVALID_CONFIG_FILE,
		"the CLI should categorise it as invalid_config_file, not auth")
	runner.check(result.error_message.contains("credentials.toml(1,6)"),
		"the message should name the file and the parser's position: %s" % result.error_message)

## The three payloads C-24 gave typed views to had no live coverage at all — their field names were
## only ever checked against fixtures this plugin wrote itself, which is precisely the gap this suite
## exists to close: a renamed key passes every fixture test and empties a panel.
##
## [DotquaySelfCheck] is deliberately absent. `self check` needs the network *and* a release-read
## credential for the update channel, so it would skip on most machines and fail on the rest for
## reasons that say nothing about the wire contract. Its fixture suite is the honest limit there.
func test_real_cli_emits_a_doctor_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := _make_project()
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	var result := _run(cli, PackedStringArray(["doctor", "--project", project, "--json"]), "doctor")

	runner.check(result.ok, "`doctor` should succeed on an initialised project")
	runner.equal(result.schema, RESULT.SCHEMA_SUPPORTED, "the doctor payload schema should match this addon's")

	var doctor: DotquayDoctor = DOCTOR.from_payload(result.data)

	# Asserted as an invariant rather than as `healthy == true`: this holds whatever the machine
	# running it has installed, and it is the thing the panel actually depends on — `_report_doctor`
	# returns early on `healthy`, so a true flag arriving alongside real issues would hide them.
	runner.equal(doctor.healthy, doctor.issues.is_empty(),
		"`healthy` must mirror whether any issues came back, or the panel's early return swallows them")

	for issue in doctor.issues:
		runner.check(not issue.describe().is_empty(), "every issue must render as something readable")


func test_real_cli_emits_an_outdated_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := _make_project()
	_run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	# No dependencies means no `git ls-remote` and no NuGet metadata query, so this stays offline and
	# deterministic — the assertion is the payload's shape, not what any remote currently publishes.
	var result := _run(cli, PackedStringArray(["outdated", "--project", project, "--json"]), "outdated")

	runner.check(result.ok, "`outdated` should succeed on an initialised project")
	runner.equal(result.schema, RESULT.SCHEMA_SUPPORTED, "the outdated payload schema should match this addon's")

	var outdated: DotquayOutdated = OUTDATED.from_payload(result.data)

	runner.check(outdated.is_all_up_to_date(), "a project with no dependencies has nothing to update")
	runner.equal(outdated.reported_count(), 0, "and the count the panel announces agrees with that")
	runner.check(outdated.outdated_entries().is_empty(), "leaving no rows to list")


## `init` stands in for every mutating command: install/update/remove/override/patch all report
## through the same operation payload, which is what the panel's `_:` fall-through parses. `init` is
## the only one of them that needs neither a network fetch nor an existing lockfile.
func test_real_cli_emits_an_operation_this_addon_can_parse() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var project := ProjectSettings.globalize_path("user://dotquay_contract_test_operation")
	DirAccess.make_dir_recursive_absolute(project)

	var result := _run(cli, PackedStringArray(["init", "--project", project, "--json"]), "init")

	runner.check(result.ok, "`init` should report success through the envelope")

	var operation: DotquayOperation = OPERATION.from_payload(result.data)

	# What matters is that `changes` round-trips into typed rows at all: a renamed `action` key would
	# leave every row looking `unchanged` and the panel would report "nothing needed changing" for an
	# operation that just wrote a file.
	runner.check(not operation.changes.is_empty(), "`init` writes a manifest, so it reports a change")
	runner.check(not operation.did_nothing(), "and a command that wrote something never reads as a no-op")

	for change in operation.changes:
		runner.check(not change.name.is_empty(), "every change row names what it touched")
		runner.check(not change.action.is_empty(), "and says what it did to it")
