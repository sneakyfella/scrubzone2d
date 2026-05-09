using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Physics;

namespace ScrubZone2D.Arena;

public sealed class LoadedArena : ArenaMap
{
    private const int Wall = 20;

    private readonly MapData _data;
    private readonly Color   _bgColor;
    private readonly Color   _wallColor;
    private readonly Color   _floorColor;

    public LoadedArena(MapData data)
    {
        _data       = data;
        _bgColor    = new Color(data.BgR,   data.BgG,   data.BgB);
        _wallColor  = new Color(data.WallR,  data.WallG,  data.WallB);
        _floorColor = new Color(data.FloorR, data.FloorG, data.FloorB);
    }

    public override int     WorldWidth  => _data.WorldWidth;
    public override int     WorldHeight => _data.WorldHeight;
    public override Vector2 SpawnHost   => new(_data.SpawnHostX,   _data.SpawnHostY);
    public override Vector2 SpawnJoiner => new(_data.SpawnJoinerX, _data.SpawnJoinerY);

    public override void CreatePhysicsBodies(PhysicsWorld physics)
    {
        int w = _data.WorldWidth, h = _data.WorldHeight;
        physics.CreateStaticBox(new Vector2(w / 2f, Wall / 2f),      w,    Wall);
        physics.CreateStaticBox(new Vector2(w / 2f, h - Wall / 2f),  w,    Wall);
        physics.CreateStaticBox(new Vector2(Wall / 2f,      h / 2f), Wall, h);
        physics.CreateStaticBox(new Vector2(w - Wall / 2f,  h / 2f), Wall, h);

        foreach (var wd in _data.Walls)
            physics.CreateStaticBox(new Vector2(wd.Cx, wd.Cy), wd.W, wd.H);
    }

    public override void Draw(SpriteBatch sb)
    {
        int w = _data.WorldWidth, h = _data.WorldHeight;
        UIRenderer.FillRect(sb, new Rectangle(0,    0,         w, h),         _bgColor);
        UIRenderer.FillRect(sb, new Rectangle(Wall, Wall, w - Wall * 2, h - Wall * 2), _floorColor);

        UIRenderer.FillRect(sb, new Rectangle(0, 0,         w, Wall), _wallColor);
        UIRenderer.FillRect(sb, new Rectangle(0, h - Wall,  w, Wall), _wallColor);
        UIRenderer.FillRect(sb, new Rectangle(0, 0,      Wall, h),    _wallColor);
        UIRenderer.FillRect(sb, new Rectangle(w - Wall, 0, Wall, h),  _wallColor);

        foreach (var wd in _data.Walls)
            UIRenderer.FillRect(sb,
                new Rectangle(wd.Cx - wd.W / 2, wd.Cy - wd.H / 2, wd.W, wd.H),
                _wallColor);
    }
}
