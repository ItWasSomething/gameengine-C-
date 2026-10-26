using System.Numerics;

namespace Engine.Core;

// Base class of everything that lives in a scene: GameObjects and
// Components. Unity calls this UnityEngine.Object; it's named EngineObject
// here because "Object" collides with C#'s own System.Object.
//
// Instantiate/Destroy live here (as in Unity), which is why scripts can call
// them unqualified: every MonoBehaviour inherits them.
public abstract class EngineObject
{
    // Stable identity that survives save/load — how references between
    // objects ("this enemy's target is that GameObject") are stored in files.
    public Guid instanceId { get; internal set; } = Guid.NewGuid();

    public virtual string name { get; set; } = "";

    internal bool isDestroyed;

    // Unity's trick: a destroyed object counts as "false", so `if (target)`
    // is the safe way to ask "does this still exist?".
    public static implicit operator bool(EngineObject? obj) => obj is not null && !obj.isDestroyed;

    public override string ToString() => $"{name} ({GetType().Name})";

    // ---- Instantiate ---------------------------------------------------------

    // Deep-copies a GameObject (or a component's whole GameObject) into the
    // active scene. References *inside* the copied hierarchy are pointed at
    // the copies; references to things outside it are kept as-is.
    public static T Instantiate<T>(T original) where T : EngineObject =>
        Instantiate(original, null, null, null);

    public static T Instantiate<T>(T original, Transform? parent) where T : EngineObject =>
        Instantiate(original, null, null, parent);

    public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation, Transform? parent = null) where T : EngineObject =>
        Instantiate(original, (Vector3?)position, rotation, parent);

    private static T Instantiate<T>(T original, Vector3? position, Quaternion? rotation, Transform? parent) where T : EngineObject
    {
        if (!original) throw new ArgumentException("Can't Instantiate a destroyed or null object.");
        var sourceObject = original switch
        {
            GameObject go => go,
            Component c => c.gameObject,
            _ => throw new ArgumentException($"Can't Instantiate a {original.GetType().Name}."),
        };

        var clone = SceneSerializer.CloneGameObject(sourceObject);
        clone.name = sourceObject.name + "(Clone)";
        if (position is { } p) clone.transform.position = p;
        if (rotation is { } r) clone.transform.rotation = r;

        var scene = parent?.gameObject.scene ?? SceneManager.activeScene
            ?? throw new InvalidOperationException("No active scene to Instantiate into.");
        if (parent != null) clone.transform.SetParent(parent, worldPositionStays: position != null);
        else scene.AddRootGameObject(clone);

        if (original is GameObject) return (T)(EngineObject)clone;
        // A component was passed: return the matching component on the copy.
        var index = sourceObject.components.ToList().IndexOf((Component)(EngineObject)original);
        return (T)(EngineObject)clone.components[index];
    }

    // ---- Destroy -------------------------------------------------------------

    // Removes a GameObject or component at the end of the frame (or after
    // `delay` seconds). Deferred because game code usually decides to
    // destroy something in the middle of a loop over the scene.
    public static void Destroy(EngineObject? obj, float delay = 0f)
    {
        if (!obj) return;
        var scene = obj switch
        {
            GameObject go => go.scene,
            Component c => c.gameObject.scene,
            _ => null,
        };
        if (scene != null && scene.isPlaying) scene.QueueDestroy(obj!, delay);
        else DestroyImmediate(obj);
    }

    // Destroys right now. The editor uses this; game code should prefer
    // Destroy.
    public static void DestroyImmediate(EngineObject? obj)
    {
        if (!obj) return;
        switch (obj)
        {
            case GameObject go: go.DestroyInternal(); break;
            case Transform: throw new InvalidOperationException("Can't remove a Transform — destroy the GameObject instead.");
            case Component c: c.gameObject.RemoveComponentInternal(c); break;
        }
    }

    // ---- Finding -------------------------------------------------------------

    public static T? FindObjectOfType<T>() where T : Component =>
        SceneManager.activeScene?.AllGameObjects(includeInactive: false)
            .Select(go => go.GetComponent<T>()).FirstOrDefault(c => c != null);

    public static T[] FindObjectsOfType<T>() where T : Component =>
        SceneManager.activeScene?.AllGameObjects(includeInactive: false)
            .SelectMany(go => go.GetComponents<T>()).ToArray() ?? Array.Empty<T>();
}
