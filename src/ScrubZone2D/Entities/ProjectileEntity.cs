using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Physics;
using XnaVec = Microsoft.Xna.Framework.Vector2;

namespace ScrubZone2D.Entities;

public sealed class ProjectileEntity
{
    public const int MaxBounces = 5;
    public const int Damage     = 25;

    public int   Id            { get; }
    public byte  OwnerPlayerId { get; }
    public PhysicsBody PhysicsBody { get; }
    public bool  IsDestroyed   { get; private set; }
    public bool  IsLocal       { get; }

    private readonly PhysicsBodyData _bodyData;

    public XnaVec Position => PhysicsWorld.ToXna(PhysicsBody.GetPosition());

    public int BounceCount => _bodyData.BounceCount;

    public ProjectileEntity(int id, byte ownerPlayerId, bool isLocal, PhysicsBody physicsBody)
    {
        Id            = id;
        OwnerPlayerId = ownerPlayerId;
        IsLocal       = isLocal;
        PhysicsBody   = physicsBody;
        _bodyData     = physicsBody.Data;
    }

    public void Destroy() => IsDestroyed = true;

    public bool ShouldDestroy() => BounceCount >= MaxBounces;

    public void Draw(SpriteBatch sb)
    {
        if (IsDestroyed) return;

        var pos = Position;
        const int r = 5;
        UIRenderer.FillRect(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2),
            OwnerPlayerId == 0xFF ? new Color(255, 220, 80) : new Color(80, 200, 255));
    }
}
