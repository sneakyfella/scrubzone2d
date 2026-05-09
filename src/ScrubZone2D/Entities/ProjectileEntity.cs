using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;
using XnaVec = Microsoft.Xna.Framework.Vector2;

namespace ScrubZone2D.Entities;

public sealed class ProjectileEntity
{
    // Kinetic bounces up to 4 times; laser is destroyed on first wall hit
    private static readonly int[] MaxBouncesPerType = { 5, 1 };

    public int         Id            { get; }
    public byte        OwnerPlayerId { get; }
    public WeaponType  Type          { get; }
    public int         Damage        { get; }
    public PhysicsBody PhysicsBody   { get; }
    public bool        IsDestroyed   { get; private set; }
    public bool        IsLocal       { get; }

    private readonly PhysicsBodyData _bodyData;

    public XnaVec Position => PhysicsWorld.ToXna(PhysicsBody.GetPosition());
    public XnaVec Velocity => PhysicsWorld.ToXna(PhysicsBody.GetLinearVelocity());

    public int BounceCount => _bodyData.BounceCount;

    public ProjectileEntity(int id, byte ownerPlayerId, bool isLocal, PhysicsBody physicsBody,
        WeaponType type, int damage)
    {
        Id            = id;
        OwnerPlayerId = ownerPlayerId;
        IsLocal       = isLocal;
        PhysicsBody   = physicsBody;
        _bodyData     = physicsBody.Data;
        Type          = type;
        Damage        = damage;
    }

    public void Destroy() => IsDestroyed = true;

    public bool ShouldDestroy() => BounceCount >= MaxBouncesPerType[(int)Type];

    public void Draw(SpriteBatch sb)
    {
        if (IsDestroyed) return;
        var pos = Position;

        if (Type == WeaponType.Laser)
        {
            var vel   = Velocity;
            float spd = vel.Length();
            if (spd > 0.1f && MGT.Pixel != null)
            {
                var dir   = vel / spd;
                var from  = pos - dir * 12f;
                var color = OwnerPlayerId == 0xFF
                    ? Color.FromNonPremultiplied(255, 230, 100, 220)
                    : Color.FromNonPremultiplied(100, 230, 255, 220);
                sb.Draw(MGT.Pixel, from, null, color,
                    MathF.Atan2(dir.Y, dir.X), XnaVec.Zero,
                    new XnaVec(24f, 2f), SpriteEffects.None, 0f);
            }
        }
        else
        {
            const int r = 5;
            UIRenderer.FillRect(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2),
                OwnerPlayerId == 0xFF ? new Color(255, 220, 80) : new Color(80, 200, 255));
        }
    }
}
