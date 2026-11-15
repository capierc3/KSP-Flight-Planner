using System.IO;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>The install's drag data, read once per change of PartDatabase.cfg (KSP rewrites it each time it loads).</summary>
public static class AeroDataCache
{
    private static (string Key, AeroData? Data)? _cached;

    public static async Task<AeroData?> GetAsync(string? install)
    {
        if (install is null) return null;
        var file = Path.Combine(install, AeroDataReader.PartDatabaseFile);
        var key = $"{install}|{(File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0)}";
        if (_cached is { } cached && cached.Key == key) return cached.Data;
        var data = await Task.Run(() => AeroDataReader.Read(install));
        _cached = (key, data);
        return data;
    }
}
