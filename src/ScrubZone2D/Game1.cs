using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate;
using MonoGameTemplate.Audio;
using MonoGameTemplate.Diagnostics;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using MonoGameTemplate.Tweening;
using MonoGameTemplate.UI.Framework;
using ScrubZone2D.States;

namespace ScrubZone2D;

public sealed class Game1 : Game
{
    public const int VirtualWidth  = 1280;
    public const int VirtualHeight = 720;

    private readonly GraphicsDeviceManager _graphics;
    private readonly string? _initialPlayerName;
    private SpriteBatch _sb = null!;

    private ResolutionScaler _scaler  = null!;
    private TouchHandler     _touch   = null!;
    private InputHandler     _input   = null!;
    private UiContext        _ui      = null!;
    private Tweener          _tweener = null!;
    private AudioManager     _audio   = null!;
    private GameStateManager _states  = null!;

    public Game1(string? initialPlayerName = null)
    {
        _initialPlayerName = initialPlayerName;
        Logger.Initialize("ScrubZone2D");

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = VirtualWidth,
            PreferredBackBufferHeight = VirtualHeight,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title   = "ScrubZone 2D";
        Window.AllowUserResizing = true;
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);

        var pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData([Color.White]);

        SpriteFont? font      = null;
        SpriteFont? smallFont = null;
        try { font      = Content.Load<SpriteFont>("Fonts/DefaultFont"); } catch { }
        try { smallFont = Content.Load<SpriteFont>("Fonts/SmallFont");   } catch { }

        MGT.Init(pixel, font, smallFont);

        _scaler  = new ResolutionScaler(GraphicsDevice, VirtualWidth, VirtualHeight);
        _touch   = new TouchHandler();
        _input   = new InputHandler(_scaler, _touch);
        _ui      = new UiContext(GraphicsDevice, font, VirtualWidth, VirtualHeight, _input);
        _tweener = new Tweener();
        _audio   = new AudioManager();

        InputHelper.Initialize(_scaler);

        _states = new GameStateManager(Services, Content.RootDirectory);
        _states.Push(new MainMenuState(_states, _initialPlayerName));
    }

    protected override void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Frame update order — contract from monogametemplate CLAUDE.md
        _touch.Update();
        _input.Update();
        InputHelper.Update(IsActive);
        _ui.BeginFrame();
        _tweener.Update(dt);
        _audio.Update(dt);

        Network.NetworkManager.Instance.ProcessPendingActions();

        var services = new StateServices(_ui, _input, _sb);
        _states.Update(dt, services);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        _scaler.BeginDraw();

        var services = new StateServices(_ui, _input, _sb);
        _states.Draw(_sb, services);

        _scaler.EndDraw(_sb);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _scaler.Dispose();
        Network.NetworkManager.Instance.Dispose();
        base.UnloadContent();
    }
}
