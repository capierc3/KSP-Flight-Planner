using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using KRPC.Client;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;
using AtmospherePoint = KspFlightPlanner.Core.Models.AtmospherePoint;

namespace KspFlightPlanner.App.Services;

public enum KspLinkState { Disconnected, Connecting, Connected }

/// <summary>Maneuver nodes sent to the vessel being flown.</summary>
public sealed record NodesSent(string Vessel, int Count, int Skipped);

/// <summary>
/// A body's constants as the running game has them, for a surface bake. Angles in degrees.
/// <paramref name="AtmosphereDepthM"/> is 0 without air; <paramref name="Biomes"/> is empty without a solid surface.
/// </summary>
public sealed record BakeBodyInfo(string Name, double InitialRotationDeg, double RotationPeriodS, double RadiusM, double Mu,
    bool HasSolidSurface, double AtmosphereDepthM, IReadOnlyList<string> Biomes);

/// <summary>
/// A live connection to the running game through the kRPC mod's server. While connected it reads the game time every
/// couple of seconds and, in flight, every vessel's orbit (<see cref="Game"/>); it can also put maneuver nodes on the
/// vessel being flown.
/// </summary>
public sealed class KspLink : ObservableObject, IDisposable
{
    private const string ClientName = "KSP Flight Planner";

    // Connecting blocks until KSP replies, which waits for the player when kRPC asks them to allow the client.
    private static readonly TimeSpan AskPlayerAfter = TimeSpan.FromSeconds(3);

    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _poll;
    private readonly SemaphoreSlim _gate = new(1, 1); // one call at a time on the connection
    private Connection? _connection;
    private int _attempt;
    private bool _polling;
    private bool _vesselsDue;
    private DateTime _vesselsReadAt;
    private KspLinkState _state;
    private string _status = "KSP: not connected";
    private string? _problem;
    private string? _serverVersion;
    private GameScene? _scene;
    private LiveGame? _game;
    private TimeSpan? _lastRead;
    private TimeSpan? _lastVesselsRead;

    public KspLink(Func<AppSettings> settings)
    {
        _settings = settings;
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    /// <summary>Raised on the UI thread whenever <see cref="Game"/> is read again (or cleared on disconnect).</summary>
    public event Action<LiveGame?>? GameUpdated;

    public KspLinkState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(MenuText));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public bool IsConnected => State == KspLinkState.Connected;

    // How often to read, from Settings › Live connection (read each time, so a change applies at the next poll).
    private TimeSpan PollEvery => TimeSpan.FromSeconds(_settings().KrpcPollSeconds);
    private TimeSpan VesselsEvery => TimeSpan.FromSeconds(_settings().KrpcVesselsSeconds);

    /// <summary>Status-bar tooltip: what clicking does and how long the last reads took (to tune the read intervals).</summary>
    public string ToolTipText => !IsConnected ? MenuText
        : $"{MenuText}\nReads the game time every {PollEvery.TotalSeconds:0.#} s (last took {Ms(_lastRead)}) and every vessel's orbit " +
          $"every {VesselsEvery.TotalSeconds:0.#} s (last took {Ms(_lastVesselsRead)}).";

    private static string Ms(TimeSpan? t) => t is { } d ? $"{d.TotalMilliseconds:#,0} ms" : "—";

    /// <summary>Tools-menu label: what clicking it will do.</summary>
    public string MenuText => State switch
    {
        KspLinkState.Connected => "Disconnect from KSP",
        KspLinkState.Connecting => "Cancel Connecting to KSP",
        _ => "Connect to KSP",
    };

    /// <summary>One line for the status bar.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Why the last attempt failed or the connection dropped, or null.</summary>
    public string? Problem { get => _problem; private set => SetProperty(ref _problem, value); }

    /// <summary>The kRPC mod's version, once connected.</summary>
    public string? ServerVersion { get => _serverVersion; private set => SetProperty(ref _serverVersion, value); }

