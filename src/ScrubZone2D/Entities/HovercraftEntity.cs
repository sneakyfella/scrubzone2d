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
    public const float Radius           = 18f;
    public const float ThrustForce      = 320f;
    public const float RotateSpeed      = 3.2f;
    public const float MaxSpeedPx       = 380f;
    public const float FireCooldown     = 0.45f;
    public const float ProjectileSpd    = 520f;
    public const float TurretLen        = 30f;
    public const int   MaxShield        = 75;
    public const float ShieldRegenRate  = 20f;  // points/s
    public const float ShieldRegenDelay = 3f;   // seconds after last damage
    public const float MaxChargeTime    = 2f;
    public const float MinChargeToFire  = 0.4f;
    public const float LaserSpeed       = 1400f;
    public const int   MinLaserDamage   = 15;
    public const int   MaxLaserDamage   = 55;
    public const int   KineticDamage    = 25;

    public byte   PlayerId    { get; }
    public bool   IsLocal     { get; }
    public int    Health      { get; set; } = 100;
    public int    Shield      { get; set; } = MaxShield;
    public bool   IsAlive     { get; private set; } = true;
    public Color  HullColor   { get; }
    public PhysicsBody PhysicsBody { get; }
    public float  TurretAngle { get; set; }
    public float  BodyAngle   => PhysicsBody.GetAngle();

    public XnaVec RemotePosition    { get; set; }
    public float  RemoteBodyAngle   { get; set; }
    public float  RemoteTurretAngle { get; set; }
    public uint   RemoteTick        { get; set; }

    private float _fireCooldown;
    private float _shieldRegenTimer;
    private float _shieldRegen;
    private float _chargeTime;
    private int   _nextProjId = 1;

    // Raised on fire: (projId, origin, velocity, weaponType, damage)
    public event Action<int, XnaVec, XnaVec, WeaponType, int>? OnFire;

    public HovercraftEntity(byte playerId, bool isLocal, Color hullColor, PhysicsBody physicsBody)
    {
        PlayerId       = playerId;
        IsLocal        = isLocal;
        HullColor      = hullColor;
        PhysicsBody    = physicsBody;
        RemotePosition = PhysicsWorld.ToXna(physicsBody.GetPosition());
    }

    public XnaVec Position => PhysicsWorld.ToXna(PhysicsBody.GetPosition());
    public XnaVec Velocity => PhysicsWorld.ToXna(PhysicsBody.GetLinearVelocity());

    public void Die()
    {
        IsAlive     = false;
        _chargeTime = 0f;
        PhysicsBody.SetTransform(PhysicsWorld.ToB2(new XnaVec(-500f, -500f)), 0f);
        PhysicsBody.SetLinearVelocity(PhysicsWorld.ToB2(XnaVec.Zero));
    }

    public void Respawn(XnaVec pos)
    {
        IsAlive           = true;
        Health            = 100;
        Shield            = MaxShield;
        _shieldRegenTimer = 0f;
        _shieldRegen      = 0f;
        _chargeTime       = 0f;
        _fireCooldown     = 0f;
        PhysicsBody.SetTransform(PhysicsWorld.ToB2(pos), 0f);
        PhysicsBody.SetLinearVelocity(PhysicsWorld.ToB2(XnaVec.Zero));
    }

    public void TakeDamage(int remainingShield, int remainingHull)
    {
        Shield            = remainingShield;
        Health            = remainingHull;
        _shieldRegenTimer = ShieldRegenDelay;
        _shieldRegen      = 0f;
    }

    public void UpdateLocal(float dt, InputHandler input)
    {
        if (!IsAlive || !InputHelper.IsActive) return;

        // Shield regen after combat delay
        if (_shieldRegenTimer > 0f)
            _shieldRegenTimer = MathF.Max(0f, _shieldRegenTimer - dt);
        else if (Shield < MaxShield)
        {
            _shieldRegen += ShieldRegenRate * dt;
            int gained = (int)_shieldRegen;
            if (gained > 0)
            {
                _shieldRegen -= gained;
                Shield = Math.Min(MaxShield, Shield + gained);
            }
        }

        _fireCooldown = MathF.Max(0f, _fireCooldown - dt);

        var kb      = Keyboard.GetState();
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

        var vel = PhysicsBody.GetLinearVelocity();
        float spd = vel.Length();
        if (spd > PhysicsWorld.ToM(MaxSpeedPx))
            PhysicsBody.SetLinearVelocity(vel / spd * PhysicsWorld.ToM(MaxSpeedPx));

        var mousePos = input.VirtualPositionF;
        var pos      = Position;
        TurretAngle  = MathF.Atan2(mousePos.Y - pos.Y, mousePos.X - pos.X);

        // Left click: kinetic (instant, has cooldown)
        if (InputHelper.IsMouseClicked() && _fireCooldown <= 0f)
            FireKinetic();

        // Right click: hold to charge, release to fire laser
        if (InputHelper.IsMouseDown(1))
            _chargeTime += dt;

        if (InputHelper.IsMouseReleased(1))
        {
            if (_chargeTime >= MinChargeToFire)
                FireLaser();
            _chargeTime = 0f;
        }
    }

    // Host calls this each frame to drive the joiner's physics body from received input.
    public void DriveFromInput(JoinerInputPacket input, float dt)
    {
        if (!IsAlive) return;
        var moveDir = new XnaVec(input.DirX, input.DirY);
        if (moveDir != XnaVec.Zero)
        {
            moveDir = XnaVec.Normalize(moveDir) * ThrustForce;
            PhysicsBody.ApplyForceToCenter(PhysicsWorld.ToB2(moveDir), true);
        }
        var vel = PhysicsBody.GetLinearVelocity();
        float spd = vel.Length();
        if (spd > PhysicsWorld.ToM(MaxSpeedPx))
            PhysicsBody.SetLinearVelocity(vel / spd * PhysicsWorld.ToM(MaxSpeedPx));
        TurretAngle = input.AimAngle;

        // Mirror shield regen on host for joiner entity
        if (_shieldRegenTimer > 0f)
            _shieldRegenTimer = MathF.Max(0f, _shieldRegenTimer - dt);
        else if (Shield < MaxShield)
        {
            _shieldRegen += ShieldRegenRate * dt;
            int gained = (int)_shieldRegen;
            if (gained > 0) { _shieldRegen -= gained; Shield = Math.Min(MaxShield, Shield + gained); }
        }
    }

    // Joiner calls this after UpdateLocal to snapshot the current input state for sending.
    public JoinerInputPacket BuildJoinerInputPacket(InputHandler input)
    {
        if (!IsAlive || !InputHelper.IsActive) return new JoinerInputPacket();
        var kb = Keyboard.GetState();
        float dx = 0f, dy = 0f;
        if (kb.IsKeyDown(Keys.W)) dy -= 1f;
        if (kb.IsKeyDown(Keys.S)) dy += 1f;
        if (kb.IsKeyDown(Keys.A)) dx -= 1f;
        if (kb.IsKeyDown(Keys.D)) dx += 1f;
        if (dx != 0 || dy != 0)
        { var n = XnaVec.Normalize(new XnaVec(dx, dy)); dx = n.X; dy = n.Y; }
        return new JoinerInputPacket
        {
            DirX = dx, DirY = dy,
            AimAngle  = TurretAngle,
            RightHeld  = InputHelper.IsMouseDown(1),
            ChargeTime = _chargeTime
        };
    }

    public void ApplyRemoteState(HovercraftStatePacket pkt)
    {
        if (!IsAlive) return;
        if (pkt.Tick < RemoteTick) return;
        RemoteTick        = pkt.Tick;
        RemotePosition    = new XnaVec(pkt.X, pkt.Y);
        RemoteBodyAngle   = pkt.BodyAngle;
        RemoteTurretAngle = pkt.TurretAngle;
        Shield            = pkt.Shield;
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
        TurretAngle = TurretAngle,
        Shield      = (byte)Shield
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

    private void FireKinetic()
    {
        _fireCooldown = FireCooldown;
        var pos = Position;
        var dir = new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle));
        var tip = pos + dir * (Radius + TurretLen);
        OnFire?.Invoke(_nextProjId++, tip, dir * ProjectileSpd, WeaponType.Kinetic, KineticDamage);
    }

    private void FireLaser()
    {
        float chargeFraction = Math.Clamp(
            (_chargeTime - MinChargeToFire) / (MaxChargeTime - MinChargeToFire), 0f, 1f);
        int damage = MinLaserDamage + (int)(chargeFraction * (MaxLaserDamage - MinLaserDamage));
        var pos = Position;
        var dir = new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle));
        var tip = pos + dir * (Radius + TurretLen);
        OnFire?.Invoke(_nextProjId++, tip, dir * LaserSpeed, WeaponType.Laser, damage);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!IsAlive) return;

        var pos      = IsLocal ? Position    : RemotePosition;
        float bAngle = IsLocal ? BodyAngle   : RemoteBodyAngle;
        float tAngle = IsLocal ? TurretAngle : RemoteTurretAngle;
        int   r      = (int)Radius;

        // Shield ring — brightness proportional to shield level
        if (Shield > 0)
        {
            const int pad = 4;
            float fraction = Shield / (float)MaxShield;
            int   alpha    = (int)(40 + fraction * 160f);
            UIRenderer.DrawBorder(sb,
                new Rectangle((int)pos.X - r - pad, (int)pos.Y - r - pad,
                              (r + pad) * 2, (r + pad) * 2),
                Color.FromNonPremultiplied(60, 180, 255, alpha), 3);
        }

        // Hull
        UIRenderer.FillRect(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2), HullColor);
        UIRenderer.DrawBorder(sb, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2), Color.White, 2);

        // Facing indicator
        var faceTip = pos + new XnaVec(MathF.Cos(bAngle), MathF.Sin(bAngle)) * r;
        DrawLine(sb, pos, faceTip, Color.White, 2);

        // Turret barrel
        var turretTip = pos + new XnaVec(MathF.Cos(tAngle), MathF.Sin(tAngle)) * (r + TurretLen);
        DrawLine(sb, pos, turretTip, Color.LightGray, 3);

        // Shield bar
        const int bw = 36;
        UIRenderer.ProgressBar(sb, new Rectangle((int)pos.X - bw / 2, (int)pos.Y - r - 18, bw, 4),
            Shield, MaxShield, new Color(60, 180, 255), new Color(15, 35, 60));

        // HP bar
        UIRenderer.ProgressBar(sb, new Rectangle((int)pos.X - bw / 2, (int)pos.Y - r - 12, bw, 5),
            Health, 100, Color.LimeGreen, new Color(60, 20, 20));

        // Laser charge bar (local player only, shown when charging)
        if (IsLocal && _chargeTime >= MinChargeToFire * 0.3f)
        {
            UIRenderer.ProgressBar(sb,
                new Rectangle((int)pos.X - bw / 2, (int)pos.Y + r + 4, bw, 4),
                _chargeTime, MaxChargeTime, new Color(80, 255, 200), new Color(20, 50, 40));
        }
    }
}
