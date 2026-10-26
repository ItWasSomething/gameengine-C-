using System.Numerics;

namespace Engine.Fps;

// Small shared intersection tests. Everything that can be shot or blown up
// (enemies, the player) is modelled as an upright cylinder, so these two
// functions cover bullets, projectiles and explosions.
public static class Geometry
{
    // Ray vs an upright cylinder standing at `center` (on the ground plane)
    // between heights yMin..yMax. Solved in 2D first (ray vs circle), then the
    // hit height is checked. `direction` must be normalized; returns the
    // distance along the ray.
    public static bool RayCylinder(Vector3 origin, Vector3 direction, Vector2 center, float radius,
        float yMin, float yMax, out float distance)
    {
        distance = 0f;
        Vector2 o = new(origin.X - center.X, origin.Z - center.Y);
        Vector2 d = new(direction.X, direction.Z);

        // |o + d·t|² = r²  →  a·t² + 2b·t + c = 0
        float a = Vector2.Dot(d, d);
        if (a < 1e-8f) return false;            // shooting straight up/down
        float b = Vector2.Dot(o, d);
        float c = Vector2.Dot(o, o) - radius * radius;
        float discriminant = b * b - a * c;
        if (discriminant < 0f) return false;    // misses the circle entirely

        float root = MathF.Sqrt(discriminant);
        float t = (-b - root) / a;              // near side
        if (t < 0f) t = (-b + root) / a;        // we're inside: use the far side
        if (t < 0f) return false;               // behind us

        float y = origin.Y + direction.Y * t;
        if (y < yMin || y > yMax) return false;

        distance = t;
        return true;
    }

    // Shortest distance from a point to an upright cylinder's central axis
    // segment — "how close was this explosion to that body?".
    public static float DistanceToCylinderAxis(Vector3 point, Vector2 center, float yMin, float yMax)
    {
        float y = Math.Clamp(point.Y, yMin, yMax);
        return Vector3.Distance(point, new Vector3(center.X, y, center.Y));
    }
}
