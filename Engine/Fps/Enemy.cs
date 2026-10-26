using System.Numerics;

namespace Engine.Fps;

public enum EnemyKind { Imp, Spitter, Brute, Wisp }

// Everything every enemy shares: health, getting hit (flash, squash,
// knockback), dying, moving through the level, noticing the player, and
// being hit by rays/explosions. Each enemy type is a subclass that only
// implements Think() — its own little state machine — on top of this.
//
// This is the "GameObject base class with virtual Update" shape the
// platformer roadmap mentions, applied to enemies.
public abstract class Enemy
{
    public abstract EnemyKind Kind { get; }

    public Vector2 Position;              // on the ground plane, like FpsPlayer
    public float BaseY;                   // height of its feet (0 for walkers; Wisps hover)
    public float Radius = 0.25f;
    public float Width = 0.72f;           // sprite size in world units
    public float Height = 0.9f;
    public int MaxHealth = 6;
    public int Health;
    public float MoveSpeed = 1.7f;
    public float SightRange = 10f;
    public float KnockbackResistance = 1f; // divides incoming knockback

    public virtual Vector3 BloodColor => new(0.55f, 0.1f, 0.6f);

    public bool IsAlive => Health > 0;
    public bool IsAlerted { get; protected set; }

    // Same hit-flash + squash-and-recover shape as the platformer's Target.
    public float HitFlashT => _hitFlashTimer / HitFlashDuration;
    public Vector2 VisualScale { get; private set; } = Vector2.One;
    public float WalkBob { get; protected set; }

    public event Action<Enemy>? Died;

    protected float StateTimer;
    protected readonly Random Random = new();

    private const float HitFlashDuration = 0.1f;
    private const float SquashDuration = 0.18f;
    private const float DeathDuration = 0.35f;
    private float _hitFlashTimer;
    private float _squashTimer;
    private float _deathTimer;
    private float _walkPhase;
    private Vector2 _knockback;

    protected Enemy(Vector2 position)
    {
        Position = position;
        _walkPhase = position.X * 7f + position.Y * 3f; // so a group doesn't bob in sync
    }

    // Wave spawns call this: they start already hunting you via the flow
    // field, instead of idling out of sight until you stumble on them.
    public void Alert() => IsAlerted = true;

    // Subclass constructors set MaxHealth; call this once they have.
    protected void InitHealth() => Health = MaxHealth;

    public void Update(float dt, FpsWorld world)
    {
        _hitFlashTimer = MathF.Max(0f, _hitFlashTimer - dt);
        UpdateVisualScale(dt);

        if (!IsAlive)
        {
            UpdateDead(dt);
            return;
        }

        if (_knockback != Vector2.Zero)
        {
            Position = world.Level.MoveAndCollide(Position, _knockback * dt, Radius);
            _knockback *= MathF.Exp(-10f * dt);
            if (_knockback.LengthSquared() < 0.0001f) _knockback = Vector2.Zero;
        }

        StateTimer -= dt;
        WalkBob = 0f;
        if (!IsAlerted && CanSeePlayer(world)) IsAlerted = true;

        Think(dt, world);
        SeparateFrom(world.Enemies);
    }

    // The per-type brain. Runs only while alive.
    protected abstract void Think(float dt, FpsWorld world);

    // Most enemies just lie there when dead; flying ones override this to fall.
    protected virtual void UpdateDead(float dt) { }

    // Telegraph colouring: subclasses return 0..1 while winding up an attack,
    // and the renderer tints toward an angry red glow by that much.
    public virtual float WindupT => 0f;

    // ---------------------------------------------------------------- helpers for Think()

    protected float DistanceToPlayer(FpsWorld world) => Vector2.Distance(Position, world.Player.Position);

    protected bool CanSeePlayer(FpsWorld world) =>
        world.Player.IsAlive
        && DistanceToPlayer(world) < SightRange
        && world.Level.HasLineOfSight(Position, world.Player.Position);

