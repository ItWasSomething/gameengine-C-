using System.Numerics;

namespace Engine.Fps;

// The basic melee grunt. Chases, winds up a swipe (glowing red), and only
// connects if you're STILL in reach when the windup ends — so backing off
// (or jumping over it) during the telegraph is a real defence.
//
//   Idle ──(sees you)──▶ Chase ──(in reach)──▶ Windup ──▶ swipe ──▶ Recover ──▶ Chase
public sealed class Imp : Enemy
{
    private enum State { Idle, Chase, Windup, Recover }

    public override EnemyKind Kind => EnemyKind.Imp;

    public float AttackRange = 0.75f;
    public float WindupTime = 0.45f;
    public float RecoverTime = 0.6f;
    public int AttackDamage = 12;

    private State _state = State.Idle;

    public Imp(Vector2 position) : base(position)
    {
        MaxHealth = 6;
        MoveSpeed = 1.8f;
        InitHealth();
    }

    public override float WindupT => _state == State.Windup ? 1f - StateTimer / WindupTime : 0f;

    protected override void Think(float dt, FpsWorld world)
    {
        switch (_state)
        {
            case State.Idle:
                if (IsAlerted) _state = State.Chase;
                break;

            case State.Chase:
                if (PlayerInReach(world, AttackRange)) { _state = State.Windup; StateTimer = WindupTime; break; }
                Move(world, ChaseDirection(world), MoveSpeed, dt);
                break;

            case State.Windup:
                if (StateTimer <= 0f)
                {
                    if (PlayerInReach(world, AttackRange + 0.3f))
                        world.HurtPlayer(AttackDamage, Position, knockback: 2.5f);
                    _state = State.Recover;
                    StateTimer = RecoverTime;
                }
                break;

            case State.Recover:
                if (StateTimer <= 0f) _state = State.Chase;
                break;
        }
    }
}
