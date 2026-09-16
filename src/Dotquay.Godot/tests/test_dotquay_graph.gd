extends RefCounted

## Covers [DotquayGraph] — the typed view over the `graph --json` payload, and the layout maths the
## dependency view depends on.
##
## The diamond fixture below is the same shape SPEC.md §8 uses to explain unification: two addons
## requiring one shared core. It is the case that must render as *one* node, and the case where
## shallowest-depth layering is the difference between a readable graph and a tangle.

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")

var runner: Object


func _diamond() -> Dictionary:
	return {
		"source": "resolved",
		"project_directory": "/tmp/project",
		"roots": ["a", "b"],
		"addons": [
			{"name": "a", "git": "https://example.com/a", "version": "v1.0.0", "resolved": "aaaaaaa1111",
			 "short_sha": "aaaaaaa", "path": "addons/a", "origin": "root", "source_kind": "version",
			 "constraint": "~1.0", "is_overridden": false, "is_patched": false, "status": "clean",
			 "dependencies": ["core"], "requirements": []},
			{"name": "b", "git": "https://example.com/b", "version": "v2.0.0", "resolved": "bbbbbbb2222",
			 "short_sha": "bbbbbbb", "path": "addons/b", "origin": "root", "source_kind": "version",
			 "constraint": "~2.0", "is_overridden": false, "is_patched": false, "status": "modified",
			 "dependencies": ["core"], "requirements": []},
			{"name": "core", "git": "https://example.com/core", "version": "v1.4.2", "resolved": "ccccccc3333",
			 "short_sha": "ccccccc", "path": "addons/core", "origin": "transitive", "source_kind": null,
			 "constraint": null, "is_overridden": false, "is_patched": true, "status": "patched",
			 "dependencies": [], "requirements": [
				{"requirer": "a", "kind": "version", "constraint": "~1", "git": "https://example.com/core"},
				{"requirer": "b", "kind": "version", "constraint": "~1.2", "git": "https://example.com/core"}]},
		],
		"edges": [
			{"from": "<project>", "to": "a"},
			{"from": "<project>", "to": "b"},
			{"from": "a", "to": "core"},
			{"from": "b", "to": "core"},
		],
		"dotnet": [
			{"name": "Newtonsoft.Json", "constraint": "13.0.*", "resolved": "13.0.3", "declared_by_project": true},
			{"name": "Serilog", "constraint": null, "resolved": "3.1.1", "declared_by_project": false},
		],
		"conflicts": [],
		"overrides": [],
		"has_conflicts": false,
	}


func test_parses_the_diamond() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())

	runner.equal(graph.addons.size(), 3, "a diamond is three addons, not four — one shared core")
	runner.equal(graph.roots.size(), 2, "both top-level entries should be roots")
	runner.equal(graph.source, "resolved", "the payload's source mode should be preserved")


func test_null_optional_fields_become_empty_strings() -> void:
	# JSON nulls become the literal text "<null>" if passed through String() unguarded — which would
	# render as a version label reading "<null>" on the graph node.
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())
	var core: DotquayGraph.AddonNode = graph.find("core")

	runner.equal(core.source_kind, "", "a null source_kind should read as absent")
	runner.equal(core.constraint, "", "a null constraint should read as absent")


func test_display_version_falls_back_to_short_sha() -> void:
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "pinned", "git": "https://example.com/pinned", "version": null,
		"resolved": "9f2c1abcdef", "short_sha": "9f2c1ab", "path": "addons/pinned", "origin": "root",
	})

	runner.equal(node.display_version(), "9f2c1ab", "a rev pin has no tag, so the short SHA is the honest label")


func test_display_version_reports_unresolved() -> void:
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "broken", "git": "https://example.com/broken", "version": null,
		"resolved": null, "short_sha": null, "path": "addons/broken", "origin": "root",
	})

	runner.equal(node.display_version(), "unresolved", "a node with no commit should say so rather than show blank")


func test_shared_dependency_takes_the_shallowest_depth() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())
	var depths := graph.depths()

	runner.equal(int(depths["a"]), 0, "roots sit in the first column")
	runner.equal(int(depths["b"]), 0, "roots sit in the first column")
	runner.equal(int(depths["core"]), 1, "a shared dependency sits one column right of its shallowest requirer")


func test_layers_are_sorted_and_stable() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())
	var layers := graph.layers()

	runner.equal(layers.size(), 2, "the diamond is two columns deep")
	runner.equal(layers[0][0], "a", "each column should be name-sorted so layout doesn't jump between refreshes")
	runner.equal(layers[0][1], "b", "each column should be name-sorted")
	runner.equal(layers[1][0], "core", "the shared dependency belongs in the second column")


