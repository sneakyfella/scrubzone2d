using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using NetworkingLib.Core;
using ScrubZone2D.Arena;
using ScrubZone2D.Config;
using ScrubZone2D.Entities;
using ScrubZone2D.Gameplay;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;

namespace ScrubZone2D.States;

public sealed class GameplayState : GameState
{
    private const float StateUpdateHz    = 20f;
    private const float PhysicsStep      = 1f / 60f;
    private const float PauseThreshold   = 1.5f;
    private const float StartupGrace     = 3f;
    private const float ResumeDuration   = 3f;

    private static float RespawnDelay      => GameConfig.Current.Gameplay.RespawnDelay;
    private static float ExplosionDuration => GameConfig.Current.Gameplay.ExplosionDuration;

    private enum Phase         { Playing, PeerSilent, Resuming }
    private enum ExplosionKind { Kinetic, Orb, Emp }

    private readonly GameStateManager _stateManager;

    private PhysicsWorld?     _physics;
    private ArenaMap?         _arena;
    private HovercraftEntity? _local;
    private HovercraftEntity? _remote;
    private ObjectiveManager? _objectives;

    private readonly Dictionary<(byte, int), ProjectileEntity>                                _projectiles = new();
    private readonly List<(Vector2 Position, float TimeLeft, float Radius, ExplosionKind Kind)> _explosions  = new();

    private float             _stateTimer;
    private float             _physicsAccum;
    private float             _localRespawnTimer = -1f;
    private bool              _gameOver;
    private string            _gameResult = "";
    private JoinerInputPacket _latestJoinerInput = new();

    private Phase _phase              = Phase.Playing;
    private float _gameTime;
    private float _peerSilenceTimer;
    private float _resumeTimer;
    private float _resumeSendTimer;

    private ArenaCamera _camera     = new(Game1.VirtualWidth, Game1.VirtualHeight);
    private Vector2  _localSpawn;   // used as camera target while respawning

    private static readonly Color HostColor   = new(255, 200, 80);
    private static readonly Color JoinerColor = new(80, 200, 255);

