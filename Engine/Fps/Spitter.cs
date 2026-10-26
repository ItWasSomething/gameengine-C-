using System.Numerics;

namespace Engine.Fps;

// A ranged enemy. It tries to stay at a comfortable distance — backing off
// if you get close, approaching if you're far — strafes sideways while it
// waits, and lobs slow acid globs you can see coming and sidestep.
//
//   Position (keep distance + strafe) ──(sees you, cooldown done)──▶ Windup ──▶ spit ──▶ Position
public sealed class Spitter : Enemy
{
    private enum State { Idle, Position, Windup }

    public override EnemyKind Kind => EnemyKind.Spitter;
    public override Vector3 BloodColor => new(0.4f, 0.85f, 0.2f);

    public float PreferredMin = 3.5f;
    public float PreferredMax = 6.5f;
    public float WindupTime = 0.55f;
    public float FireCooldown = 1.8f;
    public float GlobSpeed = 7f;
    public int GlobDamage = 10;

    private State _state = State.Idle;
    private float _strafeSign = 1f;
    private float _strafeTimer;

    public Spitter(Vector2 position) : base(position)
    {
        MaxHealth = 5;
        MoveSpeed = 1.5f;
        Width = 0.8f;
        Height = 0.7f;
        Radius = 0.28f;
        SightRange = 12f;
        InitHealth();
    }

    public override float WindupT => _state == State.Windup ? 1f - StateTimer / WindupTime : 0f;

    protected override void Think(float dt, FpsWorld world)
    {
        switch (_state)
        {
            case State.Idle:
                if (IsAlerted) { _state = State.Position; StateTimer = FireCooldown * 0.5f; }
                break;

            case State.Position:
            {
                bool canSee = CanSeePlayer(world);
                if (!canSee)
                {
                    Move(world, ChaseDirection(world), MoveSpeed, dt); // go find a line of fire
                    break;
                }

                Vector2 toPlayer = Vector2.Normalize(world.Player.Position - Position);
                float distance = DistanceToPlayer(world);

                // Keep in the comfort band, and strafe across the player's view
                // so it isn't a sitting duck. Switch strafe direction now and
                // then, or immediately if a wall blocks us.
                Vector2 desired = Vector2.Zero;
                if (distance < PreferredMin) desired -= toPlayer;
                else if (distance > PreferredMax) desired += toPlayer;
                desired += new Vector2(-toPlayer.Y, toPlayer.X) * _strafeSign * 0.7f;

                _strafeTimer -= dt;
                float progress = Move(world, Vector2.Normalize(desired), MoveSpeed, dt);
                if (_strafeTimer <= 0f || progress < 0.3f)
                {
                    _strafeSign = -_strafeSign;
                    _strafeTimer = 1f + (float)Random.NextDouble() * 1.5f;
                }

                if (StateTimer <= 0f) { _state = State.Windup; StateTimer = WindupTime; }
                break;
            }

            case State.Windup:
                if (StateTimer <= 0f)
                {
                    if (CanSeePlayer(world)) Spit(world);
                    _state = State.Position;
                    StateTimer = FireCooldown;
                }
                break;
        }
    }

    private void Spit(FpsWorld world)
    {
        var mouth = new Vector3(Position.X, BaseY + Height * 0.45f, Position.Y);
        var player = world.Player;
        var chest = new Vector3(player.Position.X, player.FeetY + player.BodyHeight * 0.6f, player.Position.Y);

        world.SpawnProjectile(new Projectile
        {
            Owner = Projectile.Team.Enemy,
            Position = mouth,
            Velocity = Vector3.Normalize(chest - mouth) * GlobSpeed,
            Radius = 0.12f,
            Damage = GlobDamage,
            Color = new Vector3(0.5f, 1f, 0.2f),
            TrailColor = new Vector3(0.3f, 0.7f, 0.1f),
            Size = 0.22f,
            LightRadius = 2.2f,
        });
    }
}
