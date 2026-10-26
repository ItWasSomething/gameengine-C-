using System.Numerics;

namespace Engine.Fps;

// The 3D sibling of ParticleSystem: same fixed-capacity pool + swap-remove,
// but particles have a Vector3 position, fall under gravity, and are drawn
// as billboards. Used for bullet-impact sparks/dust and enemy "blood".
public sealed class Particles3D
{
    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Color;
        public float Life;
        public float MaxLife;
        public float StartSize;
        public float Gravity;
    }

    private readonly Particle[] _particles;
    private int _count;
    private readonly Random _random = new();

    public Particles3D(int capacity = 256) => _particles = new Particle[capacity];

    public void Clear() => _count = 0;

    public void Emit(Vector3 position, Vector3 direction, int count, Vector3 color,
        float spread = 0.6f, float minSpeed = 1f, float maxSpeed = 3f,
        float life = 0.4f, float size = 0.06f, float gravity = 6f)
    {
        for (int i = 0; i < count && _count < _particles.Length; i++)
        {
            // Jitter the direction inside a rough cone around `direction`.
            Vector3 jitter = new(Rand(-1f, 1f), Rand(-1f, 1f), Rand(-1f, 1f));
            Vector3 dir = Vector3.Normalize(direction + jitter * spread);
            float lifetime = life * Rand(0.6f, 1.2f);

            _particles[_count++] = new Particle
            {
                Position = position,
                Velocity = dir * Rand(minSpeed, maxSpeed),
                Color = color,
                Life = lifetime,
                MaxLife = lifetime,
                StartSize = size * Rand(0.7f, 1.3f),
                Gravity = gravity,
            };
        }
    }

    public void Update(float dt)
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            ref var p = ref _particles[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _particles[i] = _particles[--_count]; // swap-remove: O(1), order doesn't matter
                continue;
            }

            p.Velocity.Y -= p.Gravity * dt;
            p.Position += p.Velocity * dt;

            // Land on the floor and skid instead of falling through it.
            if (p.Position.Y < 0.01f)
            {
                p.Position.Y = 0.01f;
                p.Velocity *= new Vector3(0.6f, -0.3f, 0.6f);
            }
        }
    }

    public void Draw(BillboardRenderer renderer, Texture texture)
    {
        for (int i = 0; i < _count; i++)
        {
            var p = _particles[i];
            float t = p.Life / p.MaxLife; // 1 = just born, 0 = about to die
            float size = p.StartSize * t;
            // Billboards are positioned by their bottom-centre; particles
            // think in centres, so shift down by half the size.
            var basePosition = p.Position - new Vector3(0f, size * 0.5f, 0f);
            renderer.Draw(texture, basePosition, new Vector2(size, size), new Vector4(p.Color, t));
        }
    }

    private float Rand(float min, float max) => min + (float)_random.NextDouble() * (max - min);
}
