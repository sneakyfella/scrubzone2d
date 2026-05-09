using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Arena;
using ScrubZone2D.Network;

namespace ScrubZone2D.States;

public sealed class LobbyState : GameState
{
    private static readonly Color PanelBg       = new(20, 22, 35);
    private static readonly Color Accent         = new(80, 140, 220);
    private static readonly Color HostColor      = new(255, 200, 80);
    private static readonly Color JoinerColor    = new(80, 200, 255);
    private static readonly Color TeamRed        = new(180, 60, 60);
    private static readonly Color TeamBlue       = new(60, 80, 180);
    private static readonly Color BtnModeActive  = new(55, 100, 175);
    private static readonly Color BtnModeDim     = new(28, 33, 52);
    private static readonly Color BtnReady       = new(45, 125, 65);
    private static readonly Color BtnReadyOff    = new(90, 50, 50);
    private static readonly Color BtnStart       = new(140, 95, 35);
    private static readonly Color BtnStartDim    = new(48, 44, 38);
    private static readonly Color SepColor       = new(50, 55, 75);

    private readonly GameStateManager _stateManager;
    private bool  _countdownActive;
    private float _countdown;

    public LobbyState(GameStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public override void Enter()
    {
        NetworkManager.Instance.CountdownStarted += OnCountdownStarted;
        NetworkManager.Instance.GameStarting     += OnGameStarting;
        NetworkManager.Instance.Disconnected     += OnDisconnected;
    }

    public override void Exit()
    {
        NetworkManager.Instance.CountdownStarted -= OnCountdownStarted;
        NetworkManager.Instance.GameStarting     -= OnGameStarting;
        NetworkManager.Instance.Disconnected     -= OnDisconnected;
        base.Exit();
    }

    private void OnCountdownStarted()
    {
        _countdownActive = true;
        _countdown       = 5f;
    }

    private void OnGameStarting() =>
        _stateManager.Replace(new GameplayState(_stateManager));

    private void OnDisconnected() =>
        _stateManager.Pop();

    public override void Update(float dt, StateServices svc)
    {
        if (!_countdownActive) return;
        _countdown -= dt;
        if (_countdown <= 0f && NetworkManager.Instance.Role == NetworkRole.Host)
            NetworkManager.Instance.StartGame();
    }

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        var net    = NetworkManager.Instance;
        bool isHost = net.Role == NetworkRole.Host;

        sb.Begin();

        UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(10, 10, 20));
        UIRenderer.TextCentered(sb, "LOBBY", new Rectangle(0, 44, 1280, 60), new Color(220, 200, 100));

        const int panelW = 540, panelH = 570;
        var panel = new Rectangle((1280 - panelW) / 2, 118, panelW, panelH);
        UIRenderer.Panel(sb, panel, PanelBg, Accent);

        int px = panel.X + 26, pw = panelW - 52, py = panel.Y + 22;

        // ── Game Mode ──────────────────────────────────────────────────────────
        UIRenderer.Text(sb, "GAME MODE", new Vector2(px, py), Color.Gray, small: true);
        py += 26;

        int halfW = (pw - 8) / 2;
        var ffaCol   = net.GameMode == GameMode.FFA   ? BtnModeActive : BtnModeDim;
        var teamsCol = net.GameMode == GameMode.Teams ? BtnModeActive : BtnModeDim;

        if (UIRenderer.Button(sb, "FREE FOR ALL", new Rectangle(px, py, halfW, 36), ffaCol, Color.White)
            && isHost && !_countdownActive)
            net.SetGameMode(GameMode.FFA);

        if (UIRenderer.Button(sb, "TEAMS", new Rectangle(px + halfW + 8, py, halfW, 36), teamsCol, Color.White)
            && isHost && !_countdownActive)
            net.SetGameMode(GameMode.Teams);

        py += 52;

        UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), SepColor, 1);
        py += 18;

        // ── Map Selection ──────────────────────────────────────────────────────
        UIRenderer.Text(sb, "MAP", new Vector2(px, py), Color.Gray, small: true);
        py += 26;

        int mapCount = MapRegistry.Maps.Count;
        if (mapCount == 0)
        {
            UIRenderer.Text(sb, "No maps found in Maps/ folder.", new Vector2(px, py),
                Color.Gray, small: true);
            py += 20;
        }
        else
        {
            // Up to 2 maps per row; buttons generated from registry.
            int numRows = (mapCount + 1) / 2;
            for (int mi = 0; mi < mapCount; mi++)
            {
                int col    = mi % 2;
                int row    = mi / 2;
                int bx     = col == 0 ? px : px + halfW + 8;
                int by     = py + row * 44;
                int bw     = mapCount == 1 ? pw : halfW;
                var mapCol = net.SelectedMap == mi ? BtnModeActive : BtnModeDim;

                if (UIRenderer.Button(sb, MapRegistry.NameOf((byte)mi),
                        new Rectangle(bx, by, bw, 36), mapCol, Color.White)
                    && isHost && !_countdownActive)
                    net.SetMap((byte)mi);
            }
            py += numRows * 44;
        }

        py += 8; // gap before separator

        UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), SepColor, 1);
        py += 18;

        // ── Player List ────────────────────────────────────────────────────────
        UIRenderer.Text(sb, "PLAYERS", new Vector2(px, py), Color.Gray, small: true);
        py += 26;

        string hostName   = isHost ? net.LocalName  : net.RemoteName;
        string joinerName = isHost ? net.RemoteName : net.LocalName;

        DrawPlayerRow(sb, px, py, pw,
            name: hostName, isYou: isHost, color: HostColor,
            roleLabel: "HOST", roleColor: HostColor, readyLabel: null);
        py += 44;

        if (net.IsConnected)
        {
            bool joinerReady = isHost ? net.RemoteIsReady : net.LocalIsReady;
            DrawPlayerRow(sb, px, py, pw,
                name: joinerName, isYou: !isHost, color: JoinerColor,
                roleLabel: "GUEST", roleColor: JoinerColor,
                readyLabel: joinerReady ? "READY" : "WAITING");
        }
        else
        {
            UIRenderer.FillRect(sb, new Rectangle(px, py + 8, 10, 10), new Color(50, 50, 60));
            UIRenderer.Text(sb, "Waiting for opponent...", new Vector2(px + 18, py + 5),
                Color.Gray, small: true);
        }
        py += 44;

        // ── Team Assignments (Teams mode only) ─────────────────────────────────
        if (net.GameMode == GameMode.Teams && net.IsConnected)
        {
            UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), SepColor, 1);
            py += 14;

            int teamW = (pw - 8) / 2;
            UIRenderer.Panel(sb, new Rectangle(px, py, teamW, 34),
                new Color(44, 12, 12), TeamRed);
            UIRenderer.TextCentered(sb, $"RED: {hostName}",
                new Rectangle(px, py, teamW, 34), Color.White, small: true);

            UIRenderer.Panel(sb, new Rectangle(px + teamW + 8, py, teamW, 34),
                new Color(12, 18, 50), TeamBlue);
            UIRenderer.TextCentered(sb, $"BLUE: {joinerName}",
                new Rectangle(px + teamW + 8, py, teamW, 34), Color.White, small: true);

            py += 50;
        }

        // ── Action Area ────────────────────────────────────────────────────────
        UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), SepColor, 1);
        py += 18;

        if (_countdownActive)
        {
            int secs       = (int)MathF.Ceiling(MathF.Max(0f, _countdown));
            string numStr  = secs > 0 ? secs.ToString() : "GO!";
            var numColor   = secs > 0 ? new Color(220, 200, 100) : new Color(80, 220, 100);
            UIRenderer.TextCentered(sb, numStr,
                new Rectangle(panel.X, py, panel.Width, 52), numColor);
            UIRenderer.TextCentered(sb, "Game starting!",
                new Rectangle(panel.X, py + 46, panel.Width, 28),
                new Color(150, 175, 150), small: true);
        }
        else if (isHost)
        {
            bool canStart   = net.AllJoinersReady;
            var startColor  = canStart ? BtnStart : BtnStartDim;
            var startLabel  = canStart
                ? "START GAME"
                : "START GAME  (waiting for all players to ready up)";

            if (UIRenderer.Button(sb, startLabel, new Rectangle(px, py, pw, 46), startColor, Color.White)
                && canStart)
                net.BeginCountdown();
        }
        else
        {
            bool isReady   = net.LocalIsReady;
            var readyColor = isReady ? BtnReady : BtnReadyOff;
            var readyLabel = isReady ? "READY" : "READY UP";

            if (UIRenderer.Button(sb, readyLabel, new Rectangle(px, py, pw, 46), readyColor, Color.White))
                net.SetReady(!isReady);
        }

        // ── Footer ─────────────────────────────────────────────────────────────
        var footerRole = isHost ? "HOST" : "GUEST";
        UIRenderer.Text(sb, footerRole, new Vector2(22, 686), Color.DimGray, small: true);

        if (net.StatusText != null)
            UIRenderer.Text(sb, net.StatusText, new Vector2(90, 686),
                new Color(90, 110, 135), small: true);

        sb.End();
    }

    private static void DrawPlayerRow(SpriteBatch sb, int px, int py, int pw,
        string name, bool isYou, Color color, string roleLabel, Color roleColor, string? readyLabel)
    {
        UIRenderer.FillRect(sb, new Rectangle(px, py + 8, 10, 10), color);

        string display = isYou ? $"{name} (YOU)" : name;
        UIRenderer.Text(sb, display, new Vector2(px + 18, py + 5), Color.White);

        UIRenderer.Text(sb, roleLabel, new Vector2(px + pw - 148, py + 5), roleColor, small: true);

        if (readyLabel != null)
        {
            var readyColor = readyLabel == "READY"
                ? new Color(80, 200, 80)
                : new Color(100, 100, 100);
            UIRenderer.Text(sb, readyLabel, new Vector2(px + pw - 60, py + 5), readyColor, small: true);
        }
    }
}
