using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A rocket phase in the mission builder.</summary>
public sealed class PhaseViewModel : ObservableObject
{
    private static readonly string[] Palette =
        ["#4FC3F7", "#FFB74D", "#81C784", "#BA68C8", "#E57373", "#FFD54F", "#4DB6AC", "#9575CD"];

    private readonly Action _changed;
    private string _name;
    private double _marginPercent;
    private double _extraDv;

    public PhaseViewModel(PhaseDefinition definition, int colorIndex, Action changed)
    {
        _name = definition.Name;
        _marginPercent = definition.MarginPercent;
        _extraDv = definition.ExtraDv;
        Color = Palette[colorIndex % Palette.Length];
        _changed = changed;
    }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) _changed(); }
    }

    public double MarginPercent
    {
        get => _marginPercent;
        set { if (SetProperty(ref _marginPercent, Math.Max(0, value))) _changed(); }
    }

    /// <summary>Fixed extra Δv on top of the percentage margin (e.g. landing wiggle room).</summary>
    public double ExtraDv
    {
        get => _extraDv;
        set { if (SetProperty(ref _extraDv, Math.Max(0, value))) _changed(); }
    }

    public string Color { get; }

    // Set by the mission after each recalculation.
    public double RequiredDv { get; private set; }
    public double MarginDv { get; private set; }
    public double TotalDv => RequiredDv + MarginDv;
    public string RequiredText => Format.Speed0(RequiredDv);
    public string MarginText => "+" + Format.Speed0(MarginDv);
    public string TotalText => Format.Speed0(TotalDv);

    /// <summary>Rocket diagram size: height ∝ Δv share, width narrows for upper phases.</summary>
    public double BarHeight { get; private set; }
    public double BarWidth { get; private set; }

    public PhaseDefinition ToDefinition() => new(Name, MarginPercent, ExtraDv);

    /// <param name="scaleTotal">Δv that fills the full diagram height: the mission total, or the design's if larger.</param>
    /// <param name="sideBySide">A design is shown next to the phases, so the bars are narrower.</param>
    internal void Update(PhaseSummary summary, int index, int count, double scaleTotal, bool sideBySide)
    {
        RequiredDv = summary.RequiredDv;
        MarginDv = summary.MarginDv;
        BarHeight = sideBySide ? RocketDiagram.Height(TotalDv, scaleTotal)
            : 48 + 300 * (scaleTotal > 0 ? TotalDv / scaleTotal : 1.0 / count);
        BarWidth = sideBySide ? 104 : Math.Max(90, 190 - index * 26);
        foreach (var p in new[] { nameof(RequiredDv), nameof(MarginDv), nameof(TotalDv), nameof(RequiredText),
                     nameof(MarginText), nameof(TotalText), nameof(BarHeight), nameof(BarWidth) })
            OnPropertyChanged(p);
    }
}

/// <summary>Sizes for the rocket diagram when a design is compared: both columns share one Δv scale.</summary>
internal static class RocketDiagram
{
    public static double Height(double deltaV, double scaleTotal) => 30 + 200 * (scaleTotal > 0 ? deltaV / scaleTotal : 0);
}

/// <summary>A stage of the chosen design in the rocket diagram, coloured like the phase it mostly burns for.</summary>
public sealed record StageBlock(string Label, string DeltaVText, string Color, double BarHeight, string ToolTip);

/// <summary>One row of the design check: a phase, what it needs and how well the stages cover it.</summary>
public sealed record PhaseCheckRow(string Name, string Color, string Needed, string Detail, Coverage Status)
{
    public string Mark => Status switch { Coverage.Comfortable => "✓", Coverage.InsideMargin => "✓", _ => "✗" };
}

/// <summary>One landing or lift-off on the route with the TWR of the stage firing then.</summary>
public sealed record SurfaceRow(string Title, string Detail, string Twr, TwrRating Rating)
{
    public string Mark => Rating switch { TwrRating.Good => "✓", TwrRating.Low => "✓", _ => "✗" };
}

