using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Network;

namespace ScrubZone2D.States;

public sealed class MainMenuState : GameState
{
    private readonly GameStateManager _stateManager;

    private string _playerName;
    private string _directIp  = "127.0.0.1";
    private bool   _nameFocused;
    private bool   _ipFocused;
    private bool   _busy;

    private static readonly Color Accent     = new(80, 140, 220);
    private static readonly Color PanelBg   = new(20, 22, 35);
    private static readonly Color BtnHost   = new(50, 130, 80);
    private static readonly Color BtnFind   = new(50, 80, 160);
    private static readonly Color BtnDirect = new(100, 60, 130);
#if EDITOR
    private static readonly Color BtnEditor = new(80, 55, 130);
#endif

    private KeyboardState _prevKb;

    public MainMenuState(GameStateManager stateManager, string? initialPlayerName = null)
    {
        _stateManager = stateManager;
        _playerName   = !string.IsNullOrWhiteSpace(initialPlayerName) ? initialPlayerName : "Player1";
    }

    public override void Enter()
    {
        NetworkManager.Instance.LobbyReady  += OnLobbyReady;
        NetworkManager.Instance.StartFailed += OnStartFailed;
    }

    public override void Exit()
    {
        NetworkManager.Instance.LobbyReady  -= OnLobbyReady;
        NetworkManager.Instance.StartFailed -= OnStartFailed;
        base.Exit();
    }

    private void OnLobbyReady() =>
        _stateManager.Replace(new LobbyState(_stateManager));

    private void OnStartFailed(string _) =>
        _busy = false;

    public override void Update(float dt, StateServices svc)
    {
        var kb = Keyboard.GetState();
        HandleTextInput(kb);
        _prevKb = kb;
    }

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        sb.Begin();

        UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(10, 10, 20));
        UIRenderer.TextCentered(sb, "SCRUBZONE 2D", new Rectangle(0, 80, 1280, 80),
            new Color(220, 200, 100));
        UIRenderer.TextCentered(sb, "Hovercraft Bouncing Bullet Battler",
            new Rectangle(0, 165, 1280, 36), new Color(140, 140, 160), small: true);

        const int panelW = 480;
#if EDITOR
        const int panelH = 440;
#else
        const int panelH = 380;
#endif
        var panel = new Rectangle((1280 - panelW) / 2, 210, panelW, panelH);
        UIRenderer.Panel(sb, panel, PanelBg, Accent);

        int px = panel.X + 24, pw = panelW - 48, py = panel.Y + 20;

        // Name field
        UIRenderer.Text(sb, "Player Name:", new Vector2(px, py), Color.LightGray, small: true);
        py += 22;
        if (UIRenderer.TextField(sb, _playerName, new Rectangle(px, py, pw, 30), _nameFocused))
        { _nameFocused = true; _ipFocused = false; }
        py += 44;

        if (!_busy)
        {
            if (UIRenderer.Button(sb, "HOST GAME", new Rectangle(px, py, pw, 44), BtnHost, Color.White))
            { _busy = true; _ = NetworkManager.Instance.StartHostAsync(SafeName()); }
            py += 56;

            if (UIRenderer.Button(sb, "FIND GAME  (matchmaker)", new Rectangle(px, py, pw, 44), BtnFind, Color.White))
            { _busy = true; _ = NetworkManager.Instance.StartJoinAsync(SafeName()); }
            py += 56;

            UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), new Color(60, 60, 80), 1);
            py += 12;
            UIRenderer.Text(sb, "Direct connect IP:", new Vector2(px, py), Color.Gray, small: true);
            py += 22;

            if (UIRenderer.TextField(sb, _directIp, new Rectangle(px, py, pw, 28), _ipFocused))
            { _ipFocused = true; _nameFocused = false; }
            py += 38;

            if (UIRenderer.Button(sb, "CONNECT DIRECT", new Rectangle(px, py, pw, 36), BtnDirect, Color.White))
            { _busy = true; _ = NetworkManager.Instance.StartDirectJoinAsync(SafeName(), _directIp.Trim()); }
            py += 48;

#if EDITOR
            UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), new Color(60, 60, 80), 1);
            py += 12;
            if (UIRenderer.Button(sb, "MAP EDITOR", new Rectangle(px, py, pw, 36), BtnEditor, Color.White))
                _stateManager.Push(new MapEditorState(_stateManager));
#endif
        }
        else
        {
            var busyText = NetworkManager.Instance.StatusText ?? "Connecting...";
            UIRenderer.TextCentered(sb, busyText, new Rectangle(panel.X, py, panel.Width, 60),
                Color.LightGray, small: true);
        }

        var status = NetworkManager.Instance.StatusText;
        if (status != null)
            UIRenderer.TextCentered(sb, status, new Rectangle(0, 650, 1280, 36),
                new Color(160, 180, 200), small: true);

        UIRenderer.Text(sb, "Controls: WASD move | Mouse aim | LClick fire",
            new Vector2(20, 700), Color.DimGray, small: true);

        sb.End();
    }

    private void HandleTextInput(KeyboardState kb)
    {
        foreach (Keys key in kb.GetPressedKeys())
        {
            if (_prevKb.IsKeyDown(key)) continue; // only on press

            if (key == Keys.Back)
            {
                if (_nameFocused && _playerName.Length > 0) _playerName = _playerName[..^1];
                if (_ipFocused   && _directIp.Length  > 0) _directIp   = _directIp[..^1];
                continue;
            }

            char? ch = KeyToChar(key, kb);
            if (ch == null) continue;

            if (_nameFocused && _playerName.Length < 16) _playerName += ch;
            if (_ipFocused   && _directIp.Length  < 21) _directIp   += ch;
        }
    }

    private static char? KeyToChar(Keys key, KeyboardState kb)
    {
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        int k = (int)key;

        if (k >= (int)Keys.A && k <= (int)Keys.Z)
            return shift ? (char)('A' + k - (int)Keys.A) : (char)('a' + k - (int)Keys.A);
        if (k >= (int)Keys.D0 && k <= (int)Keys.D9 && !shift)
            return (char)('0' + k - (int)Keys.D0);
        if (key == Keys.OemPeriod) return '.';

        return null;
    }

    private string SafeName() =>
        string.IsNullOrWhiteSpace(_playerName) ? "Player" : _playerName;
}
