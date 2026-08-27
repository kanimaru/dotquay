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
	# `init` predates the --json layer and still prints plain text, so its envelope is allowed to be
	# unparseable here — what matters is that the manifest exists afterwards.
	runner.check(
		FileAccess.file_exists(project.path_join("dotquay.toml")) or init_result.ok,
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


func test_real_cli_reports_a_missing_manifest_as_data() -> void:
	var cli := _cli_path()
	if cli.is_empty():
		return

	var missing := ProjectSettings.globalize_path("user://dotquay_contract_test_missing")
	var result := _run(cli, PackedStringArray(["graph", "--project", missing, "--json"]), "graph")

	runner.check(not result.ok, "an uninitialised directory is a failure, not an empty graph")
	runner.equal(result.error_kind, RESULT.KIND_NO_MANIFEST,
		"the CLI should categorise it as no_manifest so the panel can offer to initialise the project")
