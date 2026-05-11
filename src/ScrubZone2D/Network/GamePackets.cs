using NetworkingLib.Core;
using NetworkingLib.Packets;

namespace ScrubZone2D.Network;

// Game-specific packet type IDs — user-defined range 0x50+
public static class GamePacketIds
{
    public const byte HovercraftState     = 0x50;
    public const byte FireProjectile      = 0x51;
    public const byte ProjectileDestroyed = 0x52;
    public const byte PlayerDamaged       = 0x53;
    public const byte ScoreUpdate         = 0x55;
    public const byte PlayerRespawn       = 0x56;
    public const byte JoinerInput         = 0x57;
    public const byte ResumeCountdown     = 0x58;
    public const byte Debuff              = 0x59;
}

// Sent unreliably ~20 Hz — position/velocity/angles + shield for one hovercraft
public sealed class HovercraftStatePacket : IPacket
{
    public byte   PacketTypeId => GamePacketIds.HovercraftState;
    public byte   PlayerId     { get; set; }
    public float  X            { get; set; }
    public float  Y            { get; set; }
    public float  VelX         { get; set; }
    public float  VelY         { get; set; }
    public float  BodyAngle    { get; set; }
    public float  TurretAngle  { get; set; }
    public byte   Shield       { get; set; }
    public uint   Tick         { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId); w.Write(X); w.Write(Y);
        w.Write(VelX); w.Write(VelY);
        w.Write(BodyAngle); w.Write(TurretAngle);
        w.Write(Shield); w.Write(Tick);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId    = r.ReadByte();
        X           = r.ReadSingle(); Y = r.ReadSingle();
        VelX        = r.ReadSingle(); VelY = r.ReadSingle();
        BodyAngle   = r.ReadSingle(); TurretAngle = r.ReadSingle();
        Shield      = r.ReadByte();
        Tick        = r.ReadUInt32();
    }
}

// Sent reliably — spawn a new projectile
public sealed class FireProjectilePacket : IPacket
{
    public byte   PacketTypeId  => GamePacketIds.FireProjectile;
    public byte   OwnerPlayerId { get; set; }
    public int    ProjectileId  { get; set; }
    public float  OriginX       { get; set; }
    public float  OriginY       { get; set; }
    public float  VelX          { get; set; }
    public float  VelY          { get; set; }
    public byte   WeaponType    { get; set; }
    public int    Damage        { get; set; }
    public byte   MaxBounces    { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(OwnerPlayerId); w.Write(ProjectileId);
        w.Write(OriginX); w.Write(OriginY);
        w.Write(VelX); w.Write(VelY);
        w.Write(WeaponType); w.Write(Damage); w.Write(MaxBounces);
    }

    public void Deserialize(BinaryReader r)
    {
        OwnerPlayerId = r.ReadByte(); ProjectileId = r.ReadInt32();
        OriginX = r.ReadSingle(); OriginY = r.ReadSingle();
        VelX    = r.ReadSingle(); VelY    = r.ReadSingle();
        WeaponType = r.ReadByte(); Damage = r.ReadInt32(); MaxBounces = r.ReadByte();
    }
}

// Sent reliably — remove a projectile (bounce limit or wall hit)
public sealed class ProjectileDestroyedPacket : IPacket
{
    public byte PacketTypeId => GamePacketIds.ProjectileDestroyed;
    public int  ProjectileId { get; set; }

    public void Serialize(BinaryWriter w)   => w.Write(ProjectileId);
    public void Deserialize(BinaryReader r) => ProjectileId = r.ReadInt32();
}

// Sent reliably — a player took damage; attacker is authoritative
public sealed class PlayerDamagedPacket : IPacket
{
    public byte PacketTypeId    => GamePacketIds.PlayerDamaged;
    public byte TargetId        { get; set; }
    public byte AttackerId      { get; set; }
    public int  ProjectileId    { get; set; }
    public int  RemainingShield { get; set; }
    public int  RemainingHull   { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(TargetId); w.Write(AttackerId);
        w.Write(ProjectileId); w.Write(RemainingShield); w.Write(RemainingHull);
    }

