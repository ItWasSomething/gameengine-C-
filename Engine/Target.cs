using Silk.NET.Maths;

namespace Engine;

// A stationary melee practice target: no AI, no movement — it just sits
// there, absorbs hits, flashes, and eventually breaks. GameWindow owns the
// list of these and is what actually tests Player's attack hitbox against
// them; Target itself knows nothing about attacks, only about taking damage.
public class Target
{
    public Vector2D<float> Position; // bottom-left corner, world pixels
    public Vector2D<float> Size = new(48f, 48f);
    public int Health = 3;
    public bool IsAlive => Health > 0;

    public Aabb Bounds => new(Position, Size);
    public Vector2D<float> Center => Bounds.Center;

    // Hit flash: counts down from HitFlashDuration to 0 after each hit;
    // GameWindow reads HitFlashT (1 = just hit, 0 = faded out) to brighten
    // the sprite's tint via the same uTint uniform particles use.
    public float HitFlashDuration = 0.12f;
    private float _hitFlashTimer;
    public float HitFlashT => HitFlashDuration > 0f ? _hitFlashTimer / HitFlashDuration : 0f;

    // Same squash-and-recover shape as Player's landing squash, but the
    // opposite proportions: an impact compresses ALONG the hit axis (thin
    // horizontally, since hits come from the side) and bulges perpendicular
    // to it, rather than landing's flat-and-wide. Purely visual — GameWindow
    // reads VisualScale only for the draw size, never the collision Bounds.
    public Vector2D<float> VisualScale { get; private set; } = new(1f, 1f);
    public float HitSquashDuration = 0.18f;
    public float HitSquashX = 0.6f;
    public float HitSquashY = 1.35f;
    private float _squashTimer;

    public event Action? Hit;
    public event Action? Died;

    public void TakeDamage(int amount = 1)
    {
        if (!IsAlive) return;

        Health -= amount;
        _hitFlashTimer = HitFlashDuration;
        _squashTimer = HitSquashDuration;
        Hit?.Invoke();

        if (!IsAlive) Died?.Invoke();
    }

    public void Update(float dt)
    {
        _hitFlashTimer = MathF.Max(0f, _hitFlashTimer - dt);

        _squashTimer = MathF.Max(0f, _squashTimer - dt);
        if (_squashTimer > 0f)
        {
            float t = 1f - _squashTimer / HitSquashDuration; // 0 at impact -> 1 when settled
            float eased = Easing.OutCubic(t);
            float scaleX = HitSquashX + (1f - HitSquashX) * eased;
            float scaleY = HitSquashY + (1f - HitSquashY) * eased;
            VisualScale = new Vector2D<float>(scaleX, scaleY);
        }
        else
        {
            VisualScale = new Vector2D<float>(1f, 1f);
        }
    }
}
