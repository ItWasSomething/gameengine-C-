using System.Collections;
using System.Numerics;

namespace Engine.Core;

public enum Space { World, Self }

// Position, rotation and scale — and the parent/child hierarchy, which in
// Unity belongs to the Transform rather than the GameObject. A child's
// local values are relative to its parent, so moving a parent carries its
// children along.
//
// Axis conventions match Unity: +X right, +Y up, +Z forward, meters,
// rotations in degrees applied Z, then X, then Y.
[DisallowMultipleComponent]
public sealed class Transform : Component, IEnumerable<Transform>
{
    private Vector3 _localPosition;
    private Quaternion _localRotation = Quaternion.Identity;
    private Vector3 _localScale = Vector3.One;

    // The rotation is stored as a quaternion (no gimbal lock, cheap to
    // combine), but the Inspector edits Euler degrees. Turning a quaternion
    // back into Euler angles isn't unique (0,180,0 and 180,0,180 are the
    // same rotation), so the last angles *typed in* are remembered and
    // shown as-is, just like Unity.
    private Vector3 _eulerHint;

    private Transform? _parent;
    private readonly List<Transform> _children = new();

    // ---- Local ---------------------------------------------------------------

    public Vector3 localPosition
    {
        get => _localPosition;
        set => _localPosition = value;
    }

    public Quaternion localRotation
    {
        get => _localRotation;
        set
        {
            _localRotation = Quaternion.Normalize(value);
            _eulerHint = ToEulerDegrees(_localRotation);
        }
    }

    public Vector3 localEulerAngles
    {
        get => _eulerHint;
        set
        {
            _eulerHint = value;
            _localRotation = FromEulerDegrees(value);
        }
    }

    public Vector3 localScale
    {
        get => _localScale;
        set => _localScale = value;
    }

    // ---- Hierarchy -----------------------------------------------------------

    public Transform? parent
    {
        get => _parent;
        set => SetParent(value, true);
    }

    public Transform root
    {
        get
        {
            var t = this;
            while (t._parent != null) t = t._parent;
            return t;
        }
    }

