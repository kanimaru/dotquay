namespace Dotquay.Core.Local;

/// <summary>
/// One <c>[dotnet.*]</c> dependency redirected to a <c>.csproj</c> on this machine
/// (docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md).
/// </summary>
/// <param name="PackageId">The NuGet package ID as the manifest and lockfile spell it.</param>
/// <param name="ProjectPath">Absolute path to the linked <c>.csproj</c> itself, not its folder —
/// resolved once at link time so a later read never has to guess which project a folder meant.</param>
/// <param name="Analyzer">Whether the linked project is a Roslyn analyzer or source generator.</param>
/// <remarks>
/// <para>
/// <paramref name="Analyzer"/> expands to <c>OutputItemType="Analyzer"</c> and
/// <c>ReferenceOutputAssembly="false"</c>. A preset is right here for the reason it was wrong in
/// v0.10: those two attributes always travel together for a generator-as-project-reference, and there
/// is no established shape wanting one without the other — unlike <c>PrivateAssets</c>, where gdUnit4
/// deliberately uses the opposite of the conventional value.
/// </para>
/// <para>
/// Getting it wrong is silent in the worst way: without <c>OutputItemType="Analyzer"</c> MSBuild
/// treats the built DLL as an ordinary reference, the generator never runs, and nothing is reported
/// — the build simply produces less code than it should.
/// </para>
/// </remarks>
public sealed record DotnetLink(string PackageId, string ProjectPath, bool Analyzer = false);
