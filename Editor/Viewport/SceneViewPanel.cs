using System.Numerics;
using Engine.Core;
using Engine.Rendering;
using ImGuiNET;
using Silk.NET.OpenGL;
using Color = Engine.Core.Color;
using Debug = Engine.Core.Debug;
using Framebuffer = Engine.Rendering.Framebuffer;

namespace Editor.Viewport;

// Unity's Scene view: the world seen through the editor's own camera, with
// a grid, icons for lights/cameras, the selection outline and transform
// gizmos. Each frame it renders into its own Framebuffer and shows that
// texture as an ImGui image — the 3D view "lives" inside a UI panel.
//
// Mouse input over the image drives (in priority order) the gizmo, the
// camera, and click-to-select. Prefabs can be dropped in from the Project.
public sealed class SceneViewPanel : IDisposable
{
    public const string Title = "SCENE###Scene";

    private readonly GL _gl;
    private readonly EditorContext _ctx;
    private readonly Renderer3D _renderer;
    private readonly DebugDraw _debug;
    private readonly Framebuffer _output;
    private readonly EditorCamera _camera = new();
    private readonly TransformGizmo _gizmo = new();
    private bool _showGrid = true;
    private float _fpsSmoothed = 60;

    public bool IsFocused { get; private set; }

    // Extra overlay drawing hooked in by other editor systems (e.g. the
    // physics visualizer), run after the scene renders.
    public event Action<DebugDraw, CameraView>? DrawOverlays;
    // 2D drawing on top of the rendered image (labels, speech bubbles):
    // (draw list, image top-left, image size, camera).
    public event Action<ImDrawListPtr, Vector2, Vector2, CameraView>? DrawScreenOverlays;

    public SceneViewPanel(GL gl, EditorContext ctx, Renderer3D renderer)
    {
        _gl = gl;
        _ctx = ctx;
        _renderer = renderer;
        _debug = new DebugDraw(gl);
        _output = new Framebuffer(gl, 16, 16, hdr: false);
    }

    public void Draw(float dt)
    {
        _fpsSmoothed += (1f / MathF.Max(dt, 1e-4f) - _fpsSmoothed) * 0.05f;
        _camera.Update(dt);

        // Side padding = the card gap, so the image sits inside the card.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(EditorTheme.Gap + 1, 0));
        bool visible = EditorTheme.BeginPanel(Title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (!visible) { IsFocused = false; ImGui.End(); return; }
        IsFocused = ImGui.IsWindowFocused();

        DrawToolbar();

        var size = Vector2.Max(ImGui.GetContentRegionAvail() - new Vector2(0, EditorTheme.Gap + 1), new Vector2(16));
        var imageMin = ImGui.GetCursorScreenPos();

        // An invisible button covering the image gives hover/click state for
        // all three mouse buttons; the image is drawn after rendering.
        ImGui.InvisibleButton("##sceneview", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsMouseClicked(ImGuiMouseButton.Middle)))
            ImGui.SetWindowFocus();

        _output.EnsureSize((int)size.X, (int)size.Y);
        var view = _camera.GetView(_output.Aspect);
        _ctx.SpawnPoint = SnapToGrid(_camera.Position + _camera.Forward * MathF.Min(_camera.FocusDistance, 8f));

        PrefabDropTarget(size, imageMin, view);
        HandleInput(dt, size, imageMin, view, hovered, active);

        // ---- Render ----
        _renderer.Render(_ctx.Scene, view, _output);
        if (_showGrid) DrawGrid(view);
        DrawIcons();
        foreach (var line in Debug.PendingLines) _debug.Line(line.Start, line.End, line.Color);
        if (_ctx.Selected is { } selected && selected)
        {
            DrawSelection(selected);
            _gizmo.Draw(_debug, selected.transform, view);
        }
        DrawOverlays?.Invoke(_debug, view);
        _debug.Flush(view.View, view.Projection);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Flip V: GL textures start at the bottom row, ImGui images at the top.
        ImGui.GetWindowDrawList().AddImageRounded((IntPtr)_output.ColorTexture, imageMin, imageMin + size, new Vector2(0, 1), new Vector2(1, 0),
            0xFFFFFFFF, 0);
        DrawScreenOverlays?.Invoke(ImGui.GetWindowDrawList(), imageMin, size, view);
        DrawStatsOverlay(imageMin, size);
        EditorTheme.DrawFocusOutline(imageMin, imageMin + size);
        ImGui.End();
    }

