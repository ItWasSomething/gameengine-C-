using System.Numerics;

namespace Engine.Core;

// The engine's built-in components. Like in Unity, most of them are plain
// data — the renderer looks for MeshRenderers and Lights each frame and
// draws accordingly. Field names and defaults follow Unity's.

// Same shapes and sizes as Unity's GameObject > 3D Object menu: Cube and
// Sphere 1 m, Capsule and Cylinder 2 m tall, Plane 10x10 m, Quad 1x1 m.
public enum PrimitiveType { Cube, Sphere, Capsule, Cylinder, Plane, Quad }

// Which mesh to draw. (Built-in shapes for now; imported models come later.)
[AddComponentMenu("Mesh/Mesh Filter")]
[DisallowMultipleComponent]
public sealed class MeshFilter : Component
{
    public PrimitiveType mesh = PrimitiveType.Cube;
}

// How a surface reacts to light — Unity's Standard shader settings.
// Smoothness is the opposite of roughness: 1 = mirror-sharp highlights.
[Serializable]
public sealed class Material
{
    public Color color = new(0.8f, 0.8f, 0.8f);

    [Range(0, 1)] public float metallic;
    [Range(0, 1)] public float smoothness = 0.5f;

    [Tooltip("Light the surface gives off by itself. Values above 1 glow brighter.")]
    public Color emissionColor = Color.black;

    [Tooltip("Overlay a world-space checker pattern — handy on prototype geometry.")]
    public bool checker;
    [Range(0.1f, 16f)] public float checkerScale = 1f;

    public float roughness => Math.Clamp(1f - smoothness, 0.02f, 1f);
}

// Draws the MeshFilter's mesh with a material.
[AddComponentMenu("Mesh/Mesh Renderer")]
[RequireComponent(typeof(MeshFilter))]
[DisallowMultipleComponent]
public sealed class MeshRenderer : Behaviour
{
    public Material material = new();
}

public enum LightType { Directional, Point }

[AddComponentMenu("Rendering/Light")]
public sealed class Light : Behaviour
{
    public LightType type = LightType.Point;
    public Color color = Color.white;

    [Range(0, 8)] public float intensity = 1f;

    [Tooltip("Point lights only: how far the light reaches, in meters.")]
    public float range = 10f;

    // Directional lights shine along the object's forward (+Z) axis.
    public Vector3 direction => transform.forward;
}

// The point of view the game is rendered from. While playing, the editor
// shows the enabled camera with the highest depth (Camera.main = the one
// tagged "MainCamera", as in Unity).
[AddComponentMenu("Rendering/Camera")]
public sealed class Camera : Behaviour
{
    [Range(1, 179)] public float fieldOfView = 60f;     // vertical, degrees
    public float nearClipPlane = 0.3f;
    public float farClipPlane = 1000f;

    [Tooltip("When several cameras are enabled, the highest depth is shown.")]
    public float depth;

    public static Camera? main =>
        SceneManager.activeScene?.FindComponents<Camera>()
            .FirstOrDefault(c => c.enabled && c.CompareTag(TagManager.MainCamera));

    // The camera whose view the game shows: the enabled one with the
    // highest depth (ties go to the first in the hierarchy).
    public static Camera? FindRenderCamera(Scene scene)
    {
        Camera? best = null;
        foreach (var cam in scene.FindComponents<Camera>())
            if (cam.enabled && (best == null || cam.depth > best.depth)) best = cam;
        return best;
    }

    // World → camera. Note the Z flip: Unity looks down +Z, OpenGL down -Z.
    // Flipping here (exactly what Unity's worldToCameraMatrix does) is the
    // one place the two conventions meet.
    public Matrix4x4 worldToCameraMatrix
    {
        get
        {
            var world = Matrix4x4.CreateFromQuaternion(transform.rotation) * Matrix4x4.CreateTranslation(transform.position);
            Matrix4x4.Invert(world, out var view);
            return view * Matrix4x4.CreateScale(1, 1, -1);
        }
    }

    public Matrix4x4 GetProjectionMatrix(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView * Mathf.Deg2Rad, MathF.Max(aspect, 0.01f),
            MathF.Max(nearClipPlane, 0.001f), MathF.Max(farClipPlane, nearClipPlane + 0.01f));
}

// Scene-wide look: sky gradient, ambient light, exposure, fog. (Unity keeps
// these in its Lighting window; here they're a component you put on any
// one GameObject, so they're editable in the Inspector like everything else.)
[AddComponentMenu("Rendering/Sky Environment")]
[DisallowMultipleComponent]
public sealed class SkyEnvironment : Component
{
    [Header("Sky")]
    public Color skyTop = new(0.16f, 0.3f, 0.6f);
    public Color skyHorizon = new(0.62f, 0.72f, 0.82f);
    public Color ground = new(0.18f, 0.16f, 0.15f);

    [Header("Light")]
    [Range(0, 4)] public float ambientIntensity = 0.6f;
    [Tooltip("Brightens or darkens the whole image, like a camera's exposure.")]
    [Range(0.05f, 8)] public float exposure = 1f;

    [Header("Fog")]
    [Range(0, 0.2f)] public float fogDensity = 0.004f;
}

// Example script: spins (and optionally bobs) its GameObject. Written
// exactly the way your own MonoBehaviours will be.
[AddComponentMenu("Examples/Rotator")]
public class Rotator : MonoBehaviour
{
    public Vector3 degreesPerSecond = new(0, 45, 0);

    [Range(0, 2)] public float bobHeight;
    [Range(0.05f, 4)] public float bobSpeed = 1f;

    private Vector3 _startPosition;

    private void Start() => _startPosition = transform.localPosition;

    private void Update()
    {
        transform.Rotate(degreesPerSecond * Time.deltaTime, Space.World);
        if (bobHeight > 0)
            transform.localPosition = _startPosition + Vector3.UnitY * MathF.Sin(Time.time * bobSpeed * MathF.Tau) * bobHeight;
    }
}
