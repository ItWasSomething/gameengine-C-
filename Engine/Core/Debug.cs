using System.Numerics;

namespace Engine.Core;

// Unity's Debug: print to the Console/Output panel, and draw temporary
// debug lines that the editor viewport shows on top of the scene.
public static class Debug
{
    public static void Log(object? message) => Engine.Log.Info(message?.ToString() ?? "null");
    public static void LogWarning(object? message) => Engine.Log.Warn(message?.ToString() ?? "null");
    public static void LogError(object? message) => Engine.Log.Error(message?.ToString() ?? "null");

    public readonly record struct DebugLine(Vector3 Start, Vector3 End, Color Color, float ExpiresAt);

    private static readonly List<DebugLine> Lines = new();
    public static IReadOnlyList<DebugLine> PendingLines => Lines;

    // duration 0 = visible for this frame only.
    public static void DrawLine(Vector3 start, Vector3 end, Color? color = null, float duration = 0f) =>
        Lines.Add(new DebugLine(start, end, color ?? Color.white, Time.unscaledTime + duration));

    public static void DrawRay(Vector3 start, Vector3 direction, Color? color = null, float duration = 0f) =>
        DrawLine(start, start + direction, color, duration);

    // Called by whoever renders debug lines, after drawing them.
    public static void ExpireLines() => Lines.RemoveAll(l => l.ExpiresAt <= Time.unscaledTime);
}
