using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MatchmakingLib;
using MatchmakingLib.Models;
using NetworkingLib;
using NetworkingLib.Core;
using NetworkingLib.Packets;
using NetworkingLib.Packets.Lobby;
using NetworkingLib.Udp;

namespace ScrubZone2D.Network;

public enum NetworkRole { None, Host, Joiner }

// All public members are safe to call from the game thread after ProcessPendingActions().
public sealed class NetworkManager : IDisposable
{
    public static readonly NetworkManager Instance = new();

    public NetworkRole Role          { get; private set; } = NetworkRole.None;
    public byte        LocalPlayerId { get; private set; }
    public byte        RemotePlayerId { get; private set; }
    public string      LocalName     { get; private set; } = "Player";
    public string      RemoteName    { get; private set; } = "Remote";
    public bool        IsConnected   { get; private set; }
    public bool        GameStarted   { get; private set; }
    public string?     StatusText    { get; private set; } = "Ready";

    // Raised on game thread via ProcessPendingActions
    public event Action?           LobbyReady;
    public event Action?           GameStarting;
    public event Action<IPacket>?  GamePacketReceived;
    public event Action?           Disconnected;
    public event Action<string>?   StartFailed;

    private UdpGameServer?    _server;
    private UdpGameClient?    _client;
    private MatchmakingClient? _matchmaker;
    private IPEndPoint?        _remoteEndpoint; // host tracks joiner endpoint

    // Thread-safe action queue — network callbacks enqueue, game Update dequeues
    private readonly ConcurrentQueue<Action> _pending = new();

    // Monotonic tick counter for unreliable state packets
    private uint _tick;

    // Matchmaking ini path — resolved relative to the executable directory
    private static readonly string MatchmakerIni =
        Path.Combine(AppContext.BaseDirectory, "matchmaking.ini");
    private const int    GamePort      = 7777;
    private const ushort Protocol      = 1;

    private NetworkManager()
    {
        NetworkLibSetup.RegisterBuiltinPackets();
        GamePacketRegistrar.RegisterAll();
    }

    // Called once per frame from Game1.Update to marshal network events to game thread
    public void ProcessPendingActions()
    {
        while (_pending.TryDequeue(out var action))
            action();
    }

    // ── Host flow ────────────────────────────────────────────────────────────

    public async Task StartHostAsync(string playerName)
    {
        Role      = NetworkRole.Host;
        LocalName = playerName;
        Post(() => StatusText = "Starting server...");

        string localIp;
        string hostId;
        try
        {
            _server = new UdpGameServer(GamePort, maxClients: 1, Protocol);
            _server.ClientConnected    += OnServerClientConnected;
            _server.ClientDisconnected += OnServerClientDisconnected;
            _server.PacketReceived     += OnServerPacketReceived;
            _server.Start();

            localIp = GetLocalIp();
            hostId  = $"{localIp}:{GamePort}";
            Post(() => StatusText = $"Server up - ID: {hostId}");
        }
        catch (Exception ex)
        {
            var msg = $"Server failed: {ex.GetType().Name}: {ex.Message}";
            Post(() => { StatusText = msg; StartFailed?.Invoke(msg); });
            Role = NetworkRole.None;
            return;
        }

        try
        {
            _matchmaker = new MatchmakingClient(MatchmakerIni);
            await _matchmaker.ConnectAsync(hostId);

            var player = new Player(hostId, playerName, 1000.0, hostId);
            await _matchmaker.JoinQueueAsync(player, MatchmakingMode.Coop);
            Post(() => StatusText = "Waiting for opponent...");
        }
        catch
        {
            Post(() => StatusText = $"Matchmaker offline - share your IP: {localIp}:{GamePort}");
        }
    }

    // ── Joiner flow ──────────────────────────────────────────────────────────

    public async Task StartJoinAsync(string playerName)
    {
        Role      = NetworkRole.Joiner;
        LocalName = playerName;

        var joinerId = Guid.NewGuid().ToString("N")[..8];
        Post(() => StatusText = "Connecting to matchmaker...");

        try
        {
            _matchmaker = new MatchmakingClient(MatchmakerIni);
            _matchmaker.MatchFound += OnMatchFound;
            await _matchmaker.ConnectAsync(joinerId);

            Post(() => StatusText = "Joining queue...");
            var player = new Player(joinerId, playerName, 1000.0);
            bool joined = await _matchmaker.JoinQueueAsync(player, MatchmakingMode.Coop);
            if (!joined)
            {
                var reason = _matchmaker.LastJoinFailureReason ?? "unknown";
                var msg = $"Join failed: {reason}";
                Post(() => { StatusText = msg; StartFailed?.Invoke(msg); });
                return;
            }
            Post(() => StatusText = "In queue - waiting for match...");
        }
        catch (Exception ex)
        {
            var msg = $"Matchmaker error: {ex.GetType().Name}";
            Post(() => { StatusText = msg; StartFailed?.Invoke(msg); });
        }
    }

    // Skip matchmaker — connect directly to host IP (LAN / port-forward)
    public async Task StartDirectJoinAsync(string playerName, string hostIp, int port = GamePort)
    {
        Role      = NetworkRole.Joiner;
        LocalName = playerName;
        Post(() => StatusText = $"Connecting to {hostIp}:{port}...");
        await ConnectClientAsync(hostIp, port);
    }

