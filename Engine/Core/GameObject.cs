namespace Engine.Core;

// A thing in the scene. On its own a GameObject does almost nothing — it's
// a named container for Components, which supply the behavior: a
// MeshRenderer makes it visible, a Light makes it shine, your own
// MonoBehaviour scripts make it act. Every GameObject always has exactly one
// Transform (position/rotation/scale + its place in the hierarchy).
public sealed class GameObject : EngineObject
{
    private readonly List<Component> _components = new();
    private bool _activeSelf = true;

    // Set on root objects only; children find their scene through the root.
    internal Scene? ownerScene;

    public Transform transform { get; }

    public string tag { get; set; } = TagManager.Untagged;

    // 0..31; see TagManager for the names. Used for filtering (physics,
    // raycasts, cameras) — a number rather than a string so a set of layers
    // fits in one int bitmask (LayerMask).
    public int layer { get; set; }

    // Asset path of the prefab this object is an instance of (only set on an
    // instance's root), e.g. "Assets/Prefabs/Coin.prefab".
    public string? prefabAssetPath { get; set; }

    public IReadOnlyList<Component> components => _components;

    // Creates a GameObject in the active scene, like Unity's `new GameObject()`.
    public GameObject(string name = "GameObject") : this(name, addToActiveScene: true) { }

    public GameObject(string name, params Type[] components) : this(name)
    {
        foreach (var type in components) AddComponent(type);
    }

    // Loading from a file (or building a prefab asset) must *not* drop the
    // object into whatever scene happens to be active.
    internal GameObject(string name, bool addToActiveScene)
    {
        this.name = name;
        transform = new Transform();
        AttachComponent(transform);
        if (addToActiveScene) SceneManager.activeScene?.AddRootGameObject(this);
    }

    public Scene? scene => transform.root.gameObject.ownerScene;

    // ---- Active state ----------------------------------------------------------

    public bool activeSelf => _activeSelf;

    // Active only if this object *and* every parent is active.
    public bool activeInHierarchy =>
        _activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);

    public void SetActive(bool value)
    {
        if (_activeSelf == value) return;
        bool wasActive = activeInHierarchy;
        _activeSelf = value;
        if (wasActive != activeInHierarchy) scene?.OnActiveInHierarchyChanged(this, activeInHierarchy);
    }

    public bool CompareTag(string other) => tag == other;

    // ---- Components ------------------------------------------------------------

    public T AddComponent<T>() where T : Component => (T)AddComponent(typeof(T));

    public Component AddComponent(Type type)
    {
        if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
            throw new ArgumentException($"{type.Name} isn't a component type that can be added.");
        if (type == typeof(Transform))
            throw new InvalidOperationException("Every GameObject already has a Transform.");
        if (Attribute.IsDefined(type, typeof(DisallowMultipleComponentAttribute)) && GetComponent(type) is { } existing)
        {
            Debug.LogWarning($"{name} already has a {type.Name}; only one is allowed.");
            return existing;
        }

        // [RequireComponent] dependencies first, so they come earlier in the
        // component list (as in Unity).
        foreach (RequireComponentAttribute req in type.GetCustomAttributes(typeof(RequireComponentAttribute), true))
            if (GetComponent(req.type) == null) AddComponent(req.type);

        var component = (Component)Activator.CreateInstance(type)!;
        AttachComponent(component);
        scene?.OnComponentAdded(component);
        return component;
    }

    // Used by the loader: attach an already-constructed component without
    // running any lifecycle callbacks.
    internal void AttachComponent(Component component, int index = -1)
    {
        component.gameObject = this;
        if (index < 0 || index > _components.Count) _components.Add(component);
        else _components.Insert(index, component);
    }

    // Hot-reload: put a freshly compiled component exactly where the old one
    // was, with no lifecycle callbacks (it's "the same" component, new code).
    internal void ReplaceComponent(Component oldComponent, Component newComponent)
    {
        int index = _components.IndexOf(oldComponent);
        if (index < 0) return;
        newComponent.gameObject = this;
        _components[index] = newComponent;
        oldComponent.isDestroyed = true;
    }

    internal void RemoveComponentInternal(Component component)
    {
        if (component is Transform || !_components.Contains(component)) return;
        scene?.OnComponentRemoving(component);
        _components.Remove(component);
        component.isDestroyed = true;
    }

    // Reorders a component (the Inspector's Move Up/Down). Transform stays first.
    public void MoveComponent(Component component, int newIndex)
    {
        if (component is Transform) return;
        int old = _components.IndexOf(component);
        if (old < 0) return;
        _components.RemoveAt(old);
        _components.Insert(Math.Clamp(newIndex, 1, _components.Count), component);
    }

    public T? GetComponent<T>() where T : class =>
        _components.OfType<T>().FirstOrDefault();

    public Component? GetComponent(Type type) =>
        _components.FirstOrDefault(type.IsInstanceOfType);

    public bool TryGetComponent<T>(out T component) where T : class
    {
        component = GetComponent<T>()!;
        return component != null;
    }

    public T[] GetComponents<T>() where T : class => _components.OfType<T>().ToArray();

    public T? GetComponentInChildren<T>(bool includeInactive = false) where T : class
    {
        if (!includeInactive && !activeInHierarchy) return null;
        if (GetComponent<T>() is { } own) return own;
        foreach (var child in transform)
            if (child.gameObject.GetComponentInChildren<T>(includeInactive) is { } found) return found;
        return null;
    }

    public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class
    {
        var results = new List<T>();
        Collect(this);
        return results.ToArray();

        void Collect(GameObject go)
        {
            if (!includeInactive && !go.activeInHierarchy) return;
            results.AddRange(go._components.OfType<T>());
            foreach (var child in go.transform) Collect(child.gameObject);
        }
    }

    public T? GetComponentInParent<T>() where T : class
    {
        for (var t = transform; t != null; t = t.parent)
            if (t.gameObject.GetComponent<T>() is { } found) return found;
        return null;
    }

    // ---- Static finders (search the active scene) ------------------------------

    public static GameObject? Find(string name) =>
        SceneManager.activeScene?.AllGameObjects(includeInactive: false).FirstOrDefault(go => go.name == name);

    public static GameObject? FindWithTag(string tag) =>
        SceneManager.activeScene?.AllGameObjects(includeInactive: false).FirstOrDefault(go => go.tag == tag);

    public static GameObject[] FindGameObjectsWithTag(string tag) =>
        SceneManager.activeScene?.AllGameObjects(includeInactive: false).Where(go => go.tag == tag).ToArray()
        ?? Array.Empty<GameObject>();

    // A GameObject with a MeshFilter + MeshRenderer showing a built-in shape.
    public static GameObject CreatePrimitive(PrimitiveType type)
    {
        var go = new GameObject(type.ToString());
        go.AddComponent<MeshFilter>().mesh = type;
        go.AddComponent<MeshRenderer>();
        return go;
    }

    // ---- Destruction -------------------------------------------------------------

    internal void DestroyInternal()
    {
        if (isDestroyed) return;
        scene?.OnGameObjectDestroying(this);
        foreach (var child in transform.ToArray()) child.gameObject.DestroyInternal();
        transform.SetParent(null, false, detachFromScene: true);
        foreach (var c in _components) c.isDestroyed = true;
        isDestroyed = true;
    }
}