/// <summary>An entry in the stop picker: a place on the Δv map, by node key.</summary>
public sealed record StopChoice(string Key, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A quick pick for a number box (an orbit's altitude or inclination); <see cref="Custom"/> stands for any typed value.</summary>
public sealed record ValuePreset(string Text, double? Value)
{
    public static readonly ValuePreset Custom = new("Custom", null);
    public override string ToString() => Text;
}

/// <summary>An entry in the design picker; <see cref="Design"/> is null for "no design".</summary>
public sealed record DesignChoice(DesignEntry? Design, string Title)
{
    public string Path => Design?.File.Path ?? "";
    public override string ToString() => Title;
}

/// <summary>A stop in the route (like a Google Maps waypoint). Identified by map node key.</summary>
public sealed class StopViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action _legsChanged;
    private string _nodeKey;
    private string _note;
    private TransferLeg? _transfer;
    private DeployStep? _deploy;
    private RendezvousLeg? _rendezvous;
    private LandingLeg? _landing;
    private LaunchLeg? _launch;

    /// <param name="changed">The stop moved somewhere else: the route changes.</param>
    /// <param name="legsChanged">The note or a leg tool changed: same route, new leg values.</param>
    public StopViewModel(MissionStop stop, Action changed, Action legsChanged)
    {
        _nodeKey = stop.NodeKey;
        _note = stop.Note ?? "";
        _transfer = stop.Transfer;
        _deploy = stop.Deploy;
        _rendezvous = stop.Rendezvous;
        _landing = stop.Landing;
        _launch = stop.Launch;
        _changed = changed;
        _legsChanged = legsChanged;
    }

    public string NodeKey
    {
        get => _nodeKey;
        set
        {
            if (value is null || !SetProperty(ref _nodeKey, value)) return;
            _pickerKey = null;
            _changed();
        }
    }

    private string? _pickerKey;

    /// <summary>
    /// The stop picker's entry: the stop itself, or its body's "Orbit" entry for any of that body's orbits (picking it
    /// again keeps the height chosen; picking it from elsewhere starts at the low orbit).
    /// </summary>
    public string PickerKey
    {
        get => _pickerKey ?? _nodeKey;
        set { if (value is not null && value != PickerKey) NodeKey = value; }
    }

    internal void SetPickerKey(string key)
    {
        _pickerKey = key;
        OnPropertyChanged(nameof(PickerKey));
    }

    /// <summary>Free text such as "Dock at station, pick up Bob". Doesn't affect the route.</summary>
    public string Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) _legsChanged(); }
    }

    // ---------------- Tools of the leg arriving here ----------------

    /// <summary>The launch window the leg arriving here is flown in (a transfer leg); null for a Δv-map leg.</summary>
    public TransferLeg? Transfer
    {
        get => _transfer;
        set { if (SetProperty(ref _transfer, value)) _legsChanged(); }
    }

    /// <summary>The leg arriving here crosses between planets, so a launch window can be chosen for it.</summary>
    public bool CanHaveTransfer { get; private set; }
    public bool HasTransfer => _transfer is not null;
    public bool OffersTransfer => CanHaveTransfer && _transfer is null;

    /// <summary>"Depart Year 1, Day 231, arrive Year 2, Day 75 / Ejection 1,055 m/s, capture 652 m/s", or why the saved window isn't used.</summary>
    public string TransferText { get; private set; } = "";

    /// <summary>The rendezvous the leg arriving here is flown as, as planned on the Rendezvous pane; null for a Δv-map leg.</summary>
    public RendezvousLeg? Rendezvous
    {
        get => _rendezvous;
        set { if (SetProperty(ref _rendezvous, value)) _legsChanged(); }
    }

    /// <summary>The stop is a placed craft, reached by launching from KSC or from another placed craft around the same body.</summary>
    public bool CanHaveRendezvous { get; private set; }
    public bool HasRendezvous => _rendezvous is not null;
    public bool OffersRendezvous => CanHaveRendezvous && _rendezvous is null;

    /// <summary>"Arrive Year 1, Day 3, 2:41:07 / Orbital burns 187 m/s", or why the plan isn't used.</summary>
    public string RendezvousText { get; private set; } = "";

    /// <param name="startsTooEarly">The plan starts before the mission gets here (the leg before changed since).</param>
    internal void SetRendezvous(bool canHaveRendezvous, bool used, bool startsTooEarly = false)
    {
        CanHaveRendezvous = canHaveRendezvous;
        RendezvousText = _rendezvous is not { } r ? ""
            : used ? $"Start {GameCalendar.Kerbin.FormatDate(r.StartUt)}, meet {GameCalendar.Kerbin.FormatDate(r.ArrivalUt)}\n" +
                     $"Orbital burns {Format.Speed0(r.OrbitalDv)} (as planned)" +
                     (startsTooEarly ? "\nPlanned to start before the mission gets here: open the leg to plan it again." : "")
            : "The leg here no longer meets this craft, so the plan isn't used.";
        foreach (var name in new[] { nameof(CanHaveRendezvous), nameof(HasRendezvous), nameof(OffersRendezvous), nameof(RendezvousText) })
            OnPropertyChanged(name);
    }

    /// <summary>A satellite constellation released at this stop; null if none.</summary>
    public DeployStep? Deploy
    {
        get => _deploy;
        set { if (SetProperty(ref _deploy, value)) _legsChanged(); }
    }

    /// <summary>Picks the phase flying the deployment without rebuilding the legs (the caller recalculates).</summary>
    internal void SetDeployPhase(int phaseIndex)
    {
        if (_deploy is not null) _deploy = _deploy with { PhaseIndex = phaseIndex };
    }

    /// <summary>The stop is a circular orbit, so a constellation can be released here.</summary>
    public bool CanDeploy { get; private set; }
    public bool HasDeploy => _deploy is not null;

    /// <summary>"3 satellites · carrier 108 m/s · each satellite 108 m/s", or why the deployment isn't used.</summary>
    public string DeployText { get; private set; } = "";

    internal void SetDeploy(bool canDeploy, DeployResult? deploy)
    {
        CanDeploy = canDeploy;
        DeployText = _deploy is null ? ""
            : deploy is not null ? $"{_deploy.SatelliteCount} satellites, {(deploy.Orbit.IsDiveOrbit ? "dive" : "resonant")} carrier orbit\n" +
                                   $"Carrier {Format.Speed0(deploy.CarrierDv)} · each satellite {Format.Speed0(deploy.SatelliteDv)}"
            : canDeploy ? "This orbit doesn't work for the deployment: open it to change it."
            : "The stop is no longer a circular orbit, so the deployment isn't used.";
        foreach (var name in new[] { nameof(CanDeploy), nameof(HasDeploy), nameof(DeployText) })
            OnPropertyChanged(name);
    }

    // ---------------- Landing at a chosen spot (surface stops) ----------------

    /// <summary>Where on the surface the leg arriving here lands (§2.6); null for the map's "anywhere" landing.</summary>
    public LandingLeg? Landing
    {
        get => _landing;
        set
        {
            if (!SetProperty(ref _landing, value)) return;
            foreach (var name in new[] { nameof(HasLanding), nameof(OffersLanding), nameof(SelectedTarget),
                         nameof(LatitudeText), nameof(LongitudeText), nameof(GroundHeightText), nameof(LanderTwrText) })
                OnPropertyChanged(name);
            _legsChanged();
        }
    }

    /// <summary>The leg arriving here comes down from an orbit to the surface, so it can land at a chosen spot.</summary>
    public bool CanLand { get; private set; }
    public bool HasLanding => _landing is not null;
    public bool OffersLanding => CanLand && _landing is null;

    /// <summary>Spots on this stop's body: wiki landmarks, and the save's flags, landed craft and KerbNet waypoints.</summary>
    public IReadOnlyList<LandingTarget> LandingTargets { get; private set; } = [];

    /// <summary>The picked spot, when it is one of <see cref="LandingTargets"/> (an entered spot shows none).</summary>
    public LandingTarget? SelectedTarget
    {
        get => _landing?.Target is { } t && LandingTargets.Contains(t) ? t : null;
        set { if (value is not null && _landing is { } l && value != l.Target) Landing = l with { Target = value }; }
    }

    public string LatitudeText
    {
        get => _landing is { } l ? l.Target.LatitudeDeg.ToString("0.####", CultureInfo.CurrentCulture) : "";
        set => EnterSpot(value, null, null);
    }

    public string LongitudeText
    {
        get => _landing is { } l ? l.Target.LongitudeDeg.ToString("0.####", CultureInfo.CurrentCulture) : "";
        set => EnterSpot(null, value, null);
    }

    /// <summary>The ground's height above the datum in metres; blank when unknown.</summary>
    public string GroundHeightText
    {
        get => _landing?.Target.GroundHeightM is { } h ? h.ToString("#,0", CultureInfo.CurrentCulture) : "";
        set => EnterSpot(null, null, value);
    }

    public string LanderTwrText
    {
        get => _landing is { } l ? l.LanderTwr.ToString("0.##", CultureInfo.CurrentCulture) : "";
        set
        {
            if (_landing is { } l && double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var twr) && twr > 1)
                Landing = l with { LanderTwr = twr };
            else OnPropertyChanged(nameof(LanderTwrText));
        }
    }

    /// <summary>Typing a position or height makes the spot an entered one (latitude −90…90, longitude −180…180).</summary>
    private void EnterSpot(string? latitude, string? longitude, string? height)
    {
        if (_landing is not { Target: var t } l) return;
        static bool Parse(string? text, out double value) =>
            double.TryParse((text ?? "").Trim().TrimEnd('°', 'm').Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out value);
        var lat = t.LatitudeDeg;
        var lon = t.LongitudeDeg;
        var ground = t.GroundHeightM;
        var ok = latitude is null || Parse(latitude, out lat) && lat is >= -90 and <= 90;
        ok &= longitude is null || Parse(longitude, out lon) && lon is >= -180 and <= 180;
        if (height is not null)
        {
            if (string.IsNullOrWhiteSpace(height)) ground = null;
            else if (Parse(height, out var h)) ground = h;
            else ok = false;
        }
        var entered = new LandingTarget(t.Source == "Entered" ? t.Name : $"{t.Name} (edited)", "Entered", lat, lon, ground);
        if (ok && entered with { Name = t.Name, Source = t.Source } != t) Landing = l with { Target = entered };
        else foreach (var name in new[] { nameof(LatitudeText), nameof(LongitudeText), nameof(GroundHeightText) }) OnPropertyChanged(name);
    }

    /// <summary>"Deorbit 17 m/s + braking 606 m/s …", or why the landing isn't used.</summary>
    public string LandingText { get; private set; } = "";

    internal void SetLanding(bool canLand, LandingPlan? plan, IReadOnlyList<LandingTarget> targets)
    {
        CanLand = canLand;
        if (!targets.SequenceEqual(LandingTargets))
        {
            LandingTargets = targets;
            OnPropertyChanged(nameof(LandingTargets));
            OnPropertyChanged(nameof(SelectedTarget));
        }
        LandingText = _landing is null ? ""
            : plan is null ? "This stop is no longer reached by coming down from an orbit, so the landing spot isn't used."
            : LandingSummary(plan);
        foreach (var name in new[] { nameof(CanLand), nameof(HasLanding), nameof(OffersLanding), nameof(LandingText) })
            OnPropertyChanged(name);
    }

    private static string LandingSummary(LandingPlan p)
    {
        var lines = new List<string>
        {
            $"{Units.LatLon(p.Target.LatitudeDeg, p.Target.LongitudeDeg)} · from the {p.OrbitAltitudeM / 1000:0.#} km orbit" +
            (p.GroundHeightM is { } ground ? $" · ground {ground:#,0} m{(p.GroundFromBake ? " (surface bake)" : "")}" : ""),
            p.IsRoughEstimate
                ? $"Rough estimate: deorbit {Format.Speed0(p.DeorbitDv)}, braking up to {Format.Speed0(p.BrakingDv)} without air (map value kept)"
                : $"Deorbit {Format.Speed0(p.DeorbitDv)} + braking {Format.Speed0(p.BrakingDv)} = {Format.Speed0(p.TotalDv)}",
            $"Orbit inclined at least {p.MinInclinationDeg:0.#}° (plan {p.InclinationDeg:0.#}°; from an equatorial orbit +{Format.Speed0(p.PlaneChangeDv)})",
            $"Deorbit half an orbit before the spot, aiming {p.DriftM / 1000:0.#} km east of it ({p.DriftDeg:0.##}° of turning in {Units.BurnTime(p.DescentTimeS)})",
            $"Start braking {p.BrakingDistanceM / 1000:0.#} km before it at {p.PeriapsisAltitudeM / 1000:0.#} km; burn {Units.BurnTime(p.BrakingTimeS)}",
            $"Ground track moves {p.TrackShiftPerOrbitDeg:0.##}° west each orbit",
        };
        if (p.ApproachPeakM is { } approach)
            lines.Add($"Highest terrain under the braking burn: {approach:#,0} m on the {(p.NorthboundPass == true ? "northbound" : "southbound")} pass (the lower one)");
        lines.AddRange(p.Warnings);
        return string.Join("\n", lines);
    }

    // ---------------- Launch into an inclined orbit (stops a launch arrives at) ----------------

    /// <summary>The inclination the launch arriving here flies into (§2.7); null for the map's due-east (equatorial) launch.</summary>
    public LaunchLeg? Launch
    {
        get => _launch;
        set
        {
            _customInclination = false;
            if (!SetProperty(ref _launch, value))
            {
                OnPropertyChanged(nameof(InclinationText));
                OnPropertyChanged(nameof(SelectedInclinationPreset));
                return;
            }
            foreach (var name in new[] { nameof(InclinationText), nameof(SelectedInclinationPreset), nameof(ShowsLaunchText) })
                OnPropertyChanged(name);
            _legsChanged();
        }
    }

    /// <summary>The leg arriving here launches from a launch site into an orbit, so its inclination can be picked.</summary>
    public bool CanLaunch { get; private set; }
    public bool ShowsLaunchText => CanLaunch && LaunchText.Length > 0;

    /// <summary>The orbit's inclination in degrees, 0–180 (0° equatorial, 90° polar, 180° retrograde equatorial).</summary>
    public string InclinationText
    {
        get => (_launch?.InclinationDeg ?? 0).ToString("0.###", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse((value ?? "").Trim().TrimEnd('°').Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var i)
                && i is >= 0 and <= 180)
                Launch = i == 0 ? null : new LaunchLeg(i);
            else OnPropertyChanged(nameof(InclinationText));
        }
    }

    /// <summary>Quick picks for the inclination; "Custom" is any other value typed in the box.</summary>
    public IReadOnlyList<ValuePreset> InclinationPresets { get; } =
    [
        new("Equatorial", 0), new("Polar", 90), new("Retrograde", 180), ValuePreset.Custom,
    ];

    private bool _customInclination;

    /// <summary>The preset matching the inclination, else "Custom".</summary>
    public ValuePreset SelectedInclinationPreset
    {
        get => _customInclination ? ValuePreset.Custom
            : InclinationPresets.FirstOrDefault(p => p.Value == (_launch?.InclinationDeg ?? 0)) ?? ValuePreset.Custom;
        set
        {
            if (value is null) return;
            if (value.Value is { } i) Launch = i == 0 ? null : new LaunchLeg(i);
            else
            {
                _customInclination = true;
                OnPropertyChanged(nameof(SelectedInclinationPreset));
            }
        }
    }

    /// <summary>"Fly heading 355.6° (north) or 184.4° (south) … / +182 m/s over a launch due east"; empty for due east.</summary>
    public string LaunchText { get; private set; } = "";

    /// <param name="measuresPlaneChanges">The mission goes on to custom orbits around the body, whose plane changes are measured from this one.</param>
    internal void SetLaunch(bool canLaunch, LaunchPlan? plan, bool measuresPlaneChanges)
    {
        CanLaunch = canLaunch;
        LaunchText = plan is null ? "" : LaunchSummary(plan, measuresPlaneChanges);
        foreach (var name in new[] { nameof(CanLaunch), nameof(LaunchText), nameof(ShowsLaunchText) })
            OnPropertyChanged(name);
    }

    private static string LaunchSummary(LaunchPlan p, bool measuresPlaneChanges)
    {
        var lines = new List<string>
        {
            $"Fly heading {p.HeadingText} from the {p.SiteName}",
            $"+{Format.Speed0(p.LaunchDv)} over a launch due east, into the {p.OrbitAltitudeM / 1000:0.#} km low orbit",
        };
        if (!p.IsReachable)
            lines.Add($"From {Math.Abs(p.SiteLatitudeDeg):0.###}° latitude a launch reaches {p.FlownInclinationDeg:0.###}° at best: " +
                      $"+{Format.Speed0(p.TrimDv)} plane change in orbit for the rest");
        if (measuresPlaneChanges)
            lines.Add($"Plane changes to orbits around {p.Body.Name} after this are measured from {p.InclinationDeg:0.#}°");
        return string.Join("\n", lines);
    }

    internal void SetTools(bool canHaveTransfer, TransferLegResult? transfer)
    {
        CanHaveTransfer = canHaveTransfer;
        TransferText = _transfer is null ? ""
            : transfer is { Window: var w } ? $"Depart {GameCalendar.Kerbin.FormatDay(w.DepartureUt)}, arrive {GameCalendar.Kerbin.FormatDay(w.ArrivalUt)}\n" +
                                             $"Ejection {Format.Speed0(w.EjectionDv)}" + (transfer.Span.Captures ? $", capture {Format.Speed0(w.CaptureDv)}" : "")
            : canHaveTransfer ? "No transfer solution for the saved window: open it to pick another."
            : "The leg here no longer crosses between planets, so the saved window isn't used.";
        foreach (var name in new[] { nameof(CanHaveTransfer), nameof(HasTransfer), nameof(OffersTransfer), nameof(TransferText) })
            OnPropertyChanged(name);
    }

    // ---------------- Orbit altitude ----------------

    private Func<double, string?>? _setAltitude;
    private string _altitudeKmText = "";
    private string _altitudeError = "";

    /// <summary>The stop is a circular orbit of a body, so its height can be picked (low, synchronous or any altitude).</summary>
    public bool IsOrbit { get; private set; }

    /// <summary>The orbit's altitude in km. Typing a new one moves the stop to that orbit (a place kept with the mission).</summary>
    public string AltitudeKmText
    {
        get => _altitudeKmText;
        set
        {
            if (!SetProperty(ref _altitudeKmText, value)) return;
            _customAltitude = false;
            OnPropertyChanged(nameof(SelectedAltitudePreset));
            if (_setAltitude is null) return;
            AltitudeError = double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var km) && km > 0
                ? _setAltitude(km * 1000) ?? ""
                : "Enter the orbit's altitude in km.";
        }
    }

    /// <summary>Why the typed altitude can't be flown (inside the atmosphere, outside the SOI); empty when it's fine.</summary>
    public string AltitudeError
    {
        get => _altitudeError;
        private set { if (SetProperty(ref _altitudeError, value)) OnPropertyChanged(nameof(HasAltitudeError)); }
    }

    public bool HasAltitudeError => AltitudeError.Length > 0;

    /// <summary>Quick picks for the altitude: "Low" (the Δv map's low orbit), "Keostationary" where the body has one, and "Custom".</summary>
    public IReadOnlyList<ValuePreset> AltitudePresets { get; private set; } = [];

    private bool _customAltitude;

    /// <summary>The preset matching the altitude, else "Custom".</summary>
    public ValuePreset SelectedAltitudePreset
    {
        get => _customAltitude ? ValuePreset.Custom
            : AltitudePresets.FirstOrDefault(p => p.Value is { } m && Km(m) == _altitudeKmText) ?? ValuePreset.Custom;
        set
        {
            if (value is null) return;
            if (value.Value is { } m) AltitudeKmText = Km(m);
            else _customAltitude = true;
            OnPropertyChanged(nameof(SelectedAltitudePreset));
        }
    }

    /// <param name="altitudeM">The orbit's altitude, or null when the stop isn't a circular orbit.</param>
    /// <param name="setAltitude">Moves the stop to an orbit at that altitude; returns why it can't, or null.</param>
    internal void SetOrbit(double? altitudeM, double lowM, double? synchronousM, string synchronousName, Func<double, string?> setAltitude)
    {
        IsOrbit = altitudeM is not null;
        _setAltitude = null; // showing the altitude isn't the user typing one
        if (altitudeM is { } a) AltitudeKmText = Km(a);
        _setAltitude = setAltitude;
        AltitudeError = "";
        var presets = new List<ValuePreset> { new("Low", lowM) };
        if (synchronousM is { } s) presets.Add(new(synchronousName, s));
        presets.Add(ValuePreset.Custom);
        if (!presets.SequenceEqual(AltitudePresets))
        {
            AltitudePresets = presets;
            OnPropertyChanged(nameof(AltitudePresets));
        }
        foreach (var name in new[] { nameof(IsOrbit), nameof(SelectedAltitudePreset) })
            OnPropertyChanged(name);
    }

    internal void ClearOrbit()
    {
        _setAltitude = null;
        IsOrbit = false;
        OnPropertyChanged(nameof(IsOrbit));
    }

    private static string Km(double metres) => (metres / 1000).ToString("#,0.###", CultureInfo.CurrentCulture);

    public int Number { get; private set; }
    public string Role { get; private set; } = "";

    /// <summary>The route's last stop: no connector runs on from it in the Stops pane.</summary>
    public bool IsLast { get; private set; }

    internal void SetPosition(int index, int count)
    {
        Number = index + 1;
        Role = index == 0 ? "Start" : index == count - 1 ? "End" : "Stop";
        IsLast = index == count - 1;
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(IsLast));
    }

    public MissionStop ToDefinition() => new(NodeKey, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(), _transfer, _deploy, _rendezvous, _landing, _launch);
}

