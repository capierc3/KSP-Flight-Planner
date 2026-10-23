using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

public sealed class FlightTests
{
    private static readonly CelestialBody Kerbin = new()
    {
        Name = "Kerbin",
        MassKg = 5.29151583439215E+22,
        Mu = 5.29151583439215E+22 * OrbitMath.G,
        EquatorialRadiusM = 600000,
        RotationPeriodS = 21549.4251830898,
        Atmosphere = new Atmosphere(HeightM: 70000),
        SoiRadiusM = 84159286.4796305,
    };

    private const double Start = 1_000_000;

    private static OrbitalElements Circular(double altitudeM, double inclinationDeg = 0.5) =>
        new(Kerbin.EquatorialRadiusM + altitudeM, 0, inclinationDeg, 30, 0, 1.0, Start);

    private static ResonantOrbitResult Carrier(int n, double altitudeM, bool dive = false) =>
        ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(Kerbin, n, altitudeM, dive));

    [Fact]
    public void From_a_circular_orbit_at_the_altitude_it_enters_the_carrier_orbit_then_circularizes_each_satellite_one_lap_apart()
    {
        var carrier = Carrier(3, 2_863_334); // the reference case: Δv 84.57 m/s, carrier Ap 4,327.7 km
        var plan = ResonantDeployment.Plan(Kerbin, carrier, Circular(2_863_334), 3, Start + 300);

        Assert.True(plan.Inserting);
        Assert.Empty(plan.Warnings);
        Assert.Equal(["Enter the carrier orbit", "Circularize satellite 1", "Circularize satellite 2", "Circularize satellite 3"],
            plan.Burns.Select(b => b.Title));

        var insert = plan.Burns[0];
        Assert.Equal(Start + 300, insert.Ut, 3);
        Assert.Equal(84.57, insert.Burn.Prograde, 1);
        Assert.Equal(0, insert.Burn.Normal, 6);

        var onCarrier = Rendezvous.OrbitAfter(insert.Ut, insert.Burn, plan.StartOrbit, Kerbin.Mu);
        Assert.Equal(carrier.CarrierPeriodS, Kepler.OrbitalPeriod(onCarrier, Kerbin.Mu), 0);

        // Released at the carrier's periapsis (the deployment altitude), one carrier lap apart, slowing to circular.
        for (var i = 1; i <= 3; i++)
        {
            var b = plan.Burns[i];
            Assert.True(b.ByReleasedCraft);
            Assert.Equal(insert.Ut + i * carrier.CarrierPeriodS, b.Ut, 0);
            Assert.Equal(-84.57, b.Burn.Prograde, 1);
            var satellite = Rendezvous.OrbitAfter(b.Ut, b.Burn, onCarrier, Kerbin.Mu);
            Assert.True(satellite.Eccentricity < 1e-6);
            Assert.Equal(Kerbin.EquatorialRadiusM + 2_863_334, satellite.SemiMajorAxisM, 0);
        }
    }

    [Fact]
    public void Already_on_the_carrier_orbit_it_only_plans_the_releases_left()
    {
        var carrier = Carrier(4, 1_000_000);
        var onCarrier = new OrbitalElements(carrier.CarrierSemiMajorAxisM, carrier.CarrierEccentricity, 0.5, 30, 0, 2.0, Start);

        var plan = ResonantDeployment.Plan(Kerbin, carrier, onCarrier, 2, Start);

        Assert.False(plan.Inserting);
        Assert.Equal(2, plan.Burns.Count);
        Assert.All(plan.Burns, b => Assert.True(b.ByReleasedCraft));
        Assert.Equal(0, Kepler.MeanAnomalyAt(onCarrier, Kerbin.Mu, plan.Burns[0].Ut), 6); // at periapsis
        Assert.Equal(carrier.CarrierPeriodS, plan.Burns[1].Ut - plan.Burns[0].Ut, 0);
    }

    [Fact]
    public void Partway_through_the_next_release_is_the_next_pass_however_soon()
    {
        var carrier = Carrier(3, 1_000_000);
        var onCarrier = new OrbitalElements(carrier.CarrierSemiMajorAxisM, carrier.CarrierEccentricity, 0.5, 30, 0, 0, Start);
        var pass = Start + carrier.CarrierPeriodS;
        var now = pass - 60; // a minute before the shared apsis, inside the 5-minute lead

        var skipping = ResonantDeployment.Plan(Kerbin, carrier, onCarrier, 2, now + 300, 2);
        var keeping = ResonantDeployment.Plan(Kerbin, carrier, onCarrier, 2, now + 300, 2, nextReleaseAfterUt: now);

        Assert.Equal(pass + carrier.CarrierPeriodS, skipping.Burns[0].Ut, 0); // a lap late: the spacing would be off
        Assert.Equal(pass, keeping.Burns[0].Ut, 0);
        Assert.Equal("Circularize satellite 2", keeping.Burns[0].Title);
    }

    [Fact]
    public void A_dive_orbit_shares_its_apoapsis_and_satellites_speed_up_to_circularize()
    {
        var carrier = Carrier(3, 2_863_334, dive: true);
        var plan = ResonantDeployment.Plan(Kerbin, carrier, Circular(2_863_334), 3, Start);

        Assert.True(plan.Burns[0].Burn.Prograde < 0); // drop the far side
        Assert.All(plan.Burns.Skip(1), b => Assert.Equal(carrier.InjectionDeltaV, b.Burn.Prograde, 1));
    }

    [Fact]
    public void An_orbit_that_misses_the_altitude_first_brings_its_nearest_apsis_there()
    {
        var carrier = Carrier(3, 2_863_334);
        var elliptical = new OrbitalElements(Kerbin.EquatorialRadiusM + 1_500_000, 0.3, 0.5, 30, 40, 1.0, Start);

        var plan = ResonantDeployment.Plan(Kerbin, carrier, elliptical, 3, Start);

        Assert.Empty(plan.Warnings);
        Assert.Equal(["Bring the apoapsis to 2,863.3 km", "Enter the carrier orbit", "Circularize satellite 1", "Circularize satellite 2", "Circularize satellite 3"],
            plan.Burns.Select(b => b.Title));
        // The apoapsis is closer, so it's raised from the periapsis, then the carrier orbit starts there half an orbit on.
        Assert.Equal(0, Kepler.MeanAnomalyAt(elliptical, Kerbin.Mu, plan.Burns[0].Ut), 6);
        var raised = Rendezvous.OrbitAfter(plan.Burns[0].Ut, plan.Burns[0].Burn, elliptical, Kerbin.Mu);
        Assert.Equal(carrier.SatelliteRadiusM, raised.SemiMajorAxisM * (1 + raised.Eccentricity), 0);
        Assert.Equal(Kepler.OrbitalPeriod(raised, Kerbin.Mu) / 2, plan.Burns[1].Ut - plan.Burns[0].Ut, 0);

        var onCarrier = Rendezvous.OrbitAfter(plan.Burns[1].Ut, plan.Burns[1].Burn, raised, Kerbin.Mu);
        Assert.Equal(carrier.CarrierPeriodS, Kepler.OrbitalPeriod(onCarrier, Kerbin.Mu), 0);
        Assert.Equal(carrier.SatelliteRadiusM, onCarrier.SemiMajorAxisM * (1 - onCarrier.Eccentricity), 0);
    }

    [Fact]
    public void From_low_orbit_it_climbs_to_the_altitude_then_enters_the_carrier_orbit()
    {
        var carrier = Carrier(3, 2_863_334);
        var plan = ResonantDeployment.Plan(Kerbin, carrier, Circular(80_000), 3, Start + 300);

        Assert.Equal(5, plan.Burns.Count);
        Assert.Equal(Start + 300, plan.Burns[0].Ut, 3); // circular: right away
        Assert.Equal(667.65, plan.Burns[0].Burn.Prograde, 0.01); // docs/MATH.md §5.2's worked example
        Assert.Equal(515.84, plan.Burns[1].Burn.Prograde, 0.01);
        Assert.Equal(4_984.8, plan.Burns[1].Ut - plan.Burns[0].Ut, 0.1);
        var raised = Rendezvous.OrbitAfter(plan.Burns[0].Ut, plan.Burns[0].Burn, plan.StartOrbit, Kerbin.Mu);
        var onCarrier = Rendezvous.OrbitAfter(plan.Burns[1].Ut, plan.Burns[1].Burn, raised, Kerbin.Mu);
        Assert.Equal(carrier.CarrierPeriodS, Kepler.OrbitalPeriod(onCarrier, Kerbin.Mu), 0);
        Assert.Equal(carrier.SatelliteRadiusM, onCarrier.SemiMajorAxisM * (1 - onCarrier.Eccentricity), 0);
        Assert.Equal(plan.Burns[1].Ut + carrier.CarrierPeriodS, plan.Burns[2].Ut, 0);
    }

    [Fact]
    public void Drift_is_zero_on_the_planned_path_and_grows_when_a_burn_is_skipped()
    {
        var start = Circular(100_000);
        var burn = new FlightBurn("Raise", Start + 600, new NodeBurn(50, 5, -2));
        var flight = new Flight { StartOrbit = start, Burns = [burn, burn with { Title = "Satellite", Ut = Start + 700, ByReleasedCraft = true }] };
        var later = Start + 2_000;

        var flown = Rendezvous.OrbitAfter(burn.Ut, burn.Burn, start, Kerbin.Mu);
        Assert.Equal(0, flight.DriftM(flown, later, Kerbin.Mu)!.Value, 3);       // the released craft's burn doesn't move the plan
        Assert.True(flight.DriftM(start, later, Kerbin.Mu) > 10_000);              // never burned
        Assert.Equal(0, flight.DriftM(start, Start + 500, Kerbin.Mu)!.Value, 3);  // before the burn, still on plan
        Assert.Null(flight.DriftM(start, Start - 1, Kerbin.Mu));                   // before the plan's start orbit
    }
}
