using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using NetworkingLib.Core;
using ScrubZone2D.Arena;
using ScrubZone2D.Entities;
using ScrubZone2D.Network;
using ScrubZone2D.Physics;

namespace ScrubZone2D.States;

public sealed class GameplayState : GameState
{
    private const float StateUpdateHz = 20f;

    private readonly GameStateManager _stateManager;

    private PhysicsWorld?     _physics;
    private ArenaMap?         _arena;
    private HovercraftEntity? _local;
    private HovercraftEntity? _remote;
    // Key = (ownerId, projId) — prevents ID collision when both players start their counter at 1
    private readonly Dictionary<(byte, int), ProjectileEntity> _projectiles = new();

    private float  _stateTimer;
    private bool   _roundOver;
    private string _roundResult = "";

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

        _physics = new PhysicsWorld();
        _arena   = new ArenaMap();
        _arena.CreatePhysicsBodies(_physics);

        bool isHost = net.Role == NetworkRole.Host;

        var localSpawn  = isHost ? ArenaMap.SpawnHost   : ArenaMap.SpawnJoiner;
        var remoteSpawn = isHost ? ArenaMap.SpawnJoiner : ArenaMap.SpawnHost;

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

    public override void Update(float dt, StateServices svc)
    {
        if (_roundOver)
        {
            if (InputHelper.IsKeyPressed(Keys.Escape))
                _stateManager.Pop();
            return;
        }

        _local!.UpdateLocal(dt, svc.Input);
        _physics!.Step(dt);

        ProcessCollisions();

        // Remove destroyed / spent projectiles
        foreach (var proj in _projectiles.Values
            .Where(p => p.IsDestroyed || p.ShouldDestroy()).ToList())
        {
            if (proj.IsLocal && !proj.IsDestroyed)
                NetworkManager.Instance.SendReliable(
                    new ProjectileDestroyedPacket { ProjectileId = proj.Id });

            _physics.DestroyBody(proj.PhysicsBody);
            _projectiles.Remove((proj.OwnerPlayerId, proj.Id));
        }

        // Broadcast local state
        _stateTimer += dt;
        if (_stateTimer >= 1f / StateUpdateHz)
        {
            _stateTimer = 0;
            NetworkManager.Instance.SendStateUnreliable(_local.BuildStatePacket());
        }

        if (_local.Health <= 0)        EndRound(false);
        else if (_remote!.Health <= 0) EndRound(true);
    }

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        sb.Begin();

        _arena!.Draw(sb);
        _local!.Draw(sb);
        _remote!.Draw(sb);

        foreach (var p in _projectiles.Values)
            p.Draw(sb);

