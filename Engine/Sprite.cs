using Silk.NET.OpenGL;
using Silk.NET.Maths;
using System.Numerics;

namespace Engine;

// A textured quad: two triangles sharing an edge, drawn via an index buffer
// so the shared vertices aren't duplicated. This is the first real "sprite"
// in the engine — Episode 4's tilemaps will just be many of these.
public class Sprite : IDisposable
{
    // Unit quad centered on the origin; Draw() scales/moves it via uniforms
    // rather than baking a position into the vertex data, so one Sprite
    // instance's GPU buffers can be reused to draw at any position/size.
    private static readonly float[] Vertices =
    {
        // position       // texcoord
        -0.5f,  0.5f,     0f, 1f, // top-left
         0.5f,  0.5f,     1f, 1f, // top-right
         0.5f, -0.5f,     1f, 0f, // bottom-right
        -0.5f, -0.5f,     0f, 0f, // bottom-left
    };

    private static readonly uint[] Indices = { 0, 1, 2, 2, 3, 0 };

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Texture _texture;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public unsafe Sprite(GL gl, Shader shader, Texture texture)
    {
        _gl = gl;
        _shader = shader;
        _texture = texture;

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

    public unsafe void Draw(Vector2D<float> position, Vector2D<float> size, Matrix4x4 projection, Vector4? tint = null, float rotation = 0f)
    {
        _shader.Use();
        _shader.SetUniform("uPosition", position);
        _shader.SetUniform("uSize", size);
        _shader.SetUniform("uProjection", projection);
        _shader.SetUniform("uTint", tint ?? new Vector4(1f, 1f, 1f, 1f));
        _shader.SetUniform("uRotation", rotation);

        _texture.Bind(TextureUnit.Texture0);
        _shader.SetUniform("uTexture", 0);

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