func test_counts_summarise_health() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())
	var counts := graph.counts()

	runner.equal(int(counts["addons"]), 3, "addon count")
	runner.equal(int(counts["modified"]), 1, "'b' is hand-modified")
	runner.equal(int(counts["patched"]), 1, "'core' has a captured patch")
	runner.equal(int(counts["conflicts"]), 0, "the clean diamond has no conflicts")


func test_transitive_addons_are_not_removable() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())

	runner.check(graph.find("a").is_removable(), "a top-level entry can be removed from the manifest")
	runner.check(not graph.find("core").is_removable(), "a transitive addon has no manifest entry to remove")


func test_dotnet_entries_distinguish_their_origin() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_diamond())

	runner.equal(graph.dotnet.size(), 2, "both NuGet entries should parse")
	runner.check(graph.dotnet[0].declared_by_project, "the project's own [dotnet.*] entry")
	runner.check(not graph.dotnet[1].declared_by_project, "an addon-declared package (P1-5) can't be removed from the manifest")
	runner.equal(graph.dotnet[1].display_version(), "3.1.1", "resolved version wins over an absent constraint")


func _conflicted() -> Dictionary:
	var payload := _diamond()
	payload["addons"] = [payload["addons"][0], payload["addons"][1]]
	payload["has_conflicts"] = true
	payload["conflicts"] = [{
		"name": "core",
		"kind": "no_common_version",
		"message": "'core': no tag satisfies every requested version constraint at once.",
		"requirements": [
			{"requirer": "a", "kind": "version", "constraint": "~1", "git": "https://example.com/core"},
			{"requirer": "b", "kind": "version", "constraint": "~2", "git": "https://example.com/core"},
		],
		"candidates": [
			{"kind": "version", "value": "v2.0.0", "git": "https://example.com/core", "resolved": "ddd",
			 "satisfies": ["b"], "satisfies_all": false},
			{"kind": "version", "value": "v1.9.0", "git": "https://example.com/core", "resolved": "eee",
			 "satisfies": ["a", "b"], "satisfies_all": true},
		],
	}]
	return payload


func test_parses_a_conflict() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())

	runner.check(graph.has_conflicts, "the payload's conflict flag should be carried through")
	runner.check(graph.has_conflict("core"), "the conflicted name should be findable")
	runner.equal(graph.conflict_for("core").requirements.size(), 2, "both requirers should be listed")


func test_candidates_that_satisfy_everyone_rank_first() -> void:
	# The newest tag is listed first by the CLI, but the one that actually unblocks the project is
	# what the picker must offer as its default — otherwise the one-click fix is the wrong click.
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())
	var ranked := graph.conflict_for("core").ranked_candidates()

	runner.equal(ranked[0].value, "v1.9.0", "the all-satisfying candidate should be first")
	runner.check(ranked[0].satisfies_all, "and it should be flagged as such")
	runner.equal(ranked[1].value, "v2.0.0", "the partially-satisfying candidate follows")


func test_candidate_maps_to_a_cli_flag() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())
	var candidate: DotquayGraph.Candidate = graph.conflict_for("core").ranked_candidates()[0]

	runner.equal(candidate.override_flag(), "--version", "a candidate should map straight onto `dotquay override`")


func test_conflict_headline_is_plain_language() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())

	runner.check(
		graph.conflict_for("core").headline().contains("No released version"),
		"the headline should explain the conflict without quoting the resolver")


func test_requirement_describes_itself() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())
	var requirement: DotquayGraph.Requirement = graph.conflict_for("core").requirements[0]

	runner.equal(requirement.describe(), "a wants version ~1", "requirement lines feed both the dialog and the sidebar")


func test_requirement_describes_a_rev_pin_as_a_commit() -> void:
	# P1-21: 'rev' is the wire's own word for a commit, not one a person reading the sidebar should
	# have to translate themselves.
	var requirement := DotquayGraph.Requirement.from_payload(
		{"requirer": "a", "kind": "rev", "constraint": "3fa9c1d", "git": "https://example.com/core"})

	runner.equal(requirement.describe(), "a wants commit 3fa9c1d", "'rev' reads as 'commit'")


# -- manual pin vs. a real conflict (P1-18) ---------------------------------------------------

