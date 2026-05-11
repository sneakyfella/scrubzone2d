using Microsoft.Xna.Framework;
using ScrubZone2D;

namespace ScrubZone2D.Arena;

// Player-following camera. Centers on a world position and clamps to map edges
// so the viewport never shows outside the map bounds.
public sealed class ArenaCamera
{
    private const int ViewW = Game1.VirtualWidth;
    private const int ViewH = Game1.VirtualHeight;

    private readonly int _mapW;
    private readonly int _mapH;

    public Matrix Transform { get; private set; } = Matrix.Identity;

    public ArenaCamera(int mapWidth, int mapHeight)
    {
        _mapW = mapWidth;
        _mapH = mapHeight;
    }

    // Transform is a pure translation, so screen→world is just the inverse translation.
    public Vector2 ScreenToWorld(Vector2 screenPos) =>
        new(screenPos.X - Transform.M41, screenPos.Y - Transform.M42);

    public void CenterOn(Vector2 worldPos)
    {
        // Follow the player, clamping only when the map is large enough that the
        // viewport would otherwise show outside the map bounds.
        float cx = _mapW <= ViewW
            ? worldPos.X
            : Math.Clamp(worldPos.X, ViewW / 2f, _mapW - ViewW / 2f);

        float cy = _mapH <= ViewH
            ? worldPos.Y
            : Math.Clamp(worldPos.Y, ViewH / 2f, _mapH - ViewH / 2f);

        Transform = Matrix.CreateTranslation(
            MathF.Round(ViewW / 2f - cx),
            MathF.Round(ViewH / 2f - cy),
            0f);
    }
}
