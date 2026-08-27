namespace Dotquay.Core.Dotnet;

/// <summary>Thrown by the <c>.csproj</c> sync path (locating the project file, editing
/// <c>PackageReference</c>s, running <c>dotnet restore</c>) for anything that should stop a sync and
/// be shown to the user as-is.</summary>
public sealed class DotnetSyncException : Exception
{
    public DotnetSyncException(string message)
        : base(message)
    {
    }

    public DotnetSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
