using Silk.NET.OpenGL;

namespace Engine;

// An offscreen image we can draw into instead of the window: a framebuffer
// object (FBO) with one color texture attached. Once something is rendered
// into it, ColorTexture can be sampled like any other texture — which is the
// whole trick behind lighting and post-processing. Draw the world "somewhere
// else" first, then combine/filter those images into the final frame.
public class RenderTarget : IDisposable
{
    private readonly GL _gl;
    private readonly uint _framebuffer;
    private readonly uint _colorTexture;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public unsafe RenderTarget(GL gl, int width, int height)
    {
        _gl = gl;

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        // Linear (not nearest like sprite textures): these get sampled at
        // different resolutions and blurred later, where smoothness matters.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _framebuffer = _gl.GenFramebuffer();
        Resize(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Framebuffer incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Reallocates the texture's storage; the FBO attachment stays valid
    // because it points at the texture object, not at its old pixels.
    public unsafe void Resize(int width, int height)
    {
        Width = Math.Max(1, width);   // minimizing the window reports 0x0
        Height = Math.Max(1, height);

        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        // RGBA16F = half-float per channel instead of 8-bit 0..1. Colors can
        // go *above* 1.0 here without clamping — a light can be "brighter
        // than white", which is what later bloom will look for.
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.Float, null);
    }

    // Subsequent draws land in this target instead of the window.
    public void BindAsTarget()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void BindTexture(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_framebuffer);
        _gl.DeleteTexture(_colorTexture);
    }
}