    /// <summary>Where the player is in the game (flight, VAB, tracking station…).</summary>
    public GameScene? Scene
    {
        get => _scene;
        private set { if (SetProperty(ref _scene, value)) OnPropertyChanged(nameof(InFlight)); }
    }

    /// <summary>Flying a vessel: the only scene where kRPC lists vessels and nodes can be added.</summary>
    public bool InFlight => Scene == GameScene.Flight;

    /// <summary>The game as last read: its time and, in flight, its vessels. Null when not connected.</summary>
    public LiveGame? Game { get => _game; private set => SetProperty(ref _game, value); }

    /// <summary>Connects, cancels a pending attempt, or disconnects, depending on the state.</summary>
    public async Task ToggleAsync()
    {
        if (State == KspLinkState.Disconnected) await ConnectAsync();
        else Disconnect();
    }

    public async Task ConnectAsync()
    {
        if (State != KspLinkState.Disconnected) return;
        var settings = _settings();
        var endpoint = $"{settings.KrpcAddress}:{settings.KrpcRpcPort}";
        var attempt = ++_attempt;
        State = KspLinkState.Connecting;
        Problem = null;
        Status = $"KSP: connecting to {endpoint}…";

        var connecting = Task.Run(async () =>
        {
            var address = await ResolveAsync(settings.KrpcAddress);
            return new Connection(ClientName, address, settings.KrpcRpcPort, settings.KrpcStreamPort);
        });
        if (await Task.WhenAny(connecting, Task.Delay(AskPlayerAfter)) != connecting && attempt == _attempt)
            Status = $"KSP: waiting at {endpoint} — click Allow in the game if kRPC asks";

        Connection connection;
        try
        {
            connection = await connecting;
        }
        catch (Exception ex)
        {
            if (attempt != _attempt) return;
            State = KspLinkState.Disconnected;
            Problem = Describe(ex, endpoint);
            Status = "KSP: not connected";
            return;
        }

        // Cancelled while waiting. The socket is only closed now that kRPC has answered: closing it while kRPC is still
        // waiting on the player leaves its server retrying a dead client and refusing new ones until restarted.
        if (attempt != _attempt)
        {
            connection.Dispose();
            return;
        }

        _connection = connection;
        State = KspLinkState.Connected;
        Status = "KSP: connected";
        _vesselsDue = true;
        try
        {
            ServerVersion = await CallAsync(c => c.KRPC().GetStatus().Version);
        }
        catch (Exception)
        {
            return; // CallAsync has already disconnected and said why
        }
        await PollAsync();
        _poll.Start();
    }

    public void Disconnect()
    {
        _attempt++; // abandons a pending attempt; it disposes its own connection when kRPC answers
        _poll.Stop();
        _connection?.Dispose();
        _connection = null;
        ServerVersion = null;
        Scene = null;
        Problem = null;
        State = KspLinkState.Disconnected;
        Status = "KSP: not connected";
        if (Game is not null)
        {
            Game = null;
            GameUpdated?.Invoke(null);
        }
    }

    public void Dispose() => Disconnect();

    /// <summary>Reads everything again now, vessels included (e.g. after the player switched craft).</summary>
    public Task RefreshAsync()
    {
        _vesselsDue = true;
        return PollAsync();
    }

    /// <summary>The vessel being flown and how many maneuver nodes it already has, or null outside flight.</summary>
    public async Task<(string Vessel, int Nodes)?> ActiveVesselNodesAsync()
    {
        if (!IsConnected) return null;
        return await CallAsync<(string, int)?>(c =>
        {
            var space = c.SpaceCenter();
            if (c.KRPC().GameScene != GameScene.Flight || space.ActiveVessel is not { } vessel) return null;
            return (vessel.Name, vessel.Control.Nodes.Count);
        });
    }

