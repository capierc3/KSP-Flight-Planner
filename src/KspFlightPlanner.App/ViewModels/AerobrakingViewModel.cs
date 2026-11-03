using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A craft the pane can brake with: a vessel in the save as it is now, or a design's last stage.</summary>
public sealed record AeroCraftChoice(string Name, string Detail, SavedVessel? Vessel, CraftFileInfo? Design)
{
    public override string ToString() => Name;
}

/// <summary>An attitude to hold through the air, for the picker.</summary>
public sealed record AttitudeChoice(AeroAttitude Attitude, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Coming in on an orbit (by its apoapsis) or from far away (by the speed left over).</summary>
public sealed record ArrivalChoice(bool FromFarAway, string Name)
{
    public override string ToString() => Name;
}

/// <summary>How to choose the periapsis, for the picker.</summary>
public sealed record AimChoice(AerobrakeAim Aim, string Name, string Hint)
{
    public override string ToString() => Name;
}

/// <summary>One row of the pass table, ready to show.</summary>
/// <param name="IsStop">The pass to stop after (burn at the apoapsis that follows).</param>
public sealed record PassRow(string Number, string Enters, string Periapsis, string Lowest, string SpeedLost, string PeakG, string ApoapsisAfter,
    string PeriapsisAfter, string NextPass, bool ComesDown, bool IsStop = false);

/// <summary>One periapsis height on the comparison table.</summary>
public sealed record HeightRow(double PeriapsisM, string Periapsis, string FirstPass, string Passes, string PeakG, bool IsPicked);

/// <summary>
/// The Aerobraking pane: pick a craft and an orbit dipping into the air, and see what each pass does to it (docs/MATH.md §9).
/// Drag comes from the craft's parts (KSP's drag cubes) unless a drag area is typed in.
/// </summary>
public sealed class AerobrakingViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;
    private readonly SaveSync _sync;
    private CelestialBody? _body;
    private AeroCraftChoice? _craft;
    private AttitudeChoice _attitude;
    private ArrivalChoice _arrival;
    private string _massText = "";
    private string _dragText = "";
    private string _periapsisText = "45";
    private string _apoapsisText = "11400";
    private string _inclinationText = "0";
    private AimChoice _aim;
    private string _targetApText = "";
    private string _gentleApText = "";
    private string _targetPeText = "";
    private string _maxPassesText = "10";
    private string _suggestion = "";
    private string _burns = "";
    private string _dragNote = "";
    private string _summary = "";
    private string _status = "";
    private CraftAero? _shape;
    private bool _noDragData;
    private int _run;

    public AerobrakingViewModel(BodyRepository bodies, SaveSync sync)
    {
        _sync = sync;
        var stock = bodies.GetSystems().FirstOrDefault(s => s.Name == BodyRepository.StockSystemName) ?? bodies.GetSystems().FirstOrDefault();
        Bodies = (stock is null ? [] : bodies.GetBodies(stock.Id)).Where(b => b.HasAtmosphere && b.BodyClass != "Star").ToList();
        _body = Bodies.FirstOrDefault(b => b.IsDefault) ?? Bodies.FirstOrDefault();
        Attitudes =
        [
            new(AeroAttitude.EnginesFirst, "Engines first (hold retrograde)"),
            new(AeroAttitude.NoseFirst, "Nose first (hold prograde)"),
            new(AeroAttitude.SideOn, "Side-on (most drag)"),
        ];
        _attitude = Attitudes[0];
        Arrivals = [new(false, "an orbit, with apoapsis at"), new(true, "a flyby from far away, at speed")];
        _arrival = Arrivals[0];
        Aims =
        [
            new(AerobrakeAim.Own, "My own periapsis", "Type the periapsis, or click a height on the comparison table."),
            new(AerobrakeAim.FewestPasses, "Fewest passes", "As low as is safe: the most braking each pass."),
            new(AerobrakeAim.Gentlest, "Gentlest", "As high as still gets the apoapsis down in time: the least drag each pass."),
            new(AerobrakeAim.TargetOrbit, "Target orbit", "Brake down towards an orbit, then burn at apoapsis to finish it."),
        ];
        _aim = Aims[0];
        ResetTargets();

        UseOrbitCommand = new RelayCommand(UseCraftOrbit, () => _craft?.Vessel?.Orbit is { } o && o.BodyName == _body?.Name);
        PickPeriapsisCommand = new RelayCommand<HeightRow>(row =>
        {
            if (row is null) return;
            _periapsisText = (row.PeriapsisM / 1000).ToString("0.#", C);
            OnPropertyChanged(nameof(PeriapsisText));
            Aim = Aims[0]; // a height picked by hand
            Recalculate();
        });
        sync.PropertyChanged += OnSyncChanged;
        RefreshCrafts();
        Recalculate();
    }

    public IReadOnlyList<CelestialBody> Bodies { get; }
    public IReadOnlyList<AttitudeChoice> Attitudes { get; }
    public IReadOnlyList<ArrivalChoice> Arrivals { get; }
    public IReadOnlyList<AimChoice> Aims { get; }
    public ObservableCollection<AeroCraftChoice> Crafts { get; } = [];
    public ObservableCollection<PassRow> Passes { get; } = [];
    public ObservableCollection<HeightRow> Compare { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    public RelayCommand UseOrbitCommand { get; }
    public RelayCommand<HeightRow> PickPeriapsisCommand { get; }

    public CelestialBody? Body
    {
        get => _body;
        set
        {
            if (!SetProperty(ref _body, value)) return;
            if (value is not null && Parse(_periapsisText) is { } pe && pe * 1000 >= value.AtmosphereHeightM)
            {
                _periapsisText = Math.Round(value.AtmosphereHeightM * 0.65 / 1000).ToString(C);
                OnPropertyChanged(nameof(PeriapsisText));
            }
            ResetTargets();
            Recalculate();
        }
    }

    public AeroCraftChoice? Craft
    {
        get => _craft;
        set
        {
            if (!SetProperty(ref _craft, value)) return;
            _ = LoadCraftAsync(value);
        }
    }

    public AttitudeChoice Attitude { get => _attitude; set { if (SetProperty(ref _attitude, value)) { UpdateDragNote(); Recalculate(); } } }

    public ArrivalChoice Arrival
    {
        get => _arrival;
        set
        {
            if (!SetProperty(ref _arrival, value)) return;
            OnPropertyChanged(nameof(ArrivalUnit));
            ApoapsisText = value.FromFarAway ? "1000" : "11400";
        }
    }

    /// <summary>"km" for an apoapsis, "m/s" for the speed left over from far away.</summary>
    public string ArrivalUnit => _arrival.FromFarAway ? "m/s" : "km";

    /// <summary>The craft's mass in tonnes; filled from the craft, and can be changed.</summary>
    public string MassText { get => _massText; set { if (SetProperty(ref _massText, value)) Recalculate(); } }

    /// <summary>A drag area (m²) to use instead of the parts'; blank to use the parts.</summary>
    public string DragText { get => _dragText; set { if (SetProperty(ref _dragText, value)) Recalculate(); } }

    public string PeriapsisText { get => _periapsisText; set { if (SetProperty(ref _periapsisText, value)) Recalculate(); } }
    public string ApoapsisText { get => _apoapsisText; set { if (SetProperty(ref _apoapsisText, value)) Recalculate(); } }
    public string InclinationText { get => _inclinationText; set { if (SetProperty(ref _inclinationText, value)) Recalculate(); } }

    /// <summary>How the periapsis is chosen.</summary>
    public AimChoice Aim
    {
        get => _aim;
        set
        {
            if (!SetProperty(ref _aim, value)) return;
            OnPropertyChanged(nameof(IsOwnAim));
            OnPropertyChanged(nameof(IsPeriapsisWorkedOut));
            OnPropertyChanged(nameof(ShowsTargetApoapsis));
            OnPropertyChanged(nameof(ShowsTargetPeriapsis));
            OnPropertyChanged(nameof(TargetApoapsisLabel));
            OnPropertyChanged(nameof(TargetApoapsisText));
            if (value.Aim != AerobrakeAim.Own) Recalculate();
        }
    }

    /// <summary>The periapsis is typed in (else it's worked out for the aim).</summary>
    public bool IsOwnAim => _aim.Aim == AerobrakeAim.Own;

    /// <summary>The periapsis box shows the worked-out height (not for typing).</summary>
    public bool IsPeriapsisWorkedOut => !IsOwnAim;

    public bool ShowsTargetApoapsis => _aim.Aim is AerobrakeAim.Gentlest or AerobrakeAim.TargetOrbit;
    public bool ShowsTargetPeriapsis => _aim.Aim == AerobrakeAim.TargetOrbit;
    public string TargetApoapsisLabel => _aim.Aim == AerobrakeAim.Gentlest ? "Bring the apoapsis down to (km)" : "Target apoapsis (km)";

    /// <summary>The apoapsis to get down to: gentlest and target orbit each keep their own.</summary>
    public string TargetApoapsisText
    {
        get => _aim.Aim == AerobrakeAim.Gentlest ? _gentleApText : _targetApText;
        set
        {
            if (value == TargetApoapsisText) return;
            if (_aim.Aim == AerobrakeAim.Gentlest) _gentleApText = value; else _targetApText = value;
            OnPropertyChanged();
            Recalculate();
        }
    }
    public string TargetPeriapsisText { get => _targetPeText; set { if (SetProperty(ref _targetPeText, value)) Recalculate(); } }

    /// <summary>The most passes to take (gentlest, target orbit).</summary>
    public string MaxPassesText { get => _maxPassesText; set { if (SetProperty(ref _maxPassesText, value)) Recalculate(); } }

    /// <summary>Why this periapsis (for an aim); empty for your own.</summary>
    public string Suggestion { get => _suggestion; private set => SetProperty(ref _suggestion, value); }

    /// <summary>The burns after the last pass, and what they save.</summary>
    public string Burns { get => _burns; private set => SetProperty(ref _burns, value); }

    /// <summary>
    /// Target orbit: a round low orbit at the body's parking height. Gentlest: down to 7× the air's height (490 km at
    /// Kerbin), where the passes are still mild.
    /// </summary>
    private void ResetTargets()
    {
        if (_body is null) return;
        var km = (_body.DefaultParkingAltitudeM / 1000).ToString("0", C);
        (_targetApText, _targetPeText) = (km, km);
        _gentleApText = (Math.Round(_body.AtmosphereHeightM * 7 / 10_000) * 10).ToString("0", C);
        OnPropertyChanged(nameof(TargetApoapsisText));
        OnPropertyChanged(nameof(TargetPeriapsisText));
    }

    /// <summary>Where the drag comes from, and how big it is.</summary>
    public string DragNote { get => _dragNote; private set => SetProperty(ref _dragNote, value); }

    /// <summary>The headline: what the first pass does, and how it ends.</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>Working, or why there's nothing to show.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private static double? Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, C, out var v) && double.IsFinite(v) ? v : null;

    private void OnSyncChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaveSync.Save) or nameof(SaveSync.Crafts)) RefreshCrafts();
    }

    /// <summary>Vessels in the save (docked craft too), then designs; the picked one kept when it's still there.</summary>
    private void RefreshCrafts()
    {
        var picked = _craft;
        var skip = new[] { "Debris", "SpaceObject", "Flag", "DeployedSciencePart", "DeployedScienceController", "DeployedGroundPart" };
        var active = _sync.ActiveCraft?.SaveVesselId;
        var vessels = _sync.Craft.Where(v => v.Parts.Count > 0 && !skip.Contains(v.Type))
            .OrderByDescending(v => v.Id == active).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new AeroCraftChoice(v.Name, v.Id == active ? "Flying now · as it is in the save" : $"In the save · {Situation(v)}", v, null));
        var designs = _sync.Crafts.Where(c => !c.IsAutoSave)
            .Select(c => new AeroCraftChoice(Path.GetFileNameWithoutExtension(c.Path), $"{c.Facility} design · last stage, full tanks", null, c));
        Crafts.Clear();
        foreach (var c in vessels.Concat(designs)) Crafts.Add(c);
        var keep = picked is null ? null : Crafts.FirstOrDefault(c => c.Name == picked.Name && (c.Vessel is null) == (picked.Vessel is null));
        if (keep is not null) { _craft = keep; OnPropertyChanged(nameof(Craft)); }
    }

    private static string Situation(SavedVessel v) =>
        v.DockedInId is not null ? "docked" : v.Orbit?.BodyName is { } body ? $"{v.Situation.ToLowerInvariant()} {body}" : v.Situation.ToLowerInvariant();

    private Task<AeroData?> AeroAsync() => AeroDataCache.GetAsync(_sync.Settings.KspInstallPath);

    private async Task LoadCraftAsync(AeroCraftChoice? choice)
    {
        _shape = null;
        if (choice is null) { UpdateDragNote(); Recalculate(); return; }
        Status = "Reading the craft's parts…";
        try
        {
            var aero = await AeroAsync();
            var catalog = _sync.Parts.Catalog;
            var (shape, mass) = await Task.Run(() =>
            {
                if (choice.Vessel is { } v)
                    return (aero is null ? null : CraftAero.FromVessel(v, aero), VesselAnalysis.Analyze(v, catalog).WetMassT);
                var design = CraftAero.FinalStage(CraftReader.Read(choice.Design!.Path));
                return (aero is null ? null : CraftAero.FromCraft(design, aero), CraftAero.MassT(design, catalog));
            });
            if (choice != _craft) return;
            _shape = shape;
            _massText = mass > 0 ? mass.ToString("0.##", C) : _massText;
            OnPropertyChanged(nameof(MassText));
            _noDragData = aero is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Couldn't read the craft: {ex.Message}";
        }
        UpdateDragNote();
        Recalculate();
    }

    private void UpdateDragNote()
    {
        if (Parse(_dragText) is > 0) { DragNote = "Using the drag area typed in, the same at every speed."; return; }
        if (_shape is not { } s)
        {
            DragNote = _craft is null ? "Pick a craft, or type a drag area."
                : _noDragData ? "No drag data in your KSP folder yet (KSP writes PartDatabase.cfg each time it loads): type a drag area."
                : "No drag from the parts: type a drag area.";
            return;
        }
        var missing = s.MissingParts.Count == 0 ? "" : $" {s.MissingParts.Count} part type(s) have no drag data and are left out.";
        DragNote = $"From {s.PartCount} parts: {s.DragArea(10, _attitude.Attitude):N1} m² at Mach 10, {s.DragArea(3, _attitude.Attitude):N1} m² at Mach 3. " +
                   "Wings' lift isn't counted." + missing;
    }

    private void UseCraftOrbit()
    {
        if (_craft?.Vessel?.Orbit is not { } o || _body is null || o.BodyName != _body.Name) return;
        var r = _body.EquatorialRadiusM;
        var pe = o.SemiMajorAxisM * (1 - o.Eccentricity) - r;
        _inclinationText = o.InclinationDeg.ToString("0.#", C);
        OnPropertyChanged(nameof(InclinationText));
        if (o.IsClosed)
        {
            _arrival = Arrivals[0];
            _apoapsisText = ((o.SemiMajorAxisM * (1 + o.Eccentricity) - r) / 1000).ToString("0.#", C);
        }
        else
        {
            _arrival = Arrivals[1];
            _apoapsisText = Math.Sqrt(-_body.Mu / o.SemiMajorAxisM).ToString("0", C);
        }
        OnPropertyChanged(nameof(Arrival));
        OnPropertyChanged(nameof(ArrivalUnit));
        OnPropertyChanged(nameof(ApoapsisText));
        PeriapsisText = (pe / 1000).ToString("0.#", C);
    }

    /// <summary>
    /// Works out the periapsis (for an aim), the passes and the comparison in the background; a newer request wins.
    /// </summary>
    private async void Recalculate()
    {
        var run = ++_run;
        UpdateDragNote();
        Passes.Clear();
        Compare.Clear();
        Notes.Clear();
        (Summary, Suggestion, Burns) = ("", "", "");
        if (_body is not { } body) { Status = "No body with air to brake in."; return; }
        if (Parse(_massText) is not { } massT || massT <= 0) { Status = "Enter the craft's mass."; return; }
        var aim = _aim.Aim;
        var own = Parse(_periapsisText);
        if (aim == AerobrakeAim.Own && own is null) { Status = "Enter a periapsis."; return; }
        var peKm = own ?? body.AtmosphereHeightM / 2000;
        var drag = Parse(_dragText) is { } typed && typed > 0 ? DragTable.Constant(typed) : _shape?.Table(_attitude.Attitude);
        if (drag is null) { Status = "Pick a craft, or type a drag area."; return; }
        var inclination = Parse(_inclinationText) ?? 0;
        AerobrakeOrbit start;
        if (_arrival.FromFarAway)
        {
            if (Parse(_apoapsisText) is not { } vInf || vInf < 0) { Status = "Enter the speed left over from far away."; return; }
            start = AerobrakeOrbit.FromExcessSpeed(peKm * 1000, vInf, inclination);
        }
        else
        {
            if (Parse(_apoapsisText) is not { } apKm || apKm * 1000 < body.AtmosphereHeightM) { Status = "Enter an apoapsis above the air."; return; }
            if (apKm * 1000 + body.EquatorialRadiusM > body.SoiRadiusM && body.SoiRadiusM > 0) { Status = $"That apoapsis is outside {body.Name}'s sphere of influence."; return; }
            if (aim == AerobrakeAim.Own && apKm < peKm) { Status = "Enter an apoapsis above the periapsis."; return; }
            start = AerobrakeOrbit.FromApsides(body, peKm * 1000, apKm * 1000, inclination);
        }

        double targetAp = 0, targetPe = 0;
        var maxPasses = 10;
        if (aim is AerobrakeAim.Gentlest or AerobrakeAim.TargetOrbit)
        {
            if (Parse(TargetApoapsisText) is not { } tAp || tAp * 1000 <= body.AtmosphereHeightM) { Status = $"Enter an apoapsis to get down to, above the air ({body.AtmosphereHeightM / 1000:N0} km)."; return; }
            targetAp = tAp * 1000;
            if (Parse(_maxPassesText) is not { } mp || mp < 1) { Status = "Enter the most passes to take."; return; }
            maxPasses = (int)Math.Min(30, Math.Round(mp));
        }
        if (aim == AerobrakeAim.TargetOrbit)
        {
            if (Parse(_targetPeText) is not { } tPe || tPe * 1000 <= body.AtmosphereHeightM || tPe > targetAp / 1000)
            { Status = "Enter a target periapsis above the air and no higher than the target apoapsis."; return; }
            targetPe = tPe * 1000;
        }

        Status = aim == AerobrakeAim.Own ? "Working out the passes…" : "Finding the periapsis…";
        var mass = massT * 1000;
        var (suggestion, plan, sweep) = await Task.Run(() =>
        {
            var s = aim switch
            {
                AerobrakeAim.FewestPasses => AerobrakeAims.FewestPasses(body, start, mass, drag),
                AerobrakeAim.Gentlest => AerobrakeAims.Gentlest(body, start, mass, drag, targetAp, maxPasses),
                AerobrakeAim.TargetOrbit => AerobrakeAims.TargetOrbit(body, start, mass, drag, targetAp, targetPe, maxPasses),
                _ => null,
            };
            var from = s is null ? start : AerobrakeAims.At(body, start, s.PeriapsisM);
            // With a stop worked out, the passes end there: you burn at the apoapsis that follows.
            var p = s?.StopAfterPass is { } n
                ? Aerobraking.Plan(body, from, mass, drag, n, s.StopApoapsisM ?? targetAp)
                : Aerobraking.Plan(body, from, mass, drag);
            return (s, p, Aerobraking.Sweep(body, from, mass, drag, Aerobraking.SweepHeights(body.AtmosphereHeightM)));
        });
        if (run != _run) return;
        if (aim != AerobrakeAim.Own && suggestion is null)
        {
            Status = aim switch
            {
                AerobrakeAim.FewestPasses => "Even the top of the air brings it down: there's no safe periapsis.",
                AerobrakeAim.Gentlest => $"No periapsis gets the apoapsis down to {targetAp / 1000:N0} km in {maxPasses} passes safely: allow more passes, or aim higher.",
                _ => $"No safe way into that orbit in {maxPasses} passes: allow more passes, or aim higher.",
            };
            return;
        }
        Status = "";
        if (suggestion is not null)
        {
            _periapsisText = (suggestion.PeriapsisM / 1000).ToString("0.#", C);
            OnPropertyChanged(nameof(PeriapsisText));
            Suggestion = $"Periapsis {Km(suggestion.PeriapsisM)}. {suggestion.Why}";
            Burns = BurnText(body, suggestion, plan, aim, targetAp, targetPe);
        }
        Show(plan, suggestion?.StopAfterPass);
        var picked = suggestion?.PeriapsisM ?? peKm * 1000;
        foreach (var (pe, p) in sweep)
        {
            var first = p.Passes.FirstOrDefault();
            var down = p.Last is { Outcome: PassOutcome.ComesDown } last ? $"down on pass {last.Number}" : p.Last?.Outcome == PassOutcome.Escaping ? "escapes" : $"{p.Passes.Count}+ passes";
            Compare.Add(new HeightRow(pe, Km(pe), first is null ? "–" : After(first), down, first is null ? "–" : $"{PeakOutside(p):N1} g",
                Math.Abs(pe - picked) < 1));
        }
    }

    /// <summary>What to burn after the last pass, and (for a target orbit) what that saves over engines alone.</summary>
    private static string BurnText(CelestialBody body, AerobrakeSuggestion s, AerobrakePlan plan, AerobrakeAim aim, double targetAp, double targetPe)
    {
        if (s.StopAfterPass is not { } n || plan.Last is not { ApoapsisAfterM: { } ap, PeriapsisAfterM: { } pe }) return "";
        if (aim is AerobrakeAim.Gentlest or AerobrakeAim.FewestPasses)
        {
            var clear = body.AtmosphereHeightM + 1_000;
            return $"After pass {n}, at apoapsis ({Km(ap)}): burn {Format.Speed0(AerobrakeAims.RaiseBurn(body, ap, pe, clear))} prograde to lift the periapsis out of the air ({Km(clear)}).";
        }
        var text = $"After pass {n}, at apoapsis ({Km(ap)}): burn {Format.Speed0(s.RaiseBurn!.Value)} prograde to lift the periapsis to {Km(targetPe)}.";
        if (s.LowerBurn is { } lower)
            text += $" Then at periapsis: burn {Format.Speed0(lower)} retrograde to bring the apoapsis down to {Km(targetAp)}.";
        if (s.EnginesOnly is { } engines)
            text += $" {Format.Speed0(s.TotalBurn!.Value)} in all; by engine alone it's {Format.Speed0(engines)}, so this saves {Format.Speed0(engines - s.TotalBurn.Value)}.";
        return text;
    }

    private void Show(AerobrakePlan plan, int? stopAfter)
    {
        foreach (var p in plan.Passes)
            Passes.Add(new PassRow(p.Number.ToString(C), p.Number == 1 ? "start" : "+" + GameCalendar.Kerbin.FormatDuration(p.StartS),
                Km(p.PeriapsisM), p.Outcome == PassOutcome.ComesDown && p.LowestM <= 0 ? "ground" : Km(p.LowestM), Format.Speed0(p.SpeedLost), $"{p.PeakG:N1} g", After(p),
                p.PeriapsisAfterM is { } pa && p.Outcome != PassOutcome.ComesDown ? Km(pa) : "–",
                p.Number == stopAfter ? "stop: burn" : p.PeriodAfterS is { } t ? GameCalendar.Kerbin.FormatDuration(t) : "–",
                p.Outcome == PassOutcome.ComesDown, p.Number == stopAfter));
        foreach (var n in plan.Notes) Notes.Add(n);
        if (plan.Passes.FirstOrDefault() is not { } first) { Summary = plan.Notes.FirstOrDefault() ?? ""; return; }
        var end = plan.Last!;
        Summary = $"Pass 1 takes off {Format.Speed0(first.SpeedLost)}: {After(first)}. " + end.Outcome switch
        {
            _ when stopAfter is { } n && end.Number == n => $"Stop after pass {n}: apoapsis {Km(end.ApoapsisAfterM!.Value)}.",
            PassOutcome.ComesDown when end.Number == 1 => "It doesn't make it back out.",
            PassOutcome.ComesDown => $"Pass {end.Number} brings it down.",
            PassOutcome.Escaping => "",
            _ => $"After {plan.Passes.Count} passes the apoapsis is {Km(end.ApoapsisAfterM!.Value)}.",
        };
    }

    /// <summary>The hardest drag on the passes it comes back out of (the one that brings it down is a landing, not a pass).</summary>
    private static double PeakOutside(AerobrakePlan p) =>
        p.Passes.Where(x => x.Outcome != PassOutcome.ComesDown).Select(x => x.PeakG).DefaultIfEmpty(p.Passes[0].PeakG).Max();

    private static string After(AerobrakePass p) => p.Outcome switch
    {
        PassOutcome.ComesDown => "comes down",
        PassOutcome.Escaping => "still escaping",
        _ => $"apoapsis {Km(p.ApoapsisAfterM!.Value)}",
    };

    private static string Km(double metres) => (metres / 1000).ToString(metres < 100_000 ? "N1" : "N0", C) + " km";
}
