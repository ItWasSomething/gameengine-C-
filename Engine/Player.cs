using Silk.NET.Maths;

namespace Engine;

// A platformer character controller. Two things make it feel "smooth"
// instead of instant/twitchy:
//  1. Horizontal velocity eases toward a target speed (Acceleration) and
//     eases back to zero (Deceleration) instead of snapping, like Update()
//     lerping a Rigidbody2D.velocity in Unity.
//  2. Two forgiveness windows around jumping: coyote time (still jumpable
//     for a few frames after walking off a ledge) and jump buffering (a
//     jump press a few frames before landing isn't dropped, it fires the
//     instant you touch ground). Both are just countdown timers.
public class Player
{
    public Vector2D<float> Position; // bottom-left corner, world pixels
    public Vector2D<float> Velocity;
    public Vector2D<float> Size = new(48f, 48f);
    public bool IsGrounded { get; private set; }

    // Tuning knobs, in pixels/second and pixels/second^2. Public so they're
    // easy to tweak live while chasing "game feel" without touching logic.
    public float MoveSpeed = 420f;
    public float Acceleration = 4000f;
    public float Deceleration = 5000f;
    public float AirControlMultiplier = 0.7f; // less responsive while airborne
    public float Gravity = 2200f;
    public float MaxFallSpeed = 1400f;
    public float JumpVelocity = 900f;
    public float JumpCutMultiplier = 0.45f; // releasing jump early cuts the rise short
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.12f;

    // Which way the player last moved — melee attacks swing toward this,
    // since there's no separate "aim" input. +1 = right, -1 = left. Persists
    // while idle rather than resetting, so standing still and attacking
    // still swings the direction you were last facing.
    public float FacingDirection { get; private set; } = 1f;

    public bool IsAttacking { get; private set; }
    public float AttackDuration = 0.2f;      // total swing time, start to recovery end
    public float AttackActiveStart = 0.05f;  // hitbox turns on at this point in the swing
    public float AttackActiveEnd = 0.12f;    // and off again here — a brief active window, not the whole swing
    public float AttackCooldown = 0.25f;     // minimum time between swings, measured from swing start
    public float AttackRange = 30f;          // hitbox center's offset in front of the player
    public Vector2D<float> AttackHitboxSize = new(44f, 40f);

    // True only during AttackActiveStart..AttackActiveEnd of the current
    // swing — GameWindow only tests for hits while this is true, so a swing
    // has a real "the blade is out now" window instead of hitting for its
    // entire duration.
    public bool IsAttackHitboxActive =>
        IsAttacking && _attackTimer >= AttackActiveStart && _attackTimer <= AttackActiveEnd;

    // Fired the instant a swing starts (not when it lands a hit) — GameWindow
    // uses this to reset its "already hit this swing" bookkeeping so one
    // swing can't multi-hit the same target every frame the hitbox is active.
    public event Action? AttackStarted;

    private float _attackTimer;
    private float _attackCooldownTimer;

    // Ignored while already attacking or still on cooldown — matches the
    // no-buffering-for-attacks choice: unlike jumping, an attack press that
    // arrives too early is just dropped rather than queued.
    public void TryAttack()
    {
        if (IsAttacking || _attackCooldownTimer > 0f) return;
        IsAttacking = true;
        _attackTimer = 0f;
        _attackCooldownTimer = AttackCooldown;
        AttackStarted?.Invoke();
    }

    public Aabb GetAttackHitbox()
    {
        var center = new Vector2D<float>(
            Position.X + Size.X / 2f + FacingDirection * AttackRange,
            Position.Y + Size.Y / 2f);
        var min = new Vector2D<float>(
            center.X - AttackHitboxSize.X / 2f,
            center.Y - AttackHitboxSize.Y / 2f);
        return new Aabb(min, AttackHitboxSize);
    }

    // Squash &amp; stretch is purely a visual multiplier on top of Size — it
    // never touches the actual collision box, so it can't affect gameplay.
    // GameWindow reads VisualScale only when choosing a draw size.
    public Vector2D<float> VisualScale { get; private set; } = new(1f, 1f);
    public float SquashDuration = 0.15f;    // seconds to ease back to normal after landing
    public float LandSquashX = 1.4f;        // width multiplier at the instant of landing
    public float LandSquashY = 0.6f;        // height multiplier at the instant of landing
    public float AirStretchPerSpeed = 0.00035f; // vertical stretch per px/s of vertical speed
    public float MaxAirStretch = 0.35f;

    // Fired the frame a buffered/coyote/ordinary jump actually triggers, and
    // the frame IsGrounded flips from false to true. GameWindow hooks these
    // to spawn dust particles without Player needing to know particles exist.
    public event Action? Jumped;
    public event Action? Landed;

    private bool _wasGrounded = true; // starts true: the player spawns standing on the ground
    private float _squashTimer;

