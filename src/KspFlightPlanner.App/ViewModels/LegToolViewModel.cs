namespace KspFlightPlanner.App.ViewModels;

/// <summary>A mission leg (or stop) a planner works on.</summary>
/// <param name="Owner">The mission stop the leg arrives at, to find the link again when the route changes.</param>
public abstract record LegLink(object Owner, string Title);

/// <summary>
/// A planner that also works on the mission (Transfer Planner, Rendezvous, Resonant Orbits). In leg mode it plans one of
/// the mission's legs: the inputs come from the leg and every plan made is saved back into the mission through the link.
/// Linked to the mission, it can also plan a new leg from where the mission is and add it after the last stop.
/// </summary>
public abstract class LegToolViewModel<TLink> : ObservableObject where TLink : LegLink
{
    protected LegToolViewModel()
    {
        StopEditingLegCommand = new RelayCommand(StopEditingLeg, () => IsEditingLeg);
        AddToMissionCommand = new RelayCommand(AddToMission, () => CanAddToMission);
    }

    // ---------------- Leg mode ----------------

    public RelayCommand StopEditingLegCommand { get; }

    /// <summary>The mission leg being planned; null when the planner is used on its own.</summary>
    public TLink? Leg { get; protected set; }

    public bool IsEditingLeg => Leg is not null;

    public string LegTitle => Leg?.Title ?? "";

    /// <summary>Plans the mission leg: inputs from the link, and every change made afterwards saved into the mission.</summary>
    public abstract void EditLeg(TLink link);

    /// <summary>Back to using the planner on its own; the mission keeps the plan last made.</summary>
    public virtual void StopEditingLeg()
    {
        Leg = null;
        NotifyLegChanged();
    }

    /// <summary>Properties that change with the leg (or with starting to add one).</summary>
    protected virtual IEnumerable<string> LegProperties =>
        [nameof(Leg), nameof(IsEditingLeg), nameof(LegTitle), nameof(ShowsAddToMission), nameof(CanAddToMission), nameof(AddToMissionText)];

    protected void NotifyLegChanged()
    {
        foreach (var name in LegProperties) OnPropertyChanged(name);
    }

    /// <summary>The mission's current link for the leg arriving at <paramref name="owner"/>, if it still has this tool.</summary>
    protected abstract TLink? FindLink(MissionBuilderViewModel mission, object owner);

    /// <summary>The leg changed under the planner (other bodies, orbits or craft), so it has to be set up again.</summary>
    protected abstract bool LegMoved(TLink shown, TLink current);

    /// <summary>The route changed: follow the leg being planned, or stop when the mission no longer has it.</summary>
    private void FollowLeg()
    {
        if (Leg is not { } shown || Mission is null) return;
        if (FindLink(Mission, shown.Owner) is not { } current) StopEditingLeg();
        else if (LegMoved(shown, current)) EditLeg(current);
    }

    /// <summary>The start is kept to a whole <paramref name="step"/>: rounded up, so a leg never starts before the mission gets there.</summary>
    protected static double RoundUp(double ut, double step) => Math.Ceiling(ut / step) * step;

    // ---------------- Adding to the mission ----------------

    public RelayCommand AddToMissionCommand { get; }

    protected MissionBuilderViewModel? Mission { get; private set; }

    public bool HasMission => Mission is not null;

    /// <summary>Links the planner to the mission, so what it plans can be added to it and its legs followed.</summary>
    public void AttachMission(MissionBuilderViewModel mission)
    {
        Mission = mission;
        mission.RouteChanged += () =>
        {
            OnRouteChanged();
            FollowLeg();
        };
        OnPropertyChanged(nameof(HasMission));
        OnPropertyChanged(nameof(ShowsAddToMission));
    }

    /// <summary>The mission's route changed; the leg being planned (if any) is followed afterwards.</summary>
    protected virtual void OnRouteChanged()
    {
        OnPropertyChanged(nameof(CanAddToMission));
        OnPropertyChanged(nameof(AddToMissionText));
    }

    /// <summary>Starts planning a leg to add, from where the mission is (its last stop).</summary>
    public abstract void StartAdding();

    /// <summary>The Add to mission card shows.</summary>
    public virtual bool ShowsAddToMission => Mission is not null && Leg is null;

    public abstract bool CanAddToMission { get; }

    /// <summary>What adding does, or why it can't.</summary>
    public abstract string AddToMissionText { get; }

    /// <summary>Adds what's planned to the mission after its last stop; the first new stop, or null if it couldn't.</summary>
    protected abstract StopViewModel? AddLegToMission();

    /// <summary>Adds the plan, then carries on planning the new leg in leg mode.</summary>
    private void AddToMission()
    {
        if (!CanAddToMission || AddLegToMission() is not { } stop) return;
        if (FindLink(Mission!, stop) is { } link) EditLeg(link);
    }
}
