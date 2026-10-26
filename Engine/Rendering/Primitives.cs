using System.Numerics;
using Engine.Core;

namespace Engine.Rendering;

// Generates the built-in shapes, centered on the origin. Triangles wind
// counter-clockwise when seen from outside (in ordinary right-handed math).
// The camera view is mirrored on Z to match Unity's left-handed look, which
// flips that to clockwise on screen, so Renderer3D sets GL's front face to
// clockwise and back-face culling still skips the insides.
public static class Primitives
{
    // Same sizes as Unity's built-ins, so scenes and tutorials translate
    // directly: Cube/Sphere 1 m, Capsule/Cylinder 2 m tall with radius
    // 0.5, Plane 10x10 m, Quad 1x1 m facing -Z.
    public static MeshData Create(PrimitiveType type) => type switch
    {
        PrimitiveType.Cube => Cube(),
        PrimitiveType.Sphere => Sphere(0.5f, 32, 16),
        PrimitiveType.Capsule => Capsule(0.5f, 2f, 32, 8),
        PrimitiveType.Cylinder => Cylinder(0.5f, 2f, 32),
        PrimitiveType.Plane => Plane(10f),
        PrimitiveType.Quad => Quad(),
        _ => Cube(),
    };

    // Shapes with no inside: drawn from both sides (no back-face culling).
    public static bool IsTwoSided(PrimitiveType type) => type is PrimitiveType.Plane or PrimitiveType.Quad;

    // A 1x1 square in the XY plane, facing -Z (toward a camera behind it,
    // which looks along +Z) — Unity's Quad.
    public static MeshData Quad()
    {
        var m = new MeshData();
        var n = -Vector3.UnitZ;
        uint a = m.AddVertex(new(0.5f, -0.5f, 0), n, new(0, 0));
        uint b = m.AddVertex(new(-0.5f, -0.5f, 0), n, new(1, 0));
        uint c = m.AddVertex(new(-0.5f, 0.5f, 0), n, new(1, 1));
        uint d = m.AddVertex(new(0.5f, 0.5f, 0), n, new(0, 1));
        m.AddTriangle(a, b, c);
        m.AddTriangle(c, d, a);
        return m;
    }

    // Each face gets its own 4 vertices (not shared with neighbors) because
    // a cube's corners need a different normal per face to look sharp.
    public static MeshData Cube()
    {
        var m = new MeshData();
        void Face(Vector3 normal, Vector3 up)
        {
            var right = Vector3.Cross(up, normal);
            var c = normal * 0.5f;
            var r = right * 0.5f;
            var u = up * 0.5f;
            uint a = m.AddVertex(c - r - u, normal, new(0, 0));
            uint b = m.AddVertex(c + r - u, normal, new(1, 0));
            uint d = m.AddVertex(c + r + u, normal, new(1, 1));
            uint e = m.AddVertex(c - r + u, normal, new(0, 1));
            m.AddTriangle(a, b, d);
            m.AddTriangle(d, e, a);
        }
        Face(Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitZ);
        return m;
    }

    // A flat size x size square on the XZ plane, facing up.
    public static MeshData Plane(float size)
    {
        var m = new MeshData();
        var n = Vector3.UnitY;
        uint a = m.AddVertex(new Vector3(-0.5f, 0, 0.5f) * size, n, new(0, 0));
        uint b = m.AddVertex(new Vector3(0.5f, 0, 0.5f) * size, n, new(1, 0));
        uint c = m.AddVertex(new Vector3(0.5f, 0, -0.5f) * size, n, new(1, 1));
        uint d = m.AddVertex(new Vector3(-0.5f, 0, -0.5f) * size, n, new(0, 1));
        m.AddTriangle(a, b, c);
        m.AddTriangle(c, d, a);
        return m;
    }

