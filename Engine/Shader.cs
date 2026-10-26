using Silk.NET.OpenGL;
using Silk.NET.Maths;
using System.Numerics;

namespace Engine;

// Wraps a compiled + linked GL shader program. Extracted out of GameWindow
// in Episode 3 so both the triangle demo and Sprite can reuse it instead of
// duplicating compile/link boilerplate.
public class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;

    // glGetUniformLocation is a string lookup inside the driver; the 3D
    // renderer sets dozens of uniforms per object per frame, so remember
    // each answer the first time it's asked.
    private readonly Dictionary<string, int> _locations = new();

    public Shader(GL gl, string vertexPath, string fragmentPath)
    {
        _gl = gl;

        uint vertex = Compile(ShaderType.VertexShader, File.ReadAllText(vertexPath));
        uint fragment = Compile(ShaderType.FragmentShader, File.ReadAllText(fragmentPath));

        _handle = _gl.CreateProgram();
        _gl.AttachShader(_handle, vertex);
        _gl.AttachShader(_handle, fragment);
        _gl.LinkProgram(_handle);

        _gl.GetProgram(_handle, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
            throw new Exception($"Shader program failed to link: {_gl.GetProgramInfoLog(_handle)}");

        // The program keeps its own copy once linked; the individual shader
        // objects aren't needed after that.
        _gl.DetachShader(_handle, vertex);
        _gl.DetachShader(_handle, fragment);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint id = _gl.CreateShader(type);
        _gl.ShaderSource(id, source);
        _gl.CompileShader(id);

        _gl.GetShader(id, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
            throw new Exception($"{type} failed to compile: {_gl.GetShaderInfoLog(id)}");

        return id;
    }

    public void Use() => _gl.UseProgram(_handle);

    private int Location(string name)
    {
        if (!_locations.TryGetValue(name, out int location))
        {
            location = _gl.GetUniformLocation(_handle, name);
            _locations[name] = location;
        }
        return location;
    }

    public void SetUniform(string name, bool value) => SetUniform(name, value ? 1 : 0);

    public void SetUniform(string name, int value)
    {
        _gl.Uniform1(Location(name), value);
    }

    public void SetUniform(string name, float value)
    {
        _gl.Uniform1(Location(name), value);
    }

    public void SetUniform(string name, Vector2D<float> value)
    {
        _gl.Uniform2(Location(name), value.X, value.Y);
    }

    public void SetUniform(string name, Vector3 value)
    {
        _gl.Uniform3(Location(name), value.X, value.Y, value.Z);
    }

    public void SetUniform(string name, Vector4 value)
    {
        _gl.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);
    }

    // System.Numerics.Matrix4x4 (not Silk.NET.Maths) purely because it's a
    // plain sequential struct of 16 floats, so `&value` is a valid float*
    // straight into GL — no separate float[16] copy needed.
    public unsafe void SetUniform(string name, Matrix4x4 value)
    {
        int location = Location(name);
        _gl.UniformMatrix4(location, 1, false, (float*)&value);
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
