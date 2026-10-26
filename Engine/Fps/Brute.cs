using System.Numerics;

namespace Engine.Fps;

// A slow, huge tank. Up close it slams; at mid range it winds up and
// CHARGES in a straight line. The charge direction is locked when the
// windup ends, so sidestepping (or jumping onto a wall) makes it miss — and
// if it runs into a wall it stuns itself, leaving it open to punishment.
//
//   Chase ──(mid range)──▶ ChargeWindup ──▶ Charge ──(hit wall)──▶ Stunned ──▶ Chase
//     │                                        └──(hit you / time up)──▶ Recover
//     └──(close)──▶ SlamWindup ──▶ slam ──▶ Recover
public sealed class Brute : Enemy
{
    private enum State { Idle, Chase, SlamWindup, ChargeWindup, Charge, Stunned, Recover }

    public override EnemyKind Kind => EnemyKind.Brute;
    public override Vector3 BloodColor => new(0.6f, 0.1f, 0.05f);

    public float SlamRange = 1.05f;
    public float SlamWindup = 0.65f;
    public int SlamDamage = 20;
    public float ChargeMinRange = 2.5f;
    public float ChargeMaxRange = 7f;
    public float ChargeWindup = 0.7f;
    public float ChargeSpeed = 7.5f;
    public float ChargeDuration = 1.1f;
    public int ChargeDamage = 25;
    public float ChargeCooldown = 3f;
    public float StunTime = 1.6f;

    private State _state = State.Idle;
    private Vector2 _chargeDirection;
    private float _chargeCooldownTimer;
    private bool _chargeHit;

    public Brute(Vector2 position) : base(position)
    {
        MaxHealth = 30;
        MoveSpeed = 1.2f;
        Width = 1.3f;
        Height = 1.5f;
        Radius = 0.4f;
        KnockbackResistance = 6f;
        InitHealth();
    }

    public bool IsStunned => _state == State.Stunned;

    public override float WindupT => _state switch
    {
        State.SlamWindup => 1f - StateTimer / SlamWindup,
        State.ChargeWindup => 1f - StateTimer / ChargeWindup,
        State.Charge => 1f,
        _ => 0f,
    };

    protected override void Think(float dt, FpsWorld world)
    {
        _chargeCooldownTimer -= dt;

        switch (_state)
        {
            case State.Idle:
                if (IsAlerted) _state = State.Chase;
                break;

            case State.Chase:
            {
                float distance = DistanceToPlayer(world);
                if (PlayerInReach(world, SlamRange)) { Enter(State.SlamWindup, SlamWindup); break; }
                if (_chargeCooldownTimer <= 0f && distance > ChargeMinRange && distance < ChargeMaxRange && CanSeePlayer(world))
                {
                    Enter(State.ChargeWindup, ChargeWindup);
                    break;
                }
                Move(world, ChaseDirection(world), MoveSpeed, dt);
                break;
            }

            case State.SlamWindup:
                if (StateTimer <= 0f)
                {
                    if (PlayerInReach(world, SlamRange + 0.3f))
                        world.HurtPlayer(SlamDamage, Position, knockback: 6f);
                    world.Shockwave(new Vector3(Position.X, 0.05f, Position.Y), BloodColor * 0.5f);
                    Enter(State.Recover, 0.8f);
                }
                break;

            case State.ChargeWindup:
                // Keep turning to face the player during the windup, then lock in.
                _chargeDirection = Vector2.Normalize(world.Player.Position - Position);
                if (StateTimer <= 0f)
                {
                    _chargeHit = false;
                    Enter(State.Charge, ChargeDuration);
                }
                break;

            case State.Charge:
            {
                float progress = Move(world, _chargeDirection, ChargeSpeed, dt);

                if (!_chargeHit && PlayerInReach(world, Radius + world.Player.Radius + 0.15f))
                {
                    _chargeHit = true;
                    world.HurtPlayer(ChargeDamage, Position, knockback: 9f);
                }

                if (progress < 0.25f)
                {
                    // Slammed into a wall: stunned, and the impact shakes the room.
                    world.Shockwave(new Vector3(Position.X, 0.5f, Position.Y), new Vector3(0.5f, 0.45f, 0.4f));
                    Enter(State.Stunned, StunTime);
                    _chargeCooldownTimer = ChargeCooldown;
                }
                else if (StateTimer <= 0f)
                {
                    Enter(State.Recover, 0.6f);
                    _chargeCooldownTimer = ChargeCooldown;
                }
                break;
            }

            case State.Stunned:
                WalkBob = MathF.Sin(StateTimer * 30f) * 0.02f; // dazed wobble
                if (StateTimer <= 0f) _state = State.Chase;
                break;

            case State.Recover:
                if (StateTimer <= 0f) _state = State.Chase;
                break;
        }
    }

    private void Enter(State state, float timer)
    {
        _state = state;
        StateTimer = timer;
    }
}
