namespace KspFlightPlanner.App.ViewModels;

/// <summary>Lets the screens tell each other about changes (e.g. a custom location edited on the Locations tab).</summary>
public sealed class PlannerEvents
{
    /// <summary>Custom locations were added, edited or deleted. The argument is the screen that made the change.</summary>
    public event Action<object>? LocationsChanged;

    /// <summary>A screen asked to show a custom location in the Locations tab.</summary>
    public event Action<long>? OpenLocationRequested;

    /// <summary>A planning tab handed a plan to the Flight tab; the argument is the new flight's id.</summary>
    public event Action<long>? OpenFlightRequested;

    public void RaiseLocationsChanged(object sender) => LocationsChanged?.Invoke(sender);
    public void RequestOpenFlight(long flightId) => OpenFlightRequested?.Invoke(flightId);
    public void RequestOpenLocation(long locationId) => OpenLocationRequested?.Invoke(locationId);
}
