using System.Text.Json;

namespace ScrubZone2D.Config;

public sealed class ShipConfig
{
    public float MaxSpeedPx        { get; set; } = 120f;
    public float ThrustForce       { get; set; } = 480f;
    public float LinearDamping     { get; set; } = 4f;
    public float AngularDamping    { get; set; } = 8f;
    public float Radius            { get; set; } = 18f;
    public float RotateSpeed       { get; set; } = 3.2f;
    public float TurretLen         { get; set; } = 30f;
    public int   MaxShield         { get; set; } = 75;
    public float ShieldRegenRate   { get; set; } = 20f;
    public float ShieldRegenDelay  { get; set; } = 3f;
    public float EmpSlowFactor       { get; set; } = 0.2f;
    public float EmpDebuffDuration   { get; set; } = 4f;
    public float DamageBoostFactor   { get; set; } = 0.15f;
    public float DamageBoostDuration { get; set; } = 5f;
    public float ShieldLength        { get; set; } = 80f;
    public float ShieldThicknessPx   { get; set; } = 6f;
}

public sealed class KineticConfig
{
    public int   Damage         { get; set; } = 25;
    public float SpeedFactor    { get; set; } = 0.85f;
    public float FireCooldown   { get; set; } = 0.45f;
    public float BlastRadius    { get; set; } = 100f;
    public float BlastImpulsePx { get; set; } = 800f;
}

public sealed class LaserConfig
{
    public float Speed         { get; set; } = 2800f;
    public int   MinDamage     { get; set; } = 15;
    public int   MaxDamage     { get; set; } = 55;
    public float MinChargeTime { get; set; } = 0.12f;
    public float MaxChargeTime { get; set; } = 0.6f;
    public float ChargeRate    { get; set; } = 0.7f;
}

public sealed class LaserOrbConfig
{
    public float Speed          { get; set; } = 700f;
    public float BlastRadius    { get; set; } = 110f;
    public float BlastImpulsePx { get; set; } = 1400f;
    public float TriggerRadius  { get; set; } = 40f;
    public int   EmpDamage      { get; set; } = 50;
}

public sealed class GameplayConfig
{
    public float RespawnDelay      { get; set; } = 5f;
    public float ExplosionDuration { get; set; } = 0.45f;
    public bool  AllowSelfDamage   { get; set; } = true;
}

public sealed class GameConfig
{
    private static readonly JsonSerializerOptions _readOpts  = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    public static GameConfig Current { get; private set; } = new();

    public ShipConfig     Ship     { get; set; } = new();
    public KineticConfig  Kinetic  { get; set; } = new();
    public LaserConfig    Laser    { get; set; } = new();
    public LaserOrbConfig LaserOrb { get; set; } = new();
    public GameplayConfig Gameplay { get; set; } = new();

    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            Save(path);
            return;
        }
        try
        {
            var cfg = JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(path), _readOpts);
            if (cfg != null) Current = cfg;
        }
        catch { /* keep defaults on malformed file */ }
    }

    public static void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(Current, _writeOpts)); }
        catch { }
    }
}
