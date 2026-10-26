using System.Numerics;
using System.Text.Json.Nodes;
using Engine;
using Engine.Core;
using Debug = Engine.Core.Debug;

namespace Editor;

public enum PlayState { Editing, Playing, Paused }

// Everything the editor's panels share: the open scene, what's selected,
// play state and undo history. Panels never talk to each other directly —
// they all read and change this — so adding a panel never means wiring it
// into the others.
public sealed class EditorContext
{
    private readonly UndoStack _undo = new();
    private string? _playSnapshot;
    private Guid? _selectedBeforePlay;

    public string ProjectDirectory { get; }
    public Scene Scene { get; private set; } = new();
    public string? ScenePath { get; private set; }
    public bool IsDirty { get; private set; }

    public GameObject? Selected { get; private set; }
    public PlayState State { get; private set; } = PlayState.Editing;
    public bool IsPlaying => State != PlayState.Editing;

    // Raised when play starts/stops or a scene loads, so panels holding
    // object references can drop them.
    public event Action? SceneReplaced;
    public event Action? PlayStarted;

    public EditorContext(string projectDirectory)
    {
        ProjectDirectory = projectDirectory;
        AssetDatabase.projectPath = projectDirectory;
        Directory.CreateDirectory(AssetDatabase.assetsFolder);
        TagManager.Load(projectDirectory);
        SetScene(new Scene());
    }

    public string SceneDisplayName => Scene.name + (IsDirty ? "*" : "");

    private void SetScene(Scene scene)
    {
        Scene = scene;
        SceneManager.activeScene = scene;   // `new GameObject()` lands here
        Selected = null;
        SceneReplaced?.Invoke();
    }

    // ---- Selection ---------------------------------------------------------

    public void Select(GameObject? go) => Selected = go;

    // Game scripts (compiler + hot reload), and "open this .cs file at this
    // line" in the Script Editor. Set by EditorApp.
    public Scripting.ScriptSystem? Scripts { get; set; }
    public Action<string, int?>? OpenScript { get; set; }

    // Opens Edit > Project Settings > Tags and Layers.
    public bool ShowTagsAndLayers { get; set; }

    // "Ping": briefly highlight an asset in the Project panel (and reveal it),
    // like Unity does when you click a prefab reference.
    public string? PingedAsset { get; private set; }
    public DateTime PingTime { get; private set; }

    public void PingAsset(string assetPath)
    {
        PingedAsset = assetPath;
        PingTime = DateTime.Now;
    }

    // ---- Undo ----------------------------------------------------------------
    //
    // Snapshot-based: before any change, the *whole scene* is serialized and
    // pushed. Undo reloads the previous snapshot. Wasteful next to recording
    // individual commands, but it's tiny, it can't drift out of sync with
    // the scene, and scenes here are kilobytes.

    // Call *before* changing the scene, once per edit (at the start of a
    // drag, not on every frame of it).
    public void RecordUndo()
    {
        if (IsPlaying) return;   // play-mode changes are thrown away on Stop anyway
        _undo.Push(Capture());
        IsDirty = true;
    }

    public bool CanUndo => !IsPlaying && _undo.CanUndo;
    public bool CanRedo => !IsPlaying && _undo.CanRedo;

    public void Undo()
    {
        if (!CanUndo) return;
        Restore(_undo.Undo(Capture()));
        IsDirty = true;
    }

    public void Redo()
    {
        if (!CanRedo) return;
        Restore(_undo.Redo(Capture()));
        IsDirty = true;
    }

    private UndoSnapshot Capture() => new(SceneSerializer.SceneToJson(Scene), Selected?.instanceId);

    private void Restore(UndoSnapshot snapshot) => ReloadFromJson(snapshot.SceneJson, snapshot.SelectedId);

    private void ReloadFromJson(string json, Guid? selectId, bool syncPrefabs = false)
    {
        var (name, path) = (Scene.name, Scene.path);
        var scene = SceneSerializer.SceneFromJson(json, syncPrefabs);
        scene.name = name;
        scene.path = path;
        SetScene(scene);
        Selected = selectId is { } id ? scene.FindById(id) as GameObject : null;
    }

