using System.Numerics;
using Engine.Core;
using Engine.Rendering;

namespace Editor.Viewport;

// The Scene view's own camera — separate from any Camera component, so
// looking around while editing never changes the game.
//
// Controls (Unity's Scene view):
//   Right mouse + drag     look around; WASD / QE fly, Shift = faster,
//                          mouse wheel while held = change fly speed
//   Alt + left drag        orbit around the focus point
//   Middle mouse drag      pan
//   Wheel                  zoom toward the focus point
//   F                      frame the selected object
public sealed class EditorCamera
{
    public Vector3 Position = new(-7, 6, -10);
    public float Pitch = 25f;        // degrees; positive looks down (Unity)
    public float Yaw = 35f;          // degrees around world up
    public float FocusDistance = 13f;
    public float FlySpeed = 6f;
    public float Fov = 60f;

    // Smooth "fly to" target for F (frame selected).
    private Vector3? _focusTarget;
    private float _focusTargetDistance;

    public Quaternion Rotation => Transform.FromEulerDegrees(new Vector3(Pitch, Yaw, 0));
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 FocusPoint => Position + Forward * FocusDistance;

    public EditorCamera() => LookAt(new Vector3(0, 1, 0));

    // Same Z-flip as Camera.worldToCameraMatrix, so the Scene view and the
    // Game view agree on which way is which.
    public CameraView GetView(float aspect)
    {
        var world = Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
        Matrix4x4.Invert(world, out var view);
        view *= Matrix4x4.CreateScale(1, 1, -1);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(Fov * Mathf.Deg2Rad, MathF.Max(aspect, 0.01f), 0.05f, 2000f);
        return new CameraView(view, projection, Position);
    }

    public void LookAt(Vector3 target)
    {
        var dir = target - Position;
        if (dir.LengthSquared() < 1e-8f) return;
        FocusDistance = dir.Length();
        dir = Vector3.Normalize(dir);
        Yaw = MathF.Atan2(dir.X, dir.Z) * Mathf.Rad2Deg;
        Pitch = -MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    public void Look(Vector2 mouseDelta)
    {
        const float degreesPerPixel = 0.2f;
        Yaw += mouseDelta.X * degreesPerPixel;
        Pitch = Math.Clamp(Pitch + mouseDelta.Y * degreesPerPixel, -89f, 89f);
        _focusTarget = null;
    }

    // input: x = right, y = up, z = forward.
    public void Fly(Vector3 input, bool fast, float dt)
    {
        if (input == Vector3.Zero) return;
        var move = Right * input.X + Vector3.UnitY * input.Y + Forward * input.Z;
        Position += Vector3.Normalize(move) * FlySpeed * (fast ? 3f : 1f) * dt;
        _focusTarget = null;
    }

    // Rotates around the focus point instead of around the camera itself.
    public void Orbit(Vector2 mouseDelta)
    {
        var focus = FocusPoint;
        Look(mouseDelta);
        Position = focus - Forward * FocusDistance;
    }

    // Slides sideways/up, scaled by distance so it feels the same zoomed in or out.
    public void Pan(Vector2 mouseDelta)
    {
        float scale = FocusDistance * 0.0015f;
        Position += (-Right * mouseDelta.X + Up * mouseDelta.Y) * scale;
        _focusTarget = null;
    }

    public void Zoom(float wheel)
    {
        var focus = FocusPoint;
        FocusDistance = Math.Clamp(FocusDistance * MathF.Pow(0.88f, wheel), 0.3f, 1000f);
        Position = focus - Forward * FocusDistance;
        _focusTarget = null;
    }

    public void AdjustFlySpeed(float wheel) =>
        FlySpeed = Math.Clamp(FlySpeed * MathF.Pow(1.2f, wheel), 0.5f, 200f);

    public void FocusOn(Vector3 point, float radius)
    {
        _focusTarget = point;
        _focusTargetDistance = MathF.Max(radius * 2.5f, 1.5f);
    }

    // Eases toward the focus target: covers the same fraction of the
    // remaining distance each second — a quick but soft swoop.
    public void Update(float dt)
    {
        if (_focusTarget is not { } target) return;
        float t = 1f - MathF.Exp(-12f * dt);
        FocusDistance += (_focusTargetDistance - FocusDistance) * t;
        var desired = target - Forward * FocusDistance;
        Position = Vector3.Lerp(Position, desired, t);
        if (Vector3.Distance(Position, desired) < 0.001f) _focusTarget = null;
    }
}
