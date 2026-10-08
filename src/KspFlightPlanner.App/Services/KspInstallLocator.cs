using System.IO;
using System.Text.RegularExpressions;
using KspFlightPlanner.Core.Ksp;
using Microsoft.Win32;

namespace KspFlightPlanner.App.Services;

/// <summary>Finds a Steam install of Kerbal Space Program (any Steam library folder).</summary>
public static partial class KspInstallLocator
{
    private const string KspFolder = @"steamapps\common\Kerbal Space Program";

    public static string? FindSteamInstall()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return null;
            var libraries = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                libraries.AddRange(LibraryPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));

            return libraries
                .Select(lib => Path.GetFullPath(Path.Combine(lib.Replace('/', '\\'), KspFolder)))
                .FirstOrDefault(dir => KspSaveReader.CheckInstallFolder(dir) is null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    // libraryfolders.vdf lines look like:   "path"		"E:\\SteamLibrary"
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
