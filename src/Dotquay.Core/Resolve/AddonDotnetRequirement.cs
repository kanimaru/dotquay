namespace Dotquay.Core.Resolve;

/// <summary>
/// One addon's <c>[dotnet.*]</c> range on a NuGet package, kept per declarer so <c>update</c> can
/// select a version inside all of them and name each one when it can't (DESIGN-v0.21 §3.5).
/// </summary>
public sealed record AddonDotnetRequirement(string AddonName, string Version);