func test_a_real_conflict_keeps_the_generic_dialog_chrome() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())
	var conflict := graph.conflict_for("core")

	runner.check(not conflict.is_manual_pin, "a payload-parsed conflict is never a manual pin")
	runner.equal(conflict.dialog_title(), "Resolve dependency conflict",
		"a real conflict's title stays generic — the headline underneath names the specifics")
	runner.equal(conflict.candidates_heading(), "Pick a version to force",
		"forcing a version is exactly what overriding a real conflict does")
	runner.equal(conflict.ok_button_label(), "Apply override", "the CLI operation really is an override")


func test_a_manual_pin_gets_its_own_headline_title_and_button() -> void:
	# DotquayPanel._open_conflict_dialog builds exactly this shape for "Always use a specific
	# version..." on an addon with no real conflict.
	var conflict := DotquayGraph.Conflict.new()
	conflict.name = "gut"
	conflict.is_manual_pin = true

	runner.equal(conflict.headline(), "Pin 'gut' to a version",
		"a pin on a healthy addon isn't described as something that 'could not be resolved'")
	runner.equal(conflict.dialog_title(), conflict.headline(),
		"the window title and the headline say the same thing — there's no separate problem to name")
	runner.equal(conflict.candidates_heading(), "Choose the version dotquay should always use",
		"'force' implies duress; nothing forced this pin")
	runner.equal(conflict.ok_button_label(), "Pin", "'Apply override' is CLI vocabulary, not UI vocabulary")


func test_an_unrecognised_real_conflict_kind_is_not_mistaken_for_a_pin() -> void:
	# is_manual_pin is a fact set explicitly by the caller, not inferred from a blank/unknown kind —
	# a genuine conflict the plugin doesn't recognise must still read as a conflict.
	var conflict := DotquayGraph.Conflict.new()
	conflict.name = "core"
	conflict.kind = "some_future_conflict_kind"

	runner.equal(conflict.headline(), "'core' could not be resolved.",
		"an unrecognised real conflict falls back to the generic headline, not a pin's")
	runner.equal(conflict.dialog_title(), "Resolve dependency conflict",
		"and keeps the generic conflict title")


func test_candidate_display_label_shows_a_rev_as_a_commit() -> void:
	var candidate := DotquayGraph.Candidate.from_payload(
		{"kind": "rev", "value": "3fa9c1d", "git": "https://example.com/core"})

	runner.equal(candidate.display_label(), "commit 3fa9c1d", "the picker's radio label uses the same word")


func test_project_requirer_is_named_readably() -> void:
	var requirement := DotquayGraph.Requirement.from_payload(
		{"requirer": "<project>", "kind": "version", "constraint": "~1.0", "git": "https://example.com/a"})

	runner.equal(requirement.describe(), "this project wants version ~1.0", "'<project>' is wire syntax, not a name to show")


func test_linked_addon_is_healthy_not_broken() -> void:
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "my-addon", "git": "https://example.com/m", "path": ".", "folder": "my-addon",
		"origin": "root", "status": "linked",
	})

	runner.check(node.is_linked(), "a linked addon should be recognisable")
	# A link's contents are a live working copy, so it will never match the lockfile. Treating that
	# as unhealthy would leave every linked addon badged as a problem for as long as it's linked.
	runner.check(node.is_healthy(), "a linked addon is not in a bad state")


func test_counts_include_links() -> void:
	var payload := _diamond()
	payload["addons"][0]["status"] = "linked"

	var graph: DotquayGraph = GRAPH.from_payload(payload)

	runner.equal(int(graph.counts()["linked"]), 1, "linked addons should be counted for the summary line")


func test_install_as_folder_is_parsed() -> void:
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "mtoon-shader", "git": "https://example.com/m", "path": ".",
		"folder": "Godot-MToon-Shader", "origin": "transitive",
	})

	runner.equal(node.folder, "Godot-MToon-Shader", "the install folder should be carried through")
	runner.check(node.is_renamed(), "a folder differing from the name is a rename worth badging")


func test_folder_defaults_to_the_name() -> void:
	# A payload from a dotquay predating install_as has no `folder` key at all; falling back to the
	# name keeps an older CLI rendering correctly instead of showing a blank badge.
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "leaf", "git": "https://example.com/leaf", "path": "addons/leaf", "origin": "root",
	})

	runner.equal(node.folder, "leaf", "an absent folder means the addon's own name")
	runner.check(not node.is_renamed(), "matching name and folder is not a rename")


