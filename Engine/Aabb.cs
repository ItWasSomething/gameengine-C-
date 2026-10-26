using Silk.NET.Maths;

namespace Engine;

// Axis-aligned bounding box: Min is the bottom-left corner, in the same
// world-pixel, Y-up space the camera projection uses. Used for both level
// geometry (static platforms) and the player's own collision box.
public readonly struct Aabb
{
    public readonly Vector2D<float> Min;
    public readonly Vector2D<float> Size;

    public Aabb(Vector2D<float> min, Vector2D<float> size)
    {
        Min = min;
        Size = size;
    }

    public Vector2D<float> Max => new(Min.X + Size.X, Min.Y + Size.Y);
    public Vector2D<float> Center => new(Min.X + Size.X / 2f, Min.Y + Size.Y / 2f);

    // Strict inequalities: two boxes exactly touching edge-to-edge (as
    // happens right after collision resolution pushes the player flush
    // against a platform) do NOT count as intersecting.
    public bool Intersects(Aabb other) =>
        Min.X < other.Max.X && Max.X > other.Min.X &&
        Min.Y < other.Max.Y && Max.Y > other.Min.Y;
}
