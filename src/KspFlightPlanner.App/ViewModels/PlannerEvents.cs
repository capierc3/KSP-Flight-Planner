namespace KspFlightPlanner.App.ViewModels;

/// <summary>Lets the screens tell each other about changes (e.g. a custom location edited on the Locations pane).</summary>
public sealed class PlannerEvents
{
    /// <summary>Custom locations were added, edited or deleted. The argument is the screen that made the change.</summary>
    public event Action<object>? LocationsChanged;

    /// <summary>
    /// Another KSP save was chosen: its locations, missions, flights and constellations replace the last save's.
    /// Raised after <see cref="LocationsChanged"/>.
    /// </summary>
    public event Action? SaveChanged;

    /// <summary>A body's surface bake was saved (Tools › Bake Surface Data); the argument is the body's name.</summary>
    public event Action<string>? SurfaceDataChanged;

    /// <summary>A planning pane handed a plan to the Flight pane; the argument is the new flight's id.</summary>
    public event Action<long>? OpenFlightRequested;

    public void RaiseLocationsChanged(object sender) => LocationsChanged?.Invoke(sender);
    public void RaiseSurfaceDataChanged(string bodyName) => SurfaceDataChanged?.Invoke(bodyName);
    public void RaiseSaveChanged() => SaveChanged?.Invoke();
    public void RequestOpenFlight(long flightId) => OpenFlightRequested?.Invoke(flightId);
}
