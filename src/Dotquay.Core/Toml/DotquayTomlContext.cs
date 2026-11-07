using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Dotquay.Core.Toml;

/// <summary>
/// Source-generated Tomlyn metadata for the one type dotquay deserializes TOML into: the untyped
/// <see cref="TomlTable"/> model, which every reader then maps to its own records by hand. Source
/// generation is what keeps this trim- and NativeAOT-safe: the generic
/// <c>TomlSerializer.Deserialize&lt;T&gt;(string, TomlSerializerOptions)</c> overload resolves its
/// converter through reflection (IL2026/IL3050), and the Tomlyn package turns reflection off by
/// default in a <c>PublishAot</c> build, the same class of failure that forced Spectre.Console.Cli and
/// NuGet.Protocol out of this project (docs/SPRINT-01.md). Read TOML through <see cref="DotquayToml"/>
/// rather than calling <c>TomlSerializer</c> directly.
/// </summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class DotquayTomlContext : TomlSerializerContext {
    /// <summary>A context carrying per-call options. The generator only emits <c>Default</c>, built
    /// on <see cref="TomlSerializerOptions.Default"/>, but a reader needs its own
    /// <see cref="TomlSerializerOptions.SourceName"/> so parse errors name the file they came
    /// from.</summary>
    public DotquayTomlContext(TomlSerializerOptions options)
        : base(options) {
    }
}

/// <summary>The read side of TOML: one entry point, so no caller has to pick a type info itself.</summary>
public static class DotquayToml {
    /// <summary>
    /// Parses <paramref name="text"/> into the untyped model. <paramref name="sourceName"/> is the
    /// file name Tomlyn puts in front of every error position (null leaves errors unprefixed, which is
    /// what the reflection overload without options did). Invalid TOML throws
    /// <see cref="TomlException"/>; an empty document yields an empty table, never null.
    /// </summary>
    public static TomlTable Parse(string text, string? sourceName) {
        var options = new TomlSerializerOptions { SourceName = sourceName };
        var context = new DotquayTomlContext(options);
        return TomlSerializer.Deserialize(text, context.TomlTable) ?? new TomlTable();
    }
}
