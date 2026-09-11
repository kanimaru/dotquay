using System.Text.Json.Serialization;

namespace Dotquay.Core.SelfUpdate;

// Wire shapes for the release channel, kept together so the whole contract with CI is readable in
// one file. Source-generated, like every other JSON path in this project: reflection-based
// System.Text.Json is stripped by the trimmer under NativeAOT (SPEC.md §12), and a deserializer
// registered nowhere fails only in the published binary.

/// <summary>The subset of GitLab's release payload this channel reads.</summary>
internal sealed class GitLabRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("released_at")]
    public DateTimeOffset? ReleasedAt { get; set; }

    [JsonPropertyName("assets")]
    public GitLabReleaseAssets? Assets { get; set; }
}

internal sealed class GitLabReleaseAssets
{
    [JsonPropertyName("links")]
    public List<GitLabReleaseLink> Links { get; set; } = [];
}

internal sealed class GitLabReleaseLink
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

/// <summary>
/// The per-version document CI publishes beside the binaries, and the only place a checksum comes
/// from.
/// </summary>
/// <remarks>
/// Immutable by construction: it is uploaded to the generic package registry under the version it
/// describes and never overwritten, so there is no mutable "latest" document for a publish to race.
/// "What is newest" is answered by the releases API, which is already the authority on what shipped.
/// </remarks>
internal sealed class ReleaseManifestDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("released_at")]
    public DateTimeOffset? ReleasedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAssetDocument> Assets { get; set; } = [];
}

internal sealed class ReleaseAssetDocument
{
    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// The body GitLab returns with a refusal.
/// </summary>
/// <remarks>
/// Worth parsing rather than discarding, because a fine-grained personal access token's refusal
/// names the exact permission it is missing ("[Release: Read]") in <c>error_description</c>. Without
/// it, every 403 looks the same and the advice can only be a guess - which is how an afternoon goes
/// into "the token has no api scope" when the token is fine and one checkbox is not ticked.
/// </remarks>
internal sealed class GitLabApiError
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    /// <summary>GitLab's older shape. A string here most of the time, but an object on validation
    /// errors - which is why every read of this type is wrapped in a catch.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>What the last update check found, cached at <c>~/.dotquay/update-check.json</c> so the
/// notice costs a file read rather than a request (docs/DESIGN-v0.17-SELF-UPDATE.md).</summary>
public sealed class UpdateCheckState
{
    [JsonPropertyName("checked_at")]
    public DateTimeOffset CheckedAt { get; set; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }

    /// <summary>Which channel answered. A cache written against one channel says nothing about
    /// another, so changing <c>[update] channel</c> invalidates it rather than producing a notice
    /// about a version that does not exist where the user is now looking.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<GitLabRelease>))]
[JsonSerializable(typeof(GitLabRelease))]
[JsonSerializable(typeof(ReleaseManifestDocument))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(GitLabApiError))]
internal sealed partial class SelfUpdateJsonContext : JsonSerializerContext
{
}