    // ---- Creating GameObjects -----------------------------------------------

    // Set by the Scene view each frame: a point in front of the editor
    // camera, so new objects appear where you're looking.
    public Vector3 SpawnPoint { get; set; }

    public GameObject CreateGameObject(string kind, Transform? parent = null)
    {
        RecordUndo();
        GameObject go;
        switch (kind)
        {
            case "Cube": case "Sphere": case "Capsule": case "Cylinder": case "Plane": case "Quad":
                go = GameObject.CreatePrimitive(Enum.Parse<PrimitiveType>(kind));
                break;
            case "Directional Light":
                go = new GameObject("Directional Light");
                var sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1f, 0.96f, 0.84f);
                go.transform.localEulerAngles = new Vector3(50, -30, 0);
                break;
            case "Point Light":
                go = new GameObject("Point Light");
                go.AddComponent<Light>().type = LightType.Point;
                break;
            case "Camera":
                go = new GameObject("Camera");
                go.AddComponent<Camera>();
                break;
            case "Sky Environment":
                go = new GameObject("Sky Environment");
                go.AddComponent<SkyEnvironment>();
                break;
            default:
                go = new GameObject("GameObject");
                break;
        }

        if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
        else if (kind != "Directional Light" && kind != "Sky Environment") go.transform.position = SpawnPoint;

