using System.Numerics;
using Engine.Rendering;
using Engine.Core;
using Color = Engine.Core.Color;

namespace Editor.Viewport;

public enum GizmoMode { Move, Rotate, Scale }

// The draggable handles drawn on the selected node. Each handle is built
// from a simple idea:
//
//   Move   — project the mouse ray onto the axis line: "how far along X is
//            the point nearest my cursor?" The change in that distance since
//            the drag started is the change in position.
//   Rotate — hit the mouse ray against the plane perpendicular to the axis;
//            the angle swept around the center since the drag started is the
//            rotation.
//   Scale  — like Move, but the *ratio* of distances becomes the scale factor.
//
// Handles are sized by distance to the camera, so they stay the same size on
// screen however far away the node is.
public sealed class TransformGizmo
{
    private static readonly Vector3[] Axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
    private static readonly Color[] AxisColors =
    {
        ToColor(EditorTheme.AxisX), ToColor(EditorTheme.AxisY), ToColor(EditorTheme.AxisZ),
    };
    private static readonly Color HoverColor = new(1f, 0.9f, 0.3f);
    private const float HoverPixels = 9f;
    private const int RingSegments = 48;

    public GizmoMode Mode = GizmoMode.Move;
    public bool Snap;

    private int _hoverAxis = -1;
    private int _dragAxis = -1;
    private Vector3 _dragStartPosition;
    private Quaternion _dragStartRotation;
    private Vector3 _dragStartScale;
    private float _dragStartParam;
    private Vector3 _dragStartVector;
    private Vector3 _dragAxisWorld;

    public bool IsDragging => _dragAxis >= 0;
    public bool IsHovering => _hoverAxis >= 0;

    private static Color ToColor(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    private static float HandleLength(Transform node, CameraView view) =>
        Vector3.Distance(view.Position, node.position) * 0.16f;

    // Scale handles follow the node's own axes; move/rotate use world axes.
    private Vector3 AxisDirection(Transform node, int axis) =>
        Mode == GizmoMode.Scale ? Vector3.Normalize(Vector3.Transform(Axes[axis], node.rotation)) : Axes[axis];

    // Called every frame the viewport is hovered. Returns true while the
    // gizmo owns the mouse (so a click doesn't also re-select).
    public bool Update(EditorContext ctx, Transform node, Ray ray, Vector2 mouse, Vector2 size, CameraView view,
        bool mousePressed, bool mouseDown)
    {
        var origin = node.position;
        float length = HandleLength(node, view);

        if (!IsDragging)
        {
            _hoverAxis = FindHoveredAxis(node, mouse, size, view, origin, length);
            if (mousePressed && _hoverAxis >= 0) BeginDrag(ctx, node, ray, origin);
            return _hoverAxis >= 0;
        }

        if (!mouseDown)
        {
            _dragAxis = -1;
            return true;
        }

        ContinueDrag(node, ray, origin);
        return true;
    }

    private int FindHoveredAxis(Transform node, Vector2 mouse, Vector2 size, CameraView view, Vector3 origin, float length)
    {
        int best = -1;
        float bestDistance = HoverPixels;
        for (int axis = 0; axis < 3; axis++)
        {
            float d = float.MaxValue;
            if (Mode == GizmoMode.Rotate)
            {
                // Distance to the ring, sampled as a polyline on screen.
                var (u, v) = Perpendiculars(Axes[axis]);
                Vector2? prev = null;
                for (int i = 0; i <= RingSegments; i++)
                {
                    float t = i / (float)RingSegments * MathF.Tau;
                    var p = origin + (u * MathF.Cos(t) + v * MathF.Sin(t)) * length;
                    if (!ViewportMath.WorldToScreen(p, size, view, out var s)) { prev = null; continue; }
                    if (prev is { } pv) d = MathF.Min(d, ViewportMath.DistanceToSegment(mouse, pv, s));
                    prev = s;
                }
            }
            else
            {
                if (ViewportMath.WorldToScreen(origin, size, view, out var a) &&
                    ViewportMath.WorldToScreen(origin + AxisDirection(node, axis) * length, size, view, out var b))
                    d = ViewportMath.DistanceToSegment(mouse, a, b);
            }
            if (d < bestDistance) { bestDistance = d; best = axis; }
        }
        return best;
    }

    private void BeginDrag(EditorContext ctx, Transform node, Ray ray, Vector3 origin)
    {
        ctx.RecordUndo();
        _dragAxis = _hoverAxis;
        _dragAxisWorld = AxisDirection(node, _dragAxis);
        _dragStartPosition = node.position;
        _dragStartRotation = node.rotation;
        _dragStartScale = node.localScale;
        _dragStartParam = ViewportMath.ClosestParameterOnLine(ray, origin, _dragAxisWorld);
        if (Mode == GizmoMode.Rotate && ViewportMath.RayPlane(ray, origin, _dragAxisWorld, out var hit))
            _dragStartVector = hit - origin;
    }

    private void ContinueDrag(Transform node, Ray ray, Vector3 origin)
    {
        switch (Mode)
        {
            case GizmoMode.Move:
            {
                float t = ViewportMath.ClosestParameterOnLine(ray, _dragStartPosition, _dragAxisWorld);
                float delta = t - _dragStartParam;
                var target = _dragStartPosition + _dragAxisWorld * delta;
                if (Snap) target = SnapAxis(target, _dragAxis, 0.25f);
                node.position = target;
                break;
            }
            case GizmoMode.Rotate:
            {
                if (!ViewportMath.RayPlane(ray, origin, _dragAxisWorld, out var hit)) break;
                var current = hit - origin;
                float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(_dragStartVector, current), _dragAxisWorld),
                                          Vector3.Dot(_dragStartVector, current));
                if (Snap) angle = MathF.Round(angle / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps
                var global = Quaternion.Concatenate(_dragStartRotation, Quaternion.CreateFromAxisAngle(_dragAxisWorld, angle));
                node.rotation = global;
                break;
            }
            case GizmoMode.Scale:
            {
                float t = ViewportMath.ClosestParameterOnLine(ray, _dragStartPosition, _dragAxisWorld);
                if (MathF.Abs(_dragStartParam) < 1e-4f) break;
                float factor = MathF.Max(t / _dragStartParam, 0.01f);
                var s = _dragStartScale;
                float value = Component(s, _dragAxis) * factor;
                if (Snap) value = MathF.Max(0.1f, MathF.Round(value * 10) / 10);
                node.localScale = WithComponent(s, _dragAxis, value);
                break;
            }
        }
    }

