using System.Numerics;

namespace Engine.Fps;

// The first-person player: a camera with a body. Owns position, look
// direction (yaw/pitch), movement (walk, jump, wall-run), head bob and health.
// Like the platformer's Player it has no idea how it's drawn — the window
// asks it for EyePosition/ViewForward/ViewRoll and builds the camera from those.
public sealed class FpsPlayer
{
    // Position on the ground plane: X = world X, Y = world Z. Collision with
    // walls is purely 2D (walls are floor-to-ceiling), so height is tracked
    // separately in FeetY.
    public Vector2 Position;
    public float FeetY;                  // 0 = standing on the floor
    public float Radius = 0.22f;
    public float BodyHeight = 0.6f;      // feet to top of head, for being hit and the ceiling
    public float EyeHeight = 0.5f;       // feet to eyes

    // Yaw = turning left/right around the vertical axis, pitch = looking
    // up/down. Both in radians. Yaw 0 looks toward -Z.
    public float Yaw;
    public float Pitch;
    public float MouseSensitivity = 0.0025f; // radians per pixel of mouse movement
    public float MaxPitch = 1.45f;           // just short of straight up/down (~83°)

    // ---- ground movement (public so they're easy to tweak while chasing feel)
    public float MoveSpeed = 3.6f;      // world units (tiles) per second
    public float SprintMultiplier = 1.5f;
    public float Acceleration = 30f;
    public float Deceleration = 22f;
    public float AirAcceleration = 9f;  // steering while airborne: weaker, and never brakes
    public float BobFrequency = 11f;
    public float BobAmount = 0.035f;
    public float RecoilRecovery = 12f;

    // ---- jumping: the same feel tricks as the platformer's Player
    public float Gravity = 18f;
    public float JumpVelocity = 6.2f;   // apex ≈ v²/2g ≈ 1.07 units
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.12f;

    // ---- wall-running
    public float WallRunMinSpeed = 2.5f;   // must be moving at least this fast to latch on
    public float WallRunSpeed = 6.5f;      // speed you're pulled toward while running
    public float WallRunDuration = 1.3f;   // max seconds on one wall
    public float WallRunGravity = 4f;      // much floatier than normal gravity
    public float WallRunStartLift = 1.8f;  // small upward pop when you latch on
    public float WallJumpPush = 5f;        // sideways push off the wall
    public float WallJumpUp = 5.8f;
    public float WallRunCameraRoll = 0.2f; // radians (~11°) of head tilt away from the wall
    public float WallReattachDelay = 0.35f;

    public int MaxHealth = 100;
    public int Health;
    public bool IsAlive => Health > 0;

    public bool IsGrounded { get; private set; } = true;
    public bool IsWallRunning { get; private set; }
    public float VerticalVelocity { get; private set; }

    private Vector2 _velocity;           // horizontal
    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private Vector2 _wallNormal;         // points OUT of the wall we're running on
    private float _wallRunTimer;
    private Vector2 _lastWallNormal;
    private float _reattachTimer;
    private float _roll;
    private float _landDipTimer;
    private float _landDipStrength;
    private const float LandDipDuration = 0.22f;

    private float _bobPhase;
    private float _recoil;
    private float _shakeTimer;
    private float _shakeStrength;
    private Vector2 _shakeOffset;        // re-rolled once per Update, so camera and bullets agree
    private readonly Random _random = new();

    public float HorizontalSpeed => _velocity.Length();
    public Vector2 HorizontalVelocity => _velocity;
    // How much the head bob/gun sway should apply: only while feet are on something.
    public float Speed01 => IsGrounded || IsWallRunning ? Math.Clamp(HorizontalSpeed / MoveSpeed, 0f, 1f) : 0f;
    public float BobPhase => _bobPhase;
    public float ViewRoll => _roll;

    public event Action<int>? Damaged;
    public event Action? Died;
    public event Action? Jumped;
    public event Action<float>? Landed;  // impact speed
    public event Action? WallJumped;

    public FpsPlayer() => Health = MaxHealth;

    public Vector3 EyePosition
    {
        get
        {
            float bob = MathF.Sin(_bobPhase) * BobAmount * Speed01;
            float dip = _landDipTimer > 0f
                ? (1f - Easing.OutCubic(1f - _landDipTimer / LandDipDuration)) * _landDipStrength
                : 0f;
            return new Vector3(Position.X, FeetY + EyeHeight + bob - dip, Position.Y);
        }
    }

    public Vector2 FlatForward => new(MathF.Sin(Yaw), -MathF.Cos(Yaw));
    public Vector2 FlatRight => new(MathF.Cos(Yaw), MathF.Sin(Yaw));