    /// <summary>
    /// Puts maneuver nodes on the vessel being flown, optionally removing the ones it has first. Nodes whose time has
    /// already passed are skipped. Node axes are as in <see cref="NodeBurn"/>, which match KSP's.
    /// </summary>
    public async Task<NodesSent> SendNodesAsync(IReadOnlyList<(double Ut, NodeBurn Burn)> nodes, bool replaceExisting)
    {
        var sent = await CallAsync(c =>
        {
            var space = c.SpaceCenter();
            if (c.KRPC().GameScene != GameScene.Flight || space.ActiveVessel is not { } vessel)
                throw new System.InvalidOperationException("Switch to the craft in KSP (flight view) first.");
            var control = vessel.Control;
            if (replaceExisting) control.RemoveNodes();
            var now = space.UT;
            var count = 0;
            foreach (var (ut, burn) in nodes.OrderBy(n => n.Ut))
            {
                if (ut <= now) continue;
                control.AddNode(ut, (float)burn.Prograde, (float)burn.Normal, (float)burn.Radial);
                count++;
            }
            return new NodesSent(vessel.Name, count, nodes.Count - count);
        });
        await RefreshAsync();
        return sent;
    }

    // ---------------- Surface bake (KSP › Bake Surface Data) ----------------

    /// <summary>The game's celestial bodies, by the planner's names (Kerbol, not Sun).</summary>
    /// <summary>Starts streaming the flown vessel's readings (for the flight log); null when not flying one.</summary>
    public Task<LiveTelemetry?> OpenTelemetryAsync() => CallAsync(LiveTelemetry.Open);

    /// <summary>How many parts the vessel being flown has (with launch clamps, on the pad).</summary>
    public Task<int> ActiveVesselPartCountAsync() => CallAsync(c => c.SpaceCenter().ActiveVessel.Parts.All.Count);

    /// <summary>Stops the readings' streams, if the connection they came over is still up.</summary>
    public async Task CloseTelemetryAsync(LiveTelemetry telemetry)
    {
        if (!IsConnected) return;
        try { await CallAsync(_ => { telemetry.Close(); return 0; }); }
        catch (Exception) { /* lost the game meanwhile: the streams went with it */ }
    }

    public Task<IReadOnlyList<string>> BodyNamesAsync() =>
        CallAsync<IReadOnlyList<string>>(c => c.SpaceCenter().Bodies.Keys.Select(PlannerBodyName).ToList());

    /// <summary>A body's constants and biome names as the game has them.</summary>
    public Task<BakeBodyInfo> ReadBodyAsync(string bodyName) => CallAsync(c =>
    {
        var body = Body(c, bodyName);
        var hasSurface = body.HasSolidSurface;
        return new BakeBodyInfo(bodyName, body.InitialRotation * RadToDeg, body.RotationalPeriod, body.EquatorialRadius,
            body.GravitationalParameter, hasSurface, body.HasAtmosphere ? body.AtmosphereDepth : 0,
            hasSurface ? TryGetSet(() => body.Biomes).OrderBy(b => b, StringComparer.Ordinal).ToList() : []);
    });

    /// <summary>A body's rotation angle at UT 0, in degrees: not on the wiki, and what times a landing (docs/MATH.md §2.9).</summary>
    public Task<double> InitialRotationAsync(string bodyName) => CallAsync(c => Body(c, bodyName).InitialRotation * RadToDeg);

    /// <summary>
    /// Terrain height and biome along one latitude, every <paramref name="stepDeg"/> from 180° W eastward. One trip to the
    /// game per row, so the clock and orbits keep updating between rows.
    /// </summary>
    public Task<(float[] Heights, string?[] Biomes)> SampleRowAsync(string bodyName, double latitudeDeg, double stepDeg, bool biomes) =>
        CallAsync(c =>
        {
            var body = Body(c, bodyName);
            var columns = SurfaceBake.GridColumns(stepDeg);
            var heights = new float[columns];
            var names = new string?[columns];
            for (var i = 0; i < columns; i++)
            {
                var lon = SurfaceBake.ColumnLongitude(stepDeg, i);
                heights[i] = (float)body.SurfaceHeight(latitudeDeg, lon);
                if (biomes)
                {
                    try { names[i] = body.BiomeAt(latitudeDeg, lon); }
                    catch (RPCException) { names[i] = null; }
                }
            }
            return (heights, names);
        });

