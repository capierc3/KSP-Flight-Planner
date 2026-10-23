using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>Whether the player can build with a part in the chosen save.</summary>
public enum PartStatus
{
    /// <summary>Researched (and bought, in career), or the save is a sandbox.</summary>
    Unlocked,
    /// <summary>Its tech node is in the research plan.</summary>
    Planned,
    /// <summary>Career: its node is researched but the part hasn't been bought.</summary>
    NotBought,
    /// <summary>Its node isn't researched or planned.</summary>
    Locked,
}

/// <summary>
/// Which parts the chosen save can use, and the player's research plan for it. What's researched is read from the save
/// each time KSP writes it (nothing is stored per save but the plan's node ids); the part data is shared by every save.
/// </summary>
public sealed class PartAvailability : ObservableObject
{
    private readonly SaveSync _sync;
    private readonly TechPlanRepository _plans;
    private SavedResearch? _research;
    private TechPlan _plan;

    public PartAvailability(SaveSync sync, TechPlanRepository plans, PlannerEvents events)
    {
        _sync = sync;
        _plans = plans;
        _research = sync.Save?.Research;
        _plan = NewPlan(_plans.Load());

        // The live overlay replaces Save every few seconds; only a newly read file brings new research.
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SaveSync.Save) && !ReferenceEquals(sync.Save?.Research, _research)) Refresh();
        };
        sync.Parts.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PartData.Catalog)) Refresh(); };
        events.SaveChanged += Refresh;
    }

    public PartCatalog Catalog => _sync.Parts.Catalog;

    /// <summary>The save's research; null in sandbox or with no save chosen (every part counts as unlocked).</summary>
    public SavedResearch? Research => _research;

    public TechPlan Plan => _plan;

    /// <summary>Raised when the research, the plan or the part data change.</summary>
    public event Action? Changed;

    public PartStatus StatusOf(KspPart part)
    {
        if (_research is null || _research.IsUnlocked(part)) return PartStatus.Unlocked;
        if (part.TechRequired is not { } tech) return PartStatus.Locked;
        return _research.ResearchedTech.Contains(tech) ? PartStatus.NotBought
            : _plan.Planned.Contains(tech) ? PartStatus.Planned
            : PartStatus.Locked;
    }

    /// <summary>Plans a tech node (with what it needs), or unplans it (with what relied on it), and stores the plan.</summary>
    public void TogglePlanned(string nodeId)
    {
        _plan.Toggle(nodeId);
        _plans.Save(_plan.Planned);
        Changed?.Invoke();
    }

    public void ClearPlan()
    {
        _plan = NewPlan([]);
        _plans.Save([]);
        Changed?.Invoke();
    }

    private void Refresh()
    {
        _research = _sync.Save?.Research;
        _plan = NewPlan(_plans.Load());
        OnPropertyChanged(nameof(Research));
        Changed?.Invoke();
    }

    private TechPlan NewPlan(IEnumerable<string> planned) =>
        new(Catalog.TechTree, _research?.ResearchedTech ?? new HashSet<string>(), planned);
}