    public void Draw(DebugDraw draw, Transform node, CameraView view)
    {
        var origin = node.position;
        float length = HandleLength(node, view);
        int highlighted = IsDragging ? _dragAxis : _hoverAxis;

        for (int axis = 0; axis < 3; axis++)
        {
            var color = axis == highlighted ? HoverColor : AxisColors[axis];
            var dir = AxisDirection(node, axis);
            switch (Mode)
            {
                case GizmoMode.Move:
                    draw.Arrow(origin, origin + dir * length, color, overlay: true);
                    break;
                case GizmoMode.Rotate:
                    var (u, v) = Perpendiculars(dir);
                    draw.Circle(origin, u, v, length, color, overlay: true, RingSegments);
                    break;
                case GizmoMode.Scale:
                    var end = origin + dir * length;
                    draw.Line(origin, end, color, overlay: true);
                    float box = length * 0.06f;
                    draw.WireBox(new Vector3(-box), new Vector3(box), Matrix4x4.CreateTranslation(end), color, overlay: true);
                    break;
            }
        }

        // While rotating, show the swept angle as a line from the center.
        if (IsDragging && Mode == GizmoMode.Rotate && _dragStartVector.LengthSquared() > 1e-8f)
            draw.Line(origin, origin + Vector3.Normalize(_dragStartVector) * length, HoverColor, overlay: true);
    }

    // Two unit vectors perpendicular to `axis` and to each other.
    private static (Vector3, Vector3) Perpendiculars(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var v = Vector3.Cross(axis, u);
        return (u, v);
    }

    private static Vector3 SnapAxis(Vector3 p, int axis, float step) =>
        WithComponent(p, axis, MathF.Round(Component(p, axis) / step) * step);

    private static float Component(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    private static Vector3 WithComponent(Vector3 v, int i, float value)
    {
        if (i == 0) v.X = value; else if (i == 1) v.Y = value; else v.Z = value;
        return v;
    }
}
