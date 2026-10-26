using System.Numerics;

namespace Engine.Fps;

// A fast, fragile flyer. Hovers above head height (out of reach of the
// ground — and a bit harder to aim at), circles in, then dives straight at
// you in 3D, and climbs back up after the pass whether it hit or not.
//
//   Hover/approach ──(close + sees you)──▶ Windup ──▶ Dive ──▶ Retreat ──▶ Hover
public sealed class Wisp : Enemy
{
    private enum State { Idle, Hover, Windup, Dive, Retreat }

    public override EnemyKind Kind => EnemyKind.Wisp;
    public override Vector3 BloodColor => new(0.4f, 0.9f, 1f);

    public float HoverHeight = 1.4f;
    public float DiveRange = 3.5f;
    public float WindupTime = 0.35f;
    public float DiveSpeed = 8f;
    public float DiveDuration = 0.6f;
    public int DiveDamage = 8;

    private State _state = State.Idle;
    private Vector3 _diveVelocity;
    private bool _diveHit;
    private float _hoverPhase;
    private float _fallVelocity;

    public Wisp(Vector2 position) : base(position)
    {
        MaxHealth = 3;
        MoveSpeed = 2.8f;
        Width = 0.55f;
        Height = 0.55f;
        Radius = 0.2f;
        BaseY = HoverHeight;
        SightRange = 12f;
        _hoverPhase = position.X;
        InitHealth();
    }

    public override float WindupT => _state == State.Windup ? 1f - StateTimer / WindupTime : 0f;

    protected override void Think(float dt, FpsWorld world)
    {
        _hoverPhase += dt * 3f;
        float hoverTarget = HoverHeight + MathF.Sin(_hoverPhase) * 0.15f;

        switch (_state)
        {
            case State.Idle:
                BaseY = hoverTarget;
                if (IsAlerted) _state = State.Hover;
                break;

            case State.Hover:
                BaseY += (hoverTarget - BaseY) * (1f - MathF.Exp(-4f * dt));
                if (DistanceToPlayer(world) < DiveRange && CanSeePlayer(world))
                {
                    _state = State.Windup;
                    StateTimer = WindupTime;
                    break;
                }
                Move(world, ChaseDirection(world), MoveSpeed, dt);
                WalkBob = 0f; // it flies — no walking hop
                break;

            case State.Windup:
                // Pull back slightly before lunging: a readable "tell".
                BaseY += dt * 0.4f;
                if (StateTimer <= 0f)
                {
                    var player = world.Player;
                    var target = new Vector3(player.Position.X, player.FeetY + player.BodyHeight * 0.5f, player.Position.Y);
                    _diveVelocity = Vector3.Normalize(target - Center) * DiveSpeed;
                    _diveHit = false;
                    _state = State.Dive;
                    StateTimer = DiveDuration;
                }
                break;

            case State.Dive:
            {
                // The dive is full 3D: walls still block it on the ground
                // plane, and height is clamped between floor and ceiling.
                Position = world.Level.MoveAndCollide(Position, new Vector2(_diveVelocity.X, _diveVelocity.Z) * dt, Radius);
                BaseY = Math.Clamp(BaseY + _diveVelocity.Y * dt, 0.05f, FpsLevel.WallHeight - Height);

                if (!_diveHit && Vector3.Distance(Center, PlayerCenter(world)) < Radius + world.Player.Radius + 0.15f)
                {
                    _diveHit = true;
                    world.HurtPlayer(DiveDamage, Position, knockback: 2f);
                }

                if (StateTimer <= 0f || _diveHit)
                {
                    _state = State.Retreat;
                    StateTimer = 0.9f;
                }
                break;
            }

            case State.Retreat:
            {
                // Climb back up while drifting away from the player.
                BaseY += (hoverTarget - BaseY) * (1f - MathF.Exp(-3f * dt));
                Vector2 away = Position - world.Player.Position;
                if (away.LengthSquared() > 1e-6f)
                    Move(world, Vector2.Normalize(away), MoveSpeed * 0.8f, dt);
                WalkBob = 0f;
                if (StateTimer <= 0f) _state = State.Hover;
                break;
            }
        }
    }

    private static Vector3 PlayerCenter(FpsWorld world)
    {
        var p = world.Player;
        return new Vector3(p.Position.X, p.FeetY + p.BodyHeight * 0.5f, p.Position.Y);
    }

    // Dead wisps drop out of the air instead of just collapsing in place.
    protected override void UpdateDead(float dt)
    {
        _fallVelocity += 12f * dt;
        BaseY = MathF.Max(0f, BaseY - _fallVelocity * dt);
    }
}
