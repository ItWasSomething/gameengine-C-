using System.Numerics;
using Engine.Core;
using Engine.Rendering;
using ImGuiNET;
using Silk.NET.OpenGL;
using Framebuffer = Engine.Rendering.Framebuffer;

namespace Editor.Viewport;

// Unity's Game view: exactly what the player would see — the scene through
// its highest-depth enabled Camera, no gizmos. While playing, this view
// owns the keyboard/mouse (when focused), and Play switches to it.
public sealed class GameViewPanel : IDisposable
{
    public const string Title = "GAME###Game";

    private readonly GL _gl;
    private readonly EditorContext _ctx;
    private readonly Renderer3D _renderer;
    private readonly Framebuffer _output;

    public bool IsFocused { get; private set; }
    public bool IsVisible { get; private set; }

    // Set to bring this tab to the front on the next Draw (used when Play
    // starts). SetNextWindowFocus before Begin is how ImGui wants it done.
    public bool RequestFocus;

    public GameViewPanel(GL gl, EditorContext ctx, Renderer3D renderer)
    {
        _gl = gl;
        _ctx = ctx;
        _renderer = renderer;
        _output = new Framebuffer(gl, 16, 16, hdr: false);
    }

    public void Draw()
    {
        // Side padding = the card gap, so the image sits inside the card.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(EditorTheme.Gap + 1, 0));
        if (RequestFocus) ImGui.SetNextWindowFocus();
        IsVisible = EditorTheme.BeginPanel(Title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (!IsVisible) { IsFocused = false; ImGui.End(); return; }
        IsFocused = ImGui.IsWindowFocused();
        if (IsFocused) RequestFocus = false;   // keep asking until it's really in front

        var size = Vector2.Max(ImGui.GetContentRegionAvail() - new Vector2(0, EditorTheme.Gap + 1), new Vector2(16));
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##gameview", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.SetWindowFocus();
        Engine.Input.SetMousePosition(ImGui.GetMousePos() - min);

        var dl = ImGui.GetWindowDrawList();
        var camera = Camera.FindRenderCamera(_ctx.Scene);
        if (camera == null)
        {
            dl.AddRectFilled(min, min + size, Widgets.U32(new Vector4(0.03f, 0.025f, 0.02f, 1)), 6, ImDrawFlags.RoundCornersBottom);
            const string text = "No cameras rendering";
            var ts = ImGui.CalcTextSize(text);
            dl.AddText(min + (size - ts) / 2, Widgets.U32(EditorTheme.TextDim), text);
        }
        else
        {
            _output.EnsureSize((int)size.X, (int)size.Y);
            _renderer.Render(_ctx.Scene, CameraView.From(camera, _output.Aspect), _output);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            dl.AddImageRounded((IntPtr)_output.ColorTexture, min, min + size, new Vector2(0, 1), new Vector2(1, 0), 0xFFFFFFFF, 6, ImDrawFlags.RoundCornersBottom);
        }

        // A thin frame shows the game is live (orange) or paused (yellow).
        if (_ctx.IsPlaying)
            dl.AddRect(min, min + size, Widgets.U32(_ctx.State == PlayState.Paused ? EditorTheme.Warning : EditorTheme.Accent), 6, ImDrawFlags.RoundCornersBottom, 2);
        else EditorTheme.DrawFocusOutline(min, min + size);
        if (_ctx.IsPlaying && !IsFocused)
        {
            const string hint = "Click to give the game keyboard focus";
            var ts = ImGui.CalcTextSize(hint);
            var p = new Vector2(min.X + (size.X - ts.X) / 2, min.Y + size.Y - ts.Y - 10);
            dl.AddRectFilled(p - new Vector2(8, 4), p + ts + new Vector2(8, 4), Widgets.U32(new Vector4(0, 0, 0, 0.55f)), 4);
            dl.AddText(p, Widgets.U32(EditorTheme.Text), hint);
        }
        ImGui.End();
    }

    public void Dispose() => _output.Dispose();
}
