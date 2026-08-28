using System.Text;

namespace Dotquay.Core.Lockfile;

/// <summary>Serializes a <see cref="DotquayLockfile"/> back to TOML text, sorted deterministically
/// by name so re-running the resolver on an unchanged manifest produces a byte-identical lockfile
/// (no incidental diffs for teammates to review).</summary>
public static class LockfileWriter
{
    public static string Serialize(DotquayLockfile lockfile)
    {
        var sb = new StringBuilder();

        foreach (var addon in lockfile.Addons.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            StartBlock(sb);
            sb.AppendLine("[[addon]]");
            sb.AppendLine($"name = {Quote(addon.Name)}");
            sb.AppendLine($"git = {Quote(addon.Git)}");
            sb.AppendLine($"resolved = {Quote(addon.Resolved)}");

            if (addon.Version is not null)
            {
                sb.AppendLine($"version = {Quote(addon.Version)}");
            }

            sb.AppendLine($"path = {Quote(addon.Path)}");

            // Written only when it differs from the name, so adding this field doesn't churn the
            // lockfile of every project that never needed it.
            if (addon.Folder is not null && addon.Folder != addon.Name)
            {
                sb.AppendLine($"folder = {Quote(addon.Folder)}");
            }

            if (addon.ContentHash is not null)
            {
                sb.AppendLine($"content_hash = {Quote(addon.ContentHash)}");
            }

            if (addon.PatchHash is not null)
            {
                sb.AppendLine($"patch_hash = {Quote(addon.PatchHash)}");
            }

            if (addon.PatchBaseSha is not null)
            {
                sb.AppendLine($"patch_base_sha = {Quote(addon.PatchBaseSha)}");
            }

            if (addon.Dependencies.Count > 0)
            {
                sb.AppendLine($"dependencies = [{string.Join(", ", addon.Dependencies.Select(Quote))}]");
            }
        }

        foreach (var dependency in lockfile.Dotnet.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            StartBlock(sb);
            sb.AppendLine("[[dotnet]]");
            sb.AppendLine($"name = {Quote(dependency.Name)}");
            sb.AppendLine($"resolved = {Quote(dependency.Resolved)}");

            // Omitted for nuget.org, so a public-only project's lockfile is byte-identical to what
            // pre-v0.7 dotquay wrote and upgrading produces no diff.
            if (dependency.Source is not null)
            {
                sb.AppendLine($"source = {Quote(dependency.Source)}");
            }
        }

        return sb.ToString();
    }

    private static void StartBlock(StringBuilder sb)
    {
        if (sb.Length > 0)
        {
            sb.AppendLine();
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