    // The camera's actual look direction, including recoil and shake. Bullets
    // use this too, so they always go exactly where the crosshair points.
    public Vector3 ViewForward
    {
        get
        {
            float yaw = Yaw + _shakeOffset.X;
            float pitch = Math.Clamp(Pitch + _recoil + _shakeOffset.Y, -MaxPitch, MaxPitch);
            return new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), -MathF.Cos(pitch) * MathF.Cos(yaw));
        }
    }

    public void Look(float deltaX, float deltaY)
    {
        Yaw += deltaX * MouseSensitivity;
        Pitch = Math.Clamp(Pitch - deltaY * MouseSensitivity, -MaxPitch, MaxPitch);
    }

    // Same as the platformer: jump is an EDGE (KeyDown), remembered briefly so
    // a press slightly before landing — or slightly before touching a wall —
    // isn't lost.
    public void BufferJump() => _jumpBufferTimer = JumpBufferTime;

    // moveInput: X = strafe (+1 right), Y = forward (+1 forward).
    public void Update(float dt, Vector2 moveInput, bool sprint, FpsLevel level)
    {
        _jumpBufferTimer = MathF.Max(0f, _jumpBufferTimer - dt);
        _reattachTimer = MathF.Max(0f, _reattachTimer - dt);
        _landDipTimer = MathF.Max(0f, _landDipTimer - dt);

        if (IsWallRunning)
            UpdateWallRun(dt, moveInput, level);
        else
            UpdateWalkOrAir(dt, moveInput, sprint, level);

        Vector2 before = Position;
        Position = level.MoveAndCollide(Position, _velocity * dt, Radius);
        // If a wall stopped us, drop the blocked part of the velocity — otherwise
        // speed from a rocket jump would stay "stored" while pressed into a wall.
        if (dt > 0f && !IsWallRunning)
        {
            Vector2 actual = (Position - before) / dt;
            if (MathF.Abs(actual.X) < MathF.Abs(_velocity.X) * 0.5f) _velocity.X = actual.X;
            if (MathF.Abs(actual.Y) < MathF.Abs(_velocity.Y) * 0.5f) _velocity.Y = actual.Y;
        }

        UpdateVertical(dt, level);

        if (!IsGrounded && !IsWallRunning)
            TryStartWallRun(moveInput, level);

        // Camera roll eases toward its target (tilted while wall-running).
        float targetRoll = 0f;
        if (IsWallRunning)
        {
            // Wall on our right (normal pointing left of us) → tilt left, away from it.
            float wallSide = -Vector2.Dot(_wallNormal, FlatRight);
            targetRoll = -MathF.Sign(wallSide) * WallRunCameraRoll;
        }
        _roll += (targetRoll - _roll) * (1f - MathF.Exp(-10f * dt));

        float bobRate = IsWallRunning ? 1.4f : 1f; // quicker steps on the wall
        _bobPhase += dt * BobFrequency * bobRate * (IsGrounded || IsWallRunning ? HorizontalSpeed / MoveSpeed : 0f);

        _recoil *= MathF.Exp(-RecoilRecovery * dt);
        _shakeTimer = MathF.Max(0f, _shakeTimer - dt);
        _shakeOffset = _shakeTimer > 0f
            ? new Vector2((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f) * _shakeStrength
            : Vector2.Zero;
    }

    private void UpdateWalkOrAir(float dt, Vector2 moveInput, bool sprint, FpsLevel level)
    {
        Vector2 targetVelocity = Vector2.Zero;
        if (moveInput != Vector2.Zero)
        {
            moveInput = Vector2.Normalize(moveInput);
            float speed = MoveSpeed * (sprint ? SprintMultiplier : 1f);
            targetVelocity = (FlatRight * moveInput.X + FlatForward * moveInput.Y) * speed;
        }

        if (IsGrounded)
        {
            float rate = moveInput == Vector2.Zero ? Deceleration : Acceleration;
            _velocity = MoveTowards(_velocity, targetVelocity, rate * dt);
        }
        else if (moveInput != Vector2.Zero)
        {
            // In the air you can steer, but letting go of the keys keeps your
            // momentum — that's what makes wall-jumps and rocket jumps carry.
            // Only steer toward the input direction if we're not already
            // faster than that (so steering never bleeds off a speed boost).
            float currentSpeed = _velocity.Length();
            var steered = MoveTowards(_velocity, targetVelocity, AirAcceleration * dt);
            if (currentSpeed > targetVelocity.Length() && steered.Length() > 0f)
                steered = Vector2.Normalize(steered) * currentSpeed;
            _velocity = steered;
        }

        // Coyote time + buffered jump, exactly like the platformer.
        _coyoteTimer = IsGrounded ? CoyoteTime : MathF.Max(0f, _coyoteTimer - dt);
        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
        {
            VerticalVelocity = JumpVelocity;
            IsGrounded = false;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
            Jumped?.Invoke();
        }
    }

    private void UpdateVertical(float dt, FpsLevel level)
    {
        if (!IsWallRunning)
            VerticalVelocity -= Gravity * dt;
        else
            VerticalVelocity -= WallRunGravity * dt;

        FeetY += VerticalVelocity * dt;

        float ceiling = FpsLevel.WallHeight - BodyHeight;
        if (FeetY > ceiling)
        {
            FeetY = ceiling;
            VerticalVelocity = MathF.Min(VerticalVelocity, 0f);
        }

        if (FeetY <= 0f)
        {
            float impact = -VerticalVelocity;
            FeetY = 0f;
            VerticalVelocity = 0f;
            if (!IsGrounded)
            {
                IsGrounded = true;
                IsWallRunning = false;
                // A camera dip proportional to how hard we hit the floor.
                _landDipStrength = Math.Clamp(impact * 0.012f, 0.01f, 0.12f);
                _landDipTimer = LandDipDuration;
                Landed?.Invoke(impact);
            }
        }
        else
        {
            IsGrounded = false;
        }
    }

    // Latch onto a wall if: airborne, moving fast enough, holding forward,
    // a wall is right beside us, and we're running ALONG it rather than into
    // it (that would be a head-on bump, not a wall-run).
    private void TryStartWallRun(Vector2 moveInput, FpsLevel level)
    {
        if (moveInput.Y <= 0f || HorizontalSpeed < WallRunMinSpeed || FeetY < 0.1f) return;

        Vector2 moveDir = Vector2.Normalize(_velocity);
        foreach (var normal in FpsLevel.WallNormals)
        {
            if (!level.HasWallBeside(Position, Radius, normal)) continue;
            if (MathF.Abs(Vector2.Dot(moveDir, normal)) > 0.7f) continue;   // too head-on
            if (_reattachTimer > 0f && normal == _lastWallNormal) continue;  // just jumped off this one

            IsWallRunning = true;
            _wallNormal = normal;
            _wallRunTimer = WallRunDuration;
            // REPLACE vertical speed (not add to it): latching on mid-jump would
            // otherwise keep the jump's full upward speed under the much weaker
            // wall-run gravity and rocket you into the ceiling.
            VerticalVelocity = WallRunStartLift;
            return;
        }
    }

    private void UpdateWallRun(float dt, Vector2 moveInput, FpsLevel level)
    {
        _wallRunTimer -= dt;

        // The direction along the wall: perpendicular to its normal, picking
        // whichever of the two matches the way we're already going.
        var tangent = new Vector2(-_wallNormal.Y, _wallNormal.X);
        if (Vector2.Dot(tangent, _velocity) < 0f) tangent = -tangent;

        float speed = MoveTowardsScalar(HorizontalSpeed, WallRunSpeed, 12f * dt);
        // A small push INTO the wall keeps us glued to it; collision stops it
        // from actually moving us.
        _velocity = tangent * speed - _wallNormal * 0.5f;

        bool stillTouching = level.HasWallBeside(Position, Radius, _wallNormal);
        bool pressingAway = Vector2.Dot(FlatRight * moveInput.X + FlatForward * moveInput.Y, _wallNormal) > 0.5f;

        if (_jumpBufferTimer > 0f)
        {
            // Wall jump: keep most of the run speed, get shoved out from the
            // wall and up. Chain these between two walls to climb a corridor.
            _velocity = tangent * speed * 0.9f + _wallNormal * WallJumpPush;
            VerticalVelocity = WallJumpUp;
            _jumpBufferTimer = 0f;
            EndWallRun();
            WallJumped?.Invoke();
            return;
        }

        if (_wallRunTimer <= 0f || !stillTouching || moveInput.Y <= 0f || pressingAway)
        {
            _velocity = tangent * speed;
            EndWallRun();
        }
    }

    private void EndWallRun()
    {
        IsWallRunning = false;
        _lastWallNormal = _wallNormal;
        _reattachTimer = WallReattachDelay;
    }

    // Knockback/launch from explosions and big hits. This is all a rocket
    // jump is: an explosion under your feet adds a big upward impulse.
    public void AddImpulse(Vector3 impulse)
    {
        _velocity += new Vector2(impulse.X, impulse.Z);
        VerticalVelocity += impulse.Y;
        if (impulse.Y > 0f && IsGrounded)
        {
            IsGrounded = false;
            FeetY += 0.01f; // lift off so UpdateVertical doesn't immediately re-land us
        }
        if (IsWallRunning) EndWallRun();
    }

    public void AddRecoil(float radians) => _recoil += radians;

    public void Shake(float strength, float duration)
    {
        // Don't let a small shake cut short a bigger one already playing.
        if (_shakeTimer > 0f && strength < _shakeStrength) return;
        _shakeStrength = strength;
        _shakeTimer = duration;
    }

    public void Heal(int amount) => Health = Math.Min(MaxHealth, Health + amount);

    public void TakeDamage(int amount)
    {
        if (!IsAlive || amount <= 0) return;
        Health = Math.Max(0, Health - amount);
        Damaged?.Invoke(amount);
        if (!IsAlive) Died?.Invoke();
    }

    private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDelta)
    {
        Vector2 diff = target - current;
        float length = diff.Length();
        return length <= maxDelta || length == 0f ? target : current + diff / length * maxDelta;
    }

    private static float MoveTowardsScalar(float current, float target, float maxDelta) =>
        MathF.Abs(target - current) <= maxDelta ? target : current + MathF.Sign(target - current) * maxDelta;
}
