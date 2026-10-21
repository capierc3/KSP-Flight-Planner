using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>
/// The Edit flight log dialog: which rocket a flight is filed under (its flights are grouped and compared by that name),
/// and which of the save's vessels it is — for a vessel renamed once it got where it was going.
/// </summary>
public sealed class FlightLogEditViewModel : ObservableObject
{
    private readonly FlightLogSummary _log;
    private readonly Action<long, string, string?> _retag;
    private string _rocket;
    private Choice<string?> _vessel;

    /// <param name="save">The save's vessels to pick from (null when no save is read).</param>
    /// <param name="rockets">Rocket names already in the log, to pick from.</param>
    /// <param name="retag">Stores the edit: log id, rocket, vessel tag.</param>
    public FlightLogEditViewModel(FlightLogSummary log, KspSave? save, IEnumerable<string> rockets, Action<long, string, string?> retag)
    {
        _log = log;
        _retag = retag;
        _rocket = log.VesselName;
        Rockets = rockets.Order(StringComparer.CurrentCultureIgnoreCase).ToList();

        var found = save is null ? null : FlightLogs.VesselOf(log with { VesselId = null }, save);
        var choices = new List<Choice<string?>>
        {
            new(found is { } f ? $"Found by launch time: {f.Name}" : "Found by launch time (none found now)", null),
            new("None of them", FlightLogs.NoVessel),
        };
        if (save is not null)
        {
            var candidates = FlightLogs.Candidates(log, save)
                .Where(v => SaveImport.IsUsuallyWanted(v) || v.Id == found?.Id || v.Id == log.VesselId);
            choices.AddRange(candidates.Select(v => new Choice<string?>(Describe(v), v.Id)));
        }
        // A tag to a vessel that's no longer in the save stays as it is unless changed.
        if (log.VesselId is { Length: > 0 } id && choices.All(c => c.Value != id))
            choices.Add(new("A vessel no longer in the save", id));
        VesselChoices = choices;
        _vessel = choices.First(c => c.Value == log.VesselId);

        SaveCommand = new RelayCommand(Save, () => !string.IsNullOrWhiteSpace(_rocket));
    }

    public string Header => "Edit flight log";

    public string Subheader => (_log.FromLaunch ? "Launched " : "Joined in flight · launched ") +
                               GameCalendar.Kerbin.FormatDate(_log.LaunchUt) + " · logged as " + _log.VesselName;

    /// <summary>The rocket the flight is filed under: flights of the same rocket are grouped and compared together.</summary>
    public string Rocket { get => _rocket; set => SetProperty(ref _rocket, value ?? ""); }

    public IReadOnlyList<string> Rockets { get; }

    public IReadOnlyList<Choice<string?>> VesselChoices { get; }

    public Choice<string?> Vessel { get => _vessel; set { if (value is not null) SetProperty(ref _vessel, value); } }

    public RelayCommand SaveCommand { get; }

    /// <summary>Closes the dialog; the window sets it.</summary>
    public Action Close { get; set; } = () => { };

    private void Save()
    {
        _retag(_log.Id, _rocket.Trim(), _vessel.Value);
        Close();
    }

    private static string Describe(SavedVessel v) =>
        $"{v.Name} · {SaveImport.TypeText(v.Type)} · {VesselsViewModel.Situation(v.Situation)} {v.Orbit?.BodyName}".TrimEnd() +
        (v.LaunchUt > 0 ? $" · launched {GameCalendar.Kerbin.FormatDay(v.LaunchUt)}" : "");
}
