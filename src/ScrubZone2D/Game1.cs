using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate;
using MonoGameTemplate.Audio;
using MonoGameTemplate.Diagnostics;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using MonoGameTemplate.Tweening;
using MonoGameTemplate.UI.Framework;
using ScrubZone2D.Arena;
using ScrubZone2D.Config;
using ScrubZone2D.States;
#if !SHIPPING
using MonoGameTemplate.DevConsole;
#endif

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

    // SDL2 event watch — keeps the game ticking during the Win32 modal move/resize loop.
    // SDL calls registered filters for every dispatched event, even when the main message
    // pump is blocked by DefWindowProc during window dragging.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SDL_EventFilter(IntPtr userdata, IntPtr sdlEvent);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_AddEventWatch(SDL_EventFilter filter, IntPtr userdata);

    private SDL_EventFilter?    _sdlFilter;          // field reference prevents GC collection
    private readonly Stopwatch  _sinceLastTick = Stopwatch.StartNew();
    private bool                _inTick;             // reentrancy guard (single-threaded)

#if !SHIPPING
    private ConsoleEngine?       _console;
    private DebugConsoleOverlay? _consoleOverlay;
    private KeyboardState        _prevConsoleKb;
#endif

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

        // Limit catch-up after focus loss to ~3 frames instead of the default 30
        MaxElapsedTime = TimeSpan.FromMilliseconds(50);
    }

    protected override void Initialize()
    {
        base.Initialize();

        // Only needed on Windows — that's where the Win32 modal move loop blocks SDL
        if (OperatingSystem.IsWindows())
        {
            _sdlFilter = OnSdlEvent;
            SDL_AddEventWatch(_sdlFilter, IntPtr.Zero);
        }

#if !SHIPPING
        Window.TextInput += (_, e) => _consoleOverlay?.HandleTextInput(e.Character);
#endif
    }

    // Called by SDL for every event, including during the Win32 modal window-move loop.
    // When the normal game loop is blocked, elapsed time grows past the threshold and we
    // tick manually to keep physics and networking alive.
    private int OnSdlEvent(IntPtr userdata, IntPtr sdlEvent)
    {
        if (!_inTick && _sinceLastTick.ElapsedMilliseconds > 32)
        {
            _inTick = true;
            Tick();   // Tick() → Update() → _sinceLastTick.Restart(), preventing re-entry
            _inTick = false;
        }
        return 0;
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

        MapRegistry.Load(Path.Combine(AppContext.BaseDirectory, "Maps"));
        GameConfig.Load(Path.Combine(AppContext.BaseDirectory, "gameconfig.json"));

        MGT.Init(pixel, font, smallFont);

#if !SHIPPING
        _console = new ConsoleEngine();
        DevCommands.Register(_console.Registry);
        var consoleFont = font ?? smallFont;
        if (consoleFont != null)
            _consoleOverlay = new DebugConsoleOverlay(_console, pixel, consoleFont, VirtualWidth);
#endif

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
        _sinceLastTick.Restart();
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 1f / 20f);

        // Frame update order — contract from monogametemplate CLAUDE.md
        _touch.Update();
        _input.Update();
        InputHelper.Update(IsActive);
        _ui.BeginFrame();
        _tweener.Update(dt);
        _audio.Update(dt);

        Network.NetworkManager.Instance.ProcessPendingActions();

#if !SHIPPING
        {
            var currKb = Keyboard.GetState();
            if (InputHelper.IsKeyPressed(Keys.OemTilde))
                _console?.Toggle();

            if (_console?.IsOpen == true)
            {
                if (_consoleOverlay != null)
                {
                    bool escConsumed = _consoleOverlay.HandleKeyboard(currKb, _prevConsoleKb, dt);
                    if (!escConsumed && currKb.IsKeyDown(Keys.Escape) && _prevConsoleKb.IsKeyUp(Keys.Escape))
                        _console.Close();
                }
                _prevConsoleKb = currKb;
                base.Update(gameTime);
                return; // game state frozen while console is open
            }
            _prevConsoleKb = currKb;
        }
#endif

        var services = new StateServices(_ui, _input, _sb);
        _states.Update(dt, services);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        _scaler.BeginDraw();

        var services = new StateServices(_ui, _input, _sb);
        _states.Draw(_sb, services);

#if !SHIPPING
        if (_console?.IsOpen == true && _consoleOverlay != null)
        {
            _sb.Begin();
            _consoleOverlay.Draw(_sb);
            _sb.End();
        }
#endif

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
