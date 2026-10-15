using System.Reflection;

namespace KspFlightPlanner.App;

/// <summary>
/// Which build is running. The version comes from git at build time (Directory.Build.targets): a tagged
/// release is "1.0.0", a develop build "1.0.2", a feature-branch build "1.0.2-SNAPSHOT", each with the branch and
/// commit after the "+" ("1.0.2-SNAPSHOT+fly-this-mission.0dc323a").
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly App = Assembly.GetExecutingAssembly();

    /// <summary>The full version: number, branch and commit ("1.0.2-SNAPSHOT+fly-this-mission.0dc323a").</summary>
    public static string Version { get; } =
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? App.GetName().Version?.ToString(3) ?? "";

    /// <summary>The version without the branch and commit ("1.0.2-SNAPSHOT").</summary>
    public static string ShortVersion { get; } = Version.Split('+')[0];

    /// <summary>"release", "main", "develop" or "snapshot" (a feature branch).</summary>
    public static string Channel { get; } = Metadata("BuildChannel") ?? "release";

    /// <summary>The branch it was built from, or null if unknown.</summary>
    public static string? Branch { get; } = Metadata("BuildBranch");

    /// <summary>What the window title shows for a non-release build ("develop 1.0.2", "fly-this-mission 1.0.2-SNAPSHOT"), or null for a release.</summary>
    public static string? Label { get; } = Channel == "release" ? null : $"{Branch ?? Channel} {ShortVersion}";

    private static string? Metadata(string key) =>
        App.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;
}