    public GameplayState(GameStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public override void Enter()
    {
        var net = NetworkManager.Instance;
        net.GamePacketReceived += OnNetworkPacket;
        net.Disconnected       += OnDisconnected;

        _arena      = ArenaMap.Create(net.SelectedMap);
        _physics    = new PhysicsWorld();
        _objectives = new ObjectiveManager(net.GameMode);
        _arena.CreatePhysicsBodies(_physics);
        _camera     = new ArenaCamera(_arena.WorldWidth, _arena.WorldHeight);

        bool isHost = net.Role == NetworkRole.Host;

        var localSpawn  = isHost ? _arena.SpawnHost   : _arena.SpawnJoiner;
        var remoteSpawn = isHost ? _arena.SpawnJoiner : _arena.SpawnHost;
        _localSpawn     = localSpawn;

        var localBody  = _physics.CreateHovercraft(localSpawn,  HovercraftEntity.Radius, "local");
        var remoteBody = _physics.CreateHovercraft(remoteSpawn, HovercraftEntity.Radius, "remote");

        _local = new HovercraftEntity(net.LocalPlayerId, isLocal: true,
            isHost ? HostColor : JoinerColor, localBody);
        _remote = new HovercraftEntity(net.RemotePlayerId, isLocal: false,
            isHost ? JoinerColor : HostColor, remoteBody);

        _local.OnFire += OnLocalFire;
    }

    public override void Exit()
    {
        var net = NetworkManager.Instance;
        net.GamePacketReceived -= OnNetworkPacket;
        net.Disconnected       -= OnDisconnected;
        _physics?.Dispose();
        base.Exit();
    }

    // ── Update ─────────────────────────────────────────────────────────────────

    public override void Update(float dt, StateServices svc)
    {
        if (_gameOver)
        {
            if (InputHelper.IsKeyPressed(Keys.Escape))
                _stateManager.Pop();
            return;
        }

        _gameTime         += dt;
        _peerSilenceTimer += dt;

        // Camera follows local player; shows spawn when dead
        _camera.CenterOn(_local!.IsAlive ? _local.Position : _localSpawn);

        bool isHost    = NetworkManager.Instance.Role == NetworkRole.Host;
        bool peerQuiet = _gameTime > StartupGrace && _peerSilenceTimer > PauseThreshold;

        switch (_phase)
        {
            case Phase.Playing:
                if (peerQuiet) EnterPeerSilent();
                else           TickPlaying(dt, isHost, svc);
                break;

            case Phase.PeerSilent:
                if (!peerQuiet)
                {
                    // A packet just arrived — peer is back. Host drives the resume countdown.
                    if (isHost) EnterResuming();
                    // Joiner waits for host's ResumeCountdownPacket (handled in OnNetworkPacket).
                }
                else
                {
                    // Keep sending heartbeat so the other side can detect our recovery.
                    if (!isHost && _local!.IsAlive)
                        NetworkManager.Instance.SendInputUnreliable(
                            _local.BuildJoinerInputPacket(svc.Input));
                }
                break;

            case Phase.Resuming:
                if (peerQuiet) EnterPeerSilent();
                else           TickResuming(dt, isHost, svc);
                break;
        }

        if (_objectives!.IsGameOver())
            EndGame();
    }

    private void TickPlaying(float dt, bool isHost, StateServices svc)
    {
        _local!.UpdateLocal(dt, svc.Input, _camera.ScreenToWorld(svc.Input.VirtualPositionF));

        if (isHost)
            _remote!.DriveFromInput(_latestJoinerInput, dt);

        // Snapshot each projectile's direction before Box2D runs so ProcessContactEvents
        // can record the pre-bounce incoming direction on first wall contact.
        foreach (var proj in _projectiles.Values)
        {
            if (proj.IsDestroyed) continue;
            var v = proj.Velocity;
            float spd = v.Length();
            proj.PhysicsBody.Data.LastVelDir = spd > 0.1f ? v / spd : Vector2.Zero;
        }

        _physicsAccum += dt;
        while (_physicsAccum >= PhysicsStep)
        {
            _physics!.Step(PhysicsStep);
            if (isHost) ProcessCollisions();
            _physicsAccum -= PhysicsStep;
        }

        foreach (var proj in _projectiles.Values)
            proj.UpdateEffects(dt);

        _local!.UpdateDebuffs(dt);
        _remote!.UpdateDebuffs(dt);

        CleanupProjectiles(isHost);

        for (int i = _explosions.Count - 1; i >= 0; i--)
        {
            var (pos, tl, radius, kind) = _explosions[i];
            if (tl - dt <= 0f) _explosions.RemoveAt(i);
            else _explosions[i] = (pos, tl - dt, radius, kind);
        }

        if (_localRespawnTimer >= 0f)
        {
            _localRespawnTimer -= dt;
            if (_localRespawnTimer < 0f)
            {
                var spawnPos = _localSpawn;
                _local.Respawn(spawnPos);
                NetworkManager.Instance.SendReliable(new PlayerRespawnPacket
                {
                    PlayerId = NetworkManager.Instance.LocalPlayerId,
                    X = spawnPos.X, Y = spawnPos.Y
                });
            }
        }

        _stateTimer += dt;
        if (_stateTimer >= 1f / StateUpdateHz)
        {
            _stateTimer = 0f;
            if (isHost)
            {
                if (_local.IsAlive)   NetworkManager.Instance.SendStateUnreliable(_local.BuildStatePacket());
                if (_remote!.IsAlive) NetworkManager.Instance.SendStateUnreliable(_remote.BuildStatePacket());
            }
            else
            {
                NetworkManager.Instance.SendInputUnreliable(_local.BuildJoinerInputPacket(svc.Input));
                if (_local.IsAlive)   NetworkManager.Instance.SendStateUnreliable(_local.BuildStatePacket());
            }
        }
    }

    private void TickResuming(float dt, bool isHost, StateServices svc)
    {
        _resumeTimer -= dt;

        if (isHost)
        {
            // Re-sync both players' positions at 20 Hz during countdown.
            _stateTimer += dt;
            if (_stateTimer >= 1f / StateUpdateHz)
            {
                _stateTimer = 0f;
                if (_local!.IsAlive)  NetworkManager.Instance.SendStateUnreliable(_local.BuildStatePacket());
                if (_remote!.IsAlive) NetworkManager.Instance.SendStateUnreliable(_remote.BuildStatePacket());
            }

            // Broadcast countdown to joiner once per second.
            _resumeSendTimer -= dt;
            if (_resumeSendTimer <= 0f)
            {
                byte secs = (byte)Math.Max(0, (int)Math.Ceiling(_resumeTimer));
                NetworkManager.Instance.SendReliable(new ResumeCountdownPacket { SecondsLeft = secs });
                _resumeSendTimer = 1f;
            }
        }
        else
        {
            // Joiner sends heartbeat so host stays aware it's still connected.
            _stateTimer += dt;
            if (_stateTimer >= 1f / StateUpdateHz)
            {
                _stateTimer = 0f;
                NetworkManager.Instance.SendInputUnreliable(_local!.BuildJoinerInputPacket(svc.Input));
            }
        }

        if (_resumeTimer <= 0f)
            _phase = Phase.Playing;
    }

    // ── Phase transitions ──────────────────────────────────────────────────────

    private void EnterPeerSilent()
    {
        _phase = Phase.PeerSilent;
        ClearAllProjectiles();
    }

    private void EnterResuming()
    {
        _phase           = Phase.Resuming;
        _resumeTimer     = ResumeDuration;
        _resumeSendTimer = 0f;   // send first countdown packet immediately next tick
        _stateTimer      = 0f;

        // Re-sync score so joiner has the correct values before play resumes.
        NetworkManager.Instance.SendReliable(new ScoreUpdatePacket
        {
            Score0 = (byte)_objectives!.Score0,
            Score1 = (byte)_objectives.Score1
        });
    }

    // ── Collision handling ─────────────────────────────────────────────────────

    private void ProcessCollisions()
    {
        float hitDist = HovercraftEntity.Radius + 6f;

        foreach (var proj in _projectiles.Values.ToList())
        {
            if (proj.IsDestroyed) continue;

            // Kinetic projectile detonates a LaserOrb on proximity → kinetic + EMP explosions
            if (proj.Type == WeaponType.Kinetic)
            {
                foreach (var orb in _projectiles.Values)
                {
                    if (orb.IsDestroyed || orb.Type != WeaponType.LaserOrb) continue;
                    if (Vector2.Distance(proj.Position, orb.Position) > HovercraftEntity.OrbTriggerRadius) continue;

                    var orbPos        = orb.Position;
                    byte kineticOwner = proj.OwnerPlayerId;
                    proj.SuppressExplosion = true;
                    proj.Destroy();
                    orb.Destroy();

                    // EMP explosion only — no kinetic impact when manually detonating
                    _explosions.Add((orbPos, ExplosionDuration, HovercraftEntity.OrbBlastRadius, ExplosionKind.Emp));
                    ApplyAreaDamage(orbPos, HovercraftEntity.OrbBlastRadius, HovercraftEntity.OrbEmpDamage, isEmp: true, kineticOwner);

                    NetworkManager.Instance.SendReliable(new ProjectileDestroyedPacket { ProjectileId = proj.Id });
                    NetworkManager.Instance.SendReliable(new ProjectileDestroyedPacket { ProjectileId = orb.Id });
                    break;
                }
            }

            if (proj.IsDestroyed) continue;

            var opponent = proj.OwnerPlayerId == _local!.PlayerId ? _remote! : _local!;
            ApplyDirectHit(proj, opponent, hitDist);

            if (!proj.IsDestroyed && GameConfig.Current.Gameplay.AllowSelfDamage)
            {
                var owner = proj.OwnerPlayerId == _local.PlayerId ? _local! : _remote!;
                ApplyDirectHit(proj, owner, hitDist);
            }
        }
    }

    private bool ApplyDirectHit(ProjectileEntity proj, HovercraftEntity target, float hitDist)
    {
        if (!target.IsAlive) return false;
        if (Vector2.Distance(proj.Position, target.Position) > hitDist) return false;

        int remainingShield, remainingHull;
        if (proj.Type == WeaponType.Kinetic)
        {
            remainingShield = Math.Max(0, target.Shield - proj.Damage / 10);
            remainingHull   = Math.Max(0, target.Health - proj.Damage);
        }
        else
        {
            int shieldDmg   = Math.Min(target.Shield, proj.Damage);
            int overflow    = proj.Damage - shieldDmg;
            remainingShield = target.Shield - shieldDmg;
            remainingHull   = Math.Max(0, target.Health - overflow / 10);
        }

        target.TakeDamage(remainingShield, remainingHull);

        if (proj.Type == WeaponType.LaserOrb)
        {
            var orbPos = proj.Position;
            _explosions.Add((orbPos, ExplosionDuration, HovercraftEntity.OrbBlastRadius, ExplosionKind.Orb));
            ApplyBlastImpulse(orbPos, HovercraftEntity.OrbBlastRadius, HovercraftEntity.OrbBlastImpulsePx);
        }

        proj.Destroy();

        NetworkManager.Instance.SendReliable(new PlayerDamagedPacket
        {
            TargetId        = target.PlayerId,
            AttackerId      = proj.OwnerPlayerId,
            ProjectileId    = proj.Id,
            RemainingShield = remainingShield,
            RemainingHull   = remainingHull
        });
        NetworkManager.Instance.SendReliable(
            new ProjectileDestroyedPacket { ProjectileId = proj.Id });

        if (remainingHull <= 0)
        {
            target.Die();
            bool killerIsHost = proj.OwnerPlayerId == _local!.PlayerId;
            NetworkManager.Instance.SendReliable(
                _objectives!.RecordKill(killerIsPlayer0: killerIsHost));
            if (target == _local)
                _localRespawnTimer = RespawnDelay;
        }

        return true;
    }

    // ── Projectile helpers ─────────────────────────────────────────────────────

    private void CleanupProjectiles(bool isHost)
    {
        foreach (var proj in _projectiles.Values
            .Where(p => p.IsDestroyed || p.ShouldDestroy()).ToList())
        {
            if (proj.Type == WeaponType.Kinetic && !proj.SuppressExplosion)
            {
                var ep = proj.Position;
                _explosions.Add((ep, ExplosionDuration, HovercraftEntity.KineticBlastRadius, ExplosionKind.Kinetic));
                if (isHost) ApplyBlastImpulse(ep, HovercraftEntity.KineticBlastRadius, HovercraftEntity.KineticBlastImpulsePx);
            }
            else if (proj.Type == WeaponType.LaserOrb && !proj.IsDestroyed)
            {
                // Wall hit — explosion not yet triggered by ProcessCollisions
                var ep = proj.Position;
                _explosions.Add((ep, ExplosionDuration, HovercraftEntity.OrbBlastRadius, ExplosionKind.Orb));
                if (isHost) ApplyBlastImpulse(ep, HovercraftEntity.OrbBlastRadius, HovercraftEntity.OrbBlastImpulsePx);
            }

            if (isHost && proj.IsLocal && !proj.IsDestroyed)
                NetworkManager.Instance.SendReliable(
                    new ProjectileDestroyedPacket { ProjectileId = proj.Id });

            _physics!.DestroyBody(proj.PhysicsBody);
            _projectiles.Remove((proj.OwnerPlayerId, proj.Id));
        }
    }

    private void ApplyBlastImpulse(Vector2 pos, float radius, float impulsePx)
    {
        PushPlayerFromExplosion(_local!,  pos, radius, impulsePx);
        PushPlayerFromExplosion(_remote!, pos, radius, impulsePx);
    }

    private static void PushPlayerFromExplosion(HovercraftEntity player, Vector2 pos, float radius, float impulsePx)
    {
        if (!player.IsAlive) return;
        var delta = player.Position - pos;
        float dist = delta.Length();
        if (dist < 1f || dist > radius) return;
        var dir    = delta / dist;
        var curVel = player.PhysicsBody.GetLinearVelocity();
        player.PhysicsBody.SetLinearVelocity(
            curVel + PhysicsWorld.ToB2(dir * impulsePx));
    }

    private void ApplyAreaDamage(Vector2 pos, float radius, int damage, bool isEmp, byte killerPlayerId)
    {
        ApplyAreaDamageToPlayer(_local!,  pos, radius, damage, isEmp, killerPlayerId);
        ApplyAreaDamageToPlayer(_remote!, pos, radius, damage, isEmp, killerPlayerId);
    }

    private void ApplyAreaDamageToPlayer(HovercraftEntity player, Vector2 pos,
        float radius, int damage, bool isEmp, byte killerPlayerId)
    {
        if (!GameConfig.Current.Gameplay.AllowSelfDamage && player.PlayerId == killerPlayerId) return;
        if (!player.IsAlive) return;
        float dist = Vector2.Distance(player.Position, pos);
        if (dist > radius) return;

        float falloff  = 1f - (dist / radius);
        int   scaled   = Math.Max(1, (int)(damage * falloff));

        int remainingShield, remainingHull;
        if (isEmp)
        {
            // EMP: strips shields, negligible hull
            int shieldDmg   = Math.Min(player.Shield, scaled);
            remainingShield = player.Shield - shieldDmg;
            remainingHull   = player.Health;
        }
        else
        {
            // Kinetic area: less shield, more hull
            remainingShield = Math.Max(0, player.Shield - scaled / 8);
            remainingHull   = Math.Max(0, player.Health - scaled);
        }

        if (remainingShield == player.Shield && remainingHull == player.Health && !isEmp) return;

        player.TakeDamage(remainingShield, remainingHull);
        if (remainingShield != player.Shield || remainingHull != player.Health)
            NetworkManager.Instance.SendReliable(new PlayerDamagedPacket
            {
                TargetId        = player.PlayerId,
                AttackerId      = killerPlayerId,
                ProjectileId    = -1,
                RemainingShield = remainingShield,
                RemainingHull   = remainingHull
            });

        if (isEmp)
        {
            float dur = GameConfig.Current.Ship.EmpDebuffDuration;
            player.ApplyDebuff(DebuffType.EmpSlow, dur);
            NetworkManager.Instance.SendReliable(new DebuffPacket
            {
                PlayerId   = player.PlayerId,
                EffectType = (byte)DebuffType.EmpSlow,
                Duration   = dur
            });
        }

        if (remainingHull > 0) return;

        player.Die();
        bool killerIsHost = killerPlayerId == _local!.PlayerId;
        NetworkManager.Instance.SendReliable(_objectives!.RecordKill(killerIsPlayer0: killerIsHost));
        if (player == _local)
            _localRespawnTimer = RespawnDelay;
    }

    private void ClearAllProjectiles()
    {
        foreach (var proj in _projectiles.Values)
            _physics!.DestroyBody(proj.PhysicsBody);
        _projectiles.Clear();
    }

    // ── Firing ─────────────────────────────────────────────────────────────────

    private void OnLocalFire(int id, Vector2 origin, Vector2 vel, WeaponType weaponType, int damage, int maxBounces)
    {
        SpawnProjectile(id, NetworkManager.Instance.LocalPlayerId, true, origin, vel, weaponType, damage, maxBounces);
        NetworkManager.Instance.SendReliable(new FireProjectilePacket
        {
            OwnerPlayerId = NetworkManager.Instance.LocalPlayerId,
            ProjectileId  = id,
            OriginX = origin.X, OriginY = origin.Y,
            VelX    = vel.X,    VelY    = vel.Y,
            WeaponType = (byte)weaponType,
            Damage     = damage,
            MaxBounces = (byte)maxBounces
        });
    }

    private void SpawnProjectile(int id, byte owner, bool isLocal, Vector2 origin, Vector2 vel,
        WeaponType weaponType, int damage, int maxBounces)
    {
        if (_projectiles.ContainsKey((owner, id))) return;
        var body = _physics!.CreateProjectile(origin, vel, null!);
        var proj = new ProjectileEntity(id, owner, isLocal, body, weaponType, damage, maxBounces);
        body.Data.Owner = proj;
        _projectiles[(owner, id)] = proj;
    }

    // ── Network packets ────────────────────────────────────────────────────────

    private void OnNetworkPacket(IPacket packet)
    {
        _peerSilenceTimer = 0f;

        switch (packet)
        {
            case ResumeCountdownPacket countdown:
                // Joiner enters/stays in Resuming driven by host's countdown.
                if (_phase != Phase.Playing || countdown.SecondsLeft > 0)
                {
                    if (_phase == Phase.PeerSilent || _phase == Phase.Playing)
                    {
                        ClearAllProjectiles();
                        _stateTimer  = 0f;
                        _phase       = Phase.Resuming;
                    }
                    _resumeTimer = countdown.SecondsLeft;
                }
                if (countdown.SecondsLeft == 0)
                    _phase = Phase.Playing;
                break;

            case HovercraftStatePacket state:
                if (state.PlayerId == _local?.PlayerId)
                    _local?.ApplyRemoteState(state);
                else
                    _remote?.ApplyRemoteState(state);
                break;

            case JoinerInputPacket input:
                _latestJoinerInput = input;
                break;

            case FireProjectilePacket fire:
                SpawnProjectile(fire.ProjectileId, fire.OwnerPlayerId, false,
                    new(fire.OriginX, fire.OriginY), new(fire.VelX, fire.VelY),
                    (WeaponType)fire.WeaponType, fire.Damage, fire.MaxBounces);
                break;

            case ProjectileDestroyedPacket destroy:
                var remoteId = NetworkManager.Instance.RemotePlayerId;
                var localId  = NetworkManager.Instance.LocalPlayerId;
                if (_projectiles.TryGetValue((remoteId, destroy.ProjectileId), out var p1))
                    p1.Destroy();
                else if (_projectiles.TryGetValue((localId, destroy.ProjectileId), out var p2))
                    p2.Destroy();
                break;

            case PlayerDamagedPacket dmg when dmg.TargetId == _local?.PlayerId:
                _local.TakeDamage(dmg.RemainingShield, dmg.RemainingHull);
                if (_local.Health <= 0)
                {
                    _local.Die();
                    _localRespawnTimer = RespawnDelay;
                }
                break;

            case PlayerRespawnPacket respawn when respawn.PlayerId == _remote?.PlayerId:
                _remote.Respawn(new Vector2(respawn.X, respawn.Y));
                break;

            case PlayerRespawnPacket respawn when respawn.PlayerId == _local?.PlayerId:
                _local.Respawn(new Vector2(respawn.X, respawn.Y));
                break;

            case DebuffPacket debuff:
            {
                var debuffTarget = debuff.PlayerId == _local?.PlayerId ? _local : _remote;
                debuffTarget?.ApplyDebuff((DebuffType)debuff.EffectType, debuff.Duration);
                break;
            }

            case ScoreUpdatePacket score:
                _objectives?.ApplyScore(score);
                break;
        }
    }

    private void OnDisconnected()
    {
        _gameResult = "Opponent disconnected";
        _gameOver   = true;
    }

    private void EndGame()
    {
        if (_gameOver) return;
        _gameOver   = true;
        bool isHost = NetworkManager.Instance.Role == NetworkRole.Host;
        _gameResult = _objectives!.GetResultText(isHost);
    }

    // ── Draw ───────────────────────────────────────────────────────────────────

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        // World — rendered through the camera transform
        sb.Begin(transformMatrix: _camera.Transform);
        _arena!.Draw(sb);
        _local!.Draw(sb);
        _remote!.Draw(sb);
        foreach (var p in _projectiles.Values)
            p.Draw(sb);
        DrawExplosions(sb);
        sb.End();

        // HUD — screen-space, no camera transform
        sb.Begin();
        DrawHud(sb);
        sb.End();
    }

