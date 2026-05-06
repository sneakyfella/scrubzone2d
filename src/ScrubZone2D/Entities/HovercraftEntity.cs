using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;
using XnaVec = Microsoft.Xna.Framework.Vector2;

namespace ScrubZone2D.Entities;

public sealed class HovercraftEntity
{
    public const float Radius        = 18f;
    public const float ThrustForce   = 320f;  // pixels/s²
    public const float RotateSpeed   = 3.2f;  // radians/s
    public const float MaxSpeedPx    = 380f;  // pixels/s
    public const float FireCooldown  = 0.45f; // seconds
    public const float ProjectileSpd = 520f;  // pixels/s
    public const float TurretLen     = 30f;

    public byte   PlayerId    { get; }
    public bool   IsLocal     { get; }
    public int    Health      { get; set; } = 100;
    public bool   IsAlive     => Health > 0;
    public Color  HullColor   { get; }
    public PhysicsBody PhysicsBody { get; }
    public float  TurretAngle { get; set; }
    public float  BodyAngle   => PhysicsBody.GetAngle();

    // Remote interpolation targets
    public XnaVec RemotePosition    { get; set; }
    public float  RemoteBodyAngle   { get; set; }
    public float  RemoteTurretAngle { get; set; }
    public uint   RemoteTick        { get; set; }

    private float _fireCooldown;
    private int   _nextProjId = 1;

    // Raised when firing: (projId, origin, velocity) — caller creates the entity + sends packet
    public event Action<int, XnaVec, XnaVec>? OnFire;

    public HovercraftEntity(byte playerId, bool isLocal, Color hullColor, PhysicsBody physicsBody)
    {
        PlayerId    = playerId;
        IsLocal     = isLocal;
        HullColor   = hullColor;
        PhysicsBody = physicsBody;
        RemotePosition = PhysicsWorld.ToXna(physicsBody.GetPosition());
    }

    public XnaVec Position => PhysicsWorld.ToXna(PhysicsBody.GetPosition());
    public XnaVec Velocity => PhysicsWorld.ToXna(PhysicsBody.GetLinearVelocity());

    // Drive local hovercraft from keyboard + mouse (no camera — arena fits in viewport)
    public void UpdateLocal(float dt, InputHandler input)
    {
        if (!IsAlive || !InputHelper.IsActive) return;

        _fireCooldown = MathF.Max(0f, _fireCooldown - dt);

        var kb = Keyboard.GetState();

        // WASD — absolute directional impulse (W=up, S=down, A=left, D=right)
        var moveDir = XnaVec.Zero;
        if (kb.IsKeyDown(Keys.W)) moveDir.Y -= 1f;
        if (kb.IsKeyDown(Keys.S)) moveDir.Y += 1f;
        if (kb.IsKeyDown(Keys.A)) moveDir.X -= 1f;
        if (kb.IsKeyDown(Keys.D)) moveDir.X += 1f;

        if (moveDir != XnaVec.Zero)
        {
            moveDir = XnaVec.Normalize(moveDir) * ThrustForce;
            PhysicsBody.ApplyForceToCenter(PhysicsWorld.ToB2(moveDir), true);
        }

        // Clamp speed
        var vel   = PhysicsBody.GetLinearVelocity();
        float spd = vel.Length();
        if (spd > PhysicsWorld.ToM(MaxSpeedPx))
            PhysicsBody.SetLinearVelocity(vel / spd * PhysicsWorld.ToM(MaxSpeedPx));

        // Turret tracks mouse (virtual coords == world coords since arena fits in viewport)
        var mousePos  = input.VirtualPositionF;
        var pos       = Position;
        TurretAngle   = MathF.Atan2(mousePos.Y - pos.Y, mousePos.X - pos.X);

        // Fire on left click
        if (InputHelper.IsMouseClicked() && _fireCooldown <= 0f)
            Fire();
    }

    public void ApplyRemoteState(HovercraftStatePacket pkt)
    {
        if (pkt.Tick < RemoteTick) return; // discard stale packets
        RemoteTick        = pkt.Tick;
        RemotePosition    = new XnaVec(pkt.X, pkt.Y);
        RemoteBodyAngle   = pkt.BodyAngle;
        RemoteTurretAngle = pkt.TurretAngle;

        // Snap physics body to authoritative remote position
        PhysicsBody.SetTransform(PhysicsWorld.ToB2(RemotePosition), RemoteBodyAngle);
        PhysicsBody.SetLinearVelocity(PhysicsWorld.ToB2(new XnaVec(pkt.VelX, pkt.VelY)));
        TurretAngle = RemoteTurretAngle;
    }

    public HovercraftStatePacket BuildStatePacket() => new()
    {
        PlayerId    = PlayerId,
        X           = Position.X,
        Y           = Position.Y,
        VelX        = Velocity.X,
        VelY        = Velocity.Y,
        BodyAngle   = BodyAngle,
        TurretAngle = TurretAngle
    };

    private static void DrawLine(SpriteBatch sb, XnaVec from, XnaVec to, Color color, int thickness)
    {
        if (MGT.Pixel == null) return;
        var   dir = to - from;
        float len = dir.Length();
        if (len < 0.5f) return;
        sb.Draw(MGT.Pixel, from, null, color, MathF.Atan2(dir.Y, dir.X),
                XnaVec.Zero, new XnaVec(len, thickness), SpriteEffects.None, 0f);
    }

    private void Fire()
    {
        _fireCooldown = FireCooldown;
        var pos = Position;
        var tip = pos + new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle)) * (Radius + TurretLen);
        var vel = new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle)) * ProjectileSpd;
        OnFire?.Invoke(_nextProjId++, tip, vel);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!IsAlive) return;

        var pos       = IsLocal ? Position        : RemotePosition;
        float bAngle  = IsLocal ? BodyAngle       : RemoteBodyAngle;
        float tAngle  = IsLocal ? TurretAngle     : RemoteTurretAngle;
        int   r       = (int)Radius;

        // Hull (filled square approximating circle)
        UIRenderer.FillRect(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2), HullColor);
        UIRenderer.DrawBorder(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2), Color.White, 2);

        // Facing indicator
        var faceTip = pos + new XnaVec(MathF.Cos(bAngle), MathF.Sin(bAngle)) * r;
        DrawLine(sb, pos, faceTip, Color.White, 2);

        // Turret barrel
        var turretTip = pos + new XnaVec(MathF.Cos(tAngle), MathF.Sin(tAngle)) * (r + TurretLen);
        DrawLine(sb, pos, turretTip, Color.LightGray, 3);

        // HP bar above hull
        const int bw = 36, bh = 5;
        UIRenderer.ProgressBar(sb, new Rectangle((int)pos.X - bw / 2, (int)pos.Y - r - 10, bw, bh),
            Health, 100, Color.LimeGreen, new Color(60, 20, 20));
    }
}