        DrawHud(sb);
        sb.End();
    }

    // ── Collision handling ────────────────────────────────────────────────────
    // Projectiles only interact with walls physically (Box2D filter).
    // Hovercraft hits are detected via distance check here.

    private void ProcessCollisions()
    {
        const float hitDist = HovercraftEntity.Radius + 6f; // hovercraft radius + projectile radius

        foreach (var proj in _projectiles.Values.ToList())
        {
            if (proj.IsDestroyed || !proj.IsLocal) continue;

            var target = _remote!;
            if (target == null || !target.IsAlive) continue;
            if (target.PlayerId == proj.OwnerPlayerId) continue;

            var projPos = proj.Position;
            var hcPos   = target.IsLocal ? target.Position : target.RemotePosition;

            if (Vector2.Distance(projPos, hcPos) > hitDist) continue;

            target.Health = Math.Max(0, target.Health - ProjectileEntity.Damage);
            proj.Destroy();

            NetworkManager.Instance.SendReliable(new PlayerDamagedPacket
            {
                TargetId     = target.PlayerId,
                AttackerId   = proj.OwnerPlayerId,
                ProjectileId = proj.Id,
                Damage       = ProjectileEntity.Damage,
                RemainingHp  = target.Health
            });
            NetworkManager.Instance.SendReliable(
                new ProjectileDestroyedPacket { ProjectileId = proj.Id });
        }
    }

    // ── Firing ───────────────────────────────────────────────────────────────

    private void OnLocalFire(int id, Vector2 origin, Vector2 vel)
    {
        SpawnProjectile(id, NetworkManager.Instance.LocalPlayerId, true, origin, vel);
        NetworkManager.Instance.SendReliable(new FireProjectilePacket
        {
            OwnerPlayerId = NetworkManager.Instance.LocalPlayerId,
            ProjectileId  = id,
            OriginX = origin.X, OriginY = origin.Y,
            VelX    = vel.X,    VelY    = vel.Y
        });
    }

    private void SpawnProjectile(int id, byte owner, bool isLocal, Vector2 origin, Vector2 vel)
    {
        if (_projectiles.ContainsKey((owner, id))) return;
        var body = _physics!.CreateProjectile(origin, vel, null!);
        var proj = new ProjectileEntity(id, owner, isLocal, body);
        body.Data.Owner = proj;
        _projectiles[(owner, id)] = proj;
    }

    // ── Network packets ───────────────────────────────────────────────────────

    private void OnNetworkPacket(IPacket packet)
    {
        switch (packet)
        {
            case HovercraftStatePacket state:
                _remote?.ApplyRemoteState(state);
                break;

            case FireProjectilePacket fire:
                SpawnProjectile(fire.ProjectileId, fire.OwnerPlayerId, false,
                    new(fire.OriginX, fire.OriginY), new(fire.VelX, fire.VelY));
                break;

            case ProjectileDestroyedPacket destroy:
                var remoteId = NetworkManager.Instance.RemotePlayerId;
                if (_projectiles.TryGetValue((remoteId, destroy.ProjectileId), out var p))
                    p.Destroy();
                break;

            case PlayerDamagedPacket dmg when dmg.TargetId == _local?.PlayerId:
                if (_local != null) _local.Health = Math.Max(0, dmg.RemainingHp);
                break;

            case RoundOverPacket over:
                _roundResult = over.WinnerPlayerId == NetworkManager.Instance.LocalPlayerId
                    ? "You Win!" : "You Lose!";
                _roundOver = true;
                break;
        }
    }

    private void OnDisconnected()
    {
        _roundResult = "Opponent disconnected";
        _roundOver   = true;
    }

    private void EndRound(bool localWins)
    {
        if (_roundOver) return;
        _roundOver   = true;
        _roundResult = localWins ? "You Win!" : "You Lose!";

        if (localWins)
            NetworkManager.Instance.SendReliable(new RoundOverPacket
            {
                WinnerPlayerId = NetworkManager.Instance.LocalPlayerId
            });
    }

    // ── HUD ───────────────────────────────────────────────────────────────────

    private void DrawHud(SpriteBatch sb)
    {
        var net    = NetworkManager.Instance;
        bool isHost = net.Role == NetworkRole.Host;

        UIRenderer.Text(sb, isHost ? "You (Host)" : "You (Guest)",
            new Vector2(20, 648), isHost ? HostColor : JoinerColor, small: true);
        UIRenderer.ProgressBar(sb, new Rectangle(20, 665, 200, 14),
            _local!.Health, 100, Color.LimeGreen, new Color(60, 20, 20));

        UIRenderer.Text(sb, isHost ? "Opponent" : "Host",
            new Vector2(1060, 648), isHost ? JoinerColor : HostColor, small: true);
        UIRenderer.ProgressBar(sb, new Rectangle(1060, 665, 200, 14),
            _remote!.Health, 100, Color.OrangeRed, new Color(60, 20, 20));

        UIRenderer.Text(sb, "WASD move | Mouse aim | LClick fire | ESC quit",
            new Vector2(20, 10), Color.DimGray, small: true);

        if (_roundOver)
        {
            UIRenderer.FillRect(sb, new Rectangle(0, 0, 1280, 720), new Color(0, 0, 0, 160));
            UIRenderer.TextCentered(sb, _roundResult, new Rectangle(0, 290, 1280, 80), Color.White);
            UIRenderer.TextCentered(sb, "Press ESC to return to menu",
                new Rectangle(0, 380, 1280, 40), Color.Gray, small: true);
        }
    }
}
