using System.Numerics;

namespace Engine.Core;

// An RGBA color as picked on screen (sRGB), 0..1 per channel. Values above
// 1 are allowed for "brighter than white" emission. Lowercase r/g/b/a and
// the static presets match Unity's Color, so Unity code ports as-is.
[Serializable]
public struct Color
{
    public float r, g, b, a;

    public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }

    public static Color white => new(1, 1, 1);
    public static Color black => new(0, 0, 0);
    public static Color gray => new(0.5f, 0.5f, 0.5f);
    public static Color red => new(1, 0, 0);
    public static Color green => new(0, 1, 0);
    public static Color blue => new(0, 0, 1);
    public static Color yellow => new(1, 0.92f, 0.016f);
    public static Color cyan => new(0, 1, 1);
    public static Color magenta => new(1, 0, 1);
    public static Color clear => new(0, 0, 0, 0);

    // Hex like 0xFF8800, handy for picking colors from design tools.
    public static Color FromHex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    public static Color Lerp(Color x, Color y, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t);
    }

    public static Color operator *(Color c, float f) => new(c.r * f, c.g * f, c.b * f, c.a * f);

    public Color WithAlpha(float alpha) => new(r, g, b, alpha);

    public Vector3 ToVector3() => new(r, g, b);
    public Vector4 ToVector4() => new(r, g, b, a);

    // Colors are picked (and stored) the way they look on screen, which is
    // sRGB. Lighting math needs *linear* values (where 2x the number = 2x
    // the light), so the renderer converts with this before upload. The
    // 2.2 power curve is the common close-enough approximation of sRGB.
    public Vector3 ToLinear() => new(MathF.Pow(r, 2.2f), MathF.Pow(g, 2.2f), MathF.Pow(b, 2.2f));

    public override string ToString() => $"RGBA({r:0.###}, {g:0.###}, {b:0.###}, {a:0.###})";
}
