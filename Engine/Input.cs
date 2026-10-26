using System.Numerics;
using Silk.NET.Input;

namespace Engine;

// Game-facing input, like Godot's Input singleton. Whatever hosts the game
// (the editor's viewport, or a standalone player window) feeds key and mouse
// events in; game code only ever asks questions. The indirection matters in
// the editor: while you're typing in the inspector, the game must not see
// those keys — the host simply sets Enabled = false.
public static class Input
{
    private static readonly HashSet<Key> Down = new();
    private static readonly HashSet<Key> PressedThisFrame = new();
    private static readonly HashSet<Key> ReleasedThisFrame = new();
    private static readonly HashSet<MouseButton> MouseDown = new();

    public static bool Enabled { get; set; } = true;

    // Mouse movement since last frame, in pixels.
    public static Vector2 MouseDelta { get; private set; }
    private static Vector2 _pendingMouseDelta;

    public static bool IsKeyPressed(Key key) => Enabled && Down.Contains(key);
    public static bool IsKeyJustPressed(Key key) => Enabled && PressedThisFrame.Contains(key);
    public static bool IsKeyJustReleased(Key key) => Enabled && ReleasedThisFrame.Contains(key);
    public static bool IsMouseButtonPressed(MouseButton button) => Enabled && MouseDown.Contains(button);

    // -1, 0 or +1 from a pair of keys — the usual "A/D" or "Left/Right" axis.
    public static float GetAxis(Key negative, Key positive) =>
        (IsKeyPressed(positive) ? 1f : 0f) - (IsKeyPressed(negative) ? 1f : 0f);

    // ---- Unity-style API ------------------------------------------------------
    // Same names as UnityEngine.Input, so Unity code ports over. Keys use
    // Silk.NET's Key enum (Key.W, Key.Space) in place of Unity's KeyCode.

    public static bool GetKey(Key key) => IsKeyPressed(key);
    public static bool GetKeyDown(Key key) => IsKeyJustPressed(key);
    public static bool GetKeyUp(Key key) => IsKeyJustReleased(key);

    // 0 = left, 1 = right, 2 = middle — Unity's numbering.
    public static bool GetMouseButton(int button) => IsMouseButtonPressed(ToMouseButton(button));
    public static bool GetMouseButtonDown(int button) => Enabled && MousePressedThisFrame.Contains(ToMouseButton(button));
    public static bool GetMouseButtonUp(int button) => Enabled && MouseReleasedThisFrame.Contains(ToMouseButton(button));

    // Pixels from the top-left of the game view.
    public static Vector2 mousePosition { get; private set; }

    // Unity's default axes: "Horizontal" = A/D or Left/Right, "Vertical" =
    // W/S or Up/Down, "Mouse X"/"Mouse Y" = mouse movement this frame.
    // GetAxisRaw is -1/0/1; GetAxis is the same here (no smoothing yet).
    public static float GetAxisRaw(string axisName) => axisName switch
    {
        "Horizontal" => Math.Clamp(GetAxis(Key.A, Key.D) + GetAxis(Key.Left, Key.Right), -1f, 1f),
        "Vertical" => Math.Clamp(GetAxis(Key.S, Key.W) + GetAxis(Key.Down, Key.Up), -1f, 1f),
        "Mouse X" => MouseDelta.X * 0.1f,
        "Mouse Y" => -MouseDelta.Y * 0.1f,
        "Jump" => IsKeyPressed(Key.Space) ? 1f : 0f,
        _ => 0f,
    };

    public static float GetAxis(string axisName) => GetAxisRaw(axisName);

    public static bool GetButton(string buttonName) => GetAxisRaw(buttonName) != 0f;
    public static bool GetButtonDown(string buttonName) => buttonName == "Jump" && IsKeyJustPressed(Key.Space);

    private static MouseButton ToMouseButton(int button) => button switch
    {
        1 => MouseButton.Right,
        2 => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private static readonly HashSet<MouseButton> MousePressedThisFrame = new();
    private static readonly HashSet<MouseButton> MouseReleasedThisFrame = new();
    private static Vector2? _lastMousePosition;

    // ---- Called by the host -------------------------------------------------

    // Hosts that only know the absolute cursor position (rather than raw
    // deltas) call this; the delta is derived from the previous position.
    public static void FeedMousePosition(Vector2 position)
    {
        if (_lastMousePosition is { } last) _pendingMouseDelta += position - last;
        _lastMousePosition = position;
        mousePosition = position;
    }

    // For hosts that draw the game inside part of a window (the editor's
    // viewport): set the position relative to the game view directly.
    public static void SetMousePosition(Vector2 position) => mousePosition = position;

    public static void FeedKey(Key key, bool down)
    {
        if (down) { if (Down.Add(key)) PressedThisFrame.Add(key); }
        else if (Down.Remove(key)) ReleasedThisFrame.Add(key);
    }

    public static void FeedMouseButton(MouseButton button, bool down)
    {
        if (down) { if (MouseDown.Add(button)) MousePressedThisFrame.Add(button); }
        else if (MouseDown.Remove(button)) MouseReleasedThisFrame.Add(button);
    }

    public static void FeedMouseDelta(Vector2 delta) => _pendingMouseDelta += delta;

    // Call once per frame *before* game code runs.
    public static void BeginFrame()
    {
        MouseDelta = Enabled ? _pendingMouseDelta : Vector2.Zero;
        _pendingMouseDelta = Vector2.Zero;
    }

    // Call once per frame *after* game code runs, so "just pressed" lasts
    // exactly one frame.
    public static void EndFrame()
    {
        PressedThisFrame.Clear();
        MousePressedThisFrame.Clear();
        MouseReleasedThisFrame.Clear();
        ReleasedThisFrame.Clear();
    }

    // Forget everything held — used when play stops or focus is lost, so a
    // key held during the transition doesn't stay "stuck" down.
    public static void Reset()
    {
        Down.Clear();
        MouseDown.Clear();
        MousePressedThisFrame.Clear();
        MouseReleasedThisFrame.Clear();
        _lastMousePosition = null;
        PressedThisFrame.Clear();
        ReleasedThisFrame.Clear();
        _pendingMouseDelta = Vector2.Zero;
        MouseDelta = Vector2.Zero;
    }
}
