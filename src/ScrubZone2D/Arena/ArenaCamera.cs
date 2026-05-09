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

    public void CenterOn(Vector2 worldPos)
    {
        // Clamp so the viewport stays inside the map.
        // When the map is smaller than the viewport in a dimension, center the map.
        float cx = _mapW <= ViewW
            ? _mapW / 2f
            : Math.Clamp(worldPos.X, ViewW / 2f, _mapW - ViewW / 2f);

        float cy = _mapH <= ViewH
            ? _mapH / 2f
            : Math.Clamp(worldPos.Y, ViewH / 2f, _mapH - ViewH / 2f);

        Transform = Matrix.CreateTranslation(
            MathF.Round(ViewW / 2f - cx),
            MathF.Round(ViewH / 2f - cy),
            0f);
    }
}
