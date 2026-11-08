namespace Dotquay.Core.Sync;

/// <summary>A patch an install found redundant: upstream <paramref name="Version"/> contains
/// all of it. Its file goes once the lockfile records the addon unpatched.</summary>
internal sealed record DroppedPatch(string Name, string PatchFilePath, string Version);