    private void DrawHud(SpriteBatch sb)
    {
        var net     = NetworkManager.Instance;
        bool isHost = net.Role == NetworkRole.Host;

        // Score panel (top center)
        string scoreText = $"{_objectives?.Score0 ?? 0}  -  {_objectives?.Score1 ?? 0}";
        UIRenderer.TextCentered(sb, scoreText, new Rectangle(490, 8, 300, 30), Color.White);
        int    limit    = net.GameMode == GameMode.FFA ? 10 : 15;
        string modeInfo = net.GameMode == GameMode.FFA ? $"FFA | first to {limit}" : $"TEAMS | first to {limit}";
        UIRenderer.TextCentered(sb, modeInfo, new Rectangle(490, 34, 300, 20), Color.Gray, small: true);

        // Local player (bottom left)
        UIRenderer.Text(sb, isHost ? "You (Host)" : "You (Guest)",
            new Vector2(20, 636), isHost ? HostColor : JoinerColor, small: true);
        UIRenderer.ProgressBar(sb, new Rectangle(20, 650, 200, 8),
            _local!.Shield, HovercraftEntity.MaxShield, new Color(60, 180, 255), new Color(15, 35, 60));
        UIRenderer.ProgressBar(sb, new Rectangle(20, 661, 200, 14),
            _local.Health, 100, Color.LimeGreen, new Color(60, 20, 20));
        if (_local.HasDebuff(DebuffType.EmpSlow))
            UIRenderer.Text(sb, "EMP", new Vector2(226, 661), new Color(60, 210, 255), small: true);

        // Remote player (bottom right)
        UIRenderer.Text(sb, isHost ? "Opponent" : "Host",
            new Vector2(1060, 636), isHost ? JoinerColor : HostColor, small: true);
        UIRenderer.ProgressBar(sb, new Rectangle(1060, 650, 200, 8),
            _remote!.Shield, HovercraftEntity.MaxShield, new Color(60, 180, 255), new Color(15, 35, 60));
        UIRenderer.ProgressBar(sb, new Rectangle(1060, 661, 200, 14),
            _remote.Health, 100, Color.OrangeRed, new Color(60, 20, 20));

        // Laser charge bar — bottom center HUD, shown while charging
        if (_local!.IsAlive && _local.ChargeTime >= HovercraftEntity.MinChargeToFire * 0.3f)
        {
            const int bx = 490, by = 598, bw = 300, bh = 20;
            float frac  = Math.Clamp(_local.ChargeTime / HovercraftEntity.MaxChargeTime, 0f, 1f);
            int   stage = _local.ChargeStage;

            UIRenderer.FillRect(sb, new Rectangle(bx, by, bw, bh), new Color(15, 20, 15));
            var fillCol = stage < 4
                ? Color.Lerp(new Color(60, 220, 80), new Color(200, 60, 255), frac)
                : new Color(200, 60, 255);
            UIRenderer.FillRect(sb, new Rectangle(bx, by, (int)(bw * frac), bh), fillCol);
            UIRenderer.DrawBorder(sb, new Rectangle(bx, by, bw, bh), new Color(80, 100, 60), 1);

            // Stage dividers at 25 / 50 / 75 % of bar
            for (int i = 1; i <= 3; i++)
                UIRenderer.FillRect(sb, new Rectangle(bx + bw * i / 4 - 1, by, 2, bh),
                    new Color(210, 210, 160, 170));

            // Stage labels (above dividers + base + orb)
            string[] stageLabels = ["2", "3", "4", "5", "ORB"];
            int[]    labelOffsets = [2, bw / 4 - 4, bw / 2 - 4, 3 * bw / 4 - 4, bw - 20];
            for (int i = 0; i < 5; i++)
            {
                var lc = i <= stage ? Color.White : new Color(100, 100, 80);
                UIRenderer.Text(sb, stageLabels[i], new Vector2(bx + labelOffsets[i], by - 13), lc, small: true);
            }

            UIRenderer.Text(sb, "LASER CHARGE", new Vector2(bx, by - 25), new Color(100, 210, 100), small: true);
        }

        // Controls hint (top left, small)
        UIRenderer.Text(sb, "WASD move | LClick kinetic | Hold RClick laser | ESC quit",
            new Vector2(20, 10), Color.DimGray, small: true);

        // Respawn countdown
        if (_local.IsAlive == false && _localRespawnTimer >= 0f)
        {
            int secs = (int)MathF.Ceiling(_localRespawnTimer);
            UIRenderer.TextCentered(sb, $"Respawning in {secs}...",
                new Rectangle(0, 300, 1280, 50), new Color(220, 120, 80));
        }

        // Peer-silent overlay
        if (_phase == Phase.PeerSilent)
        {
            UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(0, 0, 0, 150));
            string msg = isHost ? "Waiting for player..." : "Waiting for host...";
            UIRenderer.TextCentered(sb, msg, new Rectangle(0, 300, 1280, 60), Color.Yellow);
            UIRenderer.TextCentered(sb, "Game paused",
                new Rectangle(0, 370, 1280, 30), Color.Gray, small: true);
        }

