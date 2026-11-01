using System.Reflection;

namespace KspFlightPlanner.App;

/// <summary>
/// Which build is running: the version set in Directory.Build.props, with the commit it was built from after the "+"
/// ("0.2.0+0dc323a…", added by the .NET SDK).
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly App = Assembly.GetExecutingAssembly();

    /// <summary>The version and, when known, the commit ("0.2.0+0dc323a").</summary>
    public static string Version { get; } = Shorten(
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? App.GetName().Version?.ToString(3) ?? "");

    /// <summary>The branch it was built from, or "" when unknown (no git, or a detached CI checkout).</summary>
    public static string Branch { get; } = App.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GitBranch")?.Value is { } b && b != "HEAD" ? b.Trim() : "";

    /// <summary>
    /// For the window title: just the number ("0.2.0") on main, develop or an unknown branch; the number and
    /// commit ("0.2.0+0dc323a") on any other branch, so a test build is easy to tell apart.
    /// </summary>
    public static string TitleVersion { get; } =
        Branch is "" or "main" or "develop" ? Version.Split('+')[0] : Version;

    // The SDK adds the whole commit id; seven characters are plenty to find it.
    private static string Shorten(string version) =>
        version.Split('+') is [var number, { Length: > 7 } commit] ? $"{number}+{commit[..7]}" : version;
}
