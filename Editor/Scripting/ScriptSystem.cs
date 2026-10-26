using System.Collections;
using System.Reflection;
using Engine;
using Engine.Core;
using Debug = Engine.Core.Debug;

namespace Editor.Scripting;

// Owns the game's scripts in the editor:
//
//   1. watch Assets/ for .cs changes (saved from any editor)
//   2. recompile in the background (Roslyn, ~0.2-2 s)
//   3. on the main thread, load the new assembly and *hot-swap* it in:
//        - editing: the scene is re-serialized and reloaded, so every
//          component is rebuilt from its saved values with the new code
//        - playing: each live script component is replaced by an instance
//          of its new class, with all fields (public *and* private) copied
//          across, so the game keeps running from where it was
//   4. unload the previous assembly
//
// Compile errors leave the last good version running (and block Play,
// like Unity).
public sealed class ScriptSystem : IDisposable
{
    private readonly EditorContext _ctx;
    private readonly FileSystemWatcher _watcher;
    private ScriptLoadContext? _loadContext;
    private Assembly? _assembly;
    private Task<CompileResult>? _compileTask;
    private DateTime _lastChange = DateTime.MinValue;
    private volatile bool _dirty = true;     // compile once at startup
    private (Guid GameObject, string TypeName)? _pendingAdd;

    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; private set; } = Array.Empty<ScriptDiagnostic>();
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
    public bool IsCompiling => _compileTask != null;
    public IReadOnlyDictionary<string, string> TypeFiles { get; private set; } = new Dictionary<string, string>();
    public int Generation { get; private set; }      // bumps on every successful reload

    // Raised after a successful reload (panels refresh lists, etc.).
    public event Action? Reloaded;

    public string ScriptsFolder => Path.Combine(AssetDatabase.assetsFolder, "Scripts");
    private string LibraryDll => Path.Combine(_ctx.ProjectDirectory, "Library", "ScriptAssemblies", ScriptCompiler.AssemblyName + ".dll");

    public ScriptSystem(EditorContext ctx)
    {
        _ctx = ctx;
        Directory.CreateDirectory(AssetDatabase.assetsFolder);
        _watcher = new FileSystemWatcher(AssetDatabase.assetsFolder, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        // These fire on a background thread; just note that something
        // changed. The main thread decides when to compile.
        FileSystemEventHandler changed = (_, _) => MarkDirty();
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (_, _) => MarkDirty();
        _watcher.EnableRaisingEvents = true;

        WriteIdeProject();
    }

    public void MarkDirty()
    {
        _dirty = true;
        _lastChange = DateTime.Now;
    }

    // Compiles right now and waits (used once at startup, before the first
    // scene loads).
    public void CompileNow()
    {
        _dirty = false;
        try { Apply(ScriptCompiler.Compile(AssetDatabase.assetsFolder)); }
        catch (Exception e) { Log.Error($"Script compile failed: {e}"); }
    }

    // Called once per frame on the main thread.
    public void Update()
    {
        // Debounce: editors often write a file in several steps; wait until
        // things have been quiet for a moment.
        if (_dirty && _compileTask == null && (DateTime.Now - _lastChange).TotalMilliseconds > 250)
        {
            _dirty = false;
            string assets = AssetDatabase.assetsFolder;
            _compileTask = Task.Run(() => ScriptCompiler.Compile(assets));
        }

        if (_compileTask is { IsCompleted: true } task)
        {
            _compileTask = null;
            try { Apply(task.Result); }
            catch (Exception e) { Log.Error($"Script reload failed: {e}"); }
        }
    }

    private void Apply(CompileResult result)
    {
        Diagnostics = result.Diagnostics;
        foreach (var d in result.Diagnostics.Where(d => !d.IsError).Take(20)) Log.Warn(d.Format(_ctx.ProjectDirectory));
        if (!result.Success)
        {
            foreach (var d in result.Diagnostics.Where(d => d.IsError)) Log.Error(d.Format(_ctx.ProjectDirectory));
            Log.Error("Scripts have compile errors — still running the last version that compiled.");
            return;
        }
        TypeFiles = result.TypeFiles;

        var oldAssembly = _assembly;
        var oldContext = _loadContext;

        _loadContext = new ScriptLoadContext();
        _assembly = _loadContext.LoadFromStream(new MemoryStream(result.Assembly!), new MemoryStream(result.Symbols!));

        if (oldAssembly != null) TypeRegistry.Unregister(t => t.Assembly == oldAssembly);
        TypeRegistry.RegisterAssembly(_assembly);
        AssetDatabase.ClearCache();

        if (_ctx.IsPlaying) SwapLiveComponents(oldAssembly);
        else _ctx.ReloadSceneInPlace();   // rebuild from saved values with the new types

        if (oldAssembly != null) MonoBehaviour.ClearMessageCache(oldAssembly);
        oldContext?.Unload();

        SaveLibraryCopy(result);
        Generation++;
        int scripts = _assembly.GetTypes().Count(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract);
        Log.Info($"Scripts compiled in {result.Duration.TotalSeconds:0.00}s ({scripts} component{(scripts == 1 ? "" : "s")})" +
                 (_ctx.IsPlaying ? " — hot-swapped into the running game." : "."));

        if (_pendingAdd is { } pending)
        {
            _pendingAdd = null;
            if (_ctx.Scene.FindById(pending.GameObject) is GameObject go && TypeRegistry.FindComponentType(pending.TypeName) is { } type)
                _ctx.AddComponent(go, type);
        }
        Reloaded?.Invoke();
    }

    // The runtime player (Game -- scene ...) loads this file, so scripts
    // work outside the editor too. Unity keeps it in Library/ScriptAssemblies.
    private void SaveLibraryCopy(CompileResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryDll)!);
            File.WriteAllBytes(LibraryDll, result.Assembly!);
            File.WriteAllBytes(Path.ChangeExtension(LibraryDll, ".pdb"), result.Symbols!);
        }
        catch (IOException) { /* the runtime player has it open; the next compile will try again */ }
    }

    // ---- Live hot-swap ---------------------------------------------------------

    private void SwapLiveComponents(Assembly? oldAssembly)
    {
        var scene = _ctx.Scene;
        var replaced = new Dictionary<Component, Component>();
        var deferred = new List<Action>();
        var resurrected = new List<Component>();

        foreach (var go in scene.AllGameObjects())
        {
            foreach (var old in go.components.ToList())
            {
                if (oldAssembly != null && old.GetType().Assembly == oldAssembly)
                {
                    var newType = TypeRegistry.FindComponentType(old.GetType().Name);
                    if (newType == null)
                    {
                        // The class was deleted or renamed: keep its data as a Missing Script.
                        var json = SceneSerializer.WriteComponent(old);
                        var missing = new MissingComponent { scriptName = old.GetType().Name, originalJson = json, instanceId = old.instanceId };
                        go.ReplaceComponent(old, missing);
                        continue;
                    }
                    var fresh = (Component)Activator.CreateInstance(newType)!;
                    fresh.instanceId = old.instanceId;
                    CopyState(old, fresh, deferred);
                    go.ReplaceComponent(old, fresh);
                    replaced[old] = fresh;
                }
                else if (old is MissingComponent missing && TypeRegistry.FindComponentType(missing.scriptName) is { } type &&
                         missing.originalJson != null)
                {
                    // A script that was missing (or broken) exists again.
                    var fresh = (Component)Activator.CreateInstance(type)!;
                    fresh.instanceId = old.instanceId;
                    var ctx = new ReadContext { External = id => scene.FindById(id) };
                    if (missing.originalJson["fields"] is System.Text.Json.Nodes.JsonObject fields) SceneSerializer.ReadFields(fresh, fields, ctx);
                    if (fresh is Behaviour b) b.SetEnabledSilently(missing.originalJson["enabled"]?.GetValue<bool>() ?? true);
                    go.ReplaceComponent(old, fresh);
                    deferred.Add(ctx.Resolve);
                    resurrected.Add(fresh);
                }
            }
        }

        // Values that couldn't be copied directly (they involve old script
        // types) are converted now that every new component exists.
        foreach (var action in deferred) action();

        // Anything that pointed at an old component instance (another
        // script's `target` field, a list of enemies...) now points at its
        // replacement.
        foreach (var go in scene.AllGameObjects())
            foreach (var c in go.components)
                RemapReferences(c, replaced);

        // Brought-back components start their lifecycle like newly added ones.
        foreach (var c in resurrected)
        {
            if (!c.gameObject.activeInHierarchy) continue;
            c.CallAwake();
            if (c is not Behaviour b || b.enabled) c.CallOnEnable();
        }

        if (replaced.Count > 0) Log.Info($"Hot-swapped {replaced.Count} live component{(replaced.Count == 1 ? "" : "s")} (coroutines don't survive a swap).");
    }

    // Copies every field from the old instance to the new one by name —
    // private ones too, so timers, counters and cached state survive. Fields
    // whose type came from the old assembly (your own enums, classes,
    // references to other scripts) are converted afterwards through the
    // serializer, which matches types by name.
    private static void CopyState(Component old, Component fresh, List<Action> deferred)
    {
        fresh.awoken = old.awoken;
        if (old is MonoBehaviour oldMb && fresh is MonoBehaviour newMb) newMb.started = oldMb.started;
        if (old is Behaviour ob && fresh is Behaviour nb) nb.SetEnabledSilently(ob.enabled);

        var oldFields = ScriptFields(old.GetType()).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var field in ScriptFields(fresh.GetType()))
        {
            if (!oldFields.TryGetValue(field.Name, out var oldField)) continue;   // a new field: keeps its default
            object? value = oldField.GetValue(old);

            if (value == null) { field.SetValue(fresh, null); continue; }
            if (field.FieldType.IsInstanceOfType(value) && !InvolvesScripts(value.GetType()))
            {
                field.SetValue(fresh, value);
                continue;
            }
            if (field.FieldType.IsEnum && value is Enum e && Enum.TryParse(field.FieldType, e.ToString(), out var parsed))
            {
                field.SetValue(fresh, parsed);
                continue;
            }
            if (value is Component c && typeof(Component).IsAssignableFrom(field.FieldType))
            {
                // Another script component: the remap pass will swap it in.
                var id = c.instanceId;
                deferred.Add(() =>
                {
                    var target = fresh.gameObject?.scene?.FindById(id);
                    if (field.FieldType.IsInstanceOfType(target)) field.SetValue(fresh, target);
                });
                continue;
            }
            if (TypeRegistry.IsSerializableType(oldField.FieldType) && TypeRegistry.IsSerializableType(field.FieldType))
            {
                // Round-trip through JSON: old type → data → new type.
                var node = SceneSerializer.WriteValue(value, oldField.FieldType);
                deferred.Add(() =>
                {
                    var ctx = new ReadContext { External = id => fresh.gameObject?.scene?.FindById(id) };
                    object? converted = null;
                    try { converted = SceneSerializer.ReadValue(node, field.FieldType, null, ctx, v => field.SetValue(fresh, v)); }
                    catch { /* incompatible change: the field keeps its default */ }
                    if (converted != null) field.SetValue(fresh, converted);
                    ctx.Resolve();
                });
            }
        }
    }

    // All instance fields declared by script classes (not by the engine's
    // MonoBehaviour/Behaviour/Component base classes).
    private static IEnumerable<FieldInfo> ScriptFields(Type type)
    {
        for (var t = type; t != null && t.Assembly != typeof(Component).Assembly; t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    private static bool InvolvesScripts(Type type) =>
        type.Assembly.IsCollectible || (type.IsGenericType && type.GetGenericArguments().Any(InvolvesScripts)) ||
        (type.IsArray && InvolvesScripts(type.GetElementType()!));

    private static void RemapReferences(Component component, Dictionary<Component, Component> replaced)
    {
        if (replaced.Count == 0) return;
        for (var t = component.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsValueType) continue;
                var value = f.GetValue(component);
                if (value is Component c && replaced.TryGetValue(c, out var swapped) && f.FieldType.IsInstanceOfType(swapped))
                    f.SetValue(component, swapped);
                else if (value is IList items)
                {
                    for (int i = 0; i < items.Count; i++)
                        if (items[i] is Component ic && replaced.TryGetValue(ic, out var s))
                            try { items[i] = s; } catch { /* element type changed; leave it */ }
                }
            }
        }
    }

    // ---- New scripts -----------------------------------------------------------------

    // Creates Assets/Scripts/<Name>.cs from a template; once it compiles, the
    // component is added to `addTo` (Unity's Add Component > New Script).
    public string? CreateScript(string className, GameObject? addTo, string? folder = null)
    {
        className = new string(className.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (className.Length == 0 || char.IsDigit(className[0]))
        {
            Log.Error("Script names must start with a letter and contain only letters, digits and _.");
            return null;
        }
        if (TypeRegistry.FindComponentType(className) != null && TypeRegistry.FindComponentType(className)!.Assembly == typeof(Component).Assembly)
        {
            Log.Error($"'{className}' is already a built-in component name — pick another.");
            return null;
        }

        folder ??= ScriptsFolder;
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, className + ".cs");
        if (File.Exists(file))
        {
            Log.Warn($"{AssetDatabase.ToAssetPath(file)} already exists.");
        }
        else
        {
            File.WriteAllText(file, Template(className));
            Log.Info($"Created {AssetDatabase.ToAssetPath(file)}");
        }
        if (addTo != null) _pendingAdd = (addTo.instanceId, className);
        MarkDirty();
        return file;
    }

    private static string Template(string name) => $$"""
        // A MonoBehaviour script. Public fields (and [SerializeField] private
        // ones) show up in the Inspector. Save this file and the editor
        // recompiles it and swaps the new code in — even while playing.
        public class {{name}} : MonoBehaviour
        {
            public float speed = 2f;

            // Called once, before the first Update.
            void Start()
            {
            }

            // Called every frame.
            void Update()
            {
                // Example: spin around the Y axis.
                transform.Rotate(new Vector3(0, 90 * speed * Time.deltaTime, 0));
            }
        }

        """;

    // ---- IDE support -------------------------------------------------------------------

    // A .csproj next to Assets/ so VS Code / Visual Studio understand the
    // scripts (IntelliSense, go-to-definition, error squiggles). The editor
    // never builds it — it's only for the IDE.
    private void WriteIdeProject()
    {
        try
        {
            string engineDll = typeof(Component).Assembly.Location;
            string silkInput = typeof(Silk.NET.Input.Key).Assembly.Location;
            string usings = string.Join("\n", ScriptCompiler.GlobalUsings.Select(u =>
            {
                var parts = u.Split(" = ");
                return parts.Length == 2
                    ? $"    <Using Include=\"{parts[1]}\" Alias=\"{parts[0]}\" />"
                    : $"    <Using Include=\"{u}\" />";
            }));
            string csproj = $"""
                <!-- Generated by the MyEngine editor for IDE IntelliSense only.
                     The editor compiles scripts itself; you never need to build this. -->
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>{ScriptCompiler.AssemblyName}</AssemblyName>
                    <Nullable>disable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="Library/**" />
                    <Reference Include="Engine"><HintPath>{engineDll}</HintPath></Reference>
                    <Reference Include="Silk.NET.Input.Common"><HintPath>{silkInput}</HintPath></Reference>
                  </ItemGroup>
                  <ItemGroup>
                {usings}
                  </ItemGroup>
                </Project>

                """;
            File.WriteAllText(Path.Combine(_ctx.ProjectDirectory, ScriptCompiler.AssemblyName + ".csproj"), csproj);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Couldn't write the IDE project file: {e.Message}");
        }
    }

    public void Dispose() => _watcher.Dispose();
}