        go.name = UniqueName(go.name, parent);
        Selected = go;
        return go;
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        RecordUndo();
        var parent = Selected.transform.parent;
        EngineObject.DestroyImmediate(Selected);
        Selected = parent?.gameObject;
    }

    public void DuplicateSelected()
    {
        if (Selected == null) return;
        RecordUndo();
        var source = Selected;
        var copy = SceneSerializer.CloneGameObject(source);
        copy.name = UniqueName(source.name, source.transform.parent);
        if (source.transform.parent is { } p) copy.transform.SetParent(p, worldPositionStays: false);
        else Scene.AddRootGameObject(copy);
        copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
        Selected = copy;
    }

    // Moves `go` under `newParent` (null = to the scene root), keeping its
    // world position, optionally at a given sibling index.
    public void Reparent(GameObject go, Transform? newParent, int siblingIndex = -1)
    {
        if (newParent != null && newParent.IsChildOf(go.transform)) return;
        RecordUndo();
        go.transform.SetParent(newParent, worldPositionStays: true);
        if (siblingIndex >= 0) go.transform.SetSiblingIndex(siblingIndex);
    }

    // Unity names duplicates "Cube (1)", "Cube (2)"...
    public string UniqueName(string name, Transform? parent)
    {
        IEnumerable<string> siblings = parent != null
            ? parent.Select(t => t.gameObject.name)
            : Scene.GetRootGameObjects().Select(g => g.name);
        var taken = siblings.ToHashSet();
        if (!taken.Contains(name)) return name;
        string stem = System.Text.RegularExpressions.Regex.Replace(name, @" \(\d+\)$", "");
        for (int i = 1; ; i++)
        {
            string candidate = $"{stem} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ---- Components ------------------------------------------------------------

    public void AddComponent(GameObject go, Type type)
    {
        RecordUndo();
        go.AddComponent(type);
    }

    public void RemoveComponent(Component component)
    {
        // Unity refuses if another component [RequireComponent]s this one.
        var dependent = component.gameObject.components.FirstOrDefault(c => c != component &&
            c.GetType().GetCustomAttributes(typeof(RequireComponentAttribute), true)
             .Cast<RequireComponentAttribute>().Any(r => r.type.IsInstanceOfType(component)));
        if (dependent != null)
        {
            Debug.LogWarning($"Can't remove {component.GetType().Name}: {dependent.GetType().Name} depends on it.");
            return;
        }
        RecordUndo();
        EngineObject.DestroyImmediate(component);
    }

    // Puts every field back to the value a fresh component would have.
    public void ResetComponent(Component component)
    {
        RecordUndo();
        if (component is Transform t)
        {
            t.localPosition = Vector3.Zero;
            t.localEulerAngles = Vector3.Zero;
            t.localScale = Vector3.One;
            return;
        }
        var fresh = Activator.CreateInstance(component.GetType())!;
        foreach (var field in TypeRegistry.GetSerializedFields(component.GetType()))
            field.SetValue(component, field.GetValue(fresh));
        component.CallOnValidate();
    }

    // ---- Prefabs ---------------------------------------------------------------------

    // Drag a GameObject into the Project panel → it becomes a .prefab asset
    // and the original becomes its first instance.
    public void CreatePrefab(GameObject go, string folderAbsolute)
    {
        RecordUndo();
        string file = Path.Combine(folderAbsolute, go.name + SceneSerializer.PrefabExtension);
        for (int i = 1; File.Exists(file); i++)
            file = Path.Combine(folderAbsolute, $"{go.name} {i}{SceneSerializer.PrefabExtension}");
        string assetPath = AssetDatabase.ToAssetPath(file);
        PrefabUtility.CreatePrefab(go, assetPath);
        Log.Info($"Created prefab {assetPath}");
    }

    public GameObject? InstantiatePrefab(string assetPath, Transform? parent = null, Vector3? position = null)
    {
        var asset = AssetDatabase.LoadPrefab(assetPath);
        if (asset == null) return null;
        RecordUndo();
        var instance = SceneSerializer.CloneGameObject(asset);
        instance.prefabAssetPath = assetPath;
        instance.name = UniqueName(asset.name, parent);
        if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);
        else Scene.AddRootGameObject(instance);
        if (position is { } p) instance.transform.position = p;
        Selected = instance;
        return instance;
    }

    // Writes this instance to its prefab file, then refreshes every *other*
    // instance in the scene from it.
    public void ApplyPrefab(GameObject instanceRoot)
    {
        if (instanceRoot.prefabAssetPath is not { } path) return;
        RecordUndo();
        PrefabUtility.ApplyInstance(instanceRoot);
        RefreshInstances((id, p) => p == path && id != instanceRoot.instanceId);
        Log.Info($"Applied changes to {path}");
    }

    // Throws away this instance's changes and reloads it from the prefab.
    public void RevertPrefab(GameObject instanceRoot)
    {
        if (instanceRoot.prefabAssetPath == null) return;
        RecordUndo();
        RefreshInstances((id, _) => id == instanceRoot.instanceId);
    }

    public void UnpackPrefab(GameObject instanceRoot)
    {
        RecordUndo();
        PrefabUtility.Unpack(instanceRoot);
    }

    private void RefreshInstances(Func<Guid, string, bool> filter)
    {
        var doc = SceneSerializer.WriteScene(Scene);
        PrefabUtility.SyncInstances(doc, filter);
        ReloadFromJson(doc.ToJsonString(), Selected?.instanceId);
    }

    // ---- Files -------------------------------------------------------------------------

    // A new scene, like Unity's: a Main Camera, a Directional Light, and (our
    // addition) a Sky Environment.
    public void NewScene()
    {
        Stop();
        var scene = new Scene { name = "Untitled" };
        SetScene(scene);

        var cam = new GameObject("Main Camera") { tag = TagManager.MainCamera };
        cam.AddComponent<Camera>();
        cam.transform.position = new Vector3(0, 1, -10);

        var light = new GameObject("Directional Light");
        var l = light.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = new Color(1f, 0.96f, 0.84f);
        light.transform.position = new Vector3(0, 3, 0);
        light.transform.localEulerAngles = new Vector3(50, -30, 0);

        new GameObject("Sky Environment").AddComponent<SkyEnvironment>();

        ScenePath = null;
        IsDirty = false;
        _undo.Clear();
    }

    public void OpenScene(string path)
    {
        Stop();
        try
        {
            SetScene(SceneSerializer.LoadScene(path));
            ScenePath = path;
            IsDirty = false;
            _undo.Clear();
            Log.Info($"Opened {AssetDatabase.ToAssetPath(path)}");
        }
        catch (Exception e)
        {
            Log.Error($"Couldn't open {path}: {e.Message}");
        }
    }

    public void SaveScene(string? path = null)
    {
        if (IsPlaying) { Log.Warn("Stop the game before saving (play-mode changes aren't kept)."); return; }
        path ??= ScenePath ?? Path.Combine(AssetDatabase.assetsFolder, "Scenes", "Untitled" + SceneSerializer.SceneExtension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SceneSerializer.SaveScene(Scene, path);
            ScenePath = path;
            IsDirty = false;
            Log.Info($"Saved {AssetDatabase.ToAssetPath(path)}");
        }
        catch (Exception e)
        {
            Log.Error($"Couldn't save {path}: {e.Message}");
        }
    }

    // ---- Play mode ---------------------------------------------------------------------
    //
    // Play snapshots the scene, then runs a fresh copy of it. Stop throws
    // the played copy away and restores the snapshot — whatever the game
    // did, and anything you tweaked while it ran, disappears (Unity does the
    // same; it's why play-mode edits "don't stick").

    // Returns a reason Play isn't allowed right now (e.g. script compile
    // errors), or null. Set by the script system.
    public Func<string?>? PlayBlocker { get; set; }

    // Rebuilds the open scene from its own serialized state — used after
    // scripts recompile, so every component is recreated with the new code.
    // Not an undo step, and doesn't mark the scene dirty.
    public void ReloadSceneInPlace() => ReloadFromJson(SceneSerializer.SceneToJson(Scene), Selected?.instanceId);

    public void Play()
    {
        if (State == PlayState.Paused) { State = PlayState.Playing; Scene.isPaused = false; return; }
        if (IsPlaying) return;
        if (PlayBlocker?.Invoke() is { } reason)
        {
            Log.Error(reason);
            return;
        }

        _playSnapshot = SceneSerializer.SceneToJson(Scene);
        _selectedBeforePlay = Selected?.instanceId;
        var (name, path) = (Scene.name, Scene.path);
        var running = SceneSerializer.SceneFromJson(_playSnapshot);
        running.name = name;
        running.path = path;
        SetScene(running);
        State = PlayState.Playing;
        PlayStarted?.Invoke();
        Selected = _selectedBeforePlay is { } id ? Scene.FindById(id) as GameObject : null;
        Scene.Play();
    }

    public void Pause()
    {
        if (State != PlayState.Playing) return;
        State = PlayState.Paused;
        Scene.isPaused = true;
    }

    // Advances a paused game by exactly one fixed step.
    public void StepFrame()
    {
        if (State != PlayState.Paused) return;
        Scene.StepFixed();
    }

    public void Stop()
    {
        if (!IsPlaying || _playSnapshot == null) return;
        State = PlayState.Editing;
        ReloadFromJson(_playSnapshot, _selectedBeforePlay);
        _playSnapshot = null;
        Engine.Input.Reset();
        Time.timeScale = 1f;
    }

    // While playing, a script may call SceneManager.LoadScene.
    public void LoadSceneDuringPlay(string assetPath)
    {
        try
        {
            var scene = SceneSerializer.LoadScene(AssetDatabase.ToAbsolutePath(assetPath));
            SetScene(scene);
            scene.Play();
        }
        catch (Exception e)
        {
            Log.Error($"LoadScene('{assetPath}') failed: {e.Message}");
        }
    }
}

public readonly record struct UndoSnapshot(string SceneJson, Guid? SelectedId);

public sealed class UndoStack
{
    private const int Limit = 100;
    private readonly LinkedList<UndoSnapshot> _undo = new();
    private readonly Stack<UndoSnapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(UndoSnapshot before)
    {
        _undo.AddLast(before);
        if (_undo.Count > Limit) _undo.RemoveFirst();
        _redo.Clear();
    }

    public UndoSnapshot Undo(UndoSnapshot current)
    {
        var previous = _undo.Last!.Value;
        _undo.RemoveLast();
        _redo.Push(current);
        return previous;
    }

    public UndoSnapshot Redo(UndoSnapshot current)
    {
        _undo.AddLast(current);
        return _redo.Pop();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