    private static Vector3 SnapToGrid(Vector3 p) =>
        new(MathF.Round(p.X * 2) / 2, MathF.Max(0, MathF.Round(p.Y * 2) / 2), MathF.Round(p.Z * 2) / 2);

    private void DrawToolbar()
    {
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(6, 4));
        // A dark pill behind the tool buttons, so they read as one control.
        var pill = ImGui.GetCursorScreenPos() - new Vector2(3, 2);
        ImGui.GetWindowDrawList().AddRectFilled(pill, pill + new Vector2(24 * 3 + 4 + 6, 28), Widgets.U32(EditorTheme.WithAlpha(EditorTheme.Bg0, 0.85f)));
        if (Widgets.IconButton("##move", Icon.Move, _gizmo.Mode == GizmoMode.Move, "Move (W)", null, 24)) _gizmo.Mode = GizmoMode.Move;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton("##rotate", Icon.Rotate, _gizmo.Mode == GizmoMode.Rotate, "Rotate (E)", null, 24)) _gizmo.Mode = GizmoMode.Rotate;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton("##scale", Icon.Scale, _gizmo.Mode == GizmoMode.Scale, "Scale (R)", null, 24)) _gizmo.Mode = GizmoMode.Scale;
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.Checkbox("Snap", ref _gizmo.Snap);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Snap moves to 0.25 m, rotations to 15°, scale to 0.1 (hold Ctrl to toggle)");
        ImGui.SameLine();
        ImGui.Checkbox("Grid", ref _showGrid);
        ImGui.SameLine();
        ImGui.TextDisabled($"   fly speed {_camera.FlySpeed:0.#} m/s");
        ImGui.Dummy(new Vector2(0, 2));
    }

    private void DrawStatsOverlay(Vector2 min, Vector2 size)
    {
        var s = _renderer.Stats;
        string text = $"{_fpsSmoothed:0} fps   {s.DrawCalls} draws   {s.Triangles:N0} tris";
        var textSize = ImGui.CalcTextSize(text);
        var pos = new Vector2(min.X + size.X - textSize.X - 10, min.Y + 8);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos - new Vector2(6, 3), pos + textSize + new Vector2(6, 3), Widgets.U32(new Vector4(0, 0, 0, 0.6f)));
        dl.AddText(pos, Widgets.U32(EditorTheme.Text), text);
    }

    // Drop a .prefab from the Project panel: it lands where the mouse ray
    // meets the ground (y = 0), or in front of the camera.
    private void PrefabDropTarget(Vector2 size, Vector2 imageMin, CameraView view)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        if (DragPayload.Accept(DragPayload.AssetType) && DragPayload.AssetPath is { } asset &&
            asset.EndsWith(SceneSerializer.PrefabExtension, StringComparison.OrdinalIgnoreCase))
        {
            var ray = ViewportMath.ScreenRay(ImGui.GetMousePos() - imageMin, size, view);
            var position = ViewportMath.RayPlane(ray, Vector3.Zero, Vector3.UnitY, out var hit) && Vector3.Distance(hit, view.Position) < 200
                ? hit : _ctx.SpawnPoint;
            _ctx.InstantiatePrefab(asset, null, position);
            DragPayload.AssetPath = null;
        }
        ImGui.EndDragDropTarget();
    }

    // ---- Input ------------------------------------------------------------

    private void HandleInput(float dt, Vector2 size, Vector2 imageMin, CameraView view, bool hovered, bool active)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - imageMin;
        var ray = ViewportMath.ScreenRay(mouse, size, view);

        // Camera: only while the drag started on the view (active).
        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            _camera.Look(io.MouseDelta);
            var move = new Vector3(Key(ImGuiKey.D) - Key(ImGuiKey.A), Key(ImGuiKey.E) - Key(ImGuiKey.Q), Key(ImGuiKey.W) - Key(ImGuiKey.S));
            _camera.Fly(move, io.KeyShift, dt);
            if (io.MouseWheel != 0) _camera.AdjustFlySpeed(io.MouseWheel);
            return;
        }
        if (active && io.KeyAlt && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _camera.Orbit(io.MouseDelta);
            return;
        }
        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            _camera.Pan(io.MouseDelta);
            return;
        }
        if (hovered && io.MouseWheel != 0) _camera.Zoom(io.MouseWheel);

        if (hovered && !io.WantTextInput && !io.KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W, false)) _gizmo.Mode = GizmoMode.Move;
            if (ImGui.IsKeyPressed(ImGuiKey.E, false)) _gizmo.Mode = GizmoMode.Rotate;
            if (ImGui.IsKeyPressed(ImGuiKey.R, false)) _gizmo.Mode = GizmoMode.Scale;
        }
        if ((hovered || IsFocused) && ImGui.IsKeyPressed(ImGuiKey.F, false) && !io.WantTextInput)
            FrameSelected();

        // Gizmo first: it gets the click if the mouse is over a handle.
        bool gizmoOwnsMouse = false;
        if (_ctx.Selected is { } selected && selected && (hovered || _gizmo.IsDragging))
        {
            bool snapBefore = _gizmo.Snap;
            if (io.KeyCtrl) _gizmo.Snap = !snapBefore;
            gizmoOwnsMouse = _gizmo.Update(_ctx, selected.transform, ray, mouse, size, view,
                ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hovered && !io.KeyAlt,
                ImGui.IsMouseDown(ImGuiMouseButton.Left));
            _gizmo.Snap = snapBefore;
        }

        // Otherwise a left click selects whatever is under the cursor.
        if (!gizmoOwnsMouse && hovered && !io.KeyAlt && ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
            ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Length() < 4)
        {
            var hit = ViewportMath.Pick(_ctx.Scene, ray, type =>
            {
                var mesh = _renderer.GetMesh(type);
                return (mesh.BoundsMin, mesh.BoundsMax);
            });
            // Clicking part of a prefab selects the whole instance first
            // (click again to get the part), like Unity.
            if (hit != null && PrefabUtility.GetInstanceRoot(hit) is { } root && root != hit &&
                _ctx.Selected != root && !(_ctx.Selected?.transform.IsChildOf(root.transform) ?? false))
                hit = root;
            _ctx.Select(hit);
        }
    }

    private static float Key(ImGuiKey key) => ImGui.IsKeyDown(key) ? 1f : 0f;

    public void FrameSelected()
    {
        if (_ctx.Selected is not { } go) return;
        var s = go.transform.lossyScale;
        float radius = go.GetComponent<MeshFilter>() != null ? MathF.Max(s.X, MathF.Max(s.Y, s.Z)) * 0.6f : 0.5f;
        _camera.FocusOn(go.transform.position, radius);
    }

    // ---- Overlays -------------------------------------------------------------

    // A ground grid that fades with distance, recentered on the camera so it
    // seems infinite. Every 10th line is brighter; the X and Z axes are drawn
    // in their axis colors.
    private void DrawGrid(CameraView view)
    {
        const int extent = 40;
        const float segment = 2f;
        var cam = view.Position;
        float cx = MathF.Round(cam.X), cz = MathF.Round(cam.Z);
        float heightFade = Math.Clamp(1.2f - MathF.Abs(cam.Y) / 120f, 0.3f, 1f);

        for (int i = -extent; i <= extent; i++)
        {
            float x = cx + i, z = cz + i;
            GridLine(new Vector3(x, 0, cz - extent), Vector3.UnitZ, x, cam, extent, segment, heightFade, alongZ: true);
            GridLine(new Vector3(cx - extent, 0, z), Vector3.UnitX, z, cam, extent, segment, heightFade, alongZ: false);
        }
    }

    private void GridLine(Vector3 start, Vector3 dir, float coordinate, Vector3 cam, int extent, float segment,
        float heightFade, bool alongZ)
    {
        bool axis = MathF.Abs(coordinate) < 0.01f;
        bool major = MathF.Abs(coordinate % 10) < 0.01f;
        // A line running along Z at X = 0 *is* the Z axis.
        var baseColor = axis ? (alongZ ? EditorTheme.AxisZ : EditorTheme.AxisX) : Vector4.One;
        float baseAlpha = axis ? 0.8f : major ? 0.22f : 0.09f;

        int steps = (int)(extent * 2 / segment);
        for (int s = 0; s < steps; s++)
        {
            var a = start + dir * (s * segment);
            var b = a + dir * segment;
            float alpha = baseAlpha * Fade((a + b) / 2) * heightFade;
            if (alpha > 0.003f) _debug.Line(a, b, new Color(baseColor.X, baseColor.Y, baseColor.Z, alpha));
        }

        float Fade(Vector3 p)
        {
            float d = new Vector2(p.X - cam.X, p.Z - cam.Z).Length() / extent;
            return Math.Clamp(1f - d * d, 0f, 1f);
        }
    }

    // Lights and cameras have no mesh, so draw a wireframe stand-in for
    // each — otherwise they'd be invisible (and unclickable) in the editor.
    private void DrawIcons()
    {
        var lightColor = new Color(1f, 0.85f, 0.35f, 0.9f);
        foreach (var go in _ctx.Scene.AllGameObjects(includeInactive: false))
        {
            var p = go.transform.position;
            bool selected = go == _ctx.Selected;
            if (go.GetComponent<Light>() is { } light)
            {
                if (light.type == LightType.Directional)
                {
                    _debug.WireSphere(p, 0.25f, lightColor);
                    _debug.Arrow(p, p + light.direction * 1.5f, lightColor);
                }
                else
                {
                    _debug.WireSphere(p, 0.2f, lightColor);
                    if (selected) _debug.WireSphere(p, light.range, lightColor.WithAlpha(0.25f));
                }
            }
            if (go.GetComponent<Camera>() is { } cam) DrawCameraFrustum(cam, selected);
        }
    }

    private void DrawCameraFrustum(Camera cam, bool selected)
    {
        var color = selected ? new Color(0.75f, 0.6f, 1f) : new Color(0.7f, 0.55f, 1f, 0.6f);
        float depth = 1.2f;
        float h = MathF.Tan(cam.fieldOfView * Mathf.Deg2Rad / 2) * depth;
        float w = h * 16f / 9f;
        var m = Matrix4x4.CreateFromQuaternion(cam.transform.rotation) * Matrix4x4.CreateTranslation(cam.transform.position);
        var o = cam.transform.position;
        // Unity cameras look along +Z.
        var corners = new[]
        {
            Vector3.Transform(new Vector3(-w, -h, depth), m), Vector3.Transform(new Vector3(w, -h, depth), m),
            Vector3.Transform(new Vector3(w, h, depth), m), Vector3.Transform(new Vector3(-w, h, depth), m),
        };
        for (int i = 0; i < 4; i++)
        {
            _debug.Line(o, corners[i], color);
            _debug.Line(corners[i], corners[(i + 1) % 4], color);
        }
        var top = Vector3.Transform(new Vector3(0, h * 1.5f, depth), m);
        _debug.Line(corners[2], top, color);
        _debug.Line(corners[3], top, color);
    }

    private void DrawSelection(GameObject go)
    {
        var accent = new Color(EditorTheme.Accent.X, EditorTheme.Accent.Y, EditorTheme.Accent.Z, 0.95f);
        foreach (var t in go.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = _renderer.GetMesh(t.mesh);
            var pad = (mesh.BoundsMax - mesh.BoundsMin) * 0.02f;
            bool self = t.gameObject == go;
            var color = self ? accent : accent.WithAlpha(0.35f);
            _debug.WireBox(mesh.BoundsMin - pad, mesh.BoundsMax + pad, t.transform.localToWorldMatrix, color);
            // A faint always-on-top copy, so the selection reads through walls.
            if (self)
                _debug.WireBox(mesh.BoundsMin - pad, mesh.BoundsMax + pad, t.transform.localToWorldMatrix, accent.WithAlpha(0.18f), overlay: true);
        }
    }

    public void Dispose()
    {
        _debug.Dispose();
        _output.Dispose();
    }
}
