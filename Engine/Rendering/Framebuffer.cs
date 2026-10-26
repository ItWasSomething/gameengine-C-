using Silk.NET.OpenGL;

namespace Engine.Rendering;

// An offscreen image to render into, like RenderTarget, but with a depth
// buffer — 3D needs one so nearer surfaces hide farther ones regardless of
// draw order. The editor shows the final ColorTexture inside an ImGui panel;
// that's how the game view lives "inside" the editor UI.
public sealed class Framebuffer : IDisposable
{
    private readonly GL _gl;
    private readonly bool _hdr;
    private readonly uint _fbo;
    private readonly uint _depth;

    public uint ColorTexture { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float Aspect => (float)Width / Height;

    // hdr = RGBA16F, so lighting can go above 1.0 before tone mapping
    // squeezes it back into displayable range. Otherwise plain RGBA8.
    public Framebuffer(GL gl, int width, int height, bool hdr)
    {
        _gl = gl;
        _hdr = hdr;

        ColorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        // A renderbuffer (not a texture) for depth: we never sample it, only
        // test against it, and renderbuffers are the cheaper choice for that.
        _depth = _gl.GenRenderbuffer();

        _fbo = _gl.GenFramebuffer();
        Allocate(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ColorTexture, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _depth);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Framebuffer incomplete: {status}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Cheap to call every frame: only reallocates when the size changed.
    public void EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Width || height != Height) Allocate(width, height);
    }

    private unsafe void Allocate(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
        if (_hdr)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)Width, (uint)Height, 0,
                PixelFormat.Rgba, PixelType.Float, null);
        else
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)Width, (uint)Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, null);

        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)Width, (uint)Height);
    }

    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    // Copies this framebuffer's depth into another of the same size. Used
    // after tone mapping, so editor overlays (grid, gizmos) drawn into the
    // final image still hide behind scene geometry.
    public void CopyDepthTo(Framebuffer other)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, other._fbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, other.Width, other.Height,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, other._fbo);
    }

    // Copies the color image to the window (framebuffer 0).
    public void BlitToScreen(int screenWidth, int screenHeight)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, screenWidth, screenHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Reads back the finished image (bottom row first) — used to save
    // screenshots.
    public unsafe byte[] ReadPixels()
    {
        Bind();
        var pixels = new byte[Width * Height * 4];
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        return pixels;
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(ColorTexture);
        _gl.DeleteRenderbuffer(_depth);
    }
}