    // Straight at the player if visible, otherwise follow the flow field
    // (see FpsLevel.BuildFlowField) around walls.
    protected Vector2 ChaseDirection(FpsWorld world)
    {
        if (CanSeePlayer(world))
        {
            Vector2 toPlayer = world.Player.Position - Position;
            return toPlayer.LengthSquared() > 1e-6f ? Vector2.Normalize(toPlayer) : Vector2.Zero;
        }
        return FlowDirection(world.Level, world.FlowField);
    }

    // Moves with collision and drives the walk bob. Returns how far we
    // actually got, as a fraction of how far we tried (1 = unobstructed).
    protected float Move(FpsWorld world, Vector2 direction, float speed, float dt)
    {
        Vector2 before = Position;
        Position = world.Level.MoveAndCollide(Position, direction * speed * dt, Radius);

        _walkPhase += dt * 9f * (speed / 1.7f);
        WalkBob = MathF.Abs(MathF.Sin(_walkPhase)) * 0.05f;

        float intended = speed * dt;
        return intended > 0f ? Vector2.Distance(before, Position) / intended : 1f;
    }

    // Is the player's body within `reach` horizontally AND overlapping this
    // enemy's height? That second part is why wall-running over an Imp's
    // head is a legit way to dodge it.
    protected bool PlayerInReach(FpsWorld world, float reach)
    {
        var player = world.Player;
        if (!player.IsAlive || DistanceToPlayer(world) > reach) return false;
        return player.FeetY < BaseY + Height && player.FeetY + player.BodyHeight > BaseY;
    }

    private Vector2 FlowDirection(FpsLevel level, int[,] field)
    {
        var (cx, cz) = FpsLevel.CellOf(Position);
        if (level.IsWall(cx, cz)) return Vector2.Zero;

        int best = field[cx, cz];
        Vector2 target = new(cx + 0.5f, cz + 0.5f);
        foreach (var (nx, nz) in FpsLevel.Neighbours(cx, cz))
        {
            if (level.IsWall(nx, nz) || field[nx, nz] >= best) continue;
            best = field[nx, nz];
            target = new Vector2(nx + 0.5f, nz + 0.5f);
        }

        Vector2 delta = target - Position;
        return delta.LengthSquared() < 1e-6f ? Vector2.Zero : Vector2.Normalize(delta);
    }

    // Stop enemies merging into one blob: if two overlap, push this one away
    // by half the overlap (the other pushes itself the other half).
    private void SeparateFrom(IReadOnlyList<Enemy> others)
    {
        foreach (var other in others)
        {
            if (other == this || !other.IsAlive) continue;
            Vector2 delta = Position - other.Position;
            float distance = delta.Length();
            float minDistance = Radius + other.Radius;
            if (distance > 1e-4f && distance < minDistance)
                Position += delta / distance * (minDistance - distance) * 0.5f;
        }
    }

    // ---------------------------------------------------------------- taking hits

    public void TakeDamage(int amount, Vector2 pushDirection, float knockback)
    {
        if (!IsAlive) return;

        Health -= amount;
        _hitFlashTimer = HitFlashDuration;
        _squashTimer = SquashDuration;
        _knockback += pushDirection * (knockback / KnockbackResistance);
        IsAlerted = true; // getting shot from the dark still wakes it up

        if (Health <= 0)
        {
            Health = 0;
            Died?.Invoke(this);
        }
    }

    public bool RayIntersect(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0f;
        return IsAlive && Geometry.RayCylinder(origin, direction, Position, Radius,
            BaseY, BaseY + Height * VisualScale.Y, out distance);
    }

    public Vector3 Center => new(Position.X, BaseY + Height * 0.5f, Position.Y);

    private void UpdateVisualScale(float dt)
    {
        if (!IsAlive)
        {
            // Collapse into a flat heap and stay there as a corpse.
            _deathTimer = MathF.Min(DeathDuration, _deathTimer + dt);
            float e = Easing.OutCubic(_deathTimer / DeathDuration);
            VisualScale = new Vector2(1f + 0.4f * e, 1f - 0.8f * e);
            return;
        }

        _squashTimer = MathF.Max(0f, _squashTimer - dt);
        float eased = Easing.OutCubic(1f - _squashTimer / SquashDuration);
        VisualScale = new Vector2(0.75f + 0.25f * eased, 1.2f - 0.2f * eased);
    }
}