    private float _coyoteTimer;
    private float _jumpBufferTimer;

    // Call when the jump key is pressed. Doesn't jump immediately — just
    // starts the buffer countdown; Update() decides whether it's allowed.
    public void BufferJump() => _jumpBufferTimer = JumpBufferTime;

    // Call when the jump key is released. A short hop if we're still rising.
    public void CutJumpIfRising()
    {
        if (Velocity.Y > 0f)
            Velocity.Y *= JumpCutMultiplier;
    }

    public void Update(float dt, float moveInput, IReadOnlyList<Aabb> solids)
    {
        if (MathF.Abs(moveInput) > 0.01f) FacingDirection = MathF.Sign(moveInput);

        if (IsAttacking)
        {
            _attackTimer += dt;
            if (_attackTimer >= AttackDuration) IsAttacking = false;
        }
        _attackCooldownTimer = MathF.Max(0f, _attackCooldownTimer - dt);

        float targetSpeed = moveInput * MoveSpeed;
        float rate = MathF.Abs(moveInput) > 0.01f ? Acceleration : Deceleration;
        if (!IsGrounded) rate *= AirControlMultiplier;
        Velocity.X = MoveTowards(Velocity.X, targetSpeed, rate * dt);

        // Coyote timer resets every frame you're grounded, then drains once
        // you leave the ground. Jump buffer just drains every frame; it's
        // (re)armed from outside via BufferJump().
        _coyoteTimer = IsGrounded ? CoyoteTime : MathF.Max(0f, _coyoteTimer - dt);
        _jumpBufferTimer = MathF.Max(0f, _jumpBufferTimer - dt);

        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
        {
            Velocity.Y = JumpVelocity;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
            IsGrounded = false;
            Jumped?.Invoke();
        }

        Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -MaxFallSpeed);

        // Resolve X and Y separately rather than moving diagonally and
        // testing once — otherwise sliding into a wall while falling could
        // shove the player through the floor along with it.
        MoveAndCollide(new Vector2D<float>(Velocity.X * dt, 0f), solids);
        IsGrounded = false;
        MoveAndCollide(new Vector2D<float>(0f, Velocity.Y * dt), solids);

        bool justLanded = IsGrounded && !_wasGrounded;
        if (justLanded)
        {
            _squashTimer = SquashDuration;
            Landed?.Invoke();
        }
        _wasGrounded = IsGrounded;

        UpdateVisualScale(dt);
    }

    // Purely cosmetic: while airborne, stretch tall/thin proportional to
    // vertical speed (classic "anticipation" for a jump/fall); on landing,
    // snap to a flat/wide squash and ease back to normal over SquashDuration.
    private void UpdateVisualScale(float dt)
    {
        _squashTimer = MathF.Max(0f, _squashTimer - dt);

        if (_squashTimer > 0f)
        {
            float t = 1f - _squashTimer / SquashDuration; // 0 at landing -> 1 when settled
            float eased = Easing.OutCubic(t);
            float scaleX = LandSquashX + (1f - LandSquashX) * eased;
            float scaleY = LandSquashY + (1f - LandSquashY) * eased;
            VisualScale = new Vector2D<float>(scaleX, scaleY);
        }
        else if (!IsGrounded)
        {
            float stretch = MathF.Min(MathF.Abs(Velocity.Y) * AirStretchPerSpeed, MaxAirStretch);
            VisualScale = new Vector2D<float>(1f - stretch * 0.5f, 1f + stretch);
        }
        else
        {
            VisualScale = new Vector2D<float>(1f, 1f);
        }
    }

    private void MoveAndCollide(Vector2D<float> delta, IReadOnlyList<Aabb> solids)
    {
        Position = new Vector2D<float>(Position.X + delta.X, Position.Y + delta.Y);
        var box = new Aabb(Position, Size);

        foreach (var solid in solids)
        {
            if (!box.Intersects(solid)) continue;

            float overlapX = MathF.Min(box.Max.X, solid.Max.X) - MathF.Max(box.Min.X, solid.Min.X);
            float overlapY = MathF.Min(box.Max.Y, solid.Max.Y) - MathF.Max(box.Min.Y, solid.Min.Y);

            if (delta.X != 0f)
            {
                float pushX = delta.X > 0f ? -overlapX : overlapX;
                Position = new Vector2D<float>(Position.X + pushX, Position.Y);
                Velocity = new Vector2D<float>(0f, Velocity.Y);
            }
            else if (delta.Y != 0f)
            {
                if (delta.Y < 0f) IsGrounded = true;
                float pushY = delta.Y > 0f ? -overlapY : overlapY;
                Position = new Vector2D<float>(Position.X, Position.Y + pushY);
                Velocity = new Vector2D<float>(Velocity.X, 0f);
            }

            box = new Aabb(Position, Size);
        }
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta) return target;
        return current + MathF.Sign(target - current) * maxDelta;
    }
}
