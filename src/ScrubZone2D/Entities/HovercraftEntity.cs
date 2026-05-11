using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Config;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;
using XnaVec = Microsoft.Xna.Framework.Vector2;

namespace ScrubZone2D.Entities;

public sealed class HovercraftEntity
{
    public const float Radius           = 18f;
    public static float ThrustForce           => GameConfig.Current.Ship.ThrustForce;
    public static float RotateSpeed           => GameConfig.Current.Ship.RotateSpeed;
    public static float MaxSpeedPx            => GameConfig.Current.Ship.MaxSpeedPx;
    public static float FireCooldown          => GameConfig.Current.Kinetic.FireCooldown;
    public static float ProjectileSpd         => LaserSpeed * GameConfig.Current.Kinetic.SpeedFactor;
    public static float TurretLen             => GameConfig.Current.Ship.TurretLen;
    public static int   MaxShield             => GameConfig.Current.Ship.MaxShield;
    public static float ShieldRegenRate       => GameConfig.Current.Ship.ShieldRegenRate;
    public static float ShieldRegenDelay      => GameConfig.Current.Ship.ShieldRegenDelay;
    public static float MaxChargeTime         => GameConfig.Current.Laser.MaxChargeTime;
    public static float MinChargeToFire       => GameConfig.Current.Laser.MinChargeTime;
    public static float LaserSpeed            => GameConfig.Current.Laser.Speed;
    public static int   MinLaserDamage        => GameConfig.Current.Laser.MinDamage;
    public static int   MaxLaserDamage        => GameConfig.Current.Laser.MaxDamage;
    public static int   KineticDamage         => GameConfig.Current.Kinetic.Damage;
    public static float KineticBlastRadius    => GameConfig.Current.Kinetic.BlastRadius;
    public static float KineticBlastImpulsePx => GameConfig.Current.Kinetic.BlastImpulsePx;
    public static float OrbSpeed              => GameConfig.Current.LaserOrb.Speed;
    public static float OrbBlastRadius        => GameConfig.Current.LaserOrb.BlastRadius;
    public static float OrbBlastImpulsePx     => GameConfig.Current.LaserOrb.BlastImpulsePx;
    public static float OrbTriggerRadius      => GameConfig.Current.LaserOrb.TriggerRadius;
    public static int   OrbEmpDamage          => GameConfig.Current.LaserOrb.EmpDamage;

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
    private KeyboardState _prevKb;

    private readonly List<(DebuffType Type, float TimeLeft)> _debuffs = new();
    private readonly List<(BuffType   Type, float TimeLeft)> _buffs   = new();

    public bool  ShieldDeployed { get; private set; }
    public float ShieldAngle    { get; private set; }

    public float ChargeTime  => _chargeTime;

    public float EffectiveMaxSpeedPx =>
        HasDebuff(DebuffType.EmpSlow)
            ? MaxSpeedPx * GameConfig.Current.Ship.EmpSlowFactor
            : MaxSpeedPx;

    public float DamageMultiplier =>
        HasBuff(BuffType.DamageBoost)
            ? 1f + GameConfig.Current.Ship.DamageBoostFactor
            : 1f;

    public bool HasDebuff(DebuffType type) => _debuffs.Any(d => d.Type == type);
    public bool HasBuff  (BuffType   type) => _buffs  .Any(b => b.Type == type);

    public void ApplyDebuff(DebuffType type, float duration)
    {
        for (int i = 0; i < _debuffs.Count; i++)
        {
            if (_debuffs[i].Type != type) continue;
            if (duration > _debuffs[i].TimeLeft) _debuffs[i] = (type, duration);
            return;
        }
        _debuffs.Add((type, duration));
    }

    public void ApplyBuff(BuffType type, float duration)
    {
        for (int i = 0; i < _buffs.Count; i++)
        {
            if (_buffs[i].Type != type) continue;
            if (duration > _buffs[i].TimeLeft) _buffs[i] = (type, duration);
            return;
        }
        _buffs.Add((type, duration));
    }

    public void UpdateDebuffs(float dt)
    {
        for (int i = _debuffs.Count - 1; i >= 0; i--)
        {
            var (t, tl) = _debuffs[i];
            if (tl - dt <= 0f) _debuffs.RemoveAt(i);
            else               _debuffs[i] = (t, tl - dt);
        }
    }

    public void UpdateBuffs(float dt)
    {
        for (int i = _buffs.Count - 1; i >= 0; i--)
        {
            var (t, tl) = _buffs[i];
            if (tl - dt <= 0f) _buffs.RemoveAt(i);
            else               _buffs[i] = (t, tl - dt);
        }
    }

    // Stage 0-3: 2-5 bounces; stage 4: LaserOrb mode
    public int ChargeStage
    {
        get
        {
            if (_chargeTime < MinChargeToFire) return 0;
            float frac = Math.Clamp(
                (_chargeTime - MinChargeToFire) / (MaxChargeTime - MinChargeToFire), 0f, 1f);
            return frac >= 1f ? 4 : (int)(frac * 4f);
        }
    }

    // Raised on fire: (projId, origin, velocity, weaponType, damage, maxBounces)
    public event Action<int, XnaVec, XnaVec, WeaponType, int, int>? OnFire;

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
        _debuffs.Clear();
        _buffs.Clear();
        ShieldDeployed = false;
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

