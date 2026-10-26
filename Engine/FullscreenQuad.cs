using Silk.NET.OpenGL;

namespace Engine;

// Two triangles covering the whole screen, already in clip space (-1..1), so
// no projection is needed. Drawing this with a shader that samples a
// RenderTarget's texture runs that shader once per screen pixel — the basic
// building block of every compositing/post-processing pass.
public class FullscreenQuad : IDisposable
{
    private static readonly float[] Vertices =
    {
        // position    // texcoord
        -1f,  1f,      0f, 1f,
         1f,  1f,      1f, 1f,
         1f, -1f,      1f, 0f,
        -1f, -1f,      0f, 0f,
    };

    private static readonly uint[] Indices = { 0, 1, 2, 2, 3, 0 };

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public unsafe FullscreenQuad(GL gl)
    {
        _gl = gl;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* v = Vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* i = Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        const uint stride = 4 * sizeof(float);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
    }

    // The caller binds the shader + input textures first; this just draws.
    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)Indices.Length, DrawElementsType.UnsignedInt, null);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
