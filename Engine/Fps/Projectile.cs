using System.Numerics;

namespace Engine.Fps;

// Something that actually flies through the world over several frames:
// rockets (player) and acid globs (Spitter). The opposite of hitscan — you
// can see it coming and dodge it, which is why the Spitter uses one.
public sealed class Projectile
{
    public enum Team { Player, Enemy }

    public Team Owner;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Radius = 0.1f;
    public float Gravity;                 // 0 = flies straight
    public float Life = 5f;               // seconds before it fizzles out
    public bool IsAlive = true;

    public int Damage;                    // direct hit
    public float SplashRadius;            // 0 = no explosion
    public int SplashDamage;
    public float SplashKnockback;

    // Look.
    public Vector3 Color = Vector3.One;
    public float Size = 0.2f;
    public float LightRadius = 3f;        // it lights up the walls as it flies past
    public Vector3 TrailColor = Vector3.One;
}

// A point light that exists for a moment (explosion flash) or a single
// frame (attached to a projectile). world.frag adds up to 8 of these.
public struct DynamicLight
{
    public Vector3 Position;
    public Vector3 Color;
    public float Radius;
    public float Life;
    public float MaxLife;

    public readonly float Intensity => MaxLife > 0f ? Life / MaxLife : 1f;
}
