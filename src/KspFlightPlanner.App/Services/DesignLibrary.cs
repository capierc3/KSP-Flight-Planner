using System.ComponentModel;
using System.IO;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>A saved design and what its staging gives (null when the file couldn't be read).</summary>
public sealed record DesignEntry(CraftFileInfo File, DesignReport? Report)
{
    /// <summary>"Beyonder Mk1", or "KerbLink Mk2 (in the editor)" for the auto-saved ship.</summary>
    public string Title => Report is null ? Path.GetFileNameWithoutExtension(File.Path)
        : File.IsAutoSave ? $"{Report.Name} (in the editor)" : Report.Name;

    public override string ToString() => Title;
}

/// <summary>
/// The save's ship designs, read and flown once and shared by the Vessels pane and the mission's design check.
/// Refreshed when a .craft file changes (the Ships folder is watched) or the part data is read again.
/// </summary>
public sealed class DesignLibrary : ObservableObject
{
    private readonly SaveSync _sync;
    private IReadOnlyList<DesignEntry> _entries = [];

    public DesignLibrary(SaveSync sync)
    {
        _sync = sync;
        sync.PropertyChanged += OnChanged;
        sync.Parts.PropertyChanged += OnChanged;
        Refresh();
    }

    /// <summary>VAB first, the design open in the editor first within each, then by name.</summary>
    public IReadOnlyList<DesignEntry> Entries { get => _entries; private set => SetProperty(ref _entries, value); }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaveSync.Crafts) or nameof(PartData.Catalog)) Refresh();
    }

    private void Refresh()
    {
        var catalog = _sync.Parts.Catalog;
        var gravity = _sync.HomeSurfaceGravity;
        Entries = _sync.Crafts
            .OrderBy(c => c.Facility == "VAB" ? 0 : 1).ThenBy(c => c.IsAutoSave ? 0 : 1).ThenBy(c => Path.GetFileNameWithoutExtension(c.Path))
            .Select(c => new DesignEntry(c, Simulate(c, catalog, gravity)))
            .ToList();
    }

    private static DesignReport? Simulate(CraftFileInfo file, PartCatalog catalog, double gravity)
    {
        try
        {
            return StagingSimulation.Simulate(CraftReader.Read(file.Path), catalog, gravity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