/// <summary>A constellation released at a stop, shown after the leg that arrives there, with the phase that flies it.</summary>
public sealed class DeployRowViewModel : ObservableObject
{
    private readonly Action<int> _phaseChanged;
    private PhaseViewModel _phase;

    public DeployRowViewModel(string title, DeployResult result, PhaseViewModel phase, Action<int> phaseChanged, IList<PhaseViewModel> phases)
    {
        Title = title;
        Result = result;
        _phase = phase;
        _phaseChanged = phaseChanged;
        Phases = phases;
    }

    private IList<PhaseViewModel> Phases { get; }
    public string Title { get; }
    public DeployResult Result { get; }
    public string DvText => Format.Speed0(Result.CarrierDv);

    public string Detail => $"Into the carrier orbit {Format.Speed0(Result.EnterDv)}" +
                            (Result.ReturnDv > 0 ? $", back to circular after the last release {Format.Speed0(Result.ReturnDv)}" : "") +
                            $" · each satellite circularizes itself: {Format.Speed0(Result.SatelliteDv)}";

    public PhaseViewModel Phase
    {
        get => _phase;
        set { if (value is not null && SetProperty(ref _phase, value)) _phaseChanged(Phases.IndexOf(value)); }
    }
}

/// <summary>One leg of the route with its phase and options.</summary>
public sealed class LegRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<string, double?> _editDv;
    private PhaseViewModel _phase;
    private bool _useAerobrake;
    private bool _includePlaneChange;

    public LegRowViewModel(RouteLeg leg, int number, string? sectionHeader, PhaseViewModel phase, LegSetting setting,
        Action changed, Action<string, double?> editDv)
    {
        Leg = leg;
        Number = number;
        SectionHeader = sectionHeader;
        _phase = phase;
        _useAerobrake = setting.UseAerobrake;
        _includePlaneChange = setting.IncludePlaneChange;
        _changed = changed;
        _editDv = editDv;
    }

    public RouteLeg Leg { get; }
    public int Number { get; }

    /// <summary>A constellation released at the stop this leg arrives at (last leg of a segment only).</summary>
    public DeployRowViewModel? Deploy { get; internal set; }
    public bool HasDeploy => Deploy is not null;

    /// <summary>"To stop 2: Kerbin — Low orbit (Dock at station)" on the first leg of each segment.</summary>
    public string? SectionHeader { get; }
    public bool HasSectionHeader => SectionHeader is not null;

    public string FromText => Leg.From.DisplayName;
    public string ToText => Leg.To.DisplayName;
    public bool HasPlaneChange => Leg.PlaneChangeDv > 0;
    public string PlaneChangeText => $"+{Leg.PlaneChangeDv:N0}";
    public bool CanAerobrake => Leg.CanAerobrake;
    public bool IsEdited => Leg.IsOverridden;

    /// <summary>A leg tool supplies this value, so it can't be typed over (change it in the tool).</summary>
    public bool IsFromTool => Leg.IsFromTool;

    /// <summary>The value comes from a leg tool or adds up several map edges, so it isn't typed here.</summary>
    public bool IsReadOnly => Leg.IsFromTool || Leg.IsCombined;
    public string DvToolTip => Leg.ToolNote is { } tool ? $"{tool} — change it in the leg's tool. Map value: {Leg.MapDeltaV:N0} m/s."
        : Leg.Parts is { } parts ? string.Join(" + ", parts.Select(p => $"{p.To.DisplayName} {p.DeltaV:N0} m/s")) + ". Edit the steps on the Δv map."
        : Leg.IsOverridden
        ? $"Edited — map value is {Leg.MapDeltaV:N0} m/s. Clear to restore. Applies to all missions."
        : "Map value. Type a new value to adjust it for all missions.";

    /// <summary>Editable: writes a map override for this edge (both directions).</summary>
    public string DvText
    {
        get => Leg.DeltaV.ToString("0", CultureInfo.CurrentCulture);
        set
        {
            var text = value.Trim();
            if (IsReadOnly) return;
            if (text.Length == 0) { _editDv(Leg.EdgeKey, null); return; }
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var dv)
                && dv >= 0 && dv != Leg.DeltaV)
                _editDv(Leg.EdgeKey, dv == Leg.MapDeltaV ? null : dv);
        }
    }

    public string Notes => string.Join(" · ", new[]
    {
        Leg.ToolNote,
        Leg.Parts is { } parts && !Leg.IsFromTool ? $"Via {parts[0].To.Label}: {string.Join(" + ", parts.Select(p => p.DeltaV.ToString("N0", CultureInfo.CurrentCulture)))} m/s" : null,
        Leg.IsFromTool ? null : Leg.FlightTime is { } t ? $"Flight ≈ {t}" : null,
        Leg.CanAerobrake && Leg.AerobrakeDv > 0 ? $"Aerobraking possible (still needs a {Leg.AerobrakeDv:N0} m/s deorbit burn)"
            : Leg.CanAerobrake ? "Aerobraking possible" : null,
        Leg.To.JetEnginesPossible ? "Jet engines work here" : null,
    }.Where(s => s is not null));

    public PhaseViewModel Phase
    {
        get => _phase;
        set { if (value is not null && SetProperty(ref _phase, value)) _changed(); }
    }

    public bool UseAerobrake
    {
        get => _useAerobrake;
        set { if (SetProperty(ref _useAerobrake, value)) Changed(); }
    }

    public bool IncludePlaneChange
    {
        get => _includePlaneChange;
        set { if (SetProperty(ref _includePlaneChange, value)) Changed(); }
    }

    public LegSetting Setting(IList<PhaseViewModel> phases) => new(phases.IndexOf(Phase), UseAerobrake, IncludePlaneChange);

    public double EffectiveDv => MissionPlanner.EffectiveDv(Leg, new LegSetting(0, UseAerobrake, IncludePlaneChange));
    public string EffectiveText => Format.Speed0(EffectiveDv);

    private void Changed()
    {
        OnPropertyChanged(nameof(EffectiveDv));
        OnPropertyChanged(nameof(EffectiveText));
        _changed();
    }
}

