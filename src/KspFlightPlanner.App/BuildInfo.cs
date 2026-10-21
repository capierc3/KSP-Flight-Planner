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

    // The SDK adds the whole commit id; seven characters are plenty to find it.
    private static string Shorten(string version) =>
        version.Split('+') is [var number, { Length: > 7 } commit] ? $"{number}+{commit[..7]}" : version;
}
