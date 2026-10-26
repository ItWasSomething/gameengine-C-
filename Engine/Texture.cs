using Silk.NET.OpenGL;
using StbImageSharp;

namespace Engine;

// Loads a PNG/JPG off disk into a GL texture. This is what turns a plain
// textured quad (Sprite) into something that actually looks like art.
public class Texture : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;

    public unsafe Texture(GL gl, string path)
    {
        _gl = gl;
        _handle = _gl.GenTexture();
        Bind();

        // OpenGL expects row 0 at the bottom of the image; PNGs store row 0
        // at the top. Flipping on load means UV (0,0) is the bottom-left,
        // matching the convention every OpenGL tutorial assumes.
        StbImage.stbi_set_flip_vertically_on_load(1);

        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        fixed (byte* pixels = image.Data)
            Upload(pixels, image.Width, image.Height);

        // Nearest-neighbor filtering keeps pixel art crisp instead of blurry.
        SetSampling(repeat: false);
    }

    // Builds a texture straight from RGBA bytes generated in code (see
    // Fps/ProceduralTextures) instead of a file — row 0 is the BOTTOM row,
    // same as GL, so no flip is needed. `repeat` is for surfaces like floors
    // whose UVs run past 1.0 so the image tiles instead of smearing its edge.
    public unsafe Texture(GL gl, int width, int height, byte[] rgba, bool repeat = false)
    {
        _gl = gl;
        _handle = _gl.GenTexture();
        Bind();

        fixed (byte* pixels = rgba)
            Upload(pixels, width, height);

        SetSampling(repeat);
    }

    private unsafe void Upload(byte* pixels, int width, int height)
    {
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba,
            (uint)width,
            (uint)height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixels);
    }

    private void SetSampling(bool repeat)
    {
        var wrap = repeat ? GLEnum.Repeat : GLEnum.ClampToEdge;

        // Up close (magnified) stay nearest-neighbor for crisp pixels. Tiled
        // 3D surfaces also get mipmapped minification: a floor stretching off
        // into the distance squeezes many texels into one screen pixel, and
        // without mipmaps that shimmers/sparkles badly as the camera moves.
        var minFilter = repeat ? GLEnum.LinearMipmapLinear : GLEnum.Nearest;

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);

        _gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, _handle);
    }

    public void Dispose() => _gl.DeleteTexture(_handle);
}