    public void UpdateLocal(float dt, InputHandler input, XnaVec worldMousePos)
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

        if (kb.IsKeyDown(Keys.E) && !_prevKb.IsKeyDown(Keys.E))
        {
            if (ShieldDeployed)
                ShieldDeployed = false;
            else
            {
                ShieldDeployed = true;
                ShieldAngle    = BodyAngle + MathF.PI / 2f;
            }
        }
        _prevKb = kb;

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
        if (spd > PhysicsWorld.ToM(EffectiveMaxSpeedPx))
            PhysicsBody.SetLinearVelocity(vel / spd * PhysicsWorld.ToM(EffectiveMaxSpeedPx));

        var pos     = Position;
        TurretAngle = MathF.Atan2(worldMousePos.Y - pos.Y, worldMousePos.X - pos.X);

        // Left click: kinetic (instant, has cooldown)
        if (InputHelper.IsMouseClicked() && _fireCooldown <= 0f)
            FireKinetic();

        // Right click: hold to charge, release to fire laser
        if (InputHelper.IsMouseDown(1))
            _chargeTime += dt * GameConfig.Current.Laser.ChargeRate;

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
        if (spd > PhysicsWorld.ToM(EffectiveMaxSpeedPx))
            PhysicsBody.SetLinearVelocity(vel / spd * PhysicsWorld.ToM(EffectiveMaxSpeedPx));
        TurretAngle = input.AimAngle;

        if (input.ShieldActive && !ShieldDeployed)
        {
            ShieldDeployed = true;
            ShieldAngle    = input.ShieldAngle;
        }
        else if (!input.ShieldActive)
            ShieldDeployed = false;

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
            DirX         = dx, DirY = dy,
            AimAngle     = TurretAngle,
            RightHeld    = InputHelper.IsMouseDown(1),
            ChargeTime   = _chargeTime,
            ShieldActive = ShieldDeployed,
            ShieldAngle  = ShieldAngle
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
        if (!IsLocal)
        {
            ShieldDeployed = pkt.ShieldActive;
            ShieldAngle    = pkt.ShieldAngle;
        }
    }

    public HovercraftStatePacket BuildStatePacket() => new()
    {
        PlayerId     = PlayerId,
        X            = Position.X,
        Y            = Position.Y,
        VelX         = Velocity.X,
        VelY         = Velocity.Y,
        BodyAngle    = BodyAngle,
        TurretAngle  = TurretAngle,
        Shield       = (byte)Shield,
        ShieldActive = ShieldDeployed,
        ShieldAngle  = ShieldAngle
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
        OnFire?.Invoke(_nextProjId++, tip, dir * ProjectileSpd, WeaponType.Kinetic,
            (int)(KineticDamage * DamageMultiplier), 1);
    }

    private void FireLaser()
    {
        float frac = Math.Clamp(
            (_chargeTime - MinChargeToFire) / (MaxChargeTime - MinChargeToFire), 0f, 1f);
        int stage = frac >= 1f ? 4 : (int)(frac * 4f);

        if (stage == 4) { FireOrb(); return; }

        int maxBounces = stage + 2;  // stage 0=2, 1=3, 2=4, 3=5
        int damage     = (int)((MinLaserDamage + (int)(frac * (MaxLaserDamage - MinLaserDamage))) * DamageMultiplier);
        var pos = Position;
        var dir = new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle));
        var tip = pos + dir * (Radius + TurretLen);
        OnFire?.Invoke(_nextProjId++, tip, dir * LaserSpeed, WeaponType.Laser, damage, maxBounces);
    }

    private void FireOrb()
    {
        var pos = Position;
        var dir = new XnaVec(MathF.Cos(TurretAngle), MathF.Sin(TurretAngle));
        var tip = pos + dir * (Radius + TurretLen);
        OnFire?.Invoke(_nextProjId++, tip, dir * OrbSpeed, WeaponType.LaserOrb,
            (int)(MaxLaserDamage * DamageMultiplier), 1);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!IsAlive) return;

        var pos      = IsLocal ? Position    : RemotePosition;
        float bAngle = IsLocal ? BodyAngle   : RemoteBodyAngle;
        float tAngle = IsLocal ? TurretAngle : RemoteTurretAngle;
        int   r      = (int)Radius;

        // DamageBoost buff indicator — outer gold ring
        if (HasBuff(BuffType.DamageBoost))
        {
            const int dmgPad = 12;
            UIRenderer.DrawBorder(sb,
                new Rectangle((int)pos.X - r - dmgPad, (int)pos.Y - r - dmgPad,
                              (r + dmgPad) * 2, (r + dmgPad) * 2),
                Color.FromNonPremultiplied(255, 210, 60, 200), 2);
        }

        // EMP debuff indicator — outer blue ring
        if (HasDebuff(DebuffType.EmpSlow))
        {
            const int empPad = 8;
            UIRenderer.DrawBorder(sb,
                new Rectangle((int)pos.X - r - empPad, (int)pos.Y - r - empPad,
                              (r + empPad) * 2, (r + empPad) * 2),
                Color.FromNonPremultiplied(60, 210, 255, 200), 2);
        }

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

    }
}
