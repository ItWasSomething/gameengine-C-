using System.Numerics;
using Silk.NET.Maths;

namespace Engine;

// A soft circular point light in world pixels. Plain data — Lighting does
// the drawing — so gameplay code can own a Light and just move it around
// (e.g. the player's light follows the player every frame).
public class PointLight2D
{
    public Vector2D<float> Position;
    public float Radius = 200f;
    public Vector3 Color = new(1f, 1f, 1f);
    // Can go above 1: the light map is a float texture, so a strong light
    // over-brightens what it touches instead of clamping at "normal".
    public float Intensity = 1f;

    // 0 = steady, ~0.1-0.2 = candle/lantern wobble. Applied by Lighting.Update.
    public float Flicker = 0f;

    // Transient lights (hit flashes) fade out over Lifetime and are then
    // removed; <= 0 means a permanent light.
    public float Lifetime = 0f;

    internal float Age;
    internal float FlickerPhase = Random.Shared.NextSingle() * 100f;
    internal float CurrentIntensity;
}