        // Graceful resume countdown overlay
        if (_phase == Phase.Resuming)
        {
            UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(0, 0, 0, 120));
            int secs = (int)Math.Ceiling(Math.Max(0.0, _resumeTimer));
            string countMsg = secs > 0 ? $"Resuming in {secs}..." : "Go!";
            UIRenderer.TextCentered(sb, countMsg, new Rectangle(0, 290, 1280, 70), Color.LimeGreen);
            UIRenderer.TextCentered(sb, "Synchronising game state",
                new Rectangle(0, 370, 1280, 30), Color.Gray, small: true);
        }

        // Game over overlay (drawn last — always on top)
        if (_gameOver)
        {
            UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(0, 0, 0, 160));
            UIRenderer.TextCentered(sb, _gameResult, new Rectangle(0, 270, 1280, 80), Color.White);
            UIRenderer.TextCentered(sb, "Press ESC to return to menu",
                new Rectangle(0, 400, 1280, 40), Color.Gray, small: true);
        }
    }

    private void DrawExplosions(SpriteBatch sb)
    {
        foreach (var (pos, tl, radius, kind) in _explosions)
        {
            float t = tl / ExplosionDuration;  // 1 → 0

            Color outerCol, innerCol;
            switch (kind)
            {
                case ExplosionKind.Emp:
                    outerCol = Color.FromNonPremultiplied(60, 210, 255, (int)(t * 220));
                    innerCol = Color.FromNonPremultiplied(200, 245, 255, (int)(t * t * 190));
                    break;
                case ExplosionKind.Orb:
                    outerCol = Color.FromNonPremultiplied(180, 80, 255,  (int)(t * 210));
                    innerCol = Color.FromNonPremultiplied(230, 180, 255, (int)(t * t * 170));
                    break;
                default: // Kinetic
                    outerCol = Color.FromNonPremultiplied(255, 140, 40,  (int)(t * 210));
                    innerCol = Color.FromNonPremultiplied(255, 220, 120, (int)(t * t * 170));
                    break;
            }

            DrawCircle(sb, pos, radius, outerCol, 32);
            float innerR = radius * (1f - t);
            if (innerR > 1f)
                DrawCircle(sb, pos, innerR, innerCol, 24);
        }
    }

    private static void DrawCircle(SpriteBatch sb, Vector2 center, float radius, Color color, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            float a1 = i       / (float)segments * MathF.Tau;
            float a2 = (i + 1) / (float)segments * MathF.Tau;
            var p1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            var p2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * radius;
            DrawSegment(sb, p1, p2, color, 2);
        }
    }

    private static void DrawSegment(SpriteBatch sb, Vector2 from, Vector2 to, Color color, int thickness)
    {
        if (MGT.Pixel == null) return;
        var   d   = to - from;
        float len = d.Length();
        if (len < 0.5f) return;
        sb.Draw(MGT.Pixel, from, null, color,
            MathF.Atan2(d.Y, d.X), Vector2.Zero,
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }
}
