using System.Numerics;
using Engine.Core;
using Silk.NET.OpenGL;

namespace Engine.Rendering;

// Immediate-mode line drawing: call Line()/WireBox()/etc. any time during a
// frame, then Flush() draws everything collected and empties the list. Used
// for the editor grid, gizmos and selection boxes, and by the physics
// visualizer (contacts, velocities, colliders).
//
// Two layers: "depth-tested" lines hide behind geometry like real objects;
// "overlay" lines draw on top of everything (gizmo handles you must always be
// able to grab).
public sealed class DebugDraw : IDisposable
{
    private const int FloatsPerVertex = 7;   // xyz rgba

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;
    private readonly List<float> _tested = new();
    private readonly List<float> _overlay = new();
    private int _capacityFloats;

    public unsafe DebugDraw(GL gl)
    {
        _gl = gl;
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _shader = new Shader(gl, Path.Combine(dir, "lines.vert"), Path.Combine(dir, "lines.frag"));

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.BindVertexArray(0);
    }

    public void Line(Vector3 a, Vector3 b, Color color, bool overlay = false)
    {
        var list = overlay ? _overlay : _tested;
        Add(list, a, color);
        Add(list, b, color);
    }

    private static void Add(List<float> list, Vector3 p, Color c)
    {
        list.Add(p.X); list.Add(p.Y); list.Add(p.Z);
        list.Add(c.r); list.Add(c.g); list.Add(c.b); list.Add(c.a);
    }

    // A box given in some node's local space (e.g. a unit cube's -0.5..0.5)
    // and placed in the world by `transform`.
    public void WireBox(Vector3 min, Vector3 max, Matrix4x4 transform, Color color, bool overlay = false)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            c[i] = Vector3.Transform(local, transform);
        }
        // The 12 edges: pairs of corners that differ in exactly one bit.
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0)
                    Line(c[i], c[i | bit], color, overlay);
    }

    public void Circle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, Color color, bool overlay = false, int segments = 32)
    {
        Vector3 prev = center + axisA * radius;
        for (int i = 1; i <= segments; i++)
        {
            float t = i / (float)segments * MathF.Tau;
            var p = center + (axisA * MathF.Cos(t) + axisB * MathF.Sin(t)) * radius;
            Line(prev, p, color, overlay);
            prev = p;
        }
    }

    // Three circles — reads as a sphere from any angle.
    public void WireSphere(Vector3 center, float radius, Color color, bool overlay = false)
    {
        Circle(center, Vector3.UnitX, Vector3.UnitY, radius, color, overlay);
        Circle(center, Vector3.UnitX, Vector3.UnitZ, radius, color, overlay);
        Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, color, overlay);
    }

    public void Arrow(Vector3 from, Vector3 to, Color color, bool overlay = false)
    {
        Line(from, to, color, overlay);
        var dir = to - from;
        float len = dir.Length();
        if (len < 1e-5f) return;
        dir /= len;
        var side = Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX);
        side = Vector3.Normalize(side);
        var up = Vector3.Cross(side, dir);
        float head = MathF.Min(0.25f, len * 0.3f);
        var b = to - dir * head;
        Line(to, b + side * head * 0.4f, color, overlay);
        Line(to, b - side * head * 0.4f, color, overlay);
        Line(to, b + up * head * 0.4f, color, overlay);
        Line(to, b - up * head * 0.4f, color, overlay);
    }

    public void Flush(Matrix4x4 view, Matrix4x4 projection)
    {
        _shader.Use();
        _shader.SetUniform("uViewProjection", view * projection);
        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        DrawList(_tested);
        _gl.Disable(EnableCap.DepthTest);
        DrawList(_overlay);
        _gl.DepthMask(true);

        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
        _tested.Clear();
        _overlay.Clear();
    }

    public void Clear()
    {
        _tested.Clear();
        _overlay.Clear();
    }

    private unsafe void DrawList(List<float> list)
    {
        if (list.Count == 0) return;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
        // Grow the GPU buffer only when needed; otherwise overwrite in place.
        if (list.Count > _capacityFloats)
        {
            _capacityFloats = list.Count * 2;
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_capacityFloats * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        }
        fixed (float* p = span)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(list.Count * sizeof(float)), p);
        _gl.DrawArrays(Silk.NET.OpenGL.PrimitiveType.Lines, 0, (uint)(list.Count / FloatsPerVertex));
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
    }
}
