using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate;
using MonoGameTemplate.Particles;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;
using XnaVec = Microsoft.Xna.Framework.Vector2;

namespace ScrubZone2D.Entities;

public sealed class ProjectileEntity
{
    private readonly int _maxBounces;

    public int         Id            { get; }
    public byte        OwnerPlayerId { get; }
    public WeaponType  Type          { get; }
    public int         Damage        { get; }
    public PhysicsBody PhysicsBody   { get; }
    public bool        IsDestroyed      { get; private set; }
    public bool        SuppressExplosion { get; set; }
    public bool        IsLocal          { get; }

    private readonly PhysicsBodyData _bodyData;
    private readonly ParticleSystem? _trail;

    public XnaVec Position => PhysicsWorld.ToXna(PhysicsBody.GetPosition());
    public XnaVec Velocity => PhysicsWorld.ToXna(PhysicsBody.GetLinearVelocity());

    public int     BounceCount  => _bodyData.BounceCount;
    public XnaVec? BouncePos   => _bodyData.BouncePos;
    public XnaVec? IncomingDir => _bodyData.IncomingDir;

    public ProjectileEntity(int id, byte ownerPlayerId, bool isLocal, PhysicsBody physicsBody,
        WeaponType type, int damage, int maxBounces)
    {
        Id            = id;
        OwnerPlayerId = ownerPlayerId;
        IsLocal       = isLocal;
        PhysicsBody   = physicsBody;
        _bodyData     = physicsBody.Data;
        Type          = type;
        Damage        = damage;
        _maxBounces   = maxBounces;

        if (type == WeaponType.Laser || type == WeaponType.LaserOrb)
        {
            bool isOrb  = type == WeaponType.LaserOrb;
            int[] col   = ownerPlayerId == 0xFF
                ? (isOrb ? [255, 160, 40]  : [255, 230, 100])
                : (isOrb ? [160, 80, 255]  : [100, 230, 255]);

            _trail = isOrb
                ? new ParticleSystem(new ParticleEmitterData
                  {
                      MaxParticles = 300,
                      LifetimeMin  = 0.18f, LifetimeMax = 0.35f,
                      SpeedMin     = 8f,    SpeedMax    = 28f,
                      Angle        = 0f,    Spread      = 180f,
                      Gravity      = 0f,
                      StartColor   = col,   EndColor    = col,
                      StartAlpha   = 0.85f, EndAlpha    = 0f,
                      StartSize    = 12f,   EndSize     = 2f,
                  })
                : new ParticleSystem(new ParticleEmitterData
                  {
                      MaxParticles = 256,
                      LifetimeMin  = 0.08f, LifetimeMax = 0.14f,
                      SpeedMin     = 0f,    SpeedMax    = 4f,
                      Angle        = 0f,    Spread      = 180f,
                      Gravity      = 0f,
                      StartColor   = col,   EndColor    = col,
                      StartAlpha   = 0.9f,  EndAlpha    = 0f,
                      StartSize    = 5f,    EndSize     = 1f,
                  });
        }
    }

    public void Destroy() => IsDestroyed = true;

    public bool ShouldDestroy() => BounceCount >= _maxBounces;

    public void UpdateEffects(float dt)
    {
        if (_trail == null) return;
        _trail.Update(dt);
        if (!IsDestroyed)
        {
            var p = Position;
            int count = Type == WeaponType.LaserOrb ? 14 : 6;
            for (int i = 0; i < count; i++)
                _trail.EmitAt(p);
        }
    }

    public void Draw(SpriteBatch sb)
    {
        if (IsDestroyed) return;
        var pos = Position;

        if (Type == WeaponType.Laser)
        {
            var vel = Velocity;
            float spd = vel.Length();
            if (spd < 0.1f || MGT.Pixel == null) return;

            var dir = vel / spd;

            var colorFull = OwnerPlayerId == 0xFF
                ? Color.FromNonPremultiplied(255, 230, 100, 230)
                : Color.FromNonPremultiplied(100, 230, 255, 230);
            var colorGlow = Color.FromNonPremultiplied(colorFull.R, colorFull.G, colorFull.B, 60);

            _trail?.Draw(sb, XnaVec.Zero);
            DrawLaserLine(sb, pos - dir * 14f, pos, colorFull, 3);
            DrawLaserLine(sb, pos - dir * 14f, pos, colorGlow, 6);
        }
        else if (Type == WeaponType.LaserOrb)
        {
            _trail?.Draw(sb, XnaVec.Zero);

            var cFull = OwnerPlayerId == 0xFF
                ? Color.FromNonPremultiplied(255, 160, 40, 230)
                : Color.FromNonPremultiplied(160, 80, 255, 230);
            var cGlow = Color.FromNonPremultiplied(cFull.R, cFull.G, cFull.B, 55);
            var cCore = Color.FromNonPremultiplied(255, 240, 220, 210);

            const int gr = 22;
            UIRenderer.FillRect(sb, new Rectangle((int)pos.X - gr, (int)pos.Y - gr, gr * 2, gr * 2), cGlow);
            UIRenderer.FillRect(sb, new Rectangle((int)pos.X - 14, (int)pos.Y - 14, 28, 28), cFull);
            UIRenderer.FillRect(sb, new Rectangle((int)pos.X - 6, (int)pos.Y - 6, 12, 12), cCore);
        }
        else
        {
            const int r = 5;
            UIRenderer.FillRect(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2),
                OwnerPlayerId == 0xFF ? new Color(255, 220, 80) : new Color(80, 200, 255));
        }
    }

    private static void DrawLaserLine(SpriteBatch sb, XnaVec from, XnaVec to, Color color, int thickness)
    {
        var   d   = to - from;
        float len = d.Length();
        if (len < 0.5f) return;
        sb.Draw(MGT.Pixel!, from, null, color,
            MathF.Atan2(d.Y, d.X), XnaVec.Zero,
            new XnaVec(len, thickness), SpriteEffects.None, 0f);
    }
}
