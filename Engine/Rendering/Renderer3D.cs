using System.Numerics;
using Engine.Core;
using Silk.NET.OpenGL;
using PrimitiveType = Engine.Core.PrimitiveType;

namespace Engine.Rendering;

// Where the scene is viewed from. Usually built from a Camera component, but
// the editor builds one from its own fly camera.
public readonly record struct CameraView(Matrix4x4 View, Matrix4x4 Projection, Vector3 Position)
{
    public static CameraView From(Camera camera, float aspect) =>
        new(camera.worldToCameraMatrix, camera.GetProjectionMatrix(aspect), camera.transform.position);
}

public struct RenderStats
{
    public int DrawCalls;
    public int Triangles;
    public int Lights;
}

// Draws a scene in 3D. A frame is three passes:
//
//   1. sky      — fullscreen gradient into the HDR buffer
//   2. geometry — every enabled MeshRenderer, PBR-lit by every Light
//   3. tonemap  — HDR → displayable 0..1 sRGB, into the output framebuffer
//
// The renderer only *reads* the scene: it finds components each frame and
// draws what they describe — which is why the same scene renders identically
// in the editor and in the game.
public sealed class Renderer3D : IDisposable
{
    private const int MaxDirLights = 4;
    private const int MaxPointLights = 16;

    private readonly GL _gl;
    private readonly Shader _pbr;
    private readonly Shader _sky;
    private readonly Shader _tonemap;
    private readonly FullscreenQuad _quad;
    private readonly Framebuffer _hdr;
    private readonly Dictionary<PrimitiveType, Mesh3D> _meshes = new();

    // Used when a scene has no SkyEnvironment component.
    private readonly SkyEnvironment _defaultEnvironment = new();

    public RenderStats Stats;

    public Renderer3D(GL gl)
    {
        _gl = gl;
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        string P(string file) => Path.Combine(dir, file);
        _pbr = new Shader(gl, P("pbr.vert"), P("pbr.frag"));
        _sky = new Shader(gl, P("fullscreen.vert"), P("sky.frag"));
        _tonemap = new Shader(gl, P("fullscreen.vert"), P("tonemap.frag"));
        _quad = new FullscreenQuad(gl);
        _hdr = new Framebuffer(gl, 16, 16, hdr: true);

        foreach (var type in Enum.GetValues<PrimitiveType>())
            _meshes[type] = new Mesh3D(gl, Primitives.Create(type));
    }

    public Mesh3D GetMesh(PrimitiveType type) => _meshes[type];

