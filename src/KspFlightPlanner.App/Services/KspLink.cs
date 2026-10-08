using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using KRPC.Client;
using KRPC.Client.Services.KRPC;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

public enum KspLinkState { Disconnected, Connecting, Connected }

/// <summary>
/// A live connection to the running game through the kRPC mod's server. For now it only connects, reports the game
/// scene and notices when the game goes away; nothing in the planner reads game data from it yet.
/// </summary>
public sealed class KspLink : ObservableObject, IDisposable
{
    private const string ClientName = "KSP Flight Planner";

    // Connecting blocks until KSP replies, which waits for the player when kRPC asks them to allow the client.
    private static readonly TimeSpan AskPlayerAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _check;
    private Connection? _connection;
    private int _attempt;
    private bool _checking;
    private KspLinkState _state;
    private string _status = "KSP: not connected";
    private string? _problem;
    private string? _serverVersion;
    private GameScene? _scene;

    public KspLink(Func<AppSettings> settings)
    {
        _settings = settings;
        _check = new DispatcherTimer { Interval = CheckEvery };
        _check.Tick += async (_, _) => await CheckAsync();
    }

    public KspLinkState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(MenuText));
        }
    }

    public bool IsConnected => State == KspLinkState.Connected;

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

    /// <summary>Where the player is in the game (flight, VAB, tracking station…), checked every few seconds.</summary>
    public GameScene? Scene { get => _scene; private set => SetProperty(ref _scene, value); }

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
        ServerVersion = await Task.Run(() => connection.KRPC().GetStatus().Version);
        await CheckAsync();
        _check.Start();
    }

    public void Disconnect()
    {
        _attempt++; // abandons a pending attempt; it disposes its own connection when kRPC answers
        _check.Stop();
        _connection?.Dispose();
        _connection = null;
        ServerVersion = null;
        Scene = null;
        Problem = null;
        State = KspLinkState.Disconnected;
        Status = "KSP: not connected";
    }

    public void Dispose() => Disconnect();

    /// <summary>Asks for the game scene, which also shows the game is still there.</summary>
    private async Task CheckAsync()
    {
        if (_connection is not { } connection || _checking) return;
        _checking = true;
        try
        {
            var scene = await Task.Run(() => connection.KRPC().GameScene);
            if (connection != _connection) return;
            Scene = scene;
            Status = $"KSP: connected · {SceneName(scene)}";
        }
        catch (Exception ex)
        {
            if (connection != _connection) return;
            Disconnect();
            Problem = $"Lost the connection to KSP ({ex.Message.TrimEnd('.')}).";
        }
        finally
        {
            _checking = false;
        }
    }

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
}
