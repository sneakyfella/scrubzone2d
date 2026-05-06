using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Network;

namespace ScrubZone2D.States;

public sealed class LobbyState : GameState
{
    private static readonly Color Panel  = new(20, 22, 35);
    private static readonly Color Accent = new(80, 140, 220);

    private readonly GameStateManager _stateManager;
    private float _countdown = 3f;

    public LobbyState(GameStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public override void Enter()
    {
        NetworkManager.Instance.GameStarting += OnGameStarting;
        NetworkManager.Instance.Disconnected += OnDisconnected;
    }

    public override void Exit()
    {
        NetworkManager.Instance.GameStarting -= OnGameStarting;
        NetworkManager.Instance.Disconnected -= OnDisconnected;
        base.Exit();
    }

    private void OnGameStarting()
    {
        _stateManager.Replace(new GameplayState(_stateManager));
    }

    private void OnDisconnected()
    {
        _stateManager.Pop();
    }

    public override void Update(float dt, StateServices svc)
    {
        if (!NetworkManager.Instance.IsConnected) return;
        _countdown -= dt;
        if (_countdown <= 0f && NetworkManager.Instance.Role == NetworkRole.Host)
            NetworkManager.Instance.StartGame();
    }

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        var net = NetworkManager.Instance;

        sb.Begin();

        UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(10, 10, 20));

        UIRenderer.TextCentered(sb, "LOBBY", new Rectangle(0, 80, 1280, 60), new Color(220, 200, 100));

        // Players
        const int panelW = 500, panelH = 300;
        var panel = new Rectangle((1280 - panelW) / 2, 160, panelW, panelH);
        UIRenderer.Panel(sb, panel, Panel, Accent);

        int py = panel.Y + 20;
        int px = panel.X + 24;
        int pw = panelW - 48;

        // Local player
        var localColor = net.Role == NetworkRole.Host ? new Color(255, 200, 80) : new Color(80, 200, 255);
        UIRenderer.Text(sb, $"{net.LocalName} (you)", new Vector2(px, py), localColor);

        py += 40;

        // Remote player
        if (net.IsConnected)
        {
            var remoteColor = net.Role == NetworkRole.Host ? new Color(80, 200, 255) : new Color(255, 200, 80);
            UIRenderer.Text(sb, net.RemoteName, new Vector2(px, py), remoteColor);
        }
        else
        {
            UIRenderer.Text(sb, "Waiting for opponent...", new Vector2(px, py), Color.Gray, small: true);
        }

        py += 60;

        // Status
        UIRenderer.Text(sb, net.StatusText ?? "", new Vector2(px, py), new Color(140, 160, 180), small: true);

        py += 50;

        if (net.IsConnected)
        {
            int secs = (int)MathF.Ceiling(MathF.Max(0f, _countdown));
            UIRenderer.TextCentered(sb,
                secs > 0 ? $"Game starts in {secs}..." : "Starting!",
                new Rectangle(panel.X, py, panel.Width, 48),
                new Color(180, 220, 140));
        }

        // Role tag
        var roleTag = net.Role == NetworkRole.Host ? "HOST" : "GUEST";
        UIRenderer.Text(sb, $"Role: {roleTag}", new Vector2(20, 680), Color.Gray, small: true);

        sb.End();
    }
}
