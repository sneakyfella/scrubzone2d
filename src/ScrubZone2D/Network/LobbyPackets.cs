using NetworkingLib.Core;
using NetworkingLib.Packets;

namespace ScrubZone2D.Network;

public static class LobbyPacketIds
{
    public const byte GameModeSet = 0x30;
    public const byte PlayerReady = 0x31;
    public const byte LobbySync   = 0x32;
    public const byte Countdown   = 0x33;
}

// Host -> joiner: game mode changed
public sealed class GameModeSetPacket : IPacket
{
    public byte PacketTypeId => LobbyPacketIds.GameModeSet;
    public byte Mode { get; set; }
    public void Serialize(BinaryWriter w)   => w.Write(Mode);
    public void Deserialize(BinaryReader r) => Mode = r.ReadByte();
}

// Joiner -> host: ready state toggled
public sealed class PlayerReadyPacket : IPacket
{
    public byte PacketTypeId => LobbyPacketIds.PlayerReady;
    public bool IsReady { get; set; }
    public void Serialize(BinaryWriter w)   => w.Write(IsReady);
    public void Deserialize(BinaryReader r) => IsReady = r.ReadBoolean();
}

// Host -> joiner on connect: full lobby snapshot
public sealed class LobbySyncPacket : IPacket
{
    public byte PacketTypeId => LobbyPacketIds.LobbySync;
    public byte Mode { get; set; }
    public void Serialize(BinaryWriter w)   => w.Write(Mode);
    public void Deserialize(BinaryReader r) => Mode = r.ReadByte();
}

// Host -> joiner: countdown has started (game starting soon)
public sealed class CountdownPacket : IPacket
{
    public byte PacketTypeId => LobbyPacketIds.Countdown;
    public void Serialize(BinaryWriter w)   { }
    public void Deserialize(BinaryReader r) { }
}

public static class LobbyPacketRegistrar
{
    public static void RegisterAll()
    {
        PacketRegistry.Register(LobbyPacketIds.GameModeSet, () => new GameModeSetPacket());
        PacketRegistry.Register(LobbyPacketIds.PlayerReady, () => new PlayerReadyPacket());
        PacketRegistry.Register(LobbyPacketIds.LobbySync,   () => new LobbySyncPacket());
        PacketRegistry.Register(LobbyPacketIds.Countdown,   () => new CountdownPacket());
    }
}