public sealed partial class MissionBuilderViewModel : ObservableObject
{
    private readonly DvMapRepository _maps;
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private readonly DesignLibrary? _designs;
    private IReadOnlyDictionary<string, CelestialBody> _bodies;

    private static Dictionary<string, CelestialBody> StockBodies(BodyRepository? bodies) =>
        bodies?.GetSystems().FirstOrDefault(s => s.Name == "KSP Stock") is { } stock
            ? bodies.GetBodies(stock.Id).ToDictionary(b => b.Name)
            : [];
    private DeltaVMap? _map;
    private DesignChoice? _selectedDesign;
    private DesignCheckResult? _check;

    private bool _useAerobraking = true;
    private bool _includePlaneChanges;
    private bool _suspend;
    private readonly SaveSync? _sync;
    private readonly SurfaceBakeRepository? _surfaceBakes;

    public MissionBuilderViewModel(DvMapRepository maps, CustomLocationRepository locations,
        PlannerEvents events, DesignLibrary? designs = null, BodyRepository? bodies = null,
        MissionRepository? missions = null, SettingsRepository? settings = null, SaveSync? sync = null,
        SurfaceBakeRepository? bakes = null)
    {
        _surfaceBakes = bakes;
        _missions = missions;
        // Flags, landed craft and waypoints in the save are landing spots: keep the pickers in step with the game.
        _sync = sync;
        if (sync is not null) sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save)) RefreshLegs(); };
        _settings = settings;
        // Surface gravity and pressure for the lander TWR check (stock system, like the Δv map).
        _bodies = StockBodies(bodies);
        _designs = designs;
        if (designs is not null) designs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignLibrary.Entries)) RefreshDesignChoices();
        };
        RefreshDesignChoices();
        _maps = maps;
        _locations = locations;
        _events = events;
        _baseMap = maps.GetMap();
        _map = MapWithPlaces();

        // Custom locations are managed on the Locations pane; keep the stop pickers and map in step with it.
        _events.LocationsChanged += _ => OnLocationsChanged();
        // Another save chosen (the window has offered to save this mission): carry on with that save's missions.
        _events.SaveChanged += OpenLastMission;
        // A new bake gives landing spots ground heights and approach terrain.
        // A new bake gives the bodies the game's values (BakeMerge).
        _events.SurfaceDataChanged += _ =>
        {
            _bodies = StockBodies(bodies);
            RefreshLegs();
        };

        AddStopCommand = new RelayCommand(AddStop, () => _map is not null);
        MoveStopUpCommand = new RelayCommand<StopViewModel>(s => MoveStop(s, -1), s => Stops.IndexOf(s) > 0);
        MoveStopDownCommand = new RelayCommand<StopViewModel>(s => MoveStop(s, +1), s => Stops.IndexOf(s) < Stops.Count - 1);
        RemoveStopCommand = new RelayCommand<StopViewModel>(RemoveStop, _ => Stops.Count > 1);
        ReverseStopsCommand = new RelayCommand(ReverseStops, () => Stops.Count > 1);
        AddPhaseCommand = new RelayCommand(AddPhase);
        RemovePhaseCommand = new RelayCommand<PhaseViewModel>(RemovePhase, _ => Phases.Count > 1);
        ResetPhasesCommand = new RelayCommand(() => RebuildRoute(keepPhaseTuning: false));
        ResetMapEditsCommand = new RelayCommand(ResetMapEdits, () => EditedCount > 0);
        OpenTransferCommand = new RelayCommand<StopViewModel>(OpenTransfer, s => s is not null && s.CanHaveTransfer);
        RemoveTransferCommand = new RelayCommand<StopViewModel>(RemoveTransfer, s => s?.HasTransfer == true);
        OpenDeployCommand = new RelayCommand<StopViewModel>(OpenDeploy, s => s is not null && s.CanDeploy);
        OpenRendezvousCommand = new RelayCommand<StopViewModel>(OpenRendezvous, s => s is not null && s.CanHaveRendezvous);
        RemoveRendezvousCommand = new RelayCommand<StopViewModel>(RemoveRendezvous, s => s?.HasRendezvous == true);
        RemoveDeployCommand = new RelayCommand<StopViewModel>(RemoveDeploy, s => s?.HasDeploy == true);
        AddLandingCommand = new RelayCommand<StopViewModel>(AddLanding, s => s?.OffersLanding == true);
        RemoveLandingCommand = new RelayCommand<StopViewModel>(s => s.Landing = null, s => s?.HasLanding == true);
        NewMissionCommand = new RelayCommand(() => { if (ConfirmDiscard()) NewMission(); });
        SaveMissionCommand = new RelayCommand(() => SaveMission(), () => _missions is not null && _map is not null);
        SaveMissionAsCommand = new RelayCommand(SaveMissionAs, () => _missions is not null && _map is not null);
        OpenMissionCommand = new RelayCommand<SavedMissionItem>(item => { if (ConfirmDiscard()) OpenMission(item.Mission); },
            item => item is not null && item.Mission.Id != MissionId);
        DeleteMissionCommand = new RelayCommand<SavedMissionItem>(DeleteMission, item => item is not null);
        InitAdding();

        if (_map is not null)
        {
            SyncNodes();
            OpenLastMission();
        }
        RefreshSavedMissions();
    }

    /// <summary>The stop picker's entries.</summary>
    public ObservableCollection<StopChoice> Nodes { get; } = [];
    public ObservableCollection<StopViewModel> Stops { get; } = [];
    public ObservableCollection<PhaseViewModel> Phases { get; } = [];
    public ObservableCollection<LegRowViewModel> Legs { get; } = [];

    // ---------------- Custom locations (managed on the Locations pane) ----------------

    /// <summary>Custom locations changed: refresh the map and stop pickers, and move stops off deleted locations.</summary>
    private void OnLocationsChanged()
    {
        MoveStopsOffMissingLocations();
        ReloadMap();
    }

    /// <summary>Stops pointing at a deleted custom location move to the location's parent orbit.</summary>
    private void MoveStopsOffMissingLocations()
    {
        if (_map is null) return;
        var remaining = _locations.GetAll().Select(l => l.Key).ToHashSet();
        _suspend = true;
        foreach (var stop in Stops.Where(s => s.NodeKey.StartsWith("custom.") && !remaining.Contains(s.NodeKey) && !IsPlaceKey(s.NodeKey)))
        {
            var fallback = _map.TryGet(stop.NodeKey, out var node) ? _map.ParentOf(node)?.Key : null;
            stop.NodeKey = fallback ?? "kerbin.lo";
        }
        _suspend = false;
    }

    /// <summary>
    /// Keeps the stop-picker list in step with the map without clearing it (clearing would blank the pickers). A planet's
    /// or moon's orbits are one "Orbit" entry: its low orbit, whose height the stop then sets (see <see cref="ShowOrbit"/>).
    /// </summary>
    private void SyncNodes()
    {
        if (_map is null) return;
        var desired = new List<StopChoice>();
        foreach (var n in _map.Nodes.Where(n => !n.IsCustom))
        {
            if (!FoldsIntoOrbit(n)) desired.Add(new StopChoice(n.Key, n.DisplayName));
            else if (n.Kind == "LowOrbit") desired.Add(new StopChoice(n.Key, $"{n.BodyName} — Orbit"));
            desired.AddRange(_map.ChildrenOf(n).Where(c => c.IsCustom && !FoldsIntoOrbit(c)).Select(c => new StopChoice(c.Key, c.DisplayName)));
        }
        var keys = desired.Select(n => n.Key).ToHashSet();
        foreach (var stale in Nodes.Where(n => !keys.Contains(n.Key)).ToList()) Nodes.Remove(stale);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Nodes.Count && Nodes[i].Key == desired[i].Key) { if (Nodes[i] != desired[i]) Nodes[i] = desired[i]; }
            else Nodes.Insert(i, desired[i]);
        }
    }

    /// <summary>Phases for the rocket diagram: last phase on top, launch at the bottom.</summary>
    public IEnumerable<PhaseViewModel> PhasesTopDown => Phases.Reverse().ToList();

    public RelayCommand AddStopCommand { get; }
    public RelayCommand<StopViewModel> MoveStopUpCommand { get; }
    public RelayCommand<StopViewModel> MoveStopDownCommand { get; }
    public RelayCommand<StopViewModel> RemoveStopCommand { get; }
    public RelayCommand ReverseStopsCommand { get; }
    public RelayCommand AddPhaseCommand { get; }
    public RelayCommand<PhaseViewModel> RemovePhaseCommand { get; }
    public RelayCommand ResetPhasesCommand { get; }
    public RelayCommand ResetMapEditsCommand { get; }

    // ---------------- Leg tools ----------------

    /// <summary>The route as the Δv map gives it, before leg tools replace any values.</summary>
    private IReadOnlyList<RouteSegment> _mapSegments = [];

    public RelayCommand<StopViewModel> OpenTransferCommand { get; }
    public RelayCommand<StopViewModel> RemoveTransferCommand { get; }

    public RelayCommand<StopViewModel> OpenDeployCommand { get; }
    public RelayCommand<StopViewModel> RemoveDeployCommand { get; }
    public RelayCommand<StopViewModel> AddLandingCommand { get; }
    public RelayCommand<StopViewModel> RemoveLandingCommand { get; }

    /// <summary>Starts a landing at a spot: the save's first flag if there is one, else the first landmark, else 0°, 0°.</summary>
    private void AddLanding(StopViewModel stop)
    {
        var targets = stop.LandingTargets;
        stop.Landing = new LandingLeg(targets.FirstOrDefault(t => t.Source == "Flag") ?? targets.FirstOrDefault()
                                      ?? new LandingTarget("Entered spot", "Entered", 0, 0));
    }

    /// <summary>A stop's transfer leg should be opened in the Transfer Planner.</summary>
    public event Action<TransferLegLink>? TransferRequested;

    /// <summary>A stop's constellation deployment should be opened in Resonant Orbits.</summary>
    public event Action<DeployLink>? DeployRequested;

    public RelayCommand<StopViewModel> OpenRendezvousCommand { get; }
    public RelayCommand<StopViewModel> RemoveRendezvousCommand { get; }

    /// <summary>A stop's rendezvous leg should be opened on the Rendezvous pane.</summary>
    public event Action<RendezvousLegLink>? RendezvousRequested;

    /// <summary>The Rendezvous link for the leg arriving at <paramref name="owner"/> (a stop), if it still has a rendezvous.</summary>
    public RendezvousLegLink? RendezvousLinkFor(object owner) =>
        owner is StopViewModel { HasRendezvous: true } stop && Stops.Contains(stop) ? RendezvousLink(stop) : null;

    private RendezvousLegLink? RendezvousLink(StopViewModel stop)
    {
        var index = Stops.IndexOf(stop);
        if (_map is null || index < 1 || index > _mapSegments.Count) return null;
        var legs = _mapSegments[index - 1].Legs;
        if (FindRendezvous(legs, Stops[index - 1], stop, PlacedLocations()) is not { } span
            || Location(span.TargetLocationId) is not { } target) return null;
        var title = $"Stop {index} → {index + 1}: {_map[Stops[index - 1].NodeKey].DisplayName} → {_map[stop.NodeKey].DisplayName}";
        return new RendezvousLegLink(stop, title, span, legs[span.Start].From.AltitudeM ?? 80_000, stop.Rendezvous,
            plan => stop.Rendezvous = plan)
        {
            EarliestUt = ArrivalAt(index - 1),
            Target = target,
            Chaser = span.ChaserLocationId is { } chaser ? Location(chaser) : null,
        };
    }

    private RendezvousSpan? FindRendezvous(IReadOnlyList<RouteLeg> legs, StopViewModel from, StopViewModel to, HashSet<long> placed) =>
        LegTools.FindRendezvous(legs, _map![from.NodeKey], _map[to.NodeKey], placed.Contains);

    private HashSet<long> PlacedLocations() =>
        _locations.GetAll().Concat(_places.Values).Where(l => l.IsPlaced).Select(l => l.Id).ToHashSet();

    private void OpenRendezvous(StopViewModel stop)
    {
        if (RendezvousLink(stop) is { } link) RendezvousRequested?.Invoke(link);
    }

    private void RemoveRendezvous(StopViewModel stop)
    {
        stop.Rendezvous = null;
        RouteChanged?.Invoke();
    }

    /// <summary>The stops or the whole mission changed: tools open on a leg should check it's still there.</summary>
    public event Action? RouteChanged;

    /// <summary>The Transfer Planner link for the leg arriving at <paramref name="owner"/> (a stop), if it still has a transfer.</summary>
    public TransferLegLink? TransferLinkFor(object owner) =>
        owner is StopViewModel { HasTransfer: true } stop && Stops.Contains(stop) ? TransferLink(stop) : null;

    private TransferLegLink? TransferLink(StopViewModel stop)
    {
        var index = Stops.IndexOf(stop);
        if (_map is null || index < 1 || index > _mapSegments.Count) return null;
        var segment = _mapSegments[index - 1];
        if (LegTools.FindTransfer(segment.Legs, _bodies) is not { } span) return null;
        var title = $"Stop {index} → {index + 1}: {_map[Stops[index - 1].NodeKey].DisplayName} → {_map[stop.NodeKey].DisplayName}";
        return new TransferLegLink(stop, title, span, stop.Transfer, choice => stop.Transfer = choice) { EarliestUt = ArrivalAt(index - 1) };
    }

    /// <summary>The Resonant Orbits link for the deployment at <paramref name="owner"/> (a stop), if it still has one.</summary>
    public DeployLink? DeployLinkFor(object owner) =>
        owner is StopViewModel { HasDeploy: true } stop && Stops.Contains(stop) ? DeployLink(stop) : null;

    private DeployLink? DeployLink(StopViewModel stop)
    {
        var index = Stops.IndexOf(stop);
        if (_map is null || index < 1 || !CanDeployAt(_map[stop.NodeKey])) return null;
        var node = _map[stop.NodeKey];
        return new DeployLink(stop, $"Stop {index + 1}: {node.DisplayName}", _bodies[node.BodyName], node.AltitudeM!.Value, stop.Deploy,
            choice => stop.Deploy = choice with { PhaseIndex = stop.Deploy?.PhaseIndex });
    }

    /// <summary>A deployment needs a circular orbit to release into (custom locations can be elliptical).</summary>
    private bool CanDeployAt(DvMapNode node) =>
        LegTools.CanDeploy(node, _bodies)
        && (node.CustomLocationId is not { } id || Location(id)?.IsCircular == true);

    private void OpenDeploy(StopViewModel stop)
    {
        if (DeployLink(stop) is { } link) DeployRequested?.Invoke(link);
    }

    private void RemoveDeploy(StopViewModel stop)
    {
        stop.Deploy = null;
        RouteChanged?.Invoke();
    }

    private void OpenTransfer(StopViewModel stop)
    {
        if (TransferLink(stop) is { } link) TransferRequested?.Invoke(link);
    }

    private void RemoveTransfer(StopViewModel stop)
    {
        stop.Transfer = null;
        RouteChanged?.Invoke();
    }

    /// <summary>The deployment at each stop worked out (null where there is none or it doesn't work), by stop index.</summary>
    private DeployResult?[] _deploys = [];

    /// <summary>Each leg with its tools applied (segment i arrives at stop i + 1), and each stop told what its leg offers.</summary>
    private IReadOnlyList<RouteSegment> ApplyTools(IReadOnlyList<RouteSegment> segments)
    {
        _mapSegments = segments;
        Stops[0].SetTools(false, null);
        Stops[0].SetRendezvous(false, false);
        Stops[0].SetLanding(false, null, []);
        Stops[0].SetLaunch(false, null, false);
        var placed = PlacedLocations();
        _deploys = new DeployResult?[Stops.Count];
        for (var i = 0; i < Stops.Count; i++)
        {
            var node = _map![Stops[i].NodeKey];
            var canDeploy = i > 0 && CanDeployAt(node);
            _deploys[i] = canDeploy && Stops[i].Deploy is { } step
                ? LegTools.EvaluateDeploy(node, step, _bodies, continues: i < Stops.Count - 1)
                : null;
            Stops[i].SetDeploy(canDeploy, _deploys[i]);
            ShowOrbit(Stops[i], node);
        }
        _arrivals = new double?[Stops.Count];
        var launches = new List<(int Segment, LaunchPlan Plan)>();
        var applied = segments.Select((segment, index) =>
        {
            var stop = Stops[segment.ToStop];
            var transfer = stop.Transfer is { } choice ? LegTools.EvaluateTransfer(segment.Legs, choice, _bodies) : null;
            stop.SetTools(LegTools.FindTransfer(segment.Legs, _bodies) is not null, transfer);
            var rendezvousSpan = FindRendezvous(segment.Legs, Stops[segment.ToStop - 1], stop, placed);
            var rendezvous = stop.Rendezvous is { } plan && rendezvousSpan is { } span && plan.TargetLocationId == span.TargetLocationId ? plan : null;
            var earliest = ArrivalAt(segment.ToStop - 1);
            stop.SetRendezvous(rendezvousSpan is not null, rendezvous is not null, rendezvous?.StartsBefore(earliest) == true);
            _arrivals[segment.ToStop] = rendezvous?.ArrivalUt ?? transfer?.Window.ArrivalUt;

            var landingBody = LegTools.FindLanding(segment.Legs) >= 0 ? _bodies.GetValueOrDefault(segment.Legs[^1].To.BodyName) : null;
            var landing = stop.Landing is { } spot && landingBody is not null ? LegTools.EvaluateLanding(segment.Legs, spot, _bodies, name => _surfaceBakes?.Get(name)) : null;
            stop.SetLanding(landingBody is not null, landing, landingBody is null ? [] : Landing.Targets(landingBody, _sync?.Save));

            var legs = rendezvous is not null ? LegTools.ApplyRendezvous(segment.Legs, rendezvousSpan!, rendezvous, earliest)
                : transfer is not null ? LegTools.ApplyTransfer(segment.Legs, transfer)
                : segment.Legs;
            if (landing is not null) legs = LegTools.ApplyLanding(legs, landing);

            // A rendezvous from KSC picks its own launch into the target's plane.
            var canLaunch = rendezvous is null && LegTools.FindLaunch(segment.Legs, _bodies) >= 0;
            var launch = canLaunch && stop.Launch is { } inclined ? LegTools.EvaluateLaunch(segment.Legs, inclined, _bodies) : null;
            if (launch is not null)
            {
                legs = LegTools.ApplyLaunch(legs, launch);
                launches.Add((index, launch));
            }
            stop.SetLaunch(canLaunch, launch, launch is not null && MeasuresPlaneChanges(segments, index, launch.Body.Name));
            return ReferenceEquals(legs, segment.Legs) ? segment : segment with { Legs = legs };
        }).ToList();
        foreach (var (index, plan) in launches)
            applied = LegTools.MeasurePlaneChanges(applied, index, plan, Location).ToList();
        return applied;
    }

    /// <summary>The mission goes on (from segment <paramref name="index"/>) to a custom orbit around the body before landing or leaving it.</summary>
    private static bool MeasuresPlaneChanges(IReadOnlyList<RouteSegment> segments, int index, string bodyName) =>
        segments.Skip(index).SelectMany(s => s.Legs)
            .TakeWhile(l => l.To.BodyName == bodyName && l.To.Kind != "Surface")
            .Any(l => l.To.IsCustom);

    /// <summary>When each stop is reached, where a leg tool knows it (a transfer's or rendezvous's arrival), by stop index.</summary>
    private double?[] _arrivals = [];

    /// <summary>A new tool on the leg leaving stop <paramref name="index"/> starts no sooner than the mission gets there.</summary>
    private double? ArrivalAt(int index) => index >= 0 && index < _arrivals.Length ? _arrivals[index] : null;

    // ---------------- Map view bindings ----------------

    public DeltaVMap? Map => _map;
    public IReadOnlyCollection<string> RouteEdgeKeys => Legs.SelectMany(l => l.Leg.EdgeKeys).ToHashSet();
    public IReadOnlyList<string> StopKeys => Stops.Select(s => s.NodeKey).ToList();
    public int EditedCount => _map?.Nodes.Count(n => n.IsOverridden) ?? 0;
    public string EditedText => EditedCount switch
    {
        0 => "All values are the published map values.",
        1 => "1 value edited (shown in amber).",
        var n => $"{n} values edited (shown in amber).",
    };

    /// <summary>Clicking a stop on the map adds it after the last stop.</summary>
    public void AddStopFromMap(DvMapNode node)
    {
        Stops.Add(new StopViewModel(new MissionStop(node.Key), StopChanged, RefreshLegs));
        StopChanged();
    }

    /// <summary>Called by the map or the legs list when the user edits a value.</summary>
    public void EditEdge(string nodeKey, double? dv)
    {
        _maps.SetOverride(nodeKey, dv);
        ReloadMap();
    }

    // ---------------- Options ----------------

    /// <summary>Applies to every leg where aerobraking is possible.</summary>
    public bool UseAerobraking
    {
        get => _useAerobraking;
        set
        {
            if (!SetProperty(ref _useAerobraking, value)) return;
            foreach (var leg in Legs) leg.UseAerobrake = value;
            UpdateDirty();
        }
    }

    /// <summary>Adds the map's worst-case plane change Δv to every leg that has one.</summary>
    public bool IncludePlaneChanges
    {
        get => _includePlaneChanges;
        set
        {
            if (!SetProperty(ref _includePlaneChanges, value)) return;
            foreach (var leg in Legs) leg.IncludePlaneChange = value;
            UpdateDirty();
        }
    }

    public string RouteSummary => _map is null ? "" : string.Join(" → ", Stops.Select(s => _map[s.NodeKey].DisplayName));

    // ---------------- Checking a saved design ----------------

    /// <summary>"No design" plus every saved design in the linked save.</summary>
    public ObservableCollection<DesignChoice> DesignChoices { get; } = [];

    /// <summary>The design to check against the mission; refreshed (by file) when KSP saves it again.</summary>
    public DesignChoice? SelectedDesign
    {
        get => _selectedDesign;
        set
        {
            if (!SetProperty(ref _selectedDesign, value)) return;
            // The picker passes through null while the list is refilled; that isn't the user clearing the choice.
            if (value is not null) _designPath = value.Design is null ? null : value.Path;
            Recalculate();
        }
    }

    public bool HasDesign => _check is not null;

    /// <summary>Stage blocks for the design column, last stage on top (like the phases).</summary>
    public IReadOnlyList<StageBlock> StageBlocksTopDown { get; private set; } = [];

    public IReadOnlyList<PhaseCheckRow> PhaseChecks { get; private set; } = [];

    /// <summary>
    /// Margins are wiggle room, not a requirement: a design that flies the route but not every margin is still enough,
    /// just with less room for error.
    /// </summary>
    public string DesignVerdict
    {
        get
        {
            if (_check is not { } c) return "";
            var dv = c.Status switch
            {
                Coverage.Comfortable => $"Enough — {Format.Speed0(c.SpareOverMargins)} to spare beyond your margins",
                Coverage.InsideMargin => $"Enough — {Format.Speed0(c.SpareOverRequired)} to spare, inside your {Format.Speed0(c.MarginDeltaV)} of margins",
                _ => $"Not enough — short by {Format.Speed0(c.Phases.Sum(p => p.Shortfall))} before any margin",
            };
            // Δv isn't the whole story: a stage too weak for a body's gravity can't land or lift off there.
            var worst = c.Surfaces.Where(t => t.Rating is TwrRating.TooLow or TwrRating.Low)
                .OrderBy(t => t.Rating == TwrRating.TooLow ? 0 : 1).FirstOrDefault();
            if (worst is null) return dv;
            var where = worst.Event.Kind == SurfaceEventKind.Landing ? $"landing on {worst.Event.BodyName}" : $"lift-off from {worst.Event.BodyName}";
            return worst.Rating == TwrRating.TooLow
                ? $"{dv}\nBut TWR {worst.Twr:0.00} is too low for the {where}."
                : $"{dv}\nTWR {worst.Twr:0.00} is low for the {where}.";
        }
    }

    /// <summary>
    /// Comfortable / InsideMargin / Short for the verdict's colour: the Δv verdict, made amber by a low TWR and red by
    /// one below 1 at any landing or lift-off.
    /// </summary>
    public Coverage? DesignStatus
    {
        get
        {
            if (_check is not { } c) return null;
            var twr = c.Surfaces.Any(t => t.Rating == TwrRating.TooLow) ? Coverage.Short
                : c.Surfaces.Any(t => t.Rating == TwrRating.Low) ? Coverage.InsideMargin
                : Coverage.Comfortable;
            return (Coverage)Math.Max((int)c.Status, (int)twr);
        }
    }

    public string DesignDeltaVText => _check is { } c ? Format.Speed0(c.DesignDeltaV) : "";

    /// <summary>Every landing and lift-off on the route with the TWR of the stage firing then.</summary>
    public IReadOnlyList<SurfaceRow> SurfaceChecks { get; private set; } = [];

    public bool HasSurfaceChecks => SurfaceChecks.Count > 0;

    /// <summary>
    /// Landings and lift-offs on the route: legs that touch down on or leave a surface, where they fall in their
    /// phase's Δv. Legs that cost nothing (parachutes, aerobraking) don't need engines, so they're left out.
    /// </summary>
    private List<SurfaceEvent> SurfaceEvents()
    {
        var events = new List<SurfaceEvent>();
        var offset = new double[Phases.Count];
        foreach (var row in Legs)
        {
            var setting = row.Setting(Phases);
            var phase = Math.Clamp(setting.PhaseIndex, 0, Phases.Count - 1);
            var dv = MissionPlanner.EffectiveDv(row.Leg, setting);
            if (dv > 0.5)
            {
                if (row.Leg.From.Kind == "Surface" && _bodies.TryGetValue(row.Leg.From.BodyName, out var from))
                    events.Add(new SurfaceEvent(SurfaceEventKind.LiftOff, phase, offset[phase], from.Name, from.SurfaceGravity,
                        from.Atmosphere?.SeaLevelPressureAtm ?? 0));
                if (row.Leg.To.Kind == "Surface" && _bodies.TryGetValue(row.Leg.To.BodyName, out var to))
                    events.Add(new SurfaceEvent(SurfaceEventKind.Landing, phase, offset[phase], to.Name, to.SurfaceGravity,
                        to.Atmosphere?.SeaLevelPressureAtm ?? 0));
            }
            offset[phase] += dv;
            if (row.Deploy is { } deploy && Phases.IndexOf(deploy.Phase) is var p and >= 0) offset[p] += deploy.Result.CarrierDv;
        }
        return events;
    }

    public string DesignHint => _designs is null || DesignChoices.Count <= 1
        ? "Link a KSP save (Tools › Settings) to check one of your saved designs."
        : "Stages fill the phases in order; vacuum Δv (the Δv map already counts atmospheric losses).";

    private void RefreshDesignChoices()
    {
        var keep = _designPath;
        DesignChoices.Clear();
        DesignChoices.Add(new DesignChoice(null, "— No design —"));
        foreach (var d in _designs?.Entries ?? [])
            if (d.Report is { Stages.Count: > 0 }) DesignChoices.Add(new DesignChoice(d, d.Title));
        // Same file again (KSP re-saved it): keep it selected with its new numbers. A mission's design that isn't
        // in the list (save not read yet, file gone for now) stays remembered for when it comes back.
        _designPath = keep;
        _selectedDesign = SelectDesign(keep);
        OnPropertyChanged(nameof(SelectedDesign));
        OnPropertyChanged(nameof(DesignHint));
        if (Phases.Count > 0) Recalculate();
    }

    private void CheckDesign(double missionTotal)
    {
        _check = SelectedDesign?.Design?.Report is { } report
            ? DesignCheck.Check(Phases.Select(p => new PhaseNeed(p.Name, p.RequiredDv, p.MarginDv)).ToList(), report, SurfaceEvents())
            : null;

        if (_check is { } c && SelectedDesign?.Design?.Report is { } design)
        {
            var scale = Math.Max(missionTotal, c.DesignDeltaV);
            StageBlocksTopDown = c.Stages.Select(s =>
            {
                var main = s.Phases.OrderByDescending(p => p.DeltaV).Select(p => (int?)p.Phase).FirstOrDefault();
                var label = s.Spare > 0.5 ? $"S{s.Stage} · spare {s.Spare:N0}" : $"Stage {s.Stage}";
                var tip = string.Join("\n", s.Phases.Select(p => $"{Phases[p.Phase].Name}: {Format.Speed0(p.DeltaV)}")
                    .Append(s.Spare > 0.5 ? $"Spare: {Format.Speed0(s.Spare)}" : null).OfType<string>());
                return new StageBlock(label, Format.Speed0(s.DeltaV), main is { } m ? Phases[m].Color : "#90A4AE",
                    RocketDiagram.Height(s.DeltaV, scale), tip);
            }).Reverse().ToList();
            PhaseChecks = c.Phases.Select((p, i) =>
            {
                var stages = $"stage{(p.Stages.Distinct().Count() == 1 ? "" : "s")} {string.Join(", ", p.Stages.Distinct())}";
                var detail = p.Required + p.Margin <= 0 ? "nothing needed"
                    : p.Status switch
                    {
                        Coverage.Comfortable => stages,
                        Coverage.InsideMargin => $"{stages} · uses part of its margin",
                        _ => $"short {Format.Speed0(p.Shortfall)}",
                    };
                return new PhaseCheckRow(p.Name, Phases[i].Color, Format.Speed0(p.Required + p.Margin), detail, p.Status);
            }).ToList();
        }
        SurfaceChecks = _check?.Surfaces.Select(s =>
        {
            var e = s.Event;
            var title = e.Kind == SurfaceEventKind.Landing ? $"Landing on {e.BodyName}" : $"Lift-off from {e.BodyName}";
            var where = $"{e.SurfaceGravity / VesselAnalysis.G0:0.00} g" + (e.SurfacePressureAtm > 0 ? $", {e.SurfacePressureAtm:0.###} atm" : "");
            var detail = s.Rating switch
            {
                TwrRating.NoStage => $"no stage left · {where}",
                TwrRating.TooLow => $"stage {s.Stage} · {Units.Tonnes(s.MassT)} · {where} · " +
                                    (e.Kind == SurfaceEventKind.Landing ? "can't slow the descent" : "can't lift off"),
                TwrRating.Low => $"stage {s.Stage} · {Units.Tonnes(s.MassT)} · {where} · low, little room to correct",
                _ => $"stage {s.Stage} · {Units.Tonnes(s.MassT)} · {where}",
            };
            return new SurfaceRow(title, detail, s.Stage is null ? "—" : $"{s.Twr:0.00}", s.Rating);
        }).ToList() ?? [];

        if (_check is null)
        {
            StageBlocksTopDown = [];
            PhaseChecks = [];
        }
        foreach (var p in new[] { nameof(HasDesign), nameof(StageBlocksTopDown), nameof(PhaseChecks), nameof(DesignVerdict),
                     nameof(DesignStatus), nameof(DesignDeltaVText), nameof(SurfaceChecks), nameof(HasSurfaceChecks) })
            OnPropertyChanged(p);
    }

    public string TotalRequiredText => Format.Speed0(Phases.Sum(p => p.RequiredDv));
    public string TotalMarginText => "+" + Format.Speed0(Phases.Sum(p => p.MarginDv));
    public string TotalText => Format.Speed0(Phases.Sum(p => p.TotalDv));

    // ---------------- Stops ----------------

    private void SetStops(IEnumerable<MissionStop> stops)
    {
        if (_map is null) return;
        _suspend = true;
        Stops.Clear();
        foreach (var s in stops)
            if (_map.TryGet(s.NodeKey, out _)) Stops.Add(new StopViewModel(s, StopChanged, RefreshLegs));
        _suspend = false;
        StopChanged();
    }

    private void StopChanged()
    {
        if (_suspend) return;
        for (var i = 0; i < Stops.Count; i++) Stops[i].SetPosition(i, Stops.Count);
        RebuildRoute(keepPhaseTuning: true);
        RouteChanged?.Invoke();
    }

    private void AddStop()
    {
        // The mission is built in order: the new stop goes at the end, starting at the last stop's location.
        Stops.Add(new StopViewModel(new MissionStop(Stops[^1].NodeKey), StopChanged, RefreshLegs));
        StopChanged();
    }

    private void MoveStop(StopViewModel stop, int delta)
    {
        var from = Stops.IndexOf(stop);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Stops.Count) return;
        Stops.Move(from, to);
        StopChanged();
    }

    private void RemoveStop(StopViewModel stop)
    {
        if (Stops.Count <= 1) return;
        Stops.Remove(stop);
        StopChanged();
    }

    private void ReverseStops()
    {
        // Each leg is flown the other way now, so the tools of the old legs don't apply.
        var reversed = Stops.Reverse().Select(s => s.ToDefinition() with { Transfer = null, Rendezvous = null }).ToList();
        SetStops(reversed);
    }

    // ---------------- Route & phases ----------------

    /// <summary>The default split last applied; while the phases still equal it, a route change re-splits them.</summary>
    private (IReadOnlyList<PhaseDefinition> Phases, IReadOnlyList<LegSetting> Legs)? _defaultSplit;

    /// <summary>
    /// Recomputes legs from the stops. While the phases are still the default split they follow the new route's
    /// defaults; once the user has changed them (<paramref name="keepPhaseTuning"/>) their phases and leg choices are
    /// kept and only new legs get defaults. Reset to defaults passes false.
    /// </summary>
    private void RebuildRoute(bool keepPhaseTuning)
    {
        if (_suspend || _map is null || Stops.Count == 0) return;
        var stops = Stops.Select(s => _map[s.NodeKey]).ToList();
        var segments = Route(stops);

        var currentPhases = Phases.Select(p => p.ToDefinition()).ToList();
        var currentSettings = Legs.Select(l => l.Setting(Phases)).ToList();
        var untouched = _defaultSplit is { } split
            && split.Phases.SequenceEqual(currentPhases) && split.Legs.SequenceEqual(currentSettings);
        if (keepPhaseTuning && !untouched)
        {
            var (kept, keptSettings) = MissionPlanner.KeepPhases(currentPhases, Legs.Select(l => l.Leg).ToList(), currentSettings,
                segments, stops, UseAerobraking, IncludePlaneChanges);
            _defaultSplit = null;
            Apply(segments, kept, keptSettings);
            return;
        }

        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops, UseAerobraking);
        settings = settings.Select(s => s with { IncludePlaneChange = IncludePlaneChanges }).ToList();
        _defaultSplit = (phases, settings);
        Apply(segments, phases, settings);
    }

    /// <summary>Reloads map values (after an edit) without touching the user's phase and leg choices.</summary>
    private void ReloadMap()
    {
        _baseMap = _maps.GetMap();
        _map = MapWithPlaces();
        SyncNodes();
        RefreshLegs();
        OnPropertyChanged(nameof(Map));
        OnPropertyChanged(nameof(EditedCount));
        OnPropertyChanged(nameof(EditedText));
    }

    /// <summary>Rebuilds the leg rows from the current stops and map, keeping phases and per-leg choices.</summary>
    private void RefreshLegs()
    {
        if (_suspend || _map is null || Stops.Count == 0) return;
        var phases = Phases.Select(p => p.ToDefinition()).ToList();
        var settings = Legs.Select(l => l.Setting(Phases)).ToList();
        var segments = Route(Stops.Select(s => _map[s.NodeKey]).ToList());
        Apply(segments, phases, settings);
    }

    private void Apply(IReadOnlyList<RouteSegment> segments, IReadOnlyList<PhaseDefinition> phases, IReadOnlyList<LegSetting> settings)
    {
        segments = ApplyTools(segments);
        Phases.Clear();
        for (var i = 0; i < phases.Count; i++) Phases.Add(new PhaseViewModel(phases[i], i, Recalculate));

        Legs.Clear();
        var number = 0;
        foreach (var segment in segments)
        {
            for (var j = 0; j < segment.Legs.Count; j++)
            {
                var stop = Stops[segment.ToStop];
                var header = j == 0
                    ? $"To stop {segment.ToStop + 1}: {_map![stop.NodeKey].DisplayName}" + (string.IsNullOrWhiteSpace(stop.Note) ? "" : $"  —  {stop.Note}")
                    : null;
                var setting = number < settings.Count ? settings[number] : new LegSetting(0, UseAerobraking, IncludePlaneChanges);
                var phase = Phases[Math.Clamp(setting.PhaseIndex, 0, Phases.Count - 1)];
                // Edits rebuild the leg rows, so defer them until the editing TextBox has finished losing focus.
                var row = new LegRowViewModel(segment.Legs[j], number + 1, header, phase, setting, Recalculate,
                    (key, dv) => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => EditEdge(key, dv)));
                if (j == segment.Legs.Count - 1 && _deploys[segment.ToStop] is { } deploy && stop.Deploy is { } step)
                {
                    var deployPhase = step.PhaseIndex is { } p ? Phases[Math.Clamp(p, 0, Phases.Count - 1)] : phase;
                    row.Deploy = new DeployRowViewModel(
                        $"Deploy {step.SatelliteCount} satellites at {_map![stop.NodeKey].DisplayName}", deploy, deployPhase,
                        index => { stop.SetDeployPhase(index); Recalculate(); }, Phases);
                }
                Legs.Add(row);
                number++;
            }
        }

        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(AddAfterText));
        OnPropertyChanged(nameof(RouteEdgeKeys));
        OnPropertyChanged(nameof(StopKeys));
        Recalculate();
    }

    private void Recalculate()
    {
        var legs = Legs.Select(l => l.Leg).ToList();
        var settings = Legs.Select(l => l.Setting(Phases)).ToList();
        var summaries = MissionPlanner.Summarize(Phases.Select(p => p.ToDefinition()).ToList(), legs, settings, DeploySteps());
        var missionTotal = summaries.Sum(s => s.TotalDv);
        var design = SelectedDesign?.Design?.Report;
        var scale = design is null ? missionTotal : Math.Max(missionTotal, design.TotalDeltaVVacuum);
        for (var i = 0; i < Phases.Count; i++) Phases[i].Update(summaries[i], i, Phases.Count, scale, design is not null);
        CheckDesign(missionTotal);

        OnPropertyChanged(nameof(PhasesTopDown));
        OnPropertyChanged(nameof(TotalRequiredText));
        OnPropertyChanged(nameof(TotalMarginText));
        OnPropertyChanged(nameof(TotalText));
        UpdateDirty();
    }

    /// <summary>The carrier burns of each deployment, charged to the phase picked for it.</summary>
    private IEnumerable<(int, double)> DeploySteps() =>
        Legs.Where(l => l.Deploy is not null).Select(l => (Phases.IndexOf(l.Deploy!.Phase), l.Deploy!.Result.CarrierDv)).ToList();

    private void AddPhase()
    {
        Phases.Add(new PhaseViewModel(new PhaseDefinition($"Phase {Phases.Count + 1}"), Phases.Count, Recalculate));
        Recalculate();
    }

    private void RemovePhase(PhaseViewModel phase)
    {
        var index = Phases.IndexOf(phase);
        if (index < 0 || Phases.Count <= 1) return;
        var fallback = Phases[index > 0 ? index - 1 : 1];
        foreach (var leg in Legs.Where(l => l.Phase == phase)) leg.Phase = fallback;
        foreach (var leg in Legs.Where(l => l.Deploy?.Phase == phase)) leg.Deploy!.Phase = fallback;
        Phases.Remove(phase);
        Recalculate();
    }

    private void ResetMapEdits()
    {
        _maps.ClearOverrides();
        ReloadMap();
    }
}
