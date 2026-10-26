namespace Engine;

// Tiny shared interpolation helpers for "snap away from normal, then ease
// back" reactions — Player's landing squash and Target's hit squash both
// want the exact same curve shape, just with different snap/duration values.
public static class Easing
{
    public static float OutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);
}
