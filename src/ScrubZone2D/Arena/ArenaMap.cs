using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScrubZone2D.Physics;

namespace ScrubZone2D.Arena;

public abstract class ArenaMap
{
    public abstract int     WorldWidth  { get; }
    public abstract int     WorldHeight { get; }
    public abstract Vector2 SpawnHost   { get; }
    public abstract Vector2 SpawnJoiner { get; }

    public abstract void CreatePhysicsBodies(PhysicsWorld physics);
    public abstract void Draw(SpriteBatch sb);

    public static ArenaMap Create(byte mapId) =>
        new LoadedArena(MapRegistry.GetOrDefault(mapId));

    public static string NameOf(byte mapId) => MapRegistry.NameOf(mapId);
}
