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

    public NetworkRole Role           { get; private set; } = NetworkRole.None;
    public byte        LocalPlayerId  { get; private set; }
    public byte        RemotePlayerId { get; private set; }
    public string      LocalName      { get; private set; } = "Player";
    public string      RemoteName     { get; private set; } = "Remote";
    public bool        IsConnected    { get; private set; }
    public bool        GameStarted    { get; private set; }
    public string?     StatusText     { get; private set; } = "Ready";

    // Lobby state
    public GameMode GameMode        { get; private set; } = GameMode.FFA;
    public bool     LocalIsReady    { get; private set; }
    public bool     RemoteIsReady   { get; private set; }
    // True when all joiners are ready (host perspective: requires connection + joiner ready)
    public bool     AllJoinersReady => IsConnected && RemoteIsReady;

    // Raised on game thread via ProcessPendingActions
    public event Action?          LobbyReady;
    public event Action?          LobbyUpdated;
    public event Action?          CountdownStarted;
    public event Action?          GameStarting;
    public event Action<IPacket>? GamePacketReceived;
    public event Action?          Disconnected;
    public event Action<string>?  StartFailed;

    private UdpGameServer?     _server;
    private UdpGameClient?     _client;
    private MatchmakingClient? _matchmaker;
    private IPEndPoint?        _remoteEndpoint;

    private readonly ConcurrentQueue<Action> _pending = new();
    private uint _tick;

    private static readonly string MatchmakerIni =
        Path.Combine(AppContext.BaseDirectory, "matchmaking.ini");
    private const int    GamePort = 7777;
    private const ushort Protocol = 1;

    private NetworkManager()
    {
        NetworkLibSetup.RegisterBuiltinPackets();
        GamePacketRegistrar.RegisterAll();
        LobbyPacketRegistrar.RegisterAll();
    }

    public void ProcessPendingActions()
    {
        while (_pending.TryDequeue(out var action))
            action();
    }

    // ── Host flow ────────────────────────────────────────────────────────────

    public async Task StartHostAsync(string playerName)
    {
        Role          = NetworkRole.Host;
        LocalName     = playerName;
        LocalPlayerId = 0xFF;
        GameMode      = GameMode.FFA;
        LocalIsReady  = false;
        RemoteIsReady = false;
        GameStarted   = false;
        IsConnected   = false;

        Post(() => StatusText = "Starting server...");

        string localIp;
        string hostId;
        try
        {
            _server = new UdpGameServer(GamePort, maxClients: 5, Protocol);
            _server.ClientConnected    += OnServerClientConnected;
            _server.ClientDisconnected += OnServerClientDisconnected;
            _server.PacketReceived     += OnServerPacketReceived;
            _server.Start();

            localIp = GetLocalIp();
            hostId  = $"{localIp}:{GamePort}";

            // Host enters lobby immediately — no need to wait for a joiner
            Post(() =>
            {
                StatusText = $"Hosting - share IP: {localIp}:{GamePort}";
                LobbyReady?.Invoke();
            });
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

            // Leave first — same hostId is reused across sessions so a stale entry
            // would cause a 409 AlreadyQueued and silently drop us from the queue.
            await _matchmaker.LeaveQueueAsync();

            var player = new Player(hostId, playerName, 1000.0, hostId);
            bool joined = await _matchmaker.JoinQueueAsync(player, MatchmakingMode.Coop);
            if (joined)
                Post(() => StatusText = $"Matchmaker ready - share IP: {localIp}:{GamePort}");
            else
            {
                var reason = _matchmaker.LastJoinFailureReason ?? "unknown";
                Post(() => StatusText = $"Matchmaker join failed ({reason}) - use direct connect: {localIp}:{GamePort}");
            }
        }
        catch
        {
            Post(() => StatusText = $"Matchmaker offline - share IP: {localIp}:{GamePort}");
        }
    }

    // ── Joiner flow ──────────────────────────────────────────────────────────

    public async Task StartJoinAsync(string playerName)
    {
        Role          = NetworkRole.Joiner;
        LocalName     = playerName;
        LocalIsReady  = false;
        RemoteIsReady = false;
        GameStarted   = false;
        IsConnected   = false;

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

    public async Task StartDirectJoinAsync(string playerName, string hostIp, int port = GamePort)
    {
        Role          = NetworkRole.Joiner;
        LocalName     = playerName;
        LocalIsReady  = false;
        RemoteIsReady = false;
        GameStarted   = false;
        IsConnected   = false;

        Post(() => StatusText = $"Connecting to {hostIp}:{port}...");
        await ConnectClientAsync(hostIp, port);
    }

    // ── Lobby actions ────────────────────────────────────────────────────────

    // Host: change game mode and broadcast to joiners
    public void SetGameMode(GameMode mode)
    {
        if (Role != NetworkRole.Host) return;
        GameMode = mode;
        SendReliable(new GameModeSetPacket { Mode = (byte)mode });
        Post(() => LobbyUpdated?.Invoke());
    }

    // Joiner: toggle ready state and notify host
    public void SetReady(bool ready)
    {
        if (Role != NetworkRole.Joiner) return;
        LocalIsReady = ready;
        SendReliable(new PlayerReadyPacket { IsReady = ready });
    }

    // Host: begin countdown (only when all joiners are ready)
    public void BeginCountdown()
    {
        if (Role != NetworkRole.Host || !AllJoinersReady) return;
        SendReliable(new CountdownPacket());
        Post(() => CountdownStarted?.Invoke());
    }

    // ── Game start ───────────────────────────────────────────────────────────

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

        // Sync current lobby state to the new joiner
        _server!.SendReliable(ep, new LobbySyncPacket { Mode = (byte)GameMode });

        Post(() =>
        {
            IsConnected = true;
            StatusText  = $"Player connected: {info.PlayerName}";
            LobbyUpdated?.Invoke();
        });
    }

    private void OnServerClientDisconnected(IPEndPoint ep, DisconnectReason reason)
    {
        Post(() =>
        {
            IsConnected   = false;
            RemoteIsReady = false;
            StatusText    = "Player disconnected";
            Disconnected?.Invoke();
        });
    }

    private void OnServerPacketReceived(IPEndPoint ep, IPacket packet)
    {
        switch (packet)
        {
            case PlayerReadyPacket ready:
                Post(() => { RemoteIsReady = ready.IsReady; LobbyUpdated?.Invoke(); });
                break;
            default:
                if (IsGamePacket(packet.PacketTypeId))
                    Post(() => GamePacketReceived?.Invoke(packet));
                break;
        }
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
        switch (packet)
        {
            case GameStartPacket:
                Post(() =>
                {
                    GameStarted    = true;
                    RemotePlayerId = 0xFF;
                    GameStarting?.Invoke();
                });
                break;

            case LobbySyncPacket sync:
                Post(() => { GameMode = (GameMode)sync.Mode; LobbyUpdated?.Invoke(); });
                break;

            case GameModeSetPacket modeSet:
                Post(() => { GameMode = (GameMode)modeSet.Mode; LobbyUpdated?.Invoke(); });
                break;

            case CountdownPacket:
                Post(() => CountdownStarted?.Invoke());
                break;

            default:
                if (IsGamePacket(packet.PacketTypeId))
                    Post(() => GamePacketReceived?.Invoke(packet));
                break;
        }
    }

    // ── Matchmaking callback ─────────────────────────────────────────────────

    private void OnMatchFound(object? sender, MatchmakingLib.Events.MatchFoundEventArgs e)
    {
        var allIds = string.Join(", ", e.Match.AllPlayerIds);
        Post(() => StatusText = $"Match found ({e.Match.AllPlayerIds.Count()} players): {allIds}");

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
            PlayerName       = LocalName,
            ProtocolVersion  = Protocol,
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