    // Renders `scene` into `output`. Leaves `output` bound with a valid depth
    // buffer, so the caller can draw overlays (debug lines, gizmos) on top
    // that still hide behind geometry.
    public void Render(Scene scene, CameraView view, Framebuffer output)
    {
        Stats = default;
        _hdr.EnsureSize(output.Width, output.Height);
        _hdr.Bind();

        var dirLights = new List<Light>();
        var pointLights = new List<Light>();
        var renderers = new List<(MeshRenderer Renderer, MeshFilter Filter)>();
        SkyEnvironment? env = null;

        foreach (var go in scene.AllGameObjects(includeInactive: false))
        {
            foreach (var c in go.components)
            {
                switch (c)
                {
                    case Light { enabled: true, type: LightType.Directional } d when dirLights.Count < MaxDirLights:
                        dirLights.Add(d);
                        break;
                    case Light { enabled: true, type: LightType.Point } p when pointLights.Count < MaxPointLights:
                        pointLights.Add(p);
                        break;
                    case MeshRenderer { enabled: true } r when go.GetComponent<MeshFilter>() is { } f:
                        renderers.Add((r, f));
                        break;
                    case SkyEnvironment s when env == null:
                        env = s;
                        break;
                }
            }
        }
        env ??= _defaultEnvironment;
        Stats.Lights = dirLights.Count + pointLights.Count;

        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        DrawSky(view, env, dirLights.FirstOrDefault());
        DrawMeshes(view, env, renderers, dirLights, pointLights);

        // Tone map into the output, then hand it our depth for overlays.
        output.Bind();
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _tonemap.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _hdr.ColorTexture);
        _tonemap.SetUniform("uScene", 0);
        _tonemap.SetUniform("uExposure", env.exposure);
        _quad.Draw();
        _hdr.CopyDepthTo(output);
    }

    // Unity's light intensity 1 means "this much light, without the 1/π
    // of a physically based diffuse term". Multiplying by π here makes the
    // same intensity numbers look about as bright as they do in Unity.
    private static Vector3 Radiance(Light light) => light.color.ToLinear() * light.intensity * MathF.PI;

    private void DrawSky(CameraView view, SkyEnvironment env, Light? sun)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _sky.Use();
        Matrix4x4.Invert(view.View * view.Projection, out var inverse);
        _sky.SetUniform("uInverseViewProjection", inverse);
        _sky.SetUniform("uCameraPosition", view.Position);
        _sky.SetUniform("uSkyTop", env.skyTop.ToLinear());
        _sky.SetUniform("uSkyHorizon", env.skyHorizon.ToLinear());
        _sky.SetUniform("uGround", env.ground.ToLinear());
        _sky.SetUniform("uSunDirection", sun?.direction ?? -Vector3.UnitY);
        _sky.SetUniform("uSunColor", sun == null ? Vector3.Zero : Radiance(sun) * 0.3f);
        _quad.Draw();
        Stats.DrawCalls++;
    }

    private void DrawMeshes(CameraView view, SkyEnvironment env, List<(MeshRenderer Renderer, MeshFilter Filter)> renderers,
        List<Light> dirLights, List<Light> pointLights)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        // Clockwise = front, because the camera mirrors Z (see Primitives).
        _gl.FrontFace(FrontFaceDirection.CW);
        _gl.CullFace(TriangleFace.Back);

        _pbr.Use();
        _pbr.SetUniform("uView", view.View);
        _pbr.SetUniform("uProjection", view.Projection);
        _pbr.SetUniform("uCameraPosition", view.Position);

        // Per-frame (not per-object) uniforms: environment and lights.
        _pbr.SetUniform("uSkyTop", env.skyTop.ToLinear());
        _pbr.SetUniform("uSkyHorizon", env.skyHorizon.ToLinear());
        _pbr.SetUniform("uGround", env.ground.ToLinear());
        _pbr.SetUniform("uAmbientEnergy", env.ambientIntensity);
        _pbr.SetUniform("uFogDensity", env.fogDensity);

        _pbr.SetUniform("uDirLightCount", dirLights.Count);
        for (int i = 0; i < dirLights.Count; i++)
        {
            _pbr.SetUniform($"uDirLightDirection[{i}]", dirLights[i].direction);
            _pbr.SetUniform($"uDirLightColor[{i}]", Radiance(dirLights[i]));
        }
        _pbr.SetUniform("uPointLightCount", pointLights.Count);
        for (int i = 0; i < pointLights.Count; i++)
        {
            var l = pointLights[i];
            _pbr.SetUniform($"uPointLightPosition[{i}]", l.transform.position);
            _pbr.SetUniform($"uPointLightColor[{i}]", Radiance(l));
            _pbr.SetUniform($"uPointLightRange[{i}]", MathF.Max(l.range, 0.01f));
        }

        foreach (var (renderer, filter) in renderers)
        {
            var model = renderer.transform.localToWorldMatrix;
            if (Primitives.IsTwoSided(filter.mesh)) _gl.Disable(EnableCap.CullFace);
            else _gl.Enable(EnableCap.CullFace);

            _pbr.SetUniform("uModel", model);
            Matrix4x4.Invert(model, out var inverse);
            _pbr.SetUniform("uNormalMatrix", Matrix4x4.Transpose(inverse));

            var mat = renderer.material;
            _pbr.SetUniform("uAlbedo", mat.color.ToLinear());
            _pbr.SetUniform("uMetallic", mat.metallic);
            _pbr.SetUniform("uRoughness", mat.roughness);
            _pbr.SetUniform("uEmission", mat.emissionColor.ToLinear());
            _pbr.SetUniform("uChecker", mat.checker);
            _pbr.SetUniform("uCheckerScale", mat.checkerScale);

            var mesh = _meshes[filter.mesh];
            mesh.Draw();
            Stats.DrawCalls++;
            Stats.Triangles += mesh.Data.Indices.Count / 3;
        }
        _gl.FrontFace(FrontFaceDirection.Ccw);
    }

    public void Dispose()
    {
        _pbr.Dispose();
        _sky.Dispose();
        _tonemap.Dispose();
        _quad.Dispose();
        _hdr.Dispose();
        foreach (var mesh in _meshes.Values) mesh.Dispose();
    }
}
