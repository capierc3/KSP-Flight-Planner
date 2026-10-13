using System.Reflection;

namespace KspFlightPlanner.App;

/// <summary>
/// Which build is running. A test build of a branch is published with a label in its informational version
/// ("0.1.0-fly-mode-trips+a99faf9"), so it can be told apart from the main build.
/// </summary>
public static class BuildInfo
{
    /// <summary>The full version: number, label and commit ("0.1.0-fly-mode-trips+a99faf9").</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    /// <summary>The test build's label ("fly-mode-trips"), or null for a main build.</summary>
    public static string? Label { get; } = Version.Split('+')[0] is var v && v.IndexOf('-') is var dash and >= 0 ? v[(dash + 1)..] : null;
}
