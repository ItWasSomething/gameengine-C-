using Silk.NET.Maths;
using System.Numerics;

namespace Engine;

internal struct Particle
{
    public Vector2D<float> Position;
    public Vector2D<float> Velocity;
    public float Life;
    public float MaxLife;
    public float StartSize;
    public float EndSize;
    public Vector3 Color;
    public float StartRotation;
    public float EndRotation;
}

// A small fixed-capacity particle pool, drawn as tinted/faded copies of one
// shared soft-dot sprite. Good enough for one-shot impact/dust bursts; not a
// general-purpose VFX system (no sub-emitters, curves, or GPU instancing).
public class ParticleSystem
{
    private readonly Particle[] _particles;
    private int _count;

    // Exponential velocity decay per second — most dust/impact puffs read
    // better with drag slowing them to a stop than with gravity pulling
    // them down, so gravity defaults off.
    public float Gravity = 0f;
    public float Drag = 2.5f;

    public ParticleSystem(int capacity = 128)
    {
        _particles = new Particle[capacity];
    }

    // baseDirection points the center of the emission cone (doesn't need to
    // be normalized); spreadDegrees is the full cone width around it.
    // startRotation/endRotation (radians) let a particle spin or sweep
    // through an arc over its life — most emitters just leave these at 0.
    public void Emit(
        Vector2D<float> position, int count, Vector2D<float> baseDirection, float spreadDegrees,
        float minSpeed, float maxSpeed, float minLife, float maxLife,
        float startSize, float endSize, Vector3 color,
        float startRotation = 0f, float endRotation = 0f)
    {
        var rng = Random.Shared;
        float baseAngle = MathF.Atan2(baseDirection.Y, baseDirection.X);
        float spreadRadians = spreadDegrees * MathF.PI / 180f;

        for (int n = 0; n < count && _count < _particles.Length; n++)
        {
            float angle = baseAngle + ((float)rng.NextDouble() - 0.5f) * spreadRadians;
            float speed = minSpeed + (float)rng.NextDouble() * (maxSpeed - minSpeed);
            float life = minLife + (float)rng.NextDouble() * (maxLife - minLife);

            _particles[_count++] = new Particle
            {
                Position = position,
                Velocity = new Vector2D<float>(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed),
                Life = life,
                MaxLife = life,
                StartSize = startSize,
                EndSize = endSize,
                Color = color,
                StartRotation = startRotation,
                EndRotation = endRotation,
            };
        }
    }

    public void Update(float dt)
    {
        float dragFactor = MathF.Exp(-Drag * dt);

        for (int i = _count - 1; i >= 0; i--)
        {
            ref Particle p = ref _particles[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                // Swap-remove: overwrite the dead slot with the last live
                // particle instead of shifting the whole array down.
                _particles[i] = _particles[_count - 1];
                _count--;
                continue;
            }

            p.Velocity = new Vector2D<float>(p.Velocity.X * dragFactor, (p.Velocity.Y - Gravity * dt) * dragFactor);
            p.Position = new Vector2D<float>(p.Position.X + p.Velocity.X * dt, p.Position.Y + p.Velocity.Y * dt);
        }
    }

    public void Draw(Sprite sprite, Matrix4x4 projection)
    {
        for (int i = 0; i < _count; i++)
        {
            ref Particle p = ref _particles[i];
            float t = 1f - p.Life / p.MaxLife; // 0 = just spawned, 1 = about to die
            float size = p.StartSize + (p.EndSize - p.StartSize) * t;
            float alpha = 1f - t;
            float rotation = p.StartRotation + (p.EndRotation - p.StartRotation) * t;

            sprite.Draw(p.Position, new Vector2D<float>(size, size), projection,
                new Vector4(p.Color.X, p.Color.Y, p.Color.Z, alpha), rotation);
        }
    }
}
