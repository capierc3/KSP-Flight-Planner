namespace KspFlightPlanner.Core.Models;

/// <summary>A user-saved constellation plan.</summary>
public sealed record SavedConstellation
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public long BodyId { get; init; }
    public int SatelliteCount { get; init; }
    public double AltitudeM { get; init; }
    public bool PreferDiveOrbit { get; init; }
    public bool UseOcclusion { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedUtc { get; init; }

    /// <summary>Populated by queries that join to the body/system tables.</summary>
    public string? BodyName { get; init; }
    public string? SystemName { get; init; }

    public override string ToString() => Name;
}
