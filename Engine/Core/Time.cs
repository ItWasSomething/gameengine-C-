namespace Engine.Core;

// Unity's Time: frame timing for game code. Written by the Scene as it
// runs; read anywhere. Inside FixedUpdate, deltaTime returns the fixed step
// (Unity does the same), so movement code works in either callback.
public static class Time
{
    private static float _frameDeltaTime;

    public static float deltaTime
    {
        get => inFixedTimeStep ? fixedDeltaTime : _frameDeltaTime;
        internal set => _frameDeltaTime = value;
    }
    public static float unscaledDeltaTime { get; internal set; }
    public static float fixedDeltaTime { get; set; } = 1f / 60f;
    public static float time { get; internal set; }
    public static float unscaledTime { get; internal set; }
    public static int frameCount { get; internal set; }

    // 1 = normal, 0.5 = slow motion, 0 = paused (Update still runs, with
    // deltaTime 0; FixedUpdate stops).
    public static float timeScale { get; set; } = 1f;

    // True while inside FixedUpdate, so deltaTime knows which step to give.
    internal static bool inFixedTimeStep;

    internal static void Reset()
    {
        deltaTime = unscaledDeltaTime = time = unscaledTime = 0;
        frameCount = 0;
        timeScale = 1f;
        inFixedTimeStep = false;
    }
}
