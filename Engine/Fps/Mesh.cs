using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Fps;

// A chunk of static 3D geometry uploaded to the GPU once and drawn every
// frame with a single draw call. Same VAO/VBO/EBO trio as Sprite, but each
// vertex carries a 3D position, a UV and a brightness ("shade") value, and
// the vertex data is built by a MeshBuilder instead of being a fixed quad.
//
// Vertex layout (6 floats): x, y, z,  u, v,  shade
public sealed class Mesh : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _indexCount;

    public unsafe Mesh(GL gl, float[] vertices, uint[] indices)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* v = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* i = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        const uint stride = 6 * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);

        _gl.BindVertexArray(0);
    }

    // The caller has already bound the shader, its uniforms and the texture —
    // a Mesh is just geometry.
    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, null);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}

// Collects quads on the CPU, then turns them into one Mesh. Building the
// whole level into a few big meshes (instead of drawing each wall face
// separately) is what keeps this to a handful of draw calls per frame.
public sealed class MeshBuilder
{
    private readonly List<float> _vertices = new();
    private readonly List<uint> _indices = new();

    public bool IsEmpty => _indices.Count == 0;

    // Corners in order bottom-left, bottom-right, top-right, top-left as
    // seen from the side the quad is meant to be viewed from.
    public void AddQuad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector2 uvMin, Vector2 uvMax, float shade)
    {
        uint start = (uint)(_vertices.Count / 6);

        AddVertex(bl, new Vector2(uvMin.X, uvMin.Y), shade);
        AddVertex(br, new Vector2(uvMax.X, uvMin.Y), shade);
        AddVertex(tr, new Vector2(uvMax.X, uvMax.Y), shade);
        AddVertex(tl, new Vector2(uvMin.X, uvMax.Y), shade);

        // Two triangles sharing the bl-tr diagonal, exactly like Sprite's quad.
        _indices.AddRange(new[] { start, start + 1, start + 2, start + 2, start + 3, start });
    }

    private void AddVertex(Vector3 p, Vector2 uv, float shade)
    {
        _vertices.Add(p.X); _vertices.Add(p.Y); _vertices.Add(p.Z);
        _vertices.Add(uv.X); _vertices.Add(uv.Y);
        _vertices.Add(shade);
    }

    public Mesh Build(GL gl) => new(gl, _vertices.ToArray(), _indices.ToArray());
}
