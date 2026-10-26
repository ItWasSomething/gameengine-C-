using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Rendering;

// CPU-side geometry: what a mesh *is*, before it's uploaded. Kept separate
// from the GPU object so it can be generated, inspected (e.g. for bounds or
// picking) and tested without an OpenGL context.
//
// Vertex layout (8 floats): position xyz, normal xyz, uv
public sealed class MeshData
{
    public const int FloatsPerVertex = 8;

    public readonly List<float> Vertices = new();
    public readonly List<uint> Indices = new();

    public int VertexCount => Vertices.Count / FloatsPerVertex;

    public uint AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Vertices.Add(position.X); Vertices.Add(position.Y); Vertices.Add(position.Z);
        Vertices.Add(normal.X); Vertices.Add(normal.Y); Vertices.Add(normal.Z);
        Vertices.Add(uv.X); Vertices.Add(uv.Y);
        return (uint)(VertexCount - 1);
    }

    public void AddTriangle(uint a, uint b, uint c) { Indices.Add(a); Indices.Add(b); Indices.Add(c); }

    public Vector3 GetPosition(int vertex)
    {
        int i = vertex * FloatsPerVertex;
        return new Vector3(Vertices[i], Vertices[i + 1], Vertices[i + 2]);
    }

    // Smallest box containing every vertex.
    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < VertexCount; v++)
        {
            var p = GetPosition(v);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}

// GPU-side geometry: the same VAO/VBO/EBO trio as Sprite and Fps.Mesh, now
// with normals (needed for lighting — a normal is "which way this bit of
// surface faces").
public sealed class Mesh3D : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;
    private readonly uint _indexCount;

    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public MeshData Data { get; }

    public unsafe Mesh3D(GL gl, MeshData data)
    {
        _gl = gl;
        Data = data;
        (BoundsMin, BoundsMax) = data.ComputeBounds();
        var vertices = data.Vertices.ToArray();
        var indices = data.Indices.ToArray();
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

        const uint stride = MeshData.FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);

        _gl.BindVertexArray(0);
    }

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