func test_null_folder_also_defaults_to_the_name() -> void:
	# SPEC.md §17: an optional field is emitted as null, not omitted, once a payload declares the key
	# at all — a JSON "folder": null must fall back the same way an absent key does, not render as the
	# literal text "<null>".
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "leaf", "git": "https://example.com/leaf", "path": "addons/leaf", "origin": "root",
		"folder": null,
	})

	runner.equal(node.folder, "leaf", "a null folder means the addon's own name")
	runner.check(not node.is_renamed(), "matching name and folder is not a rename")


func _folder_conflict() -> Dictionary:
	var payload := _diamond()
	payload["has_conflicts"] = true
	payload["conflicts"] = [{
		"name": "mtoon-shader",
		"kind": "disagreeing_install_folders",
		"message": "'mtoon-shader' is wanted in different folders under addons/.",
		"requirements": [],
		"candidates": [
			{"kind": "rev", "value": "aaa", "git": "https://example.com/m", "resolved": null,
			 "satisfies": ["vrm"], "satisfies_all": false, "install_as": "Godot-MToon-Shader"},
			{"kind": "rev", "value": "aaa", "git": "https://example.com/m", "resolved": null,
			 "satisfies": ["other"], "satisfies_all": false, "install_as": "mtoon"},
		],
	}]
	return payload


func test_folder_conflict_candidates_carry_their_folder() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_folder_conflict())
	var candidate: DotquayGraph.Candidate = graph.conflict_for("mtoon-shader").candidates[0]

	runner.check(candidate.is_folder_choice(), "a candidate with install_as is a folder choice")
	runner.equal(candidate.install_as, "Godot-MToon-Shader", "the folder should be carried through")


func test_folder_choice_reads_as_a_folder_not_a_version() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_folder_conflict())
	var candidate: DotquayGraph.Candidate = graph.conflict_for("mtoon-shader").candidates[0]

	# Both candidates name the same rev, so labelling them "rev aaa" twice would show the user two
	# identical options for a choice that is entirely about the destination.
	runner.equal(candidate.display_label(), "install as addons/Godot-MToon-Shader", "the label must name the folder")


func test_version_candidate_is_not_a_folder_choice() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_conflicted())
	var candidate: DotquayGraph.Candidate = graph.conflict_for("core").ranked_candidates()[0]

	runner.check(not candidate.is_folder_choice(), "an ordinary version candidate has no install_as")
	runner.equal(candidate.display_label(), "version v1.9.0", "and still labels itself by version")


func test_folder_conflict_has_its_own_headline() -> void:
	var graph: DotquayGraph = GRAPH.from_payload(_folder_conflict())

	runner.check(
		graph.conflict_for("mtoon-shader").headline().contains("different folders"),
		"a folder conflict should not be described as a version problem")


func test_null_arrays_parse_as_empty_not_a_crash() -> void:
	# SPEC.md §17: optional array fields are emitted as JSON null, never omitted. `payload.get(key,
	# [])` only supplies its default when the key is absent, so a present-but-null "conflicts" used to
	# raise "Unable to iterate on object of type 'Nil'" instead of parsing.
	var payload := _diamond()
	payload["conflicts"] = null
	payload["overrides"] = null

	var graph: DotquayGraph = GRAPH.from_payload(payload)

	runner.equal(graph.conflicts.size(), 0, "a null conflicts array should parse as empty, not raise")
	runner.equal(graph.overrides.size(), 0, "a null overrides array should parse as empty, not raise")


func test_null_nested_arrays_parse_as_empty_not_a_crash() -> void:
	# The same null-vs-absent trap applies one level down — an addon's own "dependencies" and
	# "requirements", and a conflict's "requirements" and "candidates".
	var node := DotquayGraph.AddonNode.from_payload({
		"name": "leaf", "git": "https://example.com/leaf", "path": "addons/leaf", "origin": "root",
		"dependencies": null, "requirements": null,
	})

	runner.equal(node.dependencies.size(), 0, "a null dependencies array should parse as empty")
	runner.equal(node.requirements.size(), 0, "a null requirements array should parse as empty")

	var conflict := DotquayGraph.Conflict.from_payload({
		"name": "core", "kind": "no_common_version", "message": "x",
		"requirements": null, "candidates": null,
	})

	runner.equal(conflict.requirements.size(), 0, "a null requirements array should parse as empty")
	runner.equal(conflict.candidates.size(), 0, "a null candidates array should parse as empty")


func test_empty_payload_parses_cleanly() -> void:
	# A freshly `init`ed project with no dependencies must render an empty panel, not an error.
	var graph: DotquayGraph = GRAPH.from_payload({})

	runner.check(graph.is_empty(), "a payload with nothing in it should report as empty")
	runner.equal(graph.layers().size(), 0, "and produce no layout columns")
