namespace Engine.Core;

// A loaded scene: a list of root GameObjects (each with its own hierarchy
// of children) plus, while playing, the loop that calls your scripts.
//
// In the editor a scene is just data: nothing runs. Play() starts the
// lifecycle, Unity's order:
//
//   Awake → OnEnable           when an object becomes active (once/each time)
//   Start                      before a script's first Update
//   FixedUpdate × N            fixed-rate steps to catch up with real time
//   Update                     once per frame
//   coroutines                 resume after Update
//   LateUpdate                 once per frame, after everything else
//   Destroy queue              objects destroyed this frame are removed last
public sealed class Scene
{
    private readonly List<GameObject> _roots = new();
    private readonly List<(EngineObject Target, float Time)> _destroyQueue = new();
    private float _fixedAccumulator;

    public string name { get; set; } = "Untitled";
    public string? path { get; set; }

    public bool isPlaying { get; private set; }
    public bool isPaused { get; set; }

    // Engine systems (physics) hook in here so they step right after the
    // scripts' FixedUpdate.
    public event Action<float>? FixedStepped;

    public IReadOnlyList<GameObject> GetRootGameObjects() => _roots;
    public int rootCount => _roots.Count;

    // ---- Building the hierarchy ------------------------------------------------

    public void AddRootGameObject(GameObject go)
    {
        if (go.transform.parent != null) go.transform.SetParent(null, true);
        if (go.ownerScene == this) return;
        go.ownerScene?.RemoveRootInternal(go);
        AddRootInternal(go);
        OnGameObjectEnteredScene(go);
    }

    internal void AddRootInternal(GameObject go)
    {
        if (!_roots.Contains(go)) _roots.Add(go);
        go.ownerScene = this;
    }

    internal void RemoveRootInternal(GameObject go)
    {
        _roots.Remove(go);
        if (go.ownerScene == this) go.ownerScene = null;
    }

    internal int RootIndexOf(GameObject go) => _roots.IndexOf(go);

    internal void MoveRoot(GameObject go, int index)
    {
        if (!_roots.Remove(go)) return;
        _roots.Insert(Math.Clamp(index, 0, _roots.Count), go);
    }

    // Every GameObject, parents before children, in hierarchy order.
    public IEnumerable<GameObject> AllGameObjects(bool includeInactive = true)
    {
        foreach (var root in _roots.ToList())
            foreach (var go in Walk(root))
                yield return go;

        IEnumerable<GameObject> Walk(GameObject go)
        {
            if (!includeInactive && !go.activeSelf) yield break;
            yield return go;
            foreach (var child in go.transform)
                foreach (var d in Walk(child.gameObject))
                    yield return d;
        }
    }

    public IEnumerable<T> FindComponents<T>(bool includeInactive = false) where T : class =>
        AllGameObjects(includeInactive).SelectMany(go => go.components.OfType<T>());

    public EngineObject? FindById(Guid id)
    {
        foreach (var go in AllGameObjects())
        {
            if (go.instanceId == id) return go;
            foreach (var c in go.components)
                if (c.instanceId == id) return c;
        }
        return null;
    }

    // ---- Playing -------------------------------------------------------------------

    public void Play()
    {
        if (isPlaying) return;
        isPlaying = true;
        Time.Reset();
        _fixedAccumulator = 0;
        SceneManager.activeScene = this;
        foreach (var root in _roots.ToList())
            if (root.activeSelf) Activate(root);
    }

    // Advances one rendered frame.
    public void Tick(float realDelta)
    {
        if (!isPlaying || isPaused) return;

        float delta = realDelta * Time.timeScale;
        Time.unscaledDeltaTime = realDelta;
        Time.unscaledTime += realDelta;
        Time.deltaTime = delta;

        RunStarts();

        // Don't try to catch up more than a quarter second after a hitch
        // (a breakpoint, a slow load) or physics would spiral.
        _fixedAccumulator += MathF.Min(delta, 0.25f);
        while (_fixedAccumulator >= Time.fixedDeltaTime)
        {
            StepFixed();
            _fixedAccumulator -= Time.fixedDeltaTime;
        }

        Time.time += delta;
        foreach (var mb in ActiveScripts()) if (mb.started) mb.CallUpdate();
        foreach (var mb in ActiveScripts()) mb.TickCoroutines();
        foreach (var mb in ActiveScripts()) if (mb.started) mb.CallLateUpdate();

        ProcessDestroyQueue();
        Time.frameCount++;
    }

