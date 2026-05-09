namespace ScrubZone2D.Arena;

public sealed class WallDef
{
    public int Cx { get; set; }
    public int Cy { get; set; }
    public int W  { get; set; }
    public int H  { get; set; }
}

public sealed class MapData
{
    public string       Name         { get; set; } = "Untitled";
    public int          WorldWidth   { get; set; } = 1280;
    public int          WorldHeight  { get; set; } = 720;
    public float        SpawnHostX   { get; set; } = 100;
    public float        SpawnHostY   { get; set; } = 100;
    public float        SpawnJoinerX { get; set; } = 1180;
    public float        SpawnJoinerY { get; set; } = 620;
    // Background, wall, and floor RGB
    public byte BgR    { get; set; } = 18;  public byte BgG    { get; set; } = 18;  public byte BgB    { get; set; } = 30;
    public byte WallR  { get; set; } = 70;  public byte WallG  { get; set; } = 80;  public byte WallB  { get; set; } = 100;
    public byte FloorR { get; set; } = 22;  public byte FloorG { get; set; } = 22;  public byte FloorB { get; set; } = 38;
    public List<WallDef> Walls { get; set; } = new();

    public MapData Clone() => new()
    {
        Name = Name, WorldWidth = WorldWidth, WorldHeight = WorldHeight,
        SpawnHostX = SpawnHostX, SpawnHostY = SpawnHostY,
        SpawnJoinerX = SpawnJoinerX, SpawnJoinerY = SpawnJoinerY,
        BgR = BgR, BgG = BgG, BgB = BgB,
        WallR = WallR, WallG = WallG, WallB = WallB,
        FloorR = FloorR, FloorG = FloorG, FloorB = FloorB,
        Walls = Walls.Select(w => new WallDef { Cx = w.Cx, Cy = w.Cy, W = w.W, H = w.H }).ToList()
    };
}