    // Latitude/longitude ("UV") sphere. On a sphere centered at the origin
    // the normal is simply the normalized position.
    public static MeshData Sphere(float radius, int segments, int rings)
    {
        var m = new MeshData();
        for (int y = 0; y <= rings; y++)
        {
            float v = (float)y / rings;
            float phi = v * MathF.PI;                       // 0 at top, π at bottom
            for (int x = 0; x <= segments; x++)
            {
                float u = (float)x / segments;
                float theta = u * MathF.Tau;
                var n = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), -MathF.Sin(phi) * MathF.Sin(theta));
                m.AddVertex(n * radius, n, new(u, 1 - v));
            }
        }
        AddGridIndices(m, 0, segments, rings);
        return m;
    }

    public static MeshData Cylinder(float radius, float height, int segments)
    {
        var m = new MeshData();
        float h = height / 2;
        // Side wall: 2 rings of vertices.
        for (int y = 0; y <= 1; y++)
            for (int x = 0; x <= segments; x++)
            {
                float u = (float)x / segments;
                float t = u * MathF.Tau;
                var n = new Vector3(MathF.Cos(t), 0, -MathF.Sin(t));
                m.AddVertex(n * radius + new Vector3(0, y == 0 ? h : -h, 0), n, new(u, 1 - y));
            }
        AddGridIndices(m, 0, segments, 1);
        AddCap(m, radius, h, segments, up: true);
        AddCap(m, radius, -h, segments, up: false);
        return m;
    }

    // A cylinder with hemispheres on both ends. Total height 1 when
    // height = 1 and radius = 0.5 means the caps make up the whole thing —
    // so the "straight" part has length height - 2·radius (may be zero).
    public static MeshData Capsule(float radius, float height, int segments, int capRings)
    {
        var m = new MeshData();
        float half = MathF.Max(0, height / 2 - radius);
        // Build it like a sphere whose top half is lifted by `half` and
        // bottom half lowered by `half`. One extra ring makes the seam in the
        // middle (two vertex rings at the equator, one per half).
        int rows = capRings * 2 + 1;
        for (int y = 0; y <= rows; y++)
        {
            int ring = y <= capRings ? y : y - 1;
            float phi = (float)ring / (capRings * 2) * MathF.PI;
            float offset = y <= capRings ? half : -half;
            for (int x = 0; x <= segments; x++)
            {
                float u = (float)x / segments;
                float theta = u * MathF.Tau;
                var n = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), -MathF.Sin(phi) * MathF.Sin(theta));
                m.AddVertex(n * radius + new Vector3(0, offset, 0), n, new(u, 1 - (float)y / rows));
            }
        }
        AddGridIndices(m, 0, segments, rows);
        return m;
    }

    // Connects a (segments+1) x (rows+1) grid of vertices into quads, where
    // row 0 is the top. Shared by sphere, cylinder wall and capsule.
    private static void AddGridIndices(MeshData m, uint start, int segments, int rows)
    {
        uint stride = (uint)segments + 1;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < segments; x++)
            {
                uint a = start + (uint)y * stride + (uint)x;   // top-left
                uint b = a + stride;                              // bottom-left
                m.AddTriangle(a, b, b + 1);
                m.AddTriangle(b + 1, a + 1, a);
            }
    }

    private static void AddCap(MeshData m, float radius, float y, int segments, bool up)
    {
        var n = up ? Vector3.UnitY : -Vector3.UnitY;
        uint center = m.AddVertex(new(0, y, 0), n, new(0.5f, 0.5f));
        uint first = (uint)m.VertexCount;
        for (int x = 0; x <= segments; x++)
        {
            float t = (float)x / segments * MathF.Tau;
            float c = MathF.Cos(t), s = MathF.Sin(t);
            m.AddVertex(new(c * radius, y, -s * radius), n, new(0.5f + c * 0.5f, 0.5f + s * 0.5f));
        }
        for (uint x = 0; x < segments; x++)
        {
            if (up) m.AddTriangle(center, first + x, first + x + 1);
            else m.AddTriangle(center, first + x + 1, first + x);
        }
    }
}
