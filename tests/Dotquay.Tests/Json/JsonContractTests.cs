using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;
using Xunit;

namespace Dotquay.Tests.Json;

/// <summary>
/// Pins the <c>--json</c> wire contract (SPEC.md §17) — the exact key names and enum spellings the
/// Godot plugin's GDScript parses.
/// </summary>
/// <remarks>
/// These assertions look pedantic on purpose. Every one of them is a rename that would compile
/// cleanly, pass every other test in this suite, and surface as an empty panel with no error — the
/// worst failure mode a two-language boundary has. The plugin's own fixture suite
/// (<c>src/Dotquay.Godot/tests/</c>) asserts the same shape from the other side; this file is what
/// stops the C# side drifting away from it silently.
/// </remarks>
public class JsonContractTests {
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string WriteGraph(GraphJson payload) =>
        DotquayJson.Write(JsonEnvelope<GraphJson>.Success("graph", payload));

    [Fact]
    public void Envelope_CarriesTheFieldsAConsumerBranchesOn() {
        var root = Parse(WriteGraph(new GraphJson { Source = "lockfile", ProjectDirectory = "/p" }));

        Assert.Equal(JsonEnvelope<GraphJson>.CurrentSchema, root.GetProperty("schema").GetInt32());
        Assert.Equal("dotquay", root.GetProperty("tool").GetString());
        Assert.Equal("graph", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("version").GetString()));
    }

    [Fact]
    public void GraphDotnetEntry_CarriesOriginRequiredByAndSource() {
        var payload = new GraphJson {
            Source = "lockfile",
            ProjectDirectory = "/p",
            Dotnet =
            [
                new DotnetJson
                {
                    Name = "Serilog",
                    Resolved = "3.1.1",
                    Origin = "transitive",
                    RequiredBy = ["a"],
                    Source = null,
                },
            ],
        };

        var entry = Parse(WriteGraph(payload)).GetProperty("data").GetProperty("dotnet")[0];

        // The plugin draws package edges and picks the right menu off exactly these keys (v0.19).
        Assert.Equal("transitive", entry.GetProperty("origin").GetString());
        Assert.Equal("a", entry.GetProperty("required_by")[0].GetString());
        // Present-but-null, never omitted (SPEC.md §17): "nuget.org" and "a dotquay too old to say"
        // must stay distinguishable.
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("source").ValueKind);
    }

    [Fact]
    public void FailureEnvelope_HasNullDataAndATypedError() {
        var json = DotquayJson.Write(JsonEnvelope<OperationJson>.Failure(
            "graph", new JsonError(JsonErrorKind.NoManifest, "No dotquay.toml found.")));

        var root = Parse(json);

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        // The panel keys its "Create dotquay.toml" button off this exact string, not off the message.
        Assert.Equal("no_manifest", root.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public void Properties_AreEmittedAsSnakeCase() {
        var payload = new GraphJson {
            Source = "lockfile",
            ProjectDirectory = "/p",
            Addons = [new GraphNodeJson { Name = "a", Git = "g", Path = "addons/a", Folder = "a", Origin = "root", IsOverridden = true }],
        };

        var root = Parse(WriteGraph(payload));
        var data = root.GetProperty("data");

        // GDScript reads dictionary keys literally; PascalCase would make every lookup return null.
        Assert.True(data.TryGetProperty("project_directory", out _));
        Assert.True(data.TryGetProperty("has_conflicts", out _));
        Assert.True(data.GetProperty("addons")[0].TryGetProperty("is_overridden", out _));
        Assert.True(data.GetProperty("addons")[0].TryGetProperty("source_kind", out _));
    }

    [Fact]
    public void OptionalFields_AreEmittedAsNullRatherThanOmitted() {
        var payload = new GraphJson {
            Source = "lockfile",
            ProjectDirectory = "/p",
            Addons = [new GraphNodeJson { Name = "a", Git = "g", Path = "addons/a", Folder = "a", Origin = "root" }],
        };

        var node = Parse(WriteGraph(payload)).GetProperty("data").GetProperty("addons")[0];

        // Present-but-null lets the consumer distinguish "dotquay says there is no version" from
        // "this dotquay is too old to have that field" — which an omitted key cannot.
        Assert.Equal(JsonValueKind.Null, node.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("resolved").ValueKind);
    }

    [Theory]
    [InlineData(AddonSourceKind.Version, "version")]
    [InlineData(AddonSourceKind.Branch, "branch")]
    [InlineData(AddonSourceKind.Rev, "rev")]
    public void SourceKind_MapsToItsManifestKeyword(AddonSourceKind kind, string expected) =>
        // The plugin turns this straight into a `dotquay override --<kind>` flag, so it must equal
        // the manifest keyword exactly.
        Assert.Equal(expected, JsonMapper.ToWire(kind));

    [Theory]
    [InlineData(AddonStatusKind.Clean, "clean")]
    [InlineData(AddonStatusKind.PatchedAsExpected, "patched")]
    [InlineData(AddonStatusKind.Modified, "modified")]
    [InlineData(AddonStatusKind.Missing, "missing")]
    [InlineData(AddonStatusKind.Linked, "linked")]
    public void StatusKind_MapsToItsBadgeText(AddonStatusKind kind, string expected) =>
        Assert.Equal(expected, JsonMapper.ToWire(kind));

    [Theory]
    [InlineData(DoctorIssueKind.GitNotOnPath, "git_not_on_path")]
    [InlineData(DoctorIssueKind.DanglingLockfileEntry, "dangling_lockfile_entry")]
    [InlineData(DoctorIssueKind.PluginCfgVersionDrift, "plugin_cfg_version_drift")]
    [InlineData(DoctorIssueKind.OrphanedAddonFolder, "orphaned_addon_folder")]
    [InlineData(DoctorIssueKind.UnreadablePackageManifest, "unreadable_package_manifest")]
    [InlineData(DoctorIssueKind.SshHostNotTrusted, "ssh_host_not_trusted")]
    [InlineData(DoctorIssueKind.AddonFolderIsGodotProject, "addon_folder_is_godot_project")]
    [InlineData(DoctorIssueKind.PackageAlsoBuiltFromSource, "package_also_built_from_source")]
    [InlineData(DoctorIssueKind.SourceBuildVersionMismatch, "source_build_version_mismatch")]
    public void DoctorIssueKind_MapsToASnakeCaseName(DoctorIssueKind kind, string expected) =>
        Assert.Equal(expected, JsonMapper.ToWire(kind));

    /// <summary>
    /// Every <see cref="DoctorIssueKind"/> maps to a real wire name.
    /// </summary>
    /// <remarks>
    /// The mapping ends in a <c>_ => "unknown"</c> arm, so a member added without a case compiles,
    /// runs, and ships -- silently reporting a kind no consumer can act on. The theory above only
    /// covers members somebody remembered to list, which is the same thing being forgotten twice.
    /// </remarks>
    [Fact]
    public void EveryDoctorIssueKind_HasAWireName() {
        var unmapped = Enum.GetValues<DoctorIssueKind>()
            .Where(kind => JsonMapper.ToWire(kind) == "unknown")
            .ToList();

        Assert.Empty(unmapped);
    }

    [Theory]
    [InlineData(GraphConflictKind.DifferentGitUrls, "different_git_urls")]
    [InlineData(GraphConflictKind.DisjointPins, "disjoint_pins")]
    [InlineData(GraphConflictKind.NoCommonVersion, "no_common_version")]
    [InlineData(GraphConflictKind.InvalidConstraint, "invalid_constraint")]
    [InlineData(GraphConflictKind.DisagreeingInstallFolders, "disagreeing_install_folders")]
    public void ConflictKind_MapsToASnakeCaseName(GraphConflictKind kind, string expected) {
        var conflict = new GraphConflict("core", kind, "message", [], []);

        Assert.Equal(expected, JsonMapper.ToJson(conflict).Kind);
    }

    [Fact]
    public void Conflict_CarriesItsRequirementsAndCandidates() {
        var conflict = new GraphConflict(
            "core",
            GraphConflictKind.NoCommonVersion,
            "no tag satisfies every constraint",
            [new GraphRequirement("a", AddonSourceKind.Version, "~1", "https://example.com/core.git")],
            [new GraphCandidate(AddonSourceKind.Version, "v1.9.0", "https://example.com/core.git", "abc", ["a", "b"], SatisfiesAll: true)]);

        var payload = new GraphJson {
            Source = "resolved",
            ProjectDirectory = "/p",
            Conflicts = [JsonMapper.ToJson(conflict)],
            HasConflicts = true,
        };

        var json = Parse(WriteGraph(payload)).GetProperty("data").GetProperty("conflicts")[0];

        Assert.Equal("core", json.GetProperty("name").GetString());
        Assert.Equal("a", json.GetProperty("requirements")[0].GetProperty("requirer").GetString());

        var candidate = json.GetProperty("candidates")[0];
        Assert.Equal("version", candidate.GetProperty("kind").GetString());
        Assert.Equal("v1.9.0", candidate.GetProperty("value").GetString());
        // The picker ranks on this, and shows the "satisfies" list as its justification.
        Assert.True(candidate.GetProperty("satisfies_all").GetBoolean());
        Assert.Equal(2, candidate.GetProperty("satisfies").GetArrayLength());
    }

    [Fact]
    public void ProjectRequirer_IsSpelledTheWayTheResolverSpellsIt() {
        var requirement = new GraphRequirement(
            GraphConflict.ProjectRequirerName, AddonSourceKind.Version, "~1", "https://example.com/core.git");

        // The plugin renders "<project>" as "this project"; a different spelling here would leak the
        // raw token into the UI.
        Assert.Equal("<project>", JsonMapper.ToJson(requirement).Requirer);
    }

    [Fact]
    public void ShortSha_IsSevenCharacters() {
        Assert.Equal("abcdef1", JsonMapper.ShortSha("abcdef1234567890"));
        Assert.Equal("abc", JsonMapper.ShortSha("abc"));
        Assert.Null(JsonMapper.ShortSha(null));
    }

    [Fact]
    public void EveryPayloadType_Serializes() {
        // Source generation registers one closed generic per payload (see DotquayJsonContext); a
        // missing registration compiles and only fails at runtime, inside the published AOT binary.
        // Touching all six here turns that into a test failure instead of a field bug report.
        Assert.Contains("\"list\"", DotquayJson.Write(JsonEnvelope<ListJson>.Success("list", new ListJson())));
        Assert.Contains("\"status\"", DotquayJson.Write(JsonEnvelope<StatusJson>.Success("status", new StatusJson())));
        Assert.Contains("\"outdated\"", DotquayJson.Write(JsonEnvelope<OutdatedJson>.Success("outdated", new OutdatedJson())));
        Assert.Contains("\"doctor\"", DotquayJson.Write(JsonEnvelope<DoctorJson>.Success("doctor", new DoctorJson())));
        Assert.Contains("\"add\"", DotquayJson.Write(JsonEnvelope<OperationJson>.Success("add", new OperationJson())));
        Assert.Contains("\"graph\"", WriteGraph(new GraphJson { Source = "lockfile", ProjectDirectory = "/p" }));
    }

    [Fact]
    public void OperationPayload_UsesTheDocumentedActionVocabulary() {
        var payload = new OperationJson {
            Changes =
            [
                new ChangeJson { Name = "a", Kind = JsonMapper.ChangeKind.Addon, Action = JsonMapper.ChangeAction.Added, To = "v1.0.0" },
                new ChangeJson { Name = "N", Kind = JsonMapper.ChangeKind.Dotnet, Action = JsonMapper.ChangeAction.Removed },
            ],
            Messages = ["done"],
        };

        var changes = Parse(DotquayJson.Write(JsonEnvelope<OperationJson>.Success("update", payload)))
            .GetProperty("data")
            .GetProperty("changes");

        Assert.Equal("addon", changes[0].GetProperty("kind").GetString());
        Assert.Equal("added", changes[0].GetProperty("action").GetString());
        Assert.Equal("dotnet", changes[1].GetProperty("kind").GetString());
        Assert.Equal("removed", changes[1].GetProperty("action").GetString());
    }

    [Fact]
    public void Warnings_SurviveIntoTheEnvelope() {
        var json = DotquayJson.Write(
            JsonEnvelope<OperationJson>.Success("add", new OperationJson(), ["no tags found — pinned to a commit"]));

        var warnings = Parse(json).GetProperty("warnings");

        // Without --json this hint is a yellow console line; a GUI user needs it just as much, so it
        // must not be dropped on the floor.
        Assert.Equal(1, warnings.GetArrayLength());
        Assert.Contains("no tags found", warnings[0].GetString());
    }
}
