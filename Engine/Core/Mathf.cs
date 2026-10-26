namespace Engine.Core;

// Unity's Mathf: float math helpers game code reaches for constantly.
public static class Mathf
{
    public const float PI = MathF.PI;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;
    public const float Epsilon = float.Epsilon;
    public const float Infinity = float.PositiveInfinity;

    public static float Sin(float f) => MathF.Sin(f);
    public static float Cos(float f) => MathF.Cos(f);
    public static float Tan(float f) => MathF.Tan(f);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static float Sqrt(float f) => MathF.Sqrt(f);
    public static float Abs(float f) => MathF.Abs(f);
    public static float Sign(float f) => f >= 0f ? 1f : -1f;
    public static float Pow(float f, float p) => MathF.Pow(f, p);
    public static float Exp(float f) => MathF.Exp(f);
    public static float Floor(float f) => MathF.Floor(f);
    public static float Ceil(float f) => MathF.Ceiling(f);
    public static float Round(float f) => MathF.Round(f);
    public static int FloorToInt(float f) => (int)MathF.Floor(f);
    public static int RoundToInt(float f) => (int)MathF.Round(f);
    public static float Min(float a, float b) => MathF.Min(a, b);
    public static float Max(float a, float b) => MathF.Max(a, b);
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    public static float InverseLerp(float a, float b, float value) => a == b ? 0f : Clamp01((value - a) / (b - a));

    // Moves `current` toward `target` by at most `maxDelta` — never overshoots.
    public static float MoveTowards(float current, float target, float maxDelta) =>
        MathF.Abs(target - current) <= maxDelta ? target : current + MathF.Sign(target - current) * maxDelta;

    public static float SmoothStep(float from, float to, float t)
    {
        t = Clamp01(t);
        t = t * t * (3f - 2f * t);
        return from + (to - from) * t;
    }

    // Wraps t into 0..length, like a modulo that also works for negatives.
    public static float Repeat(float t, float length) => Clamp(t - MathF.Floor(t / length) * length, 0f, length);

    public static float PingPong(float t, float length)
    {
        t = Repeat(t, length * 2f);
        return length - MathF.Abs(t - length);
    }

    public static bool Approximately(float a, float b) =>
        MathF.Abs(b - a) < MathF.Max(1e-6f * MathF.Max(MathF.Abs(a), MathF.Abs(b)), 1e-6f);

    // Critically damped spring toward target (Unity's SmoothDamp). Great
    // for cameras: fast at first, eases in, never overshoots.
    public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float deltaTime)
    {
        smoothTime = MathF.Max(0.0001f, smoothTime);
        float omega = 2f / smoothTime;
        float x = omega * deltaTime;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float temp = (currentVelocity + omega * change) * deltaTime;
        currentVelocity = (currentVelocity - omega * temp) * exp;
        float output = target + (change + temp) * exp;
        if ((target - current > 0f) == (output > target))
        {
            output = target;
            currentVelocity = 0f;
        }
        return output;
    }
}