    public void Deserialize(BinaryReader r)
    {
        TargetId        = r.ReadByte(); AttackerId    = r.ReadByte();
        ProjectileId    = r.ReadInt32();
        RemainingShield = r.ReadInt32(); RemainingHull = r.ReadInt32();
    }
}

// Sent reliably — score updated after a kill
public sealed class ScoreUpdatePacket : IPacket
{
    public byte PacketTypeId => GamePacketIds.ScoreUpdate;
    public byte Score0       { get; set; }
    public byte Score1       { get; set; }

    public void Serialize(BinaryWriter w)   { w.Write(Score0); w.Write(Score1); }
    public void Deserialize(BinaryReader r) { Score0 = r.ReadByte(); Score1 = r.ReadByte(); }
}

// Sent reliably — a player respawned at position
public sealed class PlayerRespawnPacket : IPacket
{
    public byte  PacketTypeId => GamePacketIds.PlayerRespawn;
    public byte  PlayerId     { get; set; }
    public float X            { get; set; }
    public float Y            { get; set; }

    public void Serialize(BinaryWriter w)   { w.Write(PlayerId); w.Write(X); w.Write(Y); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadByte(); X = r.ReadSingle(); Y = r.ReadSingle(); }
}

// Sent unreliably every frame — joiner's movement input for host to drive joiner's physics
public sealed class JoinerInputPacket : IPacket
{
    public byte  PacketTypeId => GamePacketIds.JoinerInput;
    public float DirX       { get; set; }
    public float DirY       { get; set; }
    public float AimAngle   { get; set; }
    public bool  RightHeld  { get; set; }
    public float ChargeTime { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(DirX); w.Write(DirY); w.Write(AimAngle);
        w.Write(RightHeld); w.Write(ChargeTime);
    }

    public void Deserialize(BinaryReader r)
    {
        DirX = r.ReadSingle(); DirY = r.ReadSingle();
        AimAngle   = r.ReadSingle();
        RightHeld  = r.ReadBoolean(); ChargeTime = r.ReadSingle();
    }
}

// Sent reliably by host when a player receives a status debuff
public sealed class DebuffPacket : IPacket
{
    public byte  PacketTypeId => GamePacketIds.Debuff;
    public byte  PlayerId     { get; set; }
    public byte  EffectType   { get; set; }
    public float Duration     { get; set; }

    public void Serialize(BinaryWriter w)   { w.Write(PlayerId); w.Write(EffectType); w.Write(Duration); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadByte(); EffectType = r.ReadByte(); Duration = r.ReadSingle(); }
}

// Sent reliably by host once per second during graceful resume countdown
public sealed class ResumeCountdownPacket : IPacket
{
    public byte PacketTypeId => GamePacketIds.ResumeCountdown;
    public byte SecondsLeft  { get; set; }

    public void Serialize(BinaryWriter w)   => w.Write(SecondsLeft);
    public void Deserialize(BinaryReader r) => SecondsLeft = r.ReadByte();
}

public static class GamePacketRegistrar
{
    public static void RegisterAll()
    {
        PacketRegistry.Register(GamePacketIds.HovercraftState,     () => new HovercraftStatePacket());
        PacketRegistry.Register(GamePacketIds.FireProjectile,      () => new FireProjectilePacket());
        PacketRegistry.Register(GamePacketIds.ProjectileDestroyed, () => new ProjectileDestroyedPacket());
        PacketRegistry.Register(GamePacketIds.PlayerDamaged,       () => new PlayerDamagedPacket());
        PacketRegistry.Register(GamePacketIds.ScoreUpdate,         () => new ScoreUpdatePacket());
        PacketRegistry.Register(GamePacketIds.PlayerRespawn,       () => new PlayerRespawnPacket());
        PacketRegistry.Register(GamePacketIds.JoinerInput,         () => new JoinerInputPacket());
        PacketRegistry.Register(GamePacketIds.ResumeCountdown,     () => new ResumeCountdownPacket());
        PacketRegistry.Register(GamePacketIds.Debuff,              () => new DebuffPacket());
    }
}
