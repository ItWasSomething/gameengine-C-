using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Engine;

// Builds the "light map": a screen-sized image of how much light reaches
// every pixel. It starts filled with AmbientColor (the dim, blue-ish dusk
// light that's everywhere) and each Light is added on top as a soft glowing
// circle. The composite pass then multiplies the normally-drawn scene by
// this image — dark where there's no light, full color (or brighter) where
// there is. The scene sprites themselves never need to know about lights.
public class Lighting : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _lightShader;
    private readonly Sprite _lightSprite;

    public RenderTarget LightMap { get; }
    public List<PointLight2D> Lights { get; } = new();

    // What an unlit pixel gets multiplied by. Pure black would be a pitch
    // dark cave; this is a cool dusk so the level stays readable.
    public Vector3 AmbientColor = new(0.42f, 0.46f, 0.68f);

    private float _time;

    public Lighting(GL gl, Shader lightShader, Texture anyTexture, int width, int height)
    {
        _gl = gl;
        _lightShader = lightShader;
        // light.frag computes its falloff mathematically and never samples
        // the texture — Sprite just requires one, so any texture will do.
        _lightSprite = new Sprite(gl, lightShader, anyTexture);
        LightMap = new RenderTarget(gl, width, height);
    }

    // Short-lived burst of light, e.g. on a melee hit.
    public void Flash(Vector2D<float> position, float radius, Vector3 color, float intensity, float lifetime)
    {
        Lights.Add(new PointLight2D
        {
            Position = position, Radius = radius, Color = color,
            Intensity = intensity, Lifetime = lifetime,
        });
    }

    public void Update(float dt)
    {
        _time += dt;

        foreach (var light in Lights)
        {
            light.Age += dt;
            float intensity = light.Intensity;

            if (light.Flicker > 0f)
            {
                // Two sine waves at unrelated frequencies read as irregular
                // flame wobble; a single sine looks like an obvious pulse.
                float t = _time + light.FlickerPhase;
                float wobble = MathF.Sin(t * 9.1f) * 0.6f + MathF.Sin(t * 23.7f) * 0.4f;
                intensity *= 1f + wobble * light.Flicker;
            }

            if (light.Lifetime > 0f)
            {
                float remaining = 1f - Math.Clamp(light.Age / light.Lifetime, 0f, 1f);
                intensity *= remaining * remaining; // fast initial drop, gentle tail
            }

            light.CurrentIntensity = intensity;
        }

        Lights.RemoveAll(l => l.Lifetime > 0f && l.Age >= l.Lifetime);
    }

    public void Resize(int width, int height) => LightMap.Resize(width, height);

    public void RenderLightMap(Matrix4x4 projection)
    {
        LightMap.BindAsTarget();
        _gl.ClearColor(AmbientColor.X, AmbientColor.Y, AmbientColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // Additive blending (One, One): overlapping lights *sum*, the way
        // real light does — two lamps next to each other make a brighter
        // spot, not one lamp painted over the other.
        _gl.BlendFunc(GLEnum.One, GLEnum.One);

        foreach (var light in Lights)
        {
            var color = light.Color * light.CurrentIntensity;
            float diameter = light.Radius * 2f;
            _lightSprite.Draw(light.Position, new Vector2D<float>(diameter, diameter), projection,
                new Vector4(color, 1f));
        }

        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
    }

    public void Dispose()
    {
        _lightSprite.Dispose();
        LightMap.Dispose();
    }
}
