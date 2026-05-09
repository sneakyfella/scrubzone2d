using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using NetworkingLib.Core;
using ScrubZone2D.Arena;
using ScrubZone2D.Entities;
using ScrubZone2D.Gameplay;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;

namespace ScrubZone2D.States;

public sealed class GameplayState : GameState
{
    private const float StateUpdateHz  = 20f;
    private const float RespawnDelay   = 5f;
    private const float PhysicsStep    = 1f / 60f;
    private const float PauseThreshold = 1.5f;   // silence before freezing
    private const float StartupGrace   = 3f;      // initial window before silence detection
    private const float ResumeDuration = 3f;      // countdown length

    private enum Phase { Playing, PeerSilent, Resuming }

    private readonly GameStateManager _stateManager;

    private PhysicsWorld?     _physics;
    private ArenaMap?         _arena;
    private HovercraftEntity? _local;
    private HovercraftEntity? _remote;
    private ObjectiveManager? _objectives;

    private readonly Dictionary<(byte, int), ProjectileEntity> _projectiles = new();

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
        _local!.UpdateLocal(dt, svc.Input);

        if (isHost)
            _remote!.DriveFromInput(_latestJoinerInput, dt);

        _physicsAccum += dt;
        while (_physicsAccum >= PhysicsStep)
        {
            _physics!.Step(PhysicsStep);
            if (isHost) ProcessCollisions();
            _physicsAccum -= PhysicsStep;
        }

        CleanupProjectiles(isHost);

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
        const float hitDist = HovercraftEntity.Radius + 6f;

        foreach (var proj in _projectiles.Values.ToList())
        {
            if (proj.IsDestroyed) continue;

            var target = proj.OwnerPlayerId == _local!.PlayerId ? _remote! : _local!;
            if (!target.IsAlive) continue;
            if (Vector2.Distance(proj.Position, target.Position) > hitDist) continue;

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
                bool killerIsHost = proj.OwnerPlayerId == _local.PlayerId;
                NetworkManager.Instance.SendReliable(
                    _objectives!.RecordKill(killerIsPlayer0: killerIsHost));
                if (target == _local)
                    _localRespawnTimer = RespawnDelay;
            }
        }
    }

    // ── Projectile helpers ─────────────────────────────────────────────────────

    private void CleanupProjectiles(bool isHost)
    {
        foreach (var proj in _projectiles.Values
            .Where(p => p.IsDestroyed || p.ShouldDestroy()).ToList())
        {
            if (isHost && proj.IsLocal && !proj.IsDestroyed)
                NetworkManager.Instance.SendReliable(
                    new ProjectileDestroyedPacket { ProjectileId = proj.Id });

            _physics!.DestroyBody(proj.PhysicsBody);
            _projectiles.Remove((proj.OwnerPlayerId, proj.Id));
        }
    }

    private void ClearAllProjectiles()
    {
        foreach (var proj in _projectiles.Values)
            _physics!.DestroyBody(proj.PhysicsBody);
        _projectiles.Clear();
    }

    // ── Firing ─────────────────────────────────────────────────────────────────

    private void OnLocalFire(int id, Vector2 origin, Vector2 vel, WeaponType weaponType, int damage)
    {
        SpawnProjectile(id, NetworkManager.Instance.LocalPlayerId, true, origin, vel, weaponType, damage);
        NetworkManager.Instance.SendReliable(new FireProjectilePacket
        {
            OwnerPlayerId = NetworkManager.Instance.LocalPlayerId,
            ProjectileId  = id,
            OriginX = origin.X, OriginY = origin.Y,
            VelX    = vel.X,    VelY    = vel.Y,
            WeaponType = (byte)weaponType,
            Damage     = damage
        });
    }

    private void SpawnProjectile(int id, byte owner, bool isLocal, Vector2 origin, Vector2 vel,
        WeaponType weaponType, int damage)
    {
        if (_projectiles.ContainsKey((owner, id))) return;
        var body = _physics!.CreateProjectile(origin, vel, null!);
        var proj = new ProjectileEntity(id, owner, isLocal, body, weaponType, damage);
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
                    (WeaponType)fire.WeaponType, fire.Damage);
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

        // Remote player (bottom right)
        UIRenderer.Text(sb, isHost ? "Opponent" : "Host",
            new Vector2(1060, 636), isHost ? JoinerColor : HostColor, small: true);
        UIRenderer.ProgressBar(sb, new Rectangle(1060, 650, 200, 8),
            _remote!.Shield, HovercraftEntity.MaxShield, new Color(60, 180, 255), new Color(15, 35, 60));
        UIRenderer.ProgressBar(sb, new Rectangle(1060, 661, 200, 14),
            _remote.Health, 100, Color.OrangeRed, new Color(60, 20, 20));

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
}