    public async Task ConnectDirectAsync(string hostIp, int port = GamePort)
    {
        Post(() => StatusText = $"Connecting to {hostIp}:{port}...");
        await ConnectClientAsync(hostIp, port);
    }

    // Host: start the game, broadcast GameStartPacket
    public void StartGame()
    {
        if (Role != NetworkRole.Host || !IsConnected) return;
        GameStarted = true;

        var pkt = new GameStartPacket();
        if (_remoteEndpoint != null)
            _server!.SendReliable(_remoteEndpoint, pkt);

        Post(() =>
        {
            GameStarted = true;
            GameStarting?.Invoke();
        });
    }

    // ── Sending game data ────────────────────────────────────────────────────

    public void SendStateUnreliable(HovercraftStatePacket pkt)
    {
        pkt.Tick = _tick++;
        if (Role == NetworkRole.Host && _remoteEndpoint != null)
            _server!.SendUnreliable(_remoteEndpoint, pkt);
        else if (Role == NetworkRole.Joiner)
            _client!.SendUnreliable(pkt);
    }

    public void SendReliable(IPacket pkt)
    {
        if (Role == NetworkRole.Host && _remoteEndpoint != null)
            _server!.SendReliable(_remoteEndpoint, pkt);
        else if (Role == NetworkRole.Joiner)
            _client!.SendReliable(pkt);
    }

    // ── Server callbacks ─────────────────────────────────────────────────────

    private void OnServerClientConnected(IPEndPoint ep, PlayerInfo info)
    {
        _remoteEndpoint = ep;
        RemotePlayerId  = info.PlayerId;
        RemoteName      = info.PlayerName;
        LocalPlayerId   = 0xFF; // host uses 0xFF as convention

        Post(() =>
        {
            IsConnected = true;
            StatusText  = $"Player connected: {info.PlayerName}";
            LobbyReady?.Invoke();
        });
    }

    private void OnServerClientDisconnected(IPEndPoint ep, DisconnectReason reason)
    {
        Post(() =>
        {
            IsConnected = false;
            StatusText  = "Player disconnected";
            Disconnected?.Invoke();
        });
    }

    private void OnServerPacketReceived(IPEndPoint ep, IPacket packet)
    {
        if (IsGamePacket(packet.PacketTypeId))
            Post(() => GamePacketReceived?.Invoke(packet));
    }

    // ── Client callbacks ─────────────────────────────────────────────────────

    private void OnClientConnected()
    {
        Post(() =>
        {
            IsConnected   = true;
            LocalPlayerId = _client!.PlayerId;
            StatusText    = "Connected to host";
            LobbyReady?.Invoke();
        });
    }

    private void OnClientDisconnected(DisconnectReason reason)
    {
        Post(() =>
        {
            IsConnected = false;
            StatusText  = "Disconnected from host";
            Disconnected?.Invoke();
        });
    }

    private void OnClientPacketReceived(IPacket packet)
    {
        if (packet is GameStartPacket)
        {
            Post(() =>
            {
                GameStarted = true;
                RemotePlayerId = 0xFF; // host is always 0xFF
                GameStarting?.Invoke();
            });
            return;
        }
        if (IsGamePacket(packet.PacketTypeId))
            Post(() => GamePacketReceived?.Invoke(packet));
    }

    // ── Matchmaking callback ─────────────────────────────────────────────────

    private void OnMatchFound(object? sender, MatchmakingLib.Events.MatchFoundEventArgs e)
    {
        var allIds = string.Join(", ", e.Match.AllPlayerIds);
        Post(() => StatusText = $"Match found ({e.Match.AllPlayerIds.Count()} players): {allIds}");

        // Host player ID format is "ip:port"
        var hostId = e.Match.AllPlayerIds.FirstOrDefault(id => id.Contains(':'));
        if (hostId == null)
        {
            var msg = $"Match found but no host IP in: {allIds}";
            Post(() => { StatusText = msg; StartFailed?.Invoke(msg); });
            return;
        }

        var parts = hostId.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var port))
        {
            var msg = $"Match found but bad host ID format: {hostId}";
            Post(() => { StatusText = msg; StartFailed?.Invoke(msg); });
            return;
        }

        var hostIp = parts[0];
        Post(() => StatusText = $"Match found - connecting to {hostIp}:{port}");
        _ = ConnectClientAsync(hostIp, port);
    }

    private async Task ConnectClientAsync(string hostIp, int port)
    {
        var config = new NetworkConfig
        {
            PlayerName      = LocalName,
            ProtocolVersion = Protocol,
            ConnectTimeoutMs = 10_000
        };
        _client = new UdpGameClient(config);
        _client.Connected      += OnClientConnected;
        _client.Disconnected   += OnClientDisconnected;
        _client.PacketReceived += OnClientPacketReceived;

        try
        {
            await _client.ConnectAsync(hostIp, port);
        }
        catch (Exception ex)
        {
            Post(() => StatusText = $"Connection failed: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Post(Action a) => _pending.Enqueue(a);

    private static bool IsGamePacket(byte typeId) => typeId >= 0x50;

    private static string GetLocalIp()
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            sock.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)sock.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    public void Dispose()
    {
        _server?.Dispose();
        _client?.Dispose();
        if (_matchmaker != null)
            _matchmaker.DisposeAsync().AsTask().Wait(1000);
    }
}