    /// <summary>Pressure and density every <paramref name="stepM"/> from the datum to the top of the atmosphere.</summary>
    public Task<IReadOnlyList<AtmospherePoint>> SampleAirAsync(string bodyName, double depthM, double stepM) =>
        CallAsync<IReadOnlyList<AtmospherePoint>>(c =>
        {
            var body = Body(c, bodyName);
            var points = new List<AtmospherePoint>();
            for (var alt = 0.0; alt <= depthM + 0.5; alt += stepM)
            {
                double? density;
                try { density = body.DensityAt(alt); }
                catch (RPCException) { density = null; }
                points.Add(new AtmospherePoint(alt, body.PressureAt(alt), density));
            }
            return points;
        });

    private static CelestialBody Body(Connection c, string bodyName) =>
        c.SpaceCenter().Bodies.TryGetValue(bodyName == "Kerbol" ? "Sun" : bodyName, out var body)
            ? body
            : throw new System.InvalidOperationException($"The game has no body called {bodyName}.");

    private static IEnumerable<string> TryGetSet(Func<ISet<string>> get)
    {
        try { return get(); }
        catch (RPCException) { return []; }
    }

    /// <summary>Reads the time and scene, and the vessels when due. Losing the game disconnects.</summary>
    private async Task PollAsync()
    {
        if (_connection is null || _polling) return;
        _polling = true;
        try
        {
            _poll.Interval = PollEvery;
            var readVessels = _vesselsDue || DateTime.UtcNow - _vesselsReadAt >= VesselsEvery;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (scene, game) = await CallAsync(c =>
            {
                var scene = c.KRPC().GameScene;
                var space = c.SpaceCenter();
                var vessels = scene == GameScene.Flight && readVessels ? ReadVessels(space) : null;
                string? targetBody = null;
                if (vessels is not null && space.TargetBody is { } body) targetBody = PlannerBodyName(body.Name);
                var nodes = scene == GameScene.Flight && TryGet(() => space.ActiveVessel) is { } active
                    ? active.Control.Nodes.Select(n => new LiveNode(n.UT, n.Prograde, n.Normal, n.Radial)).ToList()
                    : [];
                return (scene, new LiveGame(space.UT, vessels, targetBody) { ActiveNodes = nodes });
            });

            // Between full reads keep the vessels from the last one, so the game always has some while in flight.
            if (game.Vessels is null && scene == GameScene.Flight && Game?.Vessels is { } last) game = game with { Vessels = last, TargetBody = Game.TargetBody };
            if (game.Vessels is not null && readVessels)
            {
                _vesselsDue = false;
                _vesselsReadAt = DateTime.UtcNow;
                _lastVesselsRead = clock.Elapsed;
            }
            else _lastRead = clock.Elapsed;
            OnPropertyChanged(nameof(ToolTipText));
            if (scene != Scene) _vesselsDue = scene == GameScene.Flight; // read straight away on entering flight
            Scene = scene;
            Game = game;
            Status = $"KSP: {SceneName(scene)} · {GameCalendar.Kerbin.FormatDate(game.Ut)}";
            GameUpdated?.Invoke(game);
        }
        catch (Exception)
        {
            // CallAsync has already disconnected and said why.
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Every vessel with its situation and, unless it's on the ground, its orbit. Runs on a worker thread.</summary>
    private static List<LiveVessel> ReadVessels(KRPC.Client.Services.SpaceCenter.Service space)
    {
        var activeId = TryGet(() => space.ActiveVessel)?.id;
        var targetId = TryGet(() => space.TargetVessel)?.id;
        var bodies = new Dictionary<ulong, string>();
        var list = new List<LiveVessel>();
        foreach (var v in space.Vessels)
        {
            var situation = SituationName(v.Situation);
            SavedOrbit? orbit = null;
            if (situation is not ("LANDED" or "SPLASHED" or "PRELAUNCH"))
            {
                var o = v.Orbit;
                var body = o.Body;
                if (!bodies.TryGetValue(body.id, out var bodyName)) bodies[body.id] = bodyName = body.Name;
                orbit = new SavedOrbit(o.SemiMajorAxis, o.Eccentricity, o.Inclination * RadToDeg, o.LongitudeOfAscendingNode * RadToDeg,
                    o.ArgumentOfPeriapsis * RadToDeg, o.MeanAnomalyAtEpoch, o.Epoch, -1, bodyName);
            }
            list.Add(new LiveVessel(v.Name, v.Type.ToString(), situation, orbit) { IsActive = v.id == activeId, IsTarget = v.id == targetId });
        }
        return list;
    }

    /// <summary>Runs <paramref name="call"/> on a worker thread, one at a time. Disconnects (and rethrows) if the game has gone.</summary>
    private async Task<T> CallAsync<T>(Func<Connection, T> call)
    {
        if (_connection is not { } connection) throw new System.InvalidOperationException("Not connected to KSP.");
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => call(connection));
        }
        catch (Exception ex) when (ex is not (System.InvalidOperationException or RPCException))
        {
            if (connection == _connection)
            {
                Disconnect();
                Problem = $"Lost the connection to KSP ({ex.Message.TrimEnd('.')}).";
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static T? TryGet<T>(Func<T> get) where T : class
    {
        try { return get(); }
        catch (RPCException) { return null; }
    }

    internal static string SituationName(VesselSituation situation) => situation switch
    {
        VesselSituation.PreLaunch => "PRELAUNCH",
        VesselSituation.Orbiting => "ORBITING",
        VesselSituation.SubOrbital => "SUB_ORBITAL",
        VesselSituation.Escaping => "ESCAPING",
        VesselSituation.Flying => "FLYING",
        VesselSituation.Landed => "LANDED",
        VesselSituation.Splashed => "SPLASHED",
        VesselSituation.Docked => "DOCKED",
        _ => situation.ToString().ToUpperInvariant(),
    };

    /// <summary>The planner's name for a body (KSP calls Kerbol "Sun").</summary>
    internal static string PlannerBodyName(string kspName) => kspName == "Sun" ? "Kerbol" : kspName;

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var address)) return address;
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
    }

    private static string Describe(Exception ex, string endpoint) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"Nothing is listening at {endpoint}. Start KSP, open the kRPC window and click Start server.",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
            $"Couldn't find the computer \"{endpoint.Split(':')[0]}\". Check the address in Settings › Live connection.",
        ConnectionException => $"kRPC refused the connection ({ex.Message.TrimEnd('.')}).",
        _ => $"Couldn't connect to KSP at {endpoint} ({ex.Message.TrimEnd('.')}).",
    };

    private static string SceneName(GameScene scene) => scene switch
    {
        GameScene.SpaceCenter => "Space Center",
        GameScene.Flight => "in flight",
        GameScene.TrackingStation => "Tracking Station",
        GameScene.EditorVAB => "VAB",
        GameScene.EditorSPH => "SPH",
        GameScene.MissionBuilder => "Mission Builder",
        GameScene.AstronautComplex => "Astronaut Complex",
        GameScene.MissionControl => "Mission Control",
        GameScene.ResearchAndDevelopment => "R&D",
        GameScene.Administration => "Administration",
        _ => scene.ToString(),
    };

    private const double RadToDeg = 180 / Math.PI;
}
