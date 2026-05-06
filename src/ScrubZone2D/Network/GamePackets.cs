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
    public const byte RoundOver           = 0x54;
}

// Sent unreliably ~20 Hz — position/velocity/angles for one hovercraft
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
    public uint   Tick         { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId); w.Write(X); w.Write(Y);
        w.Write(VelX); w.Write(VelY);
        w.Write(BodyAngle); w.Write(TurretAngle); w.Write(Tick);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId    = r.ReadByte();
        X           = r.ReadSingle(); Y = r.ReadSingle();
        VelX        = r.ReadSingle(); VelY = r.ReadSingle();
        BodyAngle   = r.ReadSingle(); TurretAngle = r.ReadSingle();
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

    public void Serialize(BinaryWriter w)
    {
        w.Write(OwnerPlayerId); w.Write(ProjectileId);
        w.Write(OriginX); w.Write(OriginY);
        w.Write(VelX); w.Write(VelY);
    }

    public void Deserialize(BinaryReader r)
    {
        OwnerPlayerId = r.ReadByte(); ProjectileId = r.ReadInt32();
        OriginX = r.ReadSingle(); OriginY = r.ReadSingle();
        VelX    = r.ReadSingle(); VelY    = r.ReadSingle();
    }
}

// Sent reliably — remove a projectile (bounce limit or out of bounds)
public sealed class ProjectileDestroyedPacket : IPacket
{
    public byte PacketTypeId => GamePacketIds.ProjectileDestroyed;
    public int  ProjectileId { get; set; }

    public void Serialize(BinaryWriter w)   => w.Write(ProjectileId);
    public void Deserialize(BinaryReader r) => ProjectileId = r.ReadInt32();
}

// Sent reliably — a player took damage
public sealed class PlayerDamagedPacket : IPacket
{
    public byte PacketTypeId  => GamePacketIds.PlayerDamaged;
    public byte TargetId      { get; set; }
    public byte AttackerId    { get; set; }
    public int  ProjectileId  { get; set; }
    public int  Damage        { get; set; }
    public int  RemainingHp   { get; set; }

    public void Serialize(BinaryWriter w)
    {
        w.Write(TargetId); w.Write(AttackerId);
        w.Write(ProjectileId); w.Write(Damage); w.Write(RemainingHp);
    }

    public void Deserialize(BinaryReader r)
    {
        TargetId    = r.ReadByte(); AttackerId   = r.ReadByte();
        ProjectileId = r.ReadInt32(); Damage     = r.ReadInt32();
        RemainingHp  = r.ReadInt32();
    }
}

// Sent reliably — round ended
public sealed class RoundOverPacket : IPacket
{
    public byte PacketTypeId => GamePacketIds.RoundOver;
    public byte WinnerPlayerId { get; set; }

    public void Serialize(BinaryWriter w)   => w.Write(WinnerPlayerId);
    public void Deserialize(BinaryReader r) => WinnerPlayerId = r.ReadByte();
}

public static class GamePacketRegistrar
{
    public static void RegisterAll()
    {
        PacketRegistry.Register(GamePacketIds.HovercraftState,     () => new HovercraftStatePacket());
        PacketRegistry.Register(GamePacketIds.FireProjectile,      () => new FireProjectilePacket());
        PacketRegistry.Register(GamePacketIds.ProjectileDestroyed, () => new ProjectileDestroyedPacket());
        PacketRegistry.Register(GamePacketIds.PlayerDamaged,       () => new PlayerDamagedPacket());
        PacketRegistry.Register(GamePacketIds.RoundOver,           () => new RoundOverPacket());
    }
}
