using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Physics;

namespace ScrubZone2D.Arena;

// Fixed arena with a boundary and random interior walls.
// All coordinates are in virtual pixels (1280x720 space).
public sealed class ArenaMap
{
    public const int X = 40;
    public const int Y = 40;
    public const int W = 1200;
    public const int H = 640;
    public const int Wall = 20;

    // Interior walls: (centerX, centerY, width, height)
    private static readonly (int cx, int cy, int w, int h)[] Walls =
    [
        (320,  200, 20,  160),
        (320,  520, 20,  160),
        (640,  200, 160, 20),
        (640,  520, 160, 20),
        (960,  200, 20,  160),
        (960,  520, 20,  160),
        (200,  360, 160, 20),
        (1080, 360, 160, 20),
        (640,  360, 80,  80),
    ];

    // Spawn positions (pixel centres of open areas)
    public static readonly Vector2 SpawnHost   = new(100, 100);
    public static readonly Vector2 SpawnJoiner = new(1180, 620);

    private readonly Color _bgColor   = new(18, 18, 30);
    private readonly Color _wallColor = new(70, 80, 100);

    // Call once after PhysicsWorld is created
    public void CreatePhysicsBodies(PhysicsWorld physics)
    {
        // Boundary walls (top, bottom, left, right)
        var cx = X + W / 2f;
        var cy = Y + H / 2f;
        physics.CreateStaticBox(new Vector2(cx, Y + Wall / 2f),        W, Wall);        // top
        physics.CreateStaticBox(new Vector2(cx, Y + H - Wall / 2f),    W, Wall);        // bottom
        physics.CreateStaticBox(new Vector2(X + Wall / 2f, cy),        Wall, H);        // left
        physics.CreateStaticBox(new Vector2(X + W - Wall / 2f, cy),    Wall, H);        // right

        // Interior walls
        foreach (var (wcx, wcy, ww, wh) in Walls)
            physics.CreateStaticBox(new Vector2(X + wcx, Y + wcy), ww, wh);
    }

    public void Draw(SpriteBatch sb)
    {
        // Background
        UIRenderer.FillRect(sb, new Rectangle(X, Y, W, H), _bgColor);

        // Boundary walls
        UIRenderer.FillRect(sb, new Rectangle(X, Y, W, Wall),                          _wallColor); // top
        UIRenderer.FillRect(sb, new Rectangle(X, Y + H - Wall, W, Wall),               _wallColor); // bottom
        UIRenderer.FillRect(sb, new Rectangle(X, Y, Wall, H),                          _wallColor); // left
        UIRenderer.FillRect(sb, new Rectangle(X + W - Wall, Y, Wall, H),               _wallColor); // right

        // Interior walls
        foreach (var (wcx, wcy, ww, wh) in Walls)
            UIRenderer.FillRect(sb, new Rectangle(X + wcx - ww / 2, Y + wcy - wh / 2, ww, wh), _wallColor);
    }
}
