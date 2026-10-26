using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Engine.Fps;

// Draws camera-facing sprites in the 3D world (see billboard.vert). Owns one
// unit quad on the GPU and redraws it at any position/size — the exact same
// pattern as Sprite, just with a 3D-aware vertex shader.
//
// Per-frame uniforms (uView, uProjection, uCameraRight, fog, ...) are set
// once by the caller on the shader; Draw only sets what changes per sprite.
public sealed class BillboardRenderer : IDisposable
{
    private static readonly float[] Vertices =
    {
        // position       // texcoord
        -0.5f,  0.5f,     0f, 1f,
         0.5f,  0.5f,     1f, 1f,
         0.5f, -0.5f,     1f, 0f,
        -0.5f, -0.5f,     0f, 0f,
    };
    private static readonly uint[] Indices = { 0, 1, 2, 2, 3, 0 };

    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;

    public Shader Shader { get; }

    public unsafe BillboardRenderer(GL gl, Shader shader)
    {
        _gl = gl;
        Shader = shader;

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

    // `basePosition` is the bottom-centre of the sprite (where its feet are).
    public unsafe void Draw(Texture texture, Vector3 basePosition, Vector2 size, Vector4 tint)
    {
        Shader.SetUniform("uBillboardBase", basePosition);
        Shader.SetUniform("uSize", new Vector2D<float>(size.X, size.Y));
        Shader.SetUniform("uTint", tint);
        texture.Bind(TextureUnit.Texture0);

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
