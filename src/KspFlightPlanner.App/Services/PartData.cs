using System.IO;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// The part catalog (masses, engines, antennas, resource densities) read from the KSP install and stored in the
/// database. Read again automatically when GameData changes (ModuleManager rewrites its cache when mods change).
/// </summary>
public sealed class PartData : ObservableObject
{
    private readonly PartCatalogRepository _repository;
    private PartCatalog _catalog;
    private PartCatalogInfo? _info;
    private bool _isReading;
    private string? _problem;

    public PartData(PartCatalogRepository repository)
    {
        _repository = repository;
        _catalog = repository.Load();
        _info = repository.GetInfo();
    }

    public PartCatalog Catalog { get => _catalog; private set => SetProperty(ref _catalog, value); }

    public bool IsReading { get => _isReading; private set { if (SetProperty(ref _isReading, value)) OnPropertyChanged(nameof(Summary)); } }

    public string? Problem { get => _problem; private set { if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>E.g. "483 parts (48 engines, 42 antennas) from ModuleManager.ConfigCache, read 10/7/2026 10:30 PM".</summary>
    public string Summary =>
        IsReading ? "Reading part data…"
        : Problem is not null ? Problem
        : _info is null || Catalog.IsEmpty ? "No part data yet — choose your KSP folder."
        : $"{Catalog.Parts.Count:N0} parts ({Catalog.Parts.Values.Count(p => p.Engines.Count > 0)} engines, " +
          $"{Catalog.Parts.Values.Count(p => p.Antennas.Count > 0)} antennas) from {Path.GetFileName(_info.Source)}, " +
          $"read {_info.ReadUtc.ToLocalTime():g}";

    /// <summary>Reads the part data if there is none, it came from another install, or GameData changed since.</summary>
    public Task EnsureCurrentAsync(string? installPath)
    {
        if (installPath is null || KspSaveReader.CheckInstallFolder(installPath) is not null) return Task.CompletedTask;
        var source = PartCatalogReader.Source(installPath);
        if (source is null) return Task.CompletedTask;
        var upToDate = !Catalog.IsEmpty && _info is { } info
            && string.Equals(info.Source, source.Value.Path, StringComparison.OrdinalIgnoreCase)
            && info.SourceWriteUtc >= source.Value.LastWriteUtc;
        return upToDate ? Task.CompletedTask : ReadAsync(installPath);
    }

    /// <summary>Reads the part data from the install now and stores it.</summary>
    public async Task ReadAsync(string installPath)
    {
        if (IsReading) return;
        IsReading = true;
        try
        {
            var catalog = await Task.Run(() =>
            {
                var source = PartCatalogReader.Source(installPath) ?? throw new DirectoryNotFoundException("No GameData folder in the KSP folder.");
                var read = PartCatalogReader.Read(installPath);
                _repository.Replace(read, source.Path, source.LastWriteUtc);
                return read;
            });
            _info = _repository.GetInfo();
            Problem = null;
            Catalog = catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"Couldn't read part data: {ex.Message}";
        }
        finally
        {
            IsReading = false;
            OnPropertyChanged(nameof(Summary));
        }
    }
}
