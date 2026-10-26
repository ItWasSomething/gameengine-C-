using System.Numerics;
using Engine.Rendering;
using Engine.Core;

namespace Editor.Viewport;

public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float t) => Origin + Direction * t;
}

// The geometry the viewport needs for mouse interaction: turning a mouse
// position into a 3D ray, projecting 3D points back to the screen, and
// ray-vs-shape tests for click-selection.
public static class ViewportMath
{
    // Mouse pixel (origin top-left of the viewport) → world-space ray.
    // Un-project two points at the near and far planes and connect them.
    public static Ray ScreenRay(Vector2 pixel, Vector2 size, CameraView view)
    {
        float x = pixel.X / size.X * 2f - 1f;
        float y = 1f - pixel.Y / size.Y * 2f;         // screen Y points down, clip Y up
        Matrix4x4.Invert(view.View * view.Projection, out var inverse);
        var near = Vector4.Transform(new Vector4(x, y, -1f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        return new Ray(a, Vector3.Normalize(b - a));
    }

    // World point → viewport pixel. Returns false if behind the camera.
    public static bool WorldToScreen(Vector3 world, Vector2 size, CameraView view, out Vector2 pixel)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), view.View * view.Projection);
        pixel = default;
        if (clip.W <= 1e-4f) return false;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        pixel = new Vector2((ndc.X * 0.5f + 0.5f) * size.X, (0.5f - ndc.Y * 0.5f) * size.Y);
        return true;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    // Slab test: the ray enters the box when it has crossed the near side
    // of all three axis-slabs, and leaves when it crosses the first far side.
    public static bool RayBox(Ray ray, Vector3 min, Vector3 max, out float distance)
    {
        float tMin = 0f, tMax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = Component(ray.Origin, i), d = Component(ray.Direction, i);
            float lo = Component(min, i), hi = Component(max, i);
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) { distance = 0; return false; }
                continue;
            }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) { distance = 0; return false; }
        }
        distance = tMin;
        return true;
    }

    public static bool RaySphere(Ray ray, Vector3 center, float radius, out float distance)
    {
        var oc = ray.Origin - center;
        float b = Vector3.Dot(oc, ray.Direction);
        float c = oc.LengthSquared() - radius * radius;
        float disc = b * b - c;
        distance = 0;
        if (disc < 0) return false;
        distance = -b - MathF.Sqrt(disc);
        if (distance < 0) distance = -b + MathF.Sqrt(disc);
        return distance >= 0;
    }

    // Ray vs an arbitrary transformed box: move the ray into the node's
    // local space (where the box is axis-aligned), test there, then measure
    // the hit distance back in world space so different nodes compare fairly.
    public static bool RayTransformedBox(Ray ray, Matrix4x4 transform, Vector3 min, Vector3 max, out float distance)
    {
        distance = 0;
        if (!Matrix4x4.Invert(transform, out var inverse)) return false;
        var localOrigin = Vector3.Transform(ray.Origin, inverse);
        var localDir = Vector3.TransformNormal(ray.Direction, inverse);
        if (!RayBox(new Ray(localOrigin, localDir), min, max, out float t)) return false;
        var hitWorld = Vector3.Transform(localOrigin + localDir * t, transform);
        distance = Vector3.Distance(ray.Origin, hitWorld);
        return true;
    }

    // Closest approach between the mouse ray and an infinite line (point +
    // t·axis). Returns t along the axis — what a "drag along X" uses.
    public static float ClosestParameterOnLine(Ray ray, Vector3 point, Vector3 axis)
    {
        var w = point - ray.Origin;
        float b = Vector3.Dot(axis, ray.Direction);
        float d = Vector3.Dot(axis, w);
        float e = Vector3.Dot(ray.Direction, w);
        float denom = 1f - b * b;               // both directions are unit length
        if (MathF.Abs(denom) < 1e-6f) return 0f; // looking straight down the axis
        return (b * e - d) / denom;
    }

    public static bool RayPlane(Ray ray, Vector3 point, Vector3 normal, out Vector3 hit)
    {
        hit = default;
        float denom = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(denom) < 1e-6f) return false;
        float t = Vector3.Dot(point - ray.Origin, normal) / denom;
        if (t < 0) return false;
        hit = ray.At(t);
        return true;
    }

    // The nearest GameObject under the mouse. Objects with a mesh use its
    // real bounds; everything else (lights, cameras, empties) gets a small
    // sphere around its position so it's still clickable.
    public static GameObject? Pick(Scene scene, Ray ray, Func<PrimitiveType, (Vector3 Min, Vector3 Max)> bounds)
    {
        GameObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (var go in scene.AllGameObjects(includeInactive: false))
        {
            bool hit;
            float distance;
            if (go.GetComponent<MeshRenderer>() is { enabled: true } && go.GetComponent<MeshFilter>() is { } filter)
            {
                var (min, max) = bounds(filter.mesh);
                // Give flat shapes a little thickness so they can be clicked edge-on.
                min.Y = MathF.Min(min.Y, -0.01f); max.Y = MathF.Max(max.Y, 0.01f);
                min.Z = MathF.Min(min.Z, -0.01f); max.Z = MathF.Max(max.Z, 0.01f);
                hit = RayTransformedBox(ray, go.transform.localToWorldMatrix, min, max, out distance);
            }
            else
            {
                bool hasIcon = go.GetComponent<Light>() != null || go.GetComponent<Camera>() != null;
                hit = RaySphere(ray, go.transform.position, hasIcon ? 0.35f : 0.2f, out distance);
                distance -= 0.2f;    // favor small icons over a mesh just behind them
            }
            if (hit && distance < bestDistance)
            {
                best = go;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static float Component(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
}