    // One fixed step. Public so the editor can single-step a paused game.
    public void StepFixed()
    {
        Time.inFixedTimeStep = true;
        RunStarts();
        foreach (var mb in ActiveScripts()) if (mb.started) mb.CallFixedUpdate();
        FixedStepped?.Invoke(Time.fixedDeltaTime);
        Time.inFixedTimeStep = false;
    }

    private void RunStarts()
    {
        foreach (var mb in ActiveScripts())
            if (!mb.started) mb.CallStart();
    }

    // Enabled scripts on active objects, snapshotted so scripts can add or
    // destroy objects while we iterate.
    private List<MonoBehaviour> ActiveScripts()
    {
        var list = new List<MonoBehaviour>();
        foreach (var go in AllGameObjects(includeInactive: false))
            foreach (var c in go.components)
                if (c is MonoBehaviour { enabled: true, isDestroyed: false } mb) list.Add(mb);
        return list;
    }

    // ---- Lifecycle events (called by GameObject/Transform/Behaviour) ----------------

    internal void OnGameObjectEnteredScene(GameObject go)
    {
        if (isPlaying && go.activeInHierarchy) Activate(go);
    }

    internal void OnActiveInHierarchyChanged(GameObject go, bool active)
    {
        if (!isPlaying) return;
        if (active) Activate(go);
        else Deactivate(go);
    }

    internal void OnComponentAdded(Component c)
    {
        if (!isPlaying || !c.gameObject.activeInHierarchy) return;
        c.CallAwake();
        if (c is not Behaviour b || b.enabled) c.CallOnEnable();
    }

    internal void OnComponentRemoving(Component c)
    {
        if (!isPlaying || !c.awoken) return;
        if (c.gameObject.activeInHierarchy && (c is not Behaviour b || b.enabled)) c.CallOnDisable();
        c.CallOnDestroy();
    }

    internal void OnBehaviourEnabledChanged(Behaviour b, bool enabled)
    {
        if (!isPlaying || !b.gameObject.activeInHierarchy) return;
        if (!b.awoken) b.CallAwake();
        if (enabled) b.CallOnEnable(); else b.CallOnDisable();
    }

    internal void OnGameObjectDestroying(GameObject go)
    {
        if (!isPlaying) return;
        bool active = go.activeInHierarchy;
        foreach (var c in go.components)
        {
            if (!c.awoken) continue;
            if (active && (c is not Behaviour b || b.enabled)) c.CallOnDisable();
            c.CallOnDestroy();
        }
    }

    // Awake (first time only) then OnEnable for everything in the subtree
    // that is now active. Awake runs even on disabled scripts, like Unity.
    private void Activate(GameObject go)
    {
        foreach (var c in go.components.ToList())
        {
            if (!c.awoken) c.CallAwake();
            if (c is not Behaviour b || b.enabled) c.CallOnEnable();
        }
        foreach (var child in go.transform)
            if (child.gameObject.activeSelf) Activate(child.gameObject);
    }

    private void Deactivate(GameObject go)
    {
        foreach (var child in go.transform)
            if (child.gameObject.activeSelf) Deactivate(child.gameObject);
        foreach (var c in go.components)
            if (c.awoken && (c is not Behaviour b || b.enabled)) c.CallOnDisable();
    }

    // ---- Destroy queue ---------------------------------------------------------------

    internal void QueueDestroy(EngineObject target, float delay) =>
        _destroyQueue.Add((target, Time.time + delay));

    private void ProcessDestroyQueue()
    {
        for (int i = _destroyQueue.Count - 1; i >= 0; i--)
        {
            var (target, time) = _destroyQueue[i];
            if (Time.time < time) continue;
            _destroyQueue.RemoveAt(i);
            EngineObject.DestroyImmediate(target);
        }
    }
}

// Which scene is "current" — where `new GameObject()` and Instantiate put
// things, and where GameObject.Find looks.
public static class SceneManager
{
    public static Scene? activeScene { get; set; }

    public static Scene GetActiveScene() =>
        activeScene ?? throw new InvalidOperationException("No scene is loaded.");

    // Set by whoever hosts the game (editor or runtime) to swap scenes.
    public static Action<string>? LoadSceneHandler { get; set; }

    // Loads another scene by asset path, e.g. "Assets/Scenes/Level2.scene".
    // Takes effect at the end of the current frame.
    public static void LoadScene(string assetPath)
    {
        if (LoadSceneHandler == null) Debug.LogError("Scene loading isn't available here.");
        else LoadSceneHandler(assetPath);
    }
}
