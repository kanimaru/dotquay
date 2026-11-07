using Dotquay.Core.Abstractions;
using Dotquay.Core.Toml;
using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Auth;

/// <summary>
/// The shipping <see cref="ICredentialStore"/>: a plaintext TOML file at
/// <c>~/.dotquay/credentials.toml</c>, readable and writable only by its owner (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// <para>Plaintext, and said so out loud rather than dressed up. Base64 or a fixed-key XOR would
/// look like encryption while protecting against nothing, which is worse than being honest — anyone
/// who can read the file can read the token either way. What actually protects it is the file mode
/// and the fact that it lives outside every project directory, so it cannot be committed by
/// accident.</para>
/// <para>Read every time rather than cached: <c>dotquay login</c> in one terminal must take effect
/// in another without restarting anything, and the file is a few hundred bytes.</para>
/// </remarks>
public sealed class TomlCredentialStore : ICredentialStore {
    /// <summary>Bumped only if the file's shape changes incompatibly. A file claiming a version
    /// this build doesn't know is refused rather than half-read — the same rule as the JSON
    /// envelope's <c>schema</c> (SPEC.md §17), for the same reason.</summary>
    public const int CurrentVersion = 1;

    public const string FileName = "credentials.toml";

    private const string CredentialSection = "credential";

    private readonly IFileSystem _fileSystem;
    private readonly string _path;

    public TomlCredentialStore(IFileSystem fileSystem, string path) {
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>Where the file lives: <c>&lt;home&gt;/.dotquay/credentials.toml</c>, beside the
    /// shared fetch cache. Never inside a project — the value is one person's secret on one
    /// machine, and a project-local file is a file that eventually gets committed.</summary>
    public static string DefaultPath(IEnvironmentReader environment, IFileSystem fileSystem) {
        var home = environment.HomeDirectory
            ?? throw new CredentialException(
                "Could not determine your home directory, so there is nowhere to store "
                + "credentials. Set DOTQUAY_NUGET_USERNAME and DOTQUAY_NUGET_TOKEN instead.");

        return fileSystem.CombinePath(home, ".dotquay", FileName);
    }

    public string Path => _path;

    public Credential? Find(CredentialScope scope) {
        Credential? best = null;
        var bestSpecificity = -1;

        foreach (var (entryScope, credential) in ReadAll()) {
            // Longest matching path prefix wins. Ties are impossible: two entries with the same
            // host and the same path are the same TOML key, which TOML itself rejects.
            if (entryScope.Covers(scope) && entryScope.Specificity > bestSpecificity) {
                best = credential;
                bestSpecificity = entryScope.Specificity;
            }
        }

        return best;
    }

    public IReadOnlyList<StoredCredential> List() =>
        ReadAll()
            .Select(entry => new StoredCredential(entry.Scope.Key, entry.Credential.Username))
            .OrderBy(entry => entry.Scope, StringComparer.Ordinal)
            .ToList();

    public void Set(CredentialScope scope, Credential credential) {
        if (credential.Username.Length == 0 || credential.Token.Length == 0) {
            throw new CredentialException("A credential needs both a username and a token.");
        }

        var entries = ReadAll()
            .ToDictionary(entry => entry.Scope.Key, entry => entry, StringComparer.Ordinal);
        entries[scope.Key] = (scope, credential);
        Write(entries.Values);
    }

    public bool Remove(CredentialScope scope) {
        var entries = ReadAll().ToList();
        var remaining = entries.Where(entry =>
            !string.Equals(entry.Scope.Key, scope.Key, StringComparison.Ordinal)).ToList();

        if (remaining.Count == entries.Count) {
            return false;
        }

        Write(remaining);
        return true;
    }

    private List<(CredentialScope Scope, Credential Credential)> ReadAll() {
        var result = new List<(CredentialScope, Credential)>();

        if (!_fileSystem.FileExists(_path)) {
            return result;
        }

        TomlTable table;
        try {
            table = DotquayToml.Parse(_fileSystem.ReadAllText(_path), _path);
        } catch (TomlException ex) {
            throw new CredentialsFileException(
                $"{_path}: invalid TOML.{Environment.NewLine}{ex.Message}", ex);
        }

        if (table.TryGetValue("version", out var versionValue) && versionValue is long version
            && version > CurrentVersion) {
            throw new CredentialsFileException(
                $"{_path} was written by a newer dotquay (file version {version}, this build "
                + $"understands {CurrentVersion}). Refusing to read it rather than risk "
                + $"misinterpreting it.");
        }

        if (!table.TryGetValue(CredentialSection, out var raw)
            || raw is not TomlTable credentials) {
            return result;
        }

        foreach (var key in credentials.Keys) {
            if (credentials[key] is not TomlTable entry) {
                throw new CredentialsFileException($"{_path}: [{CredentialSection}.\"{key}\"] must "
                    + $"be a table.");
            }

            if (!CredentialScope.TryParse(key, out var scope, out var error)) {
                throw new CredentialsFileException($"{_path}: [{CredentialSection}.\"{key}\"] — "
                    + $"{error}");
            }

            var username = GetString(entry, "username");
            var token = GetString(entry, "token");
            if (username is null || token is null) {
                throw new CredentialsFileException(
                    $"{_path}: [{CredentialSection}.\"{key}\"] needs both 'username' and 'token'. "
                    + $"Run 'dotquay login {key}' to rewrite it.");
            }

            result.Add((scope, new Credential(username, token)));
        }

        return result;
    }

    private void Write(IEnumerable<(CredentialScope Scope, Credential Credential)> entries) {
        var ordered = entries.OrderBy(entry => entry.Scope.Key, StringComparer.Ordinal).ToList();

        var builder = new System.Text.StringBuilder();
        // Plain ASCII on purpose, matching the .gitignore writer's reasoning: this file gets opened
        // in whatever editor or shell is to hand, and a stray em-dash renders as mojibake in
        // anything reading it as ANSI.
        builder.AppendLine("# dotquay credentials - written by 'dotquay login'.");
        builder.AppendLine("# Secrets in plaintext: keep this file owner-readable and never copy "
            + "it into a project.");
        builder.AppendLine($"version = {CurrentVersion}");

        foreach (var (scope, credential) in ordered) {
            builder.AppendLine();
            builder.AppendLine($"[{CredentialSection}.{Quote(scope.Key)}]");
            builder.AppendLine($"username = {Quote(credential.Username)}");
            builder.AppendLine($"token = {Quote(credential.Token)}");
        }

        _fileSystem.WriteAllText(_path, builder.ToString());

        // After the write, not before: the file is created by WriteAllText, and a mode set on a
        // not-yet-existing path is a no-op that would leave the secret world-readable.
        _fileSystem.SetOwnerOnlyPermissions(_path);
    }

    /// <summary>Emits a TOML basic string. Tokens are opaque third-party strings — a GitLab PAT
    /// happens to be alphanumeric, but nothing guarantees the next provider's is, so this escapes
    /// rather than assuming.</summary>
    private static string Quote(string value) {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value) {
            switch (c) {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) {
                        builder.Append("\\u").Append(((int)c).ToString("X4"));
                    } else {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string? GetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s && s.Length > 0 ? s : null;
}
