using Box2D.NET.Bindings;
using XnaVec = Microsoft.Xna.Framework.Vector2;
using B2Vec = System.Numerics.Vector2;

namespace ScrubZone2D.Physics;

public enum PhysicsTag { Wall, Hovercraft, Projectile }

public sealed class PhysicsBodyData
{
    public PhysicsTag Tag        { get; init; }
    public object?    Owner      { get; set; }
    public int        BounceCount { get; set; }
}

// Wrapper so callers don't need the Box2D.NET.Bindings namespace.
public sealed class PhysicsBody
{
    internal B2.BodyId Id   { get; }
    public   PhysicsBodyData Data { get; }

    internal PhysicsBody(B2.BodyId id, PhysicsBodyData data) { Id = id; Data = data; }

    public B2Vec GetPosition()
    {
        var p = B2.BodyGetPosition(Id);
        return new B2Vec(p.x, p.y);
    }

    public float GetAngle()
    {
        var r = B2.BodyGetRotation(Id);
        return MathF.Atan2(r.s, r.c);
    }

    public B2Vec GetLinearVelocity()
    {
        var v = B2.BodyGetLinearVelocity(Id);
        return new B2Vec(v.x, v.y);
    }

    public void SetLinearVelocity(B2Vec vel)
        => B2.BodySetLinearVelocity(Id, new B2.Vec2 { x = vel.X, y = vel.Y });

    public void ApplyForceToCenter(B2Vec force, bool wake)
        => B2.BodyApplyForceToCenter(Id, new B2.Vec2 { x = force.X, y = force.Y }, wake);

    public void SetAngularVelocity(float omega)
        => B2.BodySetAngularVelocity(Id, omega);

    public void SetTransform(B2Vec pos, float angle)
    {
        var rot = B2.MakeRot(angle);
        B2.BodySetTransform(Id, new B2.Vec2 { x = pos.X, y = pos.Y }, rot);
    }
}

public sealed class PhysicsWorld : IDisposable
{
    // 32 pixels = 1 metre
    public const float PPM = 32f;

    private B2.WorldId _worldId;
    private int        _nextId = 1;
    private readonly Dictionary<int, PhysicsBodyData> _dataById = new();

    public static B2Vec  ToB2(XnaVec v)  => new(v.X / PPM, v.Y / PPM);
    public static XnaVec ToXna(B2Vec v)  => new(v.X * PPM, v.Y * PPM);
    public static float  ToM(float px)   => px / PPM;

    private static B2.Vec2 V(B2Vec v) => new() { x = v.X, y = v.Y };
    private static B2.Vec2 V(XnaVec v) => new() { x = v.X / PPM, y = v.Y / PPM };

    public unsafe PhysicsWorld()
    {
        var def = B2.DefaultWorldDef();
        def.gravity = new B2.Vec2 { x = 0f, y = 0f };
        _worldId = B2.CreateWorld(&def);
    }

    public void Step(float dt)
    {
        B2.WorldStep(_worldId, dt, 4);
        ProcessContactEvents();
    }

    private unsafe void ProcessContactEvents()
    {
        var events = B2.WorldGetContactEvents(_worldId);
        for (int i = 0; i < events.beginCount; i++)
        {
            var evt   = events.beginEvents[i];
            var dataA = GetData(B2.ShapeGetBody(evt.shapeIdA));
            var dataB = GetData(B2.ShapeGetBody(evt.shapeIdB));
            if (dataA == null || dataB == null) continue;

            if      (dataA.Tag == PhysicsTag.Projectile && dataB.Tag == PhysicsTag.Wall) dataA.BounceCount++;
            else if (dataB.Tag == PhysicsTag.Projectile && dataA.Tag == PhysicsTag.Wall) dataB.BounceCount++;
        }
    }

    private unsafe PhysicsBodyData? GetData(B2.BodyId bodyId)
    {
        int id = (int)(nint)B2.BodyGetUserData(bodyId);
        return _dataById.TryGetValue(id, out var d) ? d : null;
    }

    private unsafe PhysicsBody Register(B2.BodyId bodyId, PhysicsBodyData data)
    {
        int id = _nextId++;
        _dataById[id] = data;
        B2.BodySetUserData(bodyId, (void*)(nint)id);
        return new PhysicsBody(bodyId, data);
    }

    public unsafe PhysicsBody CreateStaticBox(XnaVec center, float widthPx, float heightPx)
    {
        var bd       = B2.DefaultBodyDef();
        bd.type      = B2.staticBody;
        bd.position  = V(center);
        var bodyId   = B2.CreateBody(_worldId, &bd);

        var sd = B2.DefaultShapeDef();
        sd.material.friction    = 0.1f;
        sd.material.restitution = 0.4f;
        sd.filter = new B2.Filter { categoryBits = 0x0001, maskBits = 0xFFFF };

        var box     = B2.MakeBox(ToM(widthPx / 2f), ToM(heightPx / 2f));
        B2.CreatePolygonShape(bodyId, &sd, &box);

        return Register(bodyId, new PhysicsBodyData { Tag = PhysicsTag.Wall });
    }

    public unsafe PhysicsBody CreateHovercraft(XnaVec position, float radiusPx, object owner)
    {
        var bd              = B2.DefaultBodyDef();
        bd.type             = B2.dynamicBody;
        bd.position         = V(position);
        bd.linearDamping    = 1.5f;
        bd.angularDamping   = 8f;
        var bodyId          = B2.CreateBody(_worldId, &bd);

        var sd = B2.DefaultShapeDef();
        sd.density               = 1f;
        sd.material.friction     = 0.2f;
        sd.material.restitution  = 0.3f;
        sd.filter = new B2.Filter { categoryBits = 0x0002, maskBits = 0x0001 }; // walls only

        var circle = new B2.Circle { center = default, radius = ToM(radiusPx) };
        B2.CreateCircleShape(bodyId, &sd, &circle);

        return Register(bodyId, new PhysicsBodyData { Tag = PhysicsTag.Hovercraft, Owner = owner });
    }

    public unsafe PhysicsBody CreateProjectile(XnaVec position, XnaVec velocityPx, object? owner)
    {
        var bd       = B2.DefaultBodyDef();
        bd.type      = B2.dynamicBody;
        bd.position  = V(position);
        bd.isBullet  = true;
        var bodyId   = B2.CreateBody(_worldId, &bd);

        var sd = B2.DefaultShapeDef();
        sd.density               = 0.1f;
        sd.material.friction     = 0f;
        sd.material.restitution  = 0.95f;
        sd.filter                = new B2.Filter { categoryBits = 0x0004, maskBits = 0x0001 }; // walls only
        sd.enableContactEvents   = true;

        var circle  = new B2.Circle { center = default, radius = ToM(5f) };
        var shapeId = B2.CreateCircleShape(bodyId, &sd, &circle);
        B2.ShapeEnableContactEvents(shapeId, true);

        var body = Register(bodyId, new PhysicsBodyData { Tag = PhysicsTag.Projectile, Owner = owner });

        B2.BodySetLinearVelocity(bodyId, V(ToB2(velocityPx)));
        return body;
    }

    public unsafe void DestroyBody(PhysicsBody body)
    {
        int id = (int)(nint)B2.BodyGetUserData(body.Id);
        _dataById.Remove(id);
        B2.DestroyBody(body.Id);
    }

    public void Dispose()
    {
        B2.DestroyWorld(_worldId);
        _dataById.Clear();
    }
}