    public int childCount => _children.Count;
    public Transform GetChild(int index) => _children[index];
    public IEnumerator<Transform> GetEnumerator() => _children.ToList().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Finds a descendant by path, e.g. "Arm/Hand".
    public Transform? Find(string path)
    {
        Transform? current = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?._children.FirstOrDefault(c => c.gameObject.name == part);
            if (current == null) return null;
        }
        return current;
    }

    public bool IsChildOf(Transform other)
    {
        for (var t = this; t != null; t = t._parent)
            if (t == other) return true;
        return false;
    }

    public int GetSiblingIndex() =>
        _parent?._children.IndexOf(this) ?? gameObject.ownerScene?.RootIndexOf(gameObject) ?? 0;

    public void SetSiblingIndex(int index)
    {
        if (_parent != null)
        {
            _parent._children.Remove(this);
            _parent._children.Insert(Math.Clamp(index, 0, _parent._children.Count), this);
        }
        else gameObject.ownerScene?.MoveRoot(gameObject, index);
    }

    public void SetAsFirstSibling() => SetSiblingIndex(0);
    public void SetAsLastSibling() => SetSiblingIndex(int.MaxValue);

    // worldPositionStays = true keeps the object where it is in the world
    // (its local values are recomputed relative to the new parent); false
    // keeps the local values, so it jumps to the same offset from the new
    // parent.
    public void SetParent(Transform? newParent, bool worldPositionStays = true) =>
        SetParent(newParent, worldPositionStays, detachFromScene: false);

    internal void SetParent(Transform? newParent, bool worldPositionStays, bool detachFromScene)
    {
        if (newParent == _parent && !detachFromScene) return;
        if (newParent != null && newParent.IsChildOf(this))
            throw new InvalidOperationException("Can't parent a Transform to one of its own children.");

        var go = gameObject;
        var oldScene = go.scene;
        bool wasActive = go.activeInHierarchy;
        var world = localToWorldMatrix;

        // Leave the old place...
        if (_parent != null) _parent._children.Remove(this);
        else oldScene?.RemoveRootInternal(go);
        _parent = null;

        // ...and join the new one.
        if (newParent != null)
        {
            _parent = newParent;
            newParent._children.Add(this);
        }
        else if (!detachFromScene)
        {
            oldScene?.AddRootInternal(go);
        }

        if (worldPositionStays) SetWorldMatrix(world);

        var newScene = go.scene;
        bool isActive = go.activeInHierarchy;
        if (newScene != null && newScene == oldScene && wasActive != isActive)
            newScene.OnActiveInHierarchyChanged(go, isActive);
        else if (newScene != null && newScene != oldScene)
            newScene.OnGameObjectEnteredScene(go);
    }

    // ---- World space ------------------------------------------------------------

    // Scale, then rotate, then move. System.Numerics uses row vectors
    // (v * M), so the first transform applied is the leftmost one.
    public Matrix4x4 localMatrix =>
        Matrix4x4.CreateScale(_localScale) * Matrix4x4.CreateFromQuaternion(_localRotation) *
        Matrix4x4.CreateTranslation(_localPosition);

    public Matrix4x4 localToWorldMatrix => _parent == null ? localMatrix : localMatrix * _parent.localToWorldMatrix;

    public Matrix4x4 worldToLocalMatrix
    {
        get
        {
            Matrix4x4.Invert(localToWorldMatrix, out var inverse);
            return inverse;
        }
    }

    public Vector3 position
    {
        get => _parent == null ? _localPosition : Vector3.Transform(_localPosition, _parent.localToWorldMatrix);
        set => _localPosition = _parent == null ? value : Vector3.Transform(value, _parent.worldToLocalMatrix);
    }

    public Quaternion rotation
    {
        get => _parent == null ? _localRotation : Quaternion.Concatenate(_localRotation, _parent.rotation);
        set => localRotation = _parent == null ? value : Quaternion.Concatenate(value, Quaternion.Inverse(_parent.rotation));
    }

    public Vector3 eulerAngles
    {
        get => _parent == null ? _eulerHint : ToEulerDegrees(rotation);
        set => rotation = FromEulerDegrees(value);
    }

    // World scale, approximately (exact only without rotated, non-uniformly
    // scaled parents — same caveat as Unity's lossyScale).
    public Vector3 lossyScale => _parent == null ? _localScale : _localScale * _parent.lossyScale;

    public Vector3 forward
    {
        get => Vector3.Transform(Vector3.UnitZ, rotation);
        set => rotation = LookRotation(value, Vector3.UnitY);
    }

    public Vector3 right
    {
        get => Vector3.Transform(Vector3.UnitX, rotation);
        set => rotation = Quaternion.Concatenate(rotation, FromToRotation(right, value));
    }

    public Vector3 up
    {
        get => Vector3.Transform(Vector3.UnitY, rotation);
        set => rotation = Quaternion.Concatenate(rotation, FromToRotation(up, value));
    }

    internal void SetWorldMatrix(Matrix4x4 world)
    {
        var local = world;
        if (_parent != null) local = world * _parent.worldToLocalMatrix;
        if (Matrix4x4.Decompose(local, out var scale, out var rot, out var pos))
        {
            _localPosition = pos;
            localRotation = rot;
            _localScale = scale;
        }
    }

    // ---- Moving things ---------------------------------------------------------------

    // Unity's default is Space.Self: Translate(Vector3.forward) moves along
    // the object's *own* forward direction.
    public void Translate(Vector3 translation, Space relativeTo = Space.Self) =>
        position += relativeTo == Space.Self ? Vector3.Transform(translation, rotation) : translation;

    public void Rotate(Vector3 eulers, Space relativeTo = Space.Self)
    {
        var delta = FromEulerDegrees(eulers);
        if (relativeTo == Space.Self) localRotation = Quaternion.Concatenate(delta, _localRotation);
        else rotation = Quaternion.Concatenate(rotation, delta);
    }

    public void Rotate(float x, float y, float z, Space relativeTo = Space.Self) => Rotate(new Vector3(x, y, z), relativeTo);

    public void Rotate(Vector3 axis, float angleDegrees, Space relativeTo = Space.Self)
    {
        var delta = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angleDegrees * Mathf.Deg2Rad);
        if (relativeTo == Space.Self) localRotation = Quaternion.Concatenate(delta, _localRotation);
        else rotation = Quaternion.Concatenate(rotation, delta);
    }

    // Rotates around a world-space point (orbiting).
    public void RotateAround(Vector3 point, Vector3 axis, float angleDegrees)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angleDegrees * Mathf.Deg2Rad);
        position = point + Vector3.Transform(position - point, q);
        rotation = Quaternion.Concatenate(rotation, q);
    }

    public void LookAt(Transform target) => LookAt(target.position, Vector3.UnitY);

    public void LookAt(Vector3 worldPosition) => LookAt(worldPosition, Vector3.UnitY);

    public void LookAt(Vector3 worldPosition, Vector3 worldUp)
    {
        var dir = worldPosition - position;
        if (dir.LengthSquared() > 1e-10f) rotation = LookRotation(dir, worldUp);
    }

    public Vector3 TransformPoint(Vector3 p) => Vector3.Transform(p, localToWorldMatrix);
    public Vector3 InverseTransformPoint(Vector3 p) => Vector3.Transform(p, worldToLocalMatrix);
    public Vector3 TransformDirection(Vector3 d) => Vector3.Transform(d, rotation);
    public Vector3 InverseTransformDirection(Vector3 d) => Vector3.Transform(d, Quaternion.Inverse(rotation));
    public Vector3 TransformVector(Vector3 v) => Vector3.TransformNormal(v, localToWorldMatrix);

    // ---- Rotation helpers (Unity's Quaternion statics) ---------------------------

    // Applied Z (roll), then X (pitch), then Y (yaw) — Unity's order, and
    // exactly what CreateFromYawPitchRoll does.
    public static Quaternion FromEulerDegrees(Vector3 e) =>
        Quaternion.CreateFromYawPitchRoll(e.Y * Mathf.Deg2Rad, e.X * Mathf.Deg2Rad, e.Z * Mathf.Deg2Rad);

    public static Vector3 ToEulerDegrees(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        // With row vectors the third row is the rotated +Z axis, and
        // M32 = -sin(pitch).
        float pitch = MathF.Asin(Math.Clamp(-m.M32, -1f, 1f));
        float yaw, roll;
        if (MathF.Abs(m.M32) < 0.9999f)
        {
            yaw = MathF.Atan2(m.M31, m.M33);
            roll = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            // Looking straight up/down: yaw and roll spin around the same
            // axis ("gimbal lock"), so put it all in yaw.
            yaw = MathF.Atan2(-m.M13, m.M11);
            roll = 0f;
        }
        return new Vector3(Wrap(pitch * Mathf.Rad2Deg), Wrap(yaw * Mathf.Rad2Deg), Wrap(roll * Mathf.Rad2Deg));

        static float Wrap(float d) => MathF.Abs(d) < 1e-4f ? 0f : d;
    }

    // The rotation that points +Z along `forward`, with +Y as close to `up`
    // as possible.
    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Cross(up, z);
        if (x.LengthSquared() < 1e-8f) x = Vector3.Cross(MathF.Abs(z.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, z);
        x = Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float d = Vector3.Dot(from, to);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }
}
